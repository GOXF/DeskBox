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

## Global 「整理方式」 (organize mode)

* Settings → 桌面整理 → 「整理方式」: 「移动到格子」 (default, upstream) /
  「只映射，不移动文件」. Stored in `<data root>/fork-organize.json`
  (`{"organizeMode":"move|mapOnly"}`, `Services/ForkOrganizeModeSettings.cs`),
  written by the settings process, read by the main process at use time and
  watched by `MappedOrganizeService` (FileSystemWatcher).
* The preview no longer has a checkbox: its footer shows the current mode
  (link opens the setting) and 「开始整理」 follows it.
* Automatic organization of new files: in 「只映射」 mode the upstream
  `DesktopAutoOrganizationWatcher` never moves; it hands the settled file to
  `MappedOrganizeService.TryTakeOverAutoOrganization` (mapped routing).
* Switching back to 「移动到格子」 dissolves every mapped widget and restores
  every hidden desktop item. Startup: installs that already have mapped widgets
  but no mode file adopt 「只映射」.

## Desktop visibility: per item (replaces hiding the whole list view)

* Only mapped entries are hidden, by setting `FILE_ATTRIBUTE_HIDDEN`.
  Unmapped and 「放回桌面」 entries stay visible as normal desktop icons.
* Ledger `data/mapped-organize.hidden` lists every path DeskBox hid; it is
  written *before* an attribute is set. Files that were already hidden are
  never recorded and never touched. Pure rules: `MappedOrganizeHiddenLedger`.
* Restore (`Reconcile` with nothing desired / member removed): entry leaves its
  widget, 「放回桌面」, 「退出映射整理」, switching to move mode, DeskBox exit
  (shutdown step + ProcessExit), next start after a crash (members re-hidden,
  stale records restored), and the uninstaller
  (`installer/DeskBox.ForkMappedOrganize.iss`, after the process is stopped).
* `MappedOrganizeHiddenRegistry` lets the widget folder loader
  (`FileService`: snapshot filter, `ClassifyExistingEntry`, `ShouldDisplayEntry`)
  keep listing DeskBox-hidden members; it is seeded lazily from the ledger.
* The legacy `desktopIconsHiddenByDeskBox` marker (whole list view hidden by
  older builds) is repaired once at startup.
* Settings warns when Explorer shows hidden items (HKCU …\Explorer\Advanced
  `Hidden`=1): mapped entries then still appear as faded icons.

## 「放回桌面」

The item menu row 「移回桌面」 reads 「放回桌面」 in mapped widgets and calls
`MappedOrganizeService.KeepOnDesktop` (single and multi-select): membership
removed, attribute restored, path recorded in `data/mapped-organize.kept` so
automatic sorting and 「一键整理」 skip it (follows renames, dropped on delete).
Dragging it into a mapped widget or confirming a preview that maps it clears
the mark. Entries deselected in a confirmed mapped preview are kept the same
way. 「退出映射整理」 clears the list.

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
  `FileSurfaceContent.SelectionAndMenus.cs` (blank menu call, item-menu
  localizer, 「放回桌面」 interception).
* `FileService.cs`: DeskBox-hidden members stay displayable (3 conditions).
* `DesktopAutoOrganizationWatcher.cs`: 只映射 hand-off (one block).
* `DesktopOrganizationSettingsSection.xaml`: one host panel for the mode card.
* `installer/DeskBox.Uninstall.iss`: include + one call.
* `Strings/*.json`: 6 keys inserted after `DesktopOrganization.Preview.Title`
  (zh-CN / zh-TW translated, other locales English fallback).

## Known gaps

* Manual mode: new desktop files stay unassigned (visible on the desktop)
  until 「一键整理」.
* Public Desktop entries need admin rights to change attributes; when that
  fails they stay visible on the desktop as well as in the widget.
* Hidden entries are also hidden in File Explorer's Desktop folder view.
* Preview "use existing widget" destination choices are ignored in mapped mode
  (grouping is per category).
* The auto/manual switch for mapped widgets stays in the mapped widget's
  blank-area menu.
* Not runtime-verified on a real desktop in this change (CI builds and runs
  unit tests only).
