using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
public static class DeletionTools
{
    private const int MaxReferences = 50;
    private const int MaxChangedDocuments = 100;

    private const string ExternalConsumersNote =
        "This symbol is publicly visible: references were checked only inside the loaded solution. " +
        "External consumers (other repositories, packages, reflection, DI, serialization) can't be seen - " +
        "deleting it is a breaking change for any of them.";

    [McpServerTool]
    [Description("Safely delete a member declaration (method, constructor, property, indexer, field, event, nested type) or a whole top-level type, identified by position or fully-qualified name. Checks the whole solution for remaining references first and REFUSES (writes nothing, lists the reference sites) if any exist, or if the member implements an interface member declared in the solution or overrides an abstract member. Removes the declaration together with its doc comments and attributes; for a field/event in a multi-declarator statement only that declarator is removed. With force=true it deletes anyway, but the compile-before-write gate still rejects the edit (with the real compiler diagnostics) if anything would stop compiling. Pairs with find_unused_members: find, then delete.")]
    public static Task<string> DeleteMember(
        [Description("Absolute path of the file containing the symbol (use with line+column)")] string? filePath = null,
        [Description("1-based line number of the symbol (use with filePath)")] int? line = null,
        [Description("1-based column of the symbol (use with filePath)")] int? column = null,
        [Description("Fully-qualified metadata name instead of a position, e.g. 'FixtureLib.Removable.DeadHelper' or 'FixtureLib.Removable'")] string? fullyQualifiedName = null,
        [Description("Delete even if references remain or a contract would break (default false). Only pass true if you will fix the reported sites; the compile gate still refuses an edit that introduces errors.")] bool force = false,
        [Description("Preview only: run every check and the compile gate, report the exact changes/conflicts the real call would produce, but write nothing (default false).")] bool dryRun = false)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var solution = WorkspaceHost.Current;
            var resolved = !string.IsNullOrEmpty(fullyQualifiedName)
                ? await RefactorTools.ResolveByFullyQualifiedNameAsync(solution, fullyQualifiedName)
                : await NavigationTools.ResolveSymbolAsync(
                    filePath ?? throw new ArgumentException("Provide either filePath+line+column or fullyQualifiedName."),
                    line ?? throw new ArgumentException("line is required when filePath is given."),
                    column ?? throw new ArgumentException("column is required when filePath is given."));

            var symbol = Normalize(resolved);
            var removals = await PlanRemovalsAsync(solution, symbol);

            var references = await FindRemainingReferencesAsync(solution, symbol, removals);
            var contracts = FindBrokenContracts(symbol);
            var note = IsExternallyVisible(symbol) ? ExternalConsumersNote : null;

            var referenceList = references.Take(MaxReferences).ToList();
            var truncated = references.Count > MaxReferences ? (bool?)true : null;

            if (!force && (references.Count > 0 || contracts.Count > 0))
            {
                var reasons = new List<string>();
                if (references.Count > 0)
                {
                    reasons.Add($"{references.Count} reference(s) remain in the solution");
                }
                if (contracts.Count > 0)
                {
                    reasons.Add($"deleting it breaks a contract: {string.Join("; ", contracts)}");
                }

                return new
                {
                    symbol = NavigationTools.Describe(symbol),
                    kind = KindOf(symbol),
                    applied = false,
                    dryRun = RefactorTools.DryRunFlag(dryRun),
                    wouldApply = dryRun ? false : (bool?)null,
                    referenceCount = references.Count,
                    references = referenceList.Count > 0 ? referenceList.Select(r => r.ToJson()).ToList() : null,
                    truncated,
                    contracts = contracts.Count > 0 ? contracts : null,
                    changedFileCount = 0,
                    changes = Array.Empty<object>(),
                    message = $"Not applied: {string.Join(", and ", reasons)}. Nothing was written. " +
                              "Pass force: true only if you will fix those sites yourself (the compile gate will still reject an edit that leaves the solution broken).",
                    note
                };
            }

            var candidate = solution;
            foreach (var group in removals.GroupBy(r => r.DocumentId))
            {
                var document = solution.GetDocument(group.Key)!;
                var text = await document.GetTextAsync();
                var newText = text.WithChanges(group
                    .OrderBy(r => r.RemoveSpan.Start)
                    .Select(r => new TextChange(r.RemoveSpan, string.Empty)));
                candidate = candidate.WithDocumentText(group.Key, newText);
            }

