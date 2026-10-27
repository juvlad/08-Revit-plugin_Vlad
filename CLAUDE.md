# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Keeping this file up to date (a rule for Claude)

**Claude keeps CLAUDE.md up to date on its own — without a separate request from the user.**
The file is edited in the same session in which the code changed, before reporting the finished work.

Must be updated whenever:

| Change in the code                                                                                                                               | What to fix here                                                              |
| ------------------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------- |
| A ribbon button is added, removed or renamed                                                                                                     | "Buttons and commands", and "How a command is built" if needed                |
| A ribbon panel is added or renamed                                                                                                               | "What this is", "Buttons and commands"                                        |
| A new class appears in`Infrastructure/` or a new shared abstraction                                                                            | "Code map", "Key decisions"                                                   |
| The format or path of a file under`%AppData%\VladTools\` changes (`formulas.txt`, `names.txt`, `dimensions\`, `links\`, `basefile\`, `coordination\`) | "Storing user settings"                                                       |
| `build.ps1`, `VladTools.csproj`, `VladTools.addin`, the Revit version or the TFM changes                                                   | "Build before reporting", "Build and run", "Porting to another Revit version" |
| A test project or a linter appears                                                                                                               | "Build and run" — add the commands to run them                               |
| Any of the "Conventions" is broken or deliberately changed                                                                                       | "Conventions" — rewrite the rule, don't leave the discrepancy standing       |

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
editor** (`.rfa`); "Project" — seventeen buttons, working **only in a project** (`.rvt`, and a project
template `.rte` — Revit opens both as the same kind of document). The one exception is the "Sheets"
batch ("Several models…"), which opens its models itself and so also runs from a family or with no
document open at all. There is no test project — checking is done by hand, in Revit.

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
UI/ModelPicker.cs                 picking a model from all four sources, or a folder; shared by "Link Manager", "Base File"
                                  and every batch's model list
UI/ModelKitRow.cs                 a row of the "Building Kit" table: discipline, model, where it was found
UI/ModelKitWindow.cs              the "Building Kit" window: building, disciplines, where to search, the found-models table
UI/GridBuilder.cs                 shared table column assemblies: a text column and a check box column
UI/BaseFileWindow.cs              the "Base File" window: one model and a list of what to do with it
UI/CoordinationChangeKind.cs      the kinds of discrepancy against the coordination file (Position/Name/Missing/New/…) + captions
UI/CoordinationChangeRow.cs       a row of the "Accept Changes" table + the ready-made edit
UI/CoordinationScan.cs            the result of comparing against one link: rows + how many elements monitor it
UI/AcceptCoordinationWindow.cs    the "Accept Changes" window: choosing a link, the discrepancy table
UI/BatchModelRow.cs               a row of a batch's model list: the model itself and a check box
UI/ModelListPanel.cs              a batch's model list: sources (Folder… in all three stores, Files…, Revit Server…, BIM360…,
                                  by GUID), saved sets, the table with check boxes; shared by both batch windows
UI/BatchCoordinationWindow.cs     "Accept Changes → Other models…": the model list, which worksets to open, what may be changed
UI/ScheduleInfo.cs                a snapshot of one schedule for the window + the ScheduleChooser call-back
UI/ScheduleAction.cs              what to do when the project already holds a schedule of that name + captions
UI/ScheduleRow.cs                 a row of the schedule table: the snapshot, the check box, the action, the state
UI/ScheduleSetScan.cs             the result of reading/filling/emptying a set: contents + what was added + failures
UI/ScheduleChooserWindow.cs       "which of this model's schedules go into the set" — shown with the model open
UI/ScheduleLibraryWindow.cs       the "Schedule Library" window: the set, where to fill it from, the insert table
UI/CategoryInfo.cs                a snapshot of one bindable category of the open project, for the category picker
UI/ParameterGroupInfo.cs          a snapshot of one Revit parameter group (ForgeTypeId + label), for the "Group" column
UI/SharedParameterInfo.cs         a snapshot of one definition read out of a shared parameter file
UI/ParameterStatusInfo.cs         what "Parameter Sets" found comparing one entry against the open project
UI/ParameterSetRow.cs             a row of the parameter-set table: the entry itself, the check box, the state
UI/WorksetInfo.cs                 a snapshot of one user workset: GUID + name + contents + owner + open/active
UI/WorksetElementAction.cs        what happens to the elements in a removed workset (Move/Delete)
UI/ProjectWorksetRow.cs           a row of the workset table in the "Worksets" window (not UI/WorksetRow.cs — that
                                  one is a workset *name* shared across links in "Link Manager")
UI/WorksetsWindow.cs              the "Worksets" window: the project's worksets, the check boxes, the question
                                  about their contents
UI/CategoryPickerWindow.cs        "Choose categories…" — a small checklist opened from a parameter-set row
UI/SharedParameterPickerWindow.cs "Add from shared file…" — picking definitions out of a shared parameter file
UI/ParameterSetWindow.cs          the "Parameter Sets" window: the set, "Add from shared file…", the apply table
UI/TranslationText.cs             a snapshot of one distinct Russian text: counts and a description of where it is used
UI/NameScope.cs                   a set of names Revit keeps unique (views of one kind, types of one family…), for the clash check
UI/TranslationRow.cs              a row of the "Translate" table: check box, original, translation, state
UI/TranslateWindow.cs             the "Translate" window: one row per text, Export…/Import…, the dictionary, the clash check
UI/WorksetCategoryInfo.cs         a snapshot of one category in one workset: movable ids, ids in model groups, locked ids
UI/WorksetCategoryScan.cs         the result of reading a scope (the selection or the whole model) + what was left out
UI/WorksetCategoryRow.cs          a row of the "Move to Workset" table: check box, workset, kind, category, state
UI/MoveToWorksetWindow.cs         the "Move to Workset" window: the scope, the "Show" list, the table, the target workset;
                                  takes UI/WorksetInfo.cs snapshots as the target list
UI/ExcelScheduleRow.cs            a row of the "Export to Excel" list: a ScheduleInfo + its check box
UI/ExcelExportWindow.cs           the "Export to Excel" window: the schedules, the layout of the sheet, open when done
UI/ExcelImportPreview.cs          the import window's data: ExcelImportRequest (what the window asks for), the preview,
                                  its columns, rows and cells with an ExcelCellState each — plain DTOs, no Revit types
UI/ExcelImportWindow.cs           the "Import from Excel" window: sheet → schedule, how rows are paired, a source-column
                                  drop-down under every heading, the highlighted preview; previews come from a call-back
UI/SheetInfo.cs                   a snapshot of one sheet: number, name, its Project Browser folders, one line per thing placed
                                  (dated), the latest change found + its note, hidden by the browser's filter, SheetCounts
                                  (views/legends/schedules/other); plus SheetTracking — where the history stands
UI/SheetListRow.cs                a line of the "Sheets" table: a browser folder (foldable, with a sheet count) or a sheet;
                                  Tree — the sheets laid out as these lines, for the window and a batch's reports alike
UI/SheetsWindow.cs                the "Sheets" window: the browser's folder tree laid flat, the "Changed" column, Find,
                                  Show (changed since the previous check / 7 / 30 days), Number, Open sheet, Export to
                                  Excel, Several models…
UI/SheetsBatchWindow.cs           "Sheets → Several models…": the sheet number, the model list and where the summary goes
UI/SheetNumberParameter.cs        the "Sheets" number: SheetNumberParameter (Revit's own / a shared one by GUID / one not
                                  shared, by name), SheetNumbering (what one model's numbers came out as), NumberedSheets,
                                  SheetParameterUse (a parameter on the project's sheets, how filled), SheetNumberSource
UI/SheetNumberWindow.cs           "Sheets → Number": Revit's own, or any parameter the sheets and titleblocks carry or a
                                  shared parameter file holds (Browse… for another) — the ones holding values first
UI/FlatTree.cs                    a tree laid flat into table rows: the one merge that folds without Clear(); shared by
                                  "Sheets" and "Views to Sheets"
UI/TitleblockInfo.cs              a snapshot of one titleblock type: "Family : Type" + the frame it draws, mm from the sheet origin
UI/PlaceableViewInfo.cs           a snapshot of one view for "Views to Sheets": folders, scale and what locks it, the views
                                  sharing its scale, size on paper, where it already stands, which views it lines up with
UI/SampleViewportInfo.cs          a snapshot of one viewport on a sheet: a possible sample for "Like a sample sheet"
UI/ViewCentering.cs               the four ways of positioning a view on its sheet (Viewport/CropRegion/ModelPoint/Sample) + captions
UI/ViewPlacementRow.cs            a line of the "Views to Sheets" table: a browser folder (a check box over its views) or a
                                  view with its scale cell
UI/ViewPlacementRequest.cs        what that window hands back: titleblock, margins (per titleblock type too), positioning,
                                  the sample, views with their sheet numbers (or a move) and scales
UI/ViewsToSheetsWindow.cs         the "Views to Sheets" window: the views in the browser's folders, scales, titleblock,
                                  margins, numbering, positioning, the sample, the sheet preview
UI/ScaleSheetInfo.cs              "Sheet Scale" snapshots: ScaleSheetInfo (a sheet, its folders, its drawing area),
                                  ScaleViewportInfo (a view on that sheet + its box), ScaleViewInfo (scale, template, dependents)
UI/ScaleTemplateMode.cs           what to do with a view template that sets the scale (Release/ChangeTemplate/Leave) + captions
UI/SheetScaleRow.cs               a line of the "Sheet Scale" table: a browser folder, a sheet (both foldable, a check box
                                  over their views) or a view
UI/SheetScaleRequest.cs           what that window hands back: the scale, the template choice, the checked views
UI/SheetScaleWindow.cs            the "Sheet Scale" window: sheets and their views in the browser's folders, "at … to …",
                                  the template choice, what changes along
UI/CopyParameterInfo.cs           "Copy Parameters" snapshots: CopyScope (selection / active view / model), CopyCategoryInfo,
                                  CopyParameterInfo (key, name, instance/type, kind, value kind, how many have it / can take it)
UI/CopyParameterPreview.cs        CopyParameterRequest (what the window asks for), CopyPreviewRow + CopyRowState (one line:
                                  source, target now, target after, Write/Same/Skipped/Blocked), CopyParameterPreview
UI/CopyParametersWindow.cs        the "Copy Parameters" window: scope, category, "Copy from" / "Copy into" lists, the four
                                  options, the conversion rule, the preview; everything Revit comes through three call-backs
UI/LinkPathInfo.cs                a snapshot of one top-level link for "Link Updater": type id, where it points (LinkEntry +
                                  as Manage Links shows it), load state, from BIM360 or not (cloud, cloud resource, Desktop
                                  Connector), and what stops LoadFrom (held / closed workset)
UI/LinkUpdateRow.cs               a row of that table + LinkTarget (one server model it could point at: RSN path, same name
                                  or loose, picked by hand, distance); Picked — the combo's null-proof view of Target
UI/LinkUpdaterWindow.cs           the "Link Updater" window: the server search (climbing from the open model's folder,
                                  "Search wider", "Another folder…", "Choose on server…"), the per-row drop-down, the checks
Infrastructure/Ribbon.cs          GetOrCreatePanel + AddPushButton
Infrastructure/AlwaysAvailable.cs an IExternalCommandAvailability saying "yes" — a button pressable with no document open
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
                                  plus ModelWorksets → ModelWorkset — a model's worksets read without loading it
                                  (a cloud one from the session, or by a brief open where the caller
                                  allows it — the batch does, "Link Manager" does not); shared by both
                                  plus Explain — an exception as a report line, naming the call that threw
                                  plus VisiblePath — the central model's path, the same for every local copy; also the
                                  key of the "Sheets" change history
Infrastructure/ModelStore.cs      a model folder across the three stores: what is inside, what is above
Infrastructure/ModelKit.cs        guessing a link kit by building number and discipline folders
Infrastructure/LinkSetLibrary.cs  saved link sets, %AppData%\VladTools\links\<name>.txt
Infrastructure/LinkPreferences.cs the "Link Manager" window settings (%AppData%\VladTools\links\_settings.txt)
Infrastructure/BaseFilePreferences.cs  the "Base File" window settings (%AppData%\VladTools\basefile\_settings.txt)
Infrastructure/BaseFileFinder.cs  guessing the base file from the building in the open model's name
Infrastructure/CoordinationCatalog.cs  comparing the project's grids and levels against the coordination file
                                  plus Verifier — the same comparison run again after the commit, to check the edit landed
Infrastructure/DatumUpdate.cs     a ready-made edit for a single grid or level: rotation, translation, elevation, name
                                  plus DatumVerdict — what a re-read after the commit says (Aligned/Off/Unknown)
Infrastructure/BatchCoordination.cs  opening somebody else's model with two worksets, a local copy, synchronising back
                                  plus OpenToRead — the read-only road: detached, every workset closed, nothing written back
                                  plus BatchModel — one opened model of the batch
Infrastructure/CoordinationPreferences.cs  the "other models" window settings (%AppData%\VladTools\coordination\_settings.txt)
Infrastructure/DisciplineCatalog.cs  a discipline code (OV, VK…) from a model name → a project workset "01_Link_OV", by tokens
Infrastructure/RoomSide.cs        one straight side of a room boundary: direction, inward normal, walls
Infrastructure/RoomSideBuilder.cs a room's GetBoundarySegments → a list of RoomSide (merging collinear segments)
Infrastructure/DimensionReferenceCollector.cs  a side + a chain kind → a ReferenceArray for NewDimension, with a wall-face cache
Infrastructure/DimensionSampleReader.cs  sample dimensions → a chain template (a heuristic, like FormulaParser)
Infrastructure/DimensionTextLayout.cs    pulling short labels of a chain out onto a leader (also a heuristic: the API gives no text width)
Infrastructure/DimensionTemplate.cs      auto-dimension templates + %AppData%\VladTools\autodim\<name>.txt and _settings.txt
Infrastructure/AutoDimensionMarker.cs    ExtensibleStorage: the "this dimension was placed by the button" mark, against duplicates
Infrastructure/ScheduleLibrary.cs        the schedule cache: sets as .rvt files, background documents, copying between them
Infrastructure/SchedulePreferences.cs    the "Schedule Library" window settings (%AppData%\VladTools\schedules\_settings.txt)
Infrastructure/ParameterSetLibrary.cs    saved parameter sets, %AppData%\VladTools\parameters\<name>.txt; also ParameterEntry, ParameterBindingKind
Infrastructure/ParameterSetPreferences.cs  the "Parameter Sets" window settings (%AppData%\VladTools\parameters\_settings.txt)
Infrastructure/SharedParameterFile.cs  reading any shared parameter file's definitions (the SharedParametersFilename
                                  swap); shared by "Parameter Sets" and "Sheets"
Infrastructure/TextSite.cs        one place holding typed text — a name, a parameter value, a column heading, a header
                                  cell, a schedule filter value, a view filter rule, a text note, a workset name — with
                                  its own read and write; FixedTextSite for what the API cannot change
Infrastructure/TextSiteCollector.cs  finds every TextSite with Cyrillic in the open project, plus the NameScopes
Infrastructure/TranslationDictionary.cs  the dictionary (%AppData%\VladTools\translate\dictionary.txt) and the
                                  TAB-separated export/import format; also Normalize and HasCyrillic for both sides
Infrastructure/TranslatePreferences.cs  the "Translate" window settings (%AppData%\VladTools\translate\_settings.txt)
Infrastructure/Xlsx.cs            writing and reading .xlsx with the framework's zip + XML only: XlsxSheet/XlsxWriter out,
                                  XlsxReader → XlsxReadSheet/XlsxValue back (shared strings, numbers, dates, strict namespaces);
                                  shared by "Excel" and "Sheets" (row groups — Excel's +/− outline — are for the latter;
                                  number cells, the highlight and hyperlinks for the "Sheets" batch summary)
Infrastructure/ScheduleTable.cs   a schedule read out of Revit: ScheduleColumn (what a column shows, whether it may be
                                  written), ScheduleDataRow (texts + the elements behind the row), the rows as drawn;
                                  the row → elements probe lives here (see "Key decisions")
Infrastructure/ScheduleWorkbook.cs  how a schedule sits on a sheet, both ways: the export layout (hidden key row/column,
                                  the hidden "VladTools" sheet) and ExcelSheetSource — a sheet as the import reads it
Infrastructure/ScheduleImport.cs  ScheduleImportPlanner: pairs sheet lines with schedule rows and judges every cell →
                                  ScheduleImportPlan (the preview + PlannedWrites); ScheduleValues — finding a column's
                                  parameter on an element or its type, parsing/comparing a value in the column's units
Infrastructure/ExcelPreferences.cs  the "Excel" button settings (%AppData%\VladTools\excel\_settings.txt)
Infrastructure/SheetPreferences.cs  the "Sheets" windows' settings, the batch's model list too (%AppData%\VladTools\sheets\_settings.txt)
Infrastructure/SheetReport.cs     the "Sheets" batch summary: SheetModelResult (one model read, or why not) → the "Models" and
                                  "All sheets" Excel sheets; the reports folder and file names
Infrastructure/SheetNumbers.cs    the number "Sheets" shows: Revit's own, or a parameter off the sheet, else off its
                                  titleblock instance, else its type; Revit's own when no sheet has it; Uses — every
                                  candidate on the sheets and titleblocks with how many sheets get a value from it
Infrastructure/SheetHistory.cs    the "Sheets" change history: per sheet and per placed view a fingerprint and the check
                                  that last found it changed (%AppData%\VladTools\sheets\history\<model>_<hash>.txt)
Infrastructure/ViewFingerprint.cs a digest of a view: its VersionGuid, template, primary, and every element it owns —
                                  what SheetHistory compares from one check to the next
Infrastructure/ProjectBrowser.cs  what the Project Browser says about an element: folders, filter, sort value, natural
                                  order; shared by "Sheets" and "Views to Sheets"
Infrastructure/ViewsToSheetsPreferences.cs  the "Views to Sheets" settings, margins per titleblock type
                                  (%AppData%\VladTools\viewstosheets\_settings.txt)
Infrastructure/ViewScale.cs       who decides a view's scale: whether a template controls it, releasing it, the primary
                                  view that holds a dependent's scale; shared by "Views to Sheets" and "Sheet Scale"
Infrastructure/SheetScalePreferences.cs  the "Sheet Scale" settings (%AppData%\VladTools\sheetscale\_settings.txt)
Infrastructure/ParameterValueConverter.cs  ParameterKey — a parameter as the same one across families (built-in id / shared
                                  GUID / name, plus instance or type) and found on any element; SourceValue — a value read
                                  once in every form; ParameterValueConverter — converting it into a target of another
                                  storage type, writing, the read-back check, the rule in words (see "Key decisions")
Infrastructure/CopyParameterPreferences.cs  the "Copy Parameters" settings (%AppData%\VladTools\copyparams\_settings.txt)
Infrastructure/ServerModelIndex.cs  ServerModel + ServerModelIndex: a part of Revit Server walked breadth first (FolderLimit,
                                  never the same folder twice), models found by exact name or loosely, tree distance
Infrastructure/LinkUpdaterPreferences.cs  the "Link Updater" settings (%AppData%\VladTools\linkupdater\_settings.txt)
Resources/*.png                   16/32 icons, embedded in the DLL
```

