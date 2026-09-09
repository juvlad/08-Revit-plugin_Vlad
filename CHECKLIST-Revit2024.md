# Checklist: supporting Revit 2022 + Revit 2024

A task for the executor (the Sonnet model). Goal: one codebase builds and works on both
**Revit 2022** and **Revit 2024**.

Everything written here has **been verified against the real `RevitAPI.dll` builds** 22.0.0.0 and
24.3.40.0, installed on this machine (reflection over both assemblies + a trial compile). Do not
re-ask or re-verify the facts in the "Already established" section — they came from measurement,
not a guess. What needs checking is **your own edits**, not these inputs.

---

## Already established (given facts, no need to verify)

### The main point

**The current code compiles against the Revit 2024 API without a single error.**
A trial build with `dotnet build -p:RevitVersion=2024` gives: **0 errors, 13 CS0618 warnings**
(deprecated APIs). So the move is not a rewrite, just a careful cleanup.

### What is the same and needs no touching

| Fact | Value |
| --- | --- |
| Revit 2024's .NET | the same `net48` (`supportedRuntime v4.0` in `Revit.exe.config` for both versions). **No need to change the TFM** |
| `RevitAPI.dll` | **not strong-named** in either 2022 or 2024 |
| `.addin` format | identical; `AddInId` can keep the same GUID — the add-ins folders differ by version |
| The ribbon (`UIControlledApplication`, `RibbonPanel`, `PushButtonData`, `TaskDialog`, `MainWindowHandle`) | unchanged |
| The `ImportPlacement`, `WorksetConfigurationOption`, `ViewDetailLevel` enums | the same members |
| `DisplayStyle` | 2024 adds a `Textures` value; `Realistic` is still there — no impact |

Checked by name and **present in both versions, not deprecated** (need no edits):
`Category.GetCategory`, `Parameter.Set(int)`, `RevitLinkType.Create`/`LoadFrom`,
`RevitLinkInstance.Create`, `RevitLinkOptions`, `WorksharingUtils.GetUserWorksetInfo`,
`ModelPathUtils.ConvertCloudGUIDsToCloudPath(string, Guid, Guid)`, `Dimension.FamilyLabel`,
`Element.VersionGuid`, `Element.GetDependentElements`, `Group.UngroupMembers`,
`ViewSchedule.IsTitleblockRevisionSchedule`, `ViewSchedule.IsInternalKeynoteSchedule`,
`View.HideElements`, `View3D.SetCategoryHidden`, `FamilyManager.SetFormula`,
`FamilyManager.RemoveParameter`, `SharedParameterElement.GuidValue`,
`WorksetTable.GetActiveWorksetId`, `ControlledApplication.VersionNumber`.

### What is deprecated in 2024 (13 CS0618 warnings)

| API | Where | Replacement that exists **in both 2022 and 2024** |
| --- | --- | --- |
| `ElementId.IntegerValue` | `CleanupCommand.cs:615,619,628,632`; `RenameNestedFamiliesCommand.cs:140 (×2),146` | no direct one — worked around instead (see steps 3.1–3.2) |
| `Definition.ParameterGroup` | `DeleteProjectParametersCommand.cs:204,208`; `DeleteSharedParametersCommand.cs:155,159` | `Definition.GetGroupTypeId()` — **present in 2022** |
| `LabelUtils.GetLabelFor(BuiltInParameterGroup)` | `DeleteProjectParametersCommand.cs:204`; `DeleteSharedParametersCommand.cs:155` | `LabelUtils.GetLabelForGroup(ForgeTypeId)` — **present in 2022** |

### What exists in only one version

| API | 2022 | 2024 | Conclusion |
| --- | --- | --- | --- |
| `ElementId.Value` | no | yes | **must not be used** |
| `ElementId(long)` | no | yes | must not be used |
| `Document.GetAllUnusedElements` / `GetUnusedElements` | no | yes | must not be used (see block 4) |

Checked separately: `ElementId.ToString()` is overridden in both versions and returns the bare
number — `new ElementId(12345).ToString() == "12345"`. This is the basis for step 3.2.

### The one genuine runtime bug

