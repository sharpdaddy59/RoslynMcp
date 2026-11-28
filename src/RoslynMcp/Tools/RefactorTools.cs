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
    [Description("PREVIEW a solution-wide rename of the symbol at a position: shows every file and line that would change, without modifying anything. Kept for back-compat: prefer apply_rename with dryRun: true, which shows the same changes AND the compile-gate verdict (wouldApply, conflicts), and can then be re-run without dryRun to write.")]
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
        [Description("Fully-qualified metadata name of the symbol instead of a position, e.g. 'FixtureLib.ProcessorBase.Process(string)' or 'FixtureLib.Greeter'")] string? fullyQualifiedName = null,
        [Description("Also rename occurrences inside string literals (default false)")] bool renameInStrings = false,
        [Description("Also rename occurrences inside comments (default false)")] bool renameInComments = false,
        [Description("Preview only: run every check and the compile gate, report the exact changes/conflicts the real call would produce, but write nothing (default false).")] bool dryRun = false)
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
            var result = await MutationApplier.ApplyOrPreviewAsync(candidateSolution, dryRun);

            return new
            {
                symbol = NavigationTools.Describe(symbol),
                newName,
                applied = result.Applied,
                dryRun = DryRunFlag(dryRun),
                wouldApply = WouldApplyFlag(dryRun, result),
                changedFileCount = result.Accepted ? changes.Count : 0,
                changes = result.Accepted ? changes : [],
                conflicts = result.Accepted || result.NewErrors.Count == 0 ? null : result.NewErrors,
                staleFile = result.StaleFile,
                message = result.Message
            };
        });
    }

    /// <summary>JSON value for a write tool's "dryRun" field: true for a dry run, omitted otherwise.</summary>
    internal static bool? DryRunFlag(bool dryRun) => dryRun ? true : null;

    /// <summary>JSON value for a write tool's "wouldApply" field: the gate verdict for a dry run, omitted otherwise.</summary>
    internal static bool? WouldApplyFlag(bool dryRun, MutationResult result) => dryRun ? result.WouldApply : null;

    internal static async Task<List<object>> DescribeDocumentChangesAsync(
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
    internal static async Task<ISymbol> ResolveByFullyQualifiedNameAsync(Solution solution, string fullyQualifiedName)
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

        // Not an exact metadata name. Models often drop or get the namespace wrong
        // ("OrderService.CalcTotal", "Bookshop.PriceFormatter.Format"): match the trailing
        // Type or Type.Member by simple name and accept it only if that is unambiguous.
        var loose = await ResolveByTrailingNameAsync(solution, namePortion, paramSignature);
        if (loose.Count == 1) return loose[0];
        if (loose.Count > 1)
        {
            throw new InvalidOperationException(
                $"'{fullyQualifiedName}' is ambiguous; pass one of these fully-qualified names: " +
                string.Join(", ", loose.Take(10).Select(c => c.ToDisplayString())) + ".");
        }

        throw new InvalidOperationException(
            $"Symbol not found: '{fullyQualifiedName}'. Expected 'Namespace.Type', 'Namespace.Type.Member', or 'Namespace.Type.Member(Param.Types)'. " +
            "symbol_search finds the exact name.");
    }

    /// <summary>
    /// Source symbols whose simple type name (and member name, if given) match the last one or two
    /// segments of <paramref name="namePortion"/>, ignoring the namespace. With a parameter list,
    /// overloads are filtered by it; without one, a method group counts as one candidate (first overload).
    /// </summary>
    private static async Task<List<ISymbol>> ResolveByTrailingNameAsync(
        Solution solution, string namePortion, string? paramSignature)
    {
        var parts = namePortion.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return [];

        var typeMatches = new List<ISymbol>();
        var memberMatches = new List<ISymbol>();
        var wantedParams = paramSignature?
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;

            // Type.Member: the second-to-last segment is the type.
            if (parts.Length >= 2)
            {
                foreach (var type in compilation.GetSymbolsWithName(parts[^2], SymbolFilter.Type).OfType<INamedTypeSymbol>())
                {
                    var members = type.GetMembers(parts[^1]).ToList();
                    if (wantedParams is not null)
                    {
                        members = members.Where(m => m is IMethodSymbol method &&
                            method.Parameters.Select(p => p.Type.ToDisplayString()).SequenceEqual(wantedParams)).ToList();
                    }
                    if (members.Count > 0) memberMatches.Add(members[0]);
                }
            }

            // Bare Type (only without a parameter list).
            if (paramSignature is null)
            {
                typeMatches.AddRange(compilation.GetSymbolsWithName(parts[^1], SymbolFilter.Type));
            }
        }

        static List<ISymbol> Distinct(List<ISymbol> s) =>
            s.GroupBy(x => x.ToDisplayString()).Select(g => g.First()).ToList();

        var m = Distinct(memberMatches);
        return m.Count > 0 ? m : Distinct(typeMatches);
    }
}