## Buttons and commands

| Panel                                                                                                                                                                                                                                                                                                                                       | Button                     | Command                            | What it does                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------- | ---------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Families                                                                                                                                                                                                                                                                                                                                    | "3D Thumbnail"             | `Create3DThumbnailCommand`       | creates/reconfigures a`View3D` named `3D Thumbnail` (annotations off, connectors hidden, `Realistic`, `Fine`) and opens it — Revit takes the active view as the family preview                                                                                                                                                                                                                                                                                                                                           |
| Families                                                                                                                                                                                                                                                                                                                                    | "Delete Parameters"        | `DeleteSharedParametersCommand`  | a window with every**shared** parameter of the family (dimension labels hidden by default); the checked ones are deleted via `FamilyManager.RemoveParameter`                                                                                                                                                                                                                                                                                                                                                              |
| Families                                                                                                                                                                                                                                                                                                                                    | "Add Formulas"             | `AddFormulasCommand`             | a "parameter — formula" window from a saved list; the checked ones are assigned via`FamilyManager.SetFormula`                                                                                                                                                                                                                                                                                                                                                                                                                  |
| Families                                                                                                                                                                                                                                                                                                                                    | "Rename Nested"            | `RenameNestedFamiliesCommand`    | a "find and replace" window over the names of nested`Family` and their `FamilySymbol`; the checked ones get `Element.Name`                                                                                                                                                                                                                                                                                                                                                                                                  |
| Project                                                                                                                                                                                                                                                                                                                                     | "Delete Shared Parameters" | `DeleteProjectParametersCommand` | a window with every`SharedParameterElement` of the project ("bound / unbound" filter + a button to scan families for dimension labels); the checked ones are removed via `doc.Delete`, taking the binding and the values with them                                                                                                                                                                                                                                                                                            |
| Project                                                                                                                                                                                                                                                                                                                                     | "Cleanup"                  | `CleanupCommand`                 | a window with eight check boxes and "Select all": unused families, sheets, filters, views, legends, schedules, model groups (ungrouped), unused group types — everything checked runs in one transaction                                                                                                                                                                                                                                                                                                                         |
| Project                                                                                                                                                                                                                                                                                                                                     | "Link Manager"             | `LinkManagerCommand`             | a window with a model list (files, Revit Server, BIM360, a saved set): placement and the worksets inside links — one for the whole batch, the project workset — one per row                                                                                                                                                                                                                                                                                                                                                     |
| (can be guessed from the discipline code in the model name). The "Building Kit…" button assembles the list itself: it finds the models of every discipline of the same building through the project's folders. New links are created in a single transaction and pinned; existing ones are reloaded with`LoadFrom` outside a transaction |                            |                                    |                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| Project                                                                                                                                                                                                                                                                                                                                     | "Base File"                | `BaseFileCommand`                | links the coordination file (guessed on its own from the building number in the open model's name — a file, Revit Server, or BIM360) and immediately sets up the project against it:`Document.AcquireCoordinates`, the site name, a pin, switching to a workset (creating it if it does not exist); as its last step it opens "Copy/Monitor → Select Link" mode — the actual copy-monitoring is something the Revit API cannot do                                                                                            |
| Project                                                                                                                                                                                                                                                                                                                                     | "Auto Dimensions"          | `AutoDimensionCommand`           | from a catalogue of chain kinds (overall / openings / opening centres / partitions / wall faces / all combined) places dimensions along the sides of the selected rooms; the set of chains is gathered with the "Take a sample…" button (parsing dimensions already placed by hand) or by hand, and saved as a template. Three check boxes apply to the whole set: capture the thickness of adjoining walls, pull small labels out onto a leader, remove what was placed before                                                  |
| Project                                                                                                                                                                                                                                                                                                                                     | "Accept Changes"           | `AcceptCoordinationCommand`      | compares the grids and levels monitoring the coordination file against the file itself and applies the checked ones in a single transaction: a grid by rotating about its own midpoint plus a sideways shift, a level by a new elevation, plus renaming to follow the link. What is gone from the file can also be deleted, but only once the window's own box turns those rows on and they are checked by hand; new link elements are only shown. The "Other models…" button does the same for a batch of models nobody has open: each is opened in turn with only the grid and base-file worksets in it (a local copy for a workshared one), its grids are moved, and it is synchronised back — never deleting anything                                                     |
| Project                                                                                                                                                                                                                                                                                                                                     | "Schedule Library"         | `ScheduleLibraryCommand`         | carries schedules from one model into another: a set is taken out of a base model once (the open project, a file, Revit Server, BIM360) into a small .rvt in the Windows profile, and from then on inserted into any model with`ElementTransformUtils.CopyElements` out of that file. Where the project already holds a schedule of that name, the table asks per row: skip, replace (the replacement goes back onto the same sheets) or insert alongside under a free name                                                     |
| Project                                                                                                                                                                                                                                                                                                                                     | "Worksets"                 | `WorksetsCommand`                | shows every user workset of the project — what it holds, whether it is open, who owns it — and removes the checked ones in one batch. The question Revit's own dialog asks per workset is asked once for the whole batch, under the table: move the elements standing in them into another workset (`DeleteWorksetOption.MoveElementsToWorkset`) or delete them with it (`DeleteAllElements`). The worksets are checked out before the run; a closed workset is flagged rather than counted                                                                                                                                                                                                                              |
| Project                                                                                                                                                                                                                                                                                                                                     | "Parameter Sets"           | `ParameterSetCommand`            | applies a named bundle of shared parameters to the open project: for every parameter — instance/type, categories, parameter group, "varies across groups" — built once from a shared parameter file (`Application.OpenSharedParameterFile`) and kept in the Windows profile. Opening the window compares the set against the project by GUID: what is missing is bound (`BindingMap.Insert`), what is already bound but differs is brought in line with the saved settings (`BindingMap.ReInsert`) rather than duplicated |
| Project                                                                                                                                                                                                                                                                                                                                     | "Translate"                | `TranslateCommand`               | replaces the Russian typed into a project or template with English through one dictionary: every distinct text with Cyrillic in it is one row (with where it is used), and a translation replaces it in every place at once — element names (views, templates, sheets, schedules, families, types, materials, filters, levels, worksets…), text parameter values, schedule column headings, header cells and filter values, view filter rule strings, text notes — in one transaction. Translations come from the dictionary (`%AppData%\VladTools\translate\`), from a TAB-separated file ("Export…" for a translator or an AI, "Import…" back), or by hand; the window catches name clashes and forbidden characters first. What the API cannot change (shared parameter names, line styles, subcategories) is shown and reported, not skipped |
| Project                                                                                                                                                                                                                                                                                                                                     | "Move to Workset"          | `MoveToWorksetCommand`           | finds elements standing in the wrong workset — above all model elements in Revit's own worksets (project standards, views, families), where an AI tool or a script left them — and moves the checked ones into one user workset by writing `ELEM_PARTITION_PARAM`, in one transaction. The table is one row per workset × category, over the current selection or the whole model; rows in Revit's own worksets come first and are checked. The elements are checked out first (held / out-of-date ones are named, not attempted), every one is read back after the commit, and "Select in model" selects the checked elements instead of moving them |
| Project | "Excel" | `ExcelScheduleCommand` | asks which way (a `TaskDialog` with two command links). **Export**: the checked schedules into one .xlsx, a sheet each, every value as text exactly as the schedule shows it — laid out "as in Revit" (title, headings, group lines, totals) or as a plain table with filters; every element row carries a hidden key to its elements. **Import**: a sheet of a workbook into a schedule — rows paired by those elements, by a key column or by order (the ModPlus way); a preview with every changed cell highlighted (yellow — will be written, red — cannot be, with the reason); the checked rows are written into the elements' parameters (instance → every element of the row, type → the type) in one transaction, read back, and the changed rows selected in the opened schedule |
| Project | "Sheets" | `SheetsCommand` | a table of every sheet — "A101 - Name" on the left, one line per thing placed on it on the right (views by kind, legends, schedules, panel schedules, images, CAD) — in the folders and the order of the Project Browser's current sheet organization (`BrowserOrganization.GetFolderItems`, `SortingParameterId`/`SortingOrder`); sheets the browser's filter hides and placeholder sheets get folders of their own at the end. Between them, "Changed": the day of the check that last found a change in the sheet or a view on it, and every view line carries its own ("— changed 2026-10-07") — Revit keeps no such date, so every opening of the window is a check against the previous one (`SheetHistory`, `ViewFingerprint`); "Show" narrows to what changed since the previous check or in the last 7/30 days. A click folds a folder, a double click opens the sheet (once the window has closed). "Number" picks what a sheet's number is: Revit's own Sheet Number, or any parameter the sheets and titleblocks carry or a shared parameter file holds ("Browse…" for any file; a shared one kept by GUID, any other by name), read off the sheet, else off its titleblock instance, else its type (`SheetNumbers`); the table's sheet column is then headed "<parameter> - Sheet name" and Revit's own number is shown nowhere, and a model with no sheet or titleblock holding the parameter falls back to Revit's, saying so. "Export to Excel…" writes the table as the search and "Show" leave it (folding does not narrow it) into one .xlsx sheet — Number (headed by the parameter's name), Sheet name, Changed, On the sheet — whose folders are Excel row groups. "Several models…" (also offered from a family, a project with no sheets, and with no document open — `AlwaysAvailable`) does the same for a batch from `ModelListPanel` (a folder in any store, files, Revit Server, BIM360, a saved set): every model is opened detached with every workset closed (`BatchCoordination.OpenToRead`), read exactly as the window reads it (the change check only where it opened the model itself — a detached copy just reads the history), its table written into a report of its own, and a summary (`SheetReport`: "Models" with counts and a link to each report, "All sheets" across every model with notes — nothing placed, placeholder, hidden, number also in another model) written next to the folder of reports. Read-only — no transaction; the history is a file in `%AppData%` |
| Project | "Views to Sheets" | `ViewsToSheetsCommand` | one new sheet per checked view (`ViewSheet.Create` of the titleblock type chosen in the window, named after the view, numbered prefix + counter, skipping taken numbers) and the view on it (`Viewport.Create`), at the scale chosen per view (`View.Scale`), centred in the titleblock's drawing area — the titleblock's measured frame minus margins kept per titleblock type. The views are picked in a tree laid out like the Project Browser's view organization (folders, then the view-type groups); a folder's box takes its views. Four ways to position, asked in the window: the whole viewport with its title (`GetBoxOutline` + `GetLabelOutline`), the crop region, one model point in the same place on every sheet for views looking the same way, or like a sample sheet — where a viewport placed by hand stands, for new sheets and for views already on sheets, which are then moved there (`SetBoxCenter`). Workshared: the views and viewports are checked out first. Everything in one transaction; every viewport and scale is read back after the commit, and the report lists sheets where the view sticks out of the drawing area |
| Project | "Sheet Scale" | `SheetScaleCommand` | one new scale (`View.Scale`) for the views standing on the checked sheets — the sheets selected in the Project Browser start checked, every view on them but legends — laid out like the browser's sheet organization, a sheet's box taking its views. "Change the checked views at 1:100 to 1:200" narrows what can be checked to one scale. A view whose template sets the scale is handled as asked in the window: the template stops setting it (`SetNonControlledTemplateParameterIds`, every other view keeps its scale), the template gets the new scale (every view using it follows), or the view is left alone. A primary and its dependents change together; the primary is the one changed. Workshared: views, viewports and templates are checked out first. One transaction; the middle of every viewport is put back where it stood (`SetBoxCenter`); every scale and viewport is read back after the commit, and the report lists sheets where a view now sticks out of the drawing area |
| Project | "Copy Parameters" | `CopyParameterValuesCommand` | writes the value of one parameter into another on every element of one category — the selection, the active view (`FilteredElementCollector(doc, viewId)`) or the whole model; the categories are those with `AllowsBoundParameters`. Both lists are the category's parameters read off one element of each type and that type (`GetOrderedParameters`), keyed across families by `ParameterKey`; read-only ones (reporting, computed, formula-driven) are sources only. The two may store different kinds of value — `ParameterValueConverter` turns a number into text as Revit shows it, text into a number the way Revit parses a typed value (`UnitFormatUtils.TryParse`), anything into Yes/No, a length into a plain number by its displayed figure. A preview line per element (per type for a type parameter, whose elements in scope must agree) shows source, target now, target after and why not; options: only empty targets, an empty source empties a text, no unit symbol, the first number out of a text. Workshared: checked out first. One transaction; every value is read back after the commit |
| Project | "Link Updater" | `LinkUpdaterCommand` | points the model's top-level Revit links at Revit Server — made for a move from BIM360: for every link, a model of the same name on the server the open model lies on (`LinkCatalog.Host`), found by reading the server through its REST service (`ServerModelIndex`) from the open model's own folder, climbing a level at a time while a link is still missing, never onto the server root by itself; "Search wider", "Another folder…" (any server) and "Choose on server…" (one link, by hand) go further. One model of exactly the name is checked; several (nearest preselected), a loosely matching name or a file link are offered unchecked. Each checked link is reloaded with `RevitLinkType.LoadFrom` from its `RSN://` path (a link Revit keeps as a cloud resource falls back to the `ExternalResourceReference` overload) — the same element, so placement, pins, worksets, view overrides and Copy/Monitor stay; the worksets inside the link go in as `null` ("as the link had them"), are compared by name with what was open before and put right by a second load if Revit did not keep them. Workshared: the link types are checked out first; held / closed-workset links have no check box. No transaction — `LoadFrom` clears the undo history; every path is read back after the reload |

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

**Exception — `WorksetsCommand`, step 5.** Everything else is the same, but the batch is not one
transaction: each workset is deleted in a transaction of its own, all of them inside a
`TransactionGroup` that is `Assimilate()`d at the end. Autodesk's documentation for `DeleteWorkset`
names a **transaction** failure as a possible outcome ("Deleting all open views in a project is not
allowed") — a failure that rolls the transaction back rather than throwing on one item, so in a
shared transaction one bad workset would take every other deletion with it, after the user had
already confirmed. Assimilating the group buys back exactly what the batch rule was protecting: the
whole run still undoes with one Ctrl+Z. On top of that, two phases run **before** the group opens —
the checkout and the step off the active workset (see "Key decisions"). Do not fold this back into a
single transaction.

**Exception — `ExcelScheduleCommand`, steps 3–4.** One button, two directions: before any window a
`TaskDialog` with two command links asks "Export to Excel" or "Import from Excel". The import's data
snapshot is not taken once before the window: the window asks for a preview through a
`Func<ExcelImportRequest, ExcelImportPreview>` every time the sheet, the schedule or the pairing
changes, and the command's side of that call-back reads the schedule (a transaction of its own, always
rolled back — see "Key decisions") while the modal window is open, caching it per schedule. What the
window hands back is the preview the user confirmed; the command keeps the `ScheduleImportPlan` behind
each preview in a dictionary and writes from that. Steps 5–8 are as usual, plus a read-back after the
commit. The export has no transaction at all — only the rolled-back reads.

**Exception — `SheetsCommand`, steps 2 and 4–7.** It only reads: no transaction, no batch, no success
report. The window closes for one of three things it does not do itself, and `SheetsWindow.Action`
says which: open a sheet (a sheet opened behind a modal window could not be looked at — the command
sets `UIDocument.ActiveView` after `ShowDialog` returns), export (the window hands back the lines
as shown and a path; the command writes, like the "Excel" export) or the batch ("Several models…").
A dialog appears only when the write or the opening failed, or when the file was not opened in Excel.
Step 3 does one thing besides reading: the snapshot is also the change check — the history file is
compared and rewritten **before** the window opens, so opening is the check whatever the window is
closed with (see "Key decisions"). It is a file in `%AppData%`, never the model. The snapshot is
also taken in two layers: `Collect` reads everything but the number into a `Reading`, and
`Reading.Number` numbers and orders the sheets on top — the window's "Number" button asks for that
again through a call-back while it is open. Do not turn the button into closing and reopening the
window: a second `Collect` would be a second check the same day, and it would find nothing changed —
wiping out what the first one had just found.
Step 2 is not a refusal here: with no document open (the button names `AlwaysAvailable`, so Revit
lets it be pressed) the command goes straight to the batch window, and a family or a project with no
sheets gets a `TaskDialog` that says so **and** offers "Several models…". The batch itself is steps
3–8 once per model, the way the "Accept Changes" batch runs them: each model in a `try` of its own,
closed in `finally`, a failure a line in the summary and the report, never the end of the run; the
report is a `TaskDialog` with links to open the summary or the reports' folder.

**Exception — `ViewsToSheetsCommand`, steps 3 and 5.** The snapshot needs a transaction: every
titleblock type is measured on a sheet made for it in a transaction that is always rolled back (see
"Key decisions"). Step 5 is one transaction as usual, but with passes inside it — create everything,
regenerate, measure, move — and, for the crop-region, model-point and sample positioning, two
sub-transactions that are always rolled back (measuring the crop regions before the move and again
after it). Before the transaction, in a workshared model, the views and the viewports to be moved are
checked out, as in "Move to Workset". Do not turn the passes into a loop of "create, regenerate,
measure" per view: that is a regeneration per view.

**Exception — `CopyParameterValuesCommand`, steps 3–4.** The snapshot is not taken once before the
window: what it needs depends on what is picked in it, so the window gets three call-backs — the
categories of a scope, the parameters of a category, the preview of a request — and the command's
`Session` answers them while the modal window is open, caching each scope and each category's
parameters (a whole-model scope is a pass over every element). The plan behind the last preview is
kept, and the command writes the plan behind exactly the preview the window hands back
(`CopyParametersWindow.Confirmed`) — the same shape as the "Excel" import. Steps 5–8 as usual, with the
"Move to Workset" checkout before the transaction and a read-back after the commit.

**Exception — `LinkUpdaterCommand`, steps 5–6.** There is no transaction at all: the whole run is
`RevitLinkType.LoadFrom`, which refuses to run inside one and clears the undo history anyway — the
same as "Link Manager"'s reload of existing links. So the batch is a loop of `LoadFrom` calls, each link
in its own `try`, after the "Move to Workset" checkout of the link types; nothing is rolled back, and
the confirmation in the window says before the run that Ctrl+Z will not reach past it. Each link is
then read back (its path, whether it loaded, its worksets) instead of trusting the result code. Step 3
is shared with the window: the command hands over the links, the window reads Revit Server itself
(`ServerModelIndex` — plain HTTP, no Revit API) while it is open.

## Key decisions

- **The target framework depends on the Revit year.** Revit 2022 and 2024 are `.NET Framework 4.8`, Revit 2025 is `.NET 8` (Revit 2025's `RevitAPI.runtimeconfig.json` says `tfm net8.0` +
  `Microsoft.WindowsDesktop.App 8.0`; a net48 build will not load in it at all). So in
  `VladTools.csproj` the `TargetFramework` is chosen by a condition on `RevitVersion`
  (`net8.0-windows` when `>= 2025`, otherwise `net48`), rather than a single fixed string — the
  comparison is numeric, MSBuild converts both sides to a number itself. This is still **one build
  key**, not a project fork and not a (multiple) `TargetFrameworks`: the latter would mean two
  references to different `RevitAPI.dll` copies in one build. This is also why `UseWPF=true` is
  used on net8.0-windows instead of explicit references to `PresentationCore`/`PresentationFramework`/
  `WindowsBase`/`System.Xaml` (those only matter for net48 — on net8 such references do not resolve
  by name; the same goes for `System.IO.Compression`, the zip behind .xlsx, referenced on net48 only
  because net8 has it in the base library) and `MSBuildWarningsAsMessages=MSB3277`: `RevitAPI.dll` drags in about fifty neighbouring
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
  worked on the second try (the "took hold only on a second load" list, each line saying whether
  Revit ignored the first request or the list could only be read after loading — that also wipes
  undo history), did not
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
  `Document.GetCloudFolderId` — exactly the id Data Management knows the folder by — and
  `Document.GetProjectId` for the project (both `<since>2022</since>` in RevitAPI.xml, present in all
  three years). So neither the hub nor the project has to be searched for — the kit is assembled
  straight from the model's folder. **Trap:** the project id must not be assembled as `b.` + the
  cloud path's `GetProjectGUID()`: that GUID is the cloud-worksharing one, not necessarily the
  BIM360/ACC project's own, and on an EMEA project every folder request came back 404 — the kit and
  the base-file guess both read nothing. The path GUID stays only as a fallback when `GetProjectId`
  throws (`LinkCatalog.DataManagementProject`); the GUID *pair* of the path is still what a link is
  made from — that is a different id space from Data Management's. Nothing came of it (the project has never been
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
  is a bad trade. So the ordinary file dialog is made to answer with a folder
  (`ModelPicker.PickFileFolder`): `CheckFileExists = false`, `ValidateNames = false` and a placeholder
  name, "Choose this folder" — open the folder, press "Open", and the folder of the returned path is
  the answer; pointing at any model in it works as well. The placeholder is what makes a folder with
  no model directly in it choosable at all — a project root, which is exactly where "Folder…" with
  its subfolders, or the "Building Kit", is meant to start. Checked by the result, not the name: a
  path that turns out to be a folder is taken as it is.
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
  same code — do not set up a second copy of it. The same goes one level up for a batch's model list:
  `UI/ModelListPanel.cs` (sources, saved sets, the check-box table) is both "Accept Changes → Other
  models…" and "Sheets → Several models…"; each window only adds its own settings and run button.
- **"Folder…" takes every model in a folder, in any of the three stores** (`ModelListPanel.AddFolder`
  over `ModelStore.Children`), and the folders inside it only when asked — the question comes up only
  when there are any, since on Revit Server and BIM360 each folder is a request. The walk is breadth
  first and stops at `FolderLimit` (200) with a note; a folder that cannot be read is named, not fatal.
  On disk, Revit's own backups are left out — `Model.0001.rvt` next to a non-workshared model, the
  `*_backup` folders next to a workshared one, `Revit_temp`: a folder of models is never meant to
  include them, and a batch would open each of them as a model.
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
- **The button never touches new link elements**: setting up monitoring on one is something the
  Revit API cannot do at all (the same gap as the "Base File" button's copy-monitoring). They are
  shown in the window as a row with no check box and counted in the status line — staying silent
  about this would make "accepted everything" mean "everything is fine".
- **A move that raised no error is not a move that happened — "Accept Changes" checks the model
  afterwards.** `ElementTransformUtils.MoveElement` returning without an exception only says Revit
  took the request; a constraint or a lock on the datum, a group the element sits in, or a failure
  resolved at commit time can leave the grid exactly where it was. Worse, `Transaction.Commit()`
  **returns a status**, and Revit can roll the whole batch back there — the command used to ignore
  it and print the shift it had asked for, so the button looked as though it only "accepted the
  difference" instead of applying it (which is precisely the one thing it must never be mistaken
  for: in Revit's own dialog "Accept Difference" leaves the grid in place and just stops reporting
  it, while this button does what "Modify Grid" does). So: the commit status is checked, and a
  rollback wipes the whole success list and is reported as such; and every moved element is read
  back through `CoordinationCatalog.Verifier` and compared against the link once more. Three
  outcomes, the same shape as checking link worksets after loading — `Aligned` (goes into
  "Accepted"), `Off` (its own report section, "Revit did not carry these out", with what is left of
  the difference) and `Unknown` (kept apart, never folded into success). The twin in the link is
  found by the element's **current** name, after the rename pass. Renames need no such check:
  `Element.Name` throws when Revit refuses.
- **Deleting what is gone from the coordination file is the one destructive thing this button does,
  and every part of the path to it is deliberate.** Deleting a level takes everything standing on it
  with it, so the row is not merely "applicable": it is checkable only while the window's
  "Delete…" box is on (`CoordinationChangeRow.CanApply` gates `RemovalAllowed` on top of
  `CoordinationChangeKinds.CanApply`), it is never checked by default even then — that is the
  "wherever a button deletes, an empty filter must not mean select everything" rule, and turning
  the box on is not a selection — and the confirmation names every element with the count of what
  is hosted on it. That count comes from `Element.GetDependentElements(null)`, read **while
  scanning**, because the number is the whole basis for the decision and has to be on the table
  before the deletion, not in the report after it; it is worded as "up to", since an element bound
  to two deleted levels is counted by both. The report says what really went — `doc.Delete` returns
  the ids, and that is the only honest count. Deletions run **first** inside the transaction: a name
  held by an element about to go frees up for the rename pass, and nothing is moved a moment before
  being deleted (a row can never be both — a `Missing` element matched nothing, so it produces no
  `Position` or `Name` row).
- **In the "Accept Changes" window everything applicable is checked from the start, deletions
  excepted — and that is not an exception to the "an empty filter does not mean select everything"
  rule.** That rule guards against accidental deletion; for everything but the removals nothing is
  deleted here, it all rolls back with one Ctrl+Z, and the button is asked to accept the
  coordination-file changes all at once — that is the whole point of it. The "only what is shown
  gets applied" rule still holds as everywhere else: a row that leaves the table through the "Show"
  filter loses its check mark, and so does one whose check box is switched off with the "Delete…" box.
- **A batch of other models never edits a central file in place.** "Accept Changes → Other models…"
  (`BatchCoordination`) takes the path a person would take: `WorksharingUtils.CreateNewLocal` into a
  temporary folder, the edit in the local, `SynchronizeWithCentral` with everything relinquished,
  and the local swept away afterwards. A central model opened straight through `OpenDocumentFile`
  **is** editable and saveable — which is exactly why it must not be done here: a batch would be
  writing into the file the whole team synchronises with, with nothing to merge through and nothing
  to fall back on. Where a local cannot be made, the model is skipped with the reason rather than
  opened anyway. A cloud model is the exception in form only: `CreateNewLocal` does not take a cloud
  path, Revit makes and keeps the local itself, and synchronising works from there — hence
  `BatchModel.NeedsSync` being "a local of ours, or the cloud" rather than plain `IsWorkshared`.
- **The batch opens exactly two worksets, and both the prefix and the reporting of them are
  load-bearing.** `00_Shared levels and grids` holds the grids, `00_Link_BM` holds the coordination
  file, and everything else stays closed — not only to open faster, but because what a closed
  workset holds is not in the document at all, so a run cannot touch an element it was not asked
  about. The names are matched by **prefix** (`BatchCoordination.Matches`, through
  `LinkPreferences.NormalizeWorkset`): the base-file workset carries a building suffix often enough
  (`00_Link_BM_K3`) that an exact name would quietly open a model with no base file in it — and a
  model with the link's workset closed produces no scan at all, which reads exactly like "this
  model is fine". For the same reason a model where **nothing** matched is not opened: the report
  names the worksets it actually has instead, and `Idle` tells four different "nothing was done"
  cases apart — nothing monitors a link, the link is not loaded, nothing this run was allowed to
  touch, and everything already in place.
- **A cloud model's worksets cannot be listed without opening it — so the batch opens it twice.**
  `WorksharingUtils.GetUserWorksetInfo` reads the worksets out of the file itself, and an Autodesk
  Docs model has no file to read: on a cloud path it throws `InternalException` with Revit's own "A
  managed exception was thrown by Revit or by one of its external applications" (seen on this
  office's models — the batch failed on it six models out of six, and "Link Manager" loaded every
  BIM360 link with its worksets untouched). There is no other call that answers the question, and
  without the ids a `WorksetConfiguration` cannot be built at all, which would leave only "load a
  consultant's model whole" or "cloud models are not supported". `LinkCatalog.ModelWorksets` — the
  one reader both "Link Manager" and the batch use — has two ways for a cloud model. A model already
  in the session (a loaded link, as a rule — `Application.Documents`) is read as it stands. Any
  other is opened with `CloseAllWorksets` (it holds no model elements then, but knows all its
  worksets — `FilteredWorksetCollector` returns **every** one, closed included), the list is read,
  the document is closed, and the real open follows with the ids — **but only when the caller passes
  `mayOpen: true`**, which the batch does. Files and Revit Server keep the short road: there the
  file is there to be read.
