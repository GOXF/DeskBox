using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// GOXF fork, 「映射整理」: after 「拖到桌面 = 放回桌面」 un-hides the entries,
/// moves their desktop icons to where the drag was released. Explorer adds a
/// newly visible entry asynchronously, so a short-lived STA worker polls the
/// desktop view for a few seconds. Skipped when the desktop uses 自动排列图标
/// (the shell ignores positions then); best effort throughout.
/// </summary>
internal static class MappedDragToDesktopPlacement
{
    private const int Attempts = 15;
    private static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(200);

    internal static void PlaceInBackground(IReadOnlyList<string> paths, MappedDesktopDropPoint drop)
    {
        if (paths.Count == 0 || drop.ListView == IntPtr.Zero)
        {
            return;
        }

        string[] snapshot = paths.ToArray();
        try
        {
            var thread = new Thread(() => Run(snapshot, drop))
            {
                IsBackground = true,
                Name = "DeskBox mapped drag placement",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Could not start desktop icon placement: {ex.Message}");
        }
    }

    private static void Run(string[] paths, MappedDesktopDropPoint drop)
    {
        try
        {
            DesktopIconPlacementNativeMethods.NotifyUpdated(paths);
            if (!DesktopIconPlacementNativeMethods.TryGetViewInfo(out DesktopIconPlacementNativeMethods.ViewInfo info))
            {
                App.Log("[MappedOrganize] Desktop view unavailable; put-back icons keep the shell's position.");
                return;
            }

            if (info.AutoArrange)
            {
                App.Log("[MappedOrganize] Desktop auto-arrange is on; put-back icons keep the shell's position.");
                return;
            }

            IReadOnlyList<(int X, int Y)> origins = MappedDragToDesktopPolicy.ComputeIconOrigins(
                drop.ClientX,
                drop.ClientY,
                info.SpacingX,
                info.SpacingY,
                paths.Length,
                drop.AreaTopClient,
                drop.AreaBottomClient);
            int positioned = 0;
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                Thread.Sleep(AttemptDelay);
                positioned = Math.Max(positioned, DesktopIconPlacementNativeMethods.TryPositionItems(paths, origins));
                if (positioned >= paths.Length)
                {
                    break;
                }
            }

            App.Log($"[MappedOrganize] Positioned {positioned}/{paths.Length} put-back icons at the drop point.");
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Desktop icon placement failed: {ex.Message}");
        }
    }
}
