﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.Text;
using System.IO;
using System.Text.RegularExpressions;
using Common;
using BuildDependencyReader.Common;

namespace BuildDependencyReader.ProjectFileParser
{
    public class Project
    {
        #region Constants

        protected static readonly log4net.ILog _logger = log4net.LogManager.GetLogger(
           System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        protected static readonly XNamespace CSProjNamespace = "http://schemas.microsoft.com/developer/msbuild/2003";
        // Thanks to http://regexhero.net/tester/
        protected static readonly Regex CONFIG_PLATFORM_REGEX
            = new Regex(@"\' *\$\(Configuration\)(\|\$\(Platform\))? *\' *=+ *\'(?<config>[^\|]*)(\|(?<platform>[^']*))?\'");

        #endregion

        #region Public Members

        public string Name { get; protected set; }
        public string Path { get; protected set; }
        public IEnumerable<Project> ProjectReferences { get; protected set; }
        public IEnumerable<AssemblyReference> AssemblyReferences { get; protected set; }
        public IEnumerable<ProjectConfiguration> Configurations { get; protected set; }
        public Nullable<ProjectConfiguration> DefaultConfiguration { get; protected set; }

        #endregion

        #region Protected Members

        protected static Dictionary<String, Project> ResolvedProjectsCache = new Dictionary<string, Project>();

        #endregion

        #region Constructors

        public Project(string csProjFilePath)
        {
            var fullPath = PathExtensions.GetFullPath(csProjFilePath);
            var projectDirectory = System.IO.Path.GetDirectoryName(fullPath);

            Project.ValidateFileExists(fullPath);

            this.Path = fullPath;
            var document = XDocument.Load(fullPath);
            this.Name = document.Descendants(CSProjNamespace + "AssemblyName").Single().Value;
            this.Configurations = GetProjectConfigurations(document).ToArray();
            this.DefaultConfiguration = this.FindDefaultConfiguration(document);
            this.AssemblyReferences = GetAssemblyReferences(this.Path, projectDirectory, document).ToArray();
            this.ProjectReferences = GetProjectReferences(projectDirectory, document).ToArray();

            this.ValidateDefaultConfiguration();
        }

        #endregion

        #region Public Methods

        public override string ToString()
        {
            return string.Format("{{ Project: Name = '{0}', Path = '{1}' }}", this.Name, this.Path);
        }

        public static Project FromCSProj(string filePath)
        {
            return GetProjectForPath(PathExtensions.GetFullPath(Uri.UnescapeDataString(filePath)));
        }


        public static string ResolvePath(string basePath, string pathToResolve)
        {
            return System.IO.Path.IsPathRooted(pathToResolve)
                 ? pathToResolve
                 : PathExtensions.GetFullPath(System.IO.Path.Combine(basePath, pathToResolve));
        }

        public IEnumerable<FileInfo> GetBuiltProjectOutputs()
        {
            var outputs = this.GetBuiltProjectOutputs(false);
            this.LogUnexpectedOutputs(outputs);
            return outputs;
        }

        #endregion

        #region Protected Methods

        protected bool _enumeratingProjectOutputs = false;
        /// <summary>
        /// Returns the set of files that seems to have been created when this project was built.
        /// <para><em>Note:</em> Project AND all dependencies (ie. referenced projects) must first be built for this to work.</para>
        /// </summary>
        /// <returns></returns>
        protected IEnumerable<FileInfo> GetBuiltProjectOutputs(bool includeDependencies)
        {
            if (_enumeratingProjectOutputs)
            {
                // Reentrancy occurred. Must have a cyclic reference with another Project
                return new FileInfo[] { };
            }
            this._enumeratingProjectOutputs = true;
            try
            {
                return GetBuiltProjectOutputsWithoutCyclicProtection(includeDependencies).ToArray();
            }
            finally
            {
                this._enumeratingProjectOutputs = false;
            }
        }

        private void LogUnexpectedOutputs(IEnumerable<FileInfo> outputs)
        {
            var outputsWithUnexpectedNames = outputs.Select(x => x.Name).Where(x => false == x.StartsWith(this.Name)).ToArray();
            if (outputsWithUnexpectedNames.Any())
            {
                _logger.WarnFormat("Project has unexpected outputs {0}:\n{1}", this.ToString(), StringExtensions.Tabify(outputsWithUnexpectedNames));
            }
        }

        protected IEnumerable<FileInfo> GetBuiltProjectOutputsWithoutCyclicProtection(bool includeDependencies)
        {
            this.ValidateDefaultConfiguration();
            var directoryInfo = new DirectoryInfo(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(this.Path), 
                                                  this.DefaultConfiguration.Value.OutputPath));
            var outputs = directoryInfo.EnumerateFiles();
            if (includeDependencies) {
                return outputs;
            }
            return outputs.Where(f => (false == ExistsAssemblyReferenceWithName(f.Name))
                                   && (false == ExistsReferencedProjectOutputWithName(f.Name)));
        }