- **"Link Manager" never opens a cloud model to read its worksets (`mayOpen: false`) — it links it
  first and reads it from the link.** With the open in place (14–21 October 2026), every BIM360 model
  it then linked failed on another office computer with a bare "Object reference not set to an
  instance of an object" — three models out of three, nothing created — while the same models had
  linked before the open was added. Which call threw was never seen (the report carried only the
  message); the open-then-link sequence is the one thing that changed, so it went. Now a new BIM360
  link is created with the base mode (`BaseOption`, "open all" by default — never "close all": if
  the second step fails, the link must not be left empty), `EnsureWorksets` reads its list from the
  link's own document at once (`Reread`, for every row in `_unreadable`, so its names count for
  "not found in any link" even when nothing needs a reload), and a mismatch is fixed by the usual
  `LoadFrom` with the real setup — the report lists it under "took hold only on a second load", and
  that load clears the undo history like every reload. The price is a cloud consultant model loaded
  twice when a workset has to close; the gain is that the link is there whatever happens to the
  worksets. "Read Worksets" in the window does not open one either: a cloud row not linked yet
  reads "Worksets read once linked" (`LinkCatalog.NotLinkedYet`). A link that came in with the base
  mode because nothing could be read and was not put right is reported as exactly that, never as
  "Revit did not apply the worksets": no workset setup was sent to Revit at all; one the check found
  as asked gets no note at all (`Account`) — every new BIM360 link starts unread, and a note on each
  would read as a failure. If the reload itself fails, its reason goes into the line
  (`WorksetCheck.Because`). Do not bring back an open of a cloud model before `RevitLinkType.Create`
  without testing a BIM360 link in Revit after it.
- **Link Manager's failure lines name the call that threw when the message says nothing**
  (`LinkCatalog.Explain`). For a `NullReferenceException`, an `InvalidCastException`, an index/key
  miss or Revit's `InternalException`, the line gets "[NullReferenceException in RevitLinkType.Create,
  called from LinkManagerCommand.Create line 716]" — the throwing method from the stack trace and
  the last frame in this assembly, with its line (the .pdb is installed). Everything else keeps its
  message alone: Revit's own exceptions already say what went wrong.
- **The batch never deletes, and this is not the same rule as the window's "Delete…" box.** In the
  open project a row that is gone from the coordination file can be deleted — behind its own switch,
  unchecked, with the count of what stands on the level in the confirmation. None of that can be
  had for a model nobody is looking at: the count would come from a document the user cannot see,
  and the answer would be given for a dozen models at once. Such rows are counted in the report and
  left alone. Levels and renames are off by default for a softer version of the same reason, and
  both are settings rather than fixed: a level drags everything standing on it, a name is what
  somebody's drawings refer to.
- **Elements are checked out before being edited, and the ones somebody else holds are named.**
  In a local copy `WorksharingUtils.CheckoutElements` is not a formality: an element borrowed by
  another user cannot be edited at all, and `AcceptCoordinationCommand.Borrow` says who is holding
  it instead of letting a Revit exception per grid into the report. What nobody owns is borrowed and
  handed straight back by the synchronisation; a model that turned out to need no change at all is
  relinquished explicitly (`BatchCoordination.Relinquish`) — deleting the local copy does not do it,
  and the central would go on believing the elements are checked out to this machine.
