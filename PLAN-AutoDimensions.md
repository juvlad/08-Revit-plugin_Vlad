# Plan: the "Auto Dimensions" button (a counterpart to Auto Dim Lines from KS Plugin)

A document — a task for the executor (the Sonnet model). Work strictly stage by stage: every stage
ends with a build and a **manual check in Revit**, there is no test project in the repository.
Do not start the next stage until the previous one has been checked in Revit.

Every project convention from [CLAUDE.md](CLAUDE.md) applies without exception: user-visible text
and comments, WPF without XAML, windows that know nothing about the Revit API, one transaction per
batch, errors gathered into a list and shown in a `TaskDialog`, no `#if` at all (it must build for
2022, 2024 and 2025 from one source tree).

---

## 1. What the button does

Project panel, the **"Auto Dimensions"** button, the `AutoDimensionCommand` command.

The user once placed a few dimension chains by hand along one wall of one room (six, in the KS
Plugin demo). From there they select rooms and press the button — and the same chains appear along
every side of every selected room: the same offset from the wall, the same dimension type, the
same set of ticks (overall / openings / opening centres / adjoining partitions).

The routine job is an architectural masonry plan: dimensions run to the core layer faces, into the
room, as several chains.

### The key architectural decision (settled — do not revisit without a reason)

The sample is **not copied literally by its references** — a sample's references are tied to
specific walls and are meaningless in another room. Instead a **catalogue of chain kinds** is set
up (a fixed list of "what to pick up"), and parsing the sample is a guess: for every sample
dimension, its chain kind, its offset from the wall and its dimension type are worked out. The
parsed result is shown in the window as a table and can be edited by hand.

A direct consequence follows: parsing a sample is a **heuristic, not a parser**, in exactly the
same status as `FormulaParser`. A wrong guess does not break anything: the row in the table is
fixed by hand. That is the safety net, and it must not be removed — the window has to stay editable.

A second consequence: the button is useful even without a sample — chains can be assembled by hand
in the window and saved as a template.

---

## 2. The catalogue of chain kinds (`DimensionChainKind`)

| Value | Text in the window | What it picks up |
| --- | --- | --- |
| `Overall` | Overall | only the two end corners of the side |
| `OpeningEdges` | Openings | the side corners + the jamb faces of the openings (doors, windows) |
| `OpeningCenters` | Opening centres | the side corners + opening centre lines |
| `Partitions` | Partitions | the side corners + both faces of the partitions meeting the side from inside the room |
| `WallFaces` | Wall faces | the corners + the wall joints of the side (for stepped walls) |
| `Combined` | All combined | `OpeningEdges` + `OpeningCenters` + `Partitions` merged into one chain |

The order of the enum values is the order in the window's drop-down.

