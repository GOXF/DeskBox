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
    private volatile bool _restoredForExit;

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
        }

        _keptOnDesktop.Clear();
        _keptOnDesktop.UnionWith(ReadList(KeptPath, MappedOrganizeHiddenLedger.Parse));
    }

    private void SaveKeptOnDesktop() =>
        WriteList(KeptPath, MappedOrganizeHiddenLedger.Serialize(_keptOnDesktop));

    /// <summary>
    /// Hides the members of the live mapped widgets and restores every other
    /// entry DeskBox hid. The ledger is written before an attribute is set,
    /// so a crash can only leave recorded entries hidden, which the next start
    /// (or the uninstaller) restores.
    /// </summary>
    private void ReconcileDesktopVisibility()
    {
        lock (_visibilityGate)
        {
            List<string> desired = _restoredForExit || _isDisposed
                ? new List<string>()
                : LiveMappedWidgets().SelectMany(MappedOrganizeMembership.GetMembers).ToList();
            MappedOrganizeHiddenLedger.Plan plan = MappedOrganizeHiddenLedger.Reconcile(desired, _hiddenLedger, ProbeAttributes);
            if (plan.ToHide.Count == 0 && plan.ToUnhide.Count == 0 && plan.Forget.Count == 0)
            {
                return;
            }

            bool ledgerChanged = false;
            foreach (string path in plan.ToHide)
            {
                ledgerChanged |= _hiddenLedger.Add(path);
            }

            foreach (string path in plan.Forget)
            {
                ledgerChanged |= _hiddenLedger.Remove(path);
            }

            if (ledgerChanged)
            {
                PersistHiddenLedger();
            }

            int hidden = 0;
            foreach (string path in plan.ToHide)
            {
                if (TrySetHidden(path, hidden: true))
                {
                    hidden++;
                }
                else
                {
                    // Not ours to hide (e.g. the Public Desktop without admin
                    // rights): it stays visible on the desktop too.
                    _hiddenLedger.Remove(path);
                    ledgerChanged = true;
                }
            }

            int restored = 0;
            foreach (string path in plan.ToUnhide)
            {
                if (TrySetHidden(path, hidden: false))
                {
                    restored++;
                    _hiddenLedger.Remove(path);
                    ledgerChanged = true;
                }
            }

            if (ledgerChanged)
            {
                PersistHiddenLedger();
            }

            if (hidden > 0 || restored > 0)
            {
                App.Log($"[MappedOrganize] Desktop visibility: hid {hidden}, restored {restored}, ledger {_hiddenLedger.Count}.");
            }
        }
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

    private static bool TrySetHidden(string path, bool hidden)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            FileAttributes updated = hidden
                ? attributes | FileAttributes.Hidden
                : attributes & ~FileAttributes.Hidden;
            if (updated != attributes)
            {
                File.SetAttributes(path, updated);
            }

            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Could not {(hidden ? "hide" : "show")} '{path}': {ex.Message}");
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