- **Checking the result travels with the link, not with the document.** `Outcome.Applied` keeps a
  `Change` — the row plus the scan it was measured against — because one model can monitor several
  links at once (the building's coordination file and the overall site placement), and a grid
  checked against the wrong one would be declared "still off" for no reason. The verifiers are built
  one per link and cached for the run: reading a link's grids and levels is the expensive half of it.
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
- **A schedule set is a small .rvt of its own, not a text description of the schedule.** The API
  does allow one to be built from scratch (`ViewSchedule.CreateSchedule` plus `AddField`, all
  present in 2022/2024/2025), but reproducing one exactly — calculated values, cell formatting,
  merged headings, an embedded schedule — is a large and fragile job, and the result would still
  differ from what the user drew. A .rvt carrier costs nothing to keep, and copying out of it is
  `ElementTransformUtils.CopyElements`, the very call Revit's own "Insert Views from File" makes, so
  the schedule arrives whole. The price is that a set is refreshed by hand when the base model
  changes; the gain is that inserting costs the opening of a file a couple of megabytes big — the
  base model is not needed at all, and a set can be handed to a colleague. This is the one place in
  the add-in where a setting is not a text file, and the reason is exactly this: text is for what we
  generate ourselves (`autodim\`, `links\`), not for what a person drew.
- **The cache is kept per Revit year** (`schedules\<year>\<set>.rvt`; `ScheduleLibrary.Year` is set
  by `App.OnStartup` from `ControlledApplication.VersionNumber`, exactly like
  `RevitServerClient.ServiceVersion`). A .rvt only ever opens in its own Revit version or a newer
  one, so a set captured in 2025 simply would not open in 2022 — and that failure would look like a
  broken button rather than what it is. The settings file stays one level up, shared by every year:
  a set name is worth remembering across them.
- **A name clash is settled before the copy, never after.** Revit will not hold two views of one
  name, and what it does when asked to — rename the arrival, or refuse — is not something to build
  on. So no clash is ever left to reach `CopyElements`: what is to be replaced is deleted from the
  project inside the same transaction first, and what is to arrive alongside is renamed **in the
  cache document** (`ScheduleLibraryCommand.Prepare`), which is then closed without saving, so the
  rename lasts exactly one run. The report still prints the name Revit really gave the schedule
  (`inserted.Name`), not the one that was asked for: if a clash was somehow settled another way, the
  report must show that rather than repeat the plan.
- **Replacing puts the schedule back on its sheets.** Deleting the project's own schedule takes its
  `ScheduleSheetInstance`s with it, so they are written down first (sheet, point, segment index,
  rotation) and recreated on the new one. Without this, "Replace" would quietly strip a sheet set —
  the kind of loss nobody notices until printing. For the same reason the sheet count is shown in
  the window **before** the choice is made, not reported after it.
- **The source model is opened detached but with its worksets open** — the opposite of "Link
  Manager", and deliberately. A key schedule's rows are elements, and closing the worksets would
  fetch the schedule without its keys — a silent loss of data. Opening is the slow half of the work
  and it is paid for once: every insert after that goes through the cache. Detaching itself is not
  optional: an ordinary open would either make a local copy or lock somebody's central model, and
  the button only ever reads. `DetachAndDiscardWorksets` is not allowed on a model that is not
  workshared, and Revit's refusal is the signal to open it plainly — hence the second attempt,
  rather than deciding in advance.
- **Choosing what to take is a call-back, not a second pass over the model.** `ScheduleChooser`
  (declared next to `ScheduleInfo`) is handed to `ScheduleLibrary.Capture`: the command opens the
  model once, asks the window, copies and closes. Reading it, then showing a window, then reopening
  it to copy would mean opening a base model twice — minutes, not seconds. The "windows know nothing
  about Revit" rule is kept the same way as with `DeleteProjectParametersWindow`'s family scan.
- **`DuplicateTypeAction` has exactly two values** — `UseDestinationTypes` and `Abort` (confirmed by
  reflection over `RevitAPI.dll`). `Abort` cancels the whole copy, so the handler always answers
  with the destination's own type: a schedule arriving with the project's own text types is a far
  better outcome than one not arriving at all.
- **Schedules are copied one at a time, not as a batch.** `CopyElements` is all-or-nothing: one
  schedule Revit will not take would cancel every other. `ScheduleLibrary.CopyOne` wraps a single id
  so a failure lands in the list and the rest carry on — the same rule as everywhere here. There are
  a handful of schedules in a set, so the cost of the extra calls is nothing.
- **A parameter set stores a GUID, never a name.** A shared parameter's name can be typed
  differently by whoever maintains the shared parameter file; the GUID is its one permanent
  identity, and it is what a set is looked up by, both in the open project
  (`SharedParameterElement.Lookup(doc, guid)`) and, for one not yet in the project, in the shared
  parameter file itself. The name in a saved entry is a label kept only for display, refreshed
  whenever the parameter is picked again from the file.
- **Categories are stored as `BuiltInCategory` names, the parameter group as a `ForgeTypeId.TypeId`
  string** — the same reasoning as a project workset or a dimension chain kind stored by name
  elsewhere in this add-in: a set has to travel between projects in any language Revit runs in.
  `ParameterSetCommand.CollectCategories` only ever offers categories with a `BuiltInCategory`
  representation (`(BuiltInCategory)category.Id.IntegerValue`, the same idiom this project already
  keeps for 2022 compatibility — see "Porting to another Revit version"); a custom, per-document
  category with no such enum value cannot be addressed by a set and is left out rather than saved as
  something nothing could read back. **Trap:** a `ForgeTypeId` group string has not been confirmed
  byte-for-byte identical between 2022 and 2025 the way `Definition.GetGroupTypeId()` itself has —
  so a saved group is validated against `ParameterUtils.GetAllBuiltInGroups()` on the Revit that is
  actually applying the set, and silently substituted with the first available group, noted in the
  report, rather than left to throw and fail the whole row over a cosmetic setting.
- **The shared parameter file Revit is configured with is swapped out only for the duration of a
  read, and always restored.** The API has no way to open an arbitrary `.txt` path directly —
  `Application.OpenSharedParameterFile()` always reads whatever `SharedParametersFilename` currently
  points at — so browsing to a different file means setting the property, reading, and setting it
  back in a `finally` (`SharedParameterFile.Read`, which the "Sheets" number picker reads through as
  well). This is an application-wide Revit setting, not something a single button
  should leave changed behind it. **Applying swaps it too, and must:** a parameter missing from the
  project can only be created out of the file the set was actually built from (remembered in
  `parameters\_settings.txt` as `FILE`), which is often not the one Revit is pointed at — without
  the swap, every genuinely new parameter would fail with "GUID not found", which is the button's
  main job. The swap there wraps the whole run rather than a single read: the `ExternalDefinition`s
  are handed to `BindingMap.Insert` and only stay valid while their file is the current one.
- **A row the project already matches is left untouched, and the report says so separately.**
  `ParameterSetCommand.Compare` is the single source of truth for both the "State" column and the
  applying, so the table can never promise one thing and the button do another; a matching row is
  counted as "Already as required" and never re-bound. Re-inserting a binding that is already right
  is a real document edit for no gain, and it would make the report claim an update that never
  happened — on a button whose whole purpose is "check and bring in line", a truthful count of what
  actually changed is the product.
- **A category the set asks for that this project does not have is not a difference.** Comparing
  against the raw saved list would leave a set built in an MEP project reading "will be updated" in
  an architectural one forever, re-binding on every run and never converging. `Compare` therefore
  splits the entry's categories into the ones this project offers and the rest, compares only the
  first, and carries the second into the caption and the report as a plain note.
- **Binding uses `Insert` for a parameter never bound before and `ReInsert` for one that already
  is — `ReInsert` freely swaps instance for type and back, in the same call that also updates
  categories.** Both overloads take the parameter group directly
  (`Insert(Definition, Binding, ForgeTypeId)` / `ReInsert(Definition, Binding, ForgeTypeId)`,
  confirmed identical in 2022 and 2025), so there is no separate `SetGroupTypeId` call needed just
  to place a parameter under the right group. `SetAllowVaryBetweenGroups` sits completely outside
  the binding, though, and cannot be read back from it — only from the `InternalDefinition` itself
  (`VariesAcrossGroups`) — so it is compared and (re)applied on its own, in its own `try`/`catch`,
  independently of whether the binding needed touching at all.
- **Narrowing categories or swapping instance for type is flagged, never applied silently.**
  Removing a category from an already-bound parameter drops its values on every element of that
  category — Revit gives no warning for this, `ReInsert` simply succeeds. `ParameterSetCommand. DescribeOne` treats exactly these two cases as the ones worth a red flag (a wider category list, a
  changed group or a changed "varies across groups" only ever add or relabel, never drop data), and
  `ParameterSetWindow.OnApply` repeats the same list in a confirmation before running — the same
  shape as "Schedule Library"'s warning before a "Replace".
- **A category with no categories chosen cannot be applied, and the window catches this itself,
  without asking the command.** `BindingMap.Insert`/`ReInsert` need a non-empty `CategorySet`; a
  freshly added parameter starts with none chosen (there is nothing sensible to default to), so
  `ParameterSetWindow.Revalidate` marks such a row on its own, in plain C#, and only asks the
  Revit-side call-back about the rows that pass this local check.
- **"Copy settings" / "Paste settings" ride the grid's own row highlight
  (`DataGrid.SelectedItems`), never the apply check boxes.** The check boxes already mean "will be
  applied" and start checked on every row (`AddRow`), so they cannot double as "which rows to copy
  into" without first unchecking the ones that should be left alone — exactly backwards from what
  the button is for. The grid's highlight (click, Ctrl+click, Shift+click; the same selection
  `ToggleSelectedRows`/Space already read) picks a source with nothing else disturbed: "Copy
  settings" is enabled only when exactly one row is highlighted, captures Binding, Categories,
  Group and "Vary by group" (never the name or the GUID — those stay each row's own identity) into
  an in-memory `RowSettings` snapshot (a defensive copy of the category list, so a later edit to
  the source row cannot retroactively change an already-pasted one), and "Paste settings" applies
  it to every row currently highlighted. The four properties are set inside the same `_settingMany`
  guard `SetAllSelected` uses for the check boxes, so a paste onto many rows revalidates the whole
  table once — through one call to `_describeStatus`, a round trip into the open project — rather
  than once per property per row.
- **A closed workset reads as empty through every collector, and on the "Worksets" button that zero
  would be the most dangerous lie it could tell.** `FilteredElementCollector` does not see elements
  in a closed workset, and there is **no API to open one in an already-open document** — confirmed by
  reflection over `RevitAPI.dll` 2022/2024/2025: `WorksetConfiguration` is only accepted when opening
  a document or a link, and `Workset.IsOpen` has no setter. So a workset full of walls counts as
  "0 elements", and on a button that offers to delete the contents a silent zero would take somebody's
  geometry down without a word. Hence `WorksetsCommand.Count` returns `int?` rather than `int`, and
  `WorksetInfo.IsCounted` keeps "empty" and "not counted" apart all the way through: the table reads
  "not counted", the row is flagged, and the confirmation names the closed worksets separately and
  says outright that their contents are **not** in the number above. Never collapse the two into one number.
- **The checkout has to happen before any transaction opens, and the deletion only inside one — the
  API forces both halves of that order.** `WorksharingUtils.CheckoutWorksets` throws
  `InvalidOperationException` when a transaction, sub-transaction or transaction group is open, while
  `WorksetTable.DeleteWorkset` throws `ModificationOutsideTransactionException` when none is (both
  straight out of `RevitAPI.xml`). The same shape as "Link Manager", where `LoadFrom` has to run
  outside a transaction and `Create` inside one. Without the checkout the button would look broken on
  exactly the models it is for: `CanDeleteWorkset` returns false for any workset the current user does
  not own, and in a freshly opened local model nobody owns anything — every row would come back
  "Revit will not delete this workset". The destination workset is checked out too: elements arriving
  in it are an edit to that workset as much as to the one going. A checkout failure is a note, not a
  stop — `CanDeleteWorkset` gets the last word a moment later.
- **The active workset is stepped off before the deletion, and that step is deliberately not undone
  by Ctrl+Z.** Revit always has an active workset (new elements need somewhere to land), and it
  cannot be deleted from under itself. `WorksetsCommand.Vacate` moves it to the chosen destination,
  or to the first workset that is staying, before the transaction group opens — the active workset is
  session state, not part of the document, the same reasoning as the workset switch in "Base File".
  It goes into the report as a note: a setting that changed silently is one the user trips over later.
- **A workset owned by another user gets no check box at all** (`WorksetsWindow.Removable`).
  `CanDeleteWorkset` refuses it outright, so a check box there would lead only to a failure line in
  the report after the user had already confirmed the run. The reason goes into "State" instead,
  where it can be read before anything is decided. The window also refuses to let **every** workset be
  checked: Revit keeps no project without a user workset, and there would be nowhere to move the
  contents either — caught once in the summary line rather than as a row of identical failures later.
- **Worksets are addressed by `Workset.UniqueId` (a `Guid`), never by `WorksetId`.** By Autodesk's own
  documentation a `WorksetId` changes on synchronising with the central model and only the GUID is
  stable — the same rule "Link Manager" keeps when it re-reads link workset ids right before loading.
  So the window carries GUIDs and the command resolves them back through
  `WorksetTable.GetWorkset(Guid)` (present since 2012, and in all three years) at the moment it
  deletes; a workset gone in the meantime becomes a report line rather than a wrong deletion.
- **"Translate" works on a *text*, never on a place — and that is the whole design, not a
  convenience.** Revit has no language switch for typed content: it translates its own words
  (categories, built-in parameters, view types) and keeps every typed string as typed. And a string
  is often data, not a label: "Отверстия" in a view parameter is also what a view filter rule, a
  schedule filter and the Project Browser's grouping compare against. Translated place by place, a
  filter would keep looking for the Russian after the parameter turned English and silently stop
  matching. So the window has one row per distinct normalised text (`TranslationDictionary.Normalize`:
  "\n" line breaks, trimmed — one rule for the project, the dictionary and imported files), and
  `TranslateCommand.Apply` replaces it in every `TextSite` that holds it, in one transaction.
- **Every kind of place is a `TextSite` with its own read and write**, and `TextSiteCollector` finds
  them. Names come off a **fixed list of classes** (`NamedClasses` + every `ElementType`), not off
  every element: a wall's or a door's `Name` is its type's. Parameter values come off
  `GetOrderedParameters` — what the Properties palette shows — not every parameter an element
  carries: Revit's hidden internal parameters are nothing a person typed. A **built-in** string
  parameter equal to the element's name is skipped, but only on an element whose name is already
  collected — that is the name seen a second time (`VIEW_NAME`, `DATUM_TEXT`, a type's "Type Name");
  a project or shared parameter is never skipped that way, and an element off the name list (a key
  schedule row, a scope box, a room) keeps its built-in name parameter as an ordinary value.
  `TEXT_TEXT` is skipped too: a text note goes through its own site.
- **Only what the API can write is written, and what it cannot is shown, not dropped.** Confirmed
  in `RevitAPI.xml` 2022/2024/2025: `Category.Name` and `InternalDefinition.Name` have no setter, so
  line styles, object-style subcategories and shared parameter names (fixed by the GUID anyway) are
  `FixedTextSite`s — collected so the window says "stays as it is — why" and the report lists them
  with where to rename by hand. Subcategories are walked under Revit's own categories only
  (`category.Id < ElementId.InvalidElementId`): an imported CAD file is a category of its own, and its
  subcategories are the file's layers. A non-shared project parameter and a global parameter are
  *tried* through `Element.Name`; a refusal is a report line with the manual route (`TextSite.Hint`).
- **A text note is written through `FormattedText`, never `TextNote.Text`** — per the API, setting the
  plain text drops all formatting. Bold/italic/underline/all caps covering the whole note, and a
  uniform list type, are put back after `SetPlainText`; formatting on part of the text has no
  counterpart in a translated sentence, so such a note is counted in the report (`FormattingReset`).
- **Schedule header cells are taken only when `CellType.Text`.** Per the API, `SetCellText` on any
  other cell type turns it into plain text — the cell would stop following the parameter it shows.
  The cell reading the schedule's own name is skipped: that is the title, and it follows the name.
  Grouped column headers live in the body section, where the API forbids `SetCellText` on a
  standard schedule — not translated, and the README says so.
- **View filter rules are rebuilt, not edited.** `GetElementFilter` and `GetRules` hand out copies;
  `ViewFilterRuleSite` rewrites the tree (logical nodes in place, a parameter node recreated with
  its `Inverted` flag) and puts it back through `SetElementFilter`. One site per distinct string in
  a filter, so `Holds` is overridden: before the write "a rule compares against the original", after
  it "a rule compares against the translation".
- **A write is checked twice, the same way "Accept Changes" checks a move.** Before: the place still
  holds the original (`TextSite.Holds`) — if it already holds the translation it was changed along
  with something else this run (a built-in mirror the skip rule missed) and is passed over silently.
  After: it really holds the translation; "Revit took it without an error" is not counted as done.
  `Transaction.Commit()`'s status is checked, and a rollback empties the success list. Names go in
  passes, like "Rename Nested": a name refused as taken (`Autodesk.Revit.Exceptions.ArgumentException`
  — **not** a `System.ArgumentException`, both are caught) is retried while some other rename in the
  pass went through.
- **The window's clash check uses deliberately narrow scopes** (`TextSiteCollector.ScopeOf`): views
  per `ViewType`, view templates, types per family, families per category, materials, filters,
  levels, grids, sheet *numbers* (sheet names may repeat), worksets… A clash the window misses still
  comes back from Revit as a report line; a clash it imagined would block a translation that would
  have gone through — so when unsure, the scope is narrower. Compared ignoring case, like Revit. A
  clash or a forbidden character blocks "Translate" until fixed or unchecked; Cyrillic left in a
  translation only warns.
- **The Project Browser is watched, because its filters cannot be edited.** A browser organization
  can filter views by a value, and the API has no read or write for that filter — but it has
  `BrowserOrganization.AreFiltersSatisfied`. `TranslateCommand.BrowserWatch` records which views pass
  every organization before the run, re-checks inside the transaction after `Regenerate()`, before
  the commit, and reports an organization that lost views, with the manual route. Silence here would
  look like "the translation made my views disappear". **Trap:** `BrowserOrganization` is an
  `ElementType` subclass — a `case BrowserOrganization` placed after `case ElementType` does not
  compile (CS8120), and one left out would be named "type name".
- **The dictionary is written whenever the window closes, applied or not**, merging what was read,
  what "Import…" brought that matches nothing in this project, and the table on top (a cleared cell
  removes its entry). A translation typed once must never be typed again; and unlike the add-in's
  settings files, a failure to write it is shown, not swallowed — it is somebody's work.
- **The export is written for whoever translates, a person or an AI**: TAB-separated (Excel opens it
  as columns; a vertical bar can stand in a name), a third column saying where each text is used, and
  a header that states the format, the forbidden name characters and "keep the first column as it
  is". On import, Excel's quoting of a cell holding a quote is undone only when the field is wrapped
  in quotes **and** carries a doubled quote inside — a text that genuinely reads `"Ось"` has none.
- **"Move to Workset" decides what is movable two different ways, depending on the workset an
  element stands in — and the asymmetry is the safety of the button.** In a **user** workset the
  test is Revit's own: `ELEM_PARTITION_PARAM` exists and is not `IsReadOnly` — what the Properties
  palette goes by, and it leaves out on its own everything whose workset Revit decides (curtain
  panels, nested parts, annotation belonging to a view). In **Revit's own** worksets
  (`WorksetKind` other than `UserWorkset`) that test is not trusted alone: a project-standards
  workset legitimately holds phases, materials, patterns, project information, a family workset the
  `Family` itself, and those rows would be **checked by default** — so there an element counts only
  if it stands in the model (`LocationPoint`/`LocationCurve` or a bounding box, not `ViewSpecific`).
  There, too, a read-only parameter does not hide the element: it becomes a "locked" id, shown in
  the row with no way to move it — "nothing found" would read as "the model is clean". Elements with
  no category or an `Internal` one are never listed in either case. Do not widen the test in Revit's
  own worksets without keeping the "stands in the model" guard.
- **Elements inside a model group are never moved by "Move to Workset"**, only counted and
  selectable. Changing one member of a group outside "Edit Group" is a group edit Revit may refuse
  at commit, and a refusal at commit rolls back the whole transaction — every other row with it.
- **The checkout comes first, and the out-of-date check comes with it.** `WorksharingUtils`'s own
  remarks give the recipe: `CheckoutElements`, then confirm the elements are up to date
  (`GetModelUpdatesStatus`). An element held by another user (`GetCheckoutStatus`, the owner's name
  goes into the report) or changed in the central model since the last reload is taken out before the
  transaction opens: left to the commit, either one fails the whole batch rather than itself. A
  standards workset is no exception — whoever last edited the phases may still own it, and with it
  every stray element inside. A `CheckoutElements` that throws (no central yet, the network) is a
  note, not a stop. The target workset itself is **not** checked out: that would lock a whole
  discipline's workset against colleagues for the sake of an edit Revit borrows per element anyway;
  a target owned by someone else is flagged in the window instead.
- **"Move to Workset" checks the result the same way "Accept Changes" does**: the commit status is
  read (a rollback empties the success list and is reported as such), and every element is read back
  — `element.WorksetId == target` — before it is counted as moved. `Parameter.Set` returning `true`
  is not a move. Failures are grouped by their reason, with the categories, so a hundred ducts
  refused for one reason are one report line.
- **Its check box means "these elements", not "move these"**: it feeds both "Move" and "Select in
  model", so it is never disabled — a row with nothing movable (all in groups, already in the target)
  is still worth selecting to look at; what each checked row will actually do is in "State". Rows in
  Revit's own worksets start checked on the whole model, every row on a selection — nothing is
  deleted here, and both are exactly what the user came to do. "Only what is shown gets applied"
  holds as everywhere: a row leaving the table through "Show" loses its check mark.
- **.xlsx is written and read by our own code** (`Infrastructure/Xlsx.cs`: `System.IO.Compression`
  + `System.Xml`), not by a library such as ClosedXML or EPPlus — the same reasoning as the JSON
  parser: the add-in lives in Revit's process, where every extra assembly is one more way to fail to
  load or to clash with another add-in's copy of it. The writer needs only text cells, five styles,
  widths, a frozen pane, merges, a filter, hidden rows/columns/sheets and row groups — plus, for the
  "Sheets" batch summary, number cells, a highlight and hyperlinks (new styles are appended after the
  first five, whose indices the exports rely on); the reader takes what Excel
  actually saves (shared strings, numbers, booleans, formula results, dates told apart by the number
  format, the "strict" namespaces). Checked against real Excel: a written file opens without a repair
  prompt with every feature intact, and a file re-saved by Excel reads back the same. Old binary
  `.xls` is refused with a "save it as .xlsx" message rather than half-read.
- **The Revit API has no "elements of a schedule row" — the rows are asked, in a transaction that is
  always rolled back** (`ScheduleTable.Probe`). `TableSectionData` gives texts only (confirmed in
  RevitAPI.xml 2022/2024/2025). Inside the throwaway transaction: headers, group headers/footers,
  blank lines and the grand total are turned off; the sorting/grouping fields are unhidden; a
  temporary text parameter is bound to the schedule's categories (created in a throwaway shared
  parameter file — the same `SharedParametersFilename` swap as "Parameter Sets", put back in a
  `finally`); every element of `FilteredElementCollector(doc, schedule.Id)` gets a key of its own in
  it; the parameter is added as a column; and each row reads back which key it shows. With
  `SetClearAfterRollback(true)` nothing of it surfaces, and nothing reaches the undo list.
  `ViewSchedule.RefreshData()` is called before every read — the definition was just changed.
  **Rejected: `TableSectionData.RemoveRow` per row** (deleting a row deletes its elements, then
  rolling back) — it is one sub-transaction per row, a host's deletion takes hosted elements in other
  rows with it, and a group member may refuse to be deleted at all. **Two traps the probe guards
  against:** copies of a model group share a parameter's value unless it varies by group instance —
  so the key parameter is set to vary (`SetAllowVaryBetweenGroups`), and every key is read back and
  dropped if it is not on exactly its own element; and a grouped row ("Itemize every instance" off)
  shows a key only when it holds one element — so the schedule is itemized for a moment, and each
  itemized row is put back into the grouped row carrying the same sorting/grouping values, which is
  exactly Revit's own rule for folding rows. When the two lists line up run for run, order settles
  it; otherwise values are matched, and a grouped row whose values repeat is left untied rather than
  guessed. A row that cannot be tied (linked elements, a category that takes no project parameter)
  keeps its texts and says why — it is exported but never written into. If no transaction can be
  opened at all, the rows are read as drawn, all untied: still enough to export.
- **The export writes text, exactly as the schedule shows it, into text-formatted cells (`@`).**
  ModPlus does the same and for the same reason: turning "1 200" or "001" into numbers depends on the
  reader's locale and loses leading zeros — and the import compares what comes back with what the
  schedule shows, for which text is the one form that survives unchanged.
- **Every exported line carries a key to its elements, in the line itself.** Hidden column A holds a
  short row key ("R12"); the hidden "VladTools" sheet maps it to the elements' UniqueIds (split into
  30 000-character pieces — a grouped row can stand for thousands of elements, more than a cell
  holds). The key travels *in the row*, so sorting or filtering the sheet in Excel cannot detach a
  line from its elements; the filter range therefore starts at column A. Hidden row 1 holds what each
  column shows (`ScheduleColumn.Key`: the field kind plus `BIP:` name / shared `GUID:` / project
  `NAME:`), so the import finds a column again after its heading was edited, the columns were moved,
  or the sheet is imported into another schedule or another project. UniqueIds, not ElementIds: a
  file may well come back to a different copy of the model.
- **"As in Revit" is the drawn body lined up against the probed rows, or nothing.** The element rows
  read the same in both (the same fields, the same formatting) and in the same order; headings, group
  lines and totals are the lines in between. If the alignment does not reach the end, that schedule
  is written as a plain table and the report says so — never with keys guessed onto the wrong lines.
- **An import change is a change of value, not of text** (`ScheduleValues.SameValue`). A number is
  parsed through `UnitFormatUtils.TryParse` with the column's own `FormatOptions`, formatted back the
  same way and compared with what the schedule shows: "1200" typed over "1 200 mm" is the same, and
  rewriting it would round off whatever the display hid. A number cell from Excel is taken in the
  column's display unit (12.5 under "m²" is 12.5 m²) and converted to internal units. Yes/No accepts
  yes/no/да/нет/1/0/true/false.
- **What an import may write is what typing into the schedule in Revit may write**: `Instance` and
  `ElementType` fields only — never calculated, count, combined, room, project-information, material
  or link columns (`ScheduleTable.ReadOnlyReason`). **Trap:** per RevitAPI.xml a shared parameter
  bound *per type* still comes to a schedule as an `Instance` field, so a parameter missing on the
  element is looked for on its type (`ScheduleValues.Find`). An instance parameter goes to every
  element of a grouped row; a type parameter to the distinct types. Refused per cell, with the reason
  in its tooltip: a read-only parameter, a choice of another element (`StorageType.ElementId`), a
  total over a grouped row (`DisplayType != Standard` and more than one element), an element in a
  model group whose parameter does not vary by group instance (left to the commit, that would roll
  back the whole batch), and two lines giving one element or type different values — neither is
  written then. A type parameter changed on one line while other lines of that type still show the
  old value is **not** a conflict: that is how editing a type in a Revit schedule works, and the
  tooltip counts the other rows instead.
- **An empty cell leaves a value alone unless "Empty cells clear values" is on** (off by default, and
  never for numbers): an empty cell is far more often a column nobody filled in than a deliberate
  deletion. The import never creates or deletes elements — a line with no element behind it is
  skipped with a note.
- **Pairing by elements takes the row standing for exactly the line's elements first**, then the one
  row holding any of them (the grouping may have shifted since the export — the row says so). A line
  whose elements now stand in several rows is left unpaired, never guessed. Rows standing for the same
  elements — a material takeoff lists an element once per material — are taken in turn.
- **Writing follows "Move to Workset" and "Accept Changes"**: the targets are checked out first
  (held / out-of-date ones named, not attempted); values go in in passes, like renames (a sheet
  number still held by the sheet about to give it up goes through in the next pass); the commit
  status is checked; and every value is read back (`ScheduleValues.Holds`) before it is counted.
- **"The changed data highlighted" in Revit itself is the selection**: after the import the schedule
  becomes the active view (`UIDocument.ActiveView`, outside any transaction) and the changed rows'
  elements are selected — Revit highlights a selected element's row in a schedule.
- **"Sheets" takes its folders from the Project Browser itself, not from a parameter it guesses.**
  `BrowserOrganization.GetCurrentBrowserOrganizationForSheets` is the organization in use,
  `GetFolderItems(sheetId)` the folders a sheet sits in (outermost first), and `SortingParameterId` +
  `SortingOrder` the order inside a folder; the sorting parameter is found among the sheet's own
  `Parameters` by its id, which covers built-in, project and shared parameters alike without
  `IntegerValue`. What the API does not give is the order of the folders themselves: they are
  sorted ascending, naturally (A2 before A10), which is how the browser lists them. An empty grouping
  value is shown as "???", as Revit shows it. All of it is in RevitAPI.xml 2022/2024/2025, unchanged.
- **No sheet is dropped because the browser does not show it.** A sheet the organization's filter
  rejects (`AreFiltersSatisfied`) and a placeholder sheet (`IsPlaceholder` — a line in sheet lists
  only, nothing to open) are still sheets of the project; each kind gets a folder of its own after
  the browser's. A filter check that throws counts as passing — a sheet must not land under "hidden"
  because Revit failed to answer.
- **The "Sheets" table is the browser's tree laid flat** (`SheetListRow`: a folder line, then its
  contents one level deeper), not a WPF grouped collection view: the window's lines map one for one
  onto Excel rows, and a grouped view would have to be laid flat again for the export anyway.
  Folding removes and inserts lines with one merge over the ordered list
  (`FlatTree.Sync`), never `Clear()`, so a fold keeps the scroll position and the
  selection. Column sorting is off: a click on a heading would scatter sheets out of their folders.
  The natural comparison falls back to an ordinal one on a tie, so two folders differing only in case
  never interleave — the window starts a new folder line wherever the name changes.
- **The export writes what the search shows, and folding is not narrowing** — "only what is shown
  gets applied", but a folded folder is still part of the table, the way a folded group in Excel
  still holds its rows. Folder lines become Excel row groups (`XlsxSheet.OutlineLevels`, the button
  above the group — `outlinePr summaryBelow="0"`, a heading over its contents rather than a total
  under its rows), so the folders fold in Excel as well. Filter buttons go on only when there are no
  folder lines: sorting through them would scatter the sheets. Checked in real Excel: the file opens
  without a repair prompt, with the levels and the button position intact. In the file the number and
  the name are columns of their own (the window keeps "A101 - Name", as the browser writes it): the
  number is what one week's file is matched against the next one's by. A folder line is merged across
  the number, the name and the date. The "Show" list narrows like "Find", and the export follows both.
- **"Sheets" dates a change itself, because Revit keeps no date of one.** Confirmed in RevitAPI.xml
  2022/2024/2025: `Element.VersionGuid` and `DocumentVersion` carry no time ("does not contain any time
  information"), `WorksharingTooltipInfo.LastChangedBy` says who and not when, and
  `Document.GetChangedElements` (2023+ only) gives ids without dates too. So **every opening of the
  window is a check**: a `ViewFingerprint` of every sheet and every view on a sheet is compared with
  the one the previous check left in `SheetHistory`, and a difference is dated with today. The date is
  the day a change was *found*; it was made after the previous check (`SheetHistoryEntry.After`, shown
  in the tooltip). The first check only sets the baseline — nothing before it can be known, and a sheet
  or view first seen after it is "Added" (a new sheet, a view placed on a sheet). Never word the
  column as "the day it was made". The check is saved **before** the window opens: looking is the check.
- **What a view's fingerprint holds, and what it leaves out on purpose.** The view's own
  `VersionGuid` (crop, scale, visibility/graphics), its template's, the primary's for a dependent view,
  and the `VersionGuid` of every element the view owns — read in one pass over the model with
  `ElementOwnerViewFilter(ElementId.InvalidElementId, true)` (every view-specific element), not a
  collector per view. A dependent view also takes its primary's elements: annotation drawn in a
  dependent belongs to the primary. A sheet is fingerprinted the same way, so its viewports,
  titleblock, schedule instances, text, images and CAD are all in it. Elements with no category are
  skipped as Revit's bookkeeping. **Model elements seen in the view are not in it**: finding them is a
  visibility pass per view — minutes on a large model — and one wall would mark every plan it shows
  in; a model change shows where it moves the view's own annotation (a dimension value, a tag's text).
  `VersionGuid` moves only on save/synchronise, so an unsaved edit is found by a later check. **Not
  yet seen on a live model**: whether a project-wide edit (a new workset, a new link) touches every
  view element at once and dates every sheet the same day — if that shows up, look here first.
- **The history is kept on this computer, keyed by the central model's path**
  (`LinkCatalog.VisiblePath`), so every local copy of one central model shares it. Keeping it in the
  model (ExtensibleStorage) was turned down: it would turn a read-only button into one that opens a
  transaction and borrows an element at every opening. Unlike the add-in's settings the file is data,
  so a failure to read or write it is shown in the window, and **a file that could not be read is
  never written over** — that would replace somebody's history with a fresh baseline. A model never
  saved has no path and is not tracked. Sheets and views no longer on a sheet drop out of the file at
  the next check (a view put back on a sheet later reads "placed" again).
- **What stands on a sheet is read in one pass per kind over the whole project**, not with a
  collector per sheet: viewports (`Viewport.SheetId` — views and legends), `ScheduleSheetInstance`
  (`IsTitleblockRevisionSchedule` skipped — it is on every sheet and belongs to the titleblock; a
  split schedule's segments fold into one line with a count), `PanelScheduleSheetInstance`,
  `ImageInstance` and view-specific `ImportInstance` (`OwnerViewId`). Titleblocks, text notes and
  annotation are left out: they are on nearly every sheet and would bury what was placed. The word for
  a view's kind is `TextSiteCollector.ViewKind`, shared with "Translate" rather than copied.
- **The "Sheets" batch only reads, so it opens a model the read-only way** (`BatchCoordination.OpenToRead`),
  not the "Accept Changes" batch's way (a local copy, a synchronisation — that one writes). A file or
  a Revit Server model is opened **detached** (`DetachAndPreserveWorksets`): no local copy, no lock on
  the central, nothing to relinquish, closed without saving. **Every workset is closed**
  (`CloseAllWorksets`, no list needed): sheets, views and everything standing on a sheet live in the
  per-view worksets Revit never closes, so the table is whole while the geometry and every link in a
  user workset stay unloaded — seconds per model, not minutes. The change check is unaffected for the
  same reason: a fingerprint holds only view-owned elements. A cloud model is opened the way
  `LinkCatalog.ModelWorksets` already opens one on this office's projects (not detached, every
  workset closed — Revit keeps its own cache, and closing without saving takes nothing from the
  central); a file whose header says it is not workshared is opened plainly (detaching is refused
  there); a model already in the session — the open project, its local copy, a loaded link — is read
  as it stands and left open (`BatchModel.WasOpen`; `Release` skips it).
- **A batch run checks a model only when it opened the model itself, and only reads the history of a
  detached copy.** The history is the model's own: RevitAPI.xml says a detached document's `PathName`
  and `Title` are empty, so `LinkCatalog.VisiblePath` would give nothing and the model would read
  "never saved, not tracked". `Collect` therefore takes the history key as a parameter: the window
  passes `VisiblePath(doc)`, the batch passes the same where there is one, else the path the model was
  opened from (`SheetsCommand.HistoryKey`). A model reached under another spelling of its path (a
  mapped drive against UNC) gets a history of its own — a fresh baseline, not wrong dates.
  **Whether a detached open keeps the central's element versions is not confirmed**: RevitAPI.xml
  promises an equal `VersionGuid` only "in two instances of the saved file". If detaching gave the
  elements new versions, every batch run would date every view "changed", and the model's own window
  would date them all again at its next opening — each check undoing the last, for good. So `Collect`
  takes `check` too: a detached copy (`BatchModel.IsDetached`) gets no fingerprints — `SheetHistory.Check`
  with none keeps the old record, which is exactly a read — and the history is not saved; its dates
  are the ones its last check left, and the summary says so ("not checked (opened detached); dates as
  of …", `SheetReport.CheckNote`) rather than showing a zero. A model the batch opens **as itself** —
  already open in the session, a cloud model (opened the way its own window's model is), a file that
  is not workshared — is checked and saved exactly like the window. To lift the restriction, test in
  Revit with nothing changed in between: the window in a local copy of model X, the batch on X, the
  window again — the second window must find no change. If it finds none, pass `check: true` for
  detached copies too.
- **A model's report in a batch is the window's own export, whole**: `SheetListRow.Tree` plus
  `SheetsCommand.Workbook`, every sheet (there is no search to narrow it) — so a batch file and a file
  exported by hand from the open model are the same file and can be compared week to week. The
  reports go into a folder named after the summary, next to it (`SheetReport.ReportsFolder`): one run
  is one file and one folder, and runs on different days never write over each other; file names are
  made unique within a run (two models called "AR" from different folders).
- **The summary (`SheetReport`) is two sheets, both with filter buttons.** "Models": one line per
  model — what became of it, the sheet organization, counts (sheets, hidden, placeholders, nothing
  placed, views, legends, schedules, images and CAD, changed since the previous check, numbers also in
  another model) and a link to its report — then a totals line kept **outside** the filter range, or
  sorting would carry it up among the models. "All sheets": every sheet of every model with a "Notes"
  column (highlighted), so a question about the whole set is one filter. The counts are real numbers
  (`XlsxSheet.SetNumber` — the one exception to "every cell is text": a count the add-in made itself,
  and "10" sorted as text comes before "9"); the links are relative (`XlsxSheet.Links`, escaped the way
  Excel escapes them itself — a space as `%20`, Cyrillic as it is), so the summary and its folder can be
  moved together. Checked in real Excel: no repair prompt, the numbers are numbers, a link followed
  opens the model's report from a path with Cyrillic and spaces in it.
  "Nothing placed" leaves placeholders out — they can never hold anything — and the window's status
  counts the same way (`SheetInfo.IsEmpty`). "Number also in another model" is information, not an
  error: a building split into discipline models usually wants numbers unique across them, but a
  project that numbers every model from 1 will see every sheet there — that is why it is a note and a
  count, not a red result.
- **The number "Sheets" shows can be another parameter, and Revit's own is then left out.**
  Revit's Sheet Number has to be unique in a model, so offices keep the number printed on the sheet in
  a parameter of their own (a series per volume, "1" in every one) and Revit's turns into a technical
  key. `SheetNumbers` reads the chosen parameter off the sheet, else off its titleblock instance, else
  that titleblock's type: offices keep it in any of the three — bound to the Sheets category, or inside
  the titleblock family. A shared parameter is found by its GUID (the "Parameter Sets" rule); one that
  is not shared — a project parameter, a titleblock family's own — has nothing but its name, and is
  found by it (`SheetNumberParameter.IsByName`, `Element.GetParameters(name)`, built-ins never). What it gives is
  `SheetInfo.Number` — what the window shows, what the groups outside the browser and every tie are
  sorted by, what the export heads with the parameter's name and what the batch compares across
  models (`Repeats`). `SheetInfo.SheetNumber` keeps Revit's own for "Find" and the last tie of the
  sort, but it is **shown nowhere** — not in the window, not in the export, not in the batch summary.
  It used to get a grey column "Sheet Number" beside the parameter, and the user read that column as
  the number the plugin had picked ("it takes the system one, not mine from the ФОП"); the window's
  sheet column is headed "<parameter> - Sheet name" instead (a `TextBlock`: a column header is a
  button, and would eat the underscore). Three cases are kept apart. A sheet with no value shows an empty number
  and is counted (the window's note, the summary's "No … value"): a sheet to fix, not one to cover
  with another number. A model where **no** sheet and no titleblock has the parameter falls back to
  Revit's numbers as a whole and says so (`SheetNumbering.Problem`: the window's red line, the
  highlighted "Result" in "Models"): an all-empty column would make the table unreadable for a model
  that simply numbers the other way. And the browser's order is never re-sorted by the chosen number:
  the table follows the browser, which can be told to sort by the parameter in Revit itself. The
  history is untouched — it keys sheets by UniqueId, so renumbering is never a "new sheet".
- **The number picker lists what the sheets carry, holding values first — not only the shared
  parameter file** (`SheetNumberWindow`). The first version listed the file's definitions and only the
  shared parameters on sheets and titleblock instances, and it failed on the first real model: the
  user picked `SH_SheetNumber` out of the office file, which stood on the sheets empty, while the
  numbers were (by everything the report showed) in a parameter called "SH_Sheet number" that the
  list never offered — not shared, or on the titleblock type. So
  `SheetNumbers.Uses` takes every parameter on the sheets, the titleblock instances and their types —
  shared or not, Revit's built-ins and element choices aside — with how many sheets get a value from
  it, and the list is ordered by that: what holds values on top, then what stands there empty, then
  the rest of the file. A picked row that no sheet gives a value is flagged red before "OK". The file
  is still there for a parameter the sheets do not carry yet — read by `SharedParameterFile.Read`, the
  `SharedParametersFilename` swap "Parameter Sets" makes, one copy for both; "Browse…" loads any
  other file, and the one picked from is remembered (`NUMBER_FILE`). `Uses` is read once, on the
  first opening of the picker.
- **A parameter name on a WPF button goes in a `TextBlock`, never as the string**
  (`SheetNumberWindow.ButtonContent`). A string in a button is access-key text: "SH_SheetNumber"
  showed as "SHSheetNumber", the underscore taken for a shortcut marker. Every office parameter name
  here has an underscore.
- **What the Project Browser says is read in one place, `ProjectBrowser`** — folders, filter, sort
  value, the natural order — and laid out in one place, `FlatTree.Sync` (the merge that folds without
  `Clear()`). "Sheets" reads the sheet organization through it, "Views to Sheets" the view one; do not
  set up a third copy. The view organization differs in one thing: under the organization's own
  folders the browser always adds a view-type group ("Floor Plans", "Sections (Building Section)"),
  which `GetFolderItems` does not return — `ViewsToSheetsCommand.TypeGroup` adds it, and the groups
  are ordered by `Placeable`, as far as the browser's own order can be seen (the API does not expose it).
- **"Views to Sheets": one view, one new sheet, named after the view.** The numbers are worked out
  in the window, in the order of the table, from a prefix and a counter whose leading zeros are kept;
  a number the project already has (placeholder sheets included — they hold numbers too) is skipped,
  never clashed with, and the "Sheet" column shows the result before "Place". A view Revit will
  not put on a sheet (`Viewport.CanAddViewToSheet` — already on one, or empty) takes the sheet made for
  it back with it: an empty sheet left behind is one more thing to clean up by hand. A view already on
  a sheet (a view stands on one sheet only, a legend excepted) is checkable only "like a sample
  sheet", and then it is moved on its own sheet, never given a new one; which lines can be checked is
  worked out by the window per positioning (`ViewsToSheetsWindow.UpdateCheckable`), and a line that
  stops being checkable loses its check mark — a switch of positioning must not leave a move behind
  that nobody sees.
- **The titleblock's frame is measured, never read from its parameters.** "Sheet Width"/"Sheet
  Height" are whatever the family's author typed and say nothing about where the frame sits relative
  to the sheet's origin. So when the window opens, a sheet of every titleblock type is made in a
  transaction that is always rolled back (`SetClearAfterRollback(true)`, as `ScheduleTable.Probe`
  does), and each titleblock's `get_BoundingBox(sheet)` is its frame. It is measured once more on the
  first sheet of the real run, and that one wins. The margins are kept **per titleblock type**: where
  the title panel sits is a property of the titleblock.
- **Revit 2022 has no API from the model onto a sheet — and RevitAPI.xml 2022 says it has.**
  `View.GetModelToProjectionTransforms`, `Viewport.GetProjectionToSheetTransform` and
  `TransformWithBoundary.GetBoundary` are documented in the 2022 RevitAPI.xml, but RevitAPI.dll 2022
  (22.1.80) does not have them; they arrived in 2024 (confirmed by reflection over both DLLs — and by
  the first compile for 2022 failing on them). So "The crop region" and "One model point" centring
  **measure** where the crop region lands instead (`ViewsToSheetsCommand.MeasureCrops`): in a
  sub-transaction that is always rolled back, the view template comes off, `CropBoxVisible` goes on
  and `AreAnnotationCategoriesHidden` goes on — what is left on the sheet is the model clipped by the
  crop and the crop boundary drawn round it, so `GetBoxOutline` is the crop region. The drawing does
  not move on the sheet when what the view shows changes, so the rectangle read is where the crop
  region stays after the rollback. The measured box must match the crop box's size over the scale
  (2 mm); otherwise something besides the crop region is still on the sheet, and that view is centred
  by its viewport, with the reason in the report — never centred on a rectangle that is not its crop.
  A model point is then carried onto the sheet through the crop box's own frame
  (`CropBox.Transform.Inverse`, divided by the scale, offset from the measured crop centre) — exact for
  an unrotated viewport, and a new one is never rotated. A split crop region is left out up front:
  each region moves on the sheet by itself, and the crop box says nothing about where. When 2022
  support is dropped, the transforms can replace the measuring; until then there is one code path
  for every year, not two.
- **"Views to Sheets" checks what the user asked for, not what the API accepted.** `SetBoxCenter`
  is exact, so every viewport's box centre is read back after the commit; but for the crop region and
  the model point the request rests on the measurement, so the crop regions are measured again after
  the move, inside the same transaction (a sub-transaction needs one to hold it — after the commit
  there is none), and one off by more than 1 mm goes into "not where it was asked to go". The real
  outline, title included, is checked against the drawing area: a view larger than it is placed
  anyway and listed — the button never picks a scale to make a view fit; the scale is the user's
  choice in the window. A moved view is checked against its own sheet's titleblock with the margins
  remembered for that type (`ViewPlacementRequest.MarginsFor`).
- **The one-model-point centring lines a group up on the middle of all its crop regions**, not on
  the first view's: the set as a whole should sit in the middle. A group is the views looking the
  same way (`AlignGroup` — view and up directions, rounded), so floor plans line up with floor plans
  and ceiling plans with ceiling plans, whatever their scale.
- **The "Views to Sheets" table is a tree with check boxes on the folders**, and a folder's box is a
  summary, not a value: `UpdateFolders` works it out from the views under it that the search shows
  (checked, cleared, or partly — `null`), and a click on it goes to those views through
  `ViewPlacementRow.Toggled`. A partly checked folder clears on a click, the same as the header box —
  WPF's own `null → false` step, kept rather than overridden, so both boxes behave alike.
- **The scale is set on the view, after it is on its sheet, and read back.** `View.Scale` is set in
  `Prepare` once `Viewport.Create` has succeeded — a view Revit refuses to place must not be left with
  a changed scale — and before the regeneration, so every measurement after it sees the new scale.
  Two things decide the scale besides the user and are caught in the window first: a **view template
  that controls it** (a scale parameter in `GetTemplateParameterIds` and not in
  `GetNonControlledTemplateParameterIds` — the cell is locked and "State" names the template; asked
  once per template), and **dependent views**, which share one scale with their primary
  (`ScaleGroup`): the row says the others change too, and two of them given different scales block
  "Place" — one of the two would lose silently. After the commit every changed view's `Scale` is read
  back; a refusal goes into "Scale not changed", not into the success list.
- **"Like a sample sheet" repeats a viewport placed by hand, and that is why it can move views already
  on sheets.** The sample is a viewport the user adjusted; the window offers the selected viewport,
  or the one on the open sheet (a model view before a legend), and lists every viewport on a sheet.
  A view in the sample's `AlignGroup` puts the middle of the sample's crop region — a model point —
  where it stands on the sample's sheet, through the same crop measuring as the model-point centring
  (`SampleAimOf`); any other view, or every view when the sample has no crop region, puts its box
  centre on the sample's box centre. Positions are sheet coordinates, from the sheet's origin — the
  window warns when the sample's titleblock is not the new sheets' one. The sample itself is never
  checkable. A moved viewport keeps its pin: it is unpinned for `SetBoxCenter` and pinned again.
- **The views are checked out before the transaction** (`ViewsToSheetsCommand.Borrow`), every view
  and every viewport to be moved — scale and placement are edits of the view. Held by another user or
  changed in the central model, a view is named in "Could not be placed" and left out: left to the
  commit, either one would fail the whole batch, not itself. The same recipe as "Move to Workset".
- **"Sheet Scale" exists because "Views to Sheets" cannot rescale what is already placed — by
  design.** That window is "one checked view, one new sheet": a view already on a sheet is checkable
  only "like a sample sheet", which also moves it, and a view whose template sets the scale has its
  cell locked. Both are right for that button and were exactly why "1:100 sheets to 1:200" did
  nothing there (reported October 2026). Do not fold a rescale-in-place mode into "Views to Sheets":
  its checkability rules are built round placing and moving. It points at "Sheet Scale" instead —
  in "State" for placed views and for template locks.
- **A view template that sets the scale is the usual blocker, and the window asks what to do with it
  rather than refusing.** On a real sheet set nearly every view has a template, and most templates
  tick "View Scale" in "Include"; the view's own scale cannot be changed past that. Three answers
  (`ScaleTemplateMode`): **Release** (default) unticks the scale in the template —
  `ViewScale.Release` adds the scale parameters to `GetNonControlledTemplateParameterIds` and writes
  it back with `SetNonControlledTemplateParameterIds`; every view using the template keeps the scale
  it has, and the checked ones get the new one — the only answer that changes exactly what was
  checked. That the others keep theirs is how unticking "Include" behaves in Revit's own dialog, and
  it is not taken on trust: their scales are recorded before and read back after the commit, and one
  that moved goes into "Changed although nobody checked it". **ChangeTemplate** gives the template the
  new scale (`View.Scale` on the template, `VIEW_SCALE` as a fallback), and every view using it
  follows, checked or not — right when the template is meant for one scale; the window counts the
  template's users and the confirmation lists the templates. **Leave** makes such views uncheckable.
  Detaching the template from the view is deliberately not offered: it drops everything else the
  template controls. The template edit is part of the same transaction and the same Ctrl+Z.
- **The scale is changed on the view that holds it.** A dependent view shares its primary's scale
  (`ViewScale.Owner` — the primary of a dependent, else the view), and so does every other dependent;
  the template that matters is the primary's. The command groups the checked views by owner, changes
  each owner once, and reads every member back — the ones nobody checked go into "Changed along with
  them", never silently. The window shows the same as "changes too" on their lines. A legend standing
  on several sheets has one scale, so all its lines share one check box (`SheetScaleWindow._byView`).
- **What decides the scale is read again from the model at run time**, not taken from the window's
  snapshot — a template's "Include" may have changed while the window was open. A template that
  controls the scale under "Leave" becomes a "Could not be changed" line, not a silent skip.
- **The middle of every viewport is put back where it stood.** How Revit grows or shrinks a viewport
  round a new scale is not documented, and annotation that keeps its size on paper shifts the box
  besides; a sheet set laid out by hand must not come back rearranged. So every viewport of every
  view the run touches has `GetBoxCenter` recorded before, and after one regeneration any that moved
  more than half a millimetre gets `SetBoxCenter` (unpinned and pinned again, as in "Views to
  Sheets"). After the commit every centre is read back — "Not where they stood" otherwise.
- **"Sheet Scale"'s drawing area is the titleblock less the margins "Views to Sheets" keeps for that
  titleblock type** (`ViewsToSheetsPreferences.Margins`), read, never written: where a title panel
  sits is a property of the titleblock, set once there. The window's "may stick out" is an estimate
  (the box grown round its middle by old/new scale — text and tags keep their paper size); the
  report checks the real outline, title included.
- **"Change the checked views at" is a checkability rule, not a find filter.** A view at another
  scale is greyed and loses its check mark — the "a line that stops being checkable loses its check
  mark" rule of "Views to Sheets" — so a 1:20 detail on a 1:100 sheet cannot ride along unseen. It is
  deliberately not remembered between runs: the next window would open with most views greyed for a
  reason nobody chose that day. "Find" in the same window only finds (see "Conventions").
- **Who decides a view's scale is answered in one place, `ViewScale`** — whether a template controls
  it, releasing it, the owner of a dependent's scale. "Views to Sheets" (`ScaleLock`) and "Sheet
  Scale" both use it; do not set up a third copy of the template check.
- **"Copy Parameters" knows a parameter across families by `ParameterKey`, and a family parameter only
  by its name.** A built-in parameter is the same everywhere by its `BuiltInParameter` value, a shared
  one by its GUID; a project parameter, or a family parameter that is not shared, has no identity across
  families but its name — "Width" in two door families are two definitions with nothing in common except
  what somebody called them, and that is exactly what "the Width of every door" means. A name is matched
  only among parameters that are neither built-in nor shared (`ParameterKey.On`), so a family's own
  "Comments" is never taken for Revit's. The instance/type level is part of the key: a parameter that is
  an instance one in one family and a type one in another is two list entries, because it is written in
  two different places. The key is text (`I|BIP|-1010106`, `T|GUID|…`, `I|NAME|Width`) — what the
  settings remember, so the next window opens on the same pair in another project.
- **The lists are read off one element of each type, not off every element.** `GetOrderedParameters`
  (what the Properties palette shows — the same choice "Translate" makes) on a few hundred
  representatives costs nothing; on fifty thousand pipes it would cost seconds for counts nobody needs
  exact. So "Elements" counts the elements of the types that have the parameter; the preview then asks
  every element for real, and an element that turns out not to have it is a line "left as it is".
- **A value changes kind by "what a person would have typed", never by Revit's internal number.**
  `Parameter.Set` refuses a value of the wrong storage type, so every pair needs a rule, and all of them
  live in `ParameterValueConverter.TryConvert`: into text — the value exactly as Revit shows it
  (`AsValueString`: the project's units and rounding, Yes/No in Revit's own word, an element by its
  name), optionally without the unit symbol (the project's `FormatOptions` copied with
  `SetSymbolTypeId(new ForgeTypeId())` — the unit and the rounding stay, only the symbol goes); text into
  a number — Revit's own parser with the project's units (`UnitFormatUtils.TryParse`: "1200", "1200 mm",
  "1.2 m"), then a bare number, then, only if asked, the first number in the text; a number into a number
  of the same spec — copied exactly; of another spec — the **figure the source displays**, converted
  through the project's display unit of each (`ConvertFromInternalUnits` / `ConvertToInternalUnits`): a
  length showing 1200 mm gives a plain number 1200, not the 3.937 feet Revit keeps inside, which no user
  has ever meant; into a whole number — rounded, and the line says so; into Yes/No — zero / "no" is No
  (`ScheduleValues.TryYesNo`, shared with the "Excel" import, Russian words included). An element choice
  is written only from another element choice: a name says nothing about which kind of element the
  target chooses, so guessing a level or a material by name is deliberately not attempted. A value that
  cannot be converted is never guessed at — its line is red with the reason, and the report groups the
  reasons (the converter returns one of a few fixed sentences for exactly that).
- **A read-only parameter is a source, never a target.** Reporting parameters, formula-driven ones and
  what Revit computes (area, volume) all come out of the API as `IsReadOnly`, and copying *from* them is
  half of what the button is for (a reporting width into a shared parameter a schedule can show). The
  "Copy into" list hides them unless asked, and shows them greyed then; there is no way to tell a
  reporting parameter from a formula from the API in a project, so the wording names all three.
- **A type parameter as the target is written per type, and the elements of the type in scope must
  agree.** A type holds one value; when it comes from an instance parameter and two elements of the type
  give two values, the line is "cannot be written" with both values and their counts — letting whichever
  element came last in the collector win would be a silent, order-dependent choice. Elements that give no
  value (an empty source) do not block the others; the line says how many they are. Writing a type changes
  its elements outside the scope too, so the window asks before copying, and the report says so.
- **An element in a model group is not written unless the target varies by group instance** — the
  "Excel" import's rule, for the same reason: left to the commit, Revit's refusal of a group edit rolls
  back the whole batch, not just that element.
- **The "Copy Parameters" preview has no check boxes.** The scope (selection / active view / model) is
  the choice of elements; a line per element with a box of its own would be a second selection over the
  first one, on tables of thousands of lines. "Show" above the preview therefore only finds — "Copy"
  writes every line that will be written, and the status line says how many of those are out of sight
  (see "Conventions"). "Find" over each parameter list is different: the parameter it hides is no longer
  the chosen one (the preview clears) — the choice comes back when it is shown again.
- **"Link Updater" repoints a link with `LoadFrom`, never by deleting it and linking the server model
  anew.** `LoadFrom` keeps the `RevitLinkType` and its instances the same elements, so placement, pins,
  the project workset, view overrides and — the one that cannot be rebuilt through the API at all —
  Copy/Monitor relations survive (see the "Copy-monitoring does not exist in the Revit API" entry). The
  price is the undo history, said in the confirmation. `TransmissionData` (rewriting a closed model's
  link paths) was not an option: it is for closed models and, by RevitAPI.xml, skips cloud references —
  exactly the ones to change.
- **A link is matched to a server model by its name, because that is all that survives the move.** A
  BIM360 link knows a region and a pair of GUIDs, and nothing on Revit Server answers to them. The name
  is compared case-insensitively without ".rvt" (`ServerModelIndex.Named`); only when nothing matches
  is a loose name tried (`Resembling`: letters and digits only, a trailing `_R22`/`-R2024` version
  suffix dropped). The window's rule for checking a row by itself (`LinkUpdaterWindow.Certain`) is the
  same "several matches mean the user chooses" as the kit and the workset guess: exactly one model of
  exactly the name, and a link that is not a plain file. Several of one name — the nearest is chosen
  (strictly nearer only; a tie chooses nothing) but left unchecked; a loose name — offered, red,
  unchecked; a file link — never checked by itself, it may be a local model on purpose. Picking from
  the drop-down or "Choose on server…" is the user's word and checks the row.
- **The search starts where the open model lies and climbs only while something is missing.** The
  open model's own folder (`LinkCatalog.Host` → the central's `RSN://` path) with everything under it,
  then a level up, while a link has no namesake in what was read (`LinkUpdaterWindow.Climb`) — but never
  onto the server root by itself: that is every project of the office, and a link missing from this
  project's folders is far more likely not moved yet. "Search wider" climbs past that by hand. The
  index only grows (`ServerModelIndex.Walk` skips a folder read before, with its subtree), so widening
  costs only the new folders; `FolderLimit` (400) bounds a window, and hitting it is said on screen.
  "Nearest" is the tree distance from the open model's folder (`ServerModelIndex.Distance`): a model in
  the project's own discipline folder beats its namesake in an archive.
- **The worksets inside a link are passed as `null` to `LoadFrom`, then checked.** RevitAPI.xml: null
  loads "the same set of worksets the link previously had" — the right request for a model that kept
  its worksets on the move. Whether Revit honours that for a model from another place is not taken on
  trust: for a link loaded before the run, the open/closed state of its user worksets is read by name
  before and after (`LinkPreferences.NormalizeWorkset`, case ignored — the `SameWorkset` rule); a
  difference triggers a second `LoadFrom` with the setup spelled out from the new model's own worksets
  in the "close all, open the listed ones" phrasing, and the report keeps "healed", "still different",
  "open before but missing in the server model" and "could not be compared" apart. A link not loaded
  before (a BIM360 model nobody can reach any more) has nothing to compare with, and is listed as
  "as Revit remembered" — never counted as checked. No cloud model is opened for any of this.
- **A link loaded from BIM360 is not a file path, and `LinkCatalog.Describe` cannot see it.** Revit
  keeps a model linked from the cloud as an external resource of its cloud server:
  `ExternalFileUtils.GetExternalFileReference` throws for it, so `Describe` returns null — the first
  version of "Link Updater" showed such links as "(Revit does not give the path)" and refused them,
  exactly the links the button exists for (October 2026, a loaded `MK3-SLS-K5-FAS.rvt`). They are read
  through `Element.GetExternalResourceReference(BuiltInExternalResourceTypes.RevitLink)` instead
  (`InSessionPath` for the "Points to" column; the type's name to search the server by; the type's
  UniqueId as the key — the reference information's keys are undocumented, so no GUIDs are parsed out
  of it). Repointing such a link (`LinkPathInfo.IsResource`) tries two routes in turn
  (`LinkUpdaterCommand.Load`): `LoadFrom(ModelPath)` and, if that is refused, throws, or reports
  success with the link still on the cloud, `LoadFrom(ExternalResourceReference)` with
  `ExternalResourceReference.CreateLocalResource(…, PathType.Server)` — RevitAPI.xml says which
  overload a resource link needs nowhere, and neither has been seen on a live model yet. The route
  that took is used again for the workset second load. **`Link Manager` has the same blind spot**:
  `LinkCatalog.Existing` skipped every link `Describe` returns null for, so a BIM360 link was missing
  from its list, looked new, and picking the model again sent it to `RevitLinkType.Create` a second
  time. There the GUID pair is now read off the loaded link's own document (`LinkCatalog.CloudLink`,
  `GetCloudModelPath` — the pair a link is created from, so the key matches the model picked from
  BIM360, and "Load" reloads it with `LoadFrom(ModelPath)`, which RevitAPI.xml says takes a cloud
  path). An unloaded one has no document to ask and stays unlisted; creating a cloud model whose name
  such a link carries is refused (`LinkCatalog.HasUnreadCloudLink`) with "load it in Manage Links". A link through
  Desktop Connector (`…\ACCDocs\…`, older `…\BIM 360\…`) is an ordinary file path to Revit, but counts
  as a BIM360 link for the window's own check (`IsCloud`), not as a plain file link.
- **A link's new path is read back, not taken from the result code.** `LoadFrom` returning success is
  followed by reading the same link type again (`LinkUpdaterCommand.PathNow`: its file path through
  `LinkCatalog.Describe`, else the resource's `InSessionPath`) and a comparison with the target, case
  and slash direction aside; a mismatch sends a resource link down the second route and is otherwise a
  failure line, and a path that took but a model that did not load (`RevitLinkType.IsLoaded`) is a
  section of its own.
- **Two links can never be given one server model, and a model already linked cannot be chosen.**
  Revit holds one `RevitLinkType` per model; the window blocks "Update paths" on a clash between
  checked rows and makes a row uncheckable whose target is another link's current path — removing one
  of the two links is the user's call, in Manage Links.
- **The drop-down binds `LinkUpdateRow.Picked`, which ignores a null.** A WPF combo box pushes a null
  into its `SelectedItem` source when its items are replaced (every wider search rebuilds them) or its
  row unloads; there is no empty item to pick, so a null from the combo is never a choice. The window
  sets `Target` itself (null included), and `Picked` mirrors it. The same one-binding rule as the
  "DataGridComboBoxColumn" entry; the template is the window's own because every row has its own list.

## Storing user settings

The `%AppData%\VladTools\` folder holds all state outside the Revit document. All of it survives
across documents and Revit sessions, all UTF-8 with a BOM — so Cyrillic and other non-Latin text
opens correctly in Notepad.

`formulas.txt` and `names.txt` are user settings: read every time their window opens and rewritten
whenever it closes (including "Close" and Esc). `dimensions\` is not a setting but a cache: written
when a scan actually runs, not when the window closes. `links\` is both: `_settings.txt` is
rewritten whenever the "Link Manager" window closes, while the sets themselves are only saved with
the "Save set" button. `basefile\` holds settings only. `schedules\` is both too — and the one
folder whose contents are not text at all: a set of schedules is a Revit file (see "Key decisions").
`parameters\` follows the `links\` shape most closely: `_settings.txt` only ever changes when the
window closes, and a set itself only through its own "Save set" button — table edits (categories,
binding, group) are not written until then. `coordination\` holds settings only, and has no sets of
its own on purpose: the model list there is the same `LinkSetLibrary` one "Link Manager" uses.
`translate\` is data, not settings: the dictionary there is rewritten whenever the "Translate" window
closes, with every translation the window held (see "Key decisions"). `sheets\` is both:
`_settings.txt` is rewritten when either of its windows closes, and `history\` is data — the change
history, rewritten at every opening of the window, before it is shown, and for every model a batch
opens as itself (never for a detached copy — see "Key decisions"). The batch's reports themselves are
not kept here: they go wherever the user saves the summary.
`viewstosheets\`, `sheetscale\`, `copyparams\` and `linkupdater\` hold settings only.

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
separated by a vertical bar: `FILE | path`, `SERVER | RSN://…`, `CLOUD | region | project GUID | model GUID | name`. The last field may hold the project workset to put the link into; without it,
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

`schedules\<Revit year>\<set name>.rvt` — a saved set of schedules for the "Schedule Library"
button. This is the one setting in the add-in that is not a text file: the set **is** a small Revit
project holding nothing but the schedules themselves, and a schedule is inserted by copying it out
of there (the reasons are in "Key decisions"). The year in the path is not tidiness either: a .rvt
never opens in an older Revit than the one that wrote it. A set is only ever written by the "Add to
the set from…" buttons and by "Remove from set"; Revit's own backups (`<name>.0001.rvt`) are swept
away together with the set, otherwise "deleted" would leave its contents lying on disk. A set file
can be copied to a colleague as it is — that is the whole point of keeping it as a file.

`schedules\_settings.txt` — that window's settings: `KEY = value` — `SET` (the set worked with
last), `MODEL` (the model the schedules were last taken from, in the same line format as a link
set) and `ACTION` (`Skip`/`Replace`/`AddCopy` — what to do by default when the project already holds
a schedule of that name; `Skip`, because a button that inserts must not quietly delete somebody's
schedule). With no `SET` line and no sets on disk, the window opens on
`ScheduleLibrary.DefaultSetName` rather than on an empty name box: an empty one turns the very first
press of "Open project" into a refusal ("type in a name first") instead of the thing the user came
to do — the first run must not begin with a scolding. It lies one level above the sets, shared by every Revit year: a set name is worth
remembering across them. The window also reads and adds to the Revit Server names in
`links\_settings.txt`, for the same reason as "Base File".

`parameters\<set name>.txt` — a saved bundle of shared parameters for the "Parameter Sets" button.
One line per parameter, fields separated by a vertical bar:
`PARAM | GUID | name | Instance|Type | varies across groups: 1|0 | group ForgeTypeId | categories, comma separated`.
The GUID is the parameter's real identity (see "Key decisions") — the name is only a label kept for
display, and is re-read whenever the parameter is picked again from the shared parameter file. Only
written by the "Save set" button, unlike `links\` and `autodim\`, whose `_settings.txt` is rewritten
on every close: nothing here is session state, so there is nothing to save except on request.

`parameters\_settings.txt` — that window's settings: `KEY = value` — `SET` (the set worked with
last) and `FILE` (the shared parameter file last browsed for definitions, so "Add from shared
file…" does not ask again). The "_settings" name is taken by this file, by the same rule as
`links\` and `schedules\`.

`coordination\_settings.txt` — the settings of "Accept Changes → Other models…": `KEY = value`, the
keys `WORKSET` and `MODEL` may repeat. `WORKSET` is the **start** of the name of a workset to open in
somebody else's model (`00_Link_BM` also opens `00_Link_BM_K3`); no such line at all means
`BatchCoordination.DefaultWorksets`, and on closing the window the list actually in effect is written
back in full, so there is something to edit. `LEVELS` and `RENAME` (`1`/`0`) — whether to put levels
in line with the file as well and whether to rename; both default to off, see "Key decisions".
`SET` is the link set worked with last, and the `MODEL` lines are the model list the window was
closed with, in the same line format as a link set (`LinkSetLibrary.Format`/`Parse` — no second
parser). Sets themselves live in `links\`, shared with "Link Manager": a list of a project's models
is the same list in both buttons.

`translate\dictionary.txt` — the "Translate" button's dictionary: one line per text, the Russian
original, a TAB, the English translation. Inside a text `\n` is a line break, `\t` a tab, `\\` a
backslash, and a leading `\#` a literal `#` (a bare `#` starts a comment). Both texts are normalised
(`TranslationDictionary.Normalize`). An exported file has the same format plus a third column —
where the text is used — ignored on import; a line with an empty second column means "not translated"
and is skipped. A file Excel saved as "Unicode Text" (UTF-16 with a BOM) reads as well as our own
UTF-8 one.

`translate\_settings.txt` — that window's settings: `KEY = value` — `FOLDER` (the folder the last
export or import went through).