A chain (`DimensionChain`) = `Kind` + `OffsetMm` (the dimension line's offset from the wall face) +
`DimensionTypeName` (the dimension type's name, **not** an `ElementId` — a template has to travel
between projects) + `IsEnabled`.

---

## 3. Verified APIs (reflection over Revit 2022's `RevitAPI.dll` — everything exists, no `#if` needed)

- `SpatialElement.GetBoundarySegments(SpatialElementBoundaryOptions)` → `IList<IList<BoundarySegment>>`
- `SpatialElementBoundaryLocation`: `Finish`, `Center`, `CoreBoundary`, `CoreCenter`
- `BoundarySegment.GetCurve()`, `BoundarySegment.ElementId`
- `Room.IsPointInRoom(XYZ)`
- `HostObject.FindInserts(bool, bool, bool, bool)` (inherited on `Wall`)
- `HostObjectUtils.GetSideFaces(HostObject, ShellLayerType)` → `IList<Reference>`
- `FamilyInstance.GetReferences(FamilyInstanceReferenceType)`, `FamilyInstance.GetReferenceType(Reference)`
- `FamilyInstanceReferenceType`: `Left`, `Right`, `CenterLeftRight`, …
- `Autodesk.Revit.Creation.Document.NewDimension(View, Line, ReferenceArray, DimensionType)`
- `Dimension`: `Curve`, `References`, `AreReferencesAvailable`, `DimensionType`, `View`, `NumberOfSegments`
- `Options`: `ComputeReferences`, `IncludeNonVisibleObjects`, `View`, `DetailLevel`
- `Reference`: `ElementId`, `ElementReferenceType`, `ConvertToStableRepresentation`, `EqualTo`
- `Autodesk.Revit.DB.ExtensibleStorage.Schema` (the "this dimension was created by the button" mark)

---

## 4. New files

```
Commands/AutoDimensionCommand.cs          the command: selection, calling the window, the transaction, the report
UI/DimensionChainKind.cs                  the catalogue of chain kinds (the table above)
UI/DimensionChainRow.cs                   a row of the chain table (INotifyPropertyChanged)
UI/DimensionTypeInfo.cs                   a snapshot of a dimension type for the window: Id (long) + name
UI/AutoDimensionWindow.cs                 a WPF window built in code; knows nothing about the Revit API
Infrastructure/RoomSide.cs                a room side: direction, inward normal, walls, segments
Infrastructure/RoomSideBuilder.cs         BoundarySegments → a list of sides
Infrastructure/DimensionReferenceCollector.cs   a side + a chain kind → a ReferenceArray
Infrastructure/DimensionSampleReader.cs   sample dimensions → a template (a heuristic)
Infrastructure/DimensionTemplate.cs       the template model + reading/writing %AppData%\VladTools\autodim\
Infrastructure/AutoDimensionMarker.cs     ExtensibleStorage: the mark on created dimensions
Resources/autodim_16.png, autodim_32.png  the icon
```

To edit: `App.cs` (registering the button), `CLAUDE.md`, `README.md`.

---

## Stage 0. Investigation inside a live Revit (mandatory, before any code)

Everything else rests on four assumptions. Verify them **on a real masonry plan**, not on guesses.
Write the investigation code as a temporary command (or a draft right inside
`AutoDimensionCommand`), print the result with `TaskDialog`.

1. **Jamb faces come from the wall geometry in a single pass.**
   `wall.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine })`
   → `Solid` → the `PlanarFace`s whose normal is parallel to the wall's direction. Expected: these
   are the wall's own ends **and** the jambs of every opening at once. Confirm there are as many
   such faces as expected, and that `NewDimension` accepts them.
2. **`NewDimension` accepts these references on a plan.** Gather a `ReferenceArray` from two such
   faces, place a dimension on the active plan. If it does not go through, try the same references
   obtained with `Options.View` set to the active plan.
3. **`FamilyInstance.GetReferences(Left/Right/CenterLeftRight)` is not empty** for the project's
   doors and windows. **Expected trap:** many families do not publish these planes, and the list is
   empty. Then `OpeningEdges` is built only from the faces in point 1, and `OpeningCenters` is
   impossible at all (a reference to the midpoint between two faces does not exist) — such a
   chain's row in the window is disabled with an explanation, rather than silently producing a
   distorted dimension.
4. **The boundary loop's orientation.** Confirm that `XYZ.BasisZ.CrossProduct(direction)` gives the
   normal pointing into the room. **Do not rely on this**: check the direction through
   `room.IsPointInRoom(side midpoint + normal * 10 mm)` and flip it on a negative answer.

**Stage acceptance:** the `TaskDialog` reports how many faces were found on the wall, whether
openings have `Left`/`Right`, and one dimension was actually placed on the plan. The draft code is
deleted afterwards.

If point 1 or point 2 does not hold up — **stop and report**, the rest of this plan needs
reworking, not working around.

---

## Stage 1. The core: one "Overall" chain on one room

- `AutoDimensionCommand` follows the pattern from CLAUDE.md ("How a command is built"): attributes,
  the `!doc.IsFamilyDocument` check, checking the active view is a `ViewPlan` (otherwise a
  `TaskDialog`: "The command works on floor plans; switch to a floor plan and try again" +
  `Result.Cancelled`).
- Rooms: `uidoc.Selection.GetElementIds()` → filtered to `Room`. Empty → a `TaskDialog` offering to
  take every room on the active view (`FilteredElementCollector(doc, view.Id).OfCategory(OST_Rooms)`).
- `RoomSideBuilder`: `GetBoundarySegments` loops → sides. Neighbouring segments merge into one side
  under two conditions: pointing the same way (`direction.DotProduct(next) > 1 - 1e-6`) and
  collinearity (the distance from the next segment's start to the previous one's line < 1 mm).
  Account for the loop wrapping around — the last segment may merge with the first.
- References for `Overall`: the side's end faces — the leftmost and rightmost along the side's
  axis, from the geometry pass (stage 0, point 1).
- The dimension line: `Line.CreateBound(o + n*offset, o + n*offset + d*length)`, `offset` as a
  constant for now (300 mm, say), `length` — the side's length plus a margin at both ends. Z from
  the boundary curve.
- One transaction for the whole batch, a `WarningSuppressor`, each room in its own `try`/`catch`,
  a `TaskDialog` report (successes + no more than 15 failures).

**Acceptance:** four overall dimensions appear on a rectangular room, Ctrl+Z removes all of them at once.

---

## Stage 2. Collecting references: `DimensionReferenceCollector`

The full implementation of the chain catalogue. Input: `RoomSide`, `DimensionChainKind`, the
document. Output: a `ReferenceArray`.

- The shared trick: points of interest are projected onto the side's axis (`t = (p - o) · d`),
  sorted by `t`, duplicates closer than 1 mm are dropped. The `ReferenceArray` is built in this order.
- `OpeningEdges`: faces from the side's wall geometry whose normal is parallel to `d` (stage 0, point 1).
- `OpeningCenters`: `fi.GetReferences(CenterLeftRight)` over inserts from
  `wall.FindInserts(true, false, true, true)`, filtered to the "Doors" and "Windows" categories.
  Empty → the chain is unavailable (see stage 0, point 3).
- `Partitions`: partitions are boundary segments of the room whose direction is perpendicular to
  `d` and which meet the current side. Their faces come from `HostObjectUtils.GetSideFaces` and are
  filtered by proximity to the meeting point.
- `WallFaces`: joints between neighbouring walls inside the side (when the side is made of several walls).
- `Combined`: the union of the three sets with the same `t`-based deduplication.
- Fewer than two references → the chain is skipped on that side, and the reason goes into the
  report ("Room 105, east side: the 'Opening centres' chain has nothing to pick up").

**Cache:** wall geometry is read once per run and stacked into a dictionary keyed by `ElementId`.
Without it, the command would grind to a halt on a model with hundreds of rooms.

**Acceptance:** on a room with two doors, a window and an adjoining partition, every chain is
placed separately and picks up exactly what it is meant to.

---

## Stage 3. The `AutoDimensionWindow` window

WPF built in code, following the pattern of `CleanupWindow` (simpler structure) and
`LinkManagerWindow` (a table of rows).

- The chain table: `check box | № | Chain kind (ComboBox) | Offset, mm | Dimension type (ComboBox) | State`.
  "Add chain" / "Remove chain" buttons. The offset must be a positive number, otherwise the row is
  highlighted and "State" gives the reason.
- "Room boundary": `Finish` / `CoreBoundary` / `Center`. **`CoreBoundary` by default** — a masonry
  plan is dimensioned to the core layer faces.
- "Place dimensions": into the room (by default) / outward.
- The header: how many rooms are selected.
- Buttons: "Take a sample…", "Load template", "Save template", "Place", "Close".
- The window knows nothing about the Revit API: dimension types arrive as an array of
  `DimensionTypeInfo` (`long Id` + name), the result is handed back through a `Selected` property
  (a list of `DimensionChainRow` + the chosen settings).

**The trap that makes this stage a separate one:** `Selection.PickObject` **cannot** be called
while a modal window is open. So "Take a sample…" does not take the sample itself — it closes the
window with `WantsSample = true`; the command does `PickObjects`, parses the sample and **reopens
the window**, already filled in. The same trick would apply to a future "Select rooms…", if needed.
The window gets long-running work as a lambda, exactly like `DeleteProjectParametersWindow` —
a `Func<…>` from the command.

**Acceptance:** chains can be set up by hand, "Place" runs across every selected room, the report
shows the number of dimensions placed and what was skipped.

---

## Stage 4. Parsing a sample: `DimensionSampleReader`

Input: a list of selected `Dimension`s + the document. Output: a `DimensionTemplate`.

For every sample dimension:

1. `AreReferencesAvailable == false` → skip with a reason (the dimension's references are lost).
2. `dim.Curve as Line` → the direction `d` and a point. Not a `Line` (radial, angular) → skip.
3. Find the room and the side: take the room the walls in the dimension's references belong to,
   and pick the side within it that is parallel to `d` and closest to the dimension line.
4. `OffsetMm` = the signed distance from the straight side to the dimension line along the inward
   normal. Negative → the chain is outside; that is allowed and remembered as a flag.
5. The chain kind — from the makeup of `dim.References`:
   - exactly two references, both to the side's own walls → `Overall`;
   - references exist where `fi.GetReferenceType(ref) == CenterLeftRight` → `OpeningCenters`;
   - more than two references to the side's wall faces → `OpeningEdges`;
   - references exist to walls perpendicular to `d` → `Partitions`;
   - a mixed makeup → `Combined`.
6. `dim.DimensionType.Name` → the chain's type name.

Chains are sorted by `OffsetMm` — that gives their numbers, 1..N. Ones matching in kind and offset
(one chain picked up from several walls) are merged into one.

The result goes into the window; each row's "State" says which sample dimension it came from
("from the 3925 mm dimension"). If it could not be worked out, the row still appears, with kind
`Combined` and a "the chain kind was guessed approximately, please check" note.

**Acceptance:** the whole demo scenario — place six chains by hand along one wall, press the
button, select those six dimensions, see six rows in the window with the right offsets, select
every room on the storey, "Place", get the same set of chains along every wall.

---

## Stage 5. Storing templates

Folder `%AppData%\VladTools\autodim\`. **Not `dimensions\`** — that one is taken by
`DimensionLabelCache` (the family scan for dimension labels), the two must not be mixed.

`autodim\<template name>.txt`, UTF-8 with a BOM, fields separated by a vertical bar, `#` is a comment:

```
# VladTools auto-dimension template
BOUNDARY | CoreBoundary
SIDE | Inward
CHAIN | 1 | 120  | OpeningEdges   | Linear - 2.5mm Arial
CHAIN | 2 | 300  | OpeningCenters | Linear - 2.5mm Arial
CHAIN | 3 | 480  | Overall        | Linear - 3mm Arial
```

`autodim\_settings.txt` — the window's settings (`KEY = value`): the last template, the boundary,
the direction. Rewritten on **any** closing of the window, like `formulas.txt` and
`links\_settings.txt`. The templates themselves — only through the "Save template" button. Names
starting with an underscore are excluded from the template list (the rule already exists in
`LinkSetLibrary`).

A corrupt or empty file — silently an empty list, no exceptions (like `NameBuffer`).

**Acceptance:** a template saves and offers itself in another project; a dimension type with a
different name gives "Dimension type … was not found, the current default will be used" in "State".

---

## Stage 6. Running it again

Without a mark, running it again doubles the dimensions — the first thing a user would notice.

- `AutoDimensionMarker`: an `ExtensibleStorage` schema (its own GUID as a constant, `VendorId` —
  `VLADTOOLS`, `Public`/`Public` access), fields `RoomUniqueId` (string) and `ChainIndex` (int).
  Set on every created `Dimension` inside the same transaction.
- Before placing: gather every `Dimension` with this schema on the active view whose `RoomUniqueId`
  falls among the rooms being processed, and delete them (`doc.Delete`) — in the same transaction,
  before creating anything.
- A "Remove what this button placed before" box in the window (on by default). Unchecked — the old
  ones stay, and the report says so explicitly.
- Dimensions the user placed by hand carry no mark and are never touched.

**Acceptance:** two runs in a row give the same result as one; hand-placed dimensions stay in place.

---

## Stage 7. Polish and documentation

- The `autodim_16.png` / `autodim_32.png` icon in `Resources/` as an `EmbeddedResource`. Without
  the files the build still succeeds, the button is just left with no picture (`Icons.Load` returns
  `null`) — but it should not be left that way.
- Registration in `App.cs` on the "Project" panel, as the eighth button, with a `tooltip` and a
  `longDescription` in the same style as its neighbours.
- **`CLAUDE.md`** — edited in the same session, before reporting the work as done (the file's own
  rule): "What this is" (the button count on the "Project" panel), "Buttons and commands" (a table
  row), "Code map" (every new file), "Key decisions" (a chain catalogue instead of copying
  references; parsing a sample is a heuristic; `PickObject` while a modal window is open; the
  wall-geometry cache; the `ExtensibleStorage` mark), "Storing user settings" (the `autodim\`
  folder and how it differs from `dimensions\`).
- **`README.md`** — the user-facing description of the button and the "place a sample → select
  rooms → place" scenario.
- `.\build.ps1` builds for all three years with no errors and no new warnings.

---

## Version 1 limitations (write these into README, do not try to close them along the way)

- Floor plans only, only rooms of the open project (not of links).
- Only the partitions visible in the room boundary loop are picked up; a partition meeting a wall
  from outside the room will not make it into a chain.
- Curved walls are skipped with a message: a side is only built from straight segments.
- Sloped (non-orthogonal) walls are supported — nothing anywhere is tied to the X/Y axes, everything
  is computed from the side's own direction.
- Dimensions are not checked for text overlap: Revit will draw closely spaced ticks as they are.

---

## Order of work for the executor

1. Stage 0 — and **report the investigation results before writing the rest of the code**.
2. Stages 1→7 in order, after each one — `.\build.ps1 -RevitVersion 2022` and a check in Revit.
3. Revit holds `VladTools.dll` locked: close Revit before building, otherwise the old DLL gets
   installed silently.
4. Commit — once a stage is finished, one change per stage.
