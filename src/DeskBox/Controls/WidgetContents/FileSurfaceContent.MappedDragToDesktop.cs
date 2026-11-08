using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// GOXF fork, 「映射整理」: dragging items out of a mapped widget and dropping
/// them on the system desktop is 「放回桌面」. The items already live in the
/// desktop folder, so for Explorer that drop is a same-folder move (nothing
/// happens) and they would stay hidden and mapped; DeskBox recognises the
/// drop from the release point instead. Drops on another desktop icon, on
/// other folders, on other widgets, and every drag out of a move-mode widget
/// keep the upstream behaviour.
///
/// The gesture is remembered when it starts (<see cref="BeginMappedDragTracking"/>,
/// one call in Items_DragStarting): whether it came from a mapped widget, its
/// paths, and a background tracker that reads the release point the moment
/// the button goes up. The decision runs once per gesture, from drag
/// completion, or — when WinUI never reports the completion, or reports it
/// before the release (stack popover closing mid-drag) — from the release.
/// Every refusal is logged as <c>[MappedOrganize] DragToDesktop skip reason=…</c>.
/// </summary>
public sealed partial class FileSurfaceContent
{
    private const int MappedDragVirtualKeyLeftButton = 0x01;
    private const int MappedDragVirtualKeyRightButton = 0x02;
    private const int MappedDragVirtualKeyEscape = 0x1B;
    private static readonly TimeSpan MappedDragCompletionGrace = TimeSpan.FromMilliseconds(1500);

    private MappedDragSession? _mappedDrag;

    private sealed class MappedDragSession(string sessionId, string[] paths, MappedDragReleaseTracker tracker)
    {
        public string SessionId { get; } = sessionId;

        public string[] Paths { get; } = paths;

        public MappedDragReleaseTracker Tracker { get; } = tracker;

        public bool Decided { get; set; }

        public bool HandledInternally { get; set; }

        public bool CompletionSeen { get; set; }
    }