            bool? fileNowEmpty = null;
            if (symbol is INamedTypeSymbol)
            {
                foreach (var documentId in removals.Select(r => r.DocumentId).Distinct())
                {
                    var root = await candidate.GetDocument(documentId)!.GetSyntaxRootAsync();
                    var hasDeclarations = root!.DescendantNodes().Any(n =>
                        n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or GlobalStatementSyntax);
                    if (!hasDeclarations) fileNowEmpty = true;
                }
            }

            var changes = await RefactorTools.DescribeDocumentChangesAsync(solution, candidate, MaxChangedDocuments);
            var result = await MutationApplier.ApplyOrPreviewAsync(candidate, dryRun);

            string? message = result.Message;
            if (result.Applied)
            {
                message = fileNowEmpty == true
                    ? "Deleted. The file no longer declares any type; it was left in place (delete it yourself if it is no longer needed)."
                    : null;
            }
            else if (result.WouldApply && fileNowEmpty == true)
            {
                message += " The file would no longer declare any type; it would be left in place.";
            }
            else if (result.NewErrors.Count > 0)
            {
                message += " Nothing was written. The conflicts are the code that still depends on this declaration.";
            }

            return new
            {
                symbol = NavigationTools.Describe(symbol),
                kind = KindOf(symbol),
                applied = result.Applied,
                dryRun = RefactorTools.DryRunFlag(dryRun),
                wouldApply = RefactorTools.WouldApplyFlag(dryRun, result),
                fileNowEmpty = result.Accepted ? fileNowEmpty : null,
                referenceCount = references.Count > 0 ? (int?)references.Count : null,
                references = referenceList.Count > 0 ? referenceList.Select(r => r.ToJson()).ToList() : null,
                truncated,
                contracts = contracts.Count > 0 ? contracts : null,
                changedFileCount = result.Accepted ? changes.Count : 0,
                changes = result.Accepted ? changes : [],
                conflicts = result.Accepted || result.NewErrors.Count == 0 ? null : result.NewErrors,
                staleFile = result.StaleFile,
                message,
                note
            };
        });
    }

    private sealed record Removal(DocumentId DocumentId, TextSpan RemoveSpan, TextSpan DeclarationSpan);

    private sealed record ReferenceSite(string File, int Line, int Column, string? Snippet, int Position)
    {
        public object ToJson() => new { file = File, line = Line, column = Column, snippet = Snippet };
    }

    private static ISymbol Normalize(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { ReducedFrom: { } reducedFrom }) symbol = reducedFrom;
        symbol = symbol.OriginalDefinition;

        if (symbol.DeclaringSyntaxReferences.Length == 0 || !symbol.Locations.Any(l => l.IsInSource))
        {
            throw new InvalidOperationException(
                $"'{symbol.ToDisplayString()}' is not declared in source in the loaded solution (it comes from metadata), so it can't be deleted here.");
        }
        if (symbol.IsImplicitlyDeclared)
        {
            throw new InvalidOperationException(
                $"'{symbol.ToDisplayString()}' is implicitly declared by the compiler (for example a default constructor) and has no declaration to delete. Position on the explicit declaration you want removed.");
        }
        return symbol;
    }

    /// <summary>One removal per declaring syntax reference (several for partial types/methods).</summary>
    private static async Task<List<Removal>> PlanRemovalsAsync(Solution solution, ISymbol symbol)
    {
        var removals = new List<Removal>();
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var node = await reference.GetSyntaxAsync();
            var document = solution.GetDocument(node.SyntaxTree)
                ?? throw new InvalidOperationException($"Declaration of '{symbol.ToDisplayString()}' is not in a solution document.");

            removals.Add(node switch
            {
                VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: BaseFieldDeclarationSyntax fieldDeclaration } declaration } declarator
                    when symbol is IFieldSymbol or IEventSymbol =>
                    declaration.Variables.Count > 1
                        ? new Removal(document.Id, DeclaratorRemovalSpan(declaration, declarator), declarator.Span)
                        : new Removal(document.Id, MemberRemovalSpan(fieldDeclaration), fieldDeclaration.FullSpan),

                BaseTypeDeclarationSyntax or DelegateDeclarationSyntax when symbol is INamedTypeSymbol =>
                    new Removal(document.Id, MemberRemovalSpan((MemberDeclarationSyntax)node), node.FullSpan),

                BaseMethodDeclarationSyntax when symbol is IMethodSymbol =>
                    new Removal(document.Id, MemberRemovalSpan((MemberDeclarationSyntax)node), node.FullSpan),

                BasePropertyDeclarationSyntax when symbol is IPropertySymbol or IEventSymbol =>
                    new Removal(document.Id, MemberRemovalSpan((MemberDeclarationSyntax)node), node.FullSpan),

                _ => throw new InvalidOperationException(
                    $"delete_member can't remove '{symbol.ToDisplayString()}' ({symbol.Kind}, declared as {node.Kind()}). " +
                    "Supported: methods, constructors, finalizers, operators, properties, indexers, fields, events, and types. " +
                    "Not supported: enum members, local functions, locals, parameters, accessors, and positional record members.")
            });
        }
        return removals;
    }

    /// <summary>
    /// The member plus its leading trivia (doc comments; attributes are part of the node) and
    /// trailing end-of-line. Leading trivia is only taken back to the last blank line, so a
    /// section comment separated from the member by a blank line stays, and preprocessor
    /// directives always stay. When the member is the first in its block, blank lines before
    /// the next member are also consumed so no blank line is left hugging the brace.
    /// </summary>
    private static TextSpan MemberRemovalSpan(MemberDeclarationSyntax member)
    {
        var leading = member.GetLeadingTrivia();
        var start = member.FullSpan.Start;

        for (var i = leading.Count - 1; i >= 0; i--)
        {
            if (leading[i].IsDirective)
            {
                start = leading[i].FullSpan.End;
                break;
            }
            if (IsBlankLineStart(leading, i))
            {
                start = leading[i].FullSpan.Start;
                break;
            }
        }

        var end = member.FullSpan.End;
        var previousToken = member.GetFirstToken().GetPreviousToken();
        if (start == member.FullSpan.Start && previousToken.IsKind(SyntaxKind.OpenBraceToken))
        {
            var nextLeading = member.GetLastToken().GetNextToken().LeadingTrivia;
            var i = 0;
            while (i < nextLeading.Count)
            {
                if (nextLeading[i].IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    end = nextLeading[i].FullSpan.End;
                    i++;
                }
                else if (nextLeading[i].IsKind(SyntaxKind.WhitespaceTrivia) &&
                         i + 1 < nextLeading.Count && nextLeading[i + 1].IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    end = nextLeading[i + 1].FullSpan.End;
                    i += 2;
                }
                else
                {
                    break;
                }
            }
        }

        return TextSpan.FromBounds(start, end);
    }

    /// <summary>True if leading[i] is an end-of-line that terminates a whitespace-only line.</summary>
    private static bool IsBlankLineStart(SyntaxTriviaList leading, int i)
    {
        if (!leading[i].IsKind(SyntaxKind.EndOfLineTrivia)) return false;
        if (i == 0) return true;
        if (leading[i - 1].IsKind(SyntaxKind.EndOfLineTrivia)) return true;
        return leading[i - 1].IsKind(SyntaxKind.WhitespaceTrivia) &&
               (i - 1 == 0 || leading[i - 2].IsKind(SyntaxKind.EndOfLineTrivia));
    }

    /// <summary>Removes one declarator and its adjoining comma: "int _a = 1, _b = 2;" minus _b is "int _a = 1;".</summary>
    private static TextSpan DeclaratorRemovalSpan(VariableDeclarationSyntax declaration, VariableDeclaratorSyntax declarator)
    {
        var variables = declaration.Variables;
        var index = variables.IndexOf(declarator);
        return index > 0
            ? TextSpan.FromBounds(variables[index - 1].Span.End, declarator.Span.End)
            : TextSpan.FromBounds(declarator.Span.Start, variables[1].Span.Start);
    }

    /// <summary>
    /// References to the symbol (and, for a type, to anything declared inside it) that lie
    /// outside the declaration being removed. Cascaded symbols that survive the deletion -
    /// the interface member an implementation satisfies, the base member an override
    /// overrides - are excluded: calls through them don't break when this declaration goes.
    /// </summary>
    private static async Task<List<ReferenceSite>> FindRemainingReferencesAsync(
        Solution solution, ISymbol symbol, List<Removal> removals)
    {
        var targets = new List<ISymbol> { symbol };
        if (symbol is INamedTypeSymbol type) targets.AddRange(AllMembers(type));

        var seen = new HashSet<(string, int)>();
        var sites = new List<ReferenceSite>();

        foreach (var target in targets)
        {
            var referencedSymbols = await SymbolFinder.FindReferencesAsync(target, solution);
            foreach (var referencedSymbol in referencedSymbols)
            {
                if (!IsWithin(referencedSymbol.Definition, symbol)) continue;

                foreach (var location in referencedSymbol.Locations)
                {
                    if (!location.Location.IsInSource) continue;

                    var span = location.Location.SourceSpan;
                    var insideRemoved = removals.Any(r =>
                        r.DocumentId == location.Document.Id && r.DeclarationSpan.Contains(span));
                    if (insideRemoved) continue;

                    var lineSpan = location.Location.GetLineSpan();
                    if (!seen.Add((lineSpan.Path, span.Start))) continue;

                    var text = await location.Document.GetTextAsync();
                    var lineIndex = lineSpan.StartLinePosition.Line;
                    sites.Add(new ReferenceSite(
                        lineSpan.Path,
                        lineIndex + 1,
                        lineSpan.StartLinePosition.Character + 1,
                        text.Lines[lineIndex].ToString().Trim(),
                        span.Start));
                }
            }
        }

        return sites
            .OrderBy(s => s.File, StringComparer.Ordinal)
            .ThenBy(s => s.Position)
            .ToList();
    }

    private static IEnumerable<ISymbol> AllMembers(INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers())
        {
            yield return member;
            if (member is INamedTypeSymbol nested)
            {
                foreach (var inner in AllMembers(nested)) yield return inner;
            }
        }
    }

    /// <summary>True if candidate is the symbol itself or declared inside it (accessors, nested members).</summary>
    private static bool IsWithin(ISymbol candidate, ISymbol symbol)
    {
        for (ISymbol? current = candidate; current is not null and not INamespaceSymbol;)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, symbol)) return true;
            current = current is IMethodSymbol { AssociatedSymbol: { } associated }
                ? associated
                : current.ContainingSymbol;
        }
        return false;
    }

    /// <summary>Interface members (declared in the solution) this member implements, and abstract members it overrides.</summary>
    private static List<string> FindBrokenContracts(ISymbol symbol)
    {
        var contracts = new List<string>();
        if (symbol is not (IMethodSymbol or IPropertySymbol or IEventSymbol)) return contracts;

        var explicitImplementations = symbol switch
        {
            IMethodSymbol m => m.ExplicitInterfaceImplementations.Cast<ISymbol>(),
            IPropertySymbol p => p.ExplicitInterfaceImplementations,
            IEventSymbol e => e.ExplicitInterfaceImplementations,
            _ => []
        };
        var implemented = explicitImplementations.ToList();

        if (symbol.ContainingType is { } containingType)
        {
            implemented.AddRange(containingType.AllInterfaces
                .SelectMany(i => i.GetMembers())
                .Where(interfaceMember => SymbolEqualityComparer.Default.Equals(
                    containingType.FindImplementationForInterfaceMember(interfaceMember)?.OriginalDefinition, symbol)));
        }

        foreach (var interfaceMember in implemented.Distinct(SymbolEqualityComparer.Default))
        {
            if (interfaceMember!.Locations.Any(l => l.IsInSource))
            {
                contracts.Add($"implements interface member {interfaceMember.ToDisplayString()}");
            }
        }

        ISymbol? overridden = symbol switch
        {
            IMethodSymbol m => m.OverriddenMethod,
            IPropertySymbol p => p.OverriddenProperty,
            IEventSymbol e => e.OverriddenEvent,
            _ => null
        };
        if (overridden is { IsAbstract: true })
        {
            contracts.Add($"overrides abstract member {overridden.ToDisplayString()}");
        }

        return contracts;
    }

    internal static bool IsExternallyVisible(ISymbol symbol)
    {
        for (ISymbol? current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
            {
                return false;
            }
        }
        return true;
    }

    private static string KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
        INamedTypeSymbol { IsRecord: true } => "record",
        INamedTypeSymbol t => t.TypeKind.ToString().ToLowerInvariant(),
        IMethodSymbol { MethodKind: MethodKind.Constructor } => "constructor",
        IMethodSymbol { MethodKind: MethodKind.StaticConstructor } => "static constructor",
        IMethodSymbol { MethodKind: MethodKind.Destructor } => "finalizer",
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } => "operator",
        IMethodSymbol { MethodKind: MethodKind.Conversion } => "conversion operator",
        IMethodSymbol => "method",
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => symbol.Kind.ToString().ToLowerInvariant()
    };
}
