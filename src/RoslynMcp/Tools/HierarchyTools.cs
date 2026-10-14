using System.Collections.Immutable;
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
public static class HierarchyTools
{
    private const int DefaultDepth = 3;
    private const int MaxDepth = 10;
    private const int DefaultMaxNodes = 200;
    private const int MaxMaxNodes = 5000;

    private const string TopLevelMainName = "<Main>$";

    /// <summary>Short member display used for "via" labels, e.g. "IStage.Run(string)".</summary>
    internal static readonly SymbolDisplayFormat ViaFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    [McpServerTool]
    [Description("Transitive INCOMING call hierarchy (who calls this, and who calls those) for a method, constructor, property, or event, identified by filePath+line+column or fullyQualifiedName. Level 1 = every member that calls the target, with exact call sites; level 2 = their callers, and so on to 'depth'. Dispatch-aware by default: for overrides, interface implementations, and virtual/abstract/interface members, callers that reach the target through the base or interface declaration are included and labeled 'via' (base.X() calls are excluded since they never dispatch down). Recursion is shown as a 'cycle: true' leaf; a member already shown elsewhere is marked 'seenBefore: true'; program entry points are marked 'isEntryPoint: true'. Breadth-first, deterministic, capped at maxNodes ('truncated: true' when hit). Read-only.")]
    public static Task<string> FindCallers(
        [Description("Absolute path of the file containing the symbol (use with line+column)")] string? filePath = null,
        [Description("1-based line number of the symbol (use with filePath)")] int? line = null,
        [Description("1-based column of the symbol (use with filePath)")] int? column = null,
        [Description("Fully-qualified name instead of a position, e.g. 'FixtureLib.Pipeline.Execute' or 'FixtureLib.Lower.Run(string)'")] string? fullyQualifiedName = null,
        [Description("How many caller levels to walk (default 3, max 10)")] int depth = DefaultDepth,
        [Description("Stop after this many nodes in the tree (default 200)")] int maxNodes = DefaultMaxNodes,
        [Description("Include callers that reach the target through a base/interface declaration, or through overrides/implementations of it (default true). false = only direct callers of this exact symbol.")] bool includeDispatch = true)
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

            var target = NormalizeTarget(resolved) ?? throw new InvalidOperationException(
                $"find_callers needs a method, constructor, property, or event; '{resolved.ToDisplayString()}' is a {resolved.Kind}.");

            var effectiveDepth = Math.Clamp(depth, 1, MaxDepth);
            var effectiveMaxNodes = Math.Clamp(maxNodes, 1, MaxMaxNodes);

            var walker = new CallerWalker(solution, includeDispatch, await GetEntryPointKeysAsync(solution));
            var (roots, nodeCount, truncated) = await walker.WalkAsync(target, effectiveDepth, effectiveMaxNodes);

            var group = includeDispatch ? await walker.GetDispatchGroupAsync(target) : [];

