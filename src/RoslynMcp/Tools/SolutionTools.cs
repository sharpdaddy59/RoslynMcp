using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
public static class SolutionTools
{
    [McpServerTool]
    [Description("Load a C# solution (.sln/.slnx) or project (.csproj) for semantic analysis. Must be called before any other tool. Replaces any previously loaded solution.")]
    public static Task<string> LoadSolution(
        [Description("Absolute path to the .sln, .slnx, or .csproj file")] string? path = null,
        [Description("Alias for 'path' (either name is accepted)")] string? solutionPath = null)
    {
        // Both parameter spellings are accepted deliberately. Uptake experiment round 2
        // (2026-07-14): the model guessed 'solutionPath' five times in a row, the SDK's
        // binding failure surfaced as an opaque "An error occurred invoking
        // 'load_solution'", and the agent - with nothing to self-correct from -
        // abandoned the server entirely. Signatures here are ergonomics for a language
        // model; required-parameter binding errors are unrecoverable dead ends.
        return ToolJson.GuardAsync(async () =>
        {
            var effectivePath = path ?? solutionPath
                ?? throw new ArgumentException(
                    "Missing required argument: pass the absolute .sln/.slnx/.csproj path as 'path' (or 'solutionPath').");

            var solution = await WorkspaceHost.LoadAsync(effectivePath);
            var projects = solution.Projects.Select(p => new
            {
                name = p.Name,
                assemblyName = p.AssemblyName,
                documents = p.DocumentIds.Count
            }).ToList();

            // If every project came back with documents, the workspace is usable for
            // navigation regardless of design-time build noise (NuGet audit findings,
            // missing optional targets). Say so explicitly: agents read a bare
            // "Failure: ..." string as "this index is broken" and fall back to grep.
            var usable = projects.Count > 0 && projects.All(p => p.documents > 0);

            return new
            {
                loaded = WorkspaceHost.LoadedPath,
                projectCount = projects.Count,
                projects,
                status = usable
                    ? "Solution loaded successfully. Navigation tools (find_references, symbol_search, go_to_definition...) are ready to use."
                    : "Solution loaded with problems - some projects have no documents; navigation answers may be incomplete.",
                loadDiagnostics = WorkspaceHost.LoadDiagnostics.Count == 0 ? null : WorkspaceHost.LoadDiagnostics,
                loadDiagnosticsNote = WorkspaceHost.LoadDiagnostics.Count == 0 ? null :
                    usable ? "These design-time build messages did not prevent loading; navigation is unaffected." : null
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
