using System;
using System.Collections.Generic;
using Soldr.ProjectFileParser;
using System.IO;
namespace Soldr.Resolver
{
    public interface IProjectFinder
    {
        IEnumerable<Project> FindProjectForAssemblyReference(AssemblyReference assemblyReference);
        IEnumerable<Project> FindProjectForAssemblyName(string assemblyName);
        FileInfo GetSLNFileForProject(Project project);
        IEnumerable<Project> GetProjectsOfSLN(string slnFilePath);
        IEnumerable<Project> GetProjectsOfSLN(FileInfo slnFileInfo);
        IEnumerable<Project> AllProjectsInPath();
    }
}
