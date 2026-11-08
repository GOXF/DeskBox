using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>Where a mapped-widget drag gesture stands, as seen from the physical mouse buttons.</summary>
public enum MappedDragReleaseState
{
    /// <summary>A button is (or may still be) held: the drop has not happened yet.</summary>
    Pending,

    /// <summary>The held button went up: this is the drop point.</summary>
    Released,

    /// <summary>Esc was seen during the gesture (OLE cancels the drag on Esc).</summary>
    Cancelled,

    /// <summary>No mouse button was ever seen down (touch / pen drag): nothing to track.</summary>
    Untracked,
}

/// <summary>
/// GOXF fork, 「拖到桌面 = 放回桌面」: pure state machine fed with polled
/// button / Esc samples. Lets DeskBox read the release point at the moment of
/// release instead of when WinUI reports the drag completed: by then Explorer
/// has already processed the drop (its desktop thread may be busy and the
/// list-view hit test times out, or it has moved a visible-hidden copy of the
/// dragged icon under the cursor), and a stack popover closing mid-drag
/// completes the session while the button is still held.
/// </summary>
public sealed class MappedDragReleaseTracking
{
    private readonly TimeSpan _buttonGrace;

    public MappedDragReleaseTracking(TimeSpan? buttonGrace = null)
    {
        _buttonGrace = buttonGrace ?? TimeSpan.FromMilliseconds(400);
    }

    public MappedDragReleaseState State { get; private set; } = MappedDragReleaseState.Pending;

    public bool SawButtonDown { get; private set; }

    /// <summary>Feeds one sample; returns the (possibly final) state.</summary>
    public MappedDragReleaseState Observe(TimeSpan elapsed, bool buttonDown, bool escapeDown)
    {
        if (State != MappedDragReleaseState.Pending)
        {
            return State;
        }

        if (escapeDown && (buttonDown || SawButtonDown))
        {
            State = MappedDragReleaseState.Cancelled;
            return State;
        }

        if (buttonDown)
        {
            SawButtonDown = true;
            return State;
        }

        if (SawButtonDown)
        {
            State = MappedDragReleaseState.Released;
        }
        else if (elapsed >= _buttonGrace)
        {
            State = MappedDragReleaseState.Untracked;
        }

        return State;
    }
}

/// <summary>
/// Background poller behind <see cref="MappedDragReleaseTracking"/>: samples
/// the physical buttons every few milliseconds while a mapped-widget drag
/// runs and probes the desktop at the release point right away (Win32 only,
/// no UI-thread work). <see cref="Finished"/> is raised on the poller thread.
/// </summary>
internal sealed class MappedDragReleaseTracker
{
    private const int VirtualKeyLeftButton = 0x01;
    private const int VirtualKeyRightButton = 0x02;
    private const int VirtualKeyEscape = 0x1B;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(10);

    private readonly MappedDragReleaseTracking _tracking = new();
    private readonly object _gate = new();
    private volatile bool _stopRequested;
    private MappedDragReleaseState _state = MappedDragReleaseState.Pending;
    private Win32Helper.POINT _releasePoint;
    private MappedDesktopDropPoint _releaseProbe = MappedDesktopDropPoint.None;

    internal MappedDragReleaseTracker(string sessionId)
    {
        SessionId = sessionId;
    }

    internal string SessionId { get; }

    internal event Action<MappedDragReleaseTracker>? Finished;

    internal MappedDragReleaseState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    internal bool TryGetRelease(out Win32Helper.POINT point, out MappedDesktopDropPoint probe)
    {
        lock (_gate)
        {
            point = _releasePoint;
            probe = _releaseProbe;
            return _state == MappedDragReleaseState.Released;
        }
    }

    internal void Start()
    {
        try
        {
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "DeskBox mapped drag release",
            };
            thread.Start();
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] DragToDesktop release tracker could not start: {ex.Message}");
            Finish(MappedDragReleaseState.Untracked, default, MappedDesktopDropPoint.None);
        }
    }

    internal void Stop() => _stopRequested = true;

    private void Run()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!_stopRequested && clock.Elapsed < MaxLifetime)
            {
                bool buttonDown = Win32Helper.IsKeyDown(VirtualKeyLeftButton) || Win32Helper.IsKeyDown(VirtualKeyRightButton);
                bool escapeDown = Win32Helper.IsKeyDown(VirtualKeyEscape);
                MappedDragReleaseState state = _tracking.Observe(clock.Elapsed, buttonDown, escapeDown);
                if (state == MappedDragReleaseState.Released)
                {
                    if (!Win32Helper.GetCursorPos(out Win32Helper.POINT point))
                    {
                        Finish(MappedDragReleaseState.Untracked, default, MappedDesktopDropPoint.None);
                        return;
                    }

                    MappedDesktopDropPoint probe = MappedDragToDesktopTarget.Probe(point);
                    App.Log(
                        $"[MappedOrganize] DragToDesktop released session={SessionId} at={point.X},{point.Y} " +
                        $"after={clock.ElapsedMilliseconds}ms {probe.Describe()}");
                    Finish(state, point, probe);
                    return;
                }

                if (state != MappedDragReleaseState.Pending)
                {
                    App.Log($"[MappedOrganize] DragToDesktop tracker session={SessionId} state={state} after={clock.ElapsedMilliseconds}ms");
                    Finish(state, default, MappedDesktopDropPoint.None);
                    return;
                }

                Thread.Sleep(PollInterval);
            }

            Finish(MappedDragReleaseState.Untracked, default, MappedDesktopDropPoint.None);
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] DragToDesktop release tracker failed: {ex.Message}");
            Finish(MappedDragReleaseState.Untracked, default, MappedDesktopDropPoint.None);
        }
    }

    private void Finish(MappedDragReleaseState state, Win32Helper.POINT point, MappedDesktopDropPoint probe)
    {
        lock (_gate)
        {
            _state = state;
            _releasePoint = point;
            _releaseProbe = probe;
        }

        try
        {
            Finished?.Invoke(this);
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] DragToDesktop tracker callback failed: {ex.Message}");
        }
    }
}
