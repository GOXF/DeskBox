using DeskBox.Models;

namespace DeskBox.Services;

public enum ForkOneClickOrganizeOutcome
{
    Organized,
    NothingToOrganize,

    /// <summary>The preview has to take over (an interrupted organize must be recovered there first, or the mapped service is not up yet).</summary>
    NeedsPreview,
}

public sealed record ForkOneClickOrganizeResult(
    ForkOneClickOrganizeOutcome Outcome,
    DesktopOrganizeMode Mode,
    int OrganizedCount = 0,
    int RetainedCount = 0,
    string? UndoHistoryId = null,
    IReadOnlyList<string>? TargetNames = null);

/// <summary>
/// GOXF fork: 「整理桌面」 in one click. The desktop / tray / settings entries no
/// longer open the preview with its 待整理 / 留在桌面 split; they organize right
/// away by the global 「整理方式」 (fork-organize.json):
/// <list type="bullet">
/// <item>移动到格子: the preview's default plan (same rules, same defaults,
/// same transaction, history and undo) is executed directly.</item>
/// <item>只映射: every visible desktop entry is mapped, folders / large files
/// included (nothing is moved), and 「放回桌面」 marks are cleared first, so
/// put-back entries are organized too. Entries go to the mapped widget of
/// their own category when one exists, otherwise to the planner's bucket;
/// an existing mapped widget of the same category or name is always reused.</item>
/// </list>
/// The old preview stays reachable (「整理预览…」 in the tray menu and the
/// mapped widgets' menus). Automatic sorting of new files still respects
/// 「放回桌面」.
/// </summary>
public sealed class ForkOneClickOrganizer
{
    private readonly DesktopOrganizationCoordinator _coordinator;
    private readonly MappedOrganizeService? _mapped;
    private readonly Func<string, string> _categoryName;

    public ForkOneClickOrganizer(
        DesktopOrganizationCoordinator coordinator,
        MappedOrganizeService? mapped,
        Func<string, string> categoryName)
    {
        _coordinator = coordinator;
        _mapped = mapped;
        _categoryName = categoryName;
    }

    public async Task<ForkOneClickOrganizeResult> RunAsync(
        IntPtr ownerWindowHandle = default,
        CancellationToken cancellationToken = default)
    {
        DesktopOrganizeMode mode = ForkOrganizeModeSettings.Load();
        if (_coordinator.HasPendingRecovery)
        {
            return new ForkOneClickOrganizeResult(ForkOneClickOrganizeOutcome.NeedsPreview, mode);
        }

        if (ForkOrganizeModePolicy.ConfirmMapsInsteadOfMoving(mode))
        {
            return _mapped is null
                ? new ForkOneClickOrganizeResult(ForkOneClickOrganizeOutcome.NeedsPreview, mode)
                : await RunMappedAsync(_mapped, mode, cancellationToken);
        }

        return await RunMoveAsync(mode, ownerWindowHandle, cancellationToken);
    }

