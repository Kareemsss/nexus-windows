# NEXUS File Shelf: plan and build prompt

Status: planning. Nothing here has been built yet.

---

## Part 1: The plan

### What the feature is

Drag any file or folder onto the island and it stays there. Later, drag it back out into any app (an email, a chat, a folder, an upload box). It works like a temporary pocket that sits at the top of the screen, so you don't have to juggle two Explorer windows.

### How it behaves, step by step

1. **Pick up a file.** In Explorer, the desktop, or any app that drags real files, start dragging.
2. **Reach the island.** When the drag reaches the idle capsule (or the satellite), the island opens into a large drop zone labelled "Drop to keep here", showing the items already on the shelf. If you drag away without dropping, it closes again.
3. **Drop.** The files land on the shelf with a short edge pulse. The island goes back to whatever it was showing before. A satellite indicator shows the shelf count ("3 files").
4. **Use it later.** Hover or click the satellite, or open Island Center: the Shelf section lists every item with its icon or thumbnail and name.
5. **Drag out.** Drag one item, or "Drag all", into any app. By default an item leaves the shelf after a successful drop somewhere else (there's a setting to keep it).
6. **Other actions per item:** open, show in folder, copy path, remove. Plus "Clear shelf".

### Decisions made for v1 (change any of these before building)

| Decision | Choice | Why |
|---|---|---|
| Copy or reference? | **Reference** (store the path; never copy file bytes) | Instant, no disk use, no duplicate files. Moving or deleting the original is shown honestly as "Moved or deleted". |
| What can be shelved | Real files and folders only (`DataFormats.FileDrop`) | Browser images, Outlook attachments and text are "virtual" drags with no path. v1 rejects them with a clear message instead of half-supporting them. |
| Limit | 24 items, newest first; duplicates (same full path) are moved to the top, not added twice | Keeps the island readable. |
| Survive a restart | **Yes**, saved to `%LOCALAPPDATA%\Nexus\shelf.json` (paths only), with a toggle to keep it in memory only | Users expect a shelf to still be there tomorrow. The toggle respects NEXUS's privacy stance. |
| After drag-out | Item is removed when the target accepted it (Copy or Move); setting "Keep items after dragging them out" | Matches how shelf apps on macOS behave. A Move by the target always removes it, since the file has moved. |
| How the drop is caught | The idle capsule and satellite accept the drag; on DragEnter the island blooms into a large drop zone and the hit pad grows with it | No invisible hot strip across the top of the screen (that would steal clicks from browser tabs), no polling, no hooks. |
| Where it lives in the UI | A new Island Center section **Shelf**, a satellite indicator, and a new island mode **Drop** | Reuses the existing systems instead of inventing a new window. |
| Gaming / Quiet mode | The drop zone is a direct response to the user, so Quiet mode never blocks it. When the island is Hidden it can't be reached, except through Edge reveal when that's armed. | Same rule NEXUS already applies to Feedback activities. |

### Where it touches the code

| Layer | Change |
|---|---|
| `Nexus.Core` | `Shelf/ShelfModel.cs` (items, add/dedupe/cap/remove, pure and testable), `Shelf/ShelfStore.cs` (JSON load/save, corrupt-file backup like `SettingsStore`), `IslandMode.Drop` and `DragEnter/DragLeave/DragDrop` in `IslandStateMachine`, `CenterSection.Shelf`, `ShelfSettings` in `NexusSettings` |
| `Nexus.Platform` | Shell icon and thumbnail lookup for a path (reuse `Ui/AppIconCache.cs` patterns if they fit), `ShellService.CopyPath` |
| `Nexus.App` | `AllowDrop` plus drag events on the island root and satellite, a `DropZoneView`, a `ShelfSection` in `CenterView`, drag-out via `DragDrop.DoDragDrop`, shelf satellite indicator, Settings rows, a Preview lab entry |
| `tests` | Shelf model, store round-trip and corrupt file, state machine Drop transitions |

### Risks to verify on a real machine

1. **Drops on a layered window.** WPF registers an OLE drop target when `AllowDrop = true`. The hit pad's alpha 1 pixels should receive it, but this must be confirmed by dropping a file on the idle capsule.
2. **Admin apps.** Windows blocks drag-and-drop from an elevated app (an Explorer or IDE running as admin) to a normal one (UIPI). NEXUS can't fix this. It should show "Can't receive files from apps running as administrator" when it can detect it, and the README should say so.
3. **No focus.** The island never activates outside Island Center. Drag-out from Compact or Expanded has to work without activation.
4. **Canvas size.** The drop zone has to fit inside `IslandWindow.CanvasSize` (860 x 720).

---

## Part 2: The prompt

Copy everything inside the block below into a new session in this folder.

```text
You are working in the NEXUS repository: a Dynamic Island style activity surface for Windows, written in .NET 8 / WPF with no NuGet dependencies in the app (only the test project uses xUnit). Read README.md sections 6, 8, 9 and 10 first; they explain the architecture (Providers -> ActivityHub -> ActivityArbiter -> IslandStateMachine -> IslandController -> IslandPresenter -> widgets) and the house rules for providers and widgets.

GOAL
Add a File Shelf: the user drags files or folders onto the island, NEXUS keeps them, and the user drags them back out into any app later. Build it directly in source. Do not create PowerShell or script "patch" files that rewrite source with string replacement (the repo root has old apply-*.ps1 scripts; do not add more and do not use them).

USER STORY
1. The user drags real files (DataFormats.FileDrop) from Explorer or the desktop toward the island.
2. When the drag enters the idle capsule, Peek, Compact view or the satellite, the island springs open into a drop zone: a large target that reads "Drop to keep here" and shows the current shelf items as a strip of icons. The hit pad grows to match so the whole visible zone accepts the drop.
3. Dragging away without dropping (DragLeave, or Escape cancelling the drag) closes the drop zone and returns to the previous state.
4. On drop: items are added (newest first), an edge pulse in the theme accent confirms it, the island returns to its previous state, and a satellite indicator shows the count ("1 file", "3 files", "2 items" when folders are mixed in).
5. Island Center gets a new section "Shelf" listing each item: icon or thumbnail, file name (ellipsized in the middle so the extension stays visible), and the parent folder in secondary text. Per item: Open (click or Enter), Show in folder, Copy path, Remove. Section actions: Drag all, Clear shelf.
6. Drag out: pressing on an item and moving starts DragDrop.DoDragDrop with a FileDrop DataObject and allowed effects Copy | Move | Link. If the target performs Move, remove the item. If it performs Copy or Link, remove it unless the setting "Keep items after dragging them out" is on. If the drag is cancelled, keep it. "Drag all" drags every item that still exists.

V1 DECISIONS (already agreed, do not change without asking)
- Store references (full paths), never copy file bytes.
- Accept only DataFormats.FileDrop. Any other drag (browser images, Outlook attachments, text, URLs) shows DragDropEffects.None, and the drop zone says "Only files and folders can be shelved" in place of the drop prompt.
- Maximum 24 items. Adding a path already on the shelf (case-insensitive full path) moves it to the top instead of duplicating it. When the cap is exceeded the oldest items are dropped, and the drop zone says so ("Shelf full: oldest items removed").
- Persist to %LOCALAPPDATA%\Nexus\shelf.json (paths and added-at time only). A setting "Remember shelf after restart" (default on) switches to in-memory only, and turning it off deletes shelf.json. Load must survive a corrupt file the same way SettingsStore does (back it up as shelf.json.corrupt, start empty, log a warning). Never log file names or paths; log counts only, consistent with README section 5.
- Missing items: check File.Exists / Directory.Exists when the shelf is shown. Missing items render dimmed with "Moved or deleted" and only offer Remove. No FileSystemWatcher.
- No polling, no keyboard hooks, no new permissions, no invisible hot strip across the screen.

ARCHITECTURE: WHERE EACH PIECE GOES
Nexus.Core (pure, no WPF, unit-tested):
- Shelf/ShelfItem.cs: record ShelfItem(string Path, bool IsFolder, DateTimeOffset AddedAt).
- Shelf/ShelfModel.cs: Add(IEnumerable<(string path, bool isFolder)>, DateTimeOffset now) returning an AddResult (added count, moved-to-top count, evicted count), Remove(path), Clear(), Items, Changed event. Thread-safe enough for UI-thread use; time is passed in, never read (same style as IslandStateMachine).
- Shelf/ShelfStore.cs: Load/Save against a path supplied by the caller, atomic write (write temp then File.Replace or Move), corrupt-file handling as above.
- Settings: add ShelfSettings { bool Enabled = true; bool Remember = true; bool KeepAfterDragOut = false; } to NexusSettings, include it in Normalize(), and keep SchemaVersion handling backward compatible (an old settings.json without the section loads with defaults).
- CenterSettings: add CenterSection.Shelf. Put it in DefaultOrder right after NowPlaying; the existing Normalize() slots it in for older settings files. Respect HideEmptyCards for an empty shelf.
- State: add IslandMode.Drop and methods DragEnter(now), DragLeave(now), Dropped(now) on IslandStateMachine. Drop mode wins over Idle, Peek, Compact and Expanded, but never over Hidden, and it does not cancel Island Center (if Center is open, the drop goes straight into the Shelf section and Center stays open). Leaving or dropping restores the previous intent. Add a safety timeout: if no DragLeave or drop arrives within 10 seconds of the last DragOver, return to the previous mode (OLE can lose DragLeave when the source process dies). Expose the deadline through the existing Deadline property so the controller's timer handles it; no new timer.

Nexus.Platform:
- A small ShellIcons service that returns a frozen ImageSource-ready bitmap for a path: thumbnail for images and videos via IShellItemImageFactory (SIIGBF_THUMBNAILONLY falling back to SIIGBF_ICONONLY), shell icon otherwise. Cache by extension for non-thumbnail types, by path for thumbnails, with a small LRU cap (for example 64 entries). Look at src/Nexus.App/Ui/AppIconCache.cs first and reuse its approach if it fits. Run lookups off the UI thread and marshal the result back.
- ShellService: add CopyPath(string path) that puts the path on the clipboard (with retry, since the clipboard can be locked) and reuse the existing Open and ShowInFolder.

Nexus.App:
- IslandWindow / IslandPresenter: set AllowDrop = true on the island root (the element that already raises PointerEntered) and on SatelliteFrame. Handle DragEnter, DragOver, DragLeave and Drop. Set e.Effects to Copy for FileDrop and None otherwise, mark e.Handled. While in Drop mode, size the HitPad to the full drop-zone bounds so the zone is reliably hit-testable (the alpha 1 HitPad pattern already exists at IslandPresenter.cs around line 372).
- IslandController: wire the drag events into the state machine with Input(m => m.DragEnter(Now)) and the rest. On drop, read FileDrop paths, call ShelfModel.Add, save via ShelfStore if Remember is on, pulse, and post or refresh the shelf indicator.
- Shelf indicator: a dormant activity (IsDormant = true, CanBeForeground = false) from a ShelfProvider registered in NexusHost.RegisterProviders, so it appears in Settings > Modules and can be switched off there (switching it off hides the satellite indicator, the Center section and disables AllowDrop). Its IndicatorSpec uses a folder or document glyph from Core/Activities/Glyphs.cs (add one there if missing, Segoe Fluent Icons code point, no emoji) and the label "N files".
- DropZoneView: a new view shown for IslandMode.Drop, built with Kit, Palette, NexusButton and the existing spring presets so every theme and Reduce motion work automatically. It shows the prompt text, up to 8 item icons (then "+N"), and the error line when the drag is not acceptable. Keep its height fixed and its width within IslandWindow.CanvasSize.
- CenterView: a ShelfSection matching the other sections' structure and empty state ("Drag files onto the island to keep them here"). Item rows are real focusable controls: Tab / Shift+Tab reach every row and button, Enter opens, Delete removes, the context menu key or Shift+F10 opens the per-item menu, and every icon-only button has an AutomationProperties.Name. Visible focus indicator consistent with the rest of Island Center.
- Drag out: start DoDragDrop only after the pointer moves beyond SystemParameters.MinimumHorizontalDragDistance / MinimumVerticalDragDistance from the press point. Do not activate the window to drag. While dragging out, the island must not collapse under the pointer (treat it as hovered until DoDragDrop returns).
- Settings window: add rows on the most fitting existing page (Behavior, or Modules next to the shelf module): Remember shelf after restart, Keep items after dragging them out, and a Clear shelf button. The Center settings page must list the new Shelf section for show/hide and reordering automatically through CenterSection.
- Preview lab (Composition/PreviewEvents.cs): add "Shelf: show drop zone" and "Shelf: add sample items". The sample uses paths that exist on every machine (for example C:\Windows\win.ini and the user's Downloads folder via KnownFolders) and does not touch the user's real shelf.json.

STATES THAT MUST EXIST (every one visible and tested by hand)
- Shelf empty (Center section empty state; no satellite indicator).
- Drop zone: acceptable drag, unacceptable drag, shelf full.
- Item missing ("Moved or deleted").
- Drop failed (exception while reading the data object): the zone shows "Couldn't add these files" for 2 seconds, then closes; the error is logged without paths.
- Elevated source: if the drop never arrives because of UIPI, NEXUS can't detect it at runtime. Document it in README section 7 (Known limitations) instead: "Files can't be dragged from apps running as administrator onto NEXUS."

DESIGN RULES
- Use the existing visual system only: Palette, Kit typography (Segoe UI Variable), Segoe Fluent Icons, existing spring presets in Core/Motion/Spring.cs. Do not add new gradients, glows, badges, emoji or decorative shapes; the drop zone is a state of the same glass, not a new style.
- One focal point in the drop zone: the prompt text. Item icons are secondary.
- Copy is plain and specific. No em dash characters anywhere in UI strings. Labels: "Drop to keep here", "Drag all", "Clear shelf", "Show in folder", "Copy path", "Remove", "Moved or deleted".
- All text meets WCAG AA contrast against every theme's glass (4.5:1 normal text, 3:1 large). Check Minimal and Fluent, which are the lightest.
- Respect Reduce motion: the drop zone appears without the bloom overshoot, with an opacity change only.

TESTS (tests/Nexus.Core.Tests, xUnit)
- ShelfModel: add, dedupe moves to top, cap evicts oldest and reports it, remove, clear, folder flag kept.
- ShelfStore: round trip, missing file loads empty, corrupt file is backed up and loads empty, atomic save leaves no partial file.
- IslandStateMachine: Idle -> Drop -> Idle on leave; Compact -> Drop -> Compact on drop; Center stays Center on DragEnter; Hidden ignores DragEnter; the 10 second safety timeout restores the previous mode via Tick.
- CenterSettings.Normalize inserts Shelf after NowPlaying for an old file that lacks it.

DEFINITION OF DONE
1. dotnet build Nexus.sln -c Debug and -c Release succeed with no new warnings.
2. dotnet test tests/Nexus.Core.Tests passes, including the new tests.
3. Run the app (dotnet run --project src/Nexus.App) and verify by hand, writing the result of each check in your final report:
   - drop one file on the idle capsule; drop several files and a folder; drop on the satellite; drop while Island Center is open
   - drag a browser image (rejected with the message); drag away without dropping (zone closes)
   - drag an item out to the desktop (copy: item removed by default), out to another folder with Shift held (move: removed), cancel a drag-out with Escape (kept)
   - rename an item's original in Explorer, reopen the shelf (shows "Moved or deleted")
   - restart NEXUS with Remember on (items return) and off (shelf empty, shelf.json deleted)
   - keyboard only: Tab through the Shelf section, Enter opens, Delete removes
   - every theme, plus Reduce motion, plus island scale 0.8 and 1.5
   - Settings > Modules: turning the shelf module off removes the indicator, the section and drop acceptance
   - check %LOCALAPPDATA%\Nexus\logs\nexus.log contains no file names or paths
4. README: add File Shelf to section 6 (features) and the admin-app limitation to section 7.
5. If anything in the checklist could not be verified (for example no second app to drag into), say so plainly instead of claiming it passed.
```
