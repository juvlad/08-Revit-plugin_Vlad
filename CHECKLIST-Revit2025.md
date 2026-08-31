# Чек-лист: добавить сборку под Revit 2025

Задание для исполнителя (модель Sonnet). Цель: та же одна кодовая база, что сейчас собирается
под **Revit 2022** и **Revit 2024**, собирается **и под Revit 2025**, причём `.\build.ps1`
без параметров прогоняет **все три года за один запуск**.

Всё в разделе «Что уже выяснено» **измерено на этой машине**: рефлексия по `RevitAPI.dll`
22.0.0.0 / 24.3.40.0 / 25.4.60.0, по `SSONET.dll` всех трёх версий, чтение
`RevitAPI.runtimeconfig.json` Revit 2025 и **пробная компиляция всех текущих исходников
против Revit 2025 API на `net8.0-windows`**. Не переспрашивай и не перепроверяй эти факты.
Проверять нужно **свои правки**, а не вводные.

Предлагаемые тексты `VladTools.csproj` и `build.ps1` из шагов 2 и 3 **уже собраны и прогнаны**
в песочнице: три года подряд, 0 ошибок. Отклоняться от них без причины не нужно.

---

## Что уже выяснено (исходные данные, не требуют проверки)

### Главное

**Revit 2025 работает на .NET 8, а не на .NET Framework.** Из
`C:\Program Files\Autodesk\Revit 2025\RevitAPI.runtimeconfig.json`:

```json
"tfm": "net8.0",
"frameworks": [
  { "name": "Microsoft.NETCore.App",        "version": "8.0.0" },
  { "name": "Microsoft.WindowsDesktop.App", "version": "8.0.0" }
]
```

Значит `net48`-сборка в Revit 2025 не загрузится вовсе, и TFM обязан зависеть от года —
`net48` для 2022/2024, `net8.0-windows` для 2025. Это единственное принципиальное отличие
этого переезда от переезда на 2024.

**Кода при этом почти не меняется.** Пробная компиляция текущих исходников против Revit 2025 API
дала **4 ошибки, все одного вида** (`Definition.ParameterGroup`), а после их починки —
**0 ошибок, 8 предупреждений** (7 × CS0618 + 1 × SYSLIB0014, чинить их не нужно, см. блок
«Чего делать не надо»).

### Что убрано в Revit 2025 (ошибки компиляции)

| API | Где | 2022 | 2024 | 2025 | Замена, существующая **во всех трёх** |
| --- | --- | --- | --- | --- | --- |
| `Definition.ParameterGroup` | `DeleteProjectParametersCommand.cs:204,208`; `DeleteSharedParametersCommand.cs:155,159` | есть | есть (CS0618) | **нет** | `Definition.GetGroupTypeId()` → `ForgeTypeId` |
| `LabelUtils.GetLabelFor(BuiltInParameterGroup)` | `DeleteProjectParametersCommand.cs:204`; `DeleteSharedParametersCommand.cs:155` | есть | есть (CS0618) | **нет** | `LabelUtils.GetLabelForGroup(ForgeTypeId)` |
| тип `BuiltInParameterGroup` | только через два предыдущих | есть | есть | **нет типа целиком** | — |

Проверено поимённо: `Definition.GetGroupTypeId()` и `LabelUtils.GetLabelForGroup(ForgeTypeId)`
**есть и в 2022, и в 2024, и в 2025**, и ни в одной из версий не помечены устаревшими.
Поэтому `#if` не нужен — код остаётся общий. Это ровно та замена, которая была отложена
в [CHECKLIST-Revit2024.md](CHECKLIST-Revit2024.md); настал момент её сделать.

### Что устарело, но работает (предупреждения, чинить в этой задаче не надо)

| API | Где | Статус в 2025 | Почему не трогаем |
| --- | --- | --- | --- |
| `ElementId.IntegerValue` | `CleanupCommand.cs:615,619,628,632`; `RenameNestedFamiliesCommand.cs:140 (×2),146` | есть, CS0618 | замены, работающей и в 2022, нет: `ElementId.Value` появился только в 2024 |
| `WebRequest.Create` | `JsonHttp.cs:298` | есть, SYSLIB0014 (только на net8) | переход на `HttpClient` — отдельная задача, не про совместимость |