`Infrastructure/RevitServerClient.cs:52` — `private const string ServiceVersion = "2022";`
is inserted into the URL (`RevitServerClient.cs:83`):
`http://<server>/RevitServerAdminRESTService2022/AdminRESTService.svc/...`
On a Revit Server 2024 that address does not exist — the service answers **404**, and the "Revit
Server…" button in "Link Manager" simply shows no folders. The compiler says nothing about this.

### SSONET (Autodesk sign-in for BIM360)

The makeup of `Autodesk.Revit.AdWebServicesBase` changed between versions: in 2024,
`GetTokenExpiryDate`, `IsTokenExpired`, `RefreshToken`, `GetLoginCookie`, `GetServiceEntitlement`
**are gone**. But `AutodeskSession` only calls `GetInstance`, `IsLoggedIn`, `GetLoginUserName`,
`IsOAuth2TokenExpired`, `GetOAuth2AccessToken`, `RefreshOAuth2Token` — **all six exist in both**.
No edit is needed; do not replace the reflection with direct references (see block 4).

---

## Strategy

**Two DLLs from one source tree — one per year.** Not a single shared DLL.

Why not one: `RevitAPI.dll` is unsigned, so a DLL built for 2022 will technically load in 2024 too
— but that is undocumented behaviour, and it will stop working the moment even one API with a
changed signature shows up in the code. Building separately for each year gives proof from the
compiler, not hope.

**At the same time, there must be no `#if` in the code.** A replacement exists in both versions for
every deprecated API, so the code stays exactly the same — only the `RevitAPI.dll` references and
the install folder differ. A `REVIT2024` directive is set up in the csproj **as a spare**, but it
is not used even once in this task. If it seems like `#if` is needed, re-read the replacement table
above first — most likely a 2024-only API was picked where a shared one exists.

---

## Block 1. Building for two years

### 1.1 `src/VladTools/VladTools.csproj` — make `RevitVersion` overridable

Right now: `<RevitVersion>2022</RevitVersion>` — hard-coded.
Needed: a default value that is overridden by the `-p:RevitVersion=2024` switch.

```xml
<RevitVersion Condition="'$(RevitVersion)' == ''">2022</RevitVersion>
```

> Formally, `-p:` already overrides a property from the file (an MSBuild global property), but the
> `Condition` makes the intent explicit and survives moving the property into
> `Directory.Build.props`.

### 1.2 Split the output folders by year — **mandatory**

Right now, `AppendTargetFrameworkToOutputPath=false`, and both builds write into a single
`bin\Release` + `obj\Release`. Building 2024 after 2022, you would **silently put the 2024 DLL
into Revit 2022's add-ins folder** — Revit would either crash on startup or never show the tab.
This is the most expensive mistake in the task; it has already been reproduced while preparing
this checklist.

Add to the same `PropertyGroup`:

```xml
<BaseOutputPath>bin\R$(RevitVersion)\</BaseOutputPath>
<BaseIntermediateOutputPath>obj\R$(RevitVersion)\</BaseIntermediateOutputPath>
```

> `BaseIntermediateOutputPath` has to sit **before** the SDK import, otherwise `restore` will not
> see it. In SDK-style projects that means either `Directory.Build.props`, or setting it via `-p:`
> from `build.ps1`.
> Check that after building both versions, both `bin\R2022\Release\VladTools.dll` and
> `bin\R2024\Release\VladTools.dll` exist, and that they are **different** files (compare
> size/hash). If splitting `obj` in the csproj does not work out, do not improvise: split only
> `bin`, and clear the intermediate folder between years in `build.ps1`.

### 1.3 Product name — by year

```xml
<Product>VladTools for Revit $(RevitVersion)</Product>
```

### 1.4 A compile constant, as a spare

```xml
<DefineConstants>$(DefineConstants);REVIT$(RevitVersion)</DefineConstants>
```

**Not used** in this task. It is set up so the next move (2025+, where `net48` no longer fits)
does not have to start by editing the csproj.

### 1.5 Check that Revit is present before building

If the `C:\Program Files\Autodesk\Revit $(RevitVersion)` folder does not exist, MSBuild produces a
vague "metadata file not found". Add a clear error instead:

```xml
<Target Name="CheckRevitDir" BeforeTargets="BeforeBuild">
  <Error Condition="!Exists('$(RevitDir)\RevitAPI.dll')"
         Text="Revit $(RevitVersion) was not found at $(RevitDir). Build for an installed version: -p:RevitVersion=2022" />
</Target>
```

### 1.6 `build.ps1` — build and install both years

- Add a `[string[]]$RevitVersion = @('2022','2024')` parameter.
- Loop over the years; for each — `dotnet build $project -c $Configuration -p:DeployToRevit=$deploy -p:RevitVersion=$v`.
- A year with no Revit installed should be **skipped with a warning**, not fail the whole build:
  check `Test-Path "C:\Program Files\Autodesk\Revit $v"`.
- The final message — per year: "Restart Revit 2022 / Revit 2024".
- Keep the warning about a running Revit (Revit holds `VladTools.dll` locked).
- Keep the existing call patterns working: `.\build.ps1`, `-Configuration Debug`, `-NoDeploy`
  should still work as before. Add `.\build.ps1 -RevitVersion 2024` for a single year.

### 1.7 Installing the `.addin`

`RevitAddinsDir` is already built from `$(RevitVersion)` — confirm that after the edits the
`DeployToRevitAddins` target puts the files into `%AppData%\Autodesk\Revit\Addins\2022\` and
`...\2024\` respectively, and that `VladTools.addin` is copied into **both**. The manifest file
itself is unchanged and remains a single one per repository.

---

## Block 2. Fixing Revit Server (the only real breakage)

### 2.1 `Infrastructure/RevitServerClient.cs`

Turn `ServiceVersion` from a `const` into a mutable static property with a default of `"2022"`
(so that the un-initialised behaviour does not change):

```csharp
/// <summary>
/// The Revit Server REST service version: its name includes the year (RevitServerAdminRESTService2024),
/// and the server answers 404 for the wrong year. App.OnStartup sets the value from the
/// version of the running Revit — what is here is only a fallback, in case the add-in did not start.
/// </summary>
public static string ServiceVersion { get; set; } = "2022";
```

Leave line 83 (building the URL) untouched — it will pick up the new value on its own.

### 2.2 `App.cs` — set the version at startup

At the start of `OnStartup`, before creating the panels:

```csharp
// The Revit Server REST service name includes the Revit year; we take it from Revit itself,
// so the same build does not miss the service.
RevitServerClient.ServiceVersion = application.ControlledApplication.VersionNumber;
```

`ControlledApplication.VersionNumber` returns `"2022"` / `"2024"` — confirmed in both versions.

### 2.3 Fix the comment

The `ServiceVersion` XML doc currently says "change this on the move". After the fix, that
instruction is wrong — rewrite it (see the example above), rather than leaving it next to the new code.

---

## Block 3. Removing deprecated APIs — with not a single `#if`

The goal of this block: **0 CS0618 warnings** when building for 2024, while the 2022 build still
gives 0 errors and 0 warnings.

### 3.1 `Commands/RenameNestedFamiliesCommand.cs:137–147`

`Dictionary<int, int>` keyed by `id.IntegerValue` → `Dictionary<ElementId, int>` keyed by `id`.
`ElementId` implements `Equals`/`GetHashCode` in both versions, so it works as a key directly.

Fix the signatures of `Bump` and `Count`, and the `bySymbol` / `byFamily` dictionary declarations
in the calling code (find them further up the file — they are declared as `Dictionary<int, int>`).

### 3.2 `Commands/CleanupCommand.cs:615, 619, 628, 632`

`"id " + id.IntegerValue` → `"id " + id`, and `"id " + element.Id.IntegerValue` → `"id " + element.Id`.

Basis: `ElementId.ToString()` is overridden and returns the bare number in both versions
(confirmed: `new ElementId(12345).ToString() == "12345"`). The report text does not change.

After the edit, **read both overloads of `SafeName` in full** and confirm the report line still
reads `id 123456`, not `id Autodesk.Revit.DB.ElementId`.

### 3.3 `GroupName` — in two commands at once

