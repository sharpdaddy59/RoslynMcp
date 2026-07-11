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

            return solution;
        }
        finally
        {
            Gate.Release();
        }
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
