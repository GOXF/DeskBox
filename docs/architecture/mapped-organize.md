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

### Restore before every exit (fix/mapped-hidden-restore)

Goal: whenever DeskBox is not running, no desktop entry stays hidden by it.
`RestoreIconsForExit(reason, sessionEnd)` is synchronous and idempotent
(`MappedOrganizeExitGate`); log line `[MappedOrganize] Restored desktop items for <reason>`.

| Exit path | What runs |
| --- | --- |
| Tray exit, 重启, in-app update (`ShutdownForUpdate/RestartAsync`), dev「更新并重启」 | first line of `ShutdownApplicationAsync` (before any bounded teardown step can time out), again in the shutdown step |
| Windows shutdown / restart / sign-out, installer Restart Manager | `WM_QUERYENDSESSION` / `WM_ENDSESSION` on the tray window → `FlushSettingsForEndSession` → restore; a cancelled session end (`WM_ENDSESSION` FALSE) re-hides (`ForkSessionEndSignal`) |
| `Environment.Exit` / fatal startup | `ProcessExit` |
| Crash on a background thread | `AppDomain.UnhandledException` (terminating) — best effort |
| Hard kill (Task Manager, `Stop-Process -Force`) | cannot run; `fork-dev-update.ps1` restores from the ledger after its own force-kill; next start: `MappedOrganizeStartupRecovery` (startup step `mapped-organize-recovery`, before widget restore) shows every ledger entry, logs it, then the service hides the members again; uninstaller as before |

The desktop organizer (`DesktopOrganizationScanner`) reads attributes through
`MappedOrganizeHiddenRegistry.EffectiveAttributes`, so a ledger entry is a
normal item, never 「本次无法整理」. Older builds (no ledger support) cannot do
this — they are why the post-restart desktop showed 0 items.

### Hidden attribute never leaves the desktop

When an entry is hidden its NTFS id (volume serial + file index) is captured.
A ledger entry the desktop watcher reports as deleted is opened by id
(`ForkFileIdentityNative`, `OpenFileById` + `GetFinalPathNameByHandle`): if it
now lives outside the desktop (another folder, cut/paste, dragged out of a
mapped widget, Recycle Bin) its hidden attribute is cleared there and it leaves
the ledger (`MappedOrganizeLeakPolicy`). Not covered: a *copy* of a hidden
entry (copies keep attributes) and a move to another volume (copy + delete).

### Research: hiding icons without file attributes

* 360 桌面助手 / Fences: keep the desktop SysListView32 but position icons
  under their own fence windows (Fences moves icon positions via
  LVM_SETITEMPOSITION and draws its own frame; "quick-hide" hides the whole
  list view). WPS 桌面整理 hides the whole icon layer (SysListView32
  ShowWindow / Explorer "show desktop icons") and draws every item, mapped or
  not, in its own windows. None of them touch file attributes.
* This codebase already has the whole-layer approach: `DesktopIconVisibilityService`
  (SW_HIDE of the desktop SysListView32, not persisted by Explorer) behind the
  legacy `desktopIconsHiddenByDeskBox` marker, from the first mapped build.
  A "hide icon layer + show unmapped items in a DeskBox desktop surface" mode
  is feasible: needs a full-screen-per-monitor desktop-layer widget that lists
  unmapped desktop items with free positioning (or a grid), Explorer-restart
  re-apply (periodic check exists), desktop drop target / context menu / rename
  / keyboard parity, and blank-area double-click behaviour. Rough effort: 5–8
  focused dev days for a usable version, more for full Explorer parity
  (icon positions, multi-monitor layouts, F2/Del/Ctrl+A on the desktop).

## 「放回桌面」

The item menu row 「移回桌面」 reads 「放回桌面」 in mapped widgets and calls
`MappedOrganizeService.KeepOnDesktop` (single and multi-select): membership
removed, attribute restored, path recorded in `data/mapped-organize.kept` so
automatic sorting of new files and the mapped widget's quick 「一键整理」 skip
it (follows renames, dropped on delete).
Dragging it into a mapped widget or confirming a preview that maps it clears
the mark. Entries deselected in a confirmed mapped preview are kept the same
way. 「退出映射整理」 clears the list.

### 重新整理桌面

