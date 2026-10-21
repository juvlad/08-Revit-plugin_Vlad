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
opens; the "Read Worksets" button reads the names from the models themselves without loading them.
A BIM360 / Autodesk Docs model is the one exception: Revit cannot read a cloud model's worksets
from outside, and opening the model just to read them made the link itself fail afterwards. So a
BIM360 model is linked first, with the "On load" setting alone, and its worksets are read from the
link right away; if any of them is not as asked, the link is loaded a second time with the real
setup — the report lists it under "took hold only on a second load", and that second load clears
Revit's undo history, like every reload. "Read Worksets" shows a BIM360 model's names only once it
is linked; one not linked yet reads "Worksets read once linked".

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
report reads one of four things: silence — everything closed as asked; "took hold only on a second
load" — Revit did not comply the first time, or the worksets could only be read once the link was
already loaded; a line in "Could not be done" with the workset names — it did not
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
  the report: "took hold only on a second load" (that also wipes the undo history) — or, if it did
  not help the second time either, an ordinary line in "Could not be done" with the names of the
  worksets that are still wrong. If a link's worksets could not be read at all, the report says so
  ("could not read the worksets…, the link was loaded as is") and does not blame Revit for not
  closing them: in that case no workset setup was sent to Revit.
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
- **A folder on disk is chosen in the ordinary file dialog**: open the folder and press "Open" with
  "Choose this folder" left in the name box — or point at any model in it. These .NET versions have
  no folder-picker dialog of their own, and dragging in an extra library just for that is not worth it.
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
| Deleted from the coordination file | something monitors the link, but no matching element was found in it | yes, but only on request — see below |
| New in the coordination file | the element exists in the base file, but nothing in the project monitors it | no, shown only |
| Cannot be applied | an arc grid's radius changed, a grid became a line instead of an arc, and the like | no, shown only |

Everything applicable is checked right away — that is the whole point of the button — **except the
deletions**. Check marks can be edited by hand, and what is checked is applied as one operation and
rolls back with one **Ctrl+Z**.

**Deleting what is gone from the base file.** A grid or a level deleted in the new issue of the
coordination file is shown in red, and the **"Delete grids and levels that are gone from the
coordination file"** box turns such rows on. Until it is checked, they are shown but cannot be
selected — and even with it on, nothing is checked for you: deleting a level takes **everything
standing on it** with it, and the "State" column says how many elements that is for each row. The
confirmation names every element to be deleted, and the report says how much really went. A pinned
grid or level is unpinned for the deletion, like everything else here; if the deletion does not go
through, the pin comes back.

Worth pausing over: "gone from the file" means the button found no match **by name and then by
position**. A consultant who renamed a grid *and* moved it far away in one issue produces "deleted"
plus "new" rather than a match — check such a pair before deleting anything.

**What the button still does not do.** It does not set up monitoring on new link elements — the
Revit API cannot create monitoring links at all (the same limitation as the "Base File" button).
Those are shown in the table as a row, to be copied in by hand through "Copy/Monitor".

**The button really moves the grid — and checks that it did.** This is the "Modify Grid" action of
Revit's own dialog, not "Accept Difference": the grid is rotated about its own midpoint and shifted
sideways until it lies on the line the base file has, and the level is given the new elevation.
After the operation every moved element is **read back and compared against the link once more**,
because Revit taking an edit without an error is not the same as the element moving — a constraint
or a lock on the datum, or a group it sits in, can hold it in place. What really landed goes into
"Accepted"; what did not gets a section of its own, **"Revit did not carry these out"**, with how
much of the difference is left. And if Revit rolled the whole batch back when committing, the report
says exactly that instead of listing changes that never happened.

**About "Coordination Review" itself.** The add-in cannot press "Accept" inside that dialog — it
is simply not in the Revit API. The button edits the model itself, and Revit's own list empties
itself afterwards: it shows the difference between the copy and the original, and there is none
left. The **"Open Coordination Review after applying"** box (on by default) opens the dialog right
after the report — to confirm this by eye.

Worth knowing:

- **A grid is aligned as a line, not a segment.** You trim a grid's length to fit your own views —
  the button leaves that alone, only the position and the tilt are aligned.
- **If a grid would not move**, it is named in the report with the difference still left on it.
  Almost always it is held by something: a dimension lock or another constraint on the datum, or a
  group. Free it and press the button again.
- **A difference accepted earlier here looks like a discrepancy.** If you once pressed "Accept
  Difference" in "Coordination Review" and left a grid deliberately shifted, the button will show
  that shift: the Revit API does not let the "allowed" offset be read. Uncheck such a row.
- **There can be several links** — chosen in the "Coordination file" field at the top of the
  window; the one with the most differences comes first. An unloaded link has nothing to compare
  against, and the window says so.
- An element renamed in the base file and moved far away at the same time is not matched by the
  button and is honestly shown as two rows — "deleted from the file" and "new in the file".
- Pinned grids and levels are edited normally: the pin is removed for the duration and put back after.
- What worked and what did not — in the report after running, together with Revit's own warnings.

#### Other models — a whole batch at once

The window's **"Other models…"** button does the same thing for models nobody has open: you gather
a list of models, and each one is opened in turn, its grids are put where the coordination file
linked into it has them, and the model is synchronised back. The same button appears if the open
project has nothing monitoring a link at all — that is the usual place to start this work from.

What for: a new issue of the base file shifts the grids for every discipline at once, and putting
them in line means opening a dozen models one after another, waiting for each and pressing the same
button in it.