        /// <summary>
        /// Checks that the project's default configuration is "useable" for the build tool. Throws an exception if not.
        /// </summary>
        public void ValidateDefaultConfiguration()
        {
            if (false == this.DefaultConfiguration.HasValue)
            {
                throw new Exception(String.Format("Can't resolve build path from which to fetch project outputs because the project no default configuration (Project = {0})",
                                                  this));
            }
            if (String.IsNullOrWhiteSpace(this.DefaultConfiguration.Value.OutputPath))
            {
                throw new Exception(String.Format("Can't resolve build path from which to fetch project outputs because the default configuration '{1}' has no output path set (Project = {0})",
                                                  this, this.DefaultConfiguration.Value));
            }
        }

        protected bool ExistsReferencedProjectOutputWithName(string fileName)
        {
            var deepProjectRefOutputs = this.ProjectReferences.SelectMany(p => p.GetBuiltProjectOutputs(true));
            return deepProjectRefOutputs.Any(pf => pf.Name.Equals(fileName, StringComparison.InvariantCultureIgnoreCase));
        }

        protected bool ExistsAssemblyReferenceWithName(string fileName)
        {
            return this.AssemblyReferences.Select(AssemblyReference.AssemblyNameFromFullName)
                                          .Any(name => name.Equals(System.IO.Path.GetFileNameWithoutExtension(fileName), StringComparison.InvariantCultureIgnoreCase));
        }

        protected static void ValidateFileExists(string filePath)
        {
            if (false == File.Exists(filePath))
            {
                throw new ArgumentException(String.Format("File does not exist: '{0}'", filePath));
            }
        }

        protected static Project GetProjectForPath(string fullPath)
        {
            Project cachedProject = null;
            if (ResolvedProjectsCache.TryGetValue(fullPath, out cachedProject))
            {
                return cachedProject;
            }

            var project = CreateProjectFromCSProj(fullPath);
            ResolvedProjectsCache.Add(fullPath, project);
            return project;
        }


        protected static Project CreateProjectFromCSProj(string fullPath)
        {
            try
            {
                return new Project(fullPath);
            }
            catch (Exception e)
            {
                throw new Exception("Error while trying to process project from path: " + fullPath, e);
            }
        }

        protected Nullable<ProjectConfiguration> FindDefaultConfiguration(XDocument document)
        {
            var defaultConfigurationElement = document.Descendants(CSProjNamespace + "Configuration").SingleOrDefault();
            if (null == defaultConfigurationElement)
            {
                return null;
            }
            string defaultConfigurationName = defaultConfigurationElement.Value.Trim();
            var configsWithMatchingName = this.Configurations
                                              .Where(x => x.Configuration.ToLowerInvariant().Equals(defaultConfigurationName.ToLowerInvariant()))
                                              .ToArray();

            string defaultPlatform = null;
            var defaultPlatformElement = document.Descendants(CSProjNamespace + "Platform").SingleOrDefault();
            if (null != defaultPlatformElement)
            {
                defaultPlatform = defaultPlatformElement.Value.Trim().ToLowerInvariant();
            }
            if (String.IsNullOrWhiteSpace(defaultPlatform)) {
                return configsWithMatchingName.SingleOrDefault(x => String.IsNullOrWhiteSpace(x.Platform));
            }

            return configsWithMatchingName.Where(x => false == String.IsNullOrWhiteSpace(x.Platform))
                                          .SingleOrDefault(x => defaultPlatform.Equals(x.Platform.ToLowerInvariant()));
        }

