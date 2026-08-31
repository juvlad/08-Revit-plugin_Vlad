# Чек-лист: поддержка Revit 2022 + Revit 2024

Задание для исполнителя (модель Sonnet). Цель: одна кодовая база собирается и работает
и в **Revit 2022**, и в **Revit 2024**.

Всё, что здесь написано, **проверено на реальных сборках** `RevitAPI.dll` 22.0.0.0 и 24.3.40.0,
установленных на этой машине (рефлексия по обеим сборкам + пробная компиляция).
Не переспрашивай и не перепроверяй факты из раздела «Что уже выяснено» — они получены измерением,
а не догадкой. Проверять нужно **свои правки**, а не эти вводные.

---

## Что уже выяснено (исходные данные, не требуют проверки)

### Главное

**Текущий код компилируется против Revit 2024 API без единой ошибки.**
Пробная сборка `dotnet build -p:RevitVersion=2024` даёт: **0 ошибок, 13 предупреждений CS0618**
(устаревшие API). То есть переезд — это не переписывание, а аккуратная зачистка.

### Что совпадает и трогать не нужно

| Факт | Значение |
| --- | --- |
| .NET у Revit 2024 | тот же `net48` (`supportedRuntime v4.0` в `Revit.exe.config` обеих версий). **TFM менять не надо** |
| `RevitAPI.dll` | **не подписана строгим именем** ни в 2022, ни в 2024 |
| Формат `.addin` | одинаковый; `AddInId` может остаться прежним GUID — папки надстроек у версий разные |
| Лента (`UIControlledApplication`, `RibbonPanel`, `PushButtonData`, `TaskDialog`, `MainWindowHandle`) | без изменений |
| Перечисления `ImportPlacement`, `WorksetConfigurationOption`, `ViewDetailLevel` | состав совпадает |
| `DisplayStyle` | в 2024 добавлено значение `Textures`; `Realistic` на месте — влияния нет |

Проверены поимённо и **существуют в обеих версиях, не устарели** (правок не требуют):
`Category.GetCategory`, `Parameter.Set(int)`, `RevitLinkType.Create`/`LoadFrom`,
`RevitLinkInstance.Create`, `RevitLinkOptions`, `WorksharingUtils.GetUserWorksetInfo`,
`ModelPathUtils.ConvertCloudGUIDsToCloudPath(string, Guid, Guid)`, `Dimension.FamilyLabel`,
`Element.VersionGuid`, `Element.GetDependentElements`, `Group.UngroupMembers`,
`ViewSchedule.IsTitleblockRevisionSchedule`, `ViewSchedule.IsInternalKeynoteSchedule`,
`View.HideElements`, `View3D.SetCategoryHidden`, `FamilyManager.SetFormula`,
`FamilyManager.RemoveParameter`, `SharedParameterElement.GuidValue`,
`WorksetTable.GetActiveWorksetId`, `ControlledApplication.VersionNumber`.

### Что устарело в 2024 (13 предупреждений CS0618)

| API | Где | Замена, существующая **и в 2022, и в 2024** |
| --- | --- | --- |
| `ElementId.IntegerValue` | `CleanupCommand.cs:615,619,628,632`; `RenameNestedFamiliesCommand.cs:140 (×2),146` | нет прямой — обходится без неё (см. шаги 3.1–3.2) |
| `Definition.ParameterGroup` | `DeleteProjectParametersCommand.cs:204,208`; `DeleteSharedParametersCommand.cs:155,159` | `Definition.GetGroupTypeId()` — **есть в 2022** |
| `LabelUtils.GetLabelFor(BuiltInParameterGroup)` | `DeleteProjectParametersCommand.cs:204`; `DeleteSharedParametersCommand.cs:155` | `LabelUtils.GetLabelForGroup(ForgeTypeId)` — **есть в 2022** |

### Что есть только в одной версии

| API | 2022 | 2024 | Вывод |
| --- | --- | --- | --- |
| `ElementId.Value` | нет | есть | **использовать нельзя** |
| `ElementId(long)` | нет | есть | использовать нельзя |
| `Document.GetAllUnusedElements` / `GetUnusedElements` | нет | есть | использовать нельзя (см. блок 4) |

Проверено отдельно: `ElementId.ToString()` переопределён в обеих версиях и возвращает
голое число — `new ElementId(12345).ToString() == "12345"`. Это основание для шага 3.2.

### Единственная настоящая ошибка времени выполнения

