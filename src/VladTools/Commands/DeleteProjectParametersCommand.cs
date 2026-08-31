using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VladTools.Infrastructure;
using VladTools.UI;

namespace VladTools.Commands
{
    /// <summary>
    /// Удаляет из открытого проекта общие параметры — те, что отмечены галочкой в окне.
    /// Показывает весь список общих параметров файла: и параметры проекта (привязанные
    /// к категориям), и те, что приехали с загруженными семействами.
    ///
    /// Типовая работа: после стадии П в модели остаются сотни чужих параметров,
    /// и их надо снести пачкой по общему началу имени.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DeleteProjectParametersCommand : IExternalCommand
    {
        private const string DialogTitle = "Удалить общие параметры проекта";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData?.Application?.ActiveUIDocument;
            if (uidoc == null)
            {
                message = "Нет активного документа.";
                return Result.Cancelled;
            }

            var doc = uidoc.Document;
            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show(DialogTitle,
                    "Команда работает только в проекте.\n" +
                    "В редакторе семейств параметры удаляет кнопка «Удалить параметры».");
                return Result.Cancelled;
            }

            try
            {
                var rows = Collect(doc);

                if (rows.Count == 0)
                {
                    TaskDialog.Show(DialogTitle, "В этом проекте нет общих параметров.");
                    return Result.Cancelled;
                }

                var families = EditableFamilies(doc);

                // Сохранённую проверку применяем сразу: ради этого её и храним.
                var pending = ApplySavedScan(doc, families, rows);

                var window = new DeleteProjectParametersWindow(
                    rows,
                    families.Count,
                    pending,
                    DimensionLabelCache.SavedAt(doc.PathName),
                    force => ScanDimensionLabels(doc, families, force));

                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var deleted = new List<string>();
                var failures = new List<string>();
                var warnings = new WarningSuppressor();

                using (var transaction = new Transaction(doc, "Удалить общие параметры проекта"))
                {
                    transaction.Start();

                    // Удаление параметра тянет за собой предупреждения Revit (поля спецификаций,
                    // фильтры видов). На сотне параметров модальные окна сорвали бы пакетную
                    // работу, поэтому предупреждения гасятся и уходят в итоговый отчёт.
                    var options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(warnings);
                    transaction.SetFailureHandlingOptions(options);

                    Remove(doc, window.Selected, deleted, failures);

                    if (deleted.Count == 0)
                        transaction.RollBack();
                    else
                        transaction.Commit();
                }

                Report(deleted, failures, warnings.Messages);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        private static List<ProjectParameterRow> Collect(Document doc)
        {
            var bindings = CollectBindings(doc);

            return new FilteredElementCollector(doc)
                .OfClass(typeof(SharedParameterElement))
                .Cast<SharedParameterElement>()
                .Select(element => Describe(element, bindings))
                .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Привязки параметров проекта, разложенные по Id самого параметра: по элементу
        /// общего параметра узнать его категории напрямую нельзя, только через карту привязок.
        /// </summary>
        private static Dictionary<ElementId, ElementBinding> CollectBindings(Document doc)
        {
            var result = new Dictionary<ElementId, ElementBinding>();
            var iterator = doc.ParameterBindings.ForwardIterator();

            while (iterator.MoveNext())
            {
                var definition = iterator.Key as InternalDefinition;
                var binding = iterator.Current as ElementBinding;

                if (definition != null && binding != null)
                    result[definition.Id] = binding;
            }

            return result;
        }

        private static ProjectParameterRow Describe(
            SharedParameterElement element,
            IReadOnlyDictionary<ElementId, ElementBinding> bindings)
        {
            var definition = element.GetDefinition();

            ElementBinding binding;
            bindings.TryGetValue(element.Id, out binding);

            return new ProjectParameterRow(
                element.Id,
                definition?.Name ?? "(без имени)",
                GuidText(element),
                BindingText(binding),
                GroupName(definition),
                CategoriesText(binding),
                binding != null);
        }

        private static string GuidText(SharedParameterElement element)
        {
            try
            {
                return element.GuidValue.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string BindingText(ElementBinding binding)
        {
            if (binding == null)
                return "Нет привязки";

            return binding is InstanceBinding ? "Экземпляр" : "Тип";
        }

        private static string CategoriesText(ElementBinding binding)
        {
            if (binding?.Categories == null)
                return string.Empty;

            try
            {
                var names = binding.Categories
                    .Cast<Category>()
                    .Select(category => category.Name)
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase);

                return string.Join(", ", names);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string GroupName(Definition definition)
        {
            if (definition == null)
                return string.Empty;

            try
            {
                return LabelUtils.GetLabelFor(definition.ParameterGroup);
            }
            catch (Exception)
            {
                return definition.ParameterGroup.ToString();
            }
        }

        // ───────────────────────────── проверка семейств ─────────────────────────────

        /// <summary>
        /// Семейства, которые вообще можно открыть на редактирование: контекстные (в проекте)
        /// и нередактируемые Revit не отдаёт, и спрашивать его об этом бесполезно.
        /// </summary>
        private static List<Family> EditableFamilies(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Where(family => family.IsEditable && !family.IsInPlace)
                .OrderBy(family => family.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Проставляет строкам то, что известно из сохранённой проверки, и возвращает,
        /// сколько семейств она не покрывает — их и предложит открыть кнопка в окне.
        /// </summary>
        private static int ApplySavedScan(
            Document doc,
            IReadOnlyList<Family> families,
            IReadOnlyList<ProjectParameterRow> rows)
        {
            var saved = DimensionLabelCache.Load(doc.PathName);
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = 0;

            foreach (var family in families)
            {
                var record = Saved(saved, family);

                if (record == null)
                {
                    pending++;
                    continue;
                }

                foreach (var guid in record.ParameterGuids)
                    known.Add(guid);
            }

            foreach (var row in rows)
                row.UsedInDimensions = known.Contains(row.Guid);

            return pending;
        }

        /// <summary>
        /// Запись о семействе, которой ещё можно верить. Версия элемента у Revit меняется
        /// на сохранении и синхронизации, а не на каждой правке, поэтому перезагруженное
        /// в этом же сеансе семейство она не поймает — на такой случай в окне есть
        /// «Проверить заново», которое сюда не заглядывает.
        /// </summary>
        private static FamilyLabelRecord Saved(IReadOnlyDictionary<string, FamilyLabelRecord> saved, Family family)
        {
            var version = VersionOf(family);
            if (version.Length == 0)
                return null;

            FamilyLabelRecord record;
            return saved.TryGetValue(family.UniqueId, out record) && record.Version == version
                ? record
                : null;
        }

        /// <summary>
        /// Собирает GUID общих параметров, которыми помечены размеры загруженных семейств.
        /// Другого пути нет: метка размера живёт только внутри документа семейства,
        /// из проекта её не видно.
        ///
        /// Открываются только семейства, которых нет в сохранённой проверке или у которых
        /// сменилась версия; при <paramref name="force"/> — все подряд. Результат тут же
        /// сохраняется, чтобы в следующий раз окно открылось уже с проверкой.
        /// Семейство, которое открыть не удалось, уходит в список причин, не кэшируется
        /// и не роняет проверку.
        /// </summary>
        private static FamilyDimensionScan ScanDimensionLabels(Document doc, IReadOnlyList<Family> families, bool force)
        {
            var saved = force
                ? new Dictionary<string, FamilyLabelRecord>(StringComparer.Ordinal)
                : DimensionLabelCache.Load(doc.PathName);

            var fresh = new List<FamilyLabelRecord>();
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<string>();
            var opened = 0;
            var reused = 0;

            foreach (var family in families)
            {
                var record = Saved(saved, family);

                if (record != null)
                {
                    reused++;
                }
                else
                {
                    record = Read(family, failures);
                    if (record == null)
                        continue;

                    opened++;
                }

                fresh.Add(record);

                foreach (var guid in record.ParameterGuids)
                    guids.Add(guid);
            }

            DimensionLabelCache.Save(doc.PathName, fresh);

            return new FamilyDimensionScan(guids, opened, reused, failures);
        }

        /// <summary>Открывает семейство и читает метки его размеров; не открылось — null и строка в отказы.</summary>
        private static FamilyLabelRecord Read(Family family, List<string> failures)
        {
            Document familyDoc = null;

            try
            {
                familyDoc = family.Document.EditFamily(family);
                return new FamilyLabelRecord(family.UniqueId, VersionOf(family), CollectDimensionLabels(familyDoc));
            }
            catch (Exception exception)
            {
                failures.Add(SafeName(family) + " — " + exception.Message);
                return null;
            }
            finally
            {
                if (familyDoc != null)
                {
                    // EditFamily отдаёт независимую копию: её надо закрыть, иначе она
                    // останется висеть в памяти до конца сеанса Revit.
                    try { familyDoc.Close(false); }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// GUID общих параметров, которыми помечены размеры документа.
        /// <c>Dimension.FamilyLabel</c> у неразмечаемого размера бросает исключение вместо null.
        /// </summary>
        private static List<string> CollectDimensionLabels(Document familyDoc)
        {
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dimension in new FilteredElementCollector(familyDoc).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                try
                {
                    var label = dimension.FamilyLabel;
                    if (label != null && label.IsShared)
                        guids.Add(label.GUID.ToString());
                }
                catch (Exception)
                {
                    // Размер, который пометить нельзя, метки и не несёт.
                }
            }

            return guids.ToList();
        }

        private static string VersionOf(Family family)
        {
            try
            {
                return family.VersionGuid.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string SafeName(Family family)
        {
            try
            {
                return family.Name;
            }
            catch (Exception)
            {
                return "(семейство без имени)";
            }
        }

        // ───────────────────────────── удаление ─────────────────────────────

        /// <summary>
        /// Удаляет параметры по одному: отказ на одном не должен срывать всю пачку.
        /// Revit может унести несколько элементов за раз (параметр вместе со ссылками на него),
        /// поэтому перед удалением проверяем, жив ли элемент ещё.
        /// </summary>
        private static void Remove(
            Document doc,
            IReadOnlyList<ProjectParameterRow> rows,
            List<string> deleted,
            List<string> failures)
        {
            foreach (var row in rows)
            {
                try
                {
                    if (doc.GetElement(row.Id) == null)
                    {
                        deleted.Add(row.Name);
                        continue;
                    }

                    var removed = doc.Delete(row.Id);

                    if (removed != null && removed.Count > 0)
                        deleted.Add(row.Name);
                    else
                        failures.Add(row.Name + " — Revit не отдал параметр на удаление");
                }
                catch (Exception exception)
                {
                    failures.Add(row.Name + " — " + exception.Message);
                }
            }
        }

        private static void Report(
            IReadOnlyList<string> deleted,
            IReadOnlyList<string> failures,
            IReadOnlyList<string> warnings)
        {
            var text = deleted.Count > 0
                ? "Удалено параметров: " + deleted.Count + "."
                : "Ни один параметр не удалён.";

            if (failures.Count > 0)
            {
                const int limit = 15;
                text += "\n\nНе удалось удалить (" + failures.Count + "):\n• " +
                        string.Join("\n• ", failures.Take(limit));

                if (failures.Count > limit)
                    text += "\n… и ещё " + (failures.Count - limit);
            }

            if (warnings.Count > 0)
            {
                const int limit = 5;
                text += "\n\nПредупреждения Revit (" + warnings.Count + "):\n• " +
                        string.Join("\n• ", warnings.Take(limit));

                if (warnings.Count > limit)
                    text += "\n… и ещё " + (warnings.Count - limit);
            }

            TaskDialog.Show(DialogTitle, text);
        }
    }
}
