# VladTools — an add-in for Revit 2022, 2024 and 2025

An add-in with a **Vlad Tools** ribbon tab: the "Families" panel — for work in the family editor
(`.rfa`), the "Project" panel — for work in a project (`.rvt`). Buttons are added following one
pattern — see "How to add a new button".

## Buttons

### 3D Thumbnail (Families panel)

Works **only in the family editor**. Creates a 3D view named `3D Thumbnail` and sets it up:

| Setting        | Value                  | API                                                                                                              |
| ------------------------- | --------------------------------- | ---------------------------------------------------------------------------------------------------------------- |
| Annotations        | off                | `View.AreAnnotationCategoriesHidden = true`                                                                    |
| Connectors    | hidden                      | `SetCategoryHidden(OST_ConnectorElem + X/Y/Z axes)` and `HideElements` on the `ConnectorElement`s themselves |
| Graphics style | Realistic          | `DisplayStyle.Realistic`                                                                                       |
| Detail level    | Fine (maximum) | `ViewDetailLevel.Fine`                                                                                         |

Once created, the view opens — that way Revit picks it as the family preview when saving.

Pressing it again does not create a duplicate: if a `3D Thumbnail` view already exists, the settings
are simply reapplied to it. If a setting could not be applied (a view template controls the view,
say), the rest are still applied, and the list of problems is shown in a dialog.

### Delete Parameters (Families panel)

Works **only in the family editor**. Deletes from the open (host) family the shared parameters
that are **checked**.

How it works:

1. The button opens a window with a table of **every shared parameter** in the family: check box,
   name, "Instance/Type", "Dimensions", group, GUID.
2. The **"Hide parameters used on dimensions"** box is checked from the start and removes from the
   list the parameters that label dimensions. Such a parameter holds the family geometry together:
   deleting it drops the label from the dimension and breaks the parametrics. Clear the box to see
   them — the "Dimensions" column reads "Dimension label" for them.
3. The check boxes are set two ways, which complement each other:

   | Method | How |
   |---|---|
   | By hand | click the check box left of the parameter; a double click on the row or the space bar does the same, the header check box does all at once |
   | By a rule | the **"Match parameters starting with"** / **"Match parameters containing"** drop-down and a rule string, `SP` say |

4. The rule works like a search: matching rows stay in the table and get checked right away, the
   rest leave it. The **"Invert the search"** box flips the rule: "containing" + `ADSK` + inversion
   is every parameter **except** the ADSK ones. The bottom of the window reads "Shown: N of M" and
   "Will be deleted: N of M shown", and the button carries a counter.
5. After the rule, check marks are edited by hand: clear what is not wanted, add what is missing.
   The rule no longer touches those rows.
6. The "Delete (N)" button asks for confirmation with a list of names and deletes the parameters in
   a single transaction (`FamilyManager.RemoveParameter`) — it can be undone in Revit with Ctrl+Z.

Details:

- **Only what is shown in the table gets deleted.** A row that leaves the list — by the rule or as
  a dimension label — loses its check mark. To check something outside the rule, clear the rule
  string first.
- An empty rule string shows the whole list and checks nothing — including with inversion on,
  otherwise one check box would clear the whole table at once. "Delete" is disabled in that case —
  so that "typed nothing in" never means "delete everything".
- Case is ignored by default; the "Match case" box turns on an exact comparison.
- Columns sort on a header click — the check marks survive sorting.
- Only the open family document itself is affected. Nested families are left alone.
- Revit will not let a parameter referenced by another parameter's formula be deleted. The command
  makes several passes (the one doing the referencing goes first, then the one referenced), and
  only parameters that truly cannot be deleted end up in the final error list.

### Add Formulas (Families panel)

Works **only in the family editor**. Assigns formulas to the parameters of the open (host) family
from a list that lives with the user and travels from family to family.

How it works:

1. The button opens a window with a "parameter — formula" table. The first time, it already holds
   two formulas:
   | Parameter                     | Formula           |
   | ------------------------------------ | ------------------------ |
   | `ADSK_Diameter` | `if (1=1,PI_DN,0)`     |
   | `ADSK_Mass`                  | `SP_Mass/1 kg` |
2. A formula of your own is typed the same way into the blank row at the bottom of the table:
   the parameter name on the left, the formula on the right — exactly as it is written in the
   Revit parameter dialog.
3. Every row is validated against the open family right away, and the result shows in the "Status" column:
   | Status                               | What it means                                                                                                                   |
   | ------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------- |
   | Ready to apply       | the parameter exists, and every name in the formula does too                                                    |
   | The formula will be replaced | the parameter already has a formula, the new one will overwrite it                                        |
   | Parameter PI_DN not found   | a name from the formula (or the parameter in the left column itself) does not exist in the family |
4. The "Apply (N)" button assigns **every checked formula in a single transaction** — that is the
   batch application. It can be undone in Revit with Ctrl+Z. Afterwards a report shows what was
   assigned, what failed and why.

Details:

- The list is saved to `%AppData%\VladTools\formulas.txt` every time the window closes and offers
  itself in the next family — the same formulas never need to be typed in twice. The file can be
  edited by hand.
- If a formula's parameter is missing, "Apply" shows a "Parameter PI_DN not found" dialog. When
  part of the formulas are fine, the dialog offers to apply the rest; rows with an error are never applied.
- An unchecked box leaves the formula in the list without applying it. In the file such a line is
  commented out with a hash.
- A row is removed from the list with the "Delete row" button (select it in the table first).
- Names are compared case-sensitively — the same way Revit itself reads them in formulas.
- Validation looks at names, not meaning: an error in the formula itself is caught by Revit when
  writing, and such a row ends up in the failure report.
