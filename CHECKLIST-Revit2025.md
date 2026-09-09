# Checklist: adding a build for Revit 2025

A task for the executor (the Sonnet model). Goal: the same single codebase that already builds for
**Revit 2022** and **Revit 2024** also builds **for Revit 2025**, with `.\build.ps1` and no
arguments running **all three years in one invocation**.

Everything in "Already established" was **measured on this machine**: reflection over `RevitAPI.dll`
22.0.0.0 / 24.3.40.0 / 25.4.60.0, over `SSONET.dll` for all three versions, reading Revit 2025's
`RevitAPI.runtimeconfig.json`, and **a trial compile of all the current source files against the
Revit 2025 API on `net8.0-windows`**. Do not re-ask or re-verify these facts. What needs checking
is **your own edits**, not the inputs.

The `VladTools.csproj` and `build.ps1` texts proposed in steps 2 and 3 have **already been built
and run** in a sandbox: all three years in a row, 0 errors. There is no reason to deviate from them
without cause.

---

## Already established (given facts, no need to verify)

### The main point

**Revit 2025 runs on .NET 8, not the .NET Framework.** From
`C:\Program Files\Autodesk\Revit 2025\RevitAPI.runtimeconfig.json`:

```json
"tfm": "net8.0",
"frameworks": [
  { "name": "Microsoft.NETCore.App",        "version": "8.0.0" },
  { "name": "Microsoft.WindowsDesktop.App", "version": "8.0.0" }
]
```

So a `net48` build will not load in Revit 2025 at all, and the TFM has to depend on the year —
`net48` for 2022/2024, `net8.0-windows` for 2025. That is the one fundamental difference between
this move and the move to 2024.

**Almost none of the code needs to change.** A trial compile of the current source files against
the Revit 2025 API gave **4 errors, all of one kind** (`Definition.ParameterGroup`), and after
fixing them — **0 errors, 8 warnings** (7 × CS0618 + 1 × SYSLIB0014, no need to fix these, see
"What not to do" below).

### What was removed in Revit 2025 (compile errors)

| API | Where | 2022 | 2024 | 2025 | Replacement that exists **in all three** |
| --- | --- | --- | --- | --- | --- |
| `Definition.ParameterGroup` | `DeleteProjectParametersCommand.cs:204,208`; `DeleteSharedParametersCommand.cs:155,159` | present | present (CS0618) | **gone** | `Definition.GetGroupTypeId()` → `ForgeTypeId` |
| `LabelUtils.GetLabelFor(BuiltInParameterGroup)` | `DeleteProjectParametersCommand.cs:204`; `DeleteSharedParametersCommand.cs:155` | present | present (CS0618) | **gone** | `LabelUtils.GetLabelForGroup(ForgeTypeId)` |
| the `BuiltInParameterGroup` type | only via the two above | present | present | **the whole type is gone** | — |

Checked by name: `Definition.GetGroupTypeId()` and `LabelUtils.GetLabelForGroup(ForgeTypeId)`
**exist in 2022, 2024 and 2025 alike**, and are not marked deprecated in any of them. So no `#if` is
needed — the code stays shared. This is exactly the replacement that was postponed in
[CHECKLIST-Revit2024.md](CHECKLIST-Revit2024.md); now is the time to make it.

### What is deprecated but works (warnings, no need to fix in this task)

| API | Where | Status in 2025 | Why we leave it |
| --- | --- | --- | --- |
| `ElementId.IntegerValue` | `CleanupCommand.cs:615,619,628,632`; `RenameNestedFamiliesCommand.cs:140 (×2),146` | present, CS0618 | there is no replacement that also works in 2022: `ElementId.Value` only arrived in 2024 |
| `WebRequest.Create` | `JsonHttp.cs:298` | present, SYSLIB0014 (net8 only) | moving to `HttpClient` is a separate task, unrelated to compatibility |

`WorksetId.IntegerValue` (`LinkManagerCommand.cs:198`) **is not deprecated in any version** — no
warning is raised on it, it is a different type.

### What is the same and needs no touching