Files: `Commands/DeleteSharedParametersCommand.cs:148–160` and
`Commands/DeleteProjectParametersCommand.cs:197–209`. The methods are **byte-for-byte identical**.

Before:

```csharp
try { return LabelUtils.GetLabelFor(definition.ParameterGroup); }
catch (Exception) { return definition.ParameterGroup.ToString(); }
```

After (both halves exist in both 2022 and 2024):

```csharp
try { return LabelUtils.GetLabelForGroup(definition.GetGroupTypeId()); }
catch (Exception) { return string.Empty; }
```

**Two traps, watch out:**

1. `GetGroupTypeId()` on a parameter with no group returns an **empty** `ForgeTypeId`, and
   `GetLabelForGroup` throws on it. Hence the `try` stays mandatory.
2. In the fallback branch, **do not** return `definition.ParameterGroup.ToString()` — that is
   exactly the deprecated API being removed, and the warning would not go away.
   If you want something rather than emptiness in the column, return the technical name instead:
   `var group = definition.GetGroupTypeId(); return group == null ? string.Empty : group.TypeId;`
   — but then confirm the "Group" column is not filled with strings like
   `autodesk.parameter.group:general-2.0.0` where a readable label used to sit.
   An empty string is preferable to a machine identifier.

Duplicating the method in two commands is **left as is**: extracting it into a shared class is a
separate task, not part of this checklist.

### 3.4 Checking the block

```powershell
dotnet build "src\VladTools\VladTools.csproj" -c Release -p:DeployToRevit=false -p:RevitVersion=2022
dotnet build "src\VladTools\VladTools.csproj" -c Release -p:DeployToRevit=false -p:RevitVersion=2024
```

Both commands: **Errors: 0, Warnings: 0**. Any remaining CS0618 is an unfinished item.

---

## Block 4. What NOT to touch (a guard against unnecessary edits)

These spots look similar to the previous block, but they **must not** be changed. Checked by name.