`WorksetId.IntegerValue` (`LinkManagerCommand.cs:198`) **не устарел ни в одной версии** —
предупреждения на него нет, это другой тип.

### Что совпадает и трогать не нужно

| Факт | Значение |
| --- | --- |
| Формат `.addin` | одинаковый во всех трёх; `AddInId` остаётся тем же GUID — папки надстроек у версий разные |
| Папка надстроек 2025 | `%AppData%\Autodesk\Revit\Addins\2025` — уже существует, формула `RevitAddinsDir` в csproj подходит как есть |
| `Directory.Build.props` | правок **не требует**: `bin\R2025\` / `obj\R2025\` разводятся той же формулой, проверено сборкой |
| `RevitServerClient.ServiceVersion` | правок **не требует**: `App.OnStartup` берёт год из `ControlledApplication.VersionNumber`, в 2025 получит `"2025"` |
| `AutodeskSession` (SSONET) | правок **не требует**: все шесть вызываемых методов (`GetInstance`, `IsLoggedIn`, `GetLoginUserName`, `IsOAuth2TokenExpired`, `GetOAuth2AccessToken`, `RefreshOAuth2Token`) есть в `SSONET.dll` 2025 — проверено рефлексией |
| `Icons.Load` | правок **не требует**: иконки читаются через `GetManifestResourceStream`, а не через `pack://`-URI, поэтому загрузка надстройки в отдельный `AssemblyLoadContext` (как делает Revit 2025) на них не влияет |
| WPF-окна | строятся кодом, `pack://`-URI и `ResourceDictionary` в проекте не используются вовсе — на .NET 8 переносятся как есть |
| .NET 8 Desktop Runtime | на машине стоит (8.0.26 / 8.0.28), отдельно ставить не надо |

Ни один другой Revit API, который использует проект, в 2025 не убран и не переименован —
это следует из того, что пробная компиляция после починки `GroupName` прошла без ошибок.

---

## Стратегия

**Три DLL из одного исходника — по одной на год, по-прежнему без единого `#if`.**
Различаются только: ссылка на `RevitAPI.dll`, папка установки, `bin\R<год>\` — и теперь ещё
TFM (`net48` / `net8.0-windows`). TFM выбирается в csproj по значению `RevitVersion`,
то есть остаётся **одним ключом сборки**, а не форком проекта.

Никакого второго `.csproj` и никакого множественного `TargetFrameworks`: год и так задаётся
снаружи, а multi-targeting заставил бы ссылаться на два разных `RevitAPI.dll` в одной сборке.

---

## Шаги

### Шаг 1. Единственная правка кода: `GroupName` в двух командах

В [DeleteProjectParametersCommand.cs](src/VladTools/Commands/DeleteProjectParametersCommand.cs) (метод
начинается на строке 197) и в
[DeleteSharedParametersCommand.cs](src/VladTools/Commands/DeleteSharedParametersCommand.cs) (строка 148)
тело метода одинаковое. Заменить в **обоих** файлах этот кусок:

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

на:

```csharp
            // GetGroupTypeId вместо ParameterGroup: в Revit 2025 и сам BuiltInParameterGroup,
            // и Definition.ParameterGroup убраны совсем. Новая пара есть уже в 2022,
            // поэтому код остаётся общим для всех трёх лет.
            ForgeTypeId group;
            try
            {
                group = definition.GetGroupTypeId();
            }
            catch (Exception)
            {
                return string.Empty;
            }

            // У параметра без группы ForgeTypeId пустой, а GetLabelForGroup на таком бросает.
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

Проверка `if (definition == null) return string.Empty;` в начале метода остаётся как была.
Новых `using` не нужно: `ForgeTypeId` живёт в `Autodesk.Revit.DB`, который уже подключён в обоих файлах.

