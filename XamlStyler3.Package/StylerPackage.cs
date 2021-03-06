﻿using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Xavalon.XamlStyler.Core;
using Xavalon.XamlStyler.Core.Options;
using Xavalon.XamlStyler.Package;
using Task = System.Threading.Tasks.Task;

namespace Xavalon.XamlStyler3.Package
{
    [ProvideLoadKey("Standard", "2.1", "XAML Styler", "Xavalon", 104)]
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#1110", "#1112", "1.0", IconResourceID = 1400)] // Info on this package for Help/About
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(Guids.XamlStylerPackageGuidString)]
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1650:ElementDocumentationMustBeSpelledCorrectly", Justification = "pkgdef, VS and vsixmanifest are valid VS terms")]
    [ProvideService(typeof(StylerService), IsAsyncQueryable = true)]
    [ProvideOptionPage(typeof(PackageOptions), "XAML Styler", "General", 101, 106, true)]
    [ProvideProfile(typeof(PackageOptions), "XAML Styler", "XAML Styler Settings", 106, 107, true, DescriptionResourceID = 108)]
    [ProvideAutoLoad(Guids.UIContextGuidString, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideUIContextRule(Guids.UIContextGuidString, name: "XAML load", expression: "Dotxaml", termNames: new[] { "Dotxaml" }, termValues: new[] { "HierSingleSelectionName:.xaml$" })]
    public sealed class StylerPackage : AsyncPackage
    {
        private DTE _dte;
        private Events _events;
        private CommandEvents _fileSaveAll;
        private CommandEvents _fileSaveSelectedItems;
        private IVsUIShell _uiShell;
        
        public StylerPackage()
        {
            Trace.WriteLine(string.Format(CultureInfo.CurrentCulture, "Entering constructor for: {0}", ToString()));
        }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Trace.WriteLine(string.Format(CultureInfo.CurrentCulture, "Entering Initialize() of: {0}", ToString()));
            base.Initialize();

            _dte = await GetServiceAsync(typeof(DTE)) as DTE;
            Assumes.Present(_dte);

            if (_dte == null)
            {
                throw new NullReferenceException("DTE is null");
            }

            _uiShell = await GetServiceAsync(typeof(IVsUIShell)) as IVsUIShell;
            Assumes.Present(_uiShell);

            // Initialize command events listeners
            _events = _dte.Events;

            // File.SaveSelectedItems command
            _fileSaveSelectedItems = _events.CommandEvents["{5EFC7975-14BC-11CF-9B2B-00AA00573819}", 331];
            _fileSaveSelectedItems.BeforeExecute +=
                OnFileSaveSelectedItemsBeforeExecute;

            // File.SaveAll command
            _fileSaveAll = _events.CommandEvents["{5EFC7975-14BC-11CF-9B2B-00AA00573819}", 224];
            _fileSaveAll.BeforeExecute +=
                OnFileSaveAllBeforeExecute;

            //Initialize menu command
            // Add our command handlers for menu (commands must exist in the .vsct file)
            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService menuCommandService)
            {
                // Create the command for the menu item.
                var menuCommandId = new CommandID(Guids.CommandSetGuid, (int)PackageCommandIds.FormatXamlCommandId);
                var menuItem = new MenuCommand(MenuItemCallback, menuCommandId);
                menuCommandService.AddCommand(menuItem);
            }
        }

        private bool IsFormatableDocument(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var isFormatableDocument = !document.ReadOnly && document.Language == "XAML";

            if (!isFormatableDocument)
            {
                //xamarin
                isFormatableDocument = document.Language == "XML" && document.FullName.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
            }

            return isFormatableDocument;
        }

        private void OnFileSaveSelectedItemsBeforeExecute(string guid, int id, object customIn, object customOut,
                                                          ref bool cancelDefault)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Document document = _dte.ActiveDocument;

            if (IsFormatableDocument(document))
            {
                var options = GetDialogPage(typeof(PackageOptions)).AutomationObject as IStylerOptions;

                if (options.BeautifyOnSave)
                {
                    Execute(document);
                }
            }
        }

        private void OnFileSaveAllBeforeExecute(string guid, int id, object customIn, object customOut,
                                                ref bool cancelDefault)
        {
            // use parallel processing, but only on the documents that are formatable
            // (to avoid the overhead of Task creating when it's not necessary)
            ThreadHelper.ThrowIfNotOnUIThread();
            List<Document> docs = new List<Document>();
            foreach (Document document in _dte.Documents)
            {
                if (IsFormatableDocument(document))
                {
                    docs.Add(document);
                }
            }

            Parallel.ForEach(docs, document =>
            {
                var options = GetDialogPage(typeof(PackageOptions)).AutomationObject as IStylerOptions;

                if (options.BeautifyOnSave)
                {
                    Execute(document);
                }
            }
                );
        }

        private void Execute(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!IsFormatableDocument(document))
            {
                return;
            }

            Properties xamlEditorProps = _dte.Properties["TextEditor", "XAML"];

            var stylerOptions = GetDialogPage(typeof(PackageOptions)).AutomationObject as IStylerOptions;

            var solutionPath = String.IsNullOrEmpty(_dte.Solution?.FullName)
                ? String.Empty
                : (stylerOptions.SearchToDriveRoot ? Path.GetPathRoot(_dte.Solution.FullName) : Path.GetDirectoryName(_dte.Solution.FullName));
            var project = _dte.ActiveDocument?.ProjectItem?.ContainingProject;

            var configPath = GetConfigPathForItem(document.Path, solutionPath, project);

            if (configPath != null)
            {
                stylerOptions = ((StylerOptions)stylerOptions).Clone();
                stylerOptions.ConfigPath = configPath;
            }

            if (stylerOptions.UseVisualStudioIndentSize)
            {
                if (Int32.TryParse(xamlEditorProps.Item("IndentSize").Value.ToString(), out int outIndentSize)
                    && (outIndentSize > 0))
                {
                    stylerOptions.IndentSize = outIndentSize;
                }
            }

            stylerOptions.IndentWithTabs = (bool)xamlEditorProps.Item("InsertTabs").Value;

            StylerService styler = new StylerService(stylerOptions);

            var textDocument = (TextDocument)document.Object("TextDocument");
            
            EditPoint startPoint = textDocument.StartPoint.CreateEditPoint();
            EditPoint endPoint = textDocument.EndPoint.CreateEditPoint();

            string xamlSource = startPoint.GetText(endPoint);
            xamlSource = styler.StyleDocument(xamlSource);

            const int vsEPReplaceTextKeepMarkers = 1;
            startPoint.ReplaceText(endPoint, xamlSource, vsEPReplaceTextKeepMarkers);
        }

        private string GetConfigPathForItem(string path, string solutionRoot, Project project)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(path))
                {
                    return null;
                }

                var projectFullName = project?.FullName;
                var projectDirectory = String.IsNullOrEmpty(projectFullName)
                    ? String.Empty
                    : Path.GetDirectoryName(projectFullName);

                IEnumerable<string> configPaths
                    = (path.StartsWith(solutionRoot, StringComparison.InvariantCultureIgnoreCase))
                        ? StylerPackage.GetConfigPathBetweenPaths(path, solutionRoot)
                        : StylerPackage.GetConfigPathBetweenPaths(path, projectDirectory);

                // find the FullPath of "Settings.XamlStyler" ref in project
                var filePathsInProject = project?.ProjectItems.Cast<ProjectItem>()
                    .Where(x => string.Equals(x.Name, "Settings.XamlStyler"))
                    .SelectMany(x => x.Properties.Cast<Property>())
                    .Where(x => string.Equals(x.Name, "FullPath"))
                    .Select(x => x.Value as string);

                if (filePathsInProject != null)
                {
                    configPaths = configPaths.Concat(filePathsInProject);
                }

                return configPaths.FirstOrDefault(File.Exists);
            }
            catch
            {
                // Fail gracefully.
            }

            return null;
        }

        // Searches for configuration file up through solution root directory.
        private static IEnumerable<string> GetConfigPathBetweenPaths(string path, string root)
        {
            string configDirectory = File.GetAttributes(path).HasFlag(FileAttributes.Directory)
                ? path
                : Path.GetDirectoryName(path);

            while (configDirectory.StartsWith(root, StringComparison.InvariantCultureIgnoreCase))
            {
                yield return Path.Combine(configDirectory, "Settings.XamlStyler");
                configDirectory = Path.GetDirectoryName(configDirectory);
            }
        }

        /// <summary>
        /// This function is the callback used to execute a command when the a menu item is clicked.
        /// See the Initialize method to see how the menu item is associated to this function using
        /// the OleMenuCommandService service and the MenuCommand class.
        /// </summary>
        private void MenuItemCallback(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                _uiShell.SetWaitCursor();

                Document document = _dte.ActiveDocument;

                if (IsFormatableDocument(document))
                {
                    Execute(document);
                }
            }
            catch (Exception ex)
            {
                string title = $"Error in {GetType().Name}:";
                string message = string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}\r\n\r\nIf this deems a malfunctioning of styler, please kindly submit an issue at https://github.com/Xavalon/XamlStyler.",
                    ex.Message);

                ShowMessageBox(title, message);
            }
        }

        private void ShowMessageBox(string title, string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Guid clsid = Guid.Empty;
            int result;

            _uiShell.ShowMessageBox(
                0,
                ref clsid,
                title,
                message,
                String.Empty,
                0,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST,
                OLEMSGICON.OLEMSGICON_INFO,
                0, // false
                out result);
        }
    }
}