「放回桌面」 entries are re-sorted by the organize preview, not put back where
they came from: in 「只映射」 mode the preview lists them like any other
desktop entry (sorted into their category by the normal rules, ticked by
default — including folders / large files, which the scanner otherwise only
offers as opt-in: `IncludeKeptOnDesktopItems`, one call in
`DesktopOrganizationTaskView.ScanAsync`, pure rule
`MappedOrganizeReorganizePolicy.SelectKeptOptIns`, once per preview so a
refresh does not re-tick what the user unticked). Confirming maps them and
clears the mark (`ApplyGroupsAsync`); unticked entries stay kept. Entry
points: 「整理预览…」 in a mapped widget's blank-area menu, in the
title-bar 「…」 menu of a mapped widget or of a tab group holding one
(`ContentWidgetWindow.MappedOrganize.cs`, one call in `CreateMoreFlyout`;
`MappedOrganizeReorganizePolicy.ShouldOfferReorganize`) and in the tray menu.
The one-click organize below includes 「放回桌面」 entries without a preview.

### 一键整理桌面 (one click, replaces opening the preview)

「整理桌面」 finishes in one click; the preview with its 待整理 / 留在桌面 split
is no longer the default. Entries that now run `ForkOneClickOrganizer`
(`Services/ForkOneClickOrganize.cs`, host glue `App.ForkOneClickOrganize.cs`):
the tray 「整理桌面…」 (`OpenDesktopOrganizationFromTray`), the settings
buttons 「一键整理桌面」 (桌面整理) and 「整理桌面」 (文件格子) through
`InProcessSettingsAppActions.ShowDesktopOrganizationWindow` (IPC unchanged),
the mapped widget's blank-area 「一键整理」 and the title-bar 「…」
「一键整理桌面」 (formerly 「重新整理桌面」).

* 移动到格子: the preview's untouched default plan is executed
  (`BuildPlanAsync` → `CreateExecutionPlan(plan, [])` → `ExecuteAsync`), so
  history, 撤销 and recovery are the same; opt-in kinds (folders, large files)
  stay on the desktop as in the preview.
* 只映射: every 「放回桌面」 mark is cleared first
  (`ReleaseAllKeptOnDesktop`), the plan includes all opt-in kinds (nothing
  moves), entries go to the mapped widget of their own category when one
  exists, otherwise to the planner's bucket
  (`ForkOneClickOrganizePolicy.BuildMappedGroups`), then 「一键整理」 routing
  sweeps the rest.
* Result: a notification (with 撤销 when files were moved). A pending
  interrupted organize, an open preview or a host that is not ready opens the
  preview instead.
* The preview stays reachable as 「整理预览…」 (tray menu, mapped widget
  blank-area menu, title-bar 「…」 menu).
* Automatic sorting of new files still skips 「放回桌面」 entries.

### Same category → same widget (duplicate 「其他」 fix)