- Only the open family document itself is affected. Nested families are left alone.

### Rename Nested (Families panel)

Works **only in the family editor**. Batch-renames the **nested** families of the open family
(and, if asked, their types). The name of the open family itself is never changed — it is set by
the file name.

How it works, Excel-style:

1. The button opens a window with a list of nested items: "Kind", "Inside family", "Current name",
   "New name", "Inst." (how many instances are placed) and "Status".
2. The **"Find"** field takes a piece of the name, the **"Replace with"** field takes what to
   change it to. For example, `DN` → `DIA`: the "New name" column shows right away what each row
   will become.
3. Text can also be added **at the start** and **at the end** of the name — a `(FT)-` prefix, say.
4. Any "New name" cell can be edited by hand: the rule no longer overwrites that row.
5. The "Rename (N)" button shows an "old → new" list and renames everything in a single
   transaction — it can be undone in Revit with Ctrl+Z. A report follows.

The name buffer (on the right):

- Name fragments that repeat often (`(FT)-FL_GOST 33259-2015`, `DN`, `_TYPE 1`) are saved with the
  **"Save"** button and live in `%AppData%\VladTools\names.txt` — they open with the next family
  and survive restarting Revit.
- They are pasted with the **"→ Find"**, **"→ Replace"**, **"→ Prepend"**, **"→ Append"** buttons,
  and a double click pastes the value into whichever field the cursor was in.
- The file can be edited by hand: one line — one value, a line starting with a hash is a comment.

Details:

| Status | What it means |
| --- | --- |
| Will be renamed | the name is valid and differs from the current one |
| Unchanged | the new name matches the current one — the row is skipped |
| Name already taken | another nested family (or another row in the list) already carries that name |
| Empty name, Revit will not accept the characters: … | the name is empty or contains `\`, `:`, `{`, `}`, `[`, `]`, `\|`, `;`, `<`, `>`, `?`, `` ` ``, `~` |

- Only what is shown in the table is renamed. The **"Show"** field switches the list: nested
  families, their types, or both together; switching it resets the new names to the current ones.
- The left check box includes a row in the batch; the header check box does all at once, the space
  bar toggles the selected rows.
- Case is ignored by default: `dn` will find `DN` too. The "Match case" box turns on an exact comparison.
- Types are checked for a taken name within their own family, families among themselves.
- Swapping names (`A` → `B`, `B` → `A`) is done in several passes: while a name is held by a
  neighbour that is also being renamed, Revit will not release it. What could not be done goes
  into the report.
- The "Reset" button clears the rule and returns every new name to the current one.

### Delete Shared Parameters (Project panel)

Works **only in a project** (`.rvt`). Deletes from the open project the shared parameters that are
**checked** — together with their values on every element.

What for: after the DD stage a model arrives with hundreds of somebody else's shared parameters,
and removing them one at a time through "Manage → Project Parameters" is hours of work. Here they
are swept away in a batch by a common name prefix.

How it works:

1. The button opens a window with a table of **every shared parameter in the project**: check box,
   name, "Instance/Type", "Dimensions", categories, group, GUID.
2. The **"Show"** field splits the list into three kinds:

   | Value | What lands in the table |
   |---|---|
   | Every shared parameter | everything in the file |
   | Project parameters only | bound to categories — the ones visible in "Manage → Project Parameters" |
   | Unbound only | shared parameters that arrived with loaded families or are left over from bindings that were removed |