`excel\_settings.txt` — the "Excel" button's settings, and the only thing in `excel\`: the button
keeps no data of its own (an exported workbook carries its own bookkeeping, on its hidden sheet).
`KEY = value` — `FOLDER` (the folder of the last export or import), `LAYOUT` (`AsInRevit` /
`PlainTable`), `OPEN_AFTER`, `EMPTY_CLEARS` (default `0` — see "Key decisions") and `SELECT_AFTER`
(`1`/`0`). Rewritten whenever one of the button's windows closes. Reading a schedule also writes a
throwaway shared parameter file, `%TEMP%\VladTools-rowkey-<guid>.txt`, deleted again in a `finally`
— it is not a setting and never outlives the read.

`sheets\_settings.txt` — the "Sheets" windows' settings: `KEY = value` — `NUMBER` (the parameter
shown as a sheet's number: `GUID | name` for a shared one, the name a label only; `NAME | name` for one
that is not shared, found by that name; empty or unreadable means Revit's own Sheet Number; both
windows read and write it), `NUMBER_FILE` (the shared parameter file it
was last picked from), `FOLDER` (the folder of the
last export), `OPEN_AFTER` (`1`/`0` — open the exported file in Excel), and for "Several models…":
`BATCH_FOLDER` (the folder of the last summary), `SET` (the link set worked with last) and `MODEL`
lines (may repeat — the batch's model list from last time, in the same line format as a link set,
`LinkSetLibrary.Format`/`Parse`; the sets themselves live in `links\`, shared with "Link Manager" and
the "Accept Changes" batch). Rewritten whenever either window closes, each one loading the file
fresh first, so neither loses what the other wrote. The "Show" choice is not kept: the window always
opens on every sheet.

`sheets\history\<model name>_<path hash>.txt` — the "Sheets" change history, one file per model,
keyed by the central model's path for a workshared one (`LinkCatalog.VisiblePath`) so every local
copy shares it; the hash keeps two models of one name apart, the same as `dimensions\`. Two
`KEY = value` lines — `STARTED` (the first check) and `LAST_CHECK` (the latest), `yyyy-MM-dd` — then
one line per sheet and per view on a sheet, fields separated by a vertical bar:
`UniqueId | fingerprint | None/Added/Changed | the check that found it | the check before it`. The
fingerprint is a `ViewFingerprint` digest (20 hex characters); `None` has both dates empty. Rewritten
at every opening of the window, and for every model a batch opens as itself, with what is on sheets
now; a detached copy a batch reads is looked up by the path it was opened from, and never written
(see "Key decisions"). A file with records but no `STARTED` line
counts as no history at all. Deleting the file starts the history again from the next check. Not
written over when it could not be read (see "Key decisions").

`viewstosheets\_settings.txt` — the "Views to Sheets" window's settings: `KEY = value` —
`TITLEBLOCK` (the titleblock type used last, `Family : Type`), `CENTER` (`Viewport` / `CropRegion` /
`ModelPoint` / `Sample` — `Sample` falls back to `Viewport` in a project with no viewport on a sheet;
the sample itself is not remembered, it belongs to one project), `PREFIX` and `START` (the sheet
numbering; `START` is kept as text, so `001` keeps its zeros), `SHOW_PLACED` (`1`/`0` — show the
views that cannot be checked), `SCALE` (the text last in "Scale for the checked views") and `MARGINS`
lines, one per titleblock type ever used:
`Family : Type | left | top | right | bottom`, millimetres, invariant decimal point; the name is
everything before the last four fields, so a vertical bar in a type name does not break the line. A
type with no line gets `ViewsToSheetsPreferences.DefaultMargins` (20 / 10 / 10 / 10). Rewritten
whenever the window closes. "Sheet Scale" reads the `MARGINS` lines too (its drawing area), and
never writes this file.

`sheetscale\_settings.txt` — the "Sheet Scale" window's settings: `KEY = value` — `SCALE` (the new
scale typed or picked last, as text) and `TEMPLATES` (`Release` / `ChangeTemplate` / `Leave` — what to
do with a view whose template sets the scale; default `Release`). "Change the checked views at" is not
kept, on purpose (see "Key decisions"). Rewritten whenever the window closes.

`copyparams\_settings.txt` — the "Copy Parameters" window's settings: `KEY = value` — `CATEGORY` (the
category used last, by its name), `FROM` and `TO` (the two parameters as `ParameterKey` text:
`I|…` instance / `T|…` type, then `BIP|<built-in id>`, `GUID|<shared GUID>` or `NAME|<name>`; the line
splits at its first `=`, so a name may hold one), and the check boxes `ONLY_EMPTY`, `EMPTY_CLEARS`,
`NO_SYMBOL`, `NUMBER_FROM_TEXT`, `SHOW_READONLY` (`1`/`0`). A remembered category or parameter missing
from the open project is simply not preselected. The scope is not kept: it follows the selection.
Rewritten whenever the window closes.

`linkupdater\_settings.txt` — the "Link Updater" window's settings: `KEY = value` — `ROOT` (the Revit
Server folder last added with "Another folder…", as a `ModelFolder.Format` line: `SERVER | RSN://…`).
It is a fallback, read only when the open model does not lie on a Revit Server itself (still in BIM360,
a file, open detached); otherwise the search starts from the model's own folder. The server names the
browser offers are not here — the window reads and adds to `SERVER` in `links\_settings.txt`, as
"Base File" does. Rewritten whenever the window closes.

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
  "Key decisions") — except its own deletions, which stay unchecked behind a box of their own, and
  that is the same rule holding inside a button that mostly does not delete. The second rule — "only what is shown gets applied" — holds wherever the filter
  **is** the selection: a row that leaves the table through it loses its check mark. The
  exceptions are search boxes and lists that only ever *find*: `CategoryPickerWindow` (check "Walls", type
  "door" to reach the next one, and the box out of sight stays checked), `ExcelExportWindow` (the
  same, over schedules), `ViewsToSheetsWindow` (find "Level", tick, find "Section", tick — the first
  round must survive the second), `SheetScaleWindow` (the same, over sheets — its "at 1:100" list is
  not a find filter but a checkability rule, and that one does uncheck), and `TranslateWindow` and `ExcelImportWindow`, whose search box and "Show"
  list find a row among hundreds without touching any other row's check mark — looking at the rows
  that cannot be written must not uncheck the ones that can. Their confirmations say how many of the
  rows going in are out of sight. `CopyParametersWindow`'s "Show" above the preview is the same kind:
  the preview has no check boxes (the scope is the selection), "Copy" writes every line that will be
  written, and the status line says how many of those are out of sight. Adding a filter that selects
  for the user means the rule comes back with it.
