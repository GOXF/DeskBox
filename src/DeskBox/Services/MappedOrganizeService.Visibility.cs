using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// 「映射整理」 per-item desktop visibility, 「放回桌面」 and the global
/// 「整理方式」 (GOXF fork). See docs/architecture/mapped-organize.md.
/// </summary>
public sealed partial class MappedOrganizeService
{
    private readonly object _visibilityGate = new();
    private readonly HashSet<string> _hiddenLedger = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _keptOnDesktop = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _modeWatcher;
    private DesktopOrganizeMode _mode = DesktopOrganizeMode.MoveToWidgets;
    private readonly MappedOrganizeExitGate _exitGate = new();
    private readonly Dictionary<string, MappedOrganizeFileIdentity> _hiddenIdentities = new(StringComparer.OrdinalIgnoreCase);
    private MappedDesktopSurfaceController? _desktopSurface;

    /// <summary>Entries currently marked 「放回桌面」 (copy; UI thread).</summary>
    public IReadOnlyCollection<string> KeptOnDesktopSnapshot() => _keptOnDesktop.ToArray();

    /// <summary>
    /// One-click 「整理桌面」: clears every 「放回桌面」 mark so those entries are
    /// organized like any other desktop entry. Returns how many were cleared.
    /// </summary>
    public int ReleaseAllKeptOnDesktop()
    {
        int count = _keptOnDesktop.Count;
        if (count > 0)
        {
            _keptOnDesktop.Clear();
            SaveKeptOnDesktop();
            App.Log($"[MappedOrganize] One-click organize released {count} kept-on-desktop entries.");
        }

        return count;
    }

    /// <summary>Categories that already have a (live) mapped widget.</summary>
    public IReadOnlyCollection<string> MappedCategoryIds() =>
        LiveMappedWidgets()
            .Select(MappedOrganizeWidgetMetadata.GetCategory)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The organize mode the main process last applied.</summary>
    public DesktopOrganizeMode Mode => _mode;

    private string HiddenLedgerPath => Path.Combine(_stateDirectory, MappedOrganizeHiddenLedger.FileName);

    private string KeptPath => Path.Combine(_stateDirectory, MappedOrganizeKeepList.FileName);

    /// <summary>
    /// Called by the upstream automatic organizer (any thread) for a settled
    /// new desktop file. In 「只映射」 mode the file is never moved: it is handed
    /// to the mapped routing instead and true is returned.
    /// </summary>
    public static bool TryTakeOverAutoOrganization(string path)
    {
        if (ForkOrganizeModePolicy.AutoOrganizerMayMoveFiles(ForkOrganizeModeSettings.Load()))
        {
            return false;
        }

        if (Current is { } service)
        {
            service.Enqueue(() => service.OnAutoOrganizedEntry(path));
        }

        return true;
    }

    /// <summary>
    /// 「放回桌面」 for entries of a mapped widget: leave every mapped widget,
    /// become visible on the desktop again, and stay out of automatic sorting.
    /// Returns the number of entries put back.
    /// </summary>
    public int KeepOnDesktop(IEnumerable<string> paths)
    {
        var desktopPaths = paths
            .Where(IsDesktopEntryPath)
            .Select(MappedOrganizeMembership.NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (desktopPaths.Count == 0)
        {
            return 0;
        }

        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in desktopPaths)
        {
            _pendingNewEntries.Remove(path);
            changed.UnionWith(MappedOrganizeMembership.RemovePath(_settingsService.Settings.WidgetLayout.Widgets, path));
        }

        if (MappedOrganizeKeepList.Keep(_keptOnDesktop, desktopPaths))
        {
            SaveKeptOnDesktop();
        }

        if (changed.Count > 0)
        {
            _settingsService.SaveDebounced();
        }

        RaiseMembershipChanged(changed);
        App.Log($"[MappedOrganize] Put {desktopPaths.Count} entries back on the desktop.");
        return desktopPaths.Count;
    }

    private Task OnAutoOrganizedEntry(string path)
    {
        if (_isDisposed || !HasMappedWidgets)
        {
            return Task.CompletedTask;
        }

        _pendingNewEntries.Add(path);
        SchedulePendingFlush();
        return Task.CompletedTask;
    }

    private void LoadVisibilityState()
    {
        lock (_visibilityGate)
        {
            _hiddenLedger.Clear();
            _hiddenLedger.UnionWith(ReadList(HiddenLedgerPath, MappedOrganizeHiddenLedger.Parse));
            MappedOrganizeHiddenRegistry.Publish(_hiddenLedger);
            foreach (string path in _hiddenLedger)
            {
                CaptureIdentity(path);
            }
        }

        _keptOnDesktop.Clear();
        _keptOnDesktop.UnionWith(ReadList(KeptPath, MappedOrganizeHiddenLedger.Parse));
    }

    private void SaveKeptOnDesktop() =>
        WriteList(KeptPath, MappedOrganizeHiddenLedger.Serialize(_keptOnDesktop));

