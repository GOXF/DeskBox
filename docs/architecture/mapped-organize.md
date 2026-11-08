# 映射整理 (mapped / virtual desktop organize) — fork feature

Fork-only feature (GOXF/DeskBox `custom`). Like 360桌面助手 / WPS 桌面整理:
desktop items are grouped into widgets **without moving any file**.

## Model

* A mapped-organize widget is an ordinary `WidgetKind.File` widget whose
  `MappedFolderPath` is the user Desktop. The existing desktop branch of
  `WidgetViewModel.LoadFolderContentsAsync` already merges the Public Desktop
  and the folder watchers already watch both folders.
* `WidgetConfig.Metadata` carries the feature state, so nothing new is
  serialized and backup/restore/removal follow the widget automatically:
  * `DeskBox.MappedOrganize = "1"` – marks the widget.
  * `DeskBox.MappedOrganize.Category` – classifier category it collects.
  * `DeskBox.MappedOrganize.Members` – newline-separated absolute paths.
* Membership is exclusive (one widget per desktop entry). All rules are in the
  pure `Services/MappedOrganizeMembership.cs` (unit-tested in
  `tests/DeskBox.Tests/MappedOrganizeMembershipTests.cs`).
* `Services/MappedOrganizeService.cs` is the runtime owner (UI-thread
  mutations, desktop `FileSystemWatcher`s, auto-assignment, icon state).
* Global flags live in `data/mapped-organize.state` (plain `key=value`, no new
  JSON context so the serialization inventory contract stays frozen):
  `autoAssignNewItems` (default true) and `desktopIconsHiddenByDeskBox`.

## Flows

| Action | Effect |
|---|---|
| Preview option 「只映射，不移动文件」 | `DesktopOrganizationTaskView.MappedOrganize.cs` turns the selected buckets into `MappedOrganizeGroup`s (one per category; existing mapped widget of that category is reused). Files untouched. Everything not selected is then routed by the same rules when auto mode is on, because it would otherwise be invisible. |
| Open / rename / delete inside a mapped widget | Ordinary folder pipeline on the real file. Rename keeps membership (watcher rename → `RenamePath`); delete drops it. |
| Drag between mapped widgets / drop desktop items on one | `WidgetViewModel.ImportPathsAsync` → `ClaimMappedOrganizeDrop` → membership only. |
| Drop non-desktop items on a mapped widget | Normal import (moves into the Desktop folder); the new entry is claimed by that widget (`ExpectIncoming`, 2 min). |
| Drag from a mapped widget into a normal folder widget | Normal move; watcher delete drops the membership. |
| Changes outside DeskBox | Service watcher: rename → keep membership; delete → drop; create → auto-assign. Startup prunes members that no longer exist. |
| New desktop files | Auto mode (default): after `DesktopAutoOrganizationDelaySeconds` + 1 s size-stability check, classified with `DesktopOrganizationClassifier`; same-category mapped widget, else 「其他」 (created on demand). Manual mode: widget menu 「一键整理」 only. Toggle: widget menu 「自动归类新文件」. |
| Dissolve a mapped widget (close/remove widget) | `RemoveWidgetAsync` with `RemoveWidgetOnly` (mapped widgets never follow managed storage) – only the membership in its config disappears. Remaining mapped widgets pick up the orphans (auto mode). |
| 「退出映射整理（恢复桌面图标）」 | Dissolves every mapped widget, icons restored. |

Ordinary organization never targets mapped widgets: they are filtered out in
`DesktopOrganizationRuleResolver.Resolve`, `CreateExecutionPlan` and
`GetDestinationOptions`.

## Native desktop icons (user accepted hiding)

Choice: hide the desktop `SysListView32` (child of the desktop
`SHELLDLL_DefView`) with `ShowWindow(SW_HIDE)`
(`Services/DesktopIconVisibilityService.cs`), **not** Explorer's persisted
"Show desktop icons" setting.

* Not persisted by Explorer → no failure mode can leave the user permanently
  without icons (Explorer restart / sign-out always shows them again).
* Hidden while ≥1 mapped widget exists; shown when the last one is dissolved,
  on DeskBox shutdown (`ShutdownStep "mapped-organize"`), and best-effort in
  `AppDomain.ProcessExit`.
* Crash safety: `desktopIconsHiddenByDeskBox` is written *before* hiding. On
  the next start `MappedOrganizeService.Initialize` either re-applies (mapped
  widgets exist) or restores (none left). It runs even if widget restore fails.
* Explorer restarts recreate a visible list view; a 4 s dispatcher timer
  re-hides it while mapped widgets exist.

## Multi-monitor / sleep

Mapped widgets are normal widgets, so the existing monitor binding,
topology profiles and wake recovery apply unchanged.

## Upstream-touching edits (keep minimal for the 6-hourly upstream merge)

* `App.xaml.cs`: 2× `InitializeMappedOrganize(widgetManager);` after widget
  restore (success and failure path), 1 shutdown step.
* `WidgetViewModel.cs` (attach/detach), `.ItemHydration.cs` (filter desktop
  snapshot), `.SortingAndWatchers.cs` (upsert guard), `.Operations.cs` (drop
  claim).
* `DesktopOrganizationRuleResolver.cs`, `DesktopOrganizationCoordinator.cs`:
  exclude mapped widgets.
* `DesktopOrganizationTaskView.xaml` (+ `.Actions.cs` one call),
  `FileSurfaceContent.SelectionAndMenus.cs` (one call).
* `Strings/*.json`: 6 keys inserted after `DesktopOrganization.Preview.Title`
  (zh-CN / zh-TW translated, other locales English fallback).

## Known gaps

* Manual mode: new desktop files stay unassigned (and therefore invisible
  while icons are hidden) until 「一键整理」.
* Preview "use existing widget" destination choices are ignored in mapped mode
  (grouping is per category).
* No dedicated entry in the Settings window yet; the auto/manual switch is in
  the mapped widget's blank-area menu.
* Not runtime-verified on a real desktop in this change (CI builds and runs
  unit tests only).
