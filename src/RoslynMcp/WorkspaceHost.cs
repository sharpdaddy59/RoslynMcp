using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp;

/// <summary>
/// Holds the loaded solution for the lifetime of the server process. MCP tools are
/// static and stateless; this is the one piece of shared state they operate on.
/// </summary>
public static class WorkspaceHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static MSBuildWorkspace? _workspace;
    private static Solution? _solution;
    private static Dictionary<ProjectId, HashSet<string>> _baselineErrors = new();
    private static Dictionary<string, DateTime> _fileSnapshots = new();

    public static string? LoadedPath { get; private set; }
    public static IReadOnlyList<string> LoadDiagnostics { get; private set; } = [];

    /// <summary>Loads a .sln/.slnx/.csproj. Replaces any previously loaded workspace.</summary>
    public static async Task<Solution> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Solution or project not found: {fullPath}");
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            _workspace?.Dispose();
            var workspace = MSBuildWorkspace.Create();

            Solution solution;
            if (fullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var project = await workspace.OpenProjectAsync(fullPath, cancellationToken: cancellationToken);
                solution = project.Solution;
            }
            else
            {
                solution = await workspace.OpenSolutionAsync(fullPath, cancellationToken: cancellationToken);
            }

            _workspace = workspace;
            _solution = solution;
            LoadedPath = fullPath;
            LoadDiagnostics = workspace.Diagnostics
                .Select(d => $"{d.Kind}: {d.Message}")
                .ToArray();

            await CaptureBaselineAsync(solution, cancellationToken);

            return solution;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Snapshot taken at load time: pre-existing compile errors per project (so later
    /// mutations only get blocked by NEW errors) and on-disk write times per file (so
    /// later mutations can detect external edits made after load - see MutationApplier).
    /// </summary>
    private static async Task CaptureBaselineAsync(Solution solution, CancellationToken cancellationToken)
    {
        var baselineErrors = new Dictionary<ProjectId, HashSet<string>>();
        var fileSnapshots = new Dictionary<string, DateTime>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            var errors = compilation?.GetDiagnostics(cancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(DiagnosticKey)
                .ToHashSet() ?? [];
            baselineErrors[project.Id] = errors;

            foreach (var document in project.Documents)
            {
                if (document.FilePath is { } path && File.Exists(path))
                {
                    fileSnapshots[path] = File.GetLastWriteTimeUtc(path);
                }
            }
        }

        _baselineErrors = baselineErrors;
        _fileSnapshots = fileSnapshots;
    }

    /// <summary>Identity of a diagnostic that's stable across unrelated edits to the same file.</summary>
    internal static string DiagnosticKey(Diagnostic d) =>
        $"{d.Id}|{d.Location.SourceTree?.FilePath}|{d.GetMessage()}";

    /// <summary>Pre-existing errors for a project as captured at load time; empty if the project is new since load.</summary>
    internal static IReadOnlySet<string> GetBaselineErrors(ProjectId projectId) =>
        _baselineErrors.TryGetValue(projectId, out var errors) ? errors : (IReadOnlySet<string>)ImmutableHashSet<string>.Empty;

    /// <summary>True if a file tracked at load time (or since refreshed) has a different on-disk write time now.</summary>
    internal static bool HasChangedOnDiskSinceSnapshot(string filePath)
    {
        if (!_fileSnapshots.TryGetValue(filePath, out var snapshot)) return false;
        if (!File.Exists(filePath)) return true;
        return File.GetLastWriteTimeUtc(filePath) != snapshot;
    }

    /// <summary>Call after successfully writing a file so future staleness checks compare against the new state.</summary>
    internal static void RefreshFileSnapshot(string filePath)
    {
        if (File.Exists(filePath))
        {
            _fileSnapshots[filePath] = File.GetLastWriteTimeUtc(filePath);
        }
    }

    /// <summary>
    /// Applies a candidate solution's changes to disk via the underlying Workspace and
    /// updates the tracked current solution. Callers must have already verified the
    /// candidate compiles cleanly - see MutationApplier, the only sanctioned entry point
    /// for write-side tools.
    /// </summary>
    internal static bool TryApplyChanges(Solution candidateSolution)
    {
        var workspace = _workspace
            ?? throw new InvalidOperationException(
                "No solution loaded. Call load_solution with a path to a .sln/.slnx/.csproj first.");

        if (!workspace.TryApplyChanges(candidateSolution)) return false;

        _solution = workspace.CurrentSolution;
        return true;
    }

    public static Solution Current => _solution
        ?? throw new InvalidOperationException(
            "No solution loaded. Call load_solution with a path to a .sln/.slnx/.csproj first.");

    /// <summary>Finds the document for a file path in the loaded solution.</summary>
    public static Document GetDocument(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var documentId = Current.GetDocumentIdsWithFilePath(fullPath).FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"File is not part of the loaded solution: {fullPath}");
        return Current.GetDocument(documentId)
            ?? throw new InvalidOperationException($"Document unavailable: {fullPath}");
    }

    /// <summary>Converts a 1-based line/column in a file to an absolute text position.</summary>
    public static async Task<(Document Document, int Position)> GetPositionAsync(
        string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        var document = GetDocument(filePath);
        var text = await document.GetTextAsync(cancellationToken);

        if (line < 1 || line > text.Lines.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(line),
                $"Line {line} is out of range (file has {text.Lines.Count} lines). Lines are 1-based.");
        }

        var textLine = text.Lines[line - 1];
        var position = textLine.Start + Math.Clamp(column - 1, 0, textLine.Span.Length);
        return (document, position);
    }

    /// <summary>Formats a Roslyn location as a serializable result (1-based line/column).</summary>
    public static object? DescribeLocation(Location location)
    {
        if (!location.IsInSource) return null;

        var span = location.GetLineSpan();
        var text = location.SourceTree?.GetText();
        var lineIndex = span.StartLinePosition.Line;
        var snippet = text is not null && lineIndex < text.Lines.Count
            ? text.Lines[lineIndex].ToString().Trim()
            : null;

        return new
        {
            file = span.Path,
            line = span.StartLinePosition.Line + 1,
            column = span.StartLinePosition.Character + 1,
            snippet
        };
    }
}