| Fact | Value |
| --- | --- |
| `.addin` format | identical across all three; `AddInId` keeps the same GUID — the add-ins folders differ by version |
| The 2025 add-ins folder | `%AppData%\Autodesk\Revit\Addins\2025` — already exists, the `RevitAddinsDir` formula in the csproj fits as is |
| `Directory.Build.props` | **needs no edit**: `bin\R2025\` / `obj\R2025\` are split by the same formula, confirmed by building |
| `RevitServerClient.ServiceVersion` | **needs no edit**: `App.OnStartup` takes the year from `ControlledApplication.VersionNumber`, which will give `"2025"` in 2025 |
| `AutodeskSession` (SSONET) | **needs no edit**: all six methods called (`GetInstance`, `IsLoggedIn`, `GetLoginUserName`, `IsOAuth2TokenExpired`, `GetOAuth2AccessToken`, `RefreshOAuth2Token`) exist in 2025's `SSONET.dll` — confirmed by reflection |
| `Icons.Load` | **needs no edit**: icons are read via `GetManifestResourceStream`, not a `pack://` URI, so loading the add-in into a separate `AssemblyLoadContext` (as Revit 2025 does) has no effect on them |
| WPF windows | built in code, no `pack://` URI or `ResourceDictionary` is used in the project at all — they carry over to .NET 8 unchanged |
| .NET 8 Desktop Runtime | already installed on this machine (8.0.26 / 8.0.28), no separate install needed |

No other Revit API the project uses was removed or renamed in 2025 — that follows from the trial
compile succeeding with no errors once `GroupName` was fixed.

---

## Strategy

**Three DLLs from one source tree — one per year, still with not a single `#if`.** Only these
differ: the `RevitAPI.dll` reference, the install folder, `bin\R<year>\` — and now also the TFM
(`net48` / `net8.0-windows`). The TFM is chosen in the csproj from the `RevitVersion` value, so it
remains **one build key**, not a project fork.

No second `.csproj` and no (multiple) `TargetFrameworks`: the year is already given from outside,
and multi-targeting would force referencing two different `RevitAPI.dll` copies in one build.

---

## Steps

### Step 1. The only code edit: `GroupName` in two commands

In [DeleteProjectParametersCommand.cs](src/VladTools/Commands/DeleteProjectParametersCommand.cs)
(the method starts at line 197) and in
[DeleteSharedParametersCommand.cs](src/VladTools/Commands/DeleteSharedParametersCommand.cs)
(line 148), the method body is identical. Replace this piece in **both** files:

```csharp
            try
            {
                return LabelUtils.GetLabelFor(definition.ParameterGroup);
            }
            catch (Exception)
            {
                return definition.ParameterGroup.ToString();
            }
```

with:

```csharp
            // GetGroupTypeId instead of ParameterGroup: in Revit 2025 both BuiltInParameterGroup
            // itself and Definition.ParameterGroup are gone entirely. The new pair already exists
            // in 2022, so the code stays shared across all three years.
            ForgeTypeId group;
            try
            {
                group = definition.GetGroupTypeId();
            }
            catch (Exception)
            {
                return string.Empty;
            }

            // A parameter with no group has an empty ForgeTypeId, and GetLabelForGroup throws on it.
            if (group == null || string.IsNullOrEmpty(group.TypeId))
                return string.Empty;

            try
            {
                return LabelUtils.GetLabelForGroup(group);
            }
            catch (Exception)
            {
                return group.TypeId;
            }
