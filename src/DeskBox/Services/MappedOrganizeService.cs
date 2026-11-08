using DeskBox.Models;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

/// <summary>
/// Runtime owner of 「映射整理」 (virtual desktop organize): creates the
/// mapped widgets, keeps membership in sync with the Desktop folders, routes
/// new desktop entries, and hides the native desktop icons while at least one
/// mapped widget exists. Files are never moved, renamed or deleted here.
///
/// Threading: every membership mutation runs on the UI dispatcher (the same
/// thread that owns <see cref="SettingsService.Settings"/>); file-system
/// watcher callbacks only marshal paths onto it.
/// </summary>
public sealed class MappedOrganizeService : IDisposable
{
    private const string StateFileName = "mapped-organize.state";
    private const string AutoAssignKey = "autoAssignNewItems";
    private const string IconsHiddenKey = "desktopIconsHiddenByDeskBox";
    private static readonly TimeSpan IconWatchdogInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ExpectedIncomingLifetime = TimeSpan.FromMinutes(2);

    private static readonly HashSet<string> TransientExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".download", ".tmp", ".opdownload"
    };

    private readonly SettingsService _settingsService;
    private readonly WidgetManager _widgetManager;
    private readonly LocalizationService _localizationService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DesktopOrganizationClassifier _classifier = new();
    private readonly string _statePath;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, (string WidgetId, DateTimeOffset Expires)> _expectedIncoming =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingNewEntries = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherQueueTimer? _iconWatchdog;
    private bool _autoAssignNewItems = true;
    private bool _iconsHiddenByDeskBox;
    private bool _pendingFlushScheduled;
    private bool _isDisposed;

    public MappedOrganizeService(
        SettingsService settingsService,
        WidgetManager widgetManager,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue,
        string? stateDirectory = null)
    {
        _settingsService = settingsService;
        _widgetManager = widgetManager;
        _localizationService = localizationService;
        _dispatcherQueue = dispatcherQueue;
        _statePath = Path.Combine(
            stateDirectory ?? DeskBoxDataPathService.Current.DataDirectory,
            StateFileName);
    }

    /// <summary>Process-wide instance set by the App host once widgets are restored.</summary>
    public static MappedOrganizeService? Current { get; internal set; }

    /// <summary>
    /// Raised on the UI thread with the ids of the mapped widgets whose
    /// membership changed. Static so widget view-models restored before the
    /// service exists can subscribe at construction.
    /// </summary>
    public static event Action<IReadOnlyCollection<string>>? MembershipChanged;

    public bool AutoAssignNewItems => _autoAssignNewItems;

    public bool HasMappedWidgets => LiveMappedWidgets().Any();

    public bool IsMappedWidget(string widgetId) =>
        LiveMappedWidgets().Any(widget => string.Equals(widget.Id, widgetId, StringComparison.Ordinal));

    /// <summary>Whether <paramref name="path"/> is a member of the widget (used by the widget's folder loader).</summary>
    public bool IsMember(string widgetId, string path)
    {
        WidgetConfig? widget = FindMappedWidget(widgetId);
        return widget is not null && MappedOrganizeMembership.IsMember(widget, path);
    }

    public IReadOnlySet<string> GetMemberSet(string widgetId)
    {
        WidgetConfig? widget = FindMappedWidget(widgetId);
        return widget is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : MappedOrganizeMembership.GetMembers(widget).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Startup: load the durable flags, repair state after a crash, start the
    /// watchers and apply the icon state. Safe to call once.
    /// </summary>
    public void Initialize()
    {
        LoadState();
        bool hadMappedWidgets = HasMappedWidgets;
        if (_iconsHiddenByDeskBox && !hadMappedWidgets)
        {
            App.Log("[MappedOrganize] Startup found a stale hidden-icons marker without mapped widgets; restoring icons.");
        }

        if (hadMappedWidgets)
        {
            IReadOnlyList<string> pruned = MappedOrganizeMembership.Prune(
                _settingsService.Settings.WidgetLayout.Widgets,
                path => File.Exists(path) || Directory.Exists(path));
            if (pruned.Count > 0)
            {
                _settingsService.SaveDebounced();
            }

            StartWatchers();
        }

        _widgetManager.WidgetRemoved += OnWidgetRemoved;
        ApplyIconState();
        if (hadMappedWidgets && _autoAssignNewItems)
        {
            _ = AssignUnassignedAsync(createOtherWidget: true);
        }
    }

    /// <summary>Shutdown: always give the native icons back. Membership stays for the next start.</summary>
    public void Shutdown()
    {
        if (_isDisposed)
        {
            return;
        }

        StopWatchers();
        _iconWatchdog?.Stop();
        _iconWatchdog = null;
        RestoreIconsForExit();
    }

    /// <summary>Best-effort restore usable from ProcessExit / fatal paths.</summary>
    public void RestoreIconsForExit()
    {
        try
        {
            DesktopIconVisibilityService.Apply(hidden: false);
            _iconsHiddenByDeskBox = false;
            SaveState();
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Restoring desktop icons on exit failed: {ex.Message}");
        }
    }

    public void SetAutoAssignNewItems(bool enabled)
    {
        if (_autoAssignNewItems == enabled)
        {
            return;
        }

        _autoAssignNewItems = enabled;
        SaveState();
        if (enabled && HasMappedWidgets)
        {
            _ = AssignUnassignedAsync(createOtherWidget: true);
        }
    }

    /// <summary>
    /// Executes the preview in mapped mode: one mapped widget per category
    /// group (re-using an existing mapped widget of that category), membership
    /// written, files untouched.
    /// </summary>
    public async Task<int> ApplyGroupsAsync(IReadOnlyList<MappedOrganizeGroup> groups)
    {
        var created = new List<(WidgetConfig Config, DesktopOrganizationRect? Bounds)>();
        var touched = new HashSet<string>(StringComparer.Ordinal);
        int assignedCount = 0;
        foreach (MappedOrganizeGroup group in groups.Where(group => group.Paths.Count > 0))
        {
            WidgetConfig? target = LiveMappedWidgets().FirstOrDefault(widget => string.Equals(
                MappedOrganizeWidgetMetadata.GetCategory(widget), group.CategoryId, StringComparison.Ordinal));
            if (target is null)
            {
                target = CreateMappedConfig(group.CategoryId, group.DisplayName);
                created.Add((target, group.PlannedBounds));
                _widgetManager.AddMappedOrganizeConfig(target, group.PlannedBounds);
            }

            foreach (string id in MappedOrganizeMembership.Assign(_settingsService.Settings.WidgetLayout.Widgets, target.Id, group.Paths))
            {
                touched.Add(id);
            }

            assignedCount += group.Paths.Count;
        }

        await _settingsService.SaveAsync();
        foreach ((WidgetConfig config, _) in created)
        {
            await _widgetManager.ShowMappedOrganizeWidgetAsync(config);
            touched.Remove(config.Id);
        }

        StartWatchers();
        ApplyIconState();
        RaiseMembershipChanged(touched);
        if (_autoAssignNewItems)
        {
            // Everything the preview left out (folders, retained buckets,
            // hidden-by-choice) would be invisible once the native icons are
            // hidden, so it is routed by the same rules right away.
            await AssignUnassignedAsync(createOtherWidget: true);
        }

        return assignedCount;
    }

    /// <summary>「一键整理」: routes every unassigned desktop entry.</summary>
    public Task<int> OrganizeNowAsync() => AssignUnassignedAsync(createOtherWidget: true);

    /// <summary>
    /// Drag between mapped widgets or a drop of desktop entries onto a mapped
    /// widget: only membership changes. Returns the paths that were desktop
    /// entries (and therefore handled); other paths are left to the caller.
    /// </summary>
    public IReadOnlyList<string> AssignDesktopPaths(string widgetId, IEnumerable<string> paths)
    {
        var desktopPaths = paths.Where(IsDesktopEntryPath).ToList();
        if (desktopPaths.Count == 0)
        {
            return [];
        }

        IReadOnlyList<string> changed = MappedOrganizeMembership.Assign(
            _settingsService.Settings.WidgetLayout.Widgets, widgetId, desktopPaths);
        if (changed.Count > 0)
        {
            _settingsService.SaveDebounced();
            RaiseMembershipChanged(changed);
        }

        return desktopPaths;
    }

    /// <summary>
    /// Entries the drop moves INTO the Desktop folder for a mapped widget land
    /// in that widget instead of following the routing rules.
    /// </summary>
    public void ExpectIncoming(string widgetId, IEnumerable<string> sourcePaths)
    {
        DateTimeOffset expires = DateTimeOffset.UtcNow + ExpectedIncomingLifetime;
        foreach (string source in sourcePaths)
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
            if (!string.IsNullOrWhiteSpace(name))
            {
                _expectedIncoming[name] = (widgetId, expires);
            }
        }
    }

    /// <summary>Turns the mode off: dissolves every mapped widget (membership only) and restores the icons.</summary>
    public async Task DissolveAllAsync()
    {
        foreach (WidgetConfig widget in LiveMappedWidgets().ToList())
        {
            await _widgetManager.RemoveWidgetAsync(widget.Id);
        }

        ApplyIconState();
    }

    public static bool IsDesktopEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string parent = Path.GetDirectoryName(MappedOrganizeMembership.NormalizePath(path)) ?? string.Empty;
            var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
            return PathEquals(parent, userDesktop) || PathEquals(parent, publicDesktop);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        Shutdown();
        _widgetManager.WidgetRemoved -= OnWidgetRemoved;
        _isDisposed = true;
    }

    private async Task<int> AssignUnassignedAsync(bool createOtherWidget, IReadOnlyCollection<string>? onlyPaths = null)
    {
        if (_isDisposed || !HasMappedWidgets)
        {
            return 0;
        }

        List<(string Path, bool IsDirectory)> entries = await Task.Run(() => EnumerateDesktopEntries(onlyPaths));
        IReadOnlyList<string> unassigned = MappedOrganizeMembership.FindUnassigned(
            _settingsService.Settings.WidgetLayout.Widgets,
            entries.Select(entry => entry.Path));
        if (unassigned.Count == 0)
        {
            return 0;
        }

        var isDirectory = entries.ToDictionary(entry => MappedOrganizeMembership.NormalizePath(entry.Path), entry => entry.IsDirectory, StringComparer.OrdinalIgnoreCase);
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var routed = new List<(string Path, string CategoryId)>();
        foreach (string path in unassigned)
        {
            if (TryTakeExpectedIncoming(path, out string widgetId) && IsMappedWidget(widgetId))
            {
                touched.UnionWith(MappedOrganizeMembership.Assign(_settingsService.Settings.WidgetLayout.Widgets, widgetId, [path]));
                continue;
            }

            routed.Add((path, MappedOrganizeMembership.Classify(
                _classifier, path, isDirectory.TryGetValue(path, out bool directory) && directory)));
        }

        var plan = MappedOrganizeMembership.PlanAssignments(_settingsService.Settings.WidgetLayout.Widgets, routed, out List<string> needsOther);
        foreach ((string widgetId, List<string> paths) in plan)
        {
            touched.UnionWith(MappedOrganizeMembership.Assign(_settingsService.Settings.WidgetLayout.Widgets, widgetId, paths));
        }

        WidgetConfig? createdOther = null;
        if (needsOther.Count > 0 && createOtherWidget)
        {
            createdOther = CreateMappedConfig(
                DesktopOrganizationCategoryIds.Other,
                _localizationService.T("DesktopOrganization.Category.Other"));
            _widgetManager.AddMappedOrganizeConfig(createdOther, null);
            MappedOrganizeMembership.Assign(_settingsService.Settings.WidgetLayout.Widgets, createdOther.Id, needsOther);
        }

        if (touched.Count == 0 && createdOther is null)
        {
            return 0;
        }

        await _settingsService.SaveAsync();
        if (createdOther is not null)
        {
            await _widgetManager.ShowMappedOrganizeWidgetAsync(createdOther);
        }

        RaiseMembershipChanged(touched);
        return unassigned.Count - (createOtherWidget ? 0 : needsOther.Count);
    }

    private static List<(string Path, bool IsDirectory)> EnumerateDesktopEntries(IReadOnlyCollection<string>? onlyPaths)
    {
        var result = new List<(string, bool)>();
        IEnumerable<string> candidates;
        if (onlyPaths is not null)
        {
            candidates = onlyPaths;
        }
        else
        {
            var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
            candidates = SafeEnumerate(userDesktop).Concat(SafeEnumerate(publicDesktop));
        }

        foreach (string path in candidates)
        {
            try
            {
                var info = new FileInfo(path);
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 ||
                    TransientExtensions.Contains(info.Extension) ||
                    string.Equals(info.Name, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add((path, (attributes & FileAttributes.Directory) != 0));
            }
            catch (Exception)
            {
                // Vanished or inaccessible: the watcher reports the outcome.
            }
        }

        return result;
    }

    private static IEnumerable<string> SafeEnumerate(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Enumerating '{directory}' failed: {ex.Message}");
            return [];
        }
    }

    private WidgetConfig CreateMappedConfig(string categoryId, string displayName)
    {
        var (userDesktop, _) = FileService.GetDesktopPaths();
        var config = new WidgetConfig
        {
            Name = string.IsNullOrWhiteSpace(displayName) ? categoryId : displayName,
            IsDefaultTitle = false,
            WidgetKind = WidgetKind.File,
            MappedFolderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userDesktop)),
            FollowsDefaultStoragePath = false,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = _settingsService.Settings.WidgetShell.DefaultWidgetWidth,
            Height = _settingsService.Settings.WidgetShell.DefaultWidgetHeight,
            IsVisible = true
        };
        MappedOrganizeWidgetMetadata.Mark(config, categoryId);
        return config;
    }

    private IEnumerable<WidgetConfig> LiveMappedWidgets() =>
        MappedOrganizeMembership.MappedWidgets(_settingsService.Settings.WidgetLayout.Widgets)
            .Where(widget => !_widgetManager.IsWidgetPendingDeletion(widget.Id));

    private WidgetConfig? FindMappedWidget(string widgetId) =>
        LiveMappedWidgets().FirstOrDefault(widget => string.Equals(widget.Id, widgetId, StringComparison.Ordinal));

    private bool TryTakeExpectedIncoming(string path, out string widgetId)
    {
        widgetId = string.Empty;
        string name = Path.GetFileName(path);
        if (!_expectedIncoming.TryGetValue(name, out var expected))
        {
            return false;
        }

        _expectedIncoming.Remove(name);
        if (expected.Expires < DateTimeOffset.UtcNow)
        {
            return false;
        }

        widgetId = expected.WidgetId;
        return true;
    }

    private void OnWidgetRemoved(string widgetId)
    {
        // The removed widget's membership lived in its own config, which is
        // already gone: dissolving never touches files. Remaining mapped
        // widgets pick up the now-unassigned entries; with none left the
        // native desktop icons come back.
        Enqueue(async () =>
        {
            ApplyIconState();
            if (HasMappedWidgets)
            {
                if (_autoAssignNewItems)
                {
                    await AssignUnassignedAsync(createOtherWidget: true);
                }
            }
            else
            {
                StopWatchers();
            }
        });
    }

    private void ApplyIconState()
    {
        bool shouldHide = HasMappedWidgets;
        if (shouldHide && !_iconsHiddenByDeskBox)
        {
            // Durable marker first: a crash after hiding must be recoverable.
            _iconsHiddenByDeskBox = true;
            SaveState();
        }

        DesktopIconVisibilityService.Apply(shouldHide);
        if (!shouldHide && _iconsHiddenByDeskBox)
        {
            _iconsHiddenByDeskBox = false;
            SaveState();
        }

        if (shouldHide)
        {
            EnsureIconWatchdog();
        }
        else
        {
            _iconWatchdog?.Stop();
        }
    }

    private void EnsureIconWatchdog()
    {
        if (_iconWatchdog is null)
        {
            _iconWatchdog = _dispatcherQueue.CreateTimer();
            _iconWatchdog.Interval = IconWatchdogInterval;
            _iconWatchdog.IsRepeating = true;
            _iconWatchdog.Tick += (_, _) =>
            {
                // Explorer restarts recreate a visible list view.
                if (!_isDisposed && HasMappedWidgets)
                {
                    DesktopIconVisibilityService.Apply(hidden: true);
                }
            };
        }

        if (!_iconWatchdog.IsRunning)
        {
            _iconWatchdog.Start();
        }
    }

    private void StartWatchers()
    {
        if (_watchers.Count > 0)
        {
            return;
        }

        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        foreach (string directory in new[] { userDesktop, publicDesktop }
                     .Where(directory => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024
                };
                watcher.Created += (_, e) => Enqueue(() => OnEntryCreated(e.FullPath));
                watcher.Deleted += (_, e) => Enqueue(() => OnEntryDeleted(e.FullPath));
                watcher.Renamed += (_, e) => Enqueue(() => OnEntryRenamed(e.OldFullPath, e.FullPath));
                watcher.Error += (_, e) =>
                {
                    App.Log($"[MappedOrganize] Desktop watcher error: {e.GetException().Message}");
                    Enqueue(() =>
                    {
                        if (MappedOrganizeMembership.Prune(_settingsService.Settings.WidgetLayout.Widgets,
                                path => File.Exists(path) || Directory.Exists(path)) is { Count: > 0 } changed)
                        {
                            _settingsService.SaveDebounced();
                            RaiseMembershipChanged(changed);
                        }

                        return _autoAssignNewItems ? AssignUnassignedAsync(createOtherWidget: true) : Task.CompletedTask;
                    });
                };
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                App.Log($"[MappedOrganize] Could not watch '{directory}': {ex.Message}");
            }
        }
    }

    private void StopWatchers()
    {
        foreach (FileSystemWatcher watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception)
            {
                // Disposal is best effort.
            }
        }

        _watchers.Clear();
    }

    private Task OnEntryCreated(string path)
    {
        if (!_autoAssignNewItems || !HasMappedWidgets)
        {
            return Task.CompletedTask;
        }

        _pendingNewEntries.Add(path);
        SchedulePendingFlush();
        return Task.CompletedTask;
    }

    private Task OnEntryDeleted(string path)
    {
        _pendingNewEntries.Remove(path);
        IReadOnlyList<string> changed = MappedOrganizeMembership.RemovePath(_settingsService.Settings.WidgetLayout.Widgets, path);
        if (changed.Count > 0)
        {
            _settingsService.SaveDebounced();
            RaiseMembershipChanged(changed);
        }

        return Task.CompletedTask;
    }

    private Task OnEntryRenamed(string oldPath, string newPath)
    {
        _pendingNewEntries.Remove(oldPath);
        IReadOnlyList<string> changed = MappedOrganizeMembership.RenamePath(_settingsService.Settings.WidgetLayout.Widgets, oldPath, newPath);
        if (changed.Count > 0)
        {
            _settingsService.SaveDebounced();
            RaiseMembershipChanged(changed);
            return Task.CompletedTask;
        }

        // A download finishing (x.crdownload -> x.zip) is a new visible entry.
        return OnEntryCreated(newPath);
    }

    private void SchedulePendingFlush()
    {
        if (_pendingFlushScheduled)
        {
            return;
        }

        _pendingFlushScheduled = true;
        int delaySeconds = Math.Clamp(_settingsService.Settings.DesktopOrganization.DesktopAutoOrganizationDelaySeconds, 2, 600);
        _ = Task.Run(async () =>
        {
            // Same settle delay as automatic organization, then one more
            // second to see that the size stopped changing.
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            Enqueue(FlushPendingAsync);
        });
    }

    private async Task FlushPendingAsync()
    {
        _pendingFlushScheduled = false;
        if (_pendingNewEntries.Count == 0 || _isDisposed)
        {
            return;
        }

        string[] batch = _pendingNewEntries.ToArray();
        _pendingNewEntries.Clear();
        string[] stable = await Task.Run(async () =>
        {
            var sizes = batch.ToDictionary(path => path, TryGetLength, StringComparer.OrdinalIgnoreCase);
            await Task.Delay(TimeSpan.FromSeconds(1));
            return batch.Where(path => (File.Exists(path) || Directory.Exists(path)) && sizes[path] == TryGetLength(path)).ToArray();
        });
        foreach (string unstable in batch.Except(stable, StringComparer.OrdinalIgnoreCase)
                     .Where(path => File.Exists(path)))
        {
            _pendingNewEntries.Add(unstable);
        }

        if (_pendingNewEntries.Count > 0)
        {
            SchedulePendingFlush();
        }

        if (stable.Length > 0)
        {
            await AssignUnassignedAsync(createOtherWidget: true, stable);
        }
    }

    private static long TryGetLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : -1;
        }
        catch (Exception)
        {
            return -2;
        }
    }

    private void RaiseMembershipChanged(IEnumerable<string> widgetIds)
    {
        var ids = widgetIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return;
        }

        MembershipChanged?.Invoke(ids);
    }

    private void Enqueue(Func<Task> work)
    {
        if (_isDisposed)
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                App.Log($"[MappedOrganize] Background step failed: {ex}");
            }
        });
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return;
            }

            foreach (string line in File.ReadAllLines(_statePath))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                string key = line[..separator].Trim();
                bool value = string.Equals(line[(separator + 1)..].Trim(), "true", StringComparison.OrdinalIgnoreCase);
                if (string.Equals(key, AutoAssignKey, StringComparison.Ordinal))
                {
                    _autoAssignNewItems = value;
                }
                else if (string.Equals(key, IconsHiddenKey, StringComparison.Ordinal))
                {
                    _iconsHiddenByDeskBox = value;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Could not read state: {ex.Message}");
        }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            string temporary = _statePath + ".tmp";
            File.WriteAllLines(temporary,
            [
                $"{AutoAssignKey}={(_autoAssignNewItems ? "true" : "false")}",
                $"{IconsHiddenKey}={(_iconsHiddenByDeskBox ? "true" : "false")}"
            ]);
            File.Move(temporary, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            App.Log($"[MappedOrganize] Could not write state: {ex.Message}");
        }
    }

    private static bool PathEquals(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