Комментарии писать по-русски и про **почему** — как требуют «Соглашения» в CLAUDE.md.

### Шаг 2. `VladTools.csproj`: TFM по году и WPF на .NET 8

Файл: [VladTools.csproj](src/VladTools/VladTools.csproj). Изменения:

**2.1. TFM по году** — вместо одной строки `<TargetFramework>net48</TargetFramework>`:

```xml
    <!--
      Revit 2025 работает на .NET 8 (RevitAPI.runtimeconfig.json: tfm net8.0 +
      Microsoft.WindowsDesktop.App 8.0), 2022 и 2024 — на .NET Framework 4.8.
      Сравнение числовое: MSBuild приводит обе стороны к числу, когда обе — числа.
    -->
    <TargetFramework Condition="'$(RevitVersion)' &gt;= '2025'">net8.0-windows</TargetFramework>
    <TargetFramework Condition="'$(TargetFramework)' == ''">net48</TargetFramework>
    <!-- На .NET 8 сборки WPF подключает UseWPF, ссылками по именам их уже не взять. -->
    <UseWPF Condition="'$(TargetFramework)' != 'net48'">true</UseWPF>
```

**2.2. Ссылки на WPF — только для `net48`.** Существующий `ItemGroup` с `PresentationCore`,
`PresentationFramework`, `WindowsBase`, `System.Xaml` вынести в отдельный `ItemGroup`
с `Condition="'$(TargetFramework)' == 'net48'"`. На `net8.0-windows` эти четыре ссылки
дадут ошибку, а `UseWPF` подключает всё то же самое (включая `System.Xaml`).
Ссылки на `RevitAPI` / `RevitAPIUI` остаются в своём `ItemGroup` **без условия**.

**2.3. Заглушить MSB3277.** На `net8.0-windows` MSBuild выдаёт стену предупреждений
«Найдены конфликты между различными версиями»: `RevitAPI.dll` тянет за собой полсотни
соседних DLL Revit, у части из них своя версия `System.Drawing` / `Microsoft.VisualBasic`.
На сборку это не влияет, но за этой стеной не видно настоящих предупреждений. В первый
`PropertyGroup` добавить:

```xml
    <!-- RevitAPI.dll тянет соседние DLL Revit со своими версиями System.Drawing и т.п.
         На net8 это даёт стену MSB3277, за которой не видно настоящих предупреждений. -->
    <MSBuildWarningsAsMessages>MSB3277</MSBuildWarningsAsMessages>
```

**2.4. В `DeployToRevitAddins` добавить копирование `.deps.json`.** На `net8.0-windows` рядом
с DLL появляется `VladTools.deps.json` (на `net48` его нет — отсюда условие `Exists`):

```xml
    <Copy SourceFiles="$(TargetDir)$(TargetName).deps.json" DestinationFolder="$(RevitAddinsDir)\VladTools" ContinueOnError="true" Condition="Exists('$(TargetDir)$(TargetName).deps.json')" />
```

Всё остальное в csproj — `PlatformTarget x64`, `AppendTargetFrameworkToOutputPath false`,
`Product`, `RevitDir`, `RevitAddinsDir`, `EmbeddedResource` с иконками — остаётся как есть.
`AppendTargetFrameworkToOutputPath false` важен особо: без него вывод 2025 уехал бы
в `bin\R2025\Release\net8.0-windows\` и таргет установки промахнулся бы.

Проверенный целиком файл (собран под все три года) — эталон структуры:

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
    <Product>VladTools для Revit $(RevitVersion)</Product>
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
    <!-- как сейчас, плюс строка копирования .deps.json из пункта 2.4 -->
  </Target>

</Project>
```

(В эталоне комментарии опущены для краткости — в самом файле их сохранить: там объясняется,
почему TFM зависит от года.)

### Шаг 3. `build.ps1`: все годы за один запуск

Файл: [build.ps1](build.ps1). Сейчас скрипт умеет только год по умолчанию. Заменить целиком на:

```powershell
# Сборка VladTools и установка в папки надстроек Revit.
# Использование:  .\build.ps1                        (Release + установка, все годы)
#                 .\build.ps1 -Configuration Debug
#                 .\build.ps1 -NoDeploy              (только собрать)
#                 .\build.ps1 -RevitVersion 2025     (один год)

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
    Write-Warning 'Revit запущен — он держит VladTools.dll, копирование в папку надстроек не пройдёт. Закройте Revit.'
}

$deploy = if ($NoDeploy) { 'false' } else { 'true' }
$built = @()

foreach ($year in $RevitVersion) {
    # Без установленного Revit нет RevitAPI.dll по HintPath — год пропускаем,
    # а не роняем всю сборку: на машине может стоять не каждая версия.
    if (-not (Test-Path "C:\Program Files\Autodesk\Revit $year")) {
        Write-Warning "Revit $year не установлен — год пропущен."
        continue
    }

    Write-Host "── Revit $year ──" -ForegroundColor Cyan
    dotnet build $project -c $Configuration -p:RevitVersion=$year -p:DeployToRevit=$deploy
    if ($LASTEXITCODE -ne 0) { throw "Сборка под Revit $year не удалась (код $LASTEXITCODE)." }
    $built += $year
}

if ($built.Count -eq 0) { throw 'Ни один год не собран: установленных версий Revit не найдено.' }

if (-not $NoDeploy) {
    Write-Host "Готово: Revit $($built -join ', '). Перезапустите Revit — вкладка «Vlad Tools» появится на ленте." -ForegroundColor Green
}
```

Решения этого скрипта, менять их не нужно:

- **пропуск неустановленного года** вместо падения — на чужой машине может не быть какой-то версии;
- **остановка на первой же ошибке компиляции** (`throw`) — молча собрать два года из трёх хуже,
  чем не собрать ничего;
- прежняя строка «Перезапустите Revit 2022» заменена на перечень фактически собранных лет.

### Шаг 4. Проверка сборки

```powershell
.\build.ps1 -NoDeploy
```

Ожидается три блока — `── Revit 2022 ──`, `2024`, `2025` — у каждого «Сборка успешно завершена»,
**0 ошибок**. Предупреждения: 0 для 2022, 7 × CS0618 для 2024, 7 × CS0618 + 1 × SYSLIB0014
для 2025 — это норма, см. таблицы выше. Ни одного MSB3277 быть не должно (иначе не применился
пункт 2.3).

Затем — что вывод лёг куда надо:

```powershell
Get-ChildItem src\VladTools\bin\R2022\Release, src\VladTools\bin\R2024\Release, src\VladTools\bin\R2025\Release
```

В `R2022` и `R2024` — `VladTools.dll` + `.pdb`; в `R2025` — ещё и `VladTools.deps.json`.
Никаких подпапок `net48` / `net8.0-windows` внутри быть не должно.

И, отдельно, что установка тоже отрабатывает (Revit при этом закрыть):

```powershell
.\build.ps1
Get-ChildItem "$env:AppData\Autodesk\Revit\Addins\2025\VladTools"
```

### Шаг 5. Документация

Правится в этой же сессии, до отчёта — так требует раздел «Поддержка этого файла» в CLAUDE.md.

**[CLAUDE.md](CLAUDE.md):**

- «Что это» — надстройка для Revit 2022, 2024 **и 2025**.
- «Сборка и запуск» — примеры команд; убрать фразу «`build.ps1` пока умеет собирать только год
  по умолчанию» (после шага 3 она неверна) и написать, что без параметров собираются все три года,
  а `-RevitVersion` берёт один; добавить `bin\R2025\`; добавить, что TFM теперь зависит от года.
- «Ключевые решения» — новый пункт про **TFM по году**: почему `net48` и `net8.0-windows` уживаются
  в одном csproj через `Condition`, почему WPF-ссылки условные, почему
  `AppendTargetFrameworkToOutputPath false` обязателен, и почему `#if` по-прежнему не нужен.