```

The `if (definition == null) return string.Empty;` check at the start of the method stays as it
was. No new `using` is needed: `ForgeTypeId` lives in `Autodesk.Revit.DB`, already referenced in
both files.

Write the comments in English, about **why** — as CLAUDE.md's "Conventions" require.

### Step 2. `VladTools.csproj`: TFM by year and WPF on .NET 8

File: [VladTools.csproj](src/VladTools/VladTools.csproj). Changes:

**2.1. TFM by year** — instead of a single `<TargetFramework>net48</TargetFramework>` line:

```xml
    <!--
      Revit 2025 runs on .NET 8 (RevitAPI.runtimeconfig.json: tfm net8.0 +
      Microsoft.WindowsDesktop.App 8.0), 2022 and 2024 run on .NET Framework 4.8.
      The comparison is numeric: MSBuild converts both sides to a number when both are numbers.
    -->
    <TargetFramework Condition="'$(RevitVersion)' &gt;= '2025'">net8.0-windows</TargetFramework>
    <TargetFramework Condition="'$(TargetFramework)' == ''">net48</TargetFramework>
    <!-- On .NET 8, WPF assemblies are pulled in via UseWPF — they can no longer be referenced by name. -->
    <UseWPF Condition="'$(TargetFramework)' != 'net48'">true</UseWPF>
```

**2.2. WPF references — only for `net48`.** Move the existing `ItemGroup` with
`PresentationCore`, `PresentationFramework`, `WindowsBase`, `System.Xaml` into a separate
`ItemGroup` with `Condition="'$(TargetFramework)' == 'net48'"`. On `net8.0-windows` these four
references would fail, and `UseWPF` pulls in the same things anyway (including `System.Xaml`).
The `RevitAPI` / `RevitAPIUI` references stay in their own `ItemGroup` **with no condition**.

**2.3. Silence MSB3277.** On `net8.0-windows`, MSBuild produces a wall of "Found conflicts between
different versions" warnings: `RevitAPI.dll` drags in about fifty neighbouring Revit DLLs, some
with their own version of `System.Drawing` / `Microsoft.VisualBasic`. This does not affect the
build, but the real warnings are lost behind this wall. Add to the first `PropertyGroup`:

```xml
    <!-- RevitAPI.dll drags in neighbouring Revit DLLs with their own versions of System.Drawing
         and the like. On net8 this produces a wall of MSB3277 that hides the real warnings. -->
    <MSBuildWarningsAsMessages>MSB3277</MSBuildWarningsAsMessages>
```

**2.4. Add copying `.deps.json` to `DeployToRevitAddins`.** On `net8.0-windows`,
`VladTools.deps.json` appears next to the DLL (there is none on `net48` — hence the `Exists` condition):

```xml
    <Copy SourceFiles="$(TargetDir)$(TargetName).deps.json" DestinationFolder="$(RevitAddinsDir)\VladTools" ContinueOnError="true" Condition="Exists('$(TargetDir)$(TargetName).deps.json')" />
```

Everything else in the csproj — `PlatformTarget x64`, `AppendTargetFrameworkToOutputPath false`,
`Product`, `RevitDir`, `RevitAddinsDir`, the icon `EmbeddedResource`s — stays as is.
`AppendTargetFrameworkToOutputPath false` matters especially here: without it, 2025's output would
land in `bin\R2025\Release\net8.0-windows\` and the install target would miss the file.

A verified full file (built for all three years) — the structural reference:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework Condition="'$(RevitVersion)' &gt;= '2025'">net8.0-windows</TargetFramework>
    <TargetFramework Condition="'$(TargetFramework)' == ''">net48</TargetFramework>
    <UseWPF Condition="'$(TargetFramework)' != 'net48'">true</UseWPF>
    <LangVersion>latest</LangVersion>
    <PlatformTarget>x64</PlatformTarget>
    <AssemblyName>VladTools</AssemblyName>
    <RootNamespace>VladTools</RootNamespace>
    <Version>1.0.0</Version>
    <Company>Vlad</Company>
    <Product>VladTools for Revit $(RevitVersion)</Product>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <GenerateAssemblyInfo>true</GenerateAssemblyInfo>
    <EnableDefaultNoneItems>false</EnableDefaultNoneItems>
    <MSBuildWarningsAsMessages>MSB3277</MSBuildWarningsAsMessages>
  </PropertyGroup>

  <PropertyGroup>
    <RevitDir>C:\Program Files\Autodesk\Revit $(RevitVersion)</RevitDir>
    <RevitAddinsDir>$(AppData)\Autodesk\Revit\Addins\$(RevitVersion)</RevitAddinsDir>
    <DeployToRevit Condition="'$(DeployToRevit)' == ''">true</DeployToRevit>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="RevitAPI">
      <HintPath>$(RevitDir)\RevitAPI.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="RevitAPIUI">
      <HintPath>$(RevitDir)\RevitAPIUI.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <ItemGroup Condition="'$(TargetFramework)' == 'net48'">
    <Reference Include="PresentationCore" />
    <Reference Include="PresentationFramework" />
    <Reference Include="WindowsBase" />
    <Reference Include="System.Xaml" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="Resources\*.png" />
  </ItemGroup>

  <Target Name="DeployToRevitAddins" AfterTargets="Build" Condition="'$(DeployToRevit)' == 'true'">
    <!-- as it is now, plus the .deps.json copy line from point 2.4 -->
  </Target>

</Project>
```

