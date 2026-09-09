# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Keeping this file up to date (a rule for Claude)

**Claude keeps CLAUDE.md up to date on its own — without a separate request from the user.**
The file is edited in the same session in which the code changed, before reporting the finished work.

Must be updated whenever:

| Change in the code | What to fix here |
| --- | --- |
| A ribbon button is added, removed or renamed | "Buttons and commands", and "How a command is built" if needed |
| A ribbon panel is added or renamed | "What this is", "Buttons and commands" |
| A new class appears in `Infrastructure/` or a new shared abstraction | "Code map", "Key decisions" |
| The format or path of a file under `%AppData%\VladTools\` changes (`formulas.txt`, `names.txt`, `dimensions\`, `links\`, `basefile\`) | "Storing user settings" |
| `build.ps1`, `VladTools.csproj`, `VladTools.addin`, the Revit version or the TFM changes | "Build before reporting", "Build and run", "Porting to another Revit version" |
| A test project or a linter appears | "Build and run" — add the commands to run them |
| Any of the "Conventions" is broken or deliberately changed | "Conventions" — rewrite the rule, don't leave the discrepancy standing |

Editing rules: replace what is outdated, don't append next to it; don't duplicate README.md — this
file holds architecture and conventions, README.md holds the user-facing description of the buttons
(keep that in sync too whenever button behaviour changes); don't describe what is already visible
from the folder structure.

## Build before reporting (a rule for Claude)

**Any code change ends with running `.\build.ps1` — with no flags, that is, for every year at once
and with install.** Not `dotnet build` for a single year, and not `-NoDeploy`: both check less than
is needed, and reporting "done" after either means "it compiled on my machine", not "it is
installed and ready to check".

Why exactly this way:

- **The years genuinely differ.** 2025 has a different target framework (`net8.0`), a different
  reference set and an extra file on install (`VladTools.deps.json`) — see "Key decisions", TFM by
  year. An edit that built fine under 2022 has broken under 2025 before, and the other way round too.
- **The button still has to be checked in Revit**, and the user will not do that until the DLL is
  actually sitting in `%AppData%\Autodesk\Revit\<year>\VladTools\`. Leaving the build unstalled
  means handing the user an extra step and pretending the work is done.
- **The copy step runs with `ContinueOnError="true"`.** With Revit running the build still
  "succeeds", but the installed DLL stays the old one — and the user ends up testing the previous
  version. So: check `Get-Process Revit` before building; if it is running, say so and ask for it
  to be closed rather than building silently. After building, confirm `VladTools.dll` in every
  built year's folder has a fresh timestamp, and only then report completion.

Name the years that were built for in the completion report, and separately, any new warnings.
Warnings already known and deliberately left in place (`CS0618` on `ElementId.IntegerValue`,
`SYSLIB0014` on `WebRequest.Create` — see "Porting to another Revit version") do not count as new
and are left out of the report; everything else goes in.

## What this is

An add-in for **Autodesk Revit 2022, 2024 and 2025**, written in C#, x64. It adds a **Vlad Tools**
tab to the Revit ribbon, with two panels: "Families" — four buttons, working **only in the family
editor** (`.rfa`); "Project" — six buttons, working **only in a project** (`.rvt`). There is no test
project — checking is done by hand, in Revit.

## Build and run

```powershell
.\build.ps1                          # Release + install for every installed year at once (2022, 2024, 2025)
.\build.ps1 -Configuration Debug     # Debug + install, every year too
.\build.ps1 -NoDeploy                # build only, no install
.\build.ps1 -RevitVersion 2025       # Release + install for one year only
dotnet build src\VladTools\VladTools.csproj -c Release -p:RevitVersion=2024 -p:DeployToRevit=false   # the same directly, one year
```

- Requires the .NET SDK and an installed copy of Revit — the version is set by `RevitVersion`
  (defaulting to `2022`, from [Directory.Build.props](src/VladTools/Directory.Build.props);
  overridden by the `-p:RevitVersion=2024` command-line switch, which wins over the default). It
  drives `RevitDir` (`RevitAPI.dll` / `RevitAPIUI.dll` via `HintPath`, no NuGet packages),
  `RevitAddinsDir`, `Product` and the **target framework** in
  [VladTools.csproj](src/VladTools/VladTools.csproj) — see "Key decisions", TFM by year.
- `build.ps1` with no arguments runs **all three years in one invocation** (2022, 2024, 2025),
  skipping whichever is not installed on the machine (its `RevitAPI.dll` via `HintPath` will not be
  found). `-RevitVersion` takes one year or a list; it fails on the first build that does not
  succeed, rather than silently building "whatever came out".
- Every year gets its own output folder, `bin\R2022\<Configuration>\` / `bin\R2024\<Configuration>\`
  / `bin\R2025\<Configuration>\` (and the same for `obj\`). This split is not done by the csproj
  itself but by `Directory.Build.props`: `BaseOutputPath` / `BaseIntermediateOutputPath` have to be
  known to MSBuild before the SDK import, otherwise NuGet picks an old path for its own restore
  files before the property takes effect (warning MSB3539). The same file also extends
  `DefaultItemExcludes` to cover `bin\**;obj\**` as a whole, not just "its own" year's folder:
  without that, building one year while an `obj\` from a previous build of a different year is
  already on disk picks up its generated `AssemblyInfo.cs` through the ordinary `**/*.cs` glob as a
  second source file and fails with `CS0579` ("duplicate attribute") — the SDK only excludes
  *this* build's own BaseOutputPath/BaseIntermediateOutputPath from the default globs, it knows
  nothing about a neighbouring year. Reproduced and confirmed while adding 2024 support; the same
  guard works for 2025 without any changes.
- Installing = the `DeployToRevitAddins` MSBuild target in the csproj: it copies `VladTools.dll`
  (+ `.pdb`, and on 2025 also `.deps.json` — see TFM by year) into
  `%AppData%\Autodesk\Revit\Addins\<year>\VladTools\`, and `VladTools.addin` from the repository
  root right next to it.
- **Revit holds `VladTools.dll` locked.** With Revit open, the copy will not go through
  (`build.ps1` warns about this; the copy steps run with `ContinueOnError="true"`, so the build
  "succeeds" but the installed DLL stays the old one). Close Revit, build, start it again — the
  add-in only loads at startup.
- Verifying a change = `.\build.ps1` (every year, with install — see "Build before reporting"),
  launch the relevant Revit, open an `.rfa` or `.rvt` matching the button's panel, and press it.
  Building for a single year is only for a quick intermediate compile check — it does not count as
  a completed piece of work.

## Code map

```
App.cs                            IExternalApplication — the sole point where buttons are registered
Commands/*.cs                     one IExternalCommand per button: all the Revit API work
UI/*Window.cs                     WPF windows, built in code — they know nothing about the Revit API
UI/DimensionChainKind.cs          the catalogue of auto-dimension chain kinds (Overall/Openings/Grids/Partitions/…) + captions
UI/DimensionChainRow.cs           a row of the chain table in the "Auto Dimensions" window (INotifyPropertyChanged)
UI/DimensionTypeInfo.cs           a snapshot of a dimension type for this window: Id (long) + name
UI/FormulaRule.cs                 a row of the formula table (INotifyPropertyChanged)
UI/SharedParameterRow.cs          a row of the family parameter table + a reference to the FamilyParameter
UI/ProjectParameterRow.cs         a row of the project shared parameter table: element Id + its binding
UI/FamilyParameterInfo.cs         a snapshot of a family parameter for the formula window
UI/NestedFamilyRow.cs             a row of the nested-item table: element, current and new name, validation
UI/FamilyDimensionScan.cs         the result of scanning families for dimension labels: GUID + counts + failures
UI/CleanupTarget.cs               the list of what "Cleanup" is able to remove; the order of values is the order of the rows in the window
UI/CleanupOption.cs               an item of the cleanup window: check box, count found, and its full text
UI/LinkRow.cs                     a row of the link table: LinkEntry + check box + link set + the existing link's Id, if any
UI/WorksetRow.cs                  a row of the workset list: name, check box, how many links it occurs in
UI/LinkWorksetScan.cs             the result of reading worksets from links: names + a count + failures
UI/BrowseNode.cs                  a node of the browse tree (folder/model), shared by Revit Server and BIM360
UI/ModelBrowserWindow.cs          the tree itself with check boxes; what fills its children is given as a lambda
UI/CloudLinkWindow.cs             entering cloud models as pairs of GUIDs — the fallback path to BIM360
UI/ModelPicker.cs                 picking a model from all four sources; shared by "Link Manager" and "Base File"
UI/ModelKitRow.cs                 a row of the "Building Kit" table: discipline, model, where it was found
UI/ModelKitWindow.cs              the "Building Kit" window: building, disciplines, where to search, the found-models table
UI/GridBuilder.cs                 shared table column assemblies: a text column and a check box column
UI/BaseFileWindow.cs              the "Base File" window: one model and a list of what to do with it
UI/CoordinationChangeKind.cs      the kinds of discrepancy against the coordination file (Position/Name/Missing/New/…) + captions
UI/CoordinationChangeRow.cs       a row of the "Accept Changes" table + the ready-made edit
UI/CoordinationScan.cs            the result of comparing against one link: rows + how many elements monitor it
UI/AcceptCoordinationWindow.cs    the "Accept Changes" window: choosing a link, the discrepancy table
Infrastructure/Ribbon.cs          GetOrCreatePanel + AddPushButton
Infrastructure/Icons.cs           loading PNGs from an EmbeddedResource
Infrastructure/FormulaLibrary.cs  reading/writing %AppData%\VladTools\formulas.txt
Infrastructure/FormulaParser.cs   which names in a formula do not match a family parameter
Infrastructure/NameBuffer.cs      reading/writing %AppData%\VladTools\names.txt
Infrastructure/WarningSuppressor.cs  suppresses Revit warnings on Commit and gathers their text for the report
Infrastructure/DimensionLabelCache.cs  the saved family scan for dimension labels (one file per project)
Infrastructure/JsonHttp.cs        our own JSON parsing + a GET with headers (no third-party assemblies)
Infrastructure/AutodeskSession.cs the Autodesk token of the user signed in to Revit — via reflection over SSONET.dll
Infrastructure/AccClient.cs       the BIM360/ACC tree via the Data Management API: hubs → projects → folders → models
Infrastructure/RevitServerClient.cs  Revit Server folders and models through its REST service
Infrastructure/LinkCatalog.cs     the links and worksets of the open project; shared by both link commands
                                  plus HostModel — where the open project itself lives and what it is called
Infrastructure/ModelStore.cs      a model folder across the three stores: what is inside, what is above
Infrastructure/ModelKit.cs        guessing a link kit by building number and discipline folders
Infrastructure/LinkSetLibrary.cs  saved link sets, %AppData%\VladTools\links\<name>.txt
Infrastructure/LinkPreferences.cs the "Link Manager" window settings (%AppData%\VladTools\links\_settings.txt)
Infrastructure/BaseFilePreferences.cs  the "Base File" window settings (%AppData%\VladTools\basefile\_settings.txt)
Infrastructure/BaseFileFinder.cs  guessing the base file from the building in the open model's name
Infrastructure/CoordinationCatalog.cs  comparing the project's grids and levels against the coordination file
Infrastructure/DatumUpdate.cs     a ready-made edit for a single grid or level: rotation, translation, elevation, name
Infrastructure/DisciplineCatalog.cs  a discipline code (OV, VK…) from a model name → a project workset "01_Link_OV", by tokens
Infrastructure/RoomSide.cs        one straight side of a room boundary: direction, inward normal, walls
Infrastructure/RoomSideBuilder.cs a room's GetBoundarySegments → a list of RoomSide (merging collinear segments)
Infrastructure/DimensionReferenceCollector.cs  a side + a chain kind → a ReferenceArray for NewDimension, with a wall-face cache
Infrastructure/DimensionSampleReader.cs  sample dimensions → a chain template (a heuristic, like FormulaParser)
Infrastructure/DimensionTextLayout.cs    pulling short labels of a chain out onto a leader (also a heuristic: the API gives no text width)
Infrastructure/DimensionTemplate.cs      auto-dimension templates + %AppData%\VladTools\autodim\<name>.txt and _settings.txt
Infrastructure/AutoDimensionMarker.cs    ExtensibleStorage: the "this dimension was placed by the button" mark, against duplicates
Resources/*.png                   16/32 icons, embedded in the DLL
```

## Buttons and commands

| Panel | Button | Command | What it does |
| --- | --- | --- | --- |
| Families | "3D Thumbnail" | `Create3DThumbnailCommand` | creates/reconfigures a `View3D` named `3D Thumbnail` (annotations off, connectors hidden, `Realistic`, `Fine`) and opens it — Revit takes the active view as the family preview |
| Families | "Delete Parameters" | `DeleteSharedParametersCommand` | a window with every **shared** parameter of the family (dimension labels hidden by default); the checked ones are deleted via `FamilyManager.RemoveParameter` |
| Families | "Add Formulas" | `AddFormulasCommand` | a "parameter — formula" window from a saved list; the checked ones are assigned via `FamilyManager.SetFormula` |
| Families | "Rename Nested" | `RenameNestedFamiliesCommand` | a "find and replace" window over the names of nested `Family` and their `FamilySymbol`; the checked ones get `Element.Name` |
| Project | "Delete Shared Parameters" | `DeleteProjectParametersCommand` | a window with every `SharedParameterElement` of the project ("bound / unbound" filter + a button to scan families for dimension labels); the checked ones are removed via `doc.Delete`, taking the binding and the values with them |
| Project | "Cleanup" | `CleanupCommand` | a window with eight check boxes and "Select all": unused families, sheets, filters, views, legends, schedules, model groups (ungrouped), unused group types — everything checked runs in one transaction |
| Project | "Link Manager" | `LinkManagerCommand` | a window with a model list (files, Revit Server, BIM360, a saved set): placement and the worksets inside links — one for the whole batch, the project workset — one per row
(can be guessed from the discipline code in the model name). The "Building Kit…" button assembles the list itself: it finds the models of every discipline of the same building through the project's folders. New links are created in a single transaction and pinned; existing ones are reloaded with `LoadFrom` outside a transaction |
| Project | "Base File" | `BaseFileCommand` | links the coordination file (guessed on its own from the building number in the open model's name — a file, Revit Server, or BIM360) and immediately sets up the project against it: `Document.AcquireCoordinates`, the site name, a pin, switching to a workset (creating it if it does not exist); as its last step it opens "Copy/Monitor → Select Link" mode — the actual copy-monitoring is something the Revit API cannot do |
| Project | "Auto Dimensions" | `AutoDimensionCommand` | from a catalogue of chain kinds (overall / openings / opening centres / partitions / wall faces / all combined) places dimensions along the sides of the selected rooms; the set of chains is gathered with the "Take a sample…" button (parsing dimensions already placed by hand) or by hand, and saved as a template. Three check boxes apply to the whole set: capture the thickness of adjoining walls, pull small labels out onto a leader, remove what was placed before |
| Project | "Accept Changes" | `AcceptCoordinationCommand` | compares the grids and levels monitoring the coordination file against the file itself and applies the checked ones in a single transaction: a grid by rotating about its own midpoint plus a sideways shift, a level by a new elevation, plus renaming to follow the link. Missing and new link elements are only shown |

## How a command is built (a shared pattern — follow it in new ones)

Every command repeats the same order; there is no reason to deviate without one:

1. `[Transaction(TransactionMode.Manual)]` + `[Regeneration(RegenerationOption.Manual)]`.
2. No `ActiveUIDocument` → `Result.Cancelled`. Then a document-kind check: Families-panel commands
   need `doc.IsFamilyDocument`, the Project-panel command needs the opposite. Wrong document kind →
   a `TaskDialog` saying what to use instead, + `Result.Cancelled`.
3. Gather a **data snapshot** as plain DTOs (in a family — from `doc.FamilyManager`, in a project —
   a `FilteredElementCollector` plus a map from `doc.ParameterBindings`) and hand it to the window.
4. Show the window, always setting the owner:
   `new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;`
   `ShowDialog() != true` → `Result.Cancelled`. The window hands back the result through a
   `Selected` property.
5. **One transaction for the whole batch** (the user undoes all of it with one Ctrl+Z). Inside — a
   loop over the elements, each in its own `try`/`catch`: successes into `applied` / `deleted`,
   failures as a string into `failures`. An exception on one element does not stop the rest and
   does not fail the command. If an edit produces Revit warnings (in a project — almost always), a
   `WarningSuppressor` is put on the transaction, and the text it gathers goes into the report.
6. Nothing was done → `transaction.RollBack()`, otherwise `Commit()`.
7. A `TaskDialog` report with a list of successes and no more than 15 failures ("…and N more").
8. An outer `try`/`catch` → `message = exception.Message; return Result.Failed;`.

Register a new button in [App.cs](src/VladTools/App.cs) via `Ribbon.AddPushButton`, next to the
existing ones; a step-by-step guide with an example is in [README.md](README.md), under "How to
add a new button".

**Exception — `AutoDimensionCommand`.** Steps 1–2 and 5–8 are the same, but between showing the
window and the transaction there is a loop: the "Take a sample…" button in the window does not
read the selection itself (Revit will not let `Selection.PickObject` run while a modal window is
open) — the window closes with the `WantsSample` flag, the command does `PickObjects`, parses the
sample (`DimensionSampleReader`) and reopens the window with the same instance of the data, now
filled in. The loop ends when the user closes the window with "Place" or "Close".
The data snapshot (step 3) is not a single one either: a `FilteredElementCollector` over dimension
types plus a list of saved templates, rather than a pair of DTOs.

## Key decisions

- **The target framework depends on the Revit year.** Revit 2022 and 2024 are `.NET Framework
  4.8`, Revit 2025 is `.NET 8` (Revit 2025's `RevitAPI.runtimeconfig.json` says `tfm net8.0` +
  `Microsoft.WindowsDesktop.App 8.0`; a net48 build will not load in it at all). So in
  `VladTools.csproj` the `TargetFramework` is chosen by a condition on `RevitVersion`
  (`net8.0-windows` when `>= 2025`, otherwise `net48`), rather than a single fixed string — the
  comparison is numeric, MSBuild converts both sides to a number itself. This is still **one build
  key**, not a project fork and not a (multiple) `TargetFrameworks`: the latter would mean two
  references to different `RevitAPI.dll` copies in one build. This is also why `UseWPF=true` is
  used on net8.0-windows instead of explicit references to `PresentationCore`/`PresentationFramework`/
  `WindowsBase`/`System.Xaml` (those only matter for net48 — on net8 such references do not resolve
  by name) and `MSBuildWarningsAsMessages=MSB3277`: `RevitAPI.dll` drags in about fifty neighbouring
  Revit DLLs with their own versions of `System.Drawing` and the like, and without this line the
  real warnings are lost behind the MSB3277 wall. `AppendTargetFrameworkToOutputPath=false` matters
  even more here: without it 2025's output would land in `bin\R2025\Release\net8.0-windows\`, and
  the install target would miss the file. On 2025, `VladTools.deps.json` appears next to the DLL
  (net48 has none) — `DeployToRevitAddins` copies it conditionally, via `Exists(...)`. `#if` turned
  out not to be needed: for every API difference between 2022/2024/2025 a replacement exists that
  works across all three years (see "Porting to another Revit version"), only the TFM, the
  `RevitAPI.dll` reference and the install folder differ.
- **WPF without XAML.** The project uses `Microsoft.NET.Sdk` (not the WPF SDK), no markup is
  compiled; windows are built in code in the constructor, and the WPF assemblies are pulled in by
  reference (net48) or `UseWPF` (net8.0-windows — see the TFM-by-year entry). Adding a `.xaml` file
  would mean changing the SDK type; do not do that in passing.
- **Windows know nothing about Revit.** `AddFormulasWindow` gets a `FamilyParameterInfo` (name +
  `CanAssignFormula` + `HasFormula`) and validates rows itself. The exception is
  `SharedParameterRow`, which holds the `FamilyParameter` so the command has something to delete.
- **Icons** are `EmbeddedResource`s named `VladTools.Resources.<base>_16.png` / `_32.png`.
  `Icons.Load` returns `null` if the resource is missing: the button is simply left without a picture.
- **Parameter-name case.** Revit formulas are case-sensitive, so the comparison is done with
  `StringComparison.Ordinal` (`AddFormulasCommand.Find`, the dictionary in `AddFormulasWindow`). In
  the delete window the rule ignores case by default, and the "Match case" box turns on `Ordinal`.
- **Deleting parameters in several passes.** Revit will not let a parameter be deleted while
  another parameter's formula references it. `DeleteSharedParametersCommand.Remove` runs passes
  while there is progress, and only counts as a failure what did not delete in a pass with zero
  successes.
- **Project shared parameters are `SharedParameterElement`s.** In a `.rvt` they come from
  `FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement))`, and the categories plus
  instance/type only come from the `doc.ParameterBindings` map: the parameter element itself cannot
  say. The map is built once into a dictionary keyed by `InternalDefinition.Id`. An unbound
  parameter is not a bug: it arrived with a loaded family or is left over from a removed binding —
  hence the "Show" filter in the window, and the "Instance/Type" column reading "Not bound". Deletion
  is `doc.Delete(Id)`, which takes the binding and the values with it; before every deletion
  `doc.GetElement(Id)` is checked, because Revit may have taken the element along with a previous
  one. Project parameters not created from a shared parameter file (not shared ones) are never touched
  by the command — those are `ParameterElement`s with no GUID.
- **In both delete windows, only what is shown gets deleted** — the same rule as in "Rename
  Nested": a row that leaves the table loses its check mark. Otherwise a filter would be removing
  something invisible. Hence the shape of the rule itself: it does not just mark matching names, it
  leaves only them in the table — every filter in the window converges on one `InScope` predicate,
  and `RebuildVisible` rebuilds the list and sets the check marks in a single pass. "Invert the
  search" only flips the comparison result, but **not** the empty-string case: an empty rule shows
  everything even inverted, otherwise one check box would clear the whole table. In "Rename
  Nested" the "Find" field does not behave that way and should not: there it is part of the
  replacement, not a filter.
- **Dimension labels are the only guard against broken geometry.** Revit will let a parameter
  labelling a dimension be deleted: the label simply falls off, and the family falls apart. A label
  can only be read via `Dimension.FamilyLabel` **inside the family document** (there are no such
  dimensions in a project), and the getter throws instead of returning `null` on a dimension that
  cannot be labelled — it must only be read inside a `try`. In a family this is a single collector
  pass when the window opens, and the "Do not show…" box is already checked. In a project this
  cannot be done that way: each loaded family would have to be opened via `EditFamily`, which takes
  minutes, so the check hangs on the "Scan Families" button, and until it is pressed the window
  honestly says the families have not been scanned. Only the loaded families themselves are
  scanned, not what is nested inside them.
- **The project scan is cached per family, not as a whole** (`DimensionLabelCache`): one row per
  family, keyed by `UniqueId`, its freshness marked by `Element.VersionGuid`. So a rescan only opens
  families that are new or changed, and the window opens with a ready result.
  **Trap:** per Revit's own documentation, `VersionGuid` changes on save and synchronisation, not
  on every edit ("in-between saves this version cannot be used to determine if any particular
  element has changed"), so a family reloaded during the current session looks unchanged to the
  cache — and that is the dangerous side of the mistake. Hence the button's second state: when
  there is nothing left to open, it scans everything again, bypassing the cache. That "again"
  cannot be removed while the freshness marker stays `VersionGuid`.
- **A window gets long-running Revit work as a lambda.** `DeleteProjectParametersWindow` takes a
  `Func<FamilyDimensionScan>`: confirmation, the wait cursor and the report live in the window,
  `EditFamily` and the collectors live in the command. The "windows know nothing about Revit" rule
  is kept up exactly this way. `EditFamily` cannot be called while a transaction is open, and the
  document it returns must always be closed (`Close(false)`), otherwise family copies stay hanging
  in memory for the rest of the session.
- **"Cleanup" is the only command where the order of items in the window and the order of
  execution differ.** In the window, items stand in whatever order the user named them; execution
  follows `CleanupCommand.Order`: presentation first (sheets, views, legends, schedules, filters),
  then groups, and unused families last. By then titleblocks, tags and detail components have
  become unneeded too, and they leave in the same batch. Each item gathers its own element list
  again rather than reusing what the window showed: the previous item has already changed the document.
- **Unused families are counted by hand: Revit 2022's API has no "Purge Unused"**
  (`Document.GetUnusedElements` only arrived later — confirmed by reflection over `RevitAPI.dll`).
  A type counts as used if `GetTypeId` of any placed element refers to it (the pass covers every
  kind: a tag, a titleblock and a detail component all hold a type just as firmly as a
  `FamilyInstance`), plus `ElementId` parameters on types (a curtain panel, a nested "Family Type")
  and legend components. **Trap:** a type has built-in parameters pointing at itself and at its own
  family — without explicitly excluding self-references in `AddReferences`, every single type would
  come out "in use", and cleanup would silently find nothing. Right before deletion each candidate
  is checked once more via `GetDependentElements(new ElementIsElementTypeFilter(true))`: if Revit
  says placed elements would go along with the type, the type is kept, and the count of what was
  kept goes into the report — otherwise a miss of this guard would look like "the button does nothing".
- **Model groups are ungrouped, not deleted.** `doc.Delete` on a group type would take its contents
  — the project's geometry — with it; `Group.UngroupMembers` leaves the elements in place. Revit
  will not ungroup a pinned group, so `Pinned` is cleared before calling it. There are several
  passes, as with deleting parameters: Revit will not hand over a nested group while it sits inside
  another. Group types left empty by ungrouping do not disappear from the browser on their own —
  the separate "unused groups" item removes them.
- **What "Cleanup" never touches:** the active view (Revit will not let the one the user is
  standing on be deleted), view templates, internal views (`ViewType.Internal`, browsers,
  calculation reports) and internal schedules — the revision schedule inside a titleblock and the
  internal keynote schedule (`ViewSchedule.IsTitleblockRevisionSchedule` /
  `IsInternalKeynoteSchedule`): these live not in the browser but inside the titleblock, and
  deleting them breaks it.
- **`FormulaParser` is a heuristic, not a parser.** The API gives no way to parse a formula, so the
  text has struck out of it: string literals, known parameter names (longest first — otherwise
  "SP_Mass gross" would split apart), functions and constants from `Reserved`, calls of the form
  `name(`, and units next to a number ("1 kg"). Whatever identifiers remain are counted as missing
  parameters. To extend it, edit `Reserved` and the striking-out rules.
- **Renaming nested items.** The window's list *is* the scope of the work: rows that are not shown
  (the "Show" toggle) are returned to their own names, otherwise the rule would be changing what is
  invisible. Editing the "New name" cell by hand sets `IsManual`, and the rule no longer overwrites
  that row — like a typed value overriding a formula in Excel. A taken name is checked against
  **every** row, not only the checked ones: a name held by an item that is not being renamed is
  taken too; types are compared within their own family, and case is ignored, since Revit does not
  distinguish it in names either. Renaming runs in several passes, like deleting parameters: while
  a name is held by a neighbour that is also being renamed, Revit will not release it.
- **Connectors on the 3D view are hidden twice**: by category (`OST_ConnectorElem` + the X/Y/Z
  axes, always via `new ElementId(BuiltInCategory)` — `Category.GetCategory` returns `null` inside
  a family) and per element with `HideElements` on the `ConnectorElement`s. Each view setting is
  applied through its own `Try(...)`: a failure on one (a view under a template, say) does not
  cancel the rest, it lands in the warning list instead.
- **Batch-loading links rests on three things missing from the Revit API.**
  1) *Revit Server folders* are not exposed by the API at all — they are read by
     `RevitServerAdminRESTService<year>` over HTTP (`RevitServerClient`). The service has its own
     path separator — a vertical bar (`|`, one root), while a link needs a path like
     `RSN://server/folder/model.rvt`; `RevitServerClient.RsnPath` translates between them. Three
     headers (`User-Name`, `User-Machine-Name`, `Operation-GUID`) are mandatory: without them, a
     refusal. `RevitServerClient.ServiceVersion` is not a hard-coded constant: the same built DLL now
     runs in both 2022 and 2024, and the two use different service names
     (`RevitServerAdminRESTService2024`). `App.OnStartup` sets the value from
     `ControlledApplication.VersionNumber` every time the add-in starts; the `"2022"` in the class
     itself is only a fallback, in case `OnStartup` did not run.
  2) *BIM360/ACC folders* are not exposed by the API either, but Revit itself holds a three-legged
     token for the signed-in user: `Autodesk.Revit.AdWebServicesBase.GetInstance().GetOAuth2AccessToken()`
     from `SSONET.dll` next to Revit.exe. With it, `AccClient` reads the Data Management API, and
     neither an APS application of our own nor a sign-in window is needed. **This is an
     undocumented API**, so every access to it goes through reflection over the assembly already
     loaded, and any failure means not a breakage but "there is no token": the window offers
     entering the GUIDs by hand (`CloudLinkWindow`). Take the assembly from the `AppDomain`, not
     `LoadFrom`: it is a mixed-mode assembly, a second copy is not wanted.
  3) *A cloud model is addressed by a pair of GUIDs*, not a path:
     `ModelPathUtils.ConvertCloudGUIDsToCloudPath`. The GUIDs come from the `included` section of
     the `folders/{id}/contents` response — the latest versions are already there, so a separate
     `items/{id}/tip` request per model is not needed (on a folder of a hundred files that is the
     difference between a second and a minute). Only a workshared (C4R) model can be linked: the
     rest simply have no such pair, and never make it into the list.
- **JSON is parsed with our own code** (`Infrastructure/JsonHttp.cs`), not `JavaScriptSerializer`:
  that one drags in System.Web.Extensions, and the add-in lives inside somebody else's process,
  where an extra assembly is one more way of failing to load. The parser is read-only and silent:
  a missing key or the wrong type yields empty rather than throwing.
- **Link worksets are matched by name, not by id.** Every model has its own `WorksetId`; the only
  thing "00_Shared levels and grids" has in common across links is its name. So the window works
  with names, and `LinkManagerCommand.Configuration` translates them into ids separately for every
  link via `WorksharingUtils.GetUserWorksetInfo(ModelPath)` — it reads the worksets without opening
  the model. Next to the names there is a rule (`00_`): one model calls the workset "00_Shared
  Levels and Grids", another names it differently, and the rule is applied **on load, to each link
  separately** — otherwise the name list would only ever catch what it had already seen. If the
  worksets could not be read, the link still loads, just in a plain mode, with the reason going
  into the report.
- **A link has two worksets, and they are different things.** "The worksets inside the link" is
  what to open or close inside the linked model (`WorksetConfiguration`), one setting for the whole
  batch. "The project workset" is where to put the link element itself in the open project
  (`BuiltInParameter.ELEM_PARTITION_PARAM`), and it is different for every row: architecture into
  "01_Link_AR", structure into "01_Link_KR". The project workset is set on the **created elements**,
  rather than through `WorksetTable.SetActiveWorksetId`: that way the link lands where it was asked
  to, regardless of the workset the user is standing on, and the document's active workset is
  untouched. Both elements are moved — the `RevitLinkInstance` and the `RevitLinkType` — the same
  as Revit itself does when a link is inserted with an active workset. The workset name goes into a
  saved link set as its last field, but worksets are a project's own, so on adding a row the name
  is checked against the project's list (`LinkManagerWindow.Normalize`): no such workset — reset to
  "(active)" plus a note in "State".
- **Reloading existing links comes first, creating new ones comes second — and this order is
  essential.** `RevitLinkType.LoadFrom` requires every transaction to be closed, and on top of that
  **it wipes the document's undo history**. Put it after the creation, and Ctrl+Z would no longer
  bring back the links just set up. So `Reload` runs first, outside a transaction, and
  `RevitLinkType.Create` + `RevitLinkInstance.Create` follow, in a single transaction, as
  everywhere else. An existing link's placement is left untouched: Revit will not allow it to
  change. Do not reorder this while `LoadFrom` still wipes undo history.
- **Link worksets are only ever requested as "close all, open the listed ones".** The reverse
  phrasing — `OpenAllWorksets` + `Close(checked)` — is equally valid per the XML doc, but on links
  Revit silently does not carry it out: `Create`/`LoadFrom` report success, the worksets stay open.
  That is exactly why the button "failed at closing" until September 2026. Autodesk's own example
  specifically for links (Developers Guide → Linked Files → Revit Links) uses only
  `CloseAllWorksets` + `Open(...)`, and every working recipe found in the community does too. So
  `LinkManagerCommand.Configuration` computes a **complement**: "close the checked ones" turns into
  "open every link workset except the checked ones", and "close all except the checked ones" turns
  directly into `Open(checked)`. The workset list needed for this is read anyway.
  One case cannot be expressed as a complement: "as last opened" — only Revit knows what was open
  last time — there `Close()` remains, with the same unreliability, and that case is caught by the
  check described below. Do not revert to `Close()`.
- **`BaseOption` must return `OpenAllWorksets` on a read failure, never `CloseAllWorksets`.** When
  the link's workset list could not be read, there is nothing to compute a complement from.
  Substituting "close all" here, as in the main path, would mean silently loading the link empty on
  any network hiccup — the worst possible failure. So the fallback mode is a method of its own,
  kept separate from the main path.
- **Workset ids are re-read right before loading, never taken from the window's cache.** By
  Autodesk's own documentation, `WorksetPreview.Id` changes on synchronising with the central model
  (only `UniqueId` is stable, but `WorksetConfiguration` only accepts a `WorksetId`), and any amount
  of time can pass between "Read Worksets" and "Load". A stale `WorksetId` is silently ignored by
  both `Open` and `Close`. So `Execute` clears `_worksets` right after the window closes: the cache
  lives for one load run (creation, verification, a possible reload), not the whole life of the window.
- **Checking the worksets after loading tells apart three outcomes, not two.**
  `WorksetConfiguration` only conveys a wish, and finding out whether Revit carried it out means
  re-reading the already-loaded `RevitLinkInstance.GetLinkDocument()` through a
  `FilteredWorksetCollector` (`LinkManagerCommand.Check`). There are three outcomes: "everything as
  asked", "these worksets are wrong", and — separately — **"could not be checked"**
  (`WorksetVerdict.Unknown`: the link did not hand over its document, the collector threw). Merging
  the last one with the first is exactly how a closing failure stayed unnoticed — the report read
  "Loaded" with not a word about worksets. `Unknown` goes into a report section of its own,
  "Worksets were requested but could not be checked", and a repeated `LoadFrom` is **not** attempted
  for it: that wipes the undo history, and that is not a price worth paying for a guess. What each
  side should end up with after loading is set by `Expected` — a mirror of `Configuration`; edit
  one, edit the other. The retry only happens on `Mismatched`; for new links this is only possible
  **after** `Commit()` of the creation transaction (`LoadFrom` runs outside a transaction — see
  above), so `Apply` first gathers candidates into `toVerify` and heals them in a separate pass
  after the `using (transaction)` block. The final report reads: worked on the first try (silently),
  worked on the second try (the "a reload fixed it" list — that also wipes undo history), did not
  work (a line in "Could not be done" with the workset names), or not checked (a section of its own).
- **"The workset did not close" is really two different cases, and they must sound different.**
  Either Revit refused (the check above catches this), or **there is no workset with that name in
  the link at all** — the name is checked from memory in `_settings.txt`, while the consultant
  named theirs differently, and `Matches` never finds it. The second case used to be completely
  silent from both sides: `Configuration` silently skipped the name, `Expected` promised nothing for
  a name never found (so the check said nothing either), and the window's "Found in" column showed
  "from last time" whether the workset was not found or nobody had looked for it. Now
  `LinkManagerCommand._seenNames` gathers the names actually encountered in links during the run,
  and `Missing` returns to the report the checked names that were found in none of them;
  `WorksetRow.IsCounted` gives the column a third state — "in none of them". A zero `LinkCount` on
  its own does not mean that: with no links read it means "not looked at", so `RecountWorksets` only
  sets `IsCounted` once worksets have been read for at least one checked link. An empty
  `_seenNames` (not a single link was read) turns the whole section off: the reason there is a
  different one, and it is already in the report.
- **Link workset names are only ever compared through `LinkPreferences.SameWorkset`** — ignoring
  case (Revit does not distinguish it in workset names either) and **trimming leading/trailing
  spaces**. The trim is not cosmetic: a consultant's workset can easily be called "00_Reference
  planes " with a trailing space, invisible anywhere in Revit's own list, and the mismatch used to
  be devastating. The window trimmed the name in `AddWorkset`, but comparisons (`RecountWorksets`,
  `Matches`) used it as-is — and such a workset disappeared three times over: it never showed up as
  a row of its own (the trimmed name looked like a duplicate of one already checked), it showed "in
  none of them" in "Found in", and it **never closed at all**. One rule for everyone: never add a
  new workset-name comparison through plain `string.Equals`. If a name differs by more than
  whitespace (a homoglyph, a doubled internal space), `SameWorkset` honestly says "a different
  name", and a second row appears in the list — that is the diagnostic.
- **What cannot be done to link worksets at all.** The API cannot change the worksets of an
  **already-loaded** link: `WorksetConfiguration` is only accepted by `RevitLinkType.Create` and
  `LoadFrom` — confirmed by reflection over `RevitAPI.dll`, there is no equivalent of Revit's own
  "Manage Links → Manage Worksets" button. That is exactly why reconfiguring an existing link's
  worksets costs a reload that wipes the undo history. Nested links' worksets cannot be managed at
  all (Autodesk has an article specifically about this — "Closed worksets from nested links are
  visible in host file") — if a consultant has their own underlay linked inside their model, its
  levels and grids stay visible no matter what the button does.
- **A link's project workset can be guessed from the model name**
  (`DisciplineCatalog`, the "Guess from the model name" box next to the workset choice). The model
  name is split into tokens at every non-alphanumeric character (so `_R22`, dots and hyphens fall
  away on their own, no special handling of a version suffix needed), and the last token matching a
  code **from the list** (`OV`, `VK`, `AR`…) is taken as the discipline. From the list, not "the
  last token, period": a building number or a date often sits at the very end of a name. The same
  tokeniser also splits a project workset's name — a workset matches if the code sits in it as a
  separate word (`01_Link_OV`, and the like, but not `Provod`). Exactly one match → the workset is set.
  **Several matches are not yet a reason to give up** (`DisciplineCatalog.Preferred`). Candidates
  are first compared against the model's own name: worksets are sometimes split not only by
  discipline but by building too (`01_Link_AR_B01`, `01_Link_AR_B03`), and then the right one
  identifies itself — the one with the most words in common with the model name wins. If that does
  not settle it, the "shape" of a name is used instead: the name itself with the code cut out
  (`01_Link_ES` → `01_Link_·`), and the candidate whose shape is worn by the worksets of the most
  **other** disciplines wins — that is how `01_Link_AR` beats `05_AR_Elevations` next to
  `01_Link_ES` and `01_Link_OV`. A tie or zero score still means "the user chooses", but "State"
  now lists the candidates themselves: without the names, "several match" says nothing about what
  to choose from.
  **A zero match is no longer silent** — but only where the project actually has per-discipline
  worksets set up (`HasDisciplineWorksets`): then a missing one is an answer ("no workset with code
  \"PS\" in the project"), not "the guess failed". Where disciplines are not split that way, no note
  is added — it would show up on every single row. For the same reason, "no discipline visible in
  the model name" is also written out — an empty cell with no explanation read as the button being broken.
  The guess **only fills in what is empty**: a link with a workset already set (by hand or from a
  saved set) and an existing link are left untouched; a hand edit in the table clears the
  `_autoWorkset` flag, and turning the guess off no longer resets such a choice. The code list lives
  in `links\_settings.txt` (the `DISCIPLINE` lines), empty = `DisciplineCatalog.Defaults`; on
  closing the window, the list actually in effect is written back in full, so there is something to edit.
- **A link kit is guessed from how the project's folders are laid out, not from a model list.**
  Discipline folders sit side by side in a project — `3.0_AR`, `4.2_KR`, `5.1_ES`, `9.1_PT` — and
  every model name carries a building number (`MK3-VSC-B01-AR`). That is enough to offer a
  ready-made link set instead of choosing it by hand in a dozen models of a building. The parsing
  is a heuristic of the same level as `FormulaParser` and `DisciplineCatalog` (`ModelKit`): name
  tokens, not a naming-convention parser. Hence the shape of the window: what was found is shown in
  a table **before** anything is linked, and a discipline where several models turned up is one the
  window refuses to check — the same rule as when guessing a workset: several matches mean "the
  user chooses", not "take the first one".
- **The kit walk is deliberately narrow: the root and the discipline folders in it, not the whole
  project tree.** Every cloud folder is a network request, and a full walk of a project with a
  hundred folders would cost a minute instead of a second. Hence `FolderLimit`: hitting it does not
  stay silent, it says the search was probably started from the wrong folder. A wrong root heals
  itself from both directions: if no discipline folders are found in the root, the search first
  climbs **one level up** (the open model may well sit in `01_Base Model` next to the disciplines,
  rather than inside them), then looks **one level down** (`03_Models\3.0_AR`). The folder the walk
  actually ran in is returned in `ModelKitScan.Root` and shown in the window — otherwise the wrong
  one would be remembered.
- **Revit itself says where the open model lives — the cloud included.** For a workshared model
  the **central** model's path is taken (`GetWorksharingCentralModelPath`), not the local copy's:
  the discipline folders sit next to the central model. A cloud model has
  `Document.GetCloudFolderId` — exactly the id Data Management knows the folder by (confirmed by
  reflection: present in 2022, 2024 and 2025), and the project id is assembled from the cloud
  path's GUID with a `b.` prefix. So neither the hub nor the project has to be searched for — the
  kit is assembled straight from the model's folder. Nothing came of it (the project has never been
  saved, or is open detached) is not a bug — the window will ask for the folder through buttons and
  the building through a field.
- **`ModelStore` is a third view of the same stores, and it is not a duplicate of the first two.**
  `ModelPicker` walks a person down a tree, `LinkCatalog` turns an entry into a `ModelPath` for
  Revit, and here we need to walk folders in code and be able to go **up** — something neither of
  the other two gave. Going up for the cloud costs a request (`AccClient.Folder`): `contents` and
  `topFolders` lead down, nothing leads up. The folder is picked with the same tree used for
  picking models (`ModelBrowserWindow` with a `pickFolder` predicate) — no second tree is needed.
- **None of the three years being built has its own folder-picker dialog in WPF.**
  `OpenFolderDialog` only arrived in .NET 8, while 2022 and 2024 live on .NET Framework 4.8;
  dragging in WinForms — inside an add-in loaded into someone else's process — just for one dialog
  is a bad trade. So a folder on disk is chosen by pointing at any model inside it
  (`ModelPicker.PickFileFolder`), and the tooltip says exactly that.
- **A new `RevitLinkInstance` is pinned right away (`Pinned = true`).** A consultant's link is
  inserted by coordinates, and an accidental mouse drag is later hunted down by the whole team;
  removing a pin in Revit is one button, putting a link that drifted back in place is not. Only
  newly created instances get this (in `Create`, right after `RevitLinkInstance.Create`); existing
  links are left untouched by `Reload`/`Move`. A failure here does not derail the load — it goes
  into the report as a line, like everything else here.
- **`ImportPlacement` is set on the instance, not the link.** `RevitLinkOptions` has no placement —
  only the relative path and the workset configuration; the placement method is the third argument
  to `RevitLinkInstance.Create`. A relative path is only for files: for Revit Server and the cloud
  it is always absolute.
- **One tree serves both stores** (`ModelBrowserWindow` + `BrowseNode`): both the server and the
  cloud are nested lists expensive to read whole, so their contents load as nodes are expanded, and
  what exactly fills a node is given to the window as a lambda. Reading happens right on the UI
  thread under a wait cursor: the dialog is modal, Revit waits regardless. A service failure does
  not close the window, it becomes a red line inside the folder that could not be read.
- **Copy-monitoring does not exist in the Revit API — and will not appear through a workaround.**
  The "Base File" button does four of five steps, the fifth it only starts. `RevitAPI.dll` only has
  **reading** already-existing monitoring links (`Element.IsMonitoringLinkElement`,
  `GetMonitoredLinkElementIds`, `GetMonitoredLocalElementIds`) — confirmed by reflection in 2022,
  2024 and 2025; neither creating one nor a ribbon equivalent exists. The only honest way forward is
  `PostableCommand.CopyMonitorSelectLink` (present in `RevitAPIUI.dll` in all three years):
  `BaseFileCommand.OpenMonitor` queues the command with Revit, and it fires after the report
  closes, leaving the choice of the link and the elements to the user. This must **not** be
  replaced with a copy without monitoring (`ElementTransformUtils.CopyElements`): the levels and
  grids would appear, with no link to the base file, and nothing on a plan would show the
  difference — the worst kind of silent failure. Before `PostCommand`, the state is checked via
  `UIApplication.CanPostCommand`: the mode is not available on every view, and a silently failed
  button would look like a bug.
- **The order of "Base File" steps is set by dependencies, not convenience.** Link →
  `doc.Regenerate()` → `AcquireCoordinates` → the site name → the pin, all in one transaction, as
  everywhere else. The regeneration is mandatory: before it a freshly created `RevitLinkInstance` is
  not yet geometry as far as Revit is concerned, and "Acquire Coordinates" works with exactly that.
  Switching to the workset happens **after** `Commit`: the active workset is session state, not
  part of the document, and Ctrl+Z should not restore it. That step is last on purpose: levels and
  grids copied next land in whichever workset is active at the moment of the copy — that is the
  whole reason for the step.
- **Both "Base File" worksets are created by the command if they do not exist**
  (`BaseFileCommand.Ensure`, one method for both). A new discipline may have neither
  "01_Link_BM", where the link itself goes, nor "00_Shared levels and grids", the one to switch
  into; a silent skip would mean the button failed at its main job — the link would land in the
  active workset with nowhere to switch to. Both window fields are therefore editable: choosing
  from the project's list something that is not in it is impossible. The name is checked against
  `WorksetTable.IsWorksetNameUnique` before creating it — one already taken by another kind of
  workset goes into the report. `01_Link_BM` follows the same convention as consultants'
  "01_Link_OV" (BM stands for base model); it is stored **separately from the model**, under its
  own `LINK_WORKSET` key: the workset is the same across every discipline, while the base file
  differs in a new building. For a link already in the project, the field shows its current
  workset, and changing it in the window really does move the link (`BaseFileCommand.Move`) —
  shown but not done would look like done.
  The default placement for the base file is "Origin to origin" rather than "By shared
  coordinates" as in "Link Manager": the shared coordinates are yet to be acquired from it at this
  point, and there is nothing to place by.
- **The base file itself is guessed from the open model's name** (`BaseFileFinder`), following the
  same convention as the "Building Kit": a building number sits in the model name
  (`MK3-VSC-B01-VOIDS` → `B01`), and the whole project's base files live in one folder,
  `01_Base Model`, next to the discipline folders. So the file needed is called
  `MK3-VSC-B01-BM`, and the building's position in the name uses **the same key** as the kit
  (`links\_settings.txt`, `KIT_TOKEN`): the building number should not be configured twice under
  different settings. The walk here is narrower still than the kit's — exactly two folders: the one
  the model lives in (which may itself be the base file folder) and the one a level up. The guess
  runs when the window opens, that is, while the user is waiting, and a network request there costs
  more than a missed find; for the same reason it can be turned off with a check box, and is
  **skipped entirely** when the model remembered from last time is already from this building — in
  a new discipline of the same building, the base file is the same one.
  A folder name is compared word by word, without leading numbers (`01_Base Model` ≡ `Base Model`):
  the leading number changes from project to project. The `BM` code may be missing from the name —
  then a model matching only by building is taken, with a "check it" note: it still lives in the
  base file folder, and that is more honest than "nothing found". **Several matches means none is
  taken** — the same rule as when guessing a workset or a kit; the caption lists the names though —
  without them "several match" says nothing about what to choose from. A failed guess when the
  window opens is shown as a grey line, not a dialog: the window has just opened, and a modal dialog
  on top of it out of nowhere would be the worst way to greet the user; from the "Guess" button the
  same failure speaks up in full.
- **Picking a model and parsing links are shared between both link commands.**
  `UI/ModelPicker.cs` — the four sources (file, Revit Server, BIM360, a pair of GUIDs) with their
  trees and remembering the server name; `Infrastructure/LinkCatalog.cs` — the links already in the
  project, its worksets, turning a `LinkEntry` into a `ModelPath`, and a Revit failure code into an
  English sentence. "Link Manager" and "Base File" do different things with this, but they take the
  same code — do not set up a second copy of it.
- **"Coordination Review" does not exist in the Revit API at all**, and the "Accept Changes" button
  comes at it from the other side. Neither reading its list nor pressing "Accept" in it is
  possible: of the whole monitoring feature only `Element.IsMonitoringLinkElement`,
  `IsMonitoringLocalElement`, `GetMonitoredLinkElementIds` and `GetMonitoredLocalElementIds` are
  exposed — confirmed by reflection over `RevitAPI.dll` 2022, 2024 and 2025, no "CoordinationReview",
  "Postpone", "AcceptDifference" anywhere in there (`RevitAPIUI.dll` only has
  `PostableCommand.CoordinationSelectLink`, which opens the dialog itself). So
  `CoordinationCatalog` computes the differences itself, and the command moves the elements — that
  is, it does exactly what the "Move" action inside the dialog would do. Revit's own list empties
  itself: it shows the difference between the copy and the original, and there is none left.
  **This has to be checked in Revit by eye** — that is what the "Open Coordination Review after
  applying" box is for, on by default; it is not a continuation of the work but a check on it.
- **A difference accepted in advance looks like a discrepancy here.** The "Accept Difference"
  action in Revit's own dialog remembers an allowed offset between the copy and the original, and
  the API gives no way to read it. The button compares positions directly, so a grid deliberately
  moved aside will show up in the list and, if checked, will be moved back onto the original.
  Hence the shape of the window: differences are shown as numbers first (shift, rotation,
  elevation) and only then applied — "accept everything silently" here would mean overwriting
  somebody's deliberate decision.
- **The API does not hand over the "project element — link element" pair.**
  `GetMonitoredLinkElementIds`, despite its name, does not return what an element monitors, but the
  link instances it is found in (confirmed both by the documentation and in practice). So the pair
  is recovered by name: grid and level names are unique in a document, and monitoring keeps them in
  sync. What does not match by name is matched further, by position (`MatchByPosition`), which is
  how a rename is found. A position match is only taken **when exactly one candidate is in the
  window**: otherwise a deleted grid would pair up with a random new neighbour, and the button would
  silently move the wrong thing — a mistake invisible on a plan. An element both renamed and moved
  far away honestly ends up in "not in the file" plus "new in the file", never matched at random.
- **For a grid, the infinite line is aligned, not the segment.** A grid's length in a project is
  trimmed for its own views, has nothing to do with coordination, and matching the ends would mean
  spoiling someone else's work. Hence the edit scheme (`TryGridDiff`): a rotation about the
  **midpoint of the grid itself** — after which the directions match, and the centre of rotation
  stays put — followed by a sideways shift. The shift is computed for the post-rotation position, so
  the order "rotate, then shift" is mandatory. For an arc grid, rotating about its own centre
  changes nothing but its ends, so only the centre is translated; a changed radius cannot be
  applied, and such a row goes into the table flagged.
  `Grid.Curve` is a read-only property (no setter in any of the three years), and there is no way
  other than `ElementTransformUtils`.
- **The pin is removed for the duration of the edit and put back right after.** A base file's
  grids and levels are almost always pinned — otherwise they get dragged with the mouse — and Revit
  will not move a pinned element, so without this the button would fail exactly on the models it
  was written for. A pin that does not come back does not derail the rest, but it goes into the
  report: the element is correctly positioned and unpinned, and that has to be known.
- **The button never touches missing or new link elements.** Deleting a level means taking
  everything standing on it with it, and that decision belongs to a person, not a batch button;
  setting up monitoring on a new link element is something the Revit API cannot do at all (the same
  gap as the "Base File" button's copy-monitoring). Both are shown in the window as an unchecked
  row and counted in the status line — staying silent about this would make "accepted everything"
  mean "everything is fine".
- **In the "Accept Changes" window everything applicable is checked from the start — and that is
  not an exception to the "an empty filter does not mean select everything" rule.** That rule
  guards against accidental deletion; here nothing is deleted, everything rolls back with one
  Ctrl+Z, and the button is asked to accept the coordination-file changes all at once — that is the
  whole point of it. The "only what is shown gets applied" rule still holds as everywhere else: a
  row that leaves the table through the "Show" filter loses its check mark.
- **Auto dimensions do not copy a sample by its references — a sample's references belong to
  specific walls** and are meaningless in another room. Instead there is a fixed catalogue of chain
  kinds (`DimensionChainKind`: Overall / Openings / Opening centres / Partitions / Wall faces / All
  combined), and parsing a sample (`DimensionSampleReader`) only picks a kind, an offset and a type
  for every sample dimension — a heuristic of the same status as `FormulaParser` (below), not an
  exact calculation. The reference behaviour the chains are checked against comes from
  `Замечания\Пример кладочного с размерами.pdf` (a sample masonry plan with dimensions). The result
  is ordinary `DimensionChainRow` rows in the window's table, editable by hand; the button also
  works with no sample at all, if the chains are assembled by hand in the window.
- **The corners of a chain come not from its own wall's end, but from the longitudinal face of the
  neighbouring wall.** At a real corner Revit almost always builds a mitred (bevelled) end so the
  walls meet cleanly — such a face's normal is no longer parallel to the side's axis,
  `FaceHitsForWall` does not find it, and without this rule a random matching face somewhere deep
  in the wall would be picked for the corner instead (exactly what showed up on the first check —
  dimensions on one loop caught either the inner face or a point buried in the wall's thickness at
  random). The longitudinal face of the neighbouring (perpendicular) wall is never mitred — it is
  flat along its whole length, so `DimensionReferenceCollector.BuildCorners` takes the corner from
  there: for a loop side's neighbour (`LoopNeighbor`), its nearest face to the shared corner is used.
- **The corner search is bounded by distance (`CornerWindowMm`, 600 mm), and this is not
  overcaution.** The fallback used to be "the outermost matching face on the side, whatever it is"
  — with no check on where it actually stood. On a wall with a bevelled end, the outermost matching
  face turned out to be the jamb of the **first opening**, and the chain started at a door half a
  metre from the corner: the designer's complaint "the dimension does not run along the whole wall,
  even though 'All combined' was selected" is exactly this. Now a corner is accepted only if it
  really does sit at the end of the side; the window is not zero, because with
  `SpatialElementBoundaryLocation.Center` the boundary runs along the centreline and the
  neighbouring wall's face sits half its thickness away from the end of the side. Nothing found
  within the window — the chain still builds, from the outermost face available, but is flagged
  `DimensionReferenceResult.Warning` and goes into the report under its own section, "Chains that
  need checking" (a true corner reference may simply not exist: the side may start in the middle of
  a wall, past a room separator). This case must not be merged with a success — silence is exactly
  what made a shortened chain look correct.
- **The end ticks of a chain pick up the thickness of the adjoining wall** (a window box, on by
  default; `THICKNESS` in a template). Each end takes not one face of the neighbouring wall but
  both — the near one (the corner itself) and the far one, beyond the corner. The first and last
  link of the chain then becomes that wall's thickness — `120 | 3775 | 120`, exactly how every
  chain on a masonry plan is built (checked against the "Masonry plan. Fragment 2" sample). Without
  this the thickness showed up on one side and not the other, depending on whether the own wall's
  end happened to fall within the normal tolerance (the designer's second complaint: "sometimes it
  shows the wall thickness, sometimes it doesn't"). The far face is taken **only if it lies beyond
  the corner**, outside the span of the side: at an internal (concave) corner both of the
  neighbour's faces sit inside the span, and there it is not a chain link but an ordinary
  partition — `NeighborPartitionHits` finds that instead. Hence `LineMarginMm = 1000`: the end
  references now sit outside the side's own span, and the dimension line has to reach over them.
- **`LoopNeighbor` walks the loop only when looking for a corner (`walk: true`), never when
  looking for partitions.** Between two walls at a corner there is often a short piece of boundary
  with no wall (a room separator, a door in an opening with no wall) or a collinear continuation —
  the corner search used to stop right there. Partitions must not walk: a corner is additionally
  checked by distance (`CornerWindowMm`), a partition's tick is not, and a far perpendicular wall
  projected onto our axis somewhere mid-span would become a tick out of nowhere.
- **Every point inside a chain, except the corner ones, is filtered by the range between the
  corners (`CornerSet.Interior`), not by 1 mm deduplication alone.** A wall not perfectly trimmed at
  the corner (no clean mitre — the end stays flat and parallel to the axis) gives its own corner
  face a second time through `OwnWallHits`, on top of the real corner from the neighbouring wall
  (see the previous points); this second point is usually offset from the true corner by more than
  1 mm — ordinary deduplication does not collapse it, and a short spurious tick roughly the wall's
  own thickness appears next to the corner. `Interior` drops, from any "extra" set of points (a
  wall's own faces for "Openings", joints for "Wall faces", centre lines for "Opening centres",
  partition faces for "Partitions"/"All combined"), anything that is not strictly between the
  **near** corner points of the chain — the range is computed from those, not the far ones: nothing
  legitimate lives between a wall's far and near adjoining face.
- **"All combined" does not place opening centres, even though the name might suggest it.** A
  centre line splits every opening in half and adds a tick in the middle of every door; a masonry
  plan never has such a tick, and it would make the chain twice as dense and unreadable. Whoever
  needs centres takes the separate "Opening centres" chain alongside it. `DimensionSampleReader`
  knows this: a sample that mixes centres with something else has no exact match in the catalogue
  and is flagged in "State" as parsed approximately.
- **One wall face gives both an end and an opening jamb — a shared trick across every chain
  kind.** `Wall.get_Geometry(new Options { ComputeReferences = true })` → `Solid.Faces`, and among
  them the `PlanarFace`s whose normal is parallel to the side's axis are taken
  (`DimensionReferenceCollector.FaceHitsForWall`). For a straight wall, such faces turn out to be
  exactly its ends and — not obvious in advance — both faces of every door/window opening: cutting
  an opening always adds a pair of faces perpendicular to the wall's length, that is, parallel to
  its axis. So "Overall" is the two end points of that same set, and "Openings" is the whole set;
  no separate pass over openings is needed. "Partitions" gets the same set, but from the
  neighbouring (perpendicular) side of the same boundary loop, projected onto **our** axis — walls
  meeting a room from the inside always split `RoomSideBuilder` into a side of their own (see
  below), which is how they are found. "Opening centres" is a separate path:
  `FamilyInstance.GetReferences(FamilyInstanceReferenceType.CenterLeftRight)`; if the family does
  not publish that plane, the chain is unavailable rather than substituting a random point.
- **Room sides are built by merging segments from `GetBoundarySegments`, not from
  `Room.Location`.** Neighbouring segments of one loop merge into a `RoomSide` if they point the
  same way and lie on the same line — that is how a piece of wall cut by an opening into several
  boundary segments stays one side. The "inward" normal is not taken from the loop's own walking
  order as given (Revit does not guarantee it is always the same direction) — the raw
  `BasisZ × direction` is checked with a point 50 mm off the side through `Room.IsPointInRoom` and
  flipped on a negative answer (`RoomSideBuilder.ComputeInwardNormal`). The inward/outward direction
  is not stored on the chain itself — it is one window setting for the whole run, not a row field: a
  mix of directions within one chain set is not something the heuristic needs.
- **`Selection.PickObject` cannot be called while a modal window is open** — "Take a sample…" in
  `AutoDimensionWindow` therefore does not read the selection itself, it closes the window with the
  `WantsSample` flag; the command does `PickObjects` outside the window and reopens it with the
  same state, now with the parsed sample (see "How a command is built", the
  `AutoDimensionCommand` exception).
- **Wall geometry is cached by `ElementId` for the whole run of the command**
  (`DimensionReferenceCollector`): one wall often belongs to several sides and several chains at
  once, and reading solid geometry is not free. Without the cache the command would grind to a
  halt on a model with hundreds of rooms.
- **Placing dimensions runs in two phases — all reads first, all writes after.** Creating a
  `Dimension` is also a document edit, and it marks geometry as stale: any `Face`/`Reference` read
  before that point (including from the `DimensionReferenceCollector` cache — which caches
  regardless of transactions) can no longer be used by Revit afterwards. Confirmed in practice: a
  loop of "read a chain's references → create the Dimension right away → read the next chain's
  references on the same side" fails on the second chain with a geometry-kernel error ("The input
  curve is not bound") — the second chain lands on a face already made stale by the first one. So
  `AutoDimensionCommand.Place` first walks every room/side/chain, gathering ready `Line` +
  `ReferenceArray` pairs into a `pending` list (still just reading), and only then opens a
  transaction and creates the `Dimension`s from the data already gathered, without a single new
  geometry read.
- **Labels are spread out in a third phase, after `Document.Regenerate()`.** Before regenerating, a
  freshly created dimension has no segments filled in yet (`Dimension.Segments`), and there is
  nothing to spread out. Regenerate is safe here precisely because the wall faces are no longer
  needed — all the geometry was read in the first phase. The spreading itself is
  `DimensionTextLayout`: a label with no room between its ticks is moved off the line onto
  `DimensionSegment.TextPosition` (a dimension with only two references has no segments at all —
  there it is `Dimension.TextPosition`), and Revit draws the leader itself, per the type's
  settings; `HasLeader = true` is set on top of that, inside a `try`/`catch` — not every type
  accepts it. **The Revit API gives no text width**, so it is estimated from the dimension type's
  `TEXT_SIZE` × `TEXT_WIDTH_SCALE`, the character count and the view scale (the font size is stored
  in paper units — on the model it is stretched by the view scale). This is a heuristic of the same
  status as `FormulaParser`, and it deliberately errs towards "pull it out": an unnecessary leader
  is harmless, an overlap is not. Labels pulled out one after another are spread across three
  tiers, so they do not overlap each other either.
- **A `DataGridComboBoxColumn` has three mutually exclusive bindings — set exactly one.**
  `SelectedItemBinding`, `SelectedValueBinding`, `TextBinding`: with two set, the second silently
  does nothing, a choice from the list never reaches the row, and the cell keeps whatever value the
  row was born with. That is exactly what the designer's first complaint looked like ("the right
  type will not select, something else of its own comes back"). In `AutoDimensionWindow` both
  drop-down columns are therefore built on a `DataGridTemplateColumn` with a live `ComboBox` in the
  cell (`BuildComboTemplate`): one binding, the value lands in the row the moment it is picked
  (`UpdateSourceTrigger.PropertyChanged`), not when editing mode ends.
  `IsSynchronizedWithCurrentItem = false` is mandatory there: the list is shared by every row, so is
  its collection view, and without this a choice in one row would drag the others along. Do not add
  a new `DataGridComboBoxColumn`.
- **A window row is brought in line with the project the moment it appears
  (`AutoDimensionWindow.Adopt`).** A dimension type from someone else's template or from another
  project's sample may be missing here, and there is no way to pick from a list something not in
  it — an unreachable value silently left in the row would look like "the field is empty for no
  reason". The default type is substituted, and this is noted in "State"; the same trick as with
  project worksets in "Link Manager". The default type is the one Revit itself would pick
  (`ElementTypeGroup.LinearDimensionType`), not the alphabetically first one — "the first in the
  list" is exactly what looked like a randomly substituted foreign type.
- **A second run never doubles the dimensions.** Every created `Dimension` is marked through an
  `ExtensibleStorage` schema (`AutoDimensionMarker`): a room's `UniqueId` (not `ElementId` — it
  survives a rebuild) plus a chain index; the "room + chain" pair is how the old dimension is found
  and removed before the new one is placed. A window box lets this be turned off. Dimensions the
  user placed by hand carry no mark and are never touched — by construction, not by checking a name
  or a layer.

## Storing user settings

The `%AppData%\VladTools\` folder holds all state outside the Revit document. All of it survives
across documents and Revit sessions, all UTF-8 with a BOM — so Cyrillic and other non-Latin text
opens correctly in Notepad.

`formulas.txt` and `names.txt` are user settings: read every time their window opens and rewritten
whenever it closes (including "Close" and Esc). `dimensions\` is not a setting but a cache: written
when a scan actually runs, not when the window closes. `links\` is both: `_settings.txt` is
rewritten whenever the "Link Manager" window closes, while the sets themselves are only saved with
the "Save set" button. `basefile\` holds settings only.

`formulas.txt` — the "Add Formulas" window's formula list. Format: `Parameter name = formula`, one
line per formula; only the **first** `=` sign splits the line (a formula may contain more —
`if (1=1,…)`); a `#` at the start of a line means the box is unchecked. A corrupt or empty file is
silently replaced with the `FormulaLibrary.Defaults` list.

`names.txt` — the "Rename Nested" window's name buffer. Format: one line — one saved value, a line
starting with a hash is a comment. There is no default list here: no file, or a corrupt one, simply
means an empty buffer.

`dimensions\<project name>_<path hash>.txt` — the saved family scan for dimension labels, one file
per project (the path hash keeps files with the same name from different folders from sharing one
scan). Format: `Family UniqueId | Element.VersionGuid | shared parameter GUIDs, comma separated`,
one line per family; an empty GUID list means the family was scanned and has no labels. A corrupt
file, or a failed write, silently means "no scan exists". A project with no path (never saved) is
never cached — there is no key.

`links\<set name>.txt` — a saved model list for "Link Manager". One line per model, fields
separated by a vertical bar: `FILE | path`, `SERVER | RSN://…`, `CLOUD | region | project GUID |
model GUID | name`. The last field may hold the project workset to put the link into; without it,
the active workset. Files written before this field existed are read as they are. The bar was
chosen because it cannot occur in a Windows path. For BIM360 a set is not a convenience but a
working tool: a GUID list gathered once loads even when the cloud cannot be reached.

`links\_settings.txt` — that window's settings: `KEY = value`, the keys `CLOSE` (a checked
workset's name), `SERVER` (a server name) and `DISCIPLINE` (a discipline code for guessing the
project workset from the model name) may repeat. `MATCH_WORKSET` (`1`/`0`) — whether that guess is
on. No `DISCIPLINE` line at all — `DisciplineCatalog.Defaults` is used; on closing the window, the
list actually in effect is written back in full. The "Building Kit" settings live here too: `KIT`
(a kit discipline code, may repeat; none at all — `DisciplineCatalog.KitDefaults` is used),
`KIT_TOKEN` (which piece of the model name counts as the building number; `MK3-VSC-B01-AR` → 3),
`KIT_DEEP` (descend into nested discipline folders or not), `KIT_BUILDING` (the building number
from last time) and `KIT_ROOT` (the search folder as a `ModelFolder.Format` string: `FILE | path`,
`SERVER | RSN://…`, `CLOUD | region | project | folder | name`). `KIT_ROOT` is a fallback, not the
main path — the folder is usually worked out from the open model itself, and the remembered one is
needed only where there is nothing to work it out from. The "_settings" set name is taken by this
file: `LinkSetLibrary.Names()` skips everything starting with an underscore.

`autodim\` is the "Auto Dimensions" button's folder, **separate from `dimensions\`** — that one
holds the cache of the family scan for dimension labels (a different button, a different meaning),
and the two must not be mixed. Both work like `links\`: `_settings.txt` is rewritten whenever the
window closes, the templates themselves only through the "Save template" button.

`autodim\<template name>.txt` — a saved set of chains. One line per entity, fields separated by a
vertical bar: `BOUNDARY | Finish|Center|CoreBoundary|CoreCenter`, `SIDE | Inward|Outward`,
`THICKNESS | Yes|No` (whether the end ticks pick up the thickness of adjoining walls),
`LABELS | Leader|Inline` (whether small labels get pulled out onto a leader),
`CHAIN | number | offset_mm | kind (Overall|OpeningEdges|OpeningCenters|Partitions|WallFaces|Combined) | dimension type name`.
The chain number is only there to make the file readable by eye — the order of the `CHAIN` lines
is what sets the chain order. The dimension type is stored by name, not `Id`: a template must
travel between projects, where types have their own `Id`s. Templates saved by an earlier version of
this format used Russian keys and values (`ГРАНИЦА`, `Внутрь`, and the like); those are still
accepted on read so that templates already saved keep working, but only the English keys and values
above are ever written from now on.

`autodim\_settings.txt` — the "Auto Dimensions" window's settings: `KEY = value` — `TEMPLATE` (the
last template's name), `BOUNDARY`, `OUTWARD`, `REMOVE_PREVIOUS`, `ADJACENT_THICKNESS`,
`MOVE_SMALL_TEXT`. The "_settings" name is taken by this file, by the same rule as `links\`.

`basefile\_settings.txt` — the "Base File" window's settings: `KEY = value`. `MODEL` — the last
coordination model, in the same line format as in a link set (`FILE | path`, `SERVER | RSN://…`,
`CLOUD | region | GUID | GUID | name`, the last field being the project workset): the format is
read and written by `LinkSetLibrary.Format`/`Parse`, no second parser is needed for it. Then
`PLACEMENT`, `SITE` (the site name), `LINK_WORKSET` (the workset the link goes into; default
`01_Link_BM`, empty means active), `WORKSET` (the workset to switch to) and one flag per step —
`ACQUIRE`, `RENAME`, `PIN`, `ACTIVATE`, `MONITOR`. The base-file-guessing settings live here too:
`AUTO_PICK` (`1`/`0` — whether to search for it when the window opens), `BASE_FOLDER` (the base
file folder, default `01_Base Model`; compared word by word) and `BASE_CODE` (the base model code
in the file name, default `BM`). The last two are deliberately absent from the window: they are set
once in a lifetime, and the window is already crowded. The building's position in the name is not
here at all — it is shared with the "Building Kit" and lives in `links\_settings.txt`
(`KIT_TOKEN`). Rewritten whenever the window closes. There are no sets here like in `links\`: there
is only one base file. The window reads and adds to Revit Server names in `links\_settings.txt` —
the user should not have to type them in twice.

## Conventions

- All user-visible text, XML doc comments and code comments are **in English**. Identifiers are in English too.
- A comment explains **why**, not what a line does; there are few of them and they matter — keep that bar.
- Errors are never swallowed silently and never thrown to the surface: they are gathered into a
  list and shown to the user in a final `TaskDialog` / `MessageBox`.
- A command only touches the open document: in a family — the family itself, but not what is
  nested inside it; in a project — the project itself, but not the families loaded into it and not
  what links contain. "Link Manager" does not break this rule: it sets up and reloads links in the
  open project, without touching the linked models themselves.
- **Wherever a button deletes**, an empty filter must not mean "select everything" — a guard
  against accidental deletion. Where nothing is deleted, the rule does not apply: "Accept Changes"
  checks everything applicable right away, because that is exactly what it is asked to do (see
  "Key decisions"). The second rule — "only what is shown gets applied" — holds in every window
  without exception: a row that leaves the table through a filter loses its check mark.

## Porting to another Revit version

The add-in currently builds for **Revit 2022, 2024 and 2025** from one source tree, with not a
single `#if`: for every API removed or deprecated in a newer version, a replacement exists across
every year. The year is a build key, `-p:RevitVersion=<year>` (defaulting to `2022`, set in
[Directory.Build.props](src/VladTools/Directory.Build.props)); it drives `RevitDir`,
`RevitAddinsDir`, `Product`, the **target framework** in
[VladTools.csproj](src/VladTools/VladTools.csproj) (see "Key decisions", TFM by year) and the
separate `bin\R<year>\` / `obj\R<year>\` folders (details and the related MSB3539/CS0579 build trap
are in "Build and run"). `RevitServerClient.ServiceVersion` needs no manual edit — `App.OnStartup`
sets it (see "Key decisions"). `build.ps1` with no arguments builds all three years at once,
skipping any that are not installed.

The full rundown for each move is in [CHECKLIST-Revit2024.md](CHECKLIST-Revit2024.md) and
[CHECKLIST-Revit2025.md](CHECKLIST-Revit2025.md). Briefly, what is left as a warning (not an
error) and does not need to be touched:

- **`ElementId.IntegerValue`** (`CleanupCommand.SafeName`, `RenameNestedFamiliesCommand`) — deprecated
  since 2024, but there is no replacement that also works in 2022: `ElementId.Value` only arrived
  in 2024. The `CS0618` on 2024/2025 is left in place deliberately, for as long as 2022 is needed too.
- **`WebRequest.Create`** (`JsonHttp.cs`) — deprecated on .NET 8 (`SYSLIB0014`, visible only when
  building for 2025); moving to `HttpClient` is a separate task, unrelated to compatibility.

Already done and no longer needing attention: `Definition.ParameterGroup` together with
`LabelUtils.GetLabelFor(BuiltInParameterGroup)` (`GroupName` in both delete-parameters commands) —
in Revit 2025 the `BuiltInParameterGroup` type itself is removed entirely; this was the one
compile error on the move to 2025 — replaced with `Definition.GetGroupTypeId()` +
`LabelUtils.GetLabelForGroup(ForgeTypeId)`, present and not deprecated in all three years.
`new ElementId(BuiltInCategory)` — **confirmed not deprecated** in any version, no need to touch it.

To add another year, the same way: build with `-p:RevitVersion=<year>` and watch for compile
errors, not just warnings (that is how every 2024 and 2025 spot was found). First, check
`RevitAPI.runtimeconfig.json` next to that year's `RevitAPI.dll` — that is exactly how the move to
2025's `.NET 8` was discovered; if the new year switches runtime again, the TFM condition in the
csproj (currently `net8.0-windows` when `>= 2025`) will need extending with another branch, not
rewriting. It is also worth re-checking `AutodeskSession` on the new year: it rests on the
undocumented `SSONET.dll`, whose method set has already changed between versions (four methods
`AutodeskSession` never called disappeared in 2024), but all six methods it needs are present in
2022, 2024 and 2025 so far. Revit 2023 added `Document.GetAllUnusedElements`, which could replace
the manual unused-family count in `CleanupCommand`, but that is a separate task, unrelated to compatibility.
