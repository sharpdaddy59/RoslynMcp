using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
public static class AnalysisTools
{
    private const string LimitationsNote =
        "This analysis cannot see reflection, dependency-injection containers, serialization, " +
        "or external consumers outside the loaded solution - treat every result as a lead to " +
        "verify, never as a verdict to act on unchecked.";

    [McpServerTool]
    [Description("Find declared members (methods, fields, properties, events) with zero references anywhere in the loaded solution, reported in two honesty tiers: highConfidence (private/internal, safe to consider removing) and lowConfidence (public member of a public type - may have external consumers this tool can't see). Never flags interface implementations, overrides, operators, finalizers, attributed members (reflection/DI/serialization can reach them), or generated code. Read-only.")]
    public static Task<string> FindUnusedMembers(
        [Description("Optional project name to check; empty = all projects")] string? projectName = null)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var solution = WorkspaceHost.Current;
            var projects = string.IsNullOrEmpty(projectName)
                ? solution.Projects
                : solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

            var entryPoints = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation?.GetEntryPoint(CancellationToken.None) is { } entryPoint)
                {
                    entryPoints.Add(entryPoint);
                }
            }

            var highConfidence = new List<object>();
            var lowConfidence = new List<object>();

            foreach (var project in projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation is null) continue;

                foreach (var type in GetAllTypes(compilation.Assembly.GlobalNamespace))
                {
                    if (type.DeclaringSyntaxReferences.Length == 0) continue;

                    foreach (var member in type.GetMembers())
                    {
                        if (!IsCandidate(member, entryPoints)) continue;

                        var references = await SymbolFinder.FindReferencesAsync(member, solution);
                        if (references.Any(r => r.Locations.Any())) continue;

                        var entry = new
                        {
                            symbol = NavigationTools.Describe(member),
                            location = WorkspaceHost.DescribeLocation(member.Locations.First())
                        };

                        (IsHighConfidence(member) ? highConfidence : lowConfidence).Add(entry);
                    }
                }
            }

            return new
            {
                highConfidenceCount = highConfidence.Count,
                highConfidence,
                lowConfidenceCount = lowConfidence.Count,
                lowConfidence,
                limitations = LimitationsNote
            };
        });
    }

    private static bool IsCandidate(ISymbol member, HashSet<ISymbol> entryPoints)
    {
        if (member.IsImplicitlyDeclared) return false;
        if (member.DeclaringSyntaxReferences.Length == 0) return false;
        if (member.GetAttributes().Length > 0) return false;
        if (entryPoints.Contains(member)) return false;
        if (member.ContainingType?.TypeKind == TypeKind.Interface) return false; // the abstraction itself, not a "dead" member

        var isEligibleKind = member switch
        {
            IMethodSymbol method => method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor,
            IFieldSymbol => true,
            IPropertySymbol => true,
            IEventSymbol => true,
            _ => false
        };
        if (!isEligibleKind) return false;

        if (member.IsOverride) return false;
        if (IsInterfaceImplementation(member)) return false;
        if (IsInGeneratedCode(member)) return false;

        return true;
    }

    private static bool IsHighConfidence(ISymbol member) =>
        member.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.ProtectedAndInternal;

    private static bool IsInterfaceImplementation(ISymbol member)
    {
        var explicitImplementationCount = member switch
        {
            IMethodSymbol m => m.ExplicitInterfaceImplementations.Length,
            IPropertySymbol p => p.ExplicitInterfaceImplementations.Length,
            IEventSymbol e => e.ExplicitInterfaceImplementations.Length,
            _ => 0
        };
        if (explicitImplementationCount > 0) return true;

        var containingType = member.ContainingType;
        if (containingType is null) return false;

        return containingType.AllInterfaces
            .SelectMany(i => i.GetMembers())
            .Any(interfaceMember => SymbolEqualityComparer.Default.Equals(
                containingType.FindImplementationForInterfaceMember(interfaceMember), member));
    }

    private static bool IsInGeneratedCode(ISymbol member)
    {
        foreach (var reference in member.DeclaringSyntaxReferences)
        {
            var filePath = reference.SyntaxTree.FilePath;
            if (filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
                filePath.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var leadingTrivia = reference.SyntaxTree.GetRoot().GetLeadingTrivia().ToFullString();
            if (leadingTrivia.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol root)
    {
        var stack = new Stack<INamespaceOrTypeSymbol>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is INamedTypeSymbol namedType)
            {
                yield return namedType;
                foreach (var nested in namedType.GetTypeMembers()) stack.Push(nested);
            }
            else if (current is INamespaceSymbol ns)
            {
                foreach (var member in ns.GetMembers()) stack.Push(member);
            }
        }
    }
}