        protected static IEnumerable<ProjectConfiguration> GetProjectConfigurations(XDocument document)
        {
            return document.Descendants(CSProjNamespace + "PropertyGroup")
                           .Where(IsConfigurationPropertyGroup)
                           .Select(ParseProjectConfiguration);
        }

        protected static IEnumerable<AssemblyReference> GetAssemblyReferences(string projectFileName, string projectDirectory, XDocument csprojDocument)
        {
            return csprojDocument.Descendants(CSProjNamespace + "Reference")
                                 .Select(x => ParseAssemblyReferenceElement(projectFileName, projectDirectory, x));
        }

        protected static IEnumerable<Project> GetProjectReferences(string projectDirectory, XDocument csprojDocument)
        {
            return csprojDocument.Descendants(CSProjNamespace + "ProjectReference")
                                 .Select(x => GetProjectFromProjectReferenceNode(projectDirectory, x));
        }


        protected static ProjectConfiguration ParseProjectConfiguration(XElement configurationPropertyGroupElement)
        {
            var conditionAttr = configurationPropertyGroupElement.Attribute("Condition");
            var match = CONFIG_PLATFORM_REGEX.Match(conditionAttr.Value);
            if ((false == match.Success) || String.IsNullOrWhiteSpace(match.Groups["config"].Value))
            {
                throw new Exception(String.Format("Failed to parse configuration Condition attribute: '{0}'. Match was: '{1}'.",
                                                    conditionAttr, match));
            }
            var outputPath = configurationPropertyGroupElement.Descendants(CSProjNamespace + "OutputPath")
                                                    .Single()
                                                    .Value;
            return new ProjectConfiguration(match.Groups["config"].Value, match.Groups["platform"].Value, outputPath);
        }

        protected static bool IsConfigurationPropertyGroup(XElement propertyGroupElement)
        {
            var conditionAttribute = propertyGroupElement.Attribute("Condition");
            return (null != conditionAttribute)
                && conditionAttribute.Value.ToLowerInvariant().Contains("$(Configuration)".ToLowerInvariant());
        }

        protected static AssemblyReference ParseAssemblyReferenceElement(string projectFileName, string projectDirectory, XElement referenceNode)
        {
            var assemblyName = referenceNode.Attribute("Include").Value;
            string hintPath = null;
            var hintPathNodes = referenceNode.Descendants(CSProjNamespace + "HintPath")
                                             .ToArray();
            if (1 < hintPathNodes.Length)
            {
                throw new AssemblyReferenceMultipleHintPaths(assemblyName, String.Join(",", hintPathNodes.Select(x => x.Value)), projectFileName);
            }
            var hintPathNode = hintPathNodes.SingleOrDefault();

            if (null != hintPathNode)
            {
                hintPath = ResolvePath(projectDirectory, Uri.UnescapeDataString(hintPathNode.Value));
                if (false == File.Exists(hintPath))
                {
                    throw new AssemblyReferenceHintPathDoesNotExist(assemblyName, hintPath, projectFileName);
                }
            }
            return new AssemblyReference(assemblyName, hintPath);
        }

        protected static Project GetProjectFromProjectReferenceNode(string projectDirectory, XElement projectReferenceNode)
        {
            string absoluteFilePath = ResolvePath(projectDirectory, Uri.UnescapeDataString(projectReferenceNode.Attribute("Include").Value));
            ValidateFileExists(absoluteFilePath);

            Project project;
            try
            {
                project = Project.FromCSProj(absoluteFilePath);
            }
            catch (Exception e)
            {
                throw new ArgumentException("Error when trying to resolve referenced project: " + absoluteFilePath, e);
            }
            return project;
        }

        #endregion
    }
}