- «Перенос на другую версию Revit» — переписать: 2025 больше не «будущая работа», а сделанный год;
  из списка отложенных предупреждений убрать `Definition.ParameterGroup` и
  `LabelUtils.GetLabelFor(BuiltInParameterGroup)` (сделано в шаге 1), оставить `ElementId.IntegerValue`
  и добавить `WebRequest.Create` / SYSLIB0014; сослаться на этот файл рядом со ссылкой на
  `CHECKLIST-Revit2024.md`.

**[README.md](README.md):**

- заголовок (строка 1) — «плагин для Revit 2022, 2024 и 2025»;
- раздел сборки (строки ~401–409) — новый вызов `build.ps1` и TFM по году;
- строка ~455 «Для Revit 2025+ дополнительно…» — переписать: для 2025 всё уже сделано,
  «дополнительно» относится теперь к 2026+.

**Этот файл** — по итогам работы дописать внизу раздел «Что сделано»: что собралось, какие
предупреждения остались, что проверено в самом Revit, что проверить не удалось.

### Шаг 6. Ручная проверка в Revit 2025

Автотестов в проекте нет, поэтому проверка ручная и обязательная — собрать и запустить мало.
Закрыть все Revit, выполнить `.\build.ps1`, запустить **Revit 2025** и пройти:

1. Вкладка «Vlad Tools» на ленте, обе панели, все семь кнопок **с иконками** (иконки — главная
   проверка того, что ресурсы читаются из отдельного `AssemblyLoadContext` .NET 8).
2. Открыть любое `.rfa`: «3д миниатюра», «Удалить параметры», «Добавить формулы»,
   «Переименовать вложенные» — окна открываются, таблицы заполнены, отмена работает.
3. Открыть `.rvt`: «Удалить общие параметры» — **особое внимание колонке «Группа»**: это единственное
   место, где менялся код (шаг 1). Названия групп должны быть человеческие и по-русски
   («Размеры», «Идентификация»), а не пустые и не `autodesk.parameter.group:...`.
   Ту же колонку проверить и в «Удалить параметры» в `.rfa`.
4. «Очистка» — окно, счётчики, отработка хотя бы одного пункта.
5. «Link Manager» — открыть окно; если есть доступ к BIM360, проверить вход (`AutodeskSession`)
   и дерево папок; если есть Revit Server — проверить, отвечает ли служба
   `RevitServerAdminRESTService2025`. Это **единственный пункт, который на этой машине проверить
   нельзя: Revit Server не установлен.** Кода это не касается в любом случае — год службы берётся
   из `ControlledApplication.VersionNumber`. Если служба не ответит, это вопрос к серверу и к тому,
   поставляет ли Autodesk Revit Server для 2025, а не к надстройке.
6. Убедиться, что **Revit 2022 и 2024 не сломались**: запустить хотя бы один из них и повторить
   пункт 3 (колонка «Группа»).

---

## Чего делать не надо

- **Не трогать `ElementId.IntegerValue`.** `ElementId.Value` появился только в 2024, в 2022 его нет —
  правка сломала бы сборку 2022. CS0618 в 2024/2025 остаётся сознательно.
- **Не переписывать `JsonHttp` на `HttpClient`.** SYSLIB0014 — предупреждение, только на net8,
  и это отдельная задача, не про совместимость.
- **Не добавлять `#if`.** Для всех расхождений замена существует во всех трёх годах; появление
  первой же директивы условной компиляции — признак, что выбрана не та замена.
- **Не переводить 2022 и 2024 на `net8.0-windows`.** Они работают на .NET Framework 4.8,
  net8-сборка в них не загрузится.
- **Не менять `Microsoft.NET.Sdk` на WPF SDK и не заводить `.xaml`.** `UseWPF=true` внутри
  `Microsoft.NET.Sdk` достаточно; окна как строились кодом, так и строятся.
- **Не менять `AddInId`** в `VladTools.addin` и не заводить второй `.addin` — папки надстроек
  у версий разные, конфликта нет.