    /// <summary>
    /// Restores attributes changed by older builds, then filters mapped entries
    /// only in our desktop view. File dialogs keep seeing the real files.
    /// </summary>
    private void ReconcileDesktopVisibility()
    {
        lock (_visibilityGate)
        {
            MappedOrganizeHiddenLedger.Plan plan = MappedOrganizeHiddenLedger.Reconcile([], _hiddenLedger, ProbeAttributes);
            bool ledgerChanged = false;
            foreach (string path in plan.Forget)
            {
                ledgerChanged |= _hiddenLedger.Remove(path);
                _hiddenIdentities.Remove(path);
            }

            int restored = 0;
            foreach (string path in plan.ToUnhide)
            {
                if (TryRestoreHidden(path))
                {
                    restored++;
                    _hiddenLedger.Remove(path);
                    _hiddenIdentities.Remove(path);
                    ledgerChanged = true;
                }
            }

            if (ledgerChanged)
            {
                PersistHiddenLedger();
            }

            if (restored > 0)
            {
                App.Log($"[MappedOrganize] Restored {restored} legacy hidden entries; ledger {_hiddenLedger.Count}.");
            }
        }

        if ((_exitGate.IsRestored || _isDisposed) && !_dispatcherQueue.HasThreadAccess)
        {
            // ProcessExit can run off the UI thread; restore native icons now,
            // and leave COM teardown to the thread that created the view.
            _desktopSurface?.RestoreNativeIcons();
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            UpdateDesktopSurface();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(UpdateDesktopSurface);
        }
    }

    private void UpdateDesktopSurface()
    {
        if (_exitGate.IsRestored || _isDisposed || !HasMappedWidgets || _mode != DesktopOrganizeMode.MapOnly)
        {
            _desktopSurface?.Dispose();
            _desktopSurface = null;
            return;
        }

        _desktopSurface ??= new MappedDesktopSurfaceController(_dispatcherQueue);
        _desktopSurface.Update(LiveMappedWidgets().SelectMany(MappedOrganizeMembership.GetMembers).ToArray());
    }

    private void RenameVisibilityRecords(string oldPath, string newPath, out bool wasKept)
    {
        wasKept = MappedOrganizeKeepList.Rename(_keptOnDesktop, oldPath, newPath);
        if (wasKept)
        {
            SaveKeptOnDesktop();
        }

        lock (_visibilityGate)
        {
            if (MappedOrganizeHiddenLedger.Rename(_hiddenLedger, oldPath, newPath))
            {
                string oldKey = MappedOrganizeMembership.NormalizePath(oldPath);
                if (_hiddenIdentities.Remove(oldKey, out MappedOrganizeFileIdentity identity))
                {
                    _hiddenIdentities[MappedOrganizeMembership.NormalizePath(newPath)] = identity;
                }

                PersistHiddenLedger();
            }
        }
    }

    private void PersistHiddenLedger()
    {
        MappedOrganizeHiddenRegistry.Publish(_hiddenLedger);
        WriteList(HiddenLedgerPath, MappedOrganizeHiddenLedger.Serialize(_hiddenLedger));
    }

    private static FileAttributes? ProbeAttributes(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path) ? File.GetAttributes(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryRestoreHidden(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            FileAttributes updated = attributes & ~FileAttributes.Hidden;
            if (updated != attributes)
            {
                File.SetAttributes(path, updated);
            }

            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Could not restore legacy hidden entry '{path}': {ex.Message}");
            return false;
        }
    }

    private void StartModeWatcher()
    {
        if (_modeWatcher is not null)
        {
            return;
        }

        try
        {
            string file = ForkOrganizeModeSettings.FilePath;
            string? directory = Path.GetDirectoryName(file);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var watcher = new FileSystemWatcher(directory, Path.GetFileName(file))
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, _) => Enqueue(OnModeFileChangedAsync);
            watcher.Created += (_, _) => Enqueue(OnModeFileChangedAsync);
            watcher.Renamed += (_, _) => Enqueue(OnModeFileChangedAsync);
            watcher.EnableRaisingEvents = true;
            _modeWatcher = watcher;
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Could not watch the organize-mode file: {ex.Message}");
        }
    }

    private void StopModeWatcher()
    {
        try
        {
            if (_modeWatcher is not null)
            {
                _modeWatcher.EnableRaisingEvents = false;
                _modeWatcher.Dispose();
            }
        }
        catch (Exception)
        {
            // Disposal is best effort.
        }

        _modeWatcher = null;
    }

    private async Task OnModeFileChangedAsync()
    {
        DesktopOrganizeMode current = ForkOrganizeModeSettings.Load();
        DesktopOrganizeMode previous = _mode;
        if (current == previous)
        {
            return;
        }

        _mode = current;
        App.Log($"[MappedOrganize] Organize mode changed: {previous} -> {current}.");
        if (ForkOrganizeModePolicy.OnModeChanged(previous, current, HasMappedWidgets) ==
            OrganizeModeTransitionAction.DissolveMapping)
        {
            await DissolveAllAsync();
        }

        ForkOrganizeModeSettings.RaiseChanged(current);
    }

    private static IReadOnlyList<string> ReadList(string path, Func<string, IReadOnlyList<string>> parse)
    {
        try
        {
            return File.Exists(path) ? parse(File.ReadAllText(path)) : [];
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Could not read '{Path.GetFileName(path)}': {ex.Message}");
            return [];
        }
    }

    private static void WriteList(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Could not write '{Path.GetFileName(path)}': {ex.Message}");
        }
    }
}
