using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
public static class SearchTools
{
    private const int MaxResults = 100;

    [McpServerTool]
    [Description("Search for type/member declarations by name across the loaded solution. Returns declaration locations usable as input to the position-based tools.")]
    public static Task<string> SymbolSearch(
        [Description("Symbol name or name fragment (case-insensitive substring match)")] string query,
        [Description("Optional filter: Class, Interface, Struct, Method, Property, Field, Event, Enum. Empty = all kinds.")] string? kind = null)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var solution = WorkspaceHost.Current;
            var symbols = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
                solution, query, SymbolFilter.TypeAndMember);

            var filtered = symbols
                .Where(s => kind is null or "" ||
                    s.Kind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase) ||
                    (s is INamedTypeSymbol nt && nt.TypeKind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase)))
                .Take(MaxResults)
                .Select(s => new
                {
                    symbol = NavigationTools.Describe(s),
                    locations = s.Locations.Select(WorkspaceHost.DescribeLocation).Where(l => l is not null).ToList()
                })
                .ToList();

            return new { matchCount = filtered.Count, matches = filtered };
        });
    }

    [McpServerTool]
    [Description("Get the structure of a C# file: namespaces, types, and members with their signatures and line numbers. Far cheaper than reading the whole file.")]
    public static Task<string> DocumentOutline(
        [Description("Absolute path of the file")] string filePath)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var document = WorkspaceHost.GetDocument(filePath);
            var root = await document.GetSyntaxRootAsync()
                ?? throw new InvalidOperationException("No syntax tree available.");
            var text = await document.GetTextAsync();

            var types = root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .Select(t => new
                {
                    name = t.Identifier.Text,
                    kind = t.Kind() switch
                    {
                        SyntaxKind.ClassDeclaration => "class",
                        SyntaxKind.InterfaceDeclaration => "interface",
                        SyntaxKind.StructDeclaration => "struct",
                        SyntaxKind.EnumDeclaration => "enum",
                        SyntaxKind.RecordDeclaration => "record",
                        SyntaxKind.RecordStructDeclaration => "record struct",
                        _ => t.Kind().ToString()
                    },
                    line = text.Lines.GetLinePosition(t.Identifier.SpanStart).Line + 1,
                    members = t.ChildNodes()
                        .OfType<MemberDeclarationSyntax>()
                        .Select(m => DescribeMember(m, text))
                        .Where(m => m is not null)
                        .ToList()
                })
                .ToList();

            return new { file = filePath, typeCount = types.Count, types };
        });
    }

    private static object? DescribeMember(MemberDeclarationSyntax member, Microsoft.CodeAnalysis.Text.SourceText text)
    {
        (string kind, string name, int spanStart) = member switch
        {
            MethodDeclarationSyntax m => ("method", $"{m.Identifier.Text}({string.Join(", ", m.ParameterList.Parameters.Select(p => p.Type?.ToString()))})", m.Identifier.SpanStart),
            ConstructorDeclarationSyntax c => ("constructor", $"{c.Identifier.Text}({string.Join(", ", c.ParameterList.Parameters.Select(p => p.Type?.ToString()))})", c.Identifier.SpanStart),
            PropertyDeclarationSyntax p => ("property", p.Identifier.Text, p.Identifier.SpanStart),
            FieldDeclarationSyntax f when f.Declaration.Variables.Count > 0 => ("field", f.Declaration.Variables[0].Identifier.Text, f.Declaration.Variables[0].Identifier.SpanStart),
            EventDeclarationSyntax e => ("event", e.Identifier.Text, e.Identifier.SpanStart),
            EventFieldDeclarationSyntax ef when ef.Declaration.Variables.Count > 0 => ("event", ef.Declaration.Variables[0].Identifier.Text, ef.Declaration.Variables[0].Identifier.SpanStart),
            EnumMemberDeclarationSyntax em => ("enum member", em.Identifier.Text, em.Identifier.SpanStart),
            _ => ("", "", -1)
        };

        if (spanStart < 0) return null;

        return new { kind, name, line = text.Lines.GetLinePosition(spanStart).Line + 1 };
    }
}
