using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
public static class NavigationTools
{
    private const int MaxResults = 200;

    [McpServerTool]
    [Description("Find all references to the symbol at a position. Unlike text search, this is semantic: it distinguishes Foo.Bar from Baz.Bar, follows aliases, and never matches comments or strings.")]
    public static Task<string> FindReferences(
        [Description("Absolute path of the file containing the symbol")] string filePath,
        [Description("1-based line number of the symbol")] int line,
        [Description("1-based column of the symbol")] int column)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var symbol = await ResolveSymbolAsync(filePath, line, column);
            var references = await SymbolFinder.FindReferencesAsync(symbol, WorkspaceHost.Current);

            var locations = references
                .SelectMany(r => r.Locations)
                .Select(l => WorkspaceHost.DescribeLocation(l.Location))
                .Where(l => l is not null)
                .Take(MaxResults + 1)
                .ToList();

            var truncated = locations.Count > MaxResults;
            if (truncated) locations.RemoveAt(MaxResults);

            return new
            {
                symbol = Describe(symbol),
                referenceCount = locations.Count,
                truncated = truncated ? (bool?)true : null,
                references = locations
            };
        });
    }

    [McpServerTool]
    [Description("Go to the definition of the symbol at a position (resolves through usages to the original declaration).")]
    public static Task<string> GoToDefinition(
        [Description("Absolute path of the file containing the usage")] string filePath,
        [Description("1-based line number")] int line,
        [Description("1-based column")] int column)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var symbol = await ResolveSymbolAsync(filePath, line, column);

            var definitions = symbol.Locations
                .Select(WorkspaceHost.DescribeLocation)
                .Where(l => l is not null)
                .ToList();

            return new
            {
                symbol = Describe(symbol),
                inMetadata = definitions.Count == 0 ? (bool?)true : null,
                containingAssembly = definitions.Count == 0 ? symbol.ContainingAssembly?.Name : null,
                definitions
            };
        });
    }

    [McpServerTool]
    [Description("Find implementations of an interface, interface member, or overrides of an abstract/virtual member at a position.")]
    public static Task<string> FindImplementations(
        [Description("Absolute path of the file containing the symbol")] string filePath,
        [Description("1-based line number")] int line,
        [Description("1-based column")] int column)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var symbol = await ResolveSymbolAsync(filePath, line, column);
            var solution = WorkspaceHost.Current;

            var implementations = new List<ISymbol>();
            implementations.AddRange(await SymbolFinder.FindImplementationsAsync(symbol, solution));

            if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Class } classSymbol)
            {
                implementations.AddRange(await SymbolFinder.FindDerivedClassesAsync(classSymbol, solution));
            }
            if (symbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
            {
                implementations.AddRange(await SymbolFinder.FindOverridesAsync(symbol, solution));
            }

            var results = implementations
                .DistinctBy(s => s.ToDisplayString())
                .Select(s => new
                {
                    symbol = Describe(s),
                    locations = s.Locations.Select(WorkspaceHost.DescribeLocation).Where(l => l is not null).ToList()
                })
                .Take(MaxResults)
                .ToList();

            return new { symbol = Describe(symbol), implementationCount = results.Count, implementations = results };
        });
    }

    internal static async Task<ISymbol> ResolveSymbolAsync(string filePath, int line, int column)
    {
        var (document, position) = await WorkspaceHost.GetPositionAsync(filePath, line, column);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position);

        return symbol ?? throw new InvalidOperationException(
            $"No symbol found at {filePath}:{line}:{column}. Position the coordinates on an identifier (not whitespace, keywords, or punctuation).");
    }

    internal static object Describe(ISymbol symbol) => new
    {
        name = symbol.Name,
        kind = symbol.Kind.ToString(),
        display = symbol.ToDisplayString(),
        containingType = symbol.ContainingType?.ToDisplayString()
    };
}