- **Не трогать `Directory.Build.props`** — он уже разводит `bin\R<год>\` / `obj\R<год>\` для любого года.
- **Не удалять `bin\R2022\` и `bin\R2024\`** и не сводить вывод в общую папку: раздельные папки
  защищают от ловушки CS0579, описанной в CLAUDE.md.
- **Не добавлять `global.json`** и не фиксировать версию SDK: сборка проверена на том, что стоит
  на машине (SDK 9.0.303 / 10.0.101 / 10.0.400-preview), таргет-пак .NET 8 подтягивается сам.

---

## Приёмка

- [ ] `.\build.ps1 -NoDeploy` — три года, 0 ошибок, ни одного MSB3277.
- [ ] `bin\R2025\Release\` содержит `VladTools.dll`, `.pdb`, `.deps.json` и **не содержит** подпапки TFM.
- [ ] `.\build.ps1` при закрытом Revit кладёт файлы в `Addins\2022`, `Addins\2024`, `Addins\2025`.
- [ ] В коде не осталось `ParameterGroup`, `BuiltInParameterGroup` и `GetLabelFor(` применительно
      к группам — проверить поиском.
- [ ] Нет ни одного `#if`.
- [ ] Revit 2025: лента, иконки, все семь кнопок, колонка «Группа» заполнена по-русски.
- [ ] Revit 2022 (или 2024): колонка «Группа» заполнена так же, как до правки.
- [ ] CLAUDE.md и README.md обновлены (шаг 5), в них не осталось фраз «только 2022 и 2024»
      и «build.ps1 умеет только год по умолчанию».

---

## Что сделано

Шаги 1–5 выполнены полностью, ровно по этому чек-листу (тексты `VladTools.csproj` и `build.ps1` взяты
из «эталона» без отклонений).

- **Шаг 1** — `GroupName` в `DeleteProjectParametersCommand.cs` и `DeleteSharedParametersCommand.cs`
  переведён на `GetGroupTypeId()` + `GetLabelForGroup()`, как описано.
- **Шаг 2–3** — `VladTools.csproj` и `build.ps1` заменены на предложенные тексты.
- **Шаг 4** — `.\build.ps1 -NoDeploy` на реальном репозитории (не в песочнице): три блока,
  **0 ошибок** во всех трёх, 0 предупреждений для 2022, 7×CS0618 для 2024, 8 (7×CS0618 + 1×SYSLIB0014)
  для 2025 — совпадает с прогнозом. Ни одного MSB3277. `bin\R2022\Release` и `bin\R2024\Release` —
  `VladTools.dll`+`.pdb`; `bin\R2025\Release` — ещё и `.deps.json`; подпапок TFM нигде нет.
  `.\build.ps1` (с установкой, Revit был закрыт) разложил файлы по `Addins\2022`, `Addins\2024`,
  `Addins\2025\VladTools` — включая `.deps.json` в 2025. `grep` по `ParameterGroup` в исходниках даёт
  только две строки комментария (по одной на файл), `#if` в проекте нет ни одного.
- **Шаг 5** — CLAUDE.md («Что это», «Сборка и запуск», «Ключевые решения» — новый пункт про TFM по году,
  «Перенос на другую версию Revit») и README.md (заголовок, «Сборка и установка», «Перенос на другую
  версию Revit») переписаны; заодно поправлены две строки README, которые устарели ещё раньше
  (ручная правка `RevitVersion`/`ServiceVersion` в csproj — это уже год как не так, автоматизировано
  через `-p:RevitVersion` и `App.OnStartup`).

**Шаг 6 (ручная проверка в самом Revit) не выполнен** — это интерактивная проверка внутри
Revit.exe (открыть `.rfa`/`.rvt`, понажимать кнопки), а не то, что делается из командной строки;
нужен человек с открытым Revit 2025 (и, по пункту 6 самого чек-листа, заодно 2022 или 2024).
Собранные и установленные файлы к проверке готовы. Пункт про Revit Server 2025 по-прежнему нельзя
закрыть на этой машине — сервер не установлен; изменений это не требует (год берётся из
`ControlledApplication.VersionNumber` автоматически).
