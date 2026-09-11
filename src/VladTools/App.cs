using Autodesk.Revit.UI;
using VladTools.Commands;
using VladTools.Infrastructure;

namespace VladTools
{
    /// <summary>
    /// The add-in's entry point: creates the tab and buttons on the Revit ribbon.
    /// </summary>
    public class App : IExternalApplication
    {
        public const string TabName = "Vlad Tools";
        private const string FamilyPanelName = "Families";
        private const string ProjectPanelName = "Project";

        public Result OnStartup(UIControlledApplication application)
        {
            // The Revit Server REST service name includes the Revit year; we take it from Revit
            // itself, so the same build does not confuse 2022 and 2024 when talking to the server.
            RevitServerClient.ServiceVersion = application.ControlledApplication.VersionNumber;

            // The schedule cache is a .rvt, and a .rvt never opens in an older Revit than the one
            // that wrote it — so every year keeps its sets in a folder of its own.
            ScheduleLibrary.Year = application.ControlledApplication.VersionNumber;

            var familyPanel = Ribbon.GetOrCreatePanel(application, TabName, FamilyPanelName);

            // ───────────────── Button 1: 3D Thumbnail ─────────────────
            Ribbon.AddPushButton(
                familyPanel,
                name: "VladTools_Thumbnail3D",
                text: "3D\nThumbnail",
                commandType: typeof(Create3DThumbnailCommand),
                tooltip: "Creates a \"" + Create3DThumbnailCommand.ViewName + "\" 3D view in the current family.",
                longDescription:
                    "The view is configured automatically:\n" +
                    "• annotations off;\n" +
                    "• connectors hidden;\n" +
                    "• graphics style — \"Realistic\";\n" +
                    "• detail level — \"Fine\".\n\n" +
                    "If such a view already exists, the settings are reapplied to it.",
                iconBaseName: "thumbnail3d");

            // ───────────────── Button 2: Delete Parameters ─────────────────
            Ribbon.AddPushButton(
                familyPanel,
                name: "VladTools_DeleteSharedParameters",
                text: "Delete\nParameters",
                commandType: typeof(DeleteSharedParametersCommand),
                tooltip: "Deletes the checked shared parameters from the open family.",
                longDescription:
                    "Shows every shared parameter of the family with a check box on the left of each.\n" +
                    "The box can be checked by hand (all at once from the header), or by a rule:\n" +
                    "\"Match parameters starting with\" or \"…containing\" plus a string, \"SP\" say.\n" +
                    "The rule works like a search: matches stay in the table and get checked,\n" +
                    "the rest leave it. \"Invert the search\" flips the rule —\n" +
                    "everything except the matches stays. The check marks can then be edited by hand.\n\n" +
                    "Parameters that label dimensions are left out of the list: deleting one means\n" +
                    "dropping the label and breaking the parametrics. They can be shown by clearing\n" +
                    "\"Hide parameters used on dimensions\".\n\n" +
                    "Only the open family document itself is affected, not nested families.",
                iconBaseName: "deleteparams");

            // ───────────────── Button 3: Add Formulas ─────────────────
            Ribbon.AddPushButton(
                familyPanel,
                name: "VladTools_AddFormulas",
                text: "Add\nFormulas",
                commandType: typeof(AddFormulasCommand),
                tooltip: "Assigns formulas from a saved list to the parameters of the open family.",
                longDescription:
                    "The window opens already filled in: the first time, with the default formulas\n" +
                    "(ADSK_Diameter and ADSK_Mass), afterwards with the list the user has built up.\n" +
                    "The list is stored in the Windows profile and offered in every family that follows.\n\n" +
                    "Every row is validated against the open family: if a formula's parameter is\n" +
                    "missing, the \"Status\" column reads \"Parameter … not found\" and that formula is\n" +
                    "not applied. Every checked row is applied as one batch.",
                iconBaseName: "formulas");

            // ───────────────── Button 4: Rename Nested ─────────────────
            Ribbon.AddPushButton(
                familyPanel,
                name: "VladTools_RenameNested",
                text: "Rename\nNested",
                commandType: typeof(RenameNestedFamiliesCommand),
                tooltip: "Batch-renames the nested families of the open family.",
                longDescription:
                    "The window works like \"Find and Replace\" in Excel: type \"DN\" into \"Find\" and\n" +
                    "\"DIA\" into \"Replace with\", and the \"New name\" column shows right away what every\n" +
                    "nested family will become. A prefix and a suffix can be added on top, and any\n" +
                    "\"New name\" cell can be fixed by hand.\n\n" +
                    "On the right — the name buffer: name fragments that repeat often (\"(FT)-FL_GOST…\",\n" +
                    "say) are saved to the Windows profile and pasted into the fields in the next family.\n\n" +
                    "Types of the nested families can be renamed too — pick the list in the \"Show\"\n" +
                    "field. The name of the open family itself is never changed.",
                iconBaseName: "renamenested");

            // ──────── Add the next family buttons here, following the same pattern ────────

            var projectPanel = Ribbon.GetOrCreatePanel(application, TabName, ProjectPanelName);

            // ───────────────── Button 5: Delete Project Shared Parameters ─────────────────
            Ribbon.AddPushButton(
                projectPanel,
                name: "VladTools_DeleteProjectParameters",
                text: "Delete Shared\nParameters",
                commandType: typeof(DeleteProjectParametersCommand),
                tooltip: "Deletes the checked shared parameters from the open project.",
                longDescription:
                    "For models that arrived from the DD stage: hundreds of somebody else's shared\n" +
                    "parameters are swept away in a batch.\n\n" +
                    "Shows every shared parameter in the file with a check box on the left of each.\n" +
                    "The \"Show\" field splits them into project parameters (bound to categories)\n" +
                    "and unbound ones — the parameters that arrived with loaded families.\n\n" +
                    "The box can be checked by hand (all at once from the header), or by a rule:\n" +
                    "\"Match parameters starting with\" or \"…containing\" plus a string, \"SP\" say.\n" +
                    "The rule works like a search: matches stay in the table and get checked,\n" +
                    "the rest leave it. \"Invert the search\" flips the rule —\n" +
                    "everything except the matches stays. Only what is shown in the table gets deleted.\n\n" +
                    "The \"Scan Families\" button finds the parameters that label dimensions inside the\n" +
                    "loaded families and removes them from the list — so the families do not break.\n" +
                    "The scan opens every family and takes minutes on a large model,\n" +
                    "so it never starts on its own; the result is saved in the Windows profile,\n" +
                    "so from then on only new and changed families are opened.\n\n" +
                    "Along with a parameter, its values on every project element disappear too.",
                iconBaseName: "deleteprojectparams");

            // ───────────────── Button 6: Cleanup (project) ─────────────────
            Ribbon.AddPushButton(
                projectPanel,
                name: "VladTools_Cleanup",
                text: "Cleanup",
                commandType: typeof(CleanupCommand),
                tooltip: "Removes checked items from the open project: sheets, views, filters, groups, unused families.",
                longDescription:
                    "For models needed only as geometry: someone else's presentation is stripped off\n" +
                    "in one go rather than one browser node at a time.\n\n" +
                    "The window offers eight items, each with its own check box and the number found\n" +
                    "in this model; \"Select all\" checks the ones that have something to remove:\n" +
                    "• unused families — loaded families and types with no reference at all;\n" +
                    "• every sheet — together with its viewports and titleblocks, the views themselves stay;\n" +
                    "• every filter — both view filters and selection filters;\n" +
                    "• every view — plans, sections, elevations, 3D, callouts, drafting views;\n" +
                    "• every legend;\n" +
                    "• every schedule;\n" +
                    "• model groups — ungrouped, the elements stay where they are;\n" +
                    "• unused groups — group types not placed in the model.\n\n" +
                    "The active view and view templates are left alone. Everything checked runs as one\n" +
                    "operation, so it rolls back with a single Ctrl+Z; saving the project before\n" +
                    "cleaning up is still worth doing.",
                iconBaseName: "cleanup");

            // ───────────────── Button 7: Link Manager (project) ─────────────────
            Ribbon.AddPushButton(
                projectPanel,
                name: "VladTools_LinkManager",
                text: "Link\nManager",
                commandType: typeof(LinkManagerCommand),
                tooltip: "Links a whole batch of models to the project at once: files, Revit Server and BIM360.",
                longDescription:
                    "In Revit itself every link goes in through its own dialog, and each time the same\n" +
                    "placement is chosen again and the same workset boxes are cleared again.\n" +
                    "Here the list is gathered as a whole, and the setup is done once for the whole batch.\n\n" +
                    "Models are added four ways:\n" +
                    "• \"Files…\" — ordinary .rvt files from disk or a network folder;\n" +
                    "• \"Revit Server…\" — a folder tree with a check box on every model;\n" +
                    "• \"BIM360…\" — Autodesk Docs accounts, projects and folders; the sign-in\n" +
                    "  comes from Revit itself, no separate login needed;\n" +
                    "• \"BIM360 by GUID…\" — when the cloud cannot be reached.\n\n" +
                    "The gathered list can be saved as a set in the Windows profile and offered\n" +
                    "in full in the next project.\n\n" +
                    "The placement (\"By shared coordinates\" and the rest) is chosen once for all links.\n" +
                    "The worksets inside the links are checked by name — also for all of them at once:\n" +
                    "\"00_Shared levels and grids\" is closed in every link with one check box,\n" +
                    "and a rule like \"00_\" catches it even where it is named differently.\n\n" +
                    "The project's own workset a link goes into is chosen per link —\n" +
                    "a column in the table; \"Set for checked\" applies one workset to several at once.\n\n" +
                    "Links already in the project show up in the list and can be reloaded\n" +
                    "with a new workset setup; Revit will not let their placement be changed.",
                iconBaseName: "linkmanager");

            // ───────────────── Button 8: Base File (project) ─────────────────
            Ribbon.AddPushButton(
                projectPanel,
                name: "VladTools_BaseFile",
                text: "Base\nFile",
                commandType: typeof(BaseFileCommand),
                tooltip: "Links the coordination file and immediately sets up the project against it.",
                longDescription:
                    "What every discipline model starts with: link the base file, acquire shared\n" +
                    "coordinates from it, name the site, pin the link and switch to the workset\n" +
                    "the levels and grids will go into. In Revit that is five commands scattered\n" +
                    "across the ribbon, and the order between them matters.\n\n" +
                    "The model is chosen the same way as in \"Link Manager\": a file from disk, Revit\n" +
                    "Server, BIM360, or a pair of GUIDs. The chosen model and every setting are\n" +
                    "remembered in the Windows profile and offered again in the next discipline.\n\n" +
                    "If the \"00_Shared levels and grids\" workset does not exist yet, the command creates it.\n\n" +
                    "The button does not do the actual copy-monitoring of levels and grids: the Revit\n" +
                    "API cannot create monitoring links. As its last step it opens\n" +
                    "\"Copy/Monitor → Select Link\" — all that is left is picking the link and the elements.",
                iconBaseName: "basefile");

            // ───────────────── Button 9: Auto Dimensions (project) ─────────────────
            Ribbon.AddPushButton(
                projectPanel,
                name: "VladTools_AutoDimension",
                text: "Auto\nDimensions",
                commandType: typeof(AutoDimensionCommand),
                tooltip: "Places along the sides of the selected rooms the same dimension chains you placed by hand.",
                longDescription:
                    "For architectural masonry plans: place a few dimension chains along one wall by hand once " +
                    "(overall, openings, opening centres, partitions — whatever is needed), select the rooms and " +
                    "press the button — the same set appears along every one of their sides.\n\n" +
                    "The \"Take a sample…\" button in the window parses the dimensions you selected on its own: " +
                    "it works out the kind of each chain and its offset from the wall. That is a guess, not an " +
                    "exact calculation — the result can always be fixed in the table, or a chain added or removed.\n\n" +
                    "The set of chains can be saved as a template and carried into another project.\n\n" +
                    "Only the active floor plan of the open project is affected; links are left alone.",
                iconBaseName: "autodim");

            // ───────────────── Button 10: Accept Changes (project) ─────────────────
            Ribbon.AddPushButton(
                projectPanel,
                name: "VladTools_AcceptCoordination",
                text: "Accept\nChanges",
                commandType: typeof(AcceptCoordinationCommand),
                tooltip: "Accepts coordination changes: puts the project's grids and levels in line with the base file.",
                longDescription:
                    "After a new base-file issue, \"Coordination Review\" shows a list of grids and\n" +
                    "levels that moved, and each is accepted one at a time: expand a node, pick an\n" +
                    "action, repeat. Here the whole list is accepted with one button.\n\n" +
                    "The window shows exactly what differs: for a grid, the shift and the rotation;\n" +
                    "for a level, the old and the new elevation; a rename shows as a row of its own.\n" +
                    "Checked items are applied as one operation and roll back with a single Ctrl+Z.\n\n" +
                    "Grids and levels missing from the base file, and new ones in it, are shown but\n" +
                    "never touched: deleting a level means taking everything standing on it with it,\n" +
                    "and the Revit API does not allow setting up monitoring on a new element at all.\n\n" +
                    "It works off monitoring links — the ones \"Copy/Monitor\" sets up.\n" +
                    "If the project has none, there is nothing to compare against.",
                iconBaseName: "coordination");

            // ───────────────── Button 11: Schedule Library (project) ─────────────────
            Ribbon.AddPushButton(
                projectPanel,
                name: "VladTools_ScheduleLibrary",
                text: "Schedule\nLibrary",
                commandType: typeof(ScheduleLibraryCommand),
                tooltip: "Carries schedules out of a base model into every model after it.",
                longDescription:
                    "The schedules a discipline works by are drawn once and then dragged into every new\n" +
                    "model through \"Insert Views from File\": find the base model on the network again,\n" +
                    "walk the dialog again, pick the same three schedules out of its whole list again.\n\n" +
                    "Here the list is assembled once and named. The set is kept in the Windows profile\n" +
                    "as a small Revit file, so inserting takes a second and the base model is not needed\n" +
                    "for it at all — the set can even be handed to a colleague.\n\n" +
                    "The set is filled from the model open right now (the fastest way — nothing has to\n" +
                    "be opened), or from a file, Revit Server or BIM360; those are opened in the\n" +
                    "background, and a large model takes a while.\n\n" +
                    "A schedule arrives whole: fields, filters, sorting and formatting. Where the project\n" +
                    "already holds a schedule of that name, the table asks what to do with it —\n" +
                    "skip, replace (the replacement goes back onto the same sheets), or insert alongside\n" +
                    "under a free name. Everything checked goes in as one operation and rolls back\n" +
                    "with a single Ctrl+Z.",
                iconBaseName: "schedules");

            // ───────────────── Button 12: Parameter Sets (project) ─────────────────
            Ribbon.AddPushButton(
                projectPanel,
                name: "VladTools_ParameterSets",
                text: "Parameter\nSets",
                commandType: typeof(ParameterSetCommand),
                tooltip: "Applies a saved bundle of shared parameters to the open project.",
                longDescription:
                    "The office's own shared parameters — a mass, a code, a status — have to be present in\n" +
                    "every project with exactly the same settings, and setting each one up by hand in\n" +
                    "\"Manage → Project Parameters\" one project at a time is easy to get slightly wrong.\n\n" +
                    "Here a bundle is built once, from a shared parameter file: for every parameter, whether\n" +
                    "it is bound per instance or per type, which categories, which parameter group, and —\n" +
                    "for an instance parameter — whether its value can vary between the instances of a model\n" +
                    "group. The bundle is named and kept in the Windows profile, offered in full next time.\n\n" +
                    "Opening the window in another project shows the same bundle and checks it against what\n" +
                    "is already there: a parameter missing from the project is added, one already bound is\n" +
                    "compared and brought in line with the saved settings rather than duplicated. Everything\n" +
                    "checked is applied as one operation, so it rolls back with a single Ctrl+Z.",
                iconBaseName: "parametersets");

            // ──────── Add the next project buttons here ────────

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