- **Name a WPF binding path with `nameof`, never a bare string**, and never rely on
  `GridBuilder.CheckBoxTemplate()`'s default path for a row whose flag is not called `IsSelected`.
  A binding to a property that does not exist fails **silently**: an unbound `IsChecked` still ticks
  on screen and the value simply never reaches the row. That is exactly how the category picker came
  to look as if it refused to remember anything.

## Porting to another Revit version

The add-in currently builds for **Revit 2022, 2024 and 2025** from one source tree, with not a
single `#if`: for every API removed or deprecated in a newer version, a replacement exists across
every year. The year is a build key, `-p:RevitVersion=<year>` (defaulting to `2022`, set in
[Directory.Build.props](src/VladTools/Directory.Build.props)); it drives `RevitDir`,
`RevitAddinsDir`, `Product`, the **target framework** in
[VladTools.csproj](src/VladTools/VladTools.csproj) (see "Key decisions", TFM by year) and the
separate `bin\R<year>\` / `obj\R<year>\` folders (details and the related MSB3539/CS0579 build trap
are in "Build and run"). `RevitServerClient.ServiceVersion` and `ScheduleLibrary.Year` need no
manual edit — `App.OnStartup` sets both from `ControlledApplication.VersionNumber` (see "Key
decisions"). A new year does start with an empty schedule cache, and that is by design rather than
an oversight: the sets are .rvt files, and one written by a newer Revit will not open in an older
one — they are captured again in the new year. `build.ps1` with no arguments builds all three years
at once, skipping any that are not installed.

The full rundown for each move is in [CHECKLIST-Revit2024.md](CHECKLIST-Revit2024.md) and
[CHECKLIST-Revit2025.md](CHECKLIST-Revit2025.md). Briefly, what is left as a warning (not an
error) and does not need to be touched:

- **`ElementId.IntegerValue`** (`CleanupCommand.SafeName`, `RenameNestedFamiliesCommand`) — deprecated
  since 2024, but there is no replacement that also works in 2022: `ElementId.Value` only arrived
  in 2024. The `CS0618` on 2024/2025 is left in place deliberately, for as long as 2022 is needed too.
- **`WebRequest.Create`** (`JsonHttp.cs`) — deprecated on .NET 8 (`SYSLIB0014`, visible only when
  building for 2025); moving to `HttpClient` is a separate task, unrelated to compatibility.

**The one API in the add-in with a lower bound inside a supported year:** `WorksetTable.DeleteWorkset`,
`WorksetTable.CanDeleteWorkset`, `DeleteWorksetSettings` and `DeleteWorksetOption` are all marked
`<since>2022.1</since>` in `RevitAPI.xml` — they do not exist in the original Revit **2022.0**
release. Everything else this project uses predates it. The build takes its reference from whatever
`RevitAPI.dll` the machine has, so this only bites on a 2022 that was never updated: there the
"Worksets" button fails to load, and the fix is Revit's own update, not a code change. Checked by
reflection on 2022, 2024 and 2025 — identical signatures in all three.

**RevitAPI.xml is not proof that a member exists — reflect over RevitAPI.dll.** The 2022 RevitAPI.xml
documents `View.GetModelToProjectionTransforms`, `Viewport.GetProjectionToSheetTransform` and
`TransformWithBoundary.GetBoundary`/`GetModelToProjectionTransform`, but RevitAPI.dll 2022 (22.1.80)
has none of them — they are in 2024 (see "Key decisions", "Views to Sheets"). Check a new API in the
DLL of every year (`Assembly.ReflectionOnlyLoadFrom` under Windows PowerShell works for 2022/2024;
2025 is a .NET 8 assembly and needs `MetadataLoadContext` or simply a compile for 2025), or by
compiling for every year — which `build.ps1` does anyway.

Already done and no longer needing attention: `Definition.ParameterGroup` together with
`LabelUtils.GetLabelFor(BuiltInParameterGroup)` (`GroupName` in both delete-parameters commands) —
in Revit 2025 the `BuiltInParameterGroup` type itself is removed entirely; this was the one
compile error on the move to 2025 — replaced with `Definition.GetGroupTypeId()` +
`LabelUtils.GetLabelForGroup(ForgeTypeId)`, present and not deprecated in all three years.
`new ElementId(BuiltInCategory)` — **confirmed not deprecated** in any version, no need to touch it.
`ScheduleFieldType` gained `CustomField`, `HostCount`, `Revision`, `Sheets` and `Views` in 2024 — so
`ScheduleTable.ReadOnlyReason` names only the members present in 2022 and sends every other one to
its `default` branch (read-only); a 2024-only member in a `case` would not compile for 2022. Every
other API the "Excel" button uses (`ViewSchedule.RefreshData`, `UnitFormatUtils.TryParse` with
`ValueParsingOptions`, `ExternalDefinitionCreationOptions(string, ForgeTypeId)`,
`BindingMap.Insert(Definition, Binding)`, `ScheduleField.GetSpecTypeId`/`DisplayType`) was checked
against RevitAPI.xml of all three years — present in each, with the same signature. The same goes
for what "Sheets" uses: `BrowserOrganization.GetFolderItems`/`SortingParameterId`/`SortingOrder`,
`FolderItemInfo`, `ImageInstance` (since 2020) and `PanelScheduleSheetInstance`. What "Views to
Sheets" uses — `ViewSheet.Create`, `Viewport.Create`/`CanAddViewToSheet`/`GetBoxOutline`/
`GetLabelOutline`/`GetBoxCenter`/`SetBoxCenter`, `BrowserOrganization.GetCurrentBrowserOrganizationForViews`,
`View.CropBoxVisible`/`AreAnnotationCategoriesHidden`/`Outline`/`Scale` (setter)/`GetPrimaryViewId`/
`GetDependentViewIds`/`GetTemplateParameterIds`/`GetNonControlledTemplateParameterIds`,
`BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC`/`_IMPERIAL`/`VIEW_SCALE`, `Element.Pinned`,
`ViewCropRegionShapeManager.Split`, `SubTransaction` — was confirmed by reflection over RevitAPI.dll
2022 and 2024 and by compiling for all three. "Sheet Scale" adds `View.SetNonControlledTemplateParameterIds`
(`<since>2013</since>`), checked the same way. The "Sheets" change dates use `Element.VersionGuid`,
`ElementOwnerViewFilter(ElementId, bool)`, `View.GetPrimaryViewId` and `View.ViewTemplateId` — all
in RevitAPI.xml 2022, and compiled for all three years. Its batch adds
`OpenOptions.DetachFromCentralOption` (`DetachAndPreserveWorksets`, `<since>2013</since>`),
`WorksetConfigurationOption.CloseAllWorksets`, `PushButtonData.AvailabilityClassName` and
`IExternalCommandAvailability` — long-standing API, compiled for all three years. Its number
parameter reads `Element.get_Parameter(Guid)`, `Element.GetParameters(string)`, `Parameter.IsShared`/`GUID`,
`SharedParameterElement.Lookup` and `Definition.GetDataType` — compiled for all three years too. "Copy Parameters"
uses `LabelUtils.GetLabelForSpec`, `FormatOptions(FormatOptions)` with `CanHaveSymbol` /
`SetSymbolTypeId`, `Parameter.AsValueString(FormatOptions)`, `Element.GetParameters(string)`,
`Category.AllowsBoundParameters` and `UnitFormatUtils.TryParse`/`Format` — all in RevitAPI.xml of the
three years with the same signatures, and compiled for all three. Its value class is `SourceValue`, not
`ParameterValue`: `Autodesk.Revit.DB.ParameterValue` exists, and the name clashes (CS0104). "Link
Updater" uses `RevitLinkType.GetLinkedFileStatus` (`<since>2018.2</since>`; `LinkedFileStatus` has
`LocallyUnloaded` and `InClosedWorkset` in 2022 already), `RevitLinkType.LoadFrom(ModelPath,
WorksetConfiguration)` with a null configuration (documented as "the same set of worksets the link
previously had" in 2022 and 2024 alike) and `RevitLinkType.IsLoaded`; for links kept as cloud
resources, `Element.GetExternalResourceReference`, `ExternalResourceTypes.BuiltInExternalResourceTypes.RevitLink`,
`ExternalResourceReference.InSessionPath`/`CreateLocalResource` and `RevitLinkType.LoadFrom(ExternalResourceReference,
WorksetConfiguration)` — all in RevitAPI.xml 2022, compiled for all three years.

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