            return new
            {
                target = DescribeSymbol(target, walker.IsEntryPoint(target)),
                depth = effectiveDepth,
                includeDispatch,
                dispatchGroup = group.Count > 1
                    ? group.Skip(1).Select(g => new { member = g.Symbol.ToDisplayString(ViaFormat), relation = g.Kind }).ToList()
                    : null,
                nodeCount,
                truncated,
                roots
            };
        });
    }

    /// <summary>Maps whatever the position resolved to onto the callable symbol whose callers we want.</summary>
    private static ISymbol? NormalizeTarget(ISymbol symbol)
    {
        symbol = symbol switch
        {
            IMethodSymbol { ReducedFrom: { } reduced } => reduced,
            IMethodSymbol { AssociatedSymbol: IPropertySymbol or IEventSymbol } accessor => accessor.AssociatedSymbol!,
            _ => symbol
        };
        symbol = symbol.OriginalDefinition;
        return symbol is IMethodSymbol or IPropertySymbol or IEventSymbol ? symbol : null;
    }

    /// <summary>
    /// The calling symbol Roslyn reports can be a lambda or an accessor; walk up to the
    /// member a reader (and the next FindCallers query) actually cares about.
    /// </summary>
    internal static ISymbol NormalizeCaller(ISymbol symbol)
    {
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } && symbol.ContainingSymbol is { } parent)
        {
            symbol = parent;
        }
        if (symbol is IMethodSymbol { AssociatedSymbol: IPropertySymbol or IEventSymbol } accessor)
        {
            symbol = accessor.AssociatedSymbol!;
        }
        return symbol.OriginalDefinition;
    }

    /// <summary>Identity that survives the same symbol being seen through different compilations.</summary>
    internal static string Key(ISymbol symbol) =>
        $"{symbol.ContainingAssembly?.Name}|{symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString()}";

    internal static async Task<HashSet<string>> GetEntryPointKeysAsync(Solution solution)
    {
        var keys = new HashSet<string>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation?.GetEntryPoint(CancellationToken.None) is { } entryPoint)
            {
                keys.Add(Key(entryPoint));
            }
        }
        return keys;
    }

    internal static object DescribeSymbol(ISymbol symbol, bool isEntryPoint)
    {
        if (symbol is IMethodSymbol { Name: TopLevelMainName })
        {
            return new
            {
                name = symbol.Name,
                kind = symbol.Kind.ToString(),
                display = $"{symbol.ContainingType?.Name ?? "Program"} (top-level statements)",
                containingType = symbol.ContainingType?.ToDisplayString()
            };
        }
        return NavigationTools.Describe(symbol);
    }

    private static ISymbol? Overridden(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.OverriddenMethod?.OriginalDefinition,
        IPropertySymbol p => p.OverriddenProperty?.OriginalDefinition,
        IEventSymbol e => e.OverriddenEvent?.OriginalDefinition,
        _ => null
    };

    /// <summary>True if the reference is the member name in a base.Member(...) access - a non-virtual call.</summary>
    private static bool IsBaseAccess(Location location)
    {
        var root = location.SourceTree?.GetRoot();
        if (root is null) return false;
        var node = root.FindNode(location.SourceSpan, getInnermostNodeForTie: true);
        return node.Parent is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } access
               && access.Name.Span.Contains(location.SourceSpan);
    }

    internal sealed record DispatchMember(ISymbol Symbol, string Kind);

    internal sealed record CallerEdge(ISymbol Caller, ISymbol? Via, string? ViaKind, List<Location> Locations);

    private sealed class Node
    {
        public required ISymbol Symbol { get; init; }
        public required ImmutableHashSet<string> Path { get; init; }
        public required int Level { get; init; }
        public object? Location { get; init; }
        public List<object> CallSites { get; init; } = [];
        public string? Via { get; init; }
        public string? ViaKind { get; init; }
        public bool Cycle { get; init; }
        public bool SeenBefore { get; init; }
        public bool IsEntryPoint { get; init; }
        public List<Node> Callers { get; } = [];

        public object ToResult(Func<ISymbol, bool, object> describe) => new
        {
            symbol = describe(Symbol, IsEntryPoint),
            location = Location,
            callSites = CallSites,
            via = Via,
            viaKind = ViaKind,
            cycle = Cycle ? (bool?)true : null,
            seenBefore = SeenBefore ? (bool?)true : null,
            isEntryPoint = IsEntryPoint ? (bool?)true : null,
            callers = Callers.Select(c => c.ToResult(describe)).ToList()
        };
    }

    /// <summary>
    /// Dispatch-aware caller lookup with a per-walk edge cache. Shared with
    /// analyze_change_impact (ImpactTools), which runs its own BFS over GetCallerEdgesAsync.
    /// </summary>
    internal sealed class CallerWalker(Solution solution, bool includeDispatch, HashSet<string> entryPointKeys)
    {
        private readonly Dictionary<string, List<CallerEdge>> _edgeCache = new();

        public bool IsEntryPoint(ISymbol symbol) => entryPointKeys.Contains(Key(symbol));

        public async Task<(List<object> Roots, int NodeCount, bool Truncated)> WalkAsync(
            ISymbol target, int depth, int maxNodes)
        {
            var targetKey = Key(target);
            var seen = new HashSet<string> { targetKey };
            var root = new Node { Symbol = target, Path = [targetKey], Level = 0 };
            var queue = new Queue<Node>();
            queue.Enqueue(root);

            var nodeCount = 0;
            var truncated = false;

            // Breadth-first so that hitting maxNodes cuts the deepest levels, not whole branches.
            while (queue.Count > 0 && !truncated)
            {
                var parent = queue.Dequeue();
                if (parent.Cycle || parent.Level >= depth) continue;

                foreach (var edge in await GetCallerEdgesAsync(parent.Symbol))
                {
                    if (nodeCount >= maxNodes)
                    {
                        truncated = true;
                        break;
                    }

                    var key = Key(edge.Caller);
                    var isCycle = parent.Path.Contains(key);
                    var child = new Node
                    {
                        Symbol = edge.Caller,
                        Path = parent.Path.Add(key),
                        Level = parent.Level + 1,
                        Location = edge.Caller.Locations.Where(l => l.IsInSource)
                            .Select(WorkspaceHost.DescribeLocation).FirstOrDefault(),
                        CallSites = edge.Locations.Select(DescribeCallSite).ToList(),
                        Via = edge.Via?.ToDisplayString(ViaFormat),
                        ViaKind = edge.ViaKind,
                        Cycle = isCycle,
                        SeenBefore = !isCycle && !seen.Add(key),
                        IsEntryPoint = IsEntryPoint(edge.Caller)
                    };
                    parent.Callers.Add(child);
                    nodeCount++;
                    if (!isCycle) queue.Enqueue(child);
                }
            }

            return (root.Callers.Select(c => c.ToResult(DescribeSymbol)).ToList(), nodeCount, truncated);
        }

        private static object DescribeCallSite(Location location)
        {
            var span = location.GetLineSpan();
            return new
            {
                file = span.Path,
                line = span.StartLinePosition.Line + 1,
                column = span.StartLinePosition.Character + 1
            };
        }

        /// <summary>
        /// The target itself first, then (when dispatch is on) everything a call could
        /// dispatch through: overridden base members, implemented interface members, and
        /// downward overrides/implementations. Static and non-member symbols have no group.
        /// </summary>
        public async Task<List<DispatchMember>> GetDispatchGroupAsync(ISymbol symbol)
        {
            var group = new List<DispatchMember> { new(symbol, "self") };
            var keys = new HashSet<string> { Key(symbol) };
            void Add(ISymbol s, string kind)
            {
                var original = s.OriginalDefinition;
                if (keys.Add(Key(original))) group.Add(new DispatchMember(original, kind));
            }

            if (symbol.IsStatic || symbol is IMethodSymbol { MethodKind: MethodKind.Constructor }) return group;

            var upward = new List<ISymbol> { symbol };
            for (var o = Overridden(symbol); o is not null; o = Overridden(o))
            {
                Add(o, "base");
                upward.Add(o);
            }
            foreach (var member in upward)
            {
                foreach (var i in await SymbolFinder.FindImplementedInterfaceMembersAsync(member, solution))
                {
                    Add(i, "interface");
                }
            }
            foreach (var o in await SymbolFinder.FindOverridesAsync(symbol, solution))
            {
                Add(o, "override");
            }
            foreach (var i in await SymbolFinder.FindImplementationsAsync(symbol, solution))
            {
                Add(i, "implementation");
            }
            return group;
        }

        public async Task<List<CallerEdge>> GetCallerEdgesAsync(ISymbol symbol)
        {
            var cacheKey = Key(symbol);
            if (_edgeCache.TryGetValue(cacheKey, out var cached)) return cached;

            var group = includeDispatch
                ? await GetDispatchGroupAsync(symbol)
                : [new DispatchMember(symbol, "self")];

            var merged = new Dictionary<(string Caller, string Via), CallerEdge>();
            foreach (var member in group)
            {
                var isSelf = member.Kind == "self";
                foreach (var info in await SymbolFinder.FindCallersAsync(member.Symbol, solution))
                {
                    if (!info.IsDirect) continue; // cascaded references are covered by the explicit group

                    var locations = info.Locations.Where(l => l.IsInSource);
                    // base.X() is a non-virtual call to the base body; it never reaches an override.
                    if (member.Kind == "base") locations = locations.Where(l => !IsBaseAccess(l));
                    var list = locations.ToList();
                    if (list.Count == 0) continue;

                    var caller = NormalizeCaller(info.CallingSymbol);
                    var mapKey = (Key(caller), isSelf ? "" : Key(member.Symbol));
                    if (!merged.TryGetValue(mapKey, out var edge))
                    {
                        edge = new CallerEdge(caller, isSelf ? null : member.Symbol, isSelf ? null : member.Kind, []);
                        merged[mapKey] = edge;
                    }
                    edge.Locations.AddRange(list);
                }
            }

            var edges = merged.Values
                .Select(e => e with
                {
                    Locations = e.Locations
                        .DistinctBy(l => (l.SourceTree?.FilePath, l.SourceSpan.Start))
                        .OrderBy(l => l.SourceTree?.FilePath, StringComparer.Ordinal)
                        .ThenBy(l => l.SourceSpan.Start)
                        .ToList()
                })
                .OrderBy(e => e.Locations[0].SourceTree?.FilePath, StringComparer.Ordinal)
                .ThenBy(e => e.Locations[0].SourceSpan.Start)
                .ThenBy(e => e.Via is null ? "" : e.Via.ToDisplayString(), StringComparer.Ordinal)
                .ThenBy(e => e.Caller.ToDisplayString(), StringComparer.Ordinal)
                .ToList();

            _edgeCache[cacheKey] = edges;
            return edges;
        }
    }
}