`Infrastructure/RevitServerClient.cs:52` — `private const string ServiceVersion = "2022";`
подставляется в URL (`RevitServerClient.cs:83`):
`http://<сервер>/RevitServerAdminRESTService2022/AdminRESTService.svc/...`
На сервере Revit Server 2024 такого адреса нет — служба ответит **404**, и кнопка
«Revit Server…» в «Link Manager» просто не покажет папки. Компилятор об этом молчит.

### SSONET (вход в Autodesk для BIM360)

Состав `Autodesk.Revit.AdWebServicesBase` между версиями изменился: в 2024 **пропали**
`GetTokenExpiryDate`, `IsTokenExpired`, `RefreshToken`, `GetLoginCookie`, `GetServiceEntitlement`.
Но `AutodeskSession` вызывает только `GetInstance`, `IsLoggedIn`, `GetLoginUserName`,
`IsOAuth2TokenExpired`, `GetOAuth2AccessToken`, `RefreshOAuth2Token` — **все шесть есть в обеих**.
Правок не требуется; менять рефлексию на прямые ссылки нельзя (см. блок 4).

---

## Стратегия

**Две DLL из одного исходника — по одной на год.** Не одна общая DLL.

Почему не одна: `RevitAPI.dll` не подписана, поэтому DLL, собранная под 2022, технически
загрузится и в 2024, — но это незадокументированное поведение, и оно перестанет работать,
как только в коде появится хоть один API, изменивший сигнатуру. Сборка под каждый год даёт
доказательство от компилятора, а не надежду.

**При этом `#if` в коде быть не должно.** Для всех устаревших API замена существует в обеих
версиях, поэтому код остаётся один и тот же — различаются только ссылки на `RevitAPI.dll`
и папка установки. Директива `REVIT2024` заводится в csproj **про запас**, но в этой задаче
не применяется ни разу. Если тебе показалось, что `#if` нужен, — сначала перечитай таблицу
замен выше: скорее всего, ты взял 2024-only API там, где есть общий.

---

## Блок 1. Сборка на два года

### 1.1 `src/VladTools/VladTools.csproj` — сделать `RevitVersion` переопределяемым

Сейчас: `<RevitVersion>2022</RevitVersion>` — жёстко.
Нужно: значение по умолчанию, которое перебивается ключом `-p:RevitVersion=2024`.

```xml
<RevitVersion Condition="'$(RevitVersion)' == ''">2022</RevitVersion>
```

> Формально `-p:` и так перебивает свойство из файла (глобальные свойства MSBuild),
> но `Condition` делает намерение явным и переживает вынос свойства в `Directory.Build.props`.

### 1.2 Развести выходные папки по годам — **обязательно**

Сейчас `AppendTargetFrameworkToOutputPath=false`, и обе сборки пишут в один
`bin\Release` + `obj\Release`. Собрав 2024 после 2022, ты **молча положишь
2024-ю DLL в папку надстроек Revit 2022** — Revit при запуске упадёт или не покажет вкладку.
Это самая дорогая ошибка в задании; она уже воспроизводилась при подготовке чек-листа.

Добавить в тот же `PropertyGroup`:

```xml
<BaseOutputPath>bin\R$(RevitVersion)\</BaseOutputPath>
<BaseIntermediateOutputPath>obj\R$(RevitVersion)\</BaseIntermediateOutputPath>
```

> `BaseIntermediateOutputPath` должен стоять **до** импорта SDK, иначе `restore` его не увидит.
> В SDK-стиле это значит: либо в `Directory.Build.props`, либо задавать ключом `-p:` из `build.ps1`.
> Проверь, что после сборки обеих версий существуют и `bin\R2022\Release\VladTools.dll`,
> и `bin\R2024\Release\VladTools.dll`, и что это **разные** файлы (сравни размер/хэш).
> Если развести `obj` в csproj не выходит — не изобретай: разведи только `bin`,
> а между годами в `build.ps1` вызывай очистку промежуточной папки.

### 1.3 Название продукта — по году

```xml
<Product>VladTools для Revit $(RevitVersion)</Product>
```

### 1.4 Константа компиляции про запас

```xml
<DefineConstants>$(DefineConstants);REVIT$(RevitVersion)</DefineConstants>
```

В этой задаче **не использовать**. Заводится, чтобы следующий переезд (2025+, где `net48`
уже не годится) не начинался с правки csproj.

### 1.5 Проверка наличия Revit перед сборкой

Если папки `C:\Program Files\Autodesk\Revit $(RevitVersion)` нет, MSBuild выдаст
невнятное «metadata file not found». Добавить понятную ошибку:

```xml
<Target Name="CheckRevitDir" BeforeTargets="BeforeBuild">
  <Error Condition="!Exists('$(RevitDir)\RevitAPI.dll')"
         Text="Revit $(RevitVersion) не найден в $(RevitDir). Соберите под установленную версию: -p:RevitVersion=2022" />
</Target>
```

### 1.6 `build.ps1` — собирать и ставить оба года

- Добавить параметр `[string[]]$RevitVersion = @('2022','2024')`.
- Цикл по годам; для каждого — `dotnet build $project -c $Configuration -p:DeployToRevit=$deploy -p:RevitVersion=$v`.
- Год, для которого Revit не установлен, **пропускать с предупреждением**, а не валить всю сборку:
  проверять `Test-Path "C:\Program Files\Autodesk\Revit $v"`.
- Итоговое сообщение — по годам: «Перезапустите Revit 2022 / Revit 2024».
- Предупреждение про запущенный Revit оставить (Revit держит `VladTools.dll`).
- Сохранить прежние сценарии вызова: `.\build.ps1`, `-Configuration Debug`, `-NoDeploy`
  должны продолжать работать. Добавить `.\build.ps1 -RevitVersion 2024` для одного года.

### 1.7 Установка `.addin`

`RevitAddinsDir` уже собран из `$(RevitVersion)` — убедись, что таргет `DeployToRevitAddins`
после правок кладёт файлы именно в `%AppData%\Autodesk\Revit\Addins\2022\` и `...\2024\`
соответственно, и что `VladTools.addin` копируется в **обе**. Сам файл манифеста
не меняется и остаётся один на репозиторий.

---

## Блок 2. Исправить Revit Server (единственная реальная поломка)

### 2.1 `Infrastructure/RevitServerClient.cs`

`ServiceVersion` из `const` сделать изменяемым статическим свойством со значением по умолчанию
`"2022"` (чтобы поведение без инициализации не изменилось):

```csharp
/// <summary>
/// Версия REST-службы Revit Server: её имя включает год (RevitServerAdminRESTService2024),
/// и на чужой год сервер отвечает 404. Значение ставит App.OnStartup по версии
/// запущенного Revit — здесь только запасное на случай, если надстройка не стартовала.
/// </summary>
public static string ServiceVersion { get; set; } = "2022";
```

Строку 83 (сборку URL) не трогать — она подхватит новое значение сама.

### 2.2 `App.cs` — задать версию при старте

В начале `OnStartup`, до создания панелей:

```csharp
// Имя REST-службы Revit Server включает год Revit; берём его у самого Revit,
// чтобы одна и та же сборка не промахивалась мимо службы.
RevitServerClient.ServiceVersion = application.ControlledApplication.VersionNumber;
```

`ControlledApplication.VersionNumber` возвращает `"2022"` / `"2024"` — проверено в обеих версиях.

### 2.3 Поправить комментарий

XML-doc у `ServiceVersion` сейчас велит «при переезде поменять здесь». После правки это
указание становится неверным — переписать (образец выше), а не оставлять рядом.

---

## Блок 3. Убрать устаревшие API — без единого `#if`

Цель блока: **0 предупреждений CS0618** при сборке под 2024, при этом сборка под 2022
продолжает давать 0 ошибок и 0 предупреждений.

### 3.1 `Commands/RenameNestedFamiliesCommand.cs:137–147`

`Dictionary<int, int>` с ключом `id.IntegerValue` → `Dictionary<ElementId, int>` с ключом `id`.
`ElementId` реализует `Equals`/`GetHashCode` в обеих версиях, поэтому годится как ключ напрямую.

Поправить сигнатуры `Bump` и `Count`, а также объявления словарей `bySymbol` / `byFamily`
в вызывающем коде (найди их выше по файлу — они объявлены как `Dictionary<int, int>`).

### 3.2 `Commands/CleanupCommand.cs:615, 619, 628, 632`

`"id " + id.IntegerValue` → `"id " + id`, и `"id " + element.Id.IntegerValue` → `"id " + element.Id`.

Основание: `ElementId.ToString()` переопределён и возвращает голое число в обеих версиях
(проверено: `new ElementId(12345).ToString() == "12345"`). Текст отчёта не меняется.

После правки **прочитай обе перегрузки `SafeName` целиком** и убедись, что строка
в отчёте по-прежнему выглядит как `id 123456`, а не `id Autodesk.Revit.DB.ElementId`.

### 3.3 `GroupName` — в двух командах сразу

Файлы: `Commands/DeleteSharedParametersCommand.cs:148–160`
и `Commands/DeleteProjectParametersCommand.cs:197–209`. Методы **побайтово одинаковые**.

Было:

```csharp
try { return LabelUtils.GetLabelFor(definition.ParameterGroup); }
catch (Exception) { return definition.ParameterGroup.ToString(); }
```

Стало (обе части существуют и в 2022, и в 2024):

```csharp
try { return LabelUtils.GetLabelForGroup(definition.GetGroupTypeId()); }
catch (Exception) { return string.Empty; }
```

**Осторожно, две ловушки:**

1. `GetGroupTypeId()` у параметра без группы возвращает **пустой** `ForgeTypeId`, и
   `GetLabelForGroup` на нём бросает исключение. Отсюда `try` остаётся обязательным.
2. В запасной ветке **нельзя** возвращать `definition.ParameterGroup.ToString()` — это ровно
   тот устаревший API, который мы убираем, и предупреждение никуда не денется.
   Если хочется, чтобы в колонке было хоть что-то вместо пустоты, вернуть техническое имя:
   `var group = definition.GetGroupTypeId(); return group == null ? string.Empty : group.TypeId;`
   — но тогда убедись, что колонка «Группа» не заполнилась строками вида
   `autodesk.parameter.group:general-2.0.0` там, где раньше было русское название.
   Пустая строка предпочтительнее машинного идентификатора.

Дублирование метода в двух командах **оставить как есть**: вынос в общий класс —
отдельная задача, в этот чек-лист она не входит.

### 3.4 Проверка блока

```powershell
dotnet build "src\VladTools\VladTools.csproj" -c Release -p:DeployToRevit=false -p:RevitVersion=2022
dotnet build "src\VladTools\VladTools.csproj" -c Release -p:DeployToRevit=false -p:RevitVersion=2024
```

Обе команды: **Ошибок: 0, Предупреждений: 0**. Любое оставшееся CS0618 — незакрытый пункт.

---

## Блок 4. Что НЕ трогать (защита от лишних правок)

Эти места выглядят похожими на предыдущий блок, но менять их **нельзя**. Проверено поимённо.

| Место | Почему не трогать |
| --- | --- |
| `LinkManagerCommand.cs:198` — `parameter.Set(workset.IntegerValue)` | Это `WorksetId.IntegerValue`, а не `ElementId`. В 2024 **не устарел**, предупреждения не даёт. `WorksetId.Value` не существует **ни в одной** из версий — попытка «исправить по аналогии» сломает сборку |
| `Create3DThumbnailCommand.cs:142` — `new ElementId(builtInCategory)` | Перегрузка `ElementId(BuiltInCategory)` в 2024 **не устарела**. Устарела только `ElementId(int)`, а её в коде нет |
| `CleanupCommand` — ручной подсчёт неиспользуемых семейств | `Document.GetAllUnusedElements` есть **только в 2024**. Замена потребовала бы `#if` и изменила бы поведение (набор элементов у API шире, чем считает `UsedTypeIds`). Явно **вне задачи**, даже если в CLAUDE.md это упомянуто как желательное на будущее |
| `Infrastructure/AutodeskSession.cs` — доступ рефлексией | Именно рефлексия и делает код совместимым: состав `AdWebServicesBase` между 2022 и 2024 изменился. Прямые ссылки на `SSONET.dll` привяжут сборку к одному году |
| `ModelPathUtils.ConvertCloudGUIDsToCloudPath` | Перегрузка `(string, Guid, Guid)` одинакова в обеих версиях |
| Тип SDK проекта / переход на XAML | Проект намеренно на `Microsoft.NET.Sdk`, окна собираются кодом. К совместимости отношения не имеет |
| `TargetFramework` | Revit 2024 — это `net48`. Менять на `net8.0-windows` не нужно (это про 2025+) |

---

## Блок 5. Документация — обязательна в этой же сессии

Правило из CLAUDE.md: файл поддерживается в актуальном состоянии **до** отчёта о работе,
устаревшее заменяется, а не дописывается рядом.

### 5.1 `CLAUDE.md`

- **«Что это»** — «Надстройка для Autodesk Revit 2022» → «для Autodesk Revit 2022 и 2024».
- **«Сборка и запуск»** — новые команды (`build.ps1` без ключа собирает оба года,
  `-RevitVersion 2024` — один), новые пути вывода `bin\R2022` / `bin\R2024`,
  требование «установлен Revit 2022 и/или 2024».