(Comments are omitted from the reference for brevity — keep them in the actual file: they explain
why the TFM depends on the year.)

### Step 3. `build.ps1`: every year in one run

File: [build.ps1](build.ps1). Right now the script only knows the default year. Replace it in full with:

```powershell
# Builds VladTools and installs it into the Revit add-ins folders.
# Usage:  .\build.ps1                        (Release + install, every year)
#         .\build.ps1 -Configuration Debug
#         .\build.ps1 -NoDeploy              (build only)
#         .\build.ps1 -RevitVersion 2025     (one year)

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('2022', '2024', '2025')]
    [string[]]$RevitVersion = @('2022', '2024', '2025'),
    [switch]$NoDeploy
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\VladTools\VladTools.csproj'

if (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) {
    Write-Warning 'Revit is running — it holds VladTools.dll locked, the copy into the add-ins folder will not go through. Close Revit.'
}

$deploy = if ($NoDeploy) { 'false' } else { 'true' }
$built = @()

foreach ($year in $RevitVersion) {
    # No installed Revit means no RevitAPI.dll at the HintPath — the year is skipped
    # rather than failing the whole build: not every version may be on this machine.
    if (-not (Test-Path "C:\Program Files\Autodesk\Revit $year")) {
        Write-Warning "Revit $year is not installed — the year was skipped."
        continue
    }

    Write-Host "── Revit $year ──" -ForegroundColor Cyan
    dotnet build $project -c $Configuration -p:RevitVersion=$year -p:DeployToRevit=$deploy
    if ($LASTEXITCODE -ne 0) { throw "The build for Revit $year failed (code $LASTEXITCODE)." }
    $built += $year
}

if ($built.Count -eq 0) { throw 'No year was built: no installed Revit version was found.' }

if (-not $NoDeploy) {
    Write-Host "Done: Revit $($built -join ', '). Restart Revit — the \"Vlad Tools\" tab will appear on the ribbon." -ForegroundColor Green
}
```

Decisions in this script that should not be changed:

- **skipping a year that is not installed** rather than failing — another machine may be missing a version;
- **stopping on the first compile failure** (`throw`) — silently building two years out of three is
  worse than building none;
- the old "Restart Revit 2022" line replaced with the list of years actually built.

### Step 4. Verifying the build

```powershell
.\build.ps1 -NoDeploy
```

Three blocks are expected — `── Revit 2022 ──`, `2024`, `2025` — each with "Build succeeded",
**0 errors**. Warnings: 0 for 2022, 7 × CS0618 for 2024, 7 × CS0618 + 1 × SYSLIB0014 for 2025 — that
is normal, see the tables above. There must be no MSB3277 at all (otherwise point 2.3 was not applied).

Then — that the output landed where it should:

```powershell
Get-ChildItem src\VladTools\bin\R2022\Release, src\VladTools\bin\R2024\Release, src\VladTools\bin\R2025\Release
```

`R2022` and `R2024` should have `VladTools.dll` + `.pdb`; `R2025` should have `VladTools.deps.json`
too. There must be no TFM subfolders (`net48` / `net8.0-windows`) inside any of them.

And, separately, that installing works too (close Revit for this):

```powershell
.\build.ps1
Get-ChildItem "$env:AppData\Autodesk\Revit\Addins\2025\VladTools"
```

### Step 5. Documentation