3. The **"Scan Families (N)"** button looks for parameters that label dimensions inside loaded
   families — deleting them breaks geometry. This is invisible from the project, so the button
   opens every family in turn: on a large model that is a few minutes, and Revit stops responding
   for that time, which it warns about beforehand. After scanning, the **"Hide parameters used on
   dimensions"** box turns on, the parameters found leave the list and lose their check marks, and
   the "Dimensions" column reads "Dimension label" for them.

   **The result is saved** to `%AppData%\VladTools\dimensions\` — one file per project. Next time
   the window opens already scanned, and the button shows how many families it does not cover:
   only new and changed families are opened again, the rest come from the file. The bottom of the
   window says when the scan was made.
4. Check marks are set the same way as in the family "Delete Parameters" window: by hand (a click,
   a double click on the row, the space bar, the header check box for all at once), or by a rule
   **"Match parameters starting with"** / **"…containing"**, `ADSK_` or `SP` say. The rule works
   like a search: matching rows stay in the table and get checked right away, the rest leave it.
   The **"Invert the search"** box flips the rule: `ADSK` + inversion is every parameter **except**
   the ADSK ones.
5. The "Delete (N)" button asks for confirmation with a list of names and deletes the parameters
   in a single transaction — it can be undone in Revit with Ctrl+Z. A report follows.

Details:

- While the families are not scanned, the bottom of the window says so, and the delete
  confirmation separately warns that a parameter holding geometry together may be among the checked ones.
- **Reloaded a family without saving the project? Press "Scan again".** When there is nothing left
  to open, the button relabels itself "Scan again (M)" and goes through every family bypassing the
  saved result. That is not pedantry: Revit only marks a family changed on save or
  synchronisation, so the saved scan will not notice a reload within the same session.
- The loaded families themselves are scanned. Families nested inside them are not opened, in-place
  and non-editable ones are skipped; a family that could not be opened goes into the scan report,
  is not cached, and stays unscanned.
- A project that has never been saved is not cached — the scan will have to run again in it. The
  scan file can simply be deleted to start with a clean slate.
- **Only what is shown in the table gets deleted.** A row that leaves the list — by the rule, by
  the "Show" filter, or as a dimension label — loses its check mark. To check something outside
  the rule, clear the rule string first.
- Along with a parameter its values on every element disappear too, together with the schedule
  fields and view filters that referred to it. Revit does not show these warnings one at a time —
  they are gathered and printed in the final report.
- An empty rule string shows the whole list and checks nothing — including with inversion on,
  otherwise one check box would clear the whole table at once. "Delete" is disabled in that case.
- Case is ignored by default; the "Match case" box turns on an exact comparison.
- Columns sort on a header click — the check marks survive sorting.
- A parameter Revit will not let be deleted (used by a loaded family, say) goes into the failure
  list of the report; the rest of the batch is still deleted as usual.
- Non-shared project parameters (created inside the file itself, without a shared parameter file
  or a GUID) are neither shown nor touched.
- Only the project itself is affected: loaded families and links are unchanged.

### Cleanup (Project panel)

Works **only in a project** (`.rvt`). Removes from the model whatever is checked: someone else's
presentation is stripped off in one go rather than one browser node at a time.

What for: a model arrived from outside and is needed only as geometry — it has no use for someone
else's sheets, two hundred foreign views, filters, or families placed nowhere.

The window offers eight items; each has, in brackets, how many were found **in this model**, and
under the heading, exactly what will disappear. An item with nothing to remove is disabled. The
**"Select all"** box checks every available item at once.

| Item | What it removes |
|---|---|
| Purge unused families | loaded families and types that nothing in the model refers to; the number in brackets is the type count |
| Delete every sheet in the model | sheets together with their viewports, titleblocks and stamps; the views themselves stay in the browser, removed from the sheets |
| Delete every filter in the model | everything from "View → Filters": both rule-based view filters and selection filters |
| Delete every view in the model | plans, ceiling plans, sections, elevations, callouts, 3D views, walkthroughs, drafting views |
| Delete every legend | legend views together with the components on them |
| Delete every schedule | schedules, material and note takeoffs, panel schedules |
| Ungroup model groups | **ungroups**: the elements stay where they are, only the groups themselves disappear |
| Purge unused groups in the model | group types (model, detail, attached detail) not placed in the model |

The "Clean up (N)" button shows the checked list and carries out everything in a single
transaction — it can be undone with one Ctrl+Z. A report follows: how much of what was removed,
what failed, and which Revit warnings were suppressed along the way.

Details:

- **The active view is never deleted** — Revit will not delete the view you are standing on. To
  remove it too, switch to another view and run "Cleanup" again.
- **View templates are left alone.** So are internal views: browsers, system views, analysis
  reports, the revision schedule inside a titleblock, the internal keynote schedule — the last two
  live not in the browser but inside the titleblock, and deleting them breaks it.
- **"Ungroup model groups" removes nothing from the model.** The groups are ungrouped, the
  elements stay in place. Pinned groups are unpinned first. Empty group types are left hanging in
  the browser after ungrouping — the "Purge unused groups" item removes those: check both at once.
- **The execution order differs from the order of the items in the window.** Presentation goes
  first, then groups, and only at the end — unused families: by that point titleblocks, tags and
  detail components have become unneeded as well and go in the same batch. So usually more
  families get deleted than the number shown in brackets when the window opened.
- **A family with references left in the model is not deleted**, even if the count says it is
  free: before deleting, the command asks Revit whether placed elements would go with the type.
  How many were kept this way is written in the report.
  - Counting unused families walks every element in the model, so the window does not open
    instantly on a large file.
- What Revit refuses to delete goes into the failure list of the report; the rest of the batch is
  still removed as usual.
- Only the project itself is affected: loaded families are not changed on the inside, links are left alone.

### Link Manager (Project panel)

Works **only in a project** (`.rvt`). Links a whole batch of models to it at once — from disk,
from Revit Server and from BIM360/Autodesk Docs — with one placement and one workset setup for
all of them.

What for: in Revit itself every link is inserted through its own dialog, and each time "By shared
coordinates" is chosen again and the boxes are cleared again for "00_Shared levels and grids". On
twenty links that is twenty identical dialogs in a row.

**Where to take models from.** Five buttons at the top, everything lands in one table:

| Button | What it opens |
|---|---|
| Files… | an ordinary file picker: disk, network folder; several can be checked at once |
| Revit Server… | a folder tree on the server with a check box on every model; the server name is typed in once and remembered |
| BIM360… | Autodesk Docs accounts, projects and folders — through the eyes of the account you are signed in with |
| BIM360 by GUID… | a model list as pairs of GUIDs — for when browsing is unavailable |
| Building Kit… | a ready-made set of links: the models of every discipline of your building, found automatically (see below) |

For BIM360 there is **no separate sign-in needed**: the add-in uses whichever account you are
already signed in with inside Revit. If you are not signed in to your Autodesk account, the button
says so and offers GUID entry instead. Only workshared models are visible in the cloud: a plain
`.rvt` simply dropped into a folder cannot be linked — that is a Revit limitation, not the add-in's.

**Link sets.** The gathered list can be saved in the Windows profile under its own name
("Consultants", "Stage D") and offered in full in the next project with one button. For BIM360 this
is the main way to work: a saved set loads even when the cloud cannot be reached.

**Placement** is chosen once for every link: "By shared coordinates", "Origin to origin", "Centre
to centre" or "By project site location". Next to it — the link type (overlay or attachment) and
the relative path (files only: for Revit Server and the cloud the path is always absolute).

**The project workset** a link itself goes into is chosen **per link** — the "Project workset"
column in the table: architecture into "01_Link_AR", structure into "01_Link_KR". If it is the same
for everyone, there is a "Put the links into the project workset" field and a "Set for checked"
button. The choice is saved together with the link set, so it never has to be set up again in the
next project. A non-workshared project has no such column — it has no worksets either.

**Guessing the workset from the model name.** The "Guess from the model name" box next to the
workset field: the add-in takes the discipline code from the file name (`Building1_OV_R22.rvt` →
`OV`) and sets the new link's project workset to whichever one carries that code as a separate
word — `01_Link_OV`, and the like. A version suffix (`_R22`, `_R24`) falls away by itself.

If several worksets match the code, the add-in works it out by two clues in turn. First it looks at
the model name: if worksets are also split by building (`01_Link_AR_B01`, `01_Link_AR_B03`), a
model of building B03 gets `01_Link_AR_B03`. Then — at how the other disciplines' worksets are
named: next to `01_Link_ES` and `01_Link_OV`, `01_Link_AR` wins over `05_AR_Elevations`. If that
does not settle it either, it does not guess: the candidates themselves are listed in "State", so
it is clear what to choose from.

If no workset matches at all, and the project does have per-discipline worksets, the add-in says
so: "Discipline \"PS\": no workset with this code in the project" — meaning one has to be created
or is named differently. And, the other way round: "No discipline visible in the model name" means
the discipline code was not recognised in the file name itself — add it to
`links\_settings.txt` (the `DISCIPLINE` lines). The guess only fills in what is empty: a workset
chosen by hand or arriving from a saved link set is left alone; links already in the project are too.
The discipline code list lives in `%AppData%\VladTools\links\_settings.txt` (the `DISCIPLINE`
lines) — a custom code, in any language, can be added there.

**Building Kit — the list assembles itself.** A button for the job where every model of every
building has the same disciplines linked in: architecture, structure, ES, PS, PT, mechanical,
plumbing. There is no need to pick them by hand — the project is already named so the add-in can
find them on its own:

- **the building number** comes from the open model's name: `MK3-VSC-B01-AR` → `B01` (the third
  piece of the name; the "Building" field holds a list of pieces — if your number is not the
  third one, choose the right one, and next time the add-in will take the same position);
- **the project folder** is the one the open model itself lives in. If it lives in its own
  discipline folder (`3.0_AR`), the add-in climbs a level up on its own; if it lives next to the
  discipline folders (`01_Base Model`), it works that out too. The folder is shown in "Search in",
  and it can be given by hand: on Revit Server, in BIM360, or on disk;
- **discipline folders** are recognised by the code in the name as a separate word: `3.0_AR`,
  `4.2_KR`, `5.1_ES`, `9.0_PS`. Inside them, models carrying your building number are taken. The
  "and inside nested discipline folders" box is for when the models sit not right in the
  discipline folder but in a subfolder.

The search runs on its own when the window opens, and all that is left is to look at the table and
press "Add". What can be checked without guessing is checked: a discipline with one model. If a
discipline has several, the add-in does not choose for you — both rows are left unchecked with a
note in "State". A model already in the link list, and the open model itself, are shown but not
checked. The bottom of the window says how many of the requested disciplines were found and which
were missing.

From there, what was found lives like everything else in the table: duplicates are filtered out,
and the project workset is guessed from the discipline (`01_Link_AR`, and the like) — the same one
the model was found by. The discipline list is edited right in the window and remembered
(`links\_settings.txt`, the `KIT` lines), as are the building number, the search folder and the
nesting check box.

**The worksets inside a link**, on the other hand, are shared across every link. At the bottom of
the window — a name list with check boxes: the checked worksets will close in every link. The names
come from last time, so "00_Shared levels and grids" is usually already checked when the window
opens; the "Read Worksets" button reads the names from the models themselves without opening them.

The "Found in" column is exactly the name check, and it is worth looking at whenever a workset
"somehow did not close". "in N links" — the workset was found and will close. "in none of them" —
the name is checked, but the consultants named their workset differently: there is nothing to
close, and Revit has nothing to do with it. "from last time" — the name simply has not been
checked yet: check the links and press "Read Worksets". Names not found in any link are now also
mentioned in the final report. Next to the list — a rule: "starting with" + `00_` will close the
workset even where it is named "00_General Levels and Grids". The rule applies **on load, to each
link separately**, not only through the "Check by rule" button. The "On load" field switches the
base: open every workset (a check mark then means "close"), close every workset (a check mark
means "open"), or as the model was last opened.

The "Load (N)" button shows exactly what will happen and carries everything out as a batch.
Afterwards — a report: what was linked, what failed, how the worksets turned out and which Revit
warnings were suppressed along the way.

After loading, the add-in **checks on its own what became of the worksets in every link**, and the
report reads one of four things: silence — everything closed as asked; "a reload fixed it" — Revit
did not comply the first time; a line in "Could not be done" with the workset names — it did not
comply the second time either; "Worksets were requested but could not be checked" — the link would
not hand itself over for a re-read, and the worksets have to be looked at by eye. The last case
used to look like a success, so unclosed worksets could go unnoticed.

Details:

- **New links are pinned right away** — so they cannot be dragged accidentally. Removing the pin in
  Revit is one button; links already in place are left alone.
- **Links already in the project show up in the same table** with the "Already in the project"
  status, and the "Project workset" column shows where they currently sit. They do not have to be
  touched, but if checked, they reload with the new workset setup, and if the project workset is
  changed they move into it together with all of their instances. The placement of an existing
  link does not change: Revit will not allow that — the link has to be deleted and set up again.
- **Reloading existing links wipes the undo history.** That is how Revit works: reloading a link
  clears the whole "Undo" list. So it is done first, and new links are set up after it — Ctrl+Z
  will bring those back. But whatever was done in the project before pressing the button can no
  longer be undone after the reload. The window warns about this beforehand; if only new links are
  checked, the history is left untouched.
- **Sometimes Revit does not apply the workset setup the first time — the add-in fixes it on its
  own.** Revit may report success and still leave the worksets as they were. The add-in re-reads
  the already-loaded link and, if Revit did not comply, tries once more; it writes about this in
  the report: "a reload fixed it" (that also wipes the undo history) — or, if it did not help the
  second time either, an ordinary line in "Could not be done" with the names of the worksets that
  are still wrong.
- **The worksets inside nested links cannot be closed.** That is a Revit limitation, not the
  add-in's: if a consultant's model has its own underlay linked inside it, its levels and grids
  stay visible no matter what is closed here. Only an edit on the consultant's side helps.
- **Worksets can only be set up at the moment a link loads.** So changing the setup on a link
  already in the project is done by reloading — with all the consequences for the undo history
  described above. There is no equivalent in the API to Revit's own "Manage Links → Manage
  Worksets" button, which changes worksets in place.
- **The kit is searched around your folder, not across the whole project** — the search folder and
  the discipline folders inside it. That is how it takes a second rather than a minute: every
  cloud folder is a network request. If the add-in runs into too large a tree, it says so —
  meaning the search was started from the wrong folder, and it should be pointed with the "On
  server…" / "In BIM360…" / "On disk…" buttons.
- **A folder on disk is chosen by pointing at any model in it** — these .NET versions have no
  ordinary folder-picker dialog, and dragging in an extra library just for that is not worth it.
- **Only what is shown in the table is acted on**: a row that leaves it through the "Show" filter
  loses its check mark.
- Duplicates are dropped on their own: a model already in the table will not be added a second
  time, and in the browser tree it shows greyed out with no check box.
- Reading server and cloud folders goes over the network, so the first time a large folder is
  expanded takes a second or two. If the service did not answer, the window does not close — the
  reason appears as a red line right inside that folder.
- **A project workset from a saved list is checked against the current project.** Every project
  has its own worksets; if such a workset does not exist here, the row falls back to "(active)",
  and "State" says which workset was missing.
- Settings (placement, link type, checked worksets, the rule) are saved whenever the window closes,
  **in any way** — including "Close" and Esc.

### Base File (Project panel)

Works **only in a project** (`.rvt`). Links the coordination ("base") file and immediately sets up
the project against it — what every discipline model starts with.

What for: the same thing at the start of every discipline of every project, five commands
scattered across the ribbon, and the order between them matters:

1. link the base file;
2. acquire shared coordinates from it ("Coordinates → Acquire Coordinates");
3. name the project site the way it was agreed on for the job;
4. pin the link so it cannot be dragged with the mouse;
5. switch to "00_Shared levels and grids" — so the copied levels and grids land right there;
6. copy the levels and grids by monitoring.

The button does steps 1–5 as one operation. **It cannot do the sixth** — see below.

**The button offers the base file on its own.** When the window opens it looks at the open model's
name, takes the building number from it (`MK3-VSC-B01-VOIDS` → **B01**), and looks for that
building's base file right where the model itself lives: in the **01_Base Model** folder next to
the discipline folders. If one matching model is found, it is filled in, with a caption underneath
saying where it came from. There is usually nothing to look for or navigate to: the window opens
already showing `MK3-VSC-B01-BM`.

The guess works on disk, on Revit Server and in BIM360 alike — in whichever store the open model
lives. What it does **not** do: it does not choose for you when several models match (their names
are then listed in the caption, and the choice is yours), and it does not stay silent when
something did not add up — the caption under the model name says exactly what: no building number
in the name, the folder was not found, the folder has no model of this building. The **"Guess"**
button repeats the search at any time; the **"guess it automatically"** box turns it off entirely,
for anyone who always picks the file by hand. If the model remembered from last time is already
from this building, the search does not even run — in a new discipline of the same building the
base file is the same one.

**Where to take the model from, if the guess does not fit.** The same four sources as "Link
Manager": a file from disk or a network folder, the Revit Server tree, browsing BIM360/Autodesk
Docs, and entering a cloud model as a pair of GUIDs. The server name is shared with "Link
Manager": no need to type it in twice. If a link to this model is already in the project, the
button uses it rather than setting up a second one.

**Placement** defaults to "Origin to origin" rather than "By shared coordinates" as in "Link
Manager": the shared coordinates have not yet been acquired from the base file at this point.

**There are two worksets, and they are different.** "Put the link into the workset" is where the
base file link itself goes; **01_Link_BM** is filled in by default (the same convention as
consultants' "01_Link_OV"). "Switch to the workset" is where the command switches before copying;
there it is **00_Shared levels and grids** — exactly the workset the copied levels and grids will
land in. Neither workset may exist yet in a new discipline — the button creates them. Both fields
can be typed into by hand, chosen from the project's own worksets, or cleared (then the link goes
into the active workset).

**The site name and both worksets** are remembered in the Windows profile together with the model
itself: in the next discipline of the same building everything is already filled in. For a link
already in the project, the field shows its current workset — and changing it here really does
move the link.

**About copy-monitoring.** The Revit API cannot create monitoring links at all — that capability
does not exist in 2022, 2024 or 2025 (only reading existing ones is possible). So as its last
step the button **opens the mode itself** — "Copy/Monitor → Select Link": all that is left is to
click the base file and check off the levels and grids. The button deliberately does not offer to
copy them without monitoring: on a plan such levels look exactly the same, but they carry no link
to the base file — a mistake that surfaces a month later.

Worth knowing:

- Everything except switching to the workset is done as one operation and rolls back with one
  Ctrl+Z. Ctrl+Z does not restore the active workset — that is session state, not part of the model.
- Any step can be turned off with its check box: link and acquire coordinates, say, without
  touching the site.
- What worked and what did not — in the report after running, together with Revit's own warnings.
- Settings are saved whenever the window closes, **in any way** — including "Close" and Esc.
- If your base file folder is named differently from `01_Base Model`, or the base model code in
  the names is not `BM`, this is edited in `%AppData%\VladTools\basefile\_settings.txt` (the
  `BASE_FOLDER` and `BASE_CODE` lines). Which piece of the model name counts as the building
  number lives in the same place as for the "Building Kit": `links\_settings.txt`, the `KIT_TOKEN` line.

### Auto Dimensions (Project panel)

Works **only in a project** (`.rvt`), on a **floor plan**. Places along the sides of the selected
rooms the same dimension chains you once placed by hand along one wall — routine work for
architectural masonry plans.

How it works:

1. Select the finished dimensions along one wall of one room (overall, opening widths, opening
   centres, adjoining partition widths — whichever are needed, not necessarily all at once) and
   press the button. If rooms are selected too, they become the placement area; if not, the window
   offers to take every room on the active plan.
2. In the window that opens, press **"Take a sample…"** and select those very dimensions,
   "Finish". The window closes and reopens already parsed into a table: every row has a chain
   kind, an offset from the wall (in mm) and a dimension type.
3. Parsing is a guess, not an exact calculation: a row can always be fixed by hand (change the
   chain kind from the drop-down, adjust the offset, the dimension type), a new one added with
   "Add chain", or an extra one removed.
4. At the top — **"Room boundary"** (defaulting to "By core layer faces", exactly what a masonry
   plan needs) and **"Place dimensions"** (into the room or outward — one setting for the whole set).
5. Below that — three check boxes, also shared by the whole set:
   - **"Capture the thickness of adjoining walls"** (on by default). A chain starts and ends not
     at the room corner but at the far face of the adjoining wall, so the first and last link
     becomes its thickness: `120 | 3775 | 120`. That is exactly how every chain on a masonry plan
     is built. Turn it off and the chain runs corner to corner.
   - **"Pull small labels out onto a leader"** (on by default). A value that does not fit between
     its ticks (a 120 mm pier at 1:50, say) is moved off the line, and Revit draws a leader to it —
     otherwise such labels overlap each other. The label width is estimated from the dimension
     type's font height, its width factor and the view scale: this is a generous guess, not an
     exact calculation.
   - **"Remove what this button placed before"** — see point 6.
6. The **"Place (N)"** button places chains along every side of every selected room in a single
   transaction — it can be undone with one Ctrl+Z. Running it again on the same rooms does not
   double the dimensions: the previous ones, placed by this same button, are removed first (the
   "Remove what this button placed before" box can turn this off). Dimensions placed by hand are
   never seen or touched by the button.

Chain kinds:

| Kind | What it picks up |
|---|---|
| Overall | only the two end corners of the side |
| Openings | the side corners + the jamb faces of every door and window on it |
| Opening centres | the side corners + opening centre lines (available only if the family publishes a centre plane) |
| Partitions | the side corners + the faces of the partitions meeting it from inside the room |
| Wall faces | the corners + the wall joints inside the side (for a side made of several walls in a row) |
| All combined | openings + partitions on one chain — what a masonry-plan chain is made of |

Opening centres are deliberately absent from "All combined": a centre line splits each opening in
half and adds a tick in the middle of every door, making the chain twice as dense and unreadable.
If centres are needed, add a separate "Opening centres" chain alongside.

**Templates.** A set of chains is saved with the "Save template" button to
`%AppData%\VladTools\autodim\` and carried into another project together with the boundary, the
direction and both check boxes. The dimension type is stored by name, not by id — ids are the
project's own elsewhere; if such a type does not exist in the project, the row falls back to the
current default type, which is noted in "State".

Details:

- A side made of a curved wall is skipped — auto dimensions only work along straight stretches.
- A chain with nothing to pick up on a particular side (an "Opening centres" chain on a side with
  no openings, say) is simply skipped on that side — with the reason in the final report; it does
  not affect the other chains or sides.
- If a side's corner could not be found — this happens when a side starts in the middle of a wall
  (behind a room separator) and no reference exists at that point at all — the chain is still
  placed, from the nearest face instead, and goes into the report under a section of its own,
  **"Chains that need checking"**. Check those by hand: they are shorter than they should be.
- Only the active floor plan of the open project is affected; linked models are left alone.

### Accept Changes (Project panel)

Works **only in a project** (`.rvt`). Puts the grids and levels of a discipline model in line with
a new issue of the coordination (base) file — what Revit does through "Collaborate → Coordination Review".

What for: the base file is reissued every week, a couple of grids have shifted in it and a storey
changed elevation, and in "Coordination Review" every change is accepted one at a time — expand a
node, pick an action, repeat. On a building where a dozen grids have shifted that is dozens of
clicks, repeated after every new issue.

**What the button does.** It takes the project's grids and levels that monitor the link (that is,
copied through "Copy/Monitor"), compares them against that same link, and shows what differs:

| Row | What it means | Applied |
| --- | --- | --- |
| Position | the grid has shifted or rotated, the level has a different elevation | yes |
| Name | the element is named differently in the base file | yes |
| Not in the coordination file | something monitors the link, but no matching element was found in it | no, shown only |
| New in the coordination file | the element exists in the base file, but nothing in the project monitors it | no, shown only |
| Cannot be applied | an arc grid's radius changed, a grid became a line instead of an arc, and the like | no, shown only |

Everything applicable is checked right away — that is the whole point of the button. Check marks
can be edited by hand, and what is checked is applied as one operation and rolls back with one
**Ctrl+Z**.

**What the button does not do, and why.** It does not delete grids and levels that vanished from
the base file: a level would take everything standing on it with it, and that decision belongs to
a person. And it does not set up monitoring on new link elements — the Revit API cannot create
monitoring links at all (the same limitation as the "Base File" button). Both are shown in the
table as a row, to be sorted out by hand.

**About "Coordination Review" itself.** The add-in cannot press "Accept" inside that dialog — it
is simply not in the Revit API. The button edits the model itself, and Revit's own list empties
itself afterwards: it shows the difference between the copy and the original, and there is none
left. The **"Open Coordination Review after applying"** box (on by default) opens the dialog right
after the report — to confirm this by eye.

Worth knowing:

- **A grid is aligned as a line, not a segment.** You trim a grid's length to fit your own views —
  the button leaves that alone, only the position and the tilt are aligned.
- **A difference accepted earlier here looks like a discrepancy.** If you once pressed "Accept
  Difference" in "Coordination Review" and left a grid deliberately shifted, the button will show
  that shift: the Revit API does not let the "allowed" offset be read. Uncheck such a row.
- **There can be several links** — chosen in the "Coordination file" field at the top of the
  window; the one with the most differences comes first. An unloaded link has nothing to compare
  against, and the window says so.
- An element renamed in the base file and moved far away at the same time is not matched by the
  button and is honestly shown as two rows — "not in the file" and "new in the file".
- Pinned grids and levels are edited normally: the pin is removed for the duration and put back after.
- What worked and what did not — in the report after running, together with Revit's own warnings.

## Structure

```
VladTools.sln
VladTools.addin                  the Revit manifest (copied into the add-ins folder)
build.ps1                        build + install
src/VladTools/
  App.cs                         IExternalApplication: the tab, panels, buttons
  Commands/
    Create3DThumbnailCommand.cs       logic for the "3D Thumbnail" button
    DeleteSharedParametersCommand.cs  logic for the "Delete Parameters" button (family)
    AddFormulasCommand.cs             logic for the "Add Formulas" button
    RenameNestedFamiliesCommand.cs    logic for the "Rename Nested" button
    DeleteProjectParametersCommand.cs logic for the "Delete Shared Parameters" button (project)
    CleanupCommand.cs                 logic for the "Cleanup" button (project)
    LinkManagerCommand.cs             logic for the "Link Manager" button (project)
    BaseFileCommand.cs                logic for the "Base File" button (project)
    AutoDimensionCommand.cs           logic for the "Auto Dimensions" button (project)
    AcceptCoordinationCommand.cs      logic for the "Accept Changes" button (project)
  UI/
    DeleteParametersWindow.cs        the family parameter table window, check boxes and a rule (WPF, built in code)
    SharedParameterRow.cs            a row of that table (check box + parameter data)
    DeleteProjectParametersWindow.cs the project shared parameter table window: a "Show" filter + a rule
    ProjectParameterRow.cs           a row of that table (check box, parameter data, its binding)
    AddFormulasWindow.cs             the "parameter — formula" table window
    FormulaRule.cs                   a row of that table
    FamilyParameterInfo.cs           a snapshot of a family parameter for row validation
    RenameNestedWindow.cs            a "find and replace" window over nested names + the name buffer
    NestedFamilyRow.cs               a row of that table (element, current and new name, validation)
    FamilyDimensionScan.cs           the result of scanning families for dimension labels
    CleanupWindow.cs                 the "Model Cleanup" window: a list of items with check boxes and "Select all"
    CleanupOption.cs                 an item of that list (check box, count found, explanation)
    CleanupTarget.cs                 the list of what "Cleanup" is able to remove
    LinkManagerWindow.cs             the "Link Manager" window: the link table, placement, worksets
    ModelKitWindow.cs                the "Building Kit" window: guessing links by building number
    ModelKitRow.cs                   a row of that table
    GridBuilder.cs                   shared table column assemblies
    LinkRow.cs                       a row of that table (check box, model, state)
    WorksetRow.cs                    a row of the workset list (check box, name, where it occurs)
    LinkWorksetScan.cs               the result of reading worksets from links
    ModelBrowserWindow.cs            a model tree with check boxes — shared by Revit Server and BIM360
    BrowseNode.cs                    a node of that tree: a folder or a model
    CloudLinkWindow.cs               entering cloud models as pairs of GUIDs
    ModelPicker.cs                   picking a model from a file, Revit Server, BIM360 or a pair of GUIDs
    BaseFileWindow.cs                the "Base File" window: a model and a list of what to do with it
    AutoDimensionWindow.cs           the "Auto Dimensions" window: the chain table, boundary, direction, templates
    DimensionChainRow.cs             a row of that table (chain kind, offset, dimension type)
    DimensionChainKind.cs            the catalogue of chain kinds (Overall/Openings/Centres/Partitions/…)
    DimensionTypeInfo.cs             a snapshot of a dimension type for this window
    AcceptCoordinationWindow.cs      the "Accept Changes" window: choosing a link and a difference table
    CoordinationChangeRow.cs         a row of that table (check box, what differs, before → after)
    CoordinationChangeKind.cs        the kinds of difference (Position/Name/Missing/New/Cannot be applied)
    CoordinationScan.cs              the result of comparing against one link
  Infrastructure/
    Ribbon.cs                    creating the panel and the buttons
    Icons.cs                     loading icons from the assembly's resources
    FormulaLibrary.cs            reading and writing the formula list file (%AppData%)
    FormulaParser.cs             finding names in a formula that are not family parameters
    NameBuffer.cs                reading and writing the name buffer (%AppData%)
    WarningSuppressor.cs         suppresses Revit warnings during a batch edit and gathers them for the report
    DimensionLabelCache.cs       the saved family scan for dimension labels (%AppData%, one file per project)
    JsonHttp.cs                  JSON parsing and HTTP requests (no third-party libraries)
    AutodeskSession.cs           the Autodesk token of the user signed in to Revit
    AccClient.cs                 the BIM360/ACC tree: accounts, projects, folders, models
    RevitServerClient.cs         Revit Server folders and models through its REST service
    LinkSetLibrary.cs            saved link sets (%AppData%)
    LinkPreferences.cs           the "Link Manager" window settings (%AppData%)
    ModelStore.cs                a model folder across the three stores: what is inside, what is above
    ModelKit.cs                  guessing a link kit by building number and discipline folders
    LinkCatalog.cs               the links and worksets of the open project
    BaseFilePreferences.cs       the "Base File" window settings (%AppData%)
    BaseFileFinder.cs            guessing the base file from the building in the open model's name
    RoomSide.cs                  one straight side of a room boundary (direction, normal, walls)
    RoomSideBuilder.cs           builds sides from a room's GetBoundarySegments
    DimensionReferenceCollector.cs  a side + a chain kind → references for NewDimension
    DimensionSampleReader.cs     parsing sample dimensions into a chain template
    DimensionTextLayout.cs       pulls labels that do not fit between their ticks out onto a leader
    DimensionTemplate.cs         auto-dimension templates and the window settings (%AppData%)
    AutoDimensionMarker.cs       the "this dimension was placed by the button" mark (ExtensibleStorage), against duplicates
    CoordinationCatalog.cs       comparing the project's grids and levels against the coordination file
    DatumUpdate.cs                a ready-made edit for a single grid or level: rotation, translation, elevation, name
  Resources/                     16×16 and 32×32 PNG icons (embedded in the DLL)
