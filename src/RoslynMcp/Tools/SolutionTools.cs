using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
public static class SolutionTools
{
    [McpServerTool]
    [Description("Load a C# solution (.sln/.slnx) or project (.csproj) for semantic analysis. Must be called before any other tool. Replaces any previously loaded solution.")]
    public static Task<string> LoadSolution(
        [Description("Absolute path to the .sln, .slnx, or .csproj file")] string path)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var solution = await WorkspaceHost.LoadAsync(path);
            var projects = solution.Projects.Select(p => new
            {
                name = p.Name,
                assemblyName = p.AssemblyName,
                documents = p.DocumentIds.Count
            }).ToList();

            return new
            {
                loaded = WorkspaceHost.LoadedPath,
                projectCount = projects.Count,
                projects,
                // MSBuild load warnings (missing targets, TFM issues) - often explain
                // why a symbol can't be found later, so always surfaced
                loadDiagnostics = WorkspaceHost.LoadDiagnostics.Count == 0 ? null : WorkspaceHost.LoadDiagnostics
            };
        });
    }

    [McpServerTool]
    [Description("List the projects in the loaded solution with their target frameworks and file counts.")]
    public static Task<string> ListProjects()
    {
        return ToolJson.GuardAsync(() =>
        {
            var projects = WorkspaceHost.Current.Projects.Select(p => new
            {
                name = p.Name,
                assemblyName = p.AssemblyName,
                filePath = p.FilePath,
                documents = p.DocumentIds.Count,
                projectReferences = p.ProjectReferences.Count()
            }).ToList();

            return Task.FromResult<object>(new { projectCount = projects.Count, projects });
        });
    }
}