    private async Task<ForkOneClickOrganizeResult> RunMoveAsync(
        DesktopOrganizeMode mode,
        IntPtr ownerWindowHandle,
        CancellationToken cancellationToken)
    {
        DesktopOrganizationPlan preview = await _coordinator.BuildPlanAsync(false, cancellationToken);
        if (preview.EligibleItemCount == 0)
        {
            return new ForkOneClickOrganizeResult(ForkOneClickOrganizeOutcome.NothingToOrganize, mode);
        }

        // No selections: every bucket ticked, default destinations — exactly
        // what 「开始整理」 does on an untouched preview.
        DesktopOrganizationPlan plan = _coordinator.CreateExecutionPlan(preview, []);
        if (plan.EligibleItemCount == 0)
        {
            return new ForkOneClickOrganizeResult(ForkOneClickOrganizeOutcome.NothingToOrganize, mode);
        }

        DesktopOrganizationExecutionResult result =
            await _coordinator.ExecuteAsync(plan, progress: null, cancellationToken, ownerWindowHandle);
        return new ForkOneClickOrganizeResult(
            ForkOneClickOrganizeOutcome.Organized,
            mode,
            result.CompletedItems.Count,
            result.RetainedItems.Count,
            result.History.CanUndo ? result.History.Id : null,
            plan.Targets.Select(target => target.SuggestedDisplayName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList());
    }

    private async Task<ForkOneClickOrganizeResult> RunMappedAsync(
        MappedOrganizeService mapped,
        DesktopOrganizeMode mode,
        CancellationToken cancellationToken)
    {
        mapped.ReleaseAllKeptOnDesktop();
        DesktopOrganizationPlan scanned = await _coordinator.BuildPlanAsync(includeSlowItems: true, cancellationToken);
        IReadOnlyList<string> optIns = ForkOneClickOrganizePolicy.SelectOptIns(scanned.SourceItems);
        DesktopOrganizationPlan plan = optIns.Count > 0
            ? _coordinator.CreatePreviewPlanWithOptionalItems(scanned, optIns)
            : scanned;
        IReadOnlyList<MappedOrganizeGroup> groups = ForkOneClickOrganizePolicy.BuildMappedGroups(
            plan.Targets,
            mapped.MappedCategoryIds(),
            _categoryName);

        int count = groups.Count > 0 ? await mapped.ApplyGroupsAsync(groups) : 0;
        // Whatever the plan did not list (public desktop, overflow) follows
        // the same routing as 「一键整理」, also with automatic sorting off.
        count += await mapped.OrganizeNowAsync();
        return count > 0
            ? new ForkOneClickOrganizeResult(
                ForkOneClickOrganizeOutcome.Organized,
                mode,
                count,
                TargetNames: groups.Select(group => group.DisplayName).ToList())
            : new ForkOneClickOrganizeResult(ForkOneClickOrganizeOutcome.NothingToOrganize, mode);
    }
}

/// <summary>Pure decisions of the one-click organize (unit-tested in ForkOneClickOrganizeTests).</summary>
public static class ForkOneClickOrganizePolicy
{
    /// <summary>In 「只映射」 nothing is moved, so the opt-in kinds (folders, large files, batch overflow) are organized too.</summary>
    public static IReadOnlyList<string> SelectOptIns(IEnumerable<DesktopOrganizationFileSnapshot>? items) =>
        (items ?? [])
            .Where(item => item.CanOptIn && !string.IsNullOrWhiteSpace(item.SourcePath))
            .Select(item => item.SourcePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Turns planner buckets into mapped groups. An entry whose own category
    /// already has a mapped widget goes there (a lone new .png joins 「图片」
    /// instead of being merged into 「其他」); otherwise it follows its bucket.
    /// One group per category, in first-seen order.
    /// </summary>
    public static IReadOnlyList<MappedOrganizeGroup> BuildMappedGroups(
        IEnumerable<DesktopOrganizationTargetPlan>? targets,
        IReadOnlyCollection<string>? mappedCategoryIds,
        Func<string, string> categoryName)
    {
        var mapped = new HashSet<string>(mappedCategoryIds ?? [], StringComparer.Ordinal);
        var order = new List<string>();
        var paths = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var bounds = new Dictionary<string, DesktopOrganizationRect?>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DesktopOrganizationTargetPlan target in targets ?? [])
        {
            foreach (DesktopOrganizationFileSnapshot item in target.Items)
            {
                if (string.IsNullOrWhiteSpace(item.SourcePath) || !seen.Add(item.SourcePath))
                {
                    continue;
                }

                string categoryId = mapped.Contains(item.CategoryId) ? item.CategoryId : target.CategoryId;
                if (string.IsNullOrWhiteSpace(categoryId))
                {
                    categoryId = DesktopOrganizationCategoryIds.Other;
                }

                if (!paths.TryGetValue(categoryId, out List<string>? list))
                {
                    list = [];
                    paths.Add(categoryId, list);
                    order.Add(categoryId);
                }

                if (!bounds.ContainsKey(categoryId) &&
                    string.Equals(target.CategoryId, categoryId, StringComparison.Ordinal))
                {
                    bounds[categoryId] = target.PlannedBounds;
                }

                list.Add(item.SourcePath);
            }
        }

        return order
            .Select(categoryId => new MappedOrganizeGroup(
                categoryId,
                categoryName(categoryId),
                bounds.TryGetValue(categoryId, out DesktopOrganizationRect? rect) ? rect : null,
                paths[categoryId]))
            .ToList();
    }

    /// <summary>
    /// The mapped widget a group joins: same category first, then same name
    /// (trimmed, case-insensitive). Null means a new mapped widget is needed.
    /// </summary>
    public static WidgetConfig? FindMappedTarget(
        IEnumerable<WidgetConfig>? mappedWidgets,
        string categoryId,
        string? displayName)
    {
        List<WidgetConfig> widgets = (mappedWidgets ?? []).Where(widget => widget is not null).ToList();
        WidgetConfig? byCategory = widgets.FirstOrDefault(widget => string.Equals(
            MappedOrganizeWidgetMetadata.GetCategory(widget), categoryId, StringComparison.Ordinal));
        if (byCategory is not null)
        {
            return byCategory;
        }

        string name = displayName?.Trim() ?? string.Empty;
        return name.Length == 0
            ? null
            : widgets.FirstOrDefault(widget =>
                string.Equals(widget.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }
}