- **«Перенос на другую версию Revit»** — раздел переписать целиком. Сейчас он описывает
  переезд «одной строкой `<RevitVersion>`» и перечисляет `LabelUtils.GetLabelFor(ParameterGroup)`,
  `new ElementId(BuiltInCategory)` и `ElementId.IntegerValue` как «проверить при переезде».
  После работы: первые и третий пункт **уже сделаны**, второй проверен и безопасен;
  вместо этого описать, как добавить третий год (`build.ps1` + проверка `ServiceVersion`),
  и что для 2025+ дополнительно нужен `net8.0-windows` и там же придётся вернуться
  к `ElementId.Value` через `#if`.
- **«Ключевые решения»** — добавить два пункта:
  - почему в коде нет ни одного `#if`: для каждого устаревшего API замена существует
    и в 2022, и в 2024 (`GetGroupTypeId`, `GetLabelForGroup`, `ElementId` как ключ словаря,
    `ElementId.ToString()`), а `ElementId.Value` и `GetAllUnusedElements` — только 2024,
    и потому не используются;
  - почему `RevitServerClient.ServiceVersion` теперь задаётся в рантайме из
    `ControlledApplication.VersionNumber`, а не константой;
  - **ловушка сборки**: без разведённых `bin`/`obj` по годам вторая сборка перезаписывает
    первую и в папку надстроек Revit 2022 уезжает DLL, собранная под 2024.

### 5.2 `README.md`

Требования («Revit 2022 или 2024»), установка и сборка по годам. Описание кнопок
не меняется — поведение прежнее.

### 5.3 Заголовок csproj

`<Product>` уже правится в шаге 1.3 — сверь, что нигде больше не осталось строки
«для Revit 2022» (кроме сгенерированных файлов в `obj\`, их править не нужно).

---

## Блок 6. Проверка

### 6.1 Компиляция (делает исполнитель)

- [ ] `-p:RevitVersion=2022` → Ошибок: 0, Предупреждений: 0
- [ ] `-p:RevitVersion=2024` → Ошибок: 0, Предупреждений: 0
- [ ] `.\build.ps1 -NoDeploy` собирает **оба** года подряд без ошибок
- [ ] `bin\R2022\Release\VladTools.dll` и `bin\R2024\Release\VladTools.dll` существуют
      и **различаются** (сравнить `Get-FileHash`)
- [ ] `.\build.ps1` кладёт в `%AppData%\Autodesk\Revit\Addins\2022\VladTools\`
      и `...\Addins\2024\VladTools\` **разные** DLL, и в каждой папке-родителе лежит `VladTools.addin`

### 6.2 Ручная проверка в Revit (делает пользователь — оформи как список для него)

Исполнитель Revit не запускает. Подготовь пользователю готовый список, **по обеим версиям**:

Семейства (открыть любой `.rfa`):
- [ ] «3д миниатюра» — вид создаётся, соединители скрыты
- [ ] «Удалить параметры» — окно открывается, **колонка «Группа» заполнена русскими названиями**
      («Данные», «Размеры» и т. п.), а не пустая и не `autodesk.parameter.group:…`
- [ ] «Добавить формулы» — формула применяется
- [ ] «Переименовать вложенные» — переименование проходит, счётчик использований в колонке верный
      (это проверка шага 3.1: словарь сменил тип ключа)

Проект (открыть `.rvt`):
- [ ] «Удалить общие параметры» — **колонка «Группа»** заполнена так же, как в семействах;
      кнопка «Проверить семейства» отрабатывает
- [ ] «Очистка» — счётчики находят элементы; в отчёте об ошибках элемент без имени
      подписан как `id 123456`, а не типом (это проверка шага 3.2)
- [ ] «Link Manager» → «Revit Server…» — **дерево папок открывается** (это проверка блока 2;
      в 2024 до правки был бы 404)
- [ ] «Link Manager» → «BIM360…» — список хабов открывается под вошедшим пользователем
- [ ] Связь создаётся, ложится в заданный рабочий набор проекта

### 6.3 Приоритет проверок

Если времени мало, в первую очередь: **колонка «Группа»** (блок 3.3 — самое заметное
пользователю изменение), **Revit Server в 2024** (блок 2 — единственная поломка) и
**какая DLL попала в какую папку** (блок 1.2 — самая дорогая ошибка).

---

## Порядок выполнения

1. Блок 1 (сборка) — иначе нечем проверять остальное.
2. Блок 3 (устаревшие API) — тут же убедиться в 0 предупреждений на обеих версиях.
3. Блок 2 (Revit Server) — правка мелкая, но её легко забыть: компилятор молчит.
4. Блок 5 (документация).
5. Блок 6.1 (проверка сборки) и подготовка списка 6.2 для пользователя.

Отчёт в конце: что сделано, вывод обеих сборок с числом ошибок и предупреждений,
что осталось на ручную проверку в Revit.