Edited in this same session, before reporting — as the "Keeping this file up to date" section of
CLAUDE.md requires.

**[CLAUDE.md](CLAUDE.md):**

- "What this is" — an add-in for Revit 2022, 2024 **and 2025**.
- "Build and run" — updated command examples; remove the phrase "`build.ps1` can currently only
  build the default year" (wrong after step 3) and say that with no arguments all three years are
  built, and `-RevitVersion` takes one; add `bin\R2025\`; add that the TFM now depends on the year.
- "Key decisions" — a new entry about **TFM by year**: why `net48` and `net8.0-windows` coexist in
  one csproj through a `Condition`, why the WPF references are conditional, why
  `AppendTargetFrameworkToOutputPath false` is mandatory, and why `#if` is still not needed.
- "Porting to another Revit version" — rewrite: 2025 is no longer "future work" but a year that is
  done; remove `Definition.ParameterGroup` and `LabelUtils.GetLabelFor(BuiltInParameterGroup)` from
  the list of deferred warnings (done in step 1), keep `ElementId.IntegerValue` and add
  `WebRequest.Create` / SYSLIB0014; link to this file next to the link to `CHECKLIST-Revit2024.md`.

**[README.md](README.md):**

- the title (line 1) — "an add-in for Revit 2022, 2024 and 2025";
- the build section (around lines 401–409) — the new `build.ps1` call and TFM by year;
- the line around 455, "For Revit 2025+ additionally…" — rewrite: for 2025 everything is already
  done, "additionally" now refers to 2026+.

**This file** — once the work is done, add a "What was done" section at the bottom: what built,
which warnings remain, what was checked in Revit itself, what could not be checked.

### Step 6. Manual verification in Revit 2025

The project has no automated tests, so a manual check is mandatory — building and launching alone
is not enough. Close every Revit, run `.\build.ps1`, launch **Revit 2025** and go through:

1. The "Vlad Tools" tab on the ribbon, both panels, all seven buttons **with icons** (the icons are
   the main check that resources are read correctly from a separate .NET 8 `AssemblyLoadContext`).
2. Open any `.rfa`: "3D Thumbnail", "Delete Parameters", "Add Formulas",
   "Rename Nested" — the windows open, the tables are filled in, cancelling works.
3. Open a `.rvt`: "Delete Shared Parameters" — **pay special attention to the "Group" column**:
   this is the only spot where the code changed (step 1). Group names should read as normal
   English words ("Dimensions", "Identity Data"), not empty and not
   `autodesk.parameter.group:...`. Check the same column in "Delete Parameters" in an `.rfa` too.
4. "Cleanup" — the window, the counters, at least one item running through.
5. "Link Manager" — open the window; if BIM360 access is available, check the sign-in
   (`AutodeskSession`) and the folder tree; if Revit Server is available, check whether the
   `RevitServerAdminRESTService2025` service answers. This is **the one item that cannot be
   checked on this machine: Revit Server is not installed.** The code is unaffected either way —
   the service year comes from `ControlledApplication.VersionNumber`. If the service does not
   answer, that is a question for the server and for whether Autodesk ships Revit Server for
   2025 at all, not for the add-in.
6. Confirm that **Revit 2022 and 2024 are not broken**: launch at least one of them and repeat
   point 3 (the "Group" column).

---

## What not to do

- **Do not touch `ElementId.IntegerValue`.** `ElementId.Value` only arrived in 2024, it does not
  exist in 2022 — the edit would break the 2022 build. The CS0618 on 2024/2025 stays deliberately.
- **Do not rewrite `JsonHttp` to use `HttpClient`.** SYSLIB0014 is a warning, net8-only, and a
  separate task, unrelated to compatibility.
- **Do not add `#if`.** A replacement exists in all three years for every discrepancy; the moment
  a conditional-compilation directive appears, it is a sign the wrong replacement was picked.
- **Do not move 2022 and 2024 to `net8.0-windows`.** They run on .NET Framework 4.8, a net8 build
  will not load in them.
