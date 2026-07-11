using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
public static class DiagnosticsTools
{
    private const int MaxResults = 200;

    [McpServerTool]
    [Description("Get compiler errors and warnings for the loaded solution without running a full build. Much faster than 'dotnet build' for checking whether code compiles.")]
    public static Task<string> GetDiagnostics(
        [Description("Optional project name to check; empty = all projects")] string? projectName = null,
        [Description("Minimum severity: Error, Warning, or Info (default Warning)")] string? minSeverity = null)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var solution = WorkspaceHost.Current;
            var minimum = Enum.TryParse<DiagnosticSeverity>(minSeverity, ignoreCase: true, out var s)
                ? s
                : DiagnosticSeverity.Warning;

            var projects = string.IsNullOrEmpty(projectName)
                ? solution.Projects
                : solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

            var results = new List<object>();
            var counts = new Dictionary<string, int>();

            foreach (var project in projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation is null) continue;

                foreach (var diagnostic in compilation.GetDiagnostics())
                {
                    if (diagnostic.Severity < minimum) continue;

                    counts.TryGetValue(diagnostic.Severity.ToString(), out var c);
                    counts[diagnostic.Severity.ToString()] = c + 1;

                    if (results.Count < MaxResults)
                    {
                        results.Add(new
                        {
                            project = project.Name,
                            id = diagnostic.Id,
                            severity = diagnostic.Severity.ToString(),
                            message = diagnostic.GetMessage(),
                            location = WorkspaceHost.DescribeLocation(diagnostic.Location)
                        });
                    }
                }
            }

            return new
            {
                counts,
                truncated = counts.Values.Sum() > MaxResults ? (bool?)true : null,
                diagnostics = results
            };
        });
    }
}