| Spot | Why not to touch it |
| --- | --- |
| `LinkManagerCommand.cs:198` — `parameter.Set(workset.IntegerValue)` | This is `WorksetId.IntegerValue`, not `ElementId`'s. **Not deprecated** in 2024, gives no warning. `WorksetId.Value` does not exist **in either** version — trying to "fix by analogy" would break the build |
| `Create3DThumbnailCommand.cs:142` — `new ElementId(builtInCategory)` | The `ElementId(BuiltInCategory)` overload is **not deprecated** in 2024. Only `ElementId(int)` is deprecated, and it is not in the code |
| `CleanupCommand` — manually counting unused families | `Document.GetAllUnusedElements` exists **only in 2024**. Replacing it would require `#if` and would change the behaviour (the API's element set is broader than what `UsedTypeIds` counts). Clearly **out of scope**, even though CLAUDE.md mentions it as desirable for later |
| `Infrastructure/AutodeskSession.cs` — reflection-based access | Reflection is exactly what keeps the code compatible: the makeup of `AdWebServicesBase` changed between 2022 and 2024. Direct references to `SSONET.dll` would tie the build to one year |
| `ModelPathUtils.ConvertCloudGUIDsToCloudPath` | The `(string, Guid, Guid)` overload is the same in both versions |
| The project's SDK type / a move to XAML | The project deliberately uses `Microsoft.NET.Sdk`, windows are built in code. Unrelated to compatibility |
| `TargetFramework` | Revit 2024 is `net48`. No need to change it to `net8.0-windows` (that is about 2025+) |

---

## Block 5. Documentation — mandatory in the same session

The rule from CLAUDE.md: the file is kept up to date **before** reporting the work as done;
what is outdated is replaced, not appended next to.

### 5.1 `CLAUDE.md`

- **"What this is"** — "An add-in for Autodesk Revit 2022" → "for Autodesk Revit 2022 and 2024".
- **"Build and run"** — the new commands (`build.ps1` with no flag builds both years,
  `-RevitVersion 2024` builds one), the new output paths `bin\R2022` / `bin\R2024`,
  the requirement "Revit 2022 and/or 2024 installed".
- **"Porting to another Revit version"** — rewrite the whole section. Right now it describes the
  move as "one line, `<RevitVersion>`" and lists `LabelUtils.GetLabelFor(ParameterGroup)`,
  `new ElementId(BuiltInCategory)` and `ElementId.IntegerValue` as "check on the move". After this
  work: the first and third points are **already done**, the second is checked and safe;
  describe instead how to add a third year (`build.ps1` + checking `ServiceVersion`), and that
  2025+ will additionally need `net8.0-windows` and will have to revisit `ElementId.Value`
  through `#if`.
- **"Key decisions"** — add two points:
  - why there is not a single `#if` in the code: a replacement exists for every deprecated API in
    both 2022 and 2024 (`GetGroupTypeId`, `GetLabelForGroup`, `ElementId` as a dictionary key,
    `ElementId.ToString()`), while `ElementId.Value` and `GetAllUnusedElements` are 2024-only and
    therefore unused;
  - why `RevitServerClient.ServiceVersion` is now set at runtime from
    `ControlledApplication.VersionNumber`, rather than being a constant;
  - **the build trap**: without splitting `bin`/`obj` by year, the second build overwrites the
    first, and the DLL built for 2024 ends up in Revit 2022's add-ins folder.

### 5.2 `README.md`

Requirements ("Revit 2022 or 2024"), install and build by year. The button descriptions do not
change — the behaviour is the same as before.

### 5.3 The csproj title

`<Product>` is already fixed in step 1.3 — confirm no "for Revit 2022" string is left anywhere else
(other than generated files under `obj\`, which do not need editing).

---

## Block 6. Verification

### 6.1 Compiling (done by the executor)

- [ ] `-p:RevitVersion=2022` → Errors: 0, Warnings: 0
- [ ] `-p:RevitVersion=2024` → Errors: 0, Warnings: 0
- [ ] `.\build.ps1 -NoDeploy` builds **both** years in a row with no errors
- [ ] `bin\R2022\Release\VladTools.dll` and `bin\R2024\Release\VladTools.dll` exist and
      **differ** (compare with `Get-FileHash`)
- [ ] `.\build.ps1` puts **different** DLLs into `%AppData%\Autodesk\Revit\Addins\2022\VladTools\`
      and `...\Addins\2024\VladTools\`, and `VladTools.addin` sits in each parent folder

### 6.2 Manual verification in Revit (done by the user — prepare it as a list for them)

The executor does not launch Revit. Prepare a ready checklist for the user, **for both versions**:

Families (open any `.rfa`):
- [ ] "3D Thumbnail" — the view is created, connectors are hidden
- [ ] "Delete Parameters" — the window opens, **the "Group" column is filled with readable
      labels** ("Data", "Dimensions", and the like), not empty and not `autodesk.parameter.group:…`
- [ ] "Add Formulas" — a formula is applied
- [ ] "Rename Nested" — renaming goes through, the usage-count column is correct
      (this checks step 3.1: the dictionary's key type changed)

Project (open a `.rvt`):
- [ ] "Delete Shared Parameters" — the **"Group" column** is filled the same way as in families;
      the "Scan Families" button runs through
- [ ] "Cleanup" — the counters find elements; in the failure report an unnamed element is labelled
      `id 123456`, not by its type (this checks step 3.2)
- [ ] "Link Manager" → "Revit Server…" — **the folder tree opens** (this checks block 2;
      before the fix, 2024 would have given a 404)
- [ ] "Link Manager" → "BIM360…" — the hub list opens under the signed-in user
- [ ] A link is created and lands in the requested project workset

### 6.3 Priority of the checks

If time is short, check first: **the "Group" column** (block 3.3 — the change most visible to the
user), **Revit Server on 2024** (block 2 — the only real breakage), and **which DLL landed in
which folder** (block 1.2 — the most expensive mistake).

---

## Order of work

1. Block 1 (build) — otherwise there is nothing to check the rest with.
2. Block 3 (deprecated APIs) — confirm 0 warnings on both versions right away.
3. Block 2 (Revit Server) — a small fix, but easy to forget: the compiler says nothing about it.
4. Block 5 (documentation).
5. Block 6.1 (build verification) and preparing the 6.2 list for the user.

Final report: what was done, the output of both builds with the error and warning counts,
what is left for manual checking in Revit.
