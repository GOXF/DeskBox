using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// GOXF fork, 「映射整理」: dragging items out of a mapped widget and dropping
/// them on the system desktop is 「放回桌面」. The items already live in the
/// desktop folder, so for Explorer that drop is a same-folder move (nothing
/// happens) and they would stay hidden and mapped; DeskBox recognises the
/// drop from the release point instead. Drops on a desktop icon, on other
/// folders, on other widgets, and every drag out of a move-mode widget keep
/// the upstream behaviour.
/// </summary>
public sealed partial class FileSurfaceContent
{
    private const int VirtualKeyLeftButton = 0x01;
    private const int VirtualKeyRightButton = 0x02;
    private const int VirtualKeyEscape = 0x1B;

    /// <summary>Returns true when the completed drag was turned into 「放回桌面」.</summary>
    private bool TryPutBackMappedDragOnDesktop(string[] draggedPaths, bool handledInternally)
    {
        if (handledInternally ||
            draggedPaths.Length == 0 ||
            !ViewModel.IsMappedOrganizeWidget ||
            MappedOrganizeService.Current is not { } service ||
            !Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            return false;
        }

        MappedDesktopDropPoint drop = MappedDragToDesktopTarget.Probe(cursor);
        if (!MappedDragToDesktopPolicy.ShouldPutBack(
                isMappedWidget: true,
                handledInternally,
                drop.Kind,
                drop.OnIcon,
                mouseButtonDown: Win32Helper.IsKeyDown(VirtualKeyLeftButton) || Win32Helper.IsKeyDown(VirtualKeyRightButton),
                escapePressed: Win32Helper.IsKeyDown(VirtualKeyEscape)))
        {
            return false;
        }

        IReadOnlyList<string> paths = MappedDragToDesktopPolicy.ComputePutBackPaths(
            draggedPaths,
            path => File.Exists(path) || Directory.Exists(path),
            MappedOrganizeService.IsDesktopEntryPath);
        if (paths.Count == 0)
        {
            return false;
        }

        int kept = service.KeepOnDesktop(paths);
        App.Log(
            $"[MappedOrganize] Drag to desktop: put back {kept}/{draggedPaths.Length} " +
            $"widget={WidgetId} target={drop.Kind} at={cursor.X},{cursor.Y}.");
        if (kept <= 0)
        {
            return false;
        }

        MappedDragToDesktopPlacement.PlaceInBackground(paths, drop);
        ShowFeedback(new WidgetFeedbackRequest(
            ForkOrganizeModeText.Format(ForkOrganizeModeText.Key.KeptOnDesktopFormat, kept),
            WidgetFeedbackSeverity.Success,
            "mapped-keep-desktop"));
        return true;
    }
}