- **Do not switch `Microsoft.NET.Sdk` to the WPF SDK and do not add a `.xaml` file.**
  `UseWPF=true` inside `Microsoft.NET.Sdk` is enough; windows keep being built in code as before.
- **Do not change `AddInId`** in `VladTools.addin` and do not add a second `.addin` — the add-ins
  folders differ by version, there is no conflict.
- **Do not touch `Directory.Build.props`** — it already splits `bin\R<year>\` / `obj\R<year>\` for
  any year.
- **Do not delete `bin\R2022\` and `bin\R2024\`** and do not merge the output into one shared
  folder: separate folders guard against the CS0579 trap described in CLAUDE.md.
- **Do not add a `global.json`** and do not pin an SDK version: the build was verified against
  whatever is installed on this machine (SDK 9.0.303 / 10.0.101 / 10.0.400-preview), the .NET 8
  target pack is pulled in on its own.

---

## Acceptance

- [ ] `.\build.ps1 -NoDeploy` — three years, 0 errors, not a single MSB3277.
- [ ] `bin\R2025\Release\` contains `VladTools.dll`, `.pdb`, `.deps.json`, and **contains no** TFM subfolder.
- [ ] With Revit closed, `.\build.ps1` puts files into `Addins\2022`, `Addins\2024`, `Addins\2025`.
- [ ] No `ParameterGroup`, `BuiltInParameterGroup` or `GetLabelFor(` referring to groups is left in
      the code — confirm with a search.
- [ ] Not a single `#if` anywhere.
- [ ] Revit 2025: the ribbon, the icons, all seven buttons, the "Group" column reads properly.
- [ ] Revit 2022 (or 2024): the "Group" column reads the same as it did before the edit.
- [ ] CLAUDE.md and README.md are updated (step 5), with no "2022 and 2024 only" or
      "build.ps1 can only build the default year" phrases left in them.

---

## What was done

Steps 1–5 were completed in full, exactly per this checklist (the `VladTools.csproj` and
`build.ps1` texts were taken from the "reference" with no deviation).

- **Step 1** — `GroupName` in `DeleteProjectParametersCommand.cs` and
  `DeleteSharedParametersCommand.cs` was moved over to `GetGroupTypeId()` + `GetLabelForGroup()`, as described.
- **Steps 2–3** — `VladTools.csproj` and `build.ps1` were replaced with the proposed texts.
- **Step 4** — `.\build.ps1 -NoDeploy` on the real repository (not the sandbox): three blocks,
  **0 errors** in all three, 0 warnings for 2022, 7×CS0618 for 2024, 8 (7×CS0618 + 1×SYSLIB0014)
  for 2025 — matching the prediction. Not a single MSB3277. `bin\R2022\Release` and
  `bin\R2024\Release` hold `VladTools.dll`+`.pdb`; `bin\R2025\Release` also holds `.deps.json`;
  there are no TFM subfolders anywhere. `.\build.ps1` (with install, Revit was closed) laid out
  files into `Addins\2022`, `Addins\2024`, `Addins\2025\VladTools` — including `.deps.json` for
  2025. A `grep` for `ParameterGroup` in the source files gives only two comment lines (one per
  file), there is not a single `#if` in the project.
- **Step 5** — CLAUDE.md ("What this is", "Build and run", "Key decisions" — a new entry about TFM
  by year, "Porting to another Revit version") and README.md (the title, "Build and install",
  "Porting to another Revit version") were rewritten; two more README lines that had gone stale
  even earlier were fixed along the way (manually editing `RevitVersion`/`ServiceVersion` in the
  csproj — that stopped being true a year ago, it is automated through `-p:RevitVersion` and `App.OnStartup`).

**Step 6 (manual verification inside Revit itself) was not completed** — it is an interactive
check inside Revit.exe (opening an `.rfa`/`.rvt`, pressing buttons), not something that can be done
from the command line; it needs a person with Revit 2025 open (and, per point 6 of the checklist
itself, 2022 or 2024 too). The built and installed files are ready for that check. The point about
Revit Server 2025 still cannot be closed on this machine — the server is not installed; this
requires no changes either way (the year is taken from `ControlledApplication.VersionNumber` automatically).
