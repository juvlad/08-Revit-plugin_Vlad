using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The open model itself as a hint: what it is called and which folder it lies in.
    ///
    /// Needed by the "Building Kit" and by nothing else: the building number comes from the open
    /// model's name, and the discipline folders are looked for next to it. Everything may turn out
    /// empty — a project may never have been saved — and that is not a breakage: then the user supplies the building and the folder.
    /// </summary>
    internal sealed class HostModel
    {
        public HostModel(LinkEntry entry, ModelFolder folder, string name)
        {
            Entry = entry;
            Folder = folder;
            Name = name ?? string.Empty;
        }

        /// <summary>The description of the model itself — it is what tells it apart from the offered links.</summary>
        public LinkEntry Entry { get; }

        /// <summary>The folder the model lies in; null for a project that was never saved.</summary>
        public ModelFolder Folder { get; }

        /// <summary>The model name with or without an extension — exactly as Revit reported it.</summary>
        public string Name { get; }

        /// <summary>The key of the model itself: Revit will not allow linking to oneself anyway.</summary>
        public string Key => Entry == null ? string.Empty : Entry.Key;
    }

    /// <summary>
    /// Everything worth knowing about the open project's links and worksets: what is already there,
    /// where a new link can be put, and how to turn a model description into a Revit path.
    ///
    /// Extracted from <c>LinkManagerCommand</c> once the "Base File" button needed the same thing:
    /// both commands create links, and both need the same translation from a "workset name" to a
    /// <c>WorksetId</c> and from a set entry to a <c>ModelPath</c>.
    /// </summary>
    internal static class LinkCatalog
    {
        /// <summary>
        /// The links already in the project. Nested ones are left out: they arrive together with their
        /// host, and cannot be loaded separately.
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

                // We show the workset the link is in right now: the user has to see what to change
                // rather than choose blindly. We look at the instance — that is what stands in the model.
                entry.Workset = WorksetName(doc, Instances(doc, type.Id).FirstOrDefault() ?? (Element)type);

                rows.Add(new LinkRow(entry, type.Id));
            }

            return rows;
        }

        /// <summary>
        /// Where the open project lies and what it is called. For a workshared one the central model
        /// path is taken rather than the local copy's: the discipline folders sit next to the central
        /// model, while the local one lives on the user's disk and has nothing to do with it.
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
                // Nothing was worked out — the window will simply ask the user for the building and the folder.
                return new HostModel(null, null, doc.Title);
            }
        }

        /// <summary>
        /// A cloud model: the pair of GUIDs is enough to identify it, and the folder comes from
        /// <c>GetCloudFolderId</c> — the very id Data Management knows it by.
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
                // Revit does not always report the folder; that does not cancel the building from the name.
            }

            return new HostModel(entry, folder, doc.Title);
        }

        /// <summary>The central model path, or the file's own path when there is none.</summary>
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
                    // Not workshared with a central model, or detached — the file path is what is left.
                }
            }

            return doc.PathName ?? string.Empty;
        }

        /// <summary>The instances of a given link type — a project may hold several.</summary>
        public static List<RevitLinkInstance> Instances(Document doc, ElementId typeId)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .Where(instance => instance.GetTypeId() == typeId)
                .ToList();
        }

        /// <summary>
        /// The open project's worksets — the ones a link can be put into.
        /// A non-workshared project has no worksets at all, and the window hides the whole column.
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

        /// <summary>The name of the workset holding the element; an empty string in a non-workshared project.</summary>
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

        /// <summary>Project workset names to their ids — the window picks by name.</summary>
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
        /// Puts an element into a project workset. A failure must not derail the load: the link is
        /// already created and working, it just lies in the wrong place — so it goes into the report as a line.
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
                    failures.Add(what + " — the workset cannot be changed: the parameter is unavailable");
                    return false;
                }

                parameter.Set(workset.IntegerValue);
                return true;
            }
            catch (Exception exception)
            {
                failures.Add(what + " — the workset could not be changed: " + Short(exception.Message));
                return false;
            }
        }

        /// <summary>
        /// Where a link came from. A cloud one is recognised by its path alone: it has a region and a
        /// pair of GUIDs and nothing more — an ordinary path does not exist for it.
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
                // The path is unavailable — the link simply will not make the list; no reason not to open the window.
                return null;
            }
        }

        /// <summary>
        /// The path to the model. A cloud model has no ordinary path at all: it is addressed by a region
        /// and a pair of GUIDs, and that is the only way to reach it.
        /// </summary>
        public static ModelPath ToModelPath(LinkEntry entry)
        {
            if (entry.Origin != LinkOrigin.Cloud)
                return ModelPathUtils.ConvertUserVisiblePathToModelPath(entry.Path);

            Guid project;
            Guid model;

            if (!Guid.TryParse(entry.ProjectGuid, out project) || !Guid.TryParse(entry.ModelGuid, out model))
                throw new InvalidOperationException("The cloud model GUID is written incorrectly.");

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

        /// <summary>A Revit failure code in words: on its own it tells the user nothing.</summary>
        public static string Describe(LinkLoadResultType result)
        {
            switch (result)
            {
                case LinkLoadResultType.LinkNotFound:
                    return "file not found";
                case LinkLoadResultType.LinkNotOpenable:
                    return "the file will not open: corrupt or in use";
                case LinkLoadResultType.LinkOpenAsHost:
                    return "this file is already open as a project";
                case LinkLoadResultType.SameModelAsHost:
                case LinkLoadResultType.SameCentralModelAsHost:
                    return "this is the open project itself";
                case LinkLoadResultType.LinkExists:
                    return "such a link is already in the project";
                case LinkLoadResultType.ExternalServerMissing:
                    return "the server is unreachable";
                case LinkLoadResultType.LinkNotLoadedOtherError:
                    return "Revit could not load the link";
                default:
                    return "the load failed (" + result + ")";
            }
        }

        /// <summary>Revit messages sometimes run to several paragraphs — a list needs a single line.</summary>
        public static string Short(string message)
        {
            var text = (message ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 160 ? text.Substring(0, 160) + "…" : text;
        }
    }
}
