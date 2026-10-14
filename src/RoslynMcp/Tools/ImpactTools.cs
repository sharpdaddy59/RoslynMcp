using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
public static class ImpactTools
{
    private const int DefaultDepth = 3;
    private const int MaxDepth = 10;
    private const int DefaultMaxNodes = 500;
    private const int MaxMaxNodes = 5000;

    private const string ExternalConsumersNote =
        "At least one target is publicly visible: dependents were found only inside the loaded solution. " +
        "External consumers (other repositories, packages, reflection, DI, serialization) can't be seen.";

    /// <summary>Attribute short names (without the "Attribute" suffix) that mark a test method.</summary>
    private static readonly HashSet<string> TestAttributeNames = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestCase", "TestMethod", "SkippableFact", "SkippableTheory"
    };

    /// <summary>"Namespace.Type.Member" - the same shape fullyQualifiedName accepts, so results can be fed back.</summary>
    private static readonly SymbolDisplayFormat QualifiedNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);

    [McpServerTool]
    [Description("Read-only blast-radius report for changing a symbol (method, constructor, property, event, field, or type), identified by filePath+line+column, fullyQualifiedName, or a batch via fullyQualifiedNames. Returns: transitive dependents up to 'depth' grouped by project (callers - dispatch-aware like find_callers -, overrides/implementations of the target, and for types/fields every member that references them, then callers of those); 'projectsToRecompile' (the target's project plus every project that transitively depends on it); and 'tests': test methods ([Fact]/[Theory]/[Test]/[TestCase]/[TestMethod]/[SkippableFact]/[SkippableTheory], matched by attribute name) that reach a target within 'depth', each with the shortest call path from the test down to the target. Breadth-first and deterministic; capped at maxNodes ('truncated: true' when hit). Only the loaded solution is visible: reflection, DI, and external consumers are not.")]
    public static Task<string> AnalyzeChangeImpact(
        [Description("Absolute path of the file containing the symbol (use with line+column)")] string? filePath = null,
        [Description("1-based line number of the symbol (use with filePath)")] int? line = null,
        [Description("1-based column of the symbol (use with filePath)")] int? column = null,
        [Description("Fully-qualified name instead of a position, e.g. 'FixtureLib.Pipeline.Execute' or 'FixtureLib.Config'")] string? fullyQualifiedName = null,
        [Description("Several fully-qualified names analyzed together as one change (dependents and tests are merged; distance is to the nearest target)")] string[]? fullyQualifiedNames = null,
        [Description("How many dependent levels to walk (default 3, max 10)")] int depth = DefaultDepth,
        [Description("Stop after this many dependents (default 500, max 5000)")] int maxNodes = DefaultMaxNodes)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var solution = WorkspaceHost.Current;
            var targets = await ResolveTargetsAsync(solution, filePath, line, column, fullyQualifiedName, fullyQualifiedNames);

            var effectiveDepth = Math.Clamp(depth, 1, MaxDepth);
            var effectiveMaxNodes = Math.Clamp(maxNodes, 1, MaxMaxNodes);

            var walker = new HierarchyTools.CallerWalker(
                solution, includeDispatch: true, await HierarchyTools.GetEntryPointKeysAsync(solution));
            var (nodes, truncated) = await WalkAsync(solution, walker, targets, effectiveDepth, effectiveMaxNodes);

            var dependents = nodes
                .Where(n => n.Parent is not null)
                .GroupBy(n => n.Project ?? "")
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new
                {
                    project = g.Key == "" ? null : g.Key,
                    members = OrderByPosition(g).Select(n => DescribeNode(n, walker)).ToList()
                })
                .ToList();

            var tests = OrderByPosition(nodes.Where(n => n.Parent is not null && IsTest(n.Symbol)))
                .OrderBy(n => n.Project ?? "", StringComparer.Ordinal)
                .Select(n => new
                {
                    symbol = QualifiedName(n.Symbol),
                    file = n.File,
                    line = n.Line,
                    column = n.Column,
                    project = n.Project,
                    distance = n.Distance,
                    path = PathToTarget(n)
                })
                .ToList();

            return new
            {
                targets = targets.Select(t => HierarchyTools.DescribeSymbol(t, walker.IsEntryPoint(t))).ToList(),
                depth = effectiveDepth,
                nodeCount = nodes.Count - targets.Count,
                truncated,
                projectsToRecompile = ProjectsToRecompile(solution, targets),
                dependents,
                tests,
                note = targets.Any(DeletionTools.IsExternallyVisible) ? ExternalConsumersNote : null
            };
        });
    }

    private static async Task<List<ISymbol>> ResolveTargetsAsync(
        Solution solution, string? filePath, int? line, int? column,
        string? fullyQualifiedName, string[]? fullyQualifiedNames)
    {
        var resolved = new List<ISymbol>();
        if (!string.IsNullOrEmpty(filePath))
        {
            resolved.Add(await NavigationTools.ResolveSymbolAsync(
                filePath,
                line ?? throw new ArgumentException("line is required when filePath is given."),
                column ?? throw new ArgumentException("column is required when filePath is given.")));
        }
        var names = (fullyQualifiedNames ?? []).Prepend(fullyQualifiedName).Where(n => !string.IsNullOrWhiteSpace(n));
        foreach (var name in names)
        {
            resolved.Add(await RefactorTools.ResolveByFullyQualifiedNameAsync(solution, name!));
        }
        if (resolved.Count == 0)
        {
            throw new ArgumentException("Provide filePath+line+column, fullyQualifiedName, or fullyQualifiedNames.");
        }

        var targets = new List<ISymbol>();
        var keys = new HashSet<string>();
        foreach (var symbol in resolved)
        {
            var target = NormalizeTarget(symbol) ?? throw new InvalidOperationException(
                $"analyze_change_impact needs a type, method, constructor, property, event, or field; '{symbol.ToDisplayString()}' is a {symbol.Kind}.");
            target = (await SymbolFinder.FindSourceDefinitionAsync(target, solution))?.OriginalDefinition ?? target;
            if (keys.Add(HierarchyTools.Key(target))) targets.Add(target);
        }
        return OrderByPosition(targets.Select(t => Locate(solution, t, null, 0, "target", null))).Select(n => n.Symbol).ToList();
    }

    private static ISymbol? NormalizeTarget(ISymbol symbol)
    {
        symbol = symbol switch
        {
            IMethodSymbol { ReducedFrom: { } reduced } => reduced,
            IMethodSymbol { AssociatedSymbol: IPropertySymbol or IEventSymbol } accessor => accessor.AssociatedSymbol!,
            _ => symbol
        };
        symbol = symbol.OriginalDefinition;
        return symbol is IMethodSymbol or IPropertySymbol or IEventSymbol or IFieldSymbol or INamedTypeSymbol ? symbol : null;
    }

    private static bool IsCallable(ISymbol symbol) => symbol is IMethodSymbol or IPropertySymbol or IEventSymbol;

    private sealed record ImpactEdge(ISymbol Symbol, string Relation, string? Via);

    private sealed class ImpactNode
    {
        public required ISymbol Symbol { get; init; }
        public required ImpactNode? Parent { get; init; }
        public required int Distance { get; init; }
        public required string Relation { get; init; }
        public string? Via { get; init; }
        public string? Project { get; init; }
        public string? File { get; init; }
        public int Line { get; init; }
        public int Column { get; init; }
        public int Start { get; init; }
    }

    /// <summary>
    /// Multi-source BFS from every target at distance 0. First discovery wins, so each node's
    /// distance is its shortest distance to any target and its parent chain is a shortest path.
    /// </summary>
    private static async Task<(List<ImpactNode> Nodes, bool Truncated)> WalkAsync(
        Solution solution, HierarchyTools.CallerWalker walker, List<ISymbol> targets, int depth, int maxNodes)
    {
        var nodes = new List<ImpactNode>();
        var seen = new HashSet<string>();
        var queue = new Queue<ImpactNode>();
        foreach (var target in targets)
        {
            seen.Add(HierarchyTools.Key(target));
            var root = Locate(solution, target, null, 0, "target", null);
            nodes.Add(root);
            queue.Enqueue(root);
        }

        var dependentCount = 0;
        var truncated = false;
        while (queue.Count > 0 && !truncated)
        {
            var parent = queue.Dequeue();
            if (parent.Distance >= depth) continue;

            foreach (var edge in await GetDependentEdgesAsync(solution, walker, parent))
            {
                if (seen.Contains(HierarchyTools.Key(edge.Symbol))) continue;
                if (dependentCount >= maxNodes)
                {
                    truncated = true;
                    break;
                }
                seen.Add(HierarchyTools.Key(edge.Symbol));
                var child = Locate(solution, edge.Symbol, parent, parent.Distance + 1, edge.Relation, edge.Via);
                nodes.Add(child);
                queue.Enqueue(child);
                dependentCount++;
            }
        }
        return (nodes, truncated);
    }

    private static async Task<List<ImpactEdge>> GetDependentEdgesAsync(
        Solution solution, HierarchyTools.CallerWalker walker, ImpactNode node)
    {
        var symbol = node.Symbol;
        var edges = new List<ImpactEdge>();

        if (IsCallable(symbol))
        {
            // Callers, dispatch-aware - exactly find_callers' level-1 edges.
            foreach (var edge in await walker.GetCallerEdgesAsync(symbol))
            {
                edges.Add(new ImpactEdge(edge.Caller, "caller", edge.Via?.ToDisplayString(HierarchyTools.ViaFormat)));
            }
            // Changing a target's contract also affects whatever overrides or implements it.
            if (node.Parent is null)
            {
                var downward = (await walker.GetDispatchGroupAsync(symbol))
                    .Where(m => m.Kind is "override" or "implementation")
                    .Select(m => Locate(solution, m.Symbol, null, 0, m.Kind, null));
                edges.AddRange(OrderByPosition(downward).Select(n => new ImpactEdge(n.Symbol, n.Relation, null)));
            }
        }
        else
        {
            edges.AddRange(await GetReferencingMembersAsync(solution, symbol));
        }
        return edges;
    }

    /// <summary>Every member (or type, for base lists and type-level attributes) whose body or signature references the symbol.</summary>
    private static async Task<List<ImpactEdge>> GetReferencingMembersAsync(Solution solution, ISymbol symbol)
    {
        var found = new Dictionary<string, (ISymbol Member, string? File, int Start)>();
        foreach (var location in (await SymbolFinder.FindReferencesAsync(symbol, solution)).SelectMany(r => r.Locations))
        {
            if (location.IsImplicit || !location.Location.IsInSource) continue;

            var model = await location.Document.GetSemanticModelAsync();
            var root = await location.Document.GetSyntaxRootAsync();
            if (model is null || root is null) continue;

            var span = location.Location.SourceSpan;
            var member = ContainingMember(model, root.FindNode(span, getInnermostNodeForTie: true), span.Start);
            if (member is null || IsWithin(member, symbol)) continue;

            var key = HierarchyTools.Key(member);
            var file = location.Location.SourceTree?.FilePath;
            if (!found.TryGetValue(key, out var existing) ||
                string.CompareOrdinal(file, existing.File) < 0 ||
                (file == existing.File && span.Start < existing.Start))
            {
                found[key] = (member, file, span.Start);
            }
        }

        return found.Values
            .OrderBy(f => f.File, StringComparer.Ordinal)
            .ThenBy(f => f.Start)
            .Select(f => new ImpactEdge(f.Member, "reference", null))
            .ToList();
    }

    /// <summary>True if candidate is the symbol itself or declared inside it (a type's own members don't depend on it).</summary>
    private static bool IsWithin(ISymbol candidate, ISymbol symbol)
    {
        var key = HierarchyTools.Key(symbol);
        for (ISymbol? current = candidate; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
        {
            if (HierarchyTools.Key(current.OriginalDefinition) == key) return true;
        }
        return false;
    }

    private static ISymbol? ContainingMember(SemanticModel model, SyntaxNode node, int position)
    {
        foreach (var ancestor in node.AncestorsAndSelf())
        {
            switch (ancestor)
            {
                case GlobalStatementSyntax:
                    return Normalize(model.GetEnclosingSymbol(position));
                case BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or BaseTypeDeclarationSyntax
                    or DelegateDeclarationSyntax or EnumMemberDeclarationSyntax:
                    return Normalize(model.GetDeclaredSymbol(ancestor));
                case BaseFieldDeclarationSyntax field:
                    var variable = field.Declaration.Variables.FirstOrDefault(v => v.Span.Contains(position))
                                   ?? field.Declaration.Variables.FirstOrDefault();
                    return variable is null ? null : Normalize(model.GetDeclaredSymbol(variable));
            }
        }
        return Normalize(model.GetEnclosingSymbol(position));

        static ISymbol? Normalize(ISymbol? s) =>
            s is null or INamespaceSymbol ? null : HierarchyTools.NormalizeCaller(s);
    }

    private static ImpactNode Locate(Solution solution, ISymbol symbol, ImpactNode? parent, int distance, string relation, string? via)
    {
        var location = symbol.Locations
            .Where(l => l.IsInSource)
            .OrderBy(l => l.SourceTree!.FilePath, StringComparer.Ordinal)
            .ThenBy(l => l.SourceSpan.Start)
            .FirstOrDefault();
        var span = location?.GetLineSpan();
        return new ImpactNode
        {
            Symbol = symbol,
            Parent = parent,
            Distance = distance,
            Relation = relation,
            Via = via,
            Project = location?.SourceTree is { } tree ? solution.GetDocument(tree)?.Project.Name : null,
            File = span?.Path,
            Line = span is { } s1 ? s1.StartLinePosition.Line + 1 : 0,
            Column = span is { } s2 ? s2.StartLinePosition.Character + 1 : 0,
            Start = location?.SourceSpan.Start ?? 0
        };
    }

    private static IEnumerable<ImpactNode> OrderByPosition(IEnumerable<ImpactNode> nodes) => nodes
        .OrderBy(n => n.File is null)
        .ThenBy(n => n.File, StringComparer.Ordinal)
        .ThenBy(n => n.Start)
        .ThenBy(n => QualifiedName(n.Symbol), StringComparer.Ordinal);

    private static object DescribeNode(ImpactNode node, HierarchyTools.CallerWalker walker) => new
    {
        symbol = QualifiedName(node.Symbol),
        display = node.Symbol is IMethodSymbol { Name: "<Main>$" } ? null : node.Symbol.ToDisplayString(),
        kind = node.Symbol.Kind.ToString(),
        relation = node.Relation,
        file = node.File,
        line = node.Line,
        column = node.Column,
        distance = node.Distance,
        via = node.Via,
        reaches = node.Parent is null ? null : QualifiedName(node.Parent.Symbol),
        isEntryPoint = walker.IsEntryPoint(node.Symbol) ? (bool?)true : null
    };

    private static string QualifiedName(ISymbol symbol) =>
        symbol is IMethodSymbol { Name: "<Main>$" }
            ? $"{symbol.ContainingType?.Name ?? "Program"} (top-level statements)"
            : symbol.ToDisplayString(QualifiedNameFormat);

    private static List<string> PathToTarget(ImpactNode node)
    {
        var path = new List<string>();
        for (var current = node; current is not null; current = current.Parent)
        {
            path.Add(QualifiedName(current.Symbol));
        }
        return path;
    }

    private static bool IsTest(ISymbol symbol) =>
        symbol is IMethodSymbol method && method.GetAttributes().Any(a =>
        {
            var name = a.AttributeClass?.Name;
            if (name is null) return false;
            if (name.EndsWith("Attribute", StringComparison.Ordinal)) name = name[..^"Attribute".Length];
            return TestAttributeNames.Contains(name);
        });

    private static List<string> ProjectsToRecompile(Solution solution, List<ISymbol> targets)
    {
        var graph = solution.GetProjectDependencyGraph();
        var ids = new HashSet<ProjectId>();
        foreach (var target in targets)
        {
            foreach (var tree in target.Locations.Where(l => l.IsInSource).Select(l => l.SourceTree!))
            {
                foreach (var documentId in solution.GetDocumentIdsWithFilePath(tree.FilePath))
                {
                    ids.Add(documentId.ProjectId);
                    ids.UnionWith(graph.GetProjectsThatTransitivelyDependOnThisProject(documentId.ProjectId));
                }
            }
        }
        return ids
            .Select(id => solution.GetProject(id)?.Name)
            .OfType<string>()
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }
}