**How the models are gathered.** The same four sources as in "Link Manager" — files, Revit Server,
BIM360 and BIM360 by GUID — plus **"Folder…"**: every model in a folder on disk, on Revit Server or
in BIM360 (and, if you say so, in the folders inside it; Revit's own backups are left out). And the
same saved **sets**: a list gathered once is loaded by name with "Load set" and kept with "Save set",
and those sets are shared with "Link Manager". The list the window was closed with comes back the
next time it opens.

**Only two worksets are opened in each model**, and this is the setting worth understanding:

- `00_Shared levels and grids` — where the grids themselves live;
- `00_Link_BM` — where the coordination file is linked in. **The start of the name is enough**, so
  `00_Link_BM_K3` and any other suffix are opened too.

Everything else in the model stays closed. That is not only about opening faster: what a closed
workset holds is not in the document at all, so the run cannot touch a single element it was not
asked about. The flip side is that the names have to match — a model where none of them did is
named in the report together with the worksets it actually has, because "nothing found" there means
"nothing was opened to look at", not "the model is fine". The list is editable in the window
(semicolons between the names) and is remembered.

**What is changed, and what is not.**

- **Grids** — always, by position.
- **Levels** — only with the "Levels as well" box on. It is off by default: a level moved in a
  discipline model takes every wall, room and view standing on it along, and here nobody is
  watching it happen.
- **Names** — only with the "Rename…" box on, off by default for the same kind of reason: a name
  is what views, schedules and somebody's drawings refer to.
- **Nothing is ever deleted.** Grids and levels gone from the coordination file are counted in the
  report and left where they are. Deleting one is a decision to take with that model open, in the
  main window of this button.

**How a model is opened and given back.** A **local copy** is made of every workshared model
(exactly as "Create New Local" does), the edit happens there, and the result goes back through
**"Synchronize with Central"** with everything relinquished; the local copy is then deleted. The
central file is never edited in place — and where a local copy cannot be made, the model is skipped
with the reason rather than opened anyway. A cloud model works the same way, with Revit keeping the
local itself.

Worth knowing:

- **There is no Ctrl+Z afterwards.** Each model is synchronised as soon as it is done — unlike the
  main window, whose changes to the open project undo with a single Ctrl+Z.
- **Revit is busy until the whole batch is finished**, with no progress bar: the Revit API is
  single-threaded, and the work is the opening of one model after another.
- **A BIM360 / Autodesk Docs model is opened twice** — first with every workset closed, only to
  find out what its worksets are called, and then properly with the two of them. Revit can read the
  worksets of a file or of a Revit Server model without opening it, but not of a cloud one; the
  first open loads nothing at all, so the cost is much smaller than it sounds. If a cloud model
  cannot be opened even like that, the report says so — the usual reasons are no access to the
  project, not being signed in to Autodesk, or the model belonging to a newer Revit.
- **A model already open in Revit is skipped** with a note — including the project you are standing
  in. Use the main window for that one.
- **An element somebody else has borrowed** cannot be edited: it is named in the report together
  with whoever is holding it.
- Every model gets a line of its own in the report: what was put in line with the file, what Revit
  refused to move, what was left alone and why.
- The settings live in `%AppData%\VladTools\coordination\_settings.txt`.

### Schedule Library (Project panel)

Works **only in a project** (`.rvt`). Carries schedules out of a base model into every model after
it — what Revit does through "Insert → Insert Views from File".

What for: a discipline works by the same few schedules, drawn once and then dragged into every new
model by hand. Each time it means finding the base model on the network again, walking the dialog
again and picking the same three schedules out of its whole list again.

**What the button does.** It keeps a **set** of schedules in the Windows profile and inserts the
checked ones into the open project in a single operation.

A set is filled from any model:

- **"Open project"** — the fastest way and the one the button is built around: stand in the base
  model, press it, and the schedules are taken straight from there, with nothing to open;
- **"File…" / "Revit Server…" / "BIM360…" / "by GUID…"** — any other model. It is opened in the
  background and closed again right after; a large model takes a while, and that happens once.

Then, in any model at all, choose the set and press "Insert".

**What is worth knowing.**

- **The set is named in the "Set" box**, and on a first run it already holds a name ("Schedules"),
  so nothing has to be typed in before the first "Open project". Any other name typed in makes a new
  set; a name chosen from the drop-down opens an existing one.
- **A set is a small Revit file**, not a list of names: `%AppData%\VladTools\schedules\<Revit
  year>\<set name>.rvt`. That is why a schedule arrives whole — fields, filters, sorting,
  formatting — and why the base model is not needed to insert it. The file can be copied to a
  colleague as it is.
- **The set is not updated on its own.** Change a schedule in the base model, and the set keeps the
  old one until the schedules are taken again — taking one of the same name refreshes it rather
  than doubling it.
- **A set belongs to its Revit year.** A Revit file never opens in an older version than the one
  that wrote it, so 2022 and 2025 keep their sets apart; in a new year they are captured again.
- **Where the project already holds a schedule of that name**, the "If the name is taken" column
  asks what to do with it, per row:
  - *Skip* — the project's own stays, nothing is inserted (the default: a button that inserts must
    not quietly delete somebody's schedule);
  - *Replace* — the project's own is deleted and the one from the set takes its place, **going back
    onto the same sheets**; how many sheets that is is shown in the table before anything is done;
  - *Insert as a copy* — it arrives alongside, under a free name ("Doors (2)").
  The drop-down at the bottom of the window sets the same action on every clashing row at once.
- **A key schedule carries its key values along** — that is data, not merely a view. Such rows are
  marked "Key schedule" in the "Kind" column, and the choosing window counts them separately.
- **"Remove from set"** throws the checked schedules out of the set; **"Delete set"** removes the
  whole set from the profile. Neither touches the project.
- Everything checked is inserted as one operation and rolls back with a single Ctrl+Z.
- What was inserted, what replaced what and what did not work — in the report after running,
  together with Revit's own warnings.

### Parameter Sets (Project panel)

Works **only in a project** (`.rvt`). Applies a saved bundle of shared parameters to the open
project — what "Manage → Project Parameters → Add" does, one parameter at a time, by hand.

What for: the office's own shared parameters (a mass, a code, a status) have to be present in every
project with exactly the same settings — instance or type, which categories, which group, whether
the value can vary between the instances of a model group. Setting each one up by hand, project
after project, drifts: a category forgotten here, a group different there.

**What the button does.** It keeps a **named set** of parameters in the Windows profile — built
once from a shared parameter file — and checks it against the open project every time the window
opens.

1. **"Add from shared file…"** picks definitions out of a shared parameter file (Revit's own — the
   one it is already configured with, or another one chosen with "Browse…"). Only the GUID and the
   name come from there; everything else is set up in the table below.
2. For every parameter in the table: **Binding** (Instance/Type), **Vary by group** (meaningful
   only for an instance parameter — Revit's own "values can vary by group instance"),
   **Categories** (its own small picker, opened from the table), **Group** (the parameter group
   Revit shows it under). Set one row up and **"Copy settings" / "Paste settings"** carry Binding,
   Categories, Group and "Vary by group" onto other rows: highlight the row done by hand, press
   "Copy settings", then highlight the rows that should match it (click, Ctrl+click, Shift+click —
   this is the table's own row highlight, not the apply check boxes) and press "Paste settings".
   The name and the GUID of every row stay its own.
3. **"State"** compares the row against the open project as the table is edited: *"Not in the
   project yet — will be added"*, *"Already matches"*, or *"Will be updated — …"* naming exactly
   what changes (categories added, group changed, "varies across groups" changed). A row that
   would **lose data** — a category taken away, or instance swapped for type and back — is coloured
   and spelled out, and confirmed separately before applying.
4. **"Apply to Project"** binds what is missing and re-binds what differs, in one operation; it
   rolls back with a single Ctrl+Z. A parameter that already matches is left alone entirely, and
   the report counts it separately ("Already as required") — so a run that changed nothing says so
   instead of claiming an update.

**What is worth knowing.**

- **The parameter's identity is its GUID, never its name.** A name can be typed differently by
  whoever names things in the shared file; the GUID cannot. A set entry is looked up in the project,
  and in the shared file when applying, by GUID alone.
- **Categories are matched by `BuiltInCategory` name**, so a set travels between projects in any
  language Revit runs in — the category picker only ever offers what this project can actually bind
  a parameter to. A category the set carries but this project does not have is simply noted and
  skipped, not treated as a difference to keep re-applying.
- **The shared parameter file is remembered with the set.** Applying reaches for the file the set
  was built from, whatever Revit itself happens to be pointed at — and puts Revit's own setting back
  afterwards. A parameter already present in the project needs no shared parameter file at all.
- **Removing a category from an already-bound parameter loses its values on that category** — Revit
  has no undo for that beyond Ctrl+Z in the same session. Such a row is flagged in the table and
  called out again before "Apply to Project" runs.
- **"Save set"** writes the table as it stands to the Windows profile; edits in the table are not
  written until it is pressed. **"Remove from set"** and **"Delete set"** only ever touch the saved
  set, never the project.
- What was added, what was updated and what did not work — in the report after running, together
  with Revit's own warnings.

### Worksets (Project panel)

Works **only in a project** (`.rvt`), and only in a workshared one. Shows every workset of the
project and removes the checked ones in one batch — what "Collaborate → Worksets → Delete" does, one
workset at a time.

What for: a model comes back from a consultant carrying two dozen of their worksets, and each one
has to be selected, deleted, and answered for separately — "what do I do with the elements in it?" —
before the next one.

**What the button does.**

1. The window opens on the **plain list of the project's worksets**: the name, how many elements
   stand in each, and its state — the active one, a closed one, one owned by another user. Nothing
   is checked to begin with.
2. **Check what to remove.** Click, Ctrl+click or Shift+click and press Space to flip a whole run of
   rows; the box in the header takes the lot. A workset somebody else owns has no check box at all —
   Revit would refuse it, and the reason is written in "State" instead.
3. **Answer the question under the table, once for the whole batch** — the one Revit asks per
   workset: **"Move them to workset …"** (pick a workset that is staying) or **"Delete them together
   with the worksets"**. Moving is what the window starts on: it is the answer that loses nothing.
4. The "State" column spells out what each checked row will cost — *"14 elements move to
   \"01_Link_AR\""*, *"Will be removed — it is empty"*, *"9 elements will be DELETED with the
   workset"* — and the summary line at the bottom adds it all up before anything is pressed.
5. **"Remove worksets"** confirms first, naming every workset and the answer in full, and then runs.
   Everything goes in one operation and undoes with a single Ctrl+Z.

**What is worth knowing.**

- **A closed workset is flagged, never counted.** The add-in cannot see inside a closed workset, and
  Revit offers no way to open one from the API — so it says "not counted" rather than "0 elements".
  On the delete path this matters: whatever is in there goes, unseen. Open those worksets in Revit
  and look first; the confirmation lists them separately for that reason.
- **The worksets you are removing are checked out for you**, along with the one the elements move
  into — without that Revit refuses to delete anything in a freshly opened local model. One owned by
  another user cannot be removed until they relinquish it.
- **The active workset is stepped off automatically** if it is one of the ones going, because Revit
  will not delete the workset it is standing on. That switch is a session setting, so Ctrl+Z does
  not put it back; the report says which workset is active now.
- **At least one workset has to stay** — Revit keeps no workshared project without one, and there
  would be nowhere to move the contents to. The window will not let everything be checked.
- **Ctrl+Z only reaches until the model is synchronised.** Save or synchronise before removing
  worksets with their contents.
- Each workset is deleted in a step of its own, so one Revit refuses does not cancel the others; the
  whole run is still a single undo. What went, what did not and why — in the report afterwards,
  together with Revit's own warnings.

### Translate (Project panel)

Works **only in a project or a project template** (`.rvt`, `.rte`). Replaces the Russian a person
typed into the file with English.

What for: a template made in a Russian Revit stays Russian when it is opened in an English one.
Revit translates only its own words — categories, built-in parameters, system families, view types
("Floor Plan", "Sheets") — while everything a person typed is kept exactly as typed: view and view
template names, the values the Project Browser groups views by ("01 - Отверстия WIP", "Планы"),
type and family names, schedule headings, text notes. There is no switch for that in Revit; the
strings have to be replaced.

**What the button does.**

1. The window lists **every text with Cyrillic in it, once per spelling**, with **where it is used**
   (hover over "Where" for the full list): "Планы" used as a parameter value on twelve views is one
   row, not twelve.
2. **The English column** is filled in three ways, and they mix freely:
   - **from the dictionary** — whatever was translated in an earlier project is filled in the moment
     the window opens;
   - **"Export…" → translate → "Import…"** — the list goes into a text file (the Russian, a TAB, the
     English, a TAB, where it is used); fill in the second column in Notepad or Excel, or hand the
     file to a translator, DeepL or an AI — the lines at the top of the file explain the format to
     whoever does it — and read it back;
   - **by hand**, right in the cell.
3. **"State"** checks every translation before anything is applied: a translation used as a name
   that would clash with another name ("План этажа" and "План эт." both becoming "Floor plan"), a
   character Revit does not allow in names (``\ : { } [ ] | ; < > ? ` ~``), a translation that still
   has Cyrillic in it. **"Show → Need attention"** lists those rows; the button stays off until the
   clashes are fixed or their rows unchecked.
4. **"Translate"** replaces every checked text **everywhere it is used, at once**, in one operation —
   one Ctrl+Z undoes all of it.

**What is translated:** the names of views, view templates, sheets, schedules, legends, families and
types, materials, fill and line patterns, filters, levels, grids, phases, phase filters, print sets,
project and global parameters, worksets; the values of text parameters (project information, sheet
numbers and names, room names, "Title on Sheet", any project or shared parameter — including the
ones the Project Browser groups by); schedule column headings, the plain-text cells of a schedule's
header and the values schedule filters compare against; the strings in view filter rules; text
notes (bold, italic, underline and all caps covering the whole note are kept).

**What is worth knowing.**

- **One text is replaced everywhere at once — and that is the point, not a convenience.** A string is
  often data: "Отверстия" in a view parameter is also what a view filter, a schedule filter and the
  browser grouping compare against. Translated in one place only, the filter would silently stop
  matching. Here they all change together.
- **What the Revit API cannot change is shown, not skipped.** A shared parameter's name is fixed by
  its GUID in the shared parameter file — Revit will not rename it even by hand. Line styles and
  object-style subcategories have no rename in the API — Manage → Additional Settings → Line Styles
  and Manage → Object Styles do it by hand. Such rows say so in "State", and the report lists them.
- **The Project Browser is watched.** A browser organization can filter views by a value, and the
  API can neither read nor edit that filter. If a translated value makes views drop out of an
  organization, the report says which organization and which views — fix its filter by hand
  (View → User Interface → Browser Organization → Edit → Filtering), or undo with Ctrl+Z.
- **The search box and "Show" only find rows, they do not select them.** Rows out of sight keep
  their check marks and are translated too; the confirmation says how many of them there are.
- **Every translation is remembered** — whatever the English column holds when the window closes,
  applied or not, goes into the dictionary (`%AppData%\VladTools\translate\dictionary.txt`) and is
  offered in the next project. Lines of an imported file that match no text of this project are kept
  there too.
- **Excel:** the exported file opens straight into columns. Save it back as **"Unicode Text
  (*.txt)"** — the other text formats lose the Cyrillic.
- **Not looked into:** text inside loaded families — tag labels, titleblock text, family parameter
  names. Those are edited in the family itself. Grouped column headers of a schedule are not
  translated either (the API does not let them be written).
- **Save a copy of the template first.** Within the session one Ctrl+Z undoes the whole
  translation; once the file is saved, it does not.

### Move to Workset (Project panel)

Works **only in a project** (`.rvt`), and only in a workshared one. Finds elements standing in the
wrong workset and moves them into the right one — in bulk, by category.

What for: elements modelled by an AI tool or a script can land in one of Revit's own worksets — a
duct whose Properties palette reads *Workset: Параметры стадий* (by its name, a project standards
workset, where no model element belongs) — or simply in the wrong discipline's workset.
Revit has no "select everything in this workset", so finding them all means clicking through the
model element by element.

**What the button does.**

1. **The scope.** With elements selected before pressing the button, the window works on the
   selection; with nothing selected, on the whole model. The radio buttons above the table switch
   between the two.
2. **The table** has one row per **workset × category**: *"Параметры стадий — Project standards —
   Ducts — 85"*. Rows in **Revit's own worksets** (project standards, views, families) come first and
   are **checked from the start** — those are elements that ended up where none belong. On the
   whole model, rows in user worksets start unchecked; on a selection, everything starts checked.
3. **"Show"** narrows the table: "Outside user worksets" (the default when there are any), "All
   worksets", or one workset at a time. A row that leaves the table loses its check mark — only what
   is shown gets moved.
4. **"Move to workset"** — pick the workset the checked elements belong in (only user worksets are
   offered; the active one is preselected). "State" spells out what each row will do, and the line at
   the bottom adds it up.
5. **"Move"** rewrites the workset of every checked element in one operation — one Ctrl+Z.
   **"Select in model"** instead closes the window and selects the checked elements in Revit, to look
   at them first; nothing is changed.

**What is worth knowing.**

- **What is not in the table is Revit's to decide**: types, views, annotation belonging to a view,
  curtain panels and other parts of an element follow their owner. For a selection the window says
  how many were left out for that reason.
- **Elements inside model groups are not moved** — changing one instance of a group from outside
  "Edit Group" is a group edit Revit may refuse, and that would cancel the whole batch. They are
  counted in "State"; edit or ungroup the group.
- **If Revit does not let an element in its own worksets change workset at all**, the row still
  shows, with that said in "State", rather than the table pretending the model is clean.
- **The elements are checked out for you.** Ones held by another user, or changed in the central
  model since your last reload, are left out and named in the report — "Reload Latest", or ask the
  owner to synchronise and relinquish them, then run again.
- **Every element is read back after the move**, and the report counts only the ones really
  standing in the new workset.
- **Closed worksets are not looked into** — what stands in them cannot be seen; the window lists
  them. A target workset that is closed is marked in the list: elements moved into it disappear
  from view.

### Excel (Project panel)

Works **only in a project** (`.rvt`, and a template `.rte`). Exports schedules to Excel, and
brings values edited in Excel back into a schedule — the pair ModPlus calls "Export to Excel" and
"Excel to Schedule". The button first asks which way.

**Export.**

1. **Check the schedules** to export; the one open on screen is checked already. "Find" only
   finds a schedule — check marks out of sight stay.
2. **Pick the layout of the sheet:**
   - *As in Revit* — the title, the headings, group lines, subtotals and the grand total, the way
     the schedule draws them;
   - *Plain table* — one heading line and one line per schedule row, with filter buttons on the
     headings. This is the form for editing in Excel.
3. **"Export…"** asks where to save. Every checked schedule becomes a sheet of one workbook, and
   the file opens in Excel straight away (a box below the list turns that off).

**Import.**

1. **Pick the workbook** (`.xlsx` or `.xlsm`; an old `.xls` has to be saved as `.xlsx` first). It
   may stay open in Excel.
2. **Pick the sheet and the schedule** it goes into. For a sheet this button exported, the
   schedule is picked for you: the one it came from.
3. **Pick how the rows are paired:**
   - *By element (from the export)* — every line of an exported sheet remembers which elements it
     stands for, so sorting or filtering the sheet in Excel does no harm. The default for such a file;
   - *By a key column* — by a value that tells the rows apart: a mark, a room number, a sheet
     number. For a sheet that came from somewhere else;
   - *By row order* — the first line of data to the first row, the second to the second, as ModPlus
     does it. "First data row" says where the data starts; the line above it is read as the headings.
4. **Check the columns.** They are found by what they show and by their headings; the drop-down
   under each heading picks the sheet column feeding it, or "(not imported)".
5. **Look at the table.** It shows the schedule as it is, with every value the sheet changes
   highlighted: **yellow** — it will be written; **red** — it cannot be; **blue** — the key the rows
   are paired by. Hover a cell for the old value and, on a red one, the reason. "Show" narrows the
   table to the rows with changes (the default), the ones that cannot be written, or the lines that
   were not paired. A row's check box keeps it out of the import.
6. **"Import"** writes the checked rows in one operation — one Ctrl+Z. Then the schedule opens with
   the changed rows selected, so Revit highlights them (a box next to the button turns that off).

**What is worth knowing.**

- **The export writes every value exactly as the schedule shows it, as text**: "1 200", "001",
  "12,50 m²" stay as they are, whatever the language of Excel. Don't delete the hidden column A,
  the hidden first row or the hidden "VladTools" sheet if the file is to be imported back — that is
  where each line keeps its elements and each column what it shows.
- **What an import writes is what typing into the schedule in Revit would write**: an instance
  parameter goes to every element of the row (a grouped row — "Itemize every instance" off — stands
  for all of them), a type parameter to the type, and with it to every element of that type. The
  tooltip says how many other rows of the schedule show the same type.
- **A change is a change of value, not of text.** "1200" typed over "1 200 mm" is the same length
  and is left alone; a number in a cell is taken in the unit the column shows — 12.5 under "m²" is
  12.5 m².
- **What cannot be written is shown red, with the reason**: calculated columns, "Count", combined
  parameters, room / project information / material columns, read-only parameters (an area, "Family
  and Type"), a choice of another element (a type, a level, a material), a total on a grouped row,
  an element inside a model group whose parameter does not vary by group instance, and two lines
  giving one element or one type different values.
- **An empty cell leaves the value as it is.** "Empty cells clear values" makes it clear the value
  instead — text parameters only; a number cannot be emptied.
- **The import never creates or deletes elements.** A line typed into the sheet by hand has no
  element behind it and is skipped, with a note.
- **Elements are checked out for you.** Ones held by another user, or changed in the central model
  since your last reload, are left out and named in the report.
- **Every value is read back after the import**, and the report counts only the ones really there.
- **Reading a schedule takes a moment on a large one.** Revit does not say which elements stand in
  which row, so the button asks the schedule itself — through a temporary hidden column, in an
  operation that is rolled back at once. The model is not changed by it, and nothing appears in Undo.
- **Rows of elements from linked models** are exported, but cannot be written into.

### Sheets (Project panel)

Works in a project (`.rvt`, and a template `.rte`) — and, for a **batch of models**, anywhere: in a
family or with no model open at all (see "Several models" below). One table for the whole sheet set:
every sheet on the left ("A101 - Floor Plan", as the Project Browser writes it), the date of the
latest change found on it, and everything placed on it on the right, one line each.

1. **The sheets sit in the same folders and in the same order as in the Project Browser** — the
   button reads the sheet organization in use (View → User Interface → Browser Organization →
   Sheets) and the parameter it sorts by. Click a folder to fold it; "Collapse all" / "Expand all"
   fold every folder at once.
2. **"On the sheet"** lists the views by kind ("Floor plan: Level 1", "Section: S-1"…), then
   legends, schedules (a split schedule once, with its number of segments), panel schedules, images
   and CAD files. The titleblock, text notes and other annotation are not listed. A view with a
   change found in it carries the date: "Floor plan: Level 1 — changed 2026-10-07"; a view newly put
   on the sheet reads "— placed 2026-10-14".
3. **"Changed"** is the latest of those dates for the sheet — in the sheet itself or in any view on
   it. Hover over it to see between which checks the change was made and what it was in. A date
   found by the check you have just made is bold. How the dates come about is below — read it once.
4. **"Show"** narrows the table to the sheets changed since the previous check, or in the last 7 or
   30 days — open the window on a Monday, pick "Changed in the last 7 days", export, and you have
   the week's progress.
5. **"Find"** narrows the table by sheet number or name, by folder, by what is placed — type a
   view's name to see which sheet it is on — or by a date ("2026-10" finds every sheet changed in
   October 2026).
6. **"Number"** chooses what a sheet's number is (see "The sheet number" below): Revit's own Sheet
   Number, or your own parameter — `ADSK_Номер листа`, say.
7. **Double-click a sheet** (or "Open sheet") to close the window and open the sheet.
8. **"Export to Excel…"** writes the table into an .xlsx and opens it (a box below the table turns
   that off): "Number", "Sheet name", "Changed" and "On the sheet" columns — the number in a column
   of its own, so one week's file can be matched against the next one's. The folders become Excel
   row groups: the +/− buttons in the margin fold them the way the browser does. What the search and
   "Show" leave in the table is written; a folded folder is written whole.

**The sheet number.** Revit's own Sheet Number has to be unique in a model, so many offices keep the
number printed on the sheet in a shared parameter of their own. The **"Number"** button (above the
table, and "Sheet number in the reports" in the "Several models" window) lets you pick it:

- Choose **"Revit's own Sheet Number"**, or **another parameter** from the list. The list holds
  every parameter this project's sheets and titleblocks carry — shared or not, a project parameter,
  a titleblock family's own — plus a shared parameter file (the one Revit is set up with at first;
  **"Browse…"** loads any other). Next to every one you see how many sheets have it and how many give
  it a value ("on 48 of 50 sheets, 45 with a value"), and **the ones holding values are at the top** —
  that is the parameter your numbers are in. A parameter standing on the sheets with no value on any
  of them is flagged red before you press "OK".
- The value is read **from the sheet**, then **from its titleblock**, then from the **titleblock's
  type** — so it works wherever the parameter lives. A shared parameter is found by its GUID, one
  that is not shared by its name.
- The table then reads "<your number> - Name" under the heading "<parameter> - Sheet name", and the
  Excel "Number" column is headed with the parameter's name. **Revit's own Sheet Number is left out**
  — in the window and in Excel; "Find" still finds a sheet by it.
- **A sheet with no value** shows the name alone, and the window says how many there are — fill
  them in on the sheets. **A model with no sheet or titleblock having the parameter** is shown with
  Revit's own numbers, and a red line (or the batch summary) says so.
- The order stays the Project Browser's. To have the browser itself sort by your parameter, set it
  in View → User Interface → Browser Organization → Sheets.
- The choice is remembered, for the window and the batch alike. A shared parameter is kept by its
  GUID, so it is found in any project whatever its name there; one that is not shared, by its name.

**How the change dates come about.** Revit keeps no date of a change — not for a view, not for any
element — so the button keeps its own record. **Every opening of the window is a check**: the button
compares every sheet and every view on a sheet with what the previous check saw, and dates what
differs with today. So:

- **A date is the day the change was found, not the day it was made.** It was made between the
  previous check and that day (the tooltip says which). Open the window once a week, and the dates
  read as weeks.
- **There is no history before the first check.** The first opening on a model only remembers how
  everything looks; dates start with the second.
- **What counts as a change of a view**: the view itself (crop, scale, visibility/graphics, its
  template) and everything drawn in it — dimensions, tags, text, detail lines and components,
  filled regions, revision clouds. For a sheet: its viewports, titleblock, text and parameters, and
  views put on it or taken off. **Model elements are not watched directly** — a moved wall shows
  only where it moves a dimension or changes a tag on the view; watching the model itself would
  take minutes on a large project and mark every plan a wall is seen in.
- **Only saved or synchronised changes are seen** — Revit records a change once the model is saved
  or synchronised. A colleague's changes show after they synchronise and you reload the latest.
- **The record is kept on this computer** (`%AppData%\VladTools\sheets\history\`), one file per
  model — every local copy of the same central model shares it. Whoever wants weekly progress opens
  the window on their own computer; a colleague's record is their own.
- Opening the window writes only that file — **nothing in the model changes**.

**What is worth knowing.**

- **Sheets the Project Browser does not show are listed too**, in folders of their own at the end:
  the ones its filter hides, and placeholder sheets (they exist only in sheet lists — there is
  nothing to open).
- **The folders themselves are in alphabetical order** (A2 before A10): Revit says which folders a
  sheet sits in, but not the order the browser lists the folders in — in practice it is the same.
- A sheet whose grouping parameter is empty sits under "???", as in the browser.
- **Nothing in the model is changed**: the button only reads.

#### Several models — a report for each, and a summary of them all

**"Several models…"** at the bottom of the window (or straight away, if you press "Sheets" with no
model open, in a family, or in a project with no sheets) opens a list of models to report on. Each
checked model is read in turn, and you get:

- **a report per model** — exactly the file "Export to Excel…" writes for an open model: the
  browser's folders as Excel groups, "Number" (headed with your parameter's name when it is the
  number — see "The sheet number" above), "Sheet name", "Changed", "On the sheet";
- **one summary** with two sheets:
  - **"Models"** — a line per model: whether it was read (and if not, why), its sheet organization,
    how many sheets, hidden by the browser's filter, placeholders, sheets with nothing placed,
    views, legends, schedules, images and CAD, sheets changed since the previous check, sheet
    numbers that also occur in another model of the batch — and a **link to the model's own
    report**. A totals line closes the table.
  - **"All sheets"** — every sheet of every model in one table with filter buttons: model, number,
    name, browser folder, "Changed", what is on it, and **"Notes"** (highlighted): nothing placed,
    placeholder, hidden by the browser's filter, "Number also in …", and "No … value" for a sheet
    your number parameter is empty on. One filter answers a question
    about the whole set — which sheets are empty, where A101 occurs, what changed this week.

**How the models are gathered** — the same list as in "Accept Changes → Other models…":

- **"Folder…"** — every model in a folder, on disk or the network, on Revit Server or in BIM360.
  If the folder has folders inside, you are asked whether to take their models too (every level
  down). Revit's own backups (`Model.0001.rvt`, the `_backup` folders) are left out. On disk, open
  the folder in the dialog and press "Open" with "Choose this folder" left in the name box — so a
  project root with only discipline folders in it can be chosen too.
- "Files…", "Revit Server…", "BIM360…", "BIM360 by GUID…" — single models, as in "Link Manager".
- **Saved sets** — "Load set" / "Save set", shared with "Link Manager" and "Accept Changes". The
  list the window was closed with comes back next time.

**"Write reports…"** asks where to save the summary (named `Sheets summary <date>.xlsx` by
default); the models' reports go into a folder of the same name next to it. One run is one file and
one folder, so runs on different days never overwrite each other. When the batch is done, a report
says what was read and what was not, with buttons to open the summary or the folder.

Worth knowing:

- **Nothing in any model is changed, and nothing is locked.** A model is opened **detached** from
  its central file — no local copy, no lock, nothing to synchronise — and closed without saving.
- **Every workset is closed** when a model is opened: sheets, views and everything placed on sheets
  are always loaded anyway, while the geometry and linked models are not. That is why a model takes
  seconds rather than minutes.
- **A model already open in Revit** — the project you are in, or a link loaded into it — is read as
  it stands (unsaved changes included) and left open.
- **Change dates.** Every report shows the dates from the same record the model's own window keeps.
  A model the batch opens as itself — one already open in Revit, a BIM360 model, a file that is not
  workshared — is also **checked** on the way, exactly as opening its window would. A model opened
  detached is **not** checked, only read: it is not yet confirmed that a detached copy keeps the
  versions Revit marks changes by, and if it did not, the batch and the window would keep marking
  everything "changed" in turn. The summary says so for such a model ("not checked (opened detached);
  dates as of …") — to bring its dates up to date, open its window.
- **Revit is busy until the whole batch is done**, with no progress bar (the Revit API is
  single-threaded). A model that cannot be opened — no access, a newer Revit version, a stale path
  in a saved set — is a line in the summary and the report; the rest carry on.
- **"Number also in another model"** is information, not an error: worth acting on where one
  building is split into discipline models and numbers must be unique across them; a project that
  numbers every model from 1 will see it on every sheet.
- The settings live in `%AppData%\VladTools\sheets\_settings.txt`.

### Views to Sheets (Project panel)

Works **only in a project** (`.rvt`, and a template `.rte`). Puts a whole batch of views on sheets at
once: **one checked view — one new sheet**, named after the view, at the scale chosen, with the view
centred in the titleblock's drawing area — or put exactly where a view placed by hand stands on a
sample sheet, which also re-positions views already on sheets.

1. **The views are listed the way the Project Browser lists them** — the view organization in use
   (Discipline, Sub-Discipline…) and, under it, the view-type groups ("Floor Plans", "Sections
   (Building Section)"…). A folder's check box takes every view in it; click a folder's name to fold
   it. "Find" narrows the list by name, kind, folder or sheet — and a view it hides **keeps its check
   mark**, so find one lot, tick it, then find the next; the line under the table says how many
   checked views are out of sight. Views that cannot be checked are hidden; "Show the views that
   cannot be checked" lists them too, without a check box — empty views, the sample itself, and views
   already on a sheet (a view stands on one sheet only, so outside "Like a sample sheet" it cannot be
   placed again; a legend is the exception and stays checkable).
2. **Scale** — every view has its scale in a cell of its own: pick one from the list or type `1:250`
   (or simply `250`). "Scale for the checked views … Set for checked" writes one scale into every
   checked view at once; "Each view's own" puts them back. "On paper", the fit check and the preview
   follow the new scale. A view whose **view template sets the scale** keeps it — the cell is locked
   and "State" names the template. **To change the scale of views already on sheets, or of templated
   views, use [Sheet Scale](#sheet-scale-project-panel)** — this window only scales the views it
   places or moves. A **dependent view** shares its scale with
   its primary and the other dependents — "State" says so, and giving two of them different scales is
   refused. The scale is changed on the view itself, so it shows wherever the view is used; it is part
   of the same operation and comes back with the same Ctrl+Z.
3. **Titleblock** — the type every new sheet gets. Its size is not taken from the family's parameters
   but measured: the button makes a sheet of every type and throws it away again, so the size shown is
   what the titleblock actually draws.
4. **Margins, mm** — from the titleblock's outer edge to the drawing area: take the title panel or the
   stamp into the margin on its side (a panel along the right edge — a large right margin). The
   margins are remembered **for every titleblock type separately**. The preview on the right shows the
   sheet, the drawing area (dashed) and the highlighted view's size to scale.
5. **Sheet numbers** — a prefix and a start number: `A-` and `101` give A-101, A-102… in the order of
   the list. Leading zeros are kept (`001`). A number the project already has is skipped, and the
   "Sheet" column shows each view's sheet before anything is made.
6. **Position by** — four ways, the window asks rather than picks:

   | Option | What goes where | For |
   |---|---|---|
   | **The whole viewport, its title included** | everything the sheet shows of the view — the crop region, annotation sticking out of it, the view title — in the middle of the drawing area | sections, details, 3D, one-off views |
   | **The crop region** | the view's crop region in the middle, whatever annotation sticks out of it | plans sharing a crop region (a scope box) land in the same place on every sheet |
   | **One model point in the same place on every sheet** | the same point of the model — the middle of all the checked views' crop regions together — in the middle | a stack of floor plans that must not jump as the sheets are flipped, even where their crop regions differ |
   | **Like a sample sheet** | wherever the sample's view stands: a view looking the same way as the sample puts the same point of the model in the same place on its sheet; any other view puts the middle of its viewport where the sample's middle is | place one view by hand, move it where it belongs, and repeat that for the rest |

   A view the chosen way cannot position — no crop region (drafting views, legends, crop off), a
   perspective, a split crop region — goes by its viewport instead, and both the "State" column and
   the report say so.
7. **"Place"** makes the sheets and positions the views — one operation, one Ctrl+Z.

**Like a sample sheet.** Move one view by hand to where it belongs on its sheet, then press the button
with that viewport selected, or with that sheet open — the window offers it as the **Sample** on its
own (any other viewport can be picked from the list). In this mode **views already on sheets can be
checked too**: they stay on their sheets and are moved there ("Sheet" reads "stays on A102 - …"), so
a set of floor plans placed earlier can be lined up with the one adjusted by hand. New sheets made in
the same run are positioned the same way. The sample itself is never moved. The position is copied
from the sheet's origin as it is — if the sample stands on a sheet of another titleblock type than
the new sheets get, the window says so. A pinned viewport is unpinned for the move and pinned again.

The report lists the sheets and, separately, every one that needs a look:

- **sticking out of the drawing area** — the view is larger than it at this scale (the "On paper"
  column warns before placing: it is estimated from the crop region and the scale); a moved view is
  checked against its own sheet's titleblock and the margins remembered for that titleblock type;
- **positioned by the viewport** rather than the way chosen, and why;
- **scale not changed** — Revit refused the new scale, with its reason;
- **not where it was asked to go** — every viewport is read back after the operation; for the crop
  region and the model point, the crop region is measured again after the move;
- **could not be placed** — Revit refused the view (it already stands on a sheet, or shows nothing),
  or, in a workshared model, another user holds it or it was changed in the central model since the
  last reload. The sheet made for a refused view is deleted again rather than left behind empty.

**What is worth knowing.**

- The size in "On paper" is the crop region over the scale. Annotation outside the crop region
  (with "Annotation Crop" off) can make the viewport larger — the report checks the real outline.
- **Measuring the crop region.** Revit 2022 has no API that says where a model point lands on a sheet,
  so for the crop region, the model point and the sample the button shows the crop boundary and hides
  the annotation for a moment, inside the same operation, reads the viewport's frame, and puts
  everything back — the views come out of it exactly as they went in. If something besides the crop
  region stays on the sheet, that view goes by its viewport instead, and the report says why.
- **In a workshared model** the views (and the viewports to be moved) are checked out before anything
  is changed; a view held by another user is named in the report and left out, rather than failing
  the whole operation at the end.
- A new viewport is never rotated; the viewport type is Revit's default.
- Schedules are not in the list: they go on a sheet differently, and a sheet of its own per schedule
  is not what this button is for.

### Sheet Scale (Project panel)

Works **only in a project** (`.rvt`, and a template `.rte`). Gives the views standing on the chosen
sheets a new scale in one go — for example every 1:100 plan on thirty sheets to 1:200 — **including
views whose view template sets the scale**, which Revit's own Properties palette refuses to change.
Every view stays where it stands on its sheet.

1. **Select the sheets in the Project Browser** (Ctrl/Shift+click), then press the button. Every view
   on them starts checked, legends excepted. Viewports selected on a sheet, or views selected in the
   browser, start checked too; with nothing selected, the sheet that is open.
2. **The window** lists every sheet that holds a view, in the folders and the order of the Project
   Browser, and under each sheet the views on it with their scale. A sheet's check box takes every
   view on it, a folder's every view in it; click a sheet or a folder to fold it. Sheets with nothing
   checked start folded. "Find" finds sheets, views and folders — a view it hides keeps its check mark,
   and the line under the table says how many checked views are out of sight.
3. **"Change the checked views at … to …"** — the first list narrows the change to one scale: with
   "1:100" chosen, a 1:20 detail on the same sheet cannot be checked and is not swept along (a view
   that stops being checkable loses its check mark). The second is the new scale: pick it or type
   `1:250` (or simply `250`).
4. **Views whose view template sets the scale** — a template with "View Scale" ticked in its
   "Include" column decides the scale, and the view's own cannot be changed past it. Three answers:

   | Option | What happens |
   |---|---|
   | **Stop the template setting the scale** (default) | "View Scale" is unticked in the template. Every view using it keeps the scale it has; the checked ones get the new one. Everything else the template sets stays as it is. |
   | **Give the template the new scale** | The template itself gets the new scale, and every view using it follows — on sheets that are not checked and on no sheet as well. Right when the template is meant for one scale only. |
   | **Leave such views as they are** | They cannot be checked; change the template by hand. |

5. **"State"** says, for every view, what will happen: the template that stops setting the scale (and
   how many other views use it), the views changing along with it, "already 1:200", or a warning
   that the view **may stick out of the drawing area** at the new scale (estimated from the viewport's
   size; the drawing area is the titleblock less the margins kept for its type in "Views to Sheets").
   A sheet's line repeats a warning of its views, so a folded sheet does not hide it.
6. **"Change scale"** — one operation, one Ctrl+Z. When the run reaches beyond the checked views (a
   view template is edited, views on other sheets change along, checked views are out of sight, a
   view may stick out) the button asks first and lists what is involved.

**Changing along.** A **primary view and its dependents** share one scale: checking one changes them
all, wherever they stand — "State" says "changes too" on the others. A **legend** standing on several
sheets has one scale, so its check box is shared by all its lines and it changes on every sheet.

**The view stays in place.** After the change the middle of every viewport of every changed view is
put back exactly where it stood (a pinned viewport is unpinned for the move and pinned again).

The report lists the views changed, with their old and new scale, and separately:

- **changed along with them** — dependents, primaries, views of a template given the new scale;
- **view templates** that no longer set the scale, or that got the new one;
- **scale not changed** — every view is read back after the operation; Revit's refusal is quoted;
- **changed although nobody checked it** — with "Stop the template setting the scale", the template's
  other views are read back too: they are meant to keep their scale, and one that did not is listed;
- **sticking out of the drawing area** — the real outline, title included, checked on every sheet;
- **not where they stood** — every viewport is read back;
- **could not be changed** — the view was deleted in the meantime, its template sets the scale and
  "Leave" was chosen, or, in a workshared model, another user holds the view, its viewport or its
  template, or it was changed in the central model since the last reload.

**What is worth knowing.**

- **In a workshared model** the views, their viewports and the templates to be edited are checked
  out before anything is changed; a view whose elements somebody else holds is named and left out,
  rather than failing the whole operation at the end.
- A perspective 3D view has no scale and is listed without a check box. Schedules are not listed:
  they have no scale.
- The scale is changed on the view itself, so it shows wherever the view is used.

### Copy Parameters (Project panel)

Works **only in a project** (`.rvt`, and a template `.rte`). Writes the value of one parameter into
another on every element of a category — **even when the two hold different kinds of value**: a
number into a text, a text into a number, a family's reporting parameter into a shared one.

1. **The scope** — the elements selected before pressing the button, the elements of the active view
   (on a schedule — the schedule's own elements), or the whole model.
2. **The category** — every category with elements in the scope, with how many (the categories Revit's
   own "Project Parameters" dialog offers; types themselves are not listed — a type parameter is
   reached through its elements).
3. **Copy from** (left) and **Copy into** (right) — the category's parameters: instance and type ones,
   built-in, shared, project and family ones, each with the kind of value it holds ("Text", "Length",
   "Yes/No"…). "Find" finds one by a part of its name; "⇄" swaps the two. Read-only parameters —
   reporting ones, ones Revit computes (area, volume), ones a formula sets — can be copied **from**; on the
   right they are hidden unless "Show read-only parameters too" is on, and greyed.
4. **How** — four options:
   - *Only where the target is empty* — fill gaps, never overwrite;
   - *An empty source empties a text target* — off by default: an empty source leaves the target alone;
   - *Numbers into text without the unit symbol* — "1200" rather than "1200 mm";
   - *A text that is not a number gives the first number in it* — "DN 50" → 50 (check the preview:
     "Floor 2 - 1200" gives 2).
5. **The preview** — every element (or every type, for a type parameter): the source, the target now
   and the target after, and the state — *will be written* (green), *already holds this value*, *left as
   it is* (empty source, target already filled, no such parameter) or *cannot be written* (red, with the
   reason). The line above the preview states the conversion rule for the chosen pair. "Show" narrows the
   table only — "Copy" writes every line that will be written.
6. **"Copy"** — everything in one operation, one Ctrl+Z.

**How a value changes kind on the way.** The rule is always "what a person would have typed":

| From → into | What is written |
|---|---|
| anything → Text | the value exactly as Revit shows it: the project's units and rounding ("1 200 mm"), Yes/No as Revit's own word, an element choice (a level, a material) by its name |
| Text → a number (length, area, plain number…) | read the way Revit reads a value typed into Properties — "1200", "1200 mm", "1.2 m"; a text that is not a number is not written (unless the "first number" option is on) |
| a number → a number of the same kind | copied exactly |
| a number → a number of another kind | the figure the source shows, taken in the target's units: a length showing 1200 gives a plain number 1200 |
| a number → a whole number | the figure shown, rounded (the line says so) |
| anything → Yes/No | zero, "No", "Нет", "0" give No; any other number, "Yes", "Да", "1" give Yes |
| an element choice → an element choice | the same element, where Revit accepts it |
| anything else → an element choice | not written: a name says nothing about which kind of element the target chooses |

**Type parameters.** A type parameter as the target is written per type, and every element of that
type changes with it — wherever it stands, not only in the scope; the button asks before copying. When
its value comes from an instance parameter, the elements of the type in the scope must agree: a type
that would get two different values is listed as "cannot be written" with both values. A type
parameter as the source gives every element its own type's value.

**What is worth knowing.**

- **Elements inside model groups** are not written when the target does not vary by group instance —
  Revit would refuse the whole operation; they are listed with the reason. Change them with "Edit Group".
- **In a workshared model** the elements (or types) are checked out first; ones held by another user, or
  changed in the central model since the last reload, are counted in the report and left out.
- **Every value is read back** after the copy, and the report counts only what the model really holds.
- The category, the two parameters and the options are remembered for the next time.

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
    ScheduleLibraryCommand.cs         logic for the "Schedule Library" button (project)
    ParameterSetCommand.cs            logic for the "Parameter Sets" button (project)
    WorksetsCommand.cs                logic for the "Worksets" button (project)
    TranslateCommand.cs               logic for the "Translate" button (project)
    MoveToWorksetCommand.cs           logic for the "Move to Workset" button (project)
    ExcelScheduleCommand.cs           logic for the "Excel" button (project): export and import
    SheetsCommand.cs                  logic for the "Sheets" button (project)
    ViewsToSheetsCommand.cs           logic for the "Views to Sheets" button (project)
    SheetScaleCommand.cs              logic for the "Sheet Scale" button (project)
    CopyParameterValuesCommand.cs     logic for the "Copy Parameters" button (project)
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
    ModelPicker.cs                   picking a model from a file, Revit Server, BIM360 or a pair of GUIDs — or a folder
    BaseFileWindow.cs                the "Base File" window: a model and a list of what to do with it
    AutoDimensionWindow.cs           the "Auto Dimensions" window: the chain table, boundary, direction, templates
    DimensionChainRow.cs             a row of that table (chain kind, offset, dimension type)
    DimensionChainKind.cs            the catalogue of chain kinds (Overall/Openings/Centres/Partitions/…)
    DimensionTypeInfo.cs             a snapshot of a dimension type for this window
    AcceptCoordinationWindow.cs      the "Accept Changes" window: choosing a link and a difference table
    CoordinationChangeRow.cs         a row of that table (check box, what differs, before → after)
    CoordinationChangeKind.cs        the kinds of difference (Position/Name/Missing/New/Cannot be applied)
    CoordinationScan.cs              the result of comparing against one link
    BatchCoordinationWindow.cs       "Other models…": the model list, the worksets to open, what may be changed
    ModelListPanel.cs                a batch's model list: Folder…, Files…, Revit Server…, BIM360…, sets, the table —
                                     shared by "Other models…" and "Sheets → Several models…"
    BatchModelRow.cs                 a row of that table (check box, model, source, where it lives)
    ScheduleLibraryWindow.cs         the "Schedule Library" window: the set, where to fill it from, the insert table
    ScheduleChooserWindow.cs         "which of this model's schedules go into the set"
    ScheduleRow.cs                   a row of those tables (check box, schedule, clash action, state)
    ScheduleInfo.cs                  a snapshot of one schedule for the window
    ScheduleAction.cs                what to do when the name is taken (Skip/Replace/Insert as a copy)
    ScheduleSetScan.cs               the result of reading or filling a set
    ParameterSetWindow.cs            the "Parameter Sets" window: the set, "Add from shared file…", the apply table
    ParameterSetRow.cs               a row of that table (check box, binding, categories, group, state)
    CategoryPickerWindow.cs          "Choose categories…" — a small checklist opened from a row
    CategoryInfo.cs                  a snapshot of one bindable category for that picker
    SharedParameterPickerWindow.cs   "Add from shared file…" — picking definitions out of a shared parameter file
    SharedParameterInfo.cs           a snapshot of one definition read out of a shared parameter file
    WorksetsWindow.cs                the "Worksets" window: the project's worksets and the question about their contents
    ProjectWorksetRow.cs             a row of that table (check box, contents, state)
    WorksetInfo.cs                   a snapshot of one user workset: GUID, name, contents, owner, open/active
                                     (also the target list of "Move to Workset")
    WorksetElementAction.cs          what happens to the elements in a removed workset (move or delete)
    ParameterGroupInfo.cs            a snapshot of one parameter group, for the "Group" column
    ParameterStatusInfo.cs           what the command found comparing a row against the open project
    TranslateWindow.cs               the "Translate" window: one row per Russian text, Export…/Import…, the clash check
    TranslationRow.cs                a row of that table (check box, original, translation, state)
    TranslationText.cs               a snapshot of one distinct text and where it is used
    NameScope.cs                     a set of names Revit keeps unique, for the clash check
    MoveToWorksetWindow.cs           the "Move to Workset" window: the scope, the workset × category table, the target
    WorksetCategoryRow.cs            a row of that table (check box, workset, kind, category, state)
    WorksetCategoryInfo.cs           a snapshot of one category in one workset: movable, in groups, locked
    WorksetCategoryScan.cs           the result of reading a scope (the selection or the whole model)
    ExcelExportWindow.cs             the "Export to Excel" window: the schedules, the layout of the sheet
    ExcelScheduleRow.cs              a row of that list (check box + schedule)
    ExcelImportWindow.cs             the "Import from Excel" window: sheet → schedule, pairing, the highlighted preview
    ExcelImportPreview.cs            what that window shows: the request, columns, rows and cells with their state
    SheetsWindow.cs                  the "Sheets" window: the sheets in the Project Browser's folders, Find, Export to Excel
    SheetsBatchWindow.cs             "Sheets → Several models…": the model list and where the summary goes
    SheetNumberWindow.cs             "Sheets → Number": Revit's own Sheet Number or a shared parameter from a file
    SheetNumberParameter.cs          which parameter is a sheet's number, and how one model's numbers came out
    SheetListRow.cs                  a line of that table: a folder or a sheet
    SheetInfo.cs                     a snapshot of one sheet: number, name, its folders, what is placed on it, its change date
    FlatTree.cs                      a tree laid flat into table rows — shared by "Sheets" and "Views to Sheets"
    ViewsToSheetsWindow.cs           the "Views to Sheets" window: the views in the browser's folders, scales, titleblock,
                                     margins, numbering, positioning, the sample, the sheet preview
    ViewPlacementRow.cs              a line of that table: a folder or a view, its scale, its sheet and state
    PlaceableViewInfo.cs             a snapshot of one view: folders, scale and what locks it, size on paper, where it stands
    TitleblockInfo.cs                a snapshot of one titleblock type and the frame it draws, measured
    ViewCentering.cs                 the four ways of positioning a view, with their captions
    ViewPlacementRequest.cs          what the window hands back: the titleblock, the drawing area, the views, numbers, scales
    SampleViewportInfo.cs            a snapshot of one viewport on a sheet — a possible sample to position like
    SheetScaleWindow.cs              the "Sheet Scale" window: the sheets and their views, the scale, what to do with templates
    SheetScaleRow.cs                 a line of that table: a folder, a sheet or a view, its new scale and state
    ScaleSheetInfo.cs                snapshots of one sheet, one viewport on it and one view: scale, template, dependents
    ScaleTemplateMode.cs             what to do with a view template that sets the scale, with captions
    SheetScaleRequest.cs             what the window hands back: the scale, the template choice, the views
    CopyParametersWindow.cs          the "Copy Parameters" window: scope, category, the two parameter lists, the preview
    CopyParameterInfo.cs             snapshots for that window: a category, a parameter; the scopes
    CopyParameterPreview.cs          what that window asks for and what the preview shows, line by line
  Infrastructure/
    Ribbon.cs                    creating the panel and the buttons
    AlwaysAvailable.cs           lets a button be pressed with no model open ("Sheets", for its batch)
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
    BatchCoordination.cs         opening somebody else's model with two worksets, and synchronising it back;
                                 or only to read it — detached, every workset closed ("Sheets" batch)
    CoordinationPreferences.cs   the "Other models…" window settings (%AppData%)
    ScheduleLibrary.cs           the schedule cache: sets as .rvt files (%AppData%), copying between documents
    SchedulePreferences.cs       the "Schedule Library" window settings (%AppData%)
    ParameterSetLibrary.cs       saved parameter sets (%AppData%)
    ParameterSetPreferences.cs   the "Parameter Sets" window settings (%AppData%)
    SharedParameterFile.cs       reading any shared parameter file — "Parameter Sets" and the "Sheets" number
    TextSite.cs                  one place holding typed text (a name, a parameter value, a filter rule…): read and write it
    TextSiteCollector.cs         finds every place with Cyrillic text in the open project
    TranslationDictionary.cs     the translation dictionary and the export/import file format (%AppData%)
    TranslatePreferences.cs      the "Translate" window settings (%AppData%)
    Xlsx.cs                      writing and reading .xlsx files (no third-party libraries)
    ScheduleTable.cs             a schedule's columns and rows, and which elements stand in each row
    ScheduleWorkbook.cs          how a schedule sits on an Excel sheet, both ways
    ScheduleImport.cs            comparing a sheet with a schedule: what would change, and why not
    ExcelPreferences.cs          the "Excel" button settings (%AppData%)
    SheetPreferences.cs          the "Sheets" window settings, the number parameter and the batch's model list (%AppData%)
    SheetNumbers.cs              the number "Sheets" shows: Revit's own, or a shared parameter off the sheet or its titleblock
    SheetReport.cs               the "Sheets" batch summary: the "Models" and "All sheets" Excel sheets
    SheetHistory.cs              the "Sheets" change history: when a change was found on each sheet and view (%AppData%)
    ViewFingerprint.cs           a digest of a view and everything it owns — what the change history compares
    ProjectBrowser.cs            the Project Browser's folders, filter, sort value and natural order — shared
    ViewsToSheetsPreferences.cs  the "Views to Sheets" window settings, margins per titleblock (%AppData%)
    ViewScale.cs                 who decides a view's scale: a template that controls it, a primary view — shared
    SheetScalePreferences.cs     the "Sheet Scale" window settings (%AppData%)
    ParameterValueConverter.cs   a parameter's identity across families, and a value converted between kinds (text, number, yes/no…)
    CopyParameterPreferences.cs  the "Copy Parameters" window settings (%AppData%)
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