```

## Build and install

```powershell
.\build.ps1
```

Requires the .NET SDK and an installed copy of Revit — the version is set by `RevitVersion`
(defaulting to `2022`, from `Directory.Build.props`; `RevitAPI.dll` and `RevitAPIUI.dll` are taken
from there). The target framework depends on the year: `net48` for 2022 and 2024, `net8.0-windows`
for 2025 (Revit 2025 itself runs on .NET 8).

`.\build.ps1` with no arguments builds and installs **all three years in one run**, skipping the
ones not installed on the machine. To build a single year:

```powershell
.\build.ps1 -RevitVersion 2024
dotnet build src\VladTools\VladTools.csproj -c Release -p:RevitVersion=2024   # the same, directly
```

After building, the files go into `%AppData%\Autodesk\Revit\Addins\<year>\`:

- `VladTools.addin`
- `VladTools\VladTools.dll`

Revit needs to be restarted. If Revit was open during the build, the DLL is locked — close it and build again.

Build without installing: `.\build.ps1 -NoDeploy` (or `-p:DeployToRevit=false` when calling
`dotnet build` directly).

To remove the add-in: delete `VladTools.addin` and the `VladTools` folder from
`%AppData%\Autodesk\Revit\Addins\<year>\`.

## How to add a new button

1. A new class in `src/VladTools/Commands/`:

```csharp
[Transaction(TransactionMode.Manual)]
public class MyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        // ...
        return Result.Succeeded;
    }
}
```

2. Icons `Resources/name_16.png` and `Resources/name_32.png` (optional — without them the button
   just has no picture).
3. Register it in [App.cs](src/VladTools/App.cs) next to an existing button:

```csharp
Ribbon.AddPushButton(panel,
    name: "VladTools_MyCommand",
    text: "My\ncommand",
    commandType: typeof(MyCommand),
    tooltip: "What the button does",
    iconBaseName: "name");
```

For a new panel: `var panel2 = Ribbon.GetOrCreatePanel(application, TabName, "Panel name");`

## Porting to another Revit version

The year is never written into the csproj by hand — it is a build key, `-p:RevitVersion=<year>`
(`.\build.ps1 -RevitVersion <year>` or `dotnet build -p:RevitVersion=<year>`), which drives the
paths to `RevitAPI.dll`, the install folder and the target framework. `RevitServerClient.ServiceVersion`
needs no edit either — the Revit Server year is filled in from the running Revit itself, when the
add-in starts.

For 2022, 2024 and 2025 all of this is already done — a detailed rundown of what changed and what
to check first for the next year is in [CHECKLIST-Revit2024.md](CHECKLIST-Revit2024.md) and
[CHECKLIST-Revit2025.md](CHECKLIST-Revit2025.md). For a year after 2025, a rebuild alone may not be
enough again, if Autodesk changes the runtime once more — as already happened moving to 2025
(`net48` → `net8.0-windows`); check `RevitAPI.runtimeconfig.json` next to the new year's
`RevitAPI.dll`.
