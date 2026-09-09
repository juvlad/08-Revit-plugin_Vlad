using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Сама открытая модель как подсказка: как она названа и в какой папке лежит.
    ///
    /// Нужна «Комплекту по корпусу» и только ему: номер корпуса берётся из имени открытой
    /// модели, а папки разделов ищутся рядом с ней. Всё может оказаться пустым — проект
    /// бывает ни разу не сохранён, — и это не поломка: тогда корпус и папку задаёт человек.
    /// </summary>
    internal sealed class HostModel
    {
        public HostModel(LinkEntry entry, ModelFolder folder, string name)
        {
            Entry = entry;
            Folder = folder;
            Name = name ?? string.Empty;
        }

        /// <summary>Описание самой модели — по нему её отличают от предложенных связей.</summary>
        public LinkEntry Entry { get; }

        /// <summary>Папка, в которой модель лежит; у несохранённого проекта — null.</summary>
        public ModelFolder Folder { get; }

        /// <summary>Имя модели с расширением или без — так, как его отдал Revit.</summary>
        public string Name { get; }

        /// <summary>Ключ самой модели: предлагать связаться с самим собой Revit всё равно не даст.</summary>
        public string Key => Entry == null ? string.Empty : Entry.Key;
    }

    /// <summary>
    /// Всё, что нужно знать про связи открытого проекта и его рабочие наборы: что уже стоит,
    /// куда можно положить новую связь, как перевести описание модели в путь Revit.
    ///
    /// Вынесено из <c>LinkManagerCommand</c>, когда то же самое понадобилось кнопке
    /// «Базовый файл»: обе команды заводят связи, и обеим нужен один и тот же перевод
    /// «строка набора → <c>WorksetId</c>» и «запись набора → <c>ModelPath</c>».
    /// </summary>
    internal static class LinkCatalog
    {
        /// <summary>
        /// Связи, уже стоящие в проекте. Вложенные не берутся: они приезжают вместе
        /// со своим носителем, и грузить их отдельно нельзя.
        /// </summary>
        public static IReadOnlyList<LinkRow> Existing(Document doc)
        {
            var rows = new List<LinkRow>();

            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .Where(type => !type.IsNestedLink)
                .OrderBy(type => type.Name, StringComparer.CurrentCultureIgnoreCase);

            foreach (var type in types)
            {
                var entry = Describe(doc, type);
                if (entry == null)
                    continue;

                // Набор показываем тот, в котором связь лежит сейчас: пользователь должен видеть,
                // что менять, а не выбирать вслепую. Смотрим по экземпляру — именно он стоит в модели.
                entry.Workset = WorksetName(doc, Instances(doc, type.Id).FirstOrDefault() ?? (Element)type);

                rows.Add(new LinkRow(entry, type.Id));
            }

            return rows;
        }

        /// <summary>
        /// Где лежит и как называется сам открытый проект. У совмещённого берётся путь
        /// центральной модели, а не локальной копии: папки разделов стоят рядом с центральной,
        /// а локальная лежит у пользователя на диске и к делу отношения не имеет.
        /// </summary>
        public static HostModel Host(Document doc)
        {
            try
            {
                if (doc.IsModelInCloud)
                    return CloudHost(doc);

                var visible = VisiblePath(doc);
                if (visible.Length == 0)
                    return new HostModel(null, null, doc.Title);

                if (visible.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase))
                {
                    string server;
                    string folderPath;
                    string modelName;
                    RevitServerClient.TryParse(visible, out server, out folderPath, out modelName);

                    return new HostModel(
                        LinkEntry.ForServer(visible),
                        ModelFolder.ForServer(server, folderPath),
                        modelName);
                }

                var directory = System.IO.Path.GetDirectoryName(visible);

                return new HostModel(
                    LinkEntry.ForFile(visible),
                    string.IsNullOrEmpty(directory) ? null : ModelFolder.ForFile(directory),
                    System.IO.Path.GetFileName(visible));
            }
            catch (Exception)
            {
                // Ничего не выяснили — окно просто спросит корпус и папку у человека.
                return new HostModel(null, null, doc.Title);
            }
        }

        /// <summary>
        /// Облачная модель: пары GUID хватает, чтобы её опознать, а папку отдаёт
        /// <c>GetCloudFolderId</c> — тем же идентификатором, каким её знает Data Management.
        /// </summary>
        private static HostModel CloudHost(Document doc)
        {
            var path = doc.GetCloudModelPath();
            var project = path.GetProjectGUID().ToString();

            var entry = LinkEntry.ForCloud(path.Region, project, path.GetModelGUID().ToString(), doc.Title);
            ModelFolder folder = null;

            try
            {
                var folderId = doc.GetCloudFolderId(false);
                if (!string.IsNullOrEmpty(folderId))
                    folder = ModelFolder.ForCloud(path.Region, AccClient.ProjectId(project), folderId, string.Empty);
            }
            catch (Exception)
            {
                // Папку Revit отдаёт не всегда; корпус из имени это не отменяет.
            }

            return new HostModel(entry, folder, doc.Title);
        }

        /// <summary>Путь центральной модели, а при её отсутствии — путь самого файла.</summary>
        private static string VisiblePath(Document doc)
        {
            if (doc.IsWorkshared)
            {
                try
                {
                    var central = doc.GetWorksharingCentralModelPath();
                    if (central != null && !central.Empty)
                        return ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                }
                catch (Exception)
                {
                    // Не совмещённая с центральной или отсоединённая — остаётся путь файла.
                }
            }

            return doc.PathName ?? string.Empty;
        }

        /// <summary>Экземпляры связи данного типа — их в проекте может быть несколько.</summary>
        public static List<RevitLinkInstance> Instances(Document doc, ElementId typeId)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .Where(instance => instance.GetTypeId() == typeId)
                .ToList();
        }

        /// <summary>
        /// Рабочие наборы открытого проекта — те, куда можно положить связь.
        /// Проект не совмещённый — наборов нет вовсе, и окно прячет весь столбец.
        /// </summary>
        public static IReadOnlyList<string> HostWorksets(Document doc)
        {
            if (!doc.IsWorkshared)
                return new List<string>();

            return new FilteredWorksetCollector(doc)
                .OfKind(WorksetKind.UserWorkset)
                .ToWorksets()
                .Select(workset => workset.Name)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>Имя набора, в котором лежит элемент; в несовмещённом проекте — пустая строка.</summary>
        public static string WorksetName(Document doc, Element element)
        {
            if (element == null || !doc.IsWorkshared)
                return string.Empty;

            try
            {
                var workset = doc.GetWorksetTable().GetWorkset(element.WorksetId);
                return workset == null || workset.Kind != WorksetKind.UserWorkset ? string.Empty : workset.Name;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>Имена наборов проекта в их идентификаторы — по имени окно и выбирает.</summary>
        public static Dictionary<string, WorksetId> WorksetIds(Document doc)
        {
            var map = new Dictionary<string, WorksetId>(StringComparer.CurrentCultureIgnoreCase);

            if (!doc.IsWorkshared)
                return map;

            foreach (var workset in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets())
                map[workset.Name] = workset.Id;

            return map;
        }

        /// <summary>
        /// Кладёт элемент в рабочий набор проекта. Отказ не должен срывать загрузку: связь уже
        /// создана и работает, просто лежит не там, — поэтому он уходит строкой в отчёт.
        /// </summary>
        public static bool Place(Element element, WorksetId workset, List<string> failures, string what)
        {
            try
            {
                if (element == null || element.WorksetId == workset)
                    return false;

                var parameter = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (parameter == null || parameter.IsReadOnly)
                {
                    failures.Add(what + " — рабочий набор сменить нельзя: параметр недоступен");
                    return false;
                }

                parameter.Set(workset.IntegerValue);
                return true;
            }
            catch (Exception exception)
            {
                failures.Add(what + " — рабочий набор сменить не удалось: " + Short(exception.Message));
                return false;
            }
        }

        /// <summary>
        /// Откуда приехала связь. Облачную узнаём по самому пути: у него есть регион
        /// и пара GUID, и больше ничего — обычного пути у неё не существует.
        /// </summary>
        public static LinkEntry Describe(Document doc, RevitLinkType type)
        {
            try
            {
                var reference = ExternalFileUtils.GetExternalFileReference(doc, type.Id);
                var path = reference.GetAbsolutePath();

                if (path.CloudPath)
                {
                    return LinkEntry.ForCloud(
                        path.Region,
                        path.GetProjectGUID().ToString(),
                        path.GetModelGUID().ToString(),
                        type.Name);
                }

                var visible = ModelPathUtils.ConvertModelPathToUserVisiblePath(path);

                return visible.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase)
                    ? LinkEntry.ForServer(visible)
                    : LinkEntry.ForFile(visible);
            }
            catch (Exception)
            {
                // Путь недоступен — связь просто не попадёт в список; это не повод не открывать окно.
                return null;
            }
        }

        /// <summary>
        /// Путь к модели. У облачной модели обычного пути нет вовсе: она адресуется
        /// регионом и парой GUID, и это единственный способ до неё добраться.
        /// </summary>
        public static ModelPath ToModelPath(LinkEntry entry)
        {
            if (entry.Origin != LinkOrigin.Cloud)
                return ModelPathUtils.ConvertUserVisiblePathToModelPath(entry.Path);

            Guid project;
            Guid model;

            if (!Guid.TryParse(entry.ProjectGuid, out project) || !Guid.TryParse(entry.ModelGuid, out model))
                throw new InvalidOperationException("GUID облачной модели записан неверно.");

            return ModelPathUtils.ConvertCloudGUIDsToCloudPath(entry.Region, project, model);
        }

        public static ImportPlacement Placement(LinkPlacement placement)
        {
            switch (placement)
            {
                case LinkPlacement.Origin:
                    return ImportPlacement.Origin;
                case LinkPlacement.Centered:
                    return ImportPlacement.Centered;
                case LinkPlacement.Site:
                    return ImportPlacement.Site;
                default:
                    return ImportPlacement.Shared;
            }
        }

        /// <summary>Код отказа Revit словами: сам по себе он пользователю ничего не говорит.</summary>
        public static string Describe(LinkLoadResultType result)
        {
            switch (result)
            {
                case LinkLoadResultType.LinkNotFound:
                    return "файл не найден";
                case LinkLoadResultType.LinkNotOpenable:
                    return "файл не открывается: повреждён или занят";
                case LinkLoadResultType.LinkOpenAsHost:
                    return "этот файл уже открыт как проект";
                case LinkLoadResultType.SameModelAsHost:
                case LinkLoadResultType.SameCentralModelAsHost:
                    return "это сам открытый проект";
                case LinkLoadResultType.LinkExists:
                    return "такая связь в проекте уже есть";
                case LinkLoadResultType.ExternalServerMissing:
                    return "сервер недоступен";
                case LinkLoadResultType.LinkNotLoadedOtherError:
                    return "Revit не смог загрузить связь";
                default:
                    return "загрузка не удалась (" + result + ")";
            }
        }

        /// <summary>Сообщения Revit бывают в несколько абзацев — в списке нужна одна строка.</summary>
        public static string Short(string message)
        {
            var text = (message ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 160 ? text.Substring(0, 160) + "…" : text;
        }
    }
}