    /// <summary>Called from Items_DragStarting once the drag package is prepared.</summary>
    private void BeginMappedDragTracking(string? sessionId, IReadOnlyList<string> paths)
    {
        MappedDragSession? previous = _mappedDrag;
        _mappedDrag = null;
        previous?.Tracker.Stop();
        try
        {
            if (string.IsNullOrEmpty(sessionId) || paths.Count == 0 || !ViewModel.IsMappedOrganizeWidget)
            {
                return;
            }

            DispatcherQueue queue = DispatcherQueue;
            var tracker = new MappedDragReleaseTracker(sessionId);
            var session = new MappedDragSession(sessionId, paths.ToArray(), tracker);
            _mappedDrag = session;
            tracker.Finished += finished => queue.TryEnqueue(() => OnMappedDragTrackerFinished(session));
            tracker.Start();
            App.Log(
                $"[MappedOrganize] DragToDesktop tracking widget={WidgetId} session={sessionId} " +
                $"paths={paths.Count} buttons(L={Win32Helper.IsKeyDown(MappedDragVirtualKeyLeftButton)}," +
                $"R={Win32Helper.IsKeyDown(MappedDragVirtualKeyRightButton)})");
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] DragToDesktop tracking failed to start: {ex.Message}");
        }
    }

    /// <summary>
    /// Drag completion hook (CompleteDragItemsSession). Returns true when the
    /// completed drag was turned into 「放回桌面」.
    /// </summary>
    private bool TryPutBackMappedDragOnDesktop(
        string[] draggedPaths,
        bool handledInternally,
        string? dragSessionId,
        DataPackageOperation dropResult)
    {
        try
        {
            MappedDragSession? session = _mappedDrag;
            if (session is not null && !string.Equals(session.SessionId, dragSessionId, StringComparison.Ordinal))
            {
                session = null;
            }

            if (session is null && !ViewModel.IsMappedOrganizeWidget)
            {
                // Ordinary (move-mode) widget: upstream drag-out, nothing to log.
                return false;
            }

            if (session is { Decided: true })
            {
                LogMappedDragSkip("already-decided", dragSessionId, "completion", dropResult);
                return false;
            }

            if (session is not null && session.Tracker.State == MappedDragReleaseState.Pending)
            {
                // Completion before the button went up: the stack popover
                // closed mid-drag. The release decides once it happens.
                session.HandledInternally |= handledInternally;
                session.CompletionSeen = true;
                LogMappedDragSkip("completion-before-release (deferred to release)", dragSessionId, "completion", dropResult);
                return false;
            }

            return DecideMappedDragPutBack(session, draggedPaths, handledInternally, dragSessionId, "completion", dropResult);
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] DragToDesktop failed: {ex}");
            return false;
        }
    }

    private void OnMappedDragTrackerFinished(MappedDragSession session)
    {
        if (_isDisposed || session.Decided || session.Tracker.State != MappedDragReleaseState.Released)
        {
            return;
        }

        if (session.CompletionSeen)
        {
            // The completion arrived early (button still held) and deferred.
            DecideMappedDragPutBack(session, [], session.HandledInternally, session.SessionId, "release-after-completion", DataPackageOperation.None);
            return;
        }

        // Normally DropCompleted follows within milliseconds and decides. If
        // it never comes (container recycled, popover detached) the release
        // decides on its own after a short grace.
        DispatcherQueue queue = DispatcherQueue;
        _ = Task.Delay(MappedDragCompletionGrace).ContinueWith(
            _ => queue.TryEnqueue(() =>
            {
                if (_isDisposed || session.Decided)
                {
                    return;
                }

                bool handled = session.HandledInternally ||
                               (string.Equals(_activeDragSessionId, session.SessionId, StringComparison.Ordinal) &&
                                _activeDragHandledAsStackMembership);
                App.Log(
                    $"[MappedOrganize] DragToDesktop completion not reported {MappedDragCompletionGrace.TotalMilliseconds}ms " +
                    $"after release; deciding from the release session={session.SessionId}");
                DecideMappedDragPutBack(session, [], handled, session.SessionId, "release", DataPackageOperation.None);
            }),
            TaskScheduler.Default);
    }

    private bool DecideMappedDragPutBack(
        MappedDragSession? session,
        string[] completionPaths,
        bool handledInternally,
        string? dragSessionId,
        string trigger,
        DataPackageOperation dropResult)
    {
        if (session is not null)
        {
            session.Decided = true;
            session.Tracker.Stop();
            handledInternally |= session.HandledInternally;
            if (ReferenceEquals(_mappedDrag, session))
            {
                _mappedDrag = null;
            }
        }

        string[] paths = completionPaths.Length > 0 ? completionPaths : session?.Paths ?? [];
        bool isMapped = session is not null || ViewModel.IsMappedOrganizeWidget;
        if (paths.Length == 0)
        {
            LogMappedDragSkip("no-dragged-paths", dragSessionId, trigger, dropResult);
            return false;
        }

        if (MappedOrganizeService.Current is not { } service)
        {
            LogMappedDragSkip("service-unavailable", dragSessionId, trigger, dropResult);
            return false;
        }

        MappedDesktopDropPoint drop;
        Win32Helper.POINT point;
        bool mouseButtonDown = false;
        bool escapePressed = false;
        string pointSource;
        if (session?.Tracker.State == MappedDragReleaseState.Cancelled)
        {
            LogMappedDragSkip("escape-pressed (tracker)", dragSessionId, trigger, dropResult);
            return false;
        }

        if (session is not null && session.Tracker.TryGetRelease(out point, out drop))
        {
            pointSource = "release";
            if (drop.IconHit == MappedDragIconHit.Failed)
            {
                // Explorer was busy at the instant of release; it has had
                // time to settle by now.
                MappedDesktopDropPoint retry = MappedDragToDesktopTarget.Probe(point);
                if (retry.Kind != MappedDragDropTargetKind.NotDesktop && retry.IconHit != MappedDragIconHit.Failed)
                {
                    drop = retry;
                    pointSource = "release+reprobe";
                }
            }
        }
        else
        {
            if (!Win32Helper.GetCursorPos(out point))
            {
                LogMappedDragSkip("no-cursor-position", dragSessionId, trigger, dropResult);
                return false;
            }

            pointSource = $"cursor-at-{trigger} (tracker={session?.Tracker.State.ToString() ?? "none"})";
            drop = MappedDragToDesktopTarget.Probe(point);
            mouseButtonDown = Win32Helper.IsKeyDown(MappedDragVirtualKeyLeftButton) ||
                              Win32Helper.IsKeyDown(MappedDragVirtualKeyRightButton);
            escapePressed = Win32Helper.IsKeyDown(MappedDragVirtualKeyEscape);
        }

        MappedDragPutBackDecision decision = MappedDragToDesktopPolicy.Evaluate(
            isMapped,
            handledInternally,
            drop.Kind,
            drop.IconHit,
            drop.IconText,
            paths,
            mouseButtonDown,
            escapePressed);
        string where = $"at={point.X},{point.Y} point={pointSource} {drop.Describe()}";
        if (!decision.PutBack)
        {
            LogMappedDragSkip(decision.Reason, dragSessionId, trigger, dropResult, where);
            return false;
        }

        IReadOnlyList<string> putBack = MappedDragToDesktopPolicy.ComputePutBackPaths(
            paths,
            path => File.Exists(path) || Directory.Exists(path),
            MappedOrganizeService.IsDesktopEntryPath);
        if (putBack.Count == 0)
        {
            LogMappedDragSkip("no-existing-desktop-paths", dragSessionId, trigger, dropResult, where);
            return false;
        }

        int kept = service.KeepOnDesktop(putBack);
        App.Log(
            $"[MappedOrganize] DragToDesktop put back {kept}/{paths.Length} widget={WidgetId} " +
            $"session={dragSessionId} trigger={trigger} why={decision.Reason} dropResult={dropResult} {where}");
        if (kept <= 0)
        {
            LogMappedDragSkip("keep-on-desktop-changed-nothing", dragSessionId, trigger, dropResult, where);
            return false;
        }

        MappedDragToDesktopPlacement.PlaceInBackground(putBack, drop);
        ShowFeedback(new WidgetFeedbackRequest(
            ForkOrganizeModeText.Format(ForkOrganizeModeText.Key.KeptOnDesktopFormat, kept),
            WidgetFeedbackSeverity.Success,
            "mapped-keep-desktop"));
        return true;
    }

    private void LogMappedDragSkip(
        string reason,
        string? dragSessionId,
        string trigger,
        DataPackageOperation dropResult,
        string? where = null)
    {
        App.Log(
            $"[MappedOrganize] DragToDesktop skip reason={reason} widget={WidgetId} session={dragSessionId} " +
            $"trigger={trigger} dropResult={dropResult} mapped={ViewModel.IsMappedOrganizeWidget}" +
            (where is null ? string.Empty : $" {where}"));
    }
}