Cause: a widget created by an earlier organize gets a rule for the
categories it received (e.g. 「其他」 = [Other]). A category with a single
item (e.g. one video, Media) is merged into the 「其他」 bucket by
`MergeSmallAndOverflowCategories`; that item matches no rule, so the planner
proposed a second, new 「其他」. `DesktopOrganizationCategoryReuse` (one call in
`DesktopOrganizationPlanner.CreatePlan`) now lets a bucket join the widget of
an enabled rule listing its category (unless the rule excludes one of the
bucket's extensions), else a widget whose name equals the bucket's name.
Mapped groups likewise join an existing mapped widget by category, else by
name (`ForkOneClickOrganizePolicy.FindMappedTarget` in `ApplyGroupsAsync`).
Limit: in 只映射 mode a real-folder (moved-files) widget named 「其他」 can't
receive mapped entries, so it still sits next to the mapped 「其他」.

### 拖到桌面 = 放回桌面

Dragging items out of a mapped widget and releasing them on the system desktop
(any monitor) runs the same `KeepOnDesktop`. Explorer sees that drop as a
same-folder move (a no-op), so DeskBox decides it at drag completion
(`FileSurfaceContent.MappedDragToDesktop.cs`, hooked once in
`CompleteDragItemsSession`): the window under the cursor must be the shell
desktop (`SHELLDLL_DefView`/`SysListView32` under `Progman`/`WorkerW`, shell
process, not a DeskBox window — `MappedDragToDesktopPolicy.ClassifyTarget`),
the point must be blank desktop (upstream `DesktopBlankHitTest`; a drop on a
folder / Recycle Bin icon stays a real move), no mouse button / Esc held, and
DeskBox must not have handled the drop itself. The put-back entries are then
moved to the release point through the desktop's `IFolderView`
(`SelectAndPositionItems`, `DesktopIconPlacementNativeMethods`), first icon
centred on the cursor, the rest in a column; skipped when 自动排列图标 is on.
Drops on other folders / widgets and move-mode widgets keep upstream behaviour.

Robustness (fix/mapped-drag-and-reorganize): the gesture is recorded at
`Items_DragStarting` (`BeginMappedDragTracking`, one call): mapped or not,
its paths, and a background `MappedDragReleaseTracker` that polls the
physical buttons (`GetAsyncKeyState`, 8 ms) and probes the desktop **at the
instant of release** (pure state machine `MappedDragReleaseTracking`). The
completion-time probe was unreliable: by then Explorer has processed the
drop, its desktop thread can be busy (the upstream LVM_HITTEST then times out
and "failed" was read as "on an icon"), and with 「显示隐藏的项目」 on, the
dragged item's own faded icon can sit under the cursor. The probe is now
three-state (`DesktopListViewProbeNativeMethods`: blank / icon + label /
failed); an icon only blocks when its label is not one of the dragged
entries, a failed hit test does not block (put-back paths are re-checked to
still exist). The decision runs once per gesture: from drag completion, or
from the release when WinUI reports completion before the release (stack
popover closing) or never (1.5 s grace). Every refusal logs
`[MappedOrganize] DragToDesktop skip reason=<reason> … target= icon= chain=`;
success logs `DragToDesktop put back`, the release `DragToDesktop released`,
the start `DragToDesktop tracking`.

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
* `FileSurfaceContent.xaml.cs`: one `TryPutBackMappedDragOnDesktop` call in
  `CompleteDragItemsSession` and one `BeginMappedDragTracking` call in
  `Items_DragStarting` (拖到桌面 = 放回桌面).
* `ContentWidgetWindow.Commands.cs`: one `AppendMappedOrganizeMoreMenuItems`
  call in `CreateMoreFlyout`; `DesktopOrganizationTaskView.xaml.cs`: one
  `IncludeKeptOnDesktopItems` call in `ScanAsync` (重新整理桌面).
* `App.Tray.cs`: `OpenDesktopOrganizationFromTray` runs the one-click organize,
  one `CreateForkOrganizePreviewTrayItem` line (整理预览…).
* `Services/InProcessSettingsAppActions.cs`: `ShowDesktopOrganizationWindow`
  → `OrganizeDesktopOneClick` (one click from the settings buttons).
* `DesktopOrganizationPlanner.cs`: one `DesktopOrganizationCategoryReuse` block
  in `CreatePlan` (no duplicate same-category widget).
* `FileService.cs`: DeskBox-hidden members stay displayable (3 conditions).
* `DesktopAutoOrganizationWatcher.cs`: 只映射 hand-off (one block).
* `DesktopOrganizationSettingsSection.xaml`: one host panel for the mode card.
* `installer/DeskBox.Uninstall.iss`: include + one call.
* Exit safety: `App.xaml.cs` 3 lines (startup recovery step, restore at top of
  `ShutdownApplicationAsync` and `FlushSettingsForEndSession`),
  `AppLifecycleRecoveryWatcher.cs` (ENDSESSION FALSE signal),
  `DesktopOrganizationScanner.cs` (effective attributes).
* `Strings/*.json`: 6 keys inserted after `DesktopOrganization.Preview.Title`
  (zh-CN / zh-TW translated, other locales English fallback).

## Known gaps

* Manual mode: new desktop files stay unassigned (visible on the desktop)
  until 「一键整理」.
* The tray item keeps the upstream text 「整理桌面…」 (upstream string; its
  ellipsis is now slightly misleading).
* Public Desktop entries need admin rights to change attributes; when that
  fails they stay visible on the desktop as well as in the widget.
* Hidden entries are also hidden in File Explorer's Desktop folder view.
* Preview "use existing widget" destination choices are ignored in mapped mode
  (grouping is per category).
* The auto/manual switch for mapped widgets stays in the mapped widget's
  blank-area menu.
* Copying a mapped item to another folder, or moving it to another volume,
  keeps the Hidden attribute on the copy (moves on the same volume are fixed,
  see 「Hidden attribute never leaves the desktop」).
* 拖到桌面 = 放回桌面: a drop onto another desktop icon is not treated as
  put-back (one whose label equals a dragged entry's name is); icon
  placement needs 自动排列图标 off (网格对齐 still snaps it). Touch / pen
  drags have no button to track and fall back to the completion-time probe.
* Not runtime-verified on a real desktop in this change (CI builds and runs
  unit tests only).
