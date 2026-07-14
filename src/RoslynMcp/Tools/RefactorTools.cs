using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
public static class RefactorTools
{
    private const int MaxChangedDocuments = 100;

    [McpServerTool]
    [Description("PREVIEW a solution-wide rename of the symbol at a position: shows every file and line that would change, without modifying anything. Apply the changes with your file-editing tools if the preview looks right.")]
    public static Task<string> RenameSymbolPreview(
        [Description("Absolute path of the file containing the symbol")] string filePath,
        [Description("1-based line number of the symbol")] int line,
        [Description("1-based column of the symbol")] int column,
        [Description("The new name for the symbol")] string newName)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var symbol = await NavigationTools.ResolveSymbolAsync(filePath, line, column);
            var solution = WorkspaceHost.Current;

            var renamed = await Renamer.RenameSymbolAsync(
                solution, symbol, new SymbolRenameOptions(), newName);

            var changes = await DescribeDocumentChangesAsync(solution, renamed, MaxChangedDocuments);

            return new
            {
                symbol = NavigationTools.Describe(symbol),
                newName,
                changedFileCount = changes.Count,
                note = "Preview only - no files were modified.",
                changes
            };
        });
    }

    [McpServerTool]
    [Description("Solution-wide semantic rename via Roslyn (not text substitution): renames the symbol at a position or by fully-qualified name, together with every override, interface implementation, and call site. Compiles the result first and only writes to disk if no new compile errors appear (conflicts are reported, nothing is written). String literals and comments are NOT touched by default - that's exactly the grep-and-sed failure mode this tool exists to avoid.")]
    public static Task<string> ApplyRename(
        [Description("The new name for the symbol")] string newName,
        [Description("Absolute path of the file containing the symbol (use with line+column)")] string? filePath = null,
        [Description("1-based line number of the symbol (use with filePath)")] int? line = null,
        [Description("1-based column of the symbol (use with filePath)")] int? column = null,
        [Description("Fully-qualified metadata name of the symbol instead of a position, e.g. 'FixtureLib.ProcessorBase.Process(System.String)' or 'FixtureLib.Greeter'")] string? fullyQualifiedName = null,
        [Description("Also rename occurrences inside string literals (default false)")] bool renameInStrings = false,
        [Description("Also rename occurrences inside comments (default false)")] bool renameInComments = false)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var solution = WorkspaceHost.Current;
            var symbol = !string.IsNullOrEmpty(fullyQualifiedName)
                ? await ResolveByFullyQualifiedNameAsync(solution, fullyQualifiedName)
                : await NavigationTools.ResolveSymbolAsync(
                    filePath ?? throw new ArgumentException("Provide either filePath+line+column or fullyQualifiedName."),
                    line ?? throw new ArgumentException("line is required when filePath is given."),
                    column ?? throw new ArgumentException("column is required when filePath is given."));

            var options = new SymbolRenameOptions(
                RenameInStrings: renameInStrings,
                RenameInComments: renameInComments);
            var candidateSolution = await Renamer.RenameSymbolAsync(solution, symbol, options, newName);

            var changes = await DescribeDocumentChangesAsync(solution, candidateSolution, MaxChangedDocuments);
            var result = await MutationApplier.TryApplyAsync(candidateSolution);

            return new
            {
                symbol = NavigationTools.Describe(symbol),
                newName,
                applied = result.Applied,
                changedFileCount = result.Applied ? changes.Count : 0,
                changes = result.Applied ? changes : [],
                conflicts = result.Applied || result.NewErrors.Count == 0 ? null : result.NewErrors,
                staleFile = result.StaleFile,
                message = result.Message
            };
        });
    }

    private static async Task<List<object>> DescribeDocumentChangesAsync(
        Solution before, Solution after, int maxDocuments)
    {
        var changes = new List<object>();
        foreach (var projectChange in after.GetChanges(before).GetProjectChanges())
        {
            foreach (var docId in projectChange.GetChangedDocuments())
            {
                if (changes.Count >= maxDocuments) break;

                var oldDoc = before.GetDocument(docId)!;
                var newDoc = after.GetDocument(docId)!;
                var oldText = await oldDoc.GetTextAsync();
                var textChanges = await newDoc.GetTextChangesAsync(oldDoc);

                changes.Add(new
                {
                    file = oldDoc.FilePath,
                    edits = textChanges.Select(tc => new
                    {
                        line = oldText.Lines.GetLinePosition(tc.Span.Start).Line + 1,
                        oldText = oldText.ToString(tc.Span),
                        newText = tc.NewText
                    }).ToList()
                });
            }
        }
        return changes;
    }

    /// <summary>
    /// Resolves "Namespace.Type", "Namespace.Type.Member", or "Namespace.Type.Member(Param.Types)"
    /// against the loaded solution's compilations. Overload disambiguation via the parenthesized
    /// parameter list is best-effort; the first matching member wins if it doesn't disambiguate.
    /// </summary>
    private static async Task<ISymbol> ResolveByFullyQualifiedNameAsync(Solution solution, string fullyQualifiedName)
    {
        var parenIndex = fullyQualifiedName.IndexOf('(');
        var namePortion = parenIndex >= 0 ? fullyQualifiedName[..parenIndex] : fullyQualifiedName;
        var paramSignature = parenIndex >= 0 ? fullyQualifiedName[(parenIndex + 1)..].TrimEnd(')') : null;

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;

            if (paramSignature is null)
            {
                var asType = compilation.GetTypeByMetadataName(namePortion);
                if (asType is not null) return asType;
            }

            var lastDot = namePortion.LastIndexOf('.');
            if (lastDot < 0) continue;

            var containingType = compilation.GetTypeByMetadataName(namePortion[..lastDot]);
            var candidates = containingType?.GetMembers(namePortion[(lastDot + 1)..]).ToList();
            if (candidates is not { Count: > 0 }) continue;

            if (candidates.Count == 1 || paramSignature is null) return candidates[0];

            var wantedParams = paramSignature
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var match = candidates.FirstOrDefault(m => m is IMethodSymbol method &&
                method.Parameters.Select(p => p.Type.ToDisplayString()).SequenceEqual(wantedParams));
            return match ?? candidates[0];
        }

        throw new InvalidOperationException(
            $"Symbol not found: '{fullyQualifiedName}'. Expected 'Namespace.Type', 'Namespace.Type.Member', or 'Namespace.Type.Member(Param.Types)'.");
    }
}
