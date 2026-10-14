using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

/// <summary>
/// Read-only navigation gaps (v2.2 item F): get_symbol_info, get_type_hierarchy,
/// find_unused_parameters. Nothing here writes; no MutationApplier.
/// </summary>
[McpServerToolType]
public static partial class SymbolInfoTools
{
    private const int DefaultMaxNodes = 200;
    private const int MaxMaxNodes = 5000;
    private const int DefaultMaxResults = 200;
    private const int MaxMaxResults = 5000;

    private const string ParameterLimitationsNote =
        "This analysis cannot see reflection, dependency-injection containers, serialization, " +
        "or external consumers outside the loaded solution - treat every result as a lead to " +
        "verify, never as a verdict to act on unchecked. Removing a parameter changes a signature: " +
        "use change_signature, which rewrites every call site through the compile gate.";

    // ------------------------------------------------------------------ get_symbol_info

    [McpServerTool]
    [Description("Everything the compiler knows about one symbol, identified by filePath+line+column or fullyQualifiedName (works for metadata symbols such as 'System.String' too): fine-grained kind, accessibility, modifiers (in C# declaration order), containing type/namespace, return type or type, parameters (name, type, default, modifier), type parameters with constraints, base type and all interfaces (for types), the member it overrides and the interface members it implements, every declaration location (partials give several; empty for metadata), isFromMetadata, the raw XML doc comment, and attributes. Read-only.")]
    public static Task<string> GetSymbolInfo(
        [Description("Absolute path of the file containing the symbol (use with line+column)")] string? filePath = null,
        [Description("1-based line number of the symbol (use with filePath)")] int? line = null,
        [Description("1-based column of the symbol (use with filePath)")] int? column = null,
        [Description("Fully-qualified name instead of a position, e.g. 'FixtureLib.Dog', 'FixtureLib.Dog.Fetch' or 'System.String'")] string? fullyQualifiedName = null)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var symbol = await ResolveAsync(filePath, line, column, fullyQualifiedName);
            return DescribeInDepth(symbol);
        });
    }

    private static async Task<ISymbol> ResolveAsync(string? filePath, int? line, int? column, string? fullyQualifiedName)
    {
        var solution = WorkspaceHost.Current;
        return !string.IsNullOrEmpty(fullyQualifiedName)
            ? await RefactorTools.ResolveByFullyQualifiedNameAsync(solution, fullyQualifiedName)
            : await NavigationTools.ResolveSymbolAsync(
                filePath ?? throw new ArgumentException("Provide either filePath+line+column or fullyQualifiedName."),
                line ?? throw new ArgumentException("line is required when filePath is given."),
                column ?? throw new ArgumentException("column is required when filePath is given."));
    }

    private static object DescribeInDepth(ISymbol symbol)
    {
        var method = symbol switch
        {
            IMethodSymbol m => m,
            INamedTypeSymbol { TypeKind: TypeKind.Delegate } d => d.DelegateInvokeMethod,
            _ => null
        };
        var parameters = symbol switch
        {
            IMethodSymbol m => m.Parameters,
            IPropertySymbol { IsIndexer: true } p => p.Parameters,
            INamedTypeSymbol { TypeKind: TypeKind.Delegate } d => d.DelegateInvokeMethod?.Parameters,
            _ => null
        };
        var typeParameters = symbol switch
        {
            IMethodSymbol m => m.TypeParameters,
            INamedTypeSymbol t => t.TypeParameters,
            _ => default
        };
        var namedType = symbol as INamedTypeSymbol;

        var declarations = Declarations(symbol);

        return new
        {
            symbol = NavigationTools.Describe(symbol),
            kind = KindOf(symbol),
            accessibility = AccessibilityOf(symbol),
            modifiers = Modifiers(symbol),
            containingType = symbol.ContainingType?.ToDisplayString(),
            containingMember = symbol is IParameterSymbol or ILocalSymbol or IMethodSymbol { MethodKind: MethodKind.LocalFunction }
                ? symbol.ContainingSymbol?.ToDisplayString()
                : null,
            containingNamespace = symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null,
            returnType = method is { MethodKind: not MethodKind.Constructor and not MethodKind.StaticConstructor }
                ? method.ReturnType.ToDisplayString()
                : null,
            type = TypeOf(symbol),
            ordinal = symbol is IParameterSymbol ps ? ps.Ordinal : (int?)null,
            @default = symbol is IParameterSymbol dp ? DefaultOf(dp) : null,
            parameters = parameters?.Select(p => new
            {
                name = p.Name,
                type = p.Type.ToDisplayString(),
                @default = DefaultOf(p),
                modifier = ParameterModifier(p)
            }).ToList(),
            typeParameters = typeParameters.IsDefaultOrEmpty
                ? null
                : typeParameters.Select(tp => new { name = tp.Name, constraints = Constraints(tp) }).ToList(),
            baseType = namedType?.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Enum or TypeKind.Delegate
                ? namedType.BaseType?.ToDisplayString()
                : null,
            interfaces = namedType is null
                ? null
                : namedType.AllInterfaces.Select(i => i.ToDisplayString()).Order(StringComparer.Ordinal).ToList(),
            overrides = Overridden(symbol)?.ToDisplayString(),
            implements = ImplementedInterfaceMembers(symbol) is { Count: > 0 } impl ? impl : null,
            declarations,
            isFromMetadata = declarations.Count == 0,
            docComment = DocComment(symbol),
            attributes = symbol.GetAttributes().Select(a => a.ToString() ?? "").Order(StringComparer.Ordinal).ToList()
        };
    }

    internal static string KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
        INamedTypeSymbol { IsRecord: true } => "record",
        INamedTypeSymbol t => t.TypeKind.ToString().ToLowerInvariant(),
        IMethodSymbol { MethodKind: MethodKind.Constructor } => "constructor",
        IMethodSymbol { MethodKind: MethodKind.StaticConstructor } => "static constructor",
        IMethodSymbol { MethodKind: MethodKind.Destructor } => "finalizer",
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } => "operator",
        IMethodSymbol { MethodKind: MethodKind.Conversion } => "conversion operator",
        IMethodSymbol { MethodKind: MethodKind.LocalFunction } => "local function",
        IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove } => "accessor",
        IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } => "lambda",
        IMethodSymbol => "method",
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IFieldSymbol { IsConst: true } => "const",
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "enum member",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        IParameterSymbol => "parameter",
        ILocalSymbol => "local",
        INamespaceSymbol => "namespace",
        ITypeParameterSymbol => "type parameter",
        _ => symbol.Kind.ToString().ToLowerInvariant()
    };

    private static string? AccessibilityOf(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.Private => "private",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => null
    };

    /// <summary>Only the modifiers that apply, in the conventional C# declaration order.</summary>
    private static List<string> Modifiers(ISymbol symbol)
    {
        var mods = new List<string>();
        if (symbol is IParameterSymbol parameter)
        {
            if (ParameterModifier(parameter) is { } pm) mods.AddRange(pm.Split(' '));
            return mods;
        }
        if (symbol is INamespaceSymbol) return mods;

        var syntaxModifiers = SyntaxModifierKinds(symbol);
        var inInterface = symbol.ContainingType?.TypeKind == TypeKind.Interface;
        var isType = symbol is INamedTypeSymbol;
        var namedType = symbol as INamedTypeSymbol;
        var isConst = symbol is IFieldSymbol { IsConst: true } or ILocalSymbol { IsConst: true };

        if (symbol.IsStatic && !isConst && symbol is not ILocalSymbol) mods.Add("static");
        if (symbol.IsExtern) mods.Add("extern");
        if (syntaxModifiers.Contains(SyntaxKind.NewKeyword)) mods.Add("new");
        if (symbol.IsVirtual) mods.Add("virtual");
        if (symbol.IsAbstract && !inInterface && namedType is not { TypeKind: TypeKind.Interface } && !(isType && symbol.IsStatic))
            mods.Add("abstract");
        if (symbol.IsSealed && (namedType is { TypeKind: TypeKind.Class, IsStatic: false } || (!isType && symbol.IsOverride)))
            mods.Add("sealed");
        if (symbol.IsOverride) mods.Add("override");
        var isReadOnly = symbol switch
        {
            IFieldSymbol f => f.IsReadOnly && !f.IsConst,
            INamedTypeSymbol t => t.IsReadOnly && t.TypeKind == TypeKind.Struct,
            IMethodSymbol m => m.IsReadOnly && m.MethodKind is MethodKind.Ordinary && m.ContainingType is not { IsReadOnly: true },
            _ => false
        };
        if (isReadOnly) mods.Add("readonly");
        if (isConst) mods.Add("const");
        if (syntaxModifiers.Contains(SyntaxKind.UnsafeKeyword)) mods.Add("unsafe");
        if (symbol is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true }) mods.Add("required");
        if (symbol is IFieldSymbol { IsVolatile: true }) mods.Add("volatile");
        if (symbol is IMethodSymbol { IsAsync: true }) mods.Add("async");
        if (syntaxModifiers.Contains(SyntaxKind.PartialKeyword)) mods.Add("partial");
        if (symbol is IMethodSymbol { IsExtensionMethod: true }) mods.Add("extension");
        return mods;
    }

    private static HashSet<SyntaxKind> SyntaxModifierKinds(ISymbol symbol)
    {
        var kinds = new HashSet<SyntaxKind>();
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var modifiers = reference.GetSyntax() switch
            {
                MemberDeclarationSyntax member => member.Modifiers,
                LocalFunctionStatementSyntax local => local.Modifiers,
                VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax field } => field.Modifiers,
                _ => default
            };
            foreach (var modifier in modifiers) kinds.Add(modifier.Kind());
        }
        return kinds;
    }

    private static string? ParameterModifier(IParameterSymbol p)
    {
        var parts = new List<string>();
        if (IsExtensionThis(p)) parts.Add("this");
        if (p.ScopedKind != ScopedKind.None && p.RefKind == RefKind.None) parts.Add("scoped");
        parts.Add(p.RefKind switch
        {
            RefKind.Ref => "ref",
            RefKind.Out => "out",
            RefKind.In => "in",
            RefKind.RefReadOnlyParameter => "ref readonly",
            _ => ""
        });
        if (p.IsParams) parts.Add("params");
        var joined = string.Join(' ', parts.Where(s => s.Length > 0));
        return joined.Length == 0 ? null : joined;
    }

    private static string? DefaultOf(IParameterSymbol p)
    {
        if (!p.HasExplicitDefaultValue) return null;
        foreach (var reference in p.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is ParameterSyntax { Default: { } eq }) return eq.Value.ToString();
        }
        return p.ExplicitDefaultValue switch
        {
            null => "null",
            string s => SymbolDisplay.FormatLiteral(s, quote: true),
            char c => SymbolDisplay.FormatLiteral(c, quote: true),
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString()
        };
    }

    private static List<string> Constraints(ITypeParameterSymbol tp)
    {
        var constraints = new List<string>();
        if (tp.HasReferenceTypeConstraint)
            constraints.Add(tp.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?" : "class");
        if (tp.HasUnmanagedTypeConstraint) constraints.Add("unmanaged");
        else if (tp.HasValueTypeConstraint) constraints.Add("struct");
        if (tp.HasNotNullConstraint) constraints.Add("notnull");
        constraints.AddRange(tp.ConstraintTypes.Select(t => t.ToDisplayString()));
        if (tp.HasConstructorConstraint) constraints.Add("new()");
        if (tp.AllowsRefLikeType) constraints.Add("allows ref struct");
        return constraints;
    }

    private static string? TypeOf(ISymbol symbol) => symbol switch
    {
        IPropertySymbol p => p.Type.ToDisplayString(),
        IFieldSymbol f => f.Type.ToDisplayString(),
        IEventSymbol e => e.Type.ToDisplayString(),
        IParameterSymbol p => p.Type.ToDisplayString(),
        ILocalSymbol l => l.Type.ToDisplayString(),
        _ => null
    };

    private static ISymbol? Overridden(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.OverriddenMethod,
        IPropertySymbol p => p.OverriddenProperty,
        IEventSymbol e => e.OverriddenEvent,
        _ => null
    };

    private static List<string> ImplementedInterfaceMembers(ISymbol symbol)
    {
        if (symbol is not (IMethodSymbol or IPropertySymbol or IEventSymbol) || symbol.ContainingType is not { } type)
            return [];

        var implemented = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        IEnumerable<ISymbol> explicitImpls = symbol switch
        {
            IMethodSymbol m => m.ExplicitInterfaceImplementations,
            IPropertySymbol p => p.ExplicitInterfaceImplementations,
            IEventSymbol e => e.ExplicitInterfaceImplementations,
            _ => []
        };
        foreach (var e in explicitImpls) implemented.Add(e);

        foreach (var iface in type.AllInterfaces)
        {
            foreach (var member in iface.GetMembers())
            {
                if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), symbol))
                    implemented.Add(member);
            }
        }
        return implemented.Select(s => s.ToDisplayString()).Order(StringComparer.Ordinal).ToList();
    }

    private record DeclarationSite(string File, int Line, int Column);

    private static List<DeclarationSite> Declarations(ISymbol symbol) =>
        symbol.Locations
            .Where(l => l.IsInSource)
            .Select(l => l.GetLineSpan())
            .Select(s => new DeclarationSite(s.Path, s.StartLinePosition.Line + 1, s.StartLinePosition.Character + 1))
            .Distinct()
            .OrderBy(d => d.File, StringComparer.Ordinal).ThenBy(d => d.Line).ThenBy(d => d.Column)
            .ToList();

    [GeneratedRegex(@"^\s*<member\b[^>]*>(?<inner>.*)</member>\s*$", RegexOptions.Singleline)]
    private static partial Regex MemberWrapper();

    private static string? DocComment(ISymbol symbol)
    {
        string? xml;
        try
        {
            xml = symbol.GetDocumentationCommentXml(CultureInfo.InvariantCulture, expandIncludes: false);
        }
        catch
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(xml)) return null;

        var match = MemberWrapper().Match(xml);
        var inner = match.Success ? match.Groups["inner"].Value : xml;
        var lines = inner.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        var text = string.Join("\n", lines);
        return text.Length == 0 ? null : text;
    }

    // ------------------------------------------------------------------ get_type_hierarchy

    [McpServerTool]
    [Description("Type hierarchy for a named type, identified by filePath+line+column or fullyQualifiedName: 'bases' walking UP (classes end with System.Object; for an interface, its base interfaces), 'interfaces' (every interface the type implements, flattened and sorted), and 'derived' walking DOWN within the loaded solution (derived classes, derived interfaces, and implementations, transitive, each with depth, relation, and the parent it was reached through), breadth-first and deterministic, capped at maxNodes ('truncated: true' when hit). Each entry carries 'file' when declared in source, or 'isFromMetadata: true'. For a member/parameter/local, answers with the containing type's hierarchy and says so in 'note'. Read-only.")]
    public static Task<string> GetTypeHierarchy(
        [Description("Absolute path of the file containing the type (use with line+column)")] string? filePath = null,
        [Description("1-based line number (use with filePath)")] int? line = null,
        [Description("1-based column (use with filePath)")] int? column = null,
        [Description("Fully-qualified name instead of a position, e.g. 'FixtureLib.Animal'")] string? fullyQualifiedName = null,
        [Description("Stop after this many derived nodes (default 200, max 5000)")] int maxNodes = DefaultMaxNodes)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var solution = WorkspaceHost.Current;
            var resolved = await ResolveAsync(filePath, line, column, fullyQualifiedName);

            string? note = null;
            if (resolved is not INamedTypeSymbol type)
            {
                type = resolved.ContainingType ?? throw new InvalidOperationException(
                    $"get_type_hierarchy needs a type (or a member of one); '{resolved.ToDisplayString()}' is a {KindOf(resolved)}.");
                note = $"'{resolved.ToDisplayString()}' is a {KindOf(resolved)}, not a type: showing the hierarchy of its containing type {type.ToDisplayString()}.";
            }
            type = type.OriginalDefinition;

            var bases = new List<object>();
            if (type.TypeKind == TypeKind.Interface)
            {
                var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                var level = type.Interfaces.Select(i => i.OriginalDefinition).ToList();
                var depth = 1;
                while (level.Count > 0)
                {
                    var next = new List<INamedTypeSymbol>();
                    foreach (var iface in level.Where(i => seen.Add(i)).OrderBy(i => i.ToDisplayString(), StringComparer.Ordinal))
                    {
                        bases.Add(Node(iface, depth));
                        next.AddRange(iface.Interfaces.Select(i => i.OriginalDefinition));
                    }
                    level = next;
                    depth++;
                }
            }
            else
            {
                var depth = 1;
                for (var current = type.BaseType; current is not null; current = current.BaseType, depth++)
                {
                    bases.Add(Node(current.OriginalDefinition, depth));
                }
            }

            var interfaces = type.AllInterfaces
                .Select(i => i.OriginalDefinition)
                .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
                .OrderBy(i => i.ToDisplayString(), StringComparer.Ordinal)
                .Select(i => Node(i, null))
                .ToList();

            var effectiveMaxNodes = Math.Clamp(maxNodes, 1, MaxMaxNodes);
            var (derived, truncated) = await WalkDerivedAsync(solution, type, effectiveMaxNodes);

            return new
            {
                type = NavigationTools.Describe(type),
                kind = KindOf(type),
                note,
                files = Declarations(type).Select(d => d.File).Distinct().ToList(),
                isFromMetadata = type.Locations.Any(l => l.IsInSource) ? (bool?)null : true,
                bases,
                interfaces,
                derivedCount = derived.Count,
                truncated = truncated ? (bool?)true : null,
                derived
            };
        });
    }

    private static object Node(INamedTypeSymbol type, int? depth)
    {
        var file = Declarations(type).Select(d => d.File).FirstOrDefault();
        return new
        {
            symbol = type.ToDisplayString(),
            kind = KindOf(type),
            depth,
            file,
            isFromMetadata = file is null ? (bool?)true : null
        };
    }

    private static async Task<(List<object> Nodes, bool Truncated)> WalkDerivedAsync(
        Solution solution, INamedTypeSymbol root, int maxNodes)
    {
        var seen = new HashSet<string> { HierarchyTools.Key(root) };
        var nodes = new List<object>();
        var level = new List<INamedTypeSymbol> { root };
        var depth = 1;

        while (level.Count > 0)
        {
            var found = new List<(INamedTypeSymbol Type, string Relation, INamedTypeSymbol Parent)>();
            foreach (var parent in level)
            {
                if (parent.TypeKind == TypeKind.Interface)
                {
                    foreach (var d in await SymbolFinder.FindDerivedInterfacesAsync(parent, solution, transitive: false))
                        found.Add((d, "derivedInterface", parent));
                    foreach (var d in await SymbolFinder.FindImplementationsAsync(parent, solution, transitive: false))
                        found.Add((d, "implementation", parent));
                }
                else if (parent.TypeKind == TypeKind.Class)
                {
                    foreach (var d in await SymbolFinder.FindDerivedClassesAsync(parent, solution, transitive: false))
                        found.Add((d, "derivedClass", parent));
                }
            }

            var next = new List<INamedTypeSymbol>();
            foreach (var (type, relation, parent) in found
                         .OrderBy(f => f.Type.ToDisplayString(), StringComparer.Ordinal)
                         .ThenBy(f => f.Relation, StringComparer.Ordinal)
                         .ThenBy(f => f.Parent.ToDisplayString(), StringComparer.Ordinal))
            {
                var definition = type.OriginalDefinition;
                if (!seen.Add(HierarchyTools.Key(definition))) continue;
                if (nodes.Count >= maxNodes) return (nodes, true);

                var file = Declarations(definition).Select(d => d.File).FirstOrDefault();
                nodes.Add(new
                {
                    symbol = definition.ToDisplayString(),
                    kind = KindOf(definition),
                    relation,
                    via = parent.ToDisplayString(),
                    depth,
                    file,
                    isFromMetadata = file is null ? (bool?)true : null
                });
                next.Add(definition);
            }
            level = next;
            depth++;
        }
        return (nodes, false);
    }

    // ------------------------------------------------------------------ find_unused_parameters

    [McpServerTool]
    [Description("Find parameters of source-declared methods, constructors, and local functions that are never read or written in the body, in the same two honesty tiers as find_unused_members: highConfidence (private/internal method or local function - every caller is in this solution) and lowConfidence (externally visible or virtual - external callers or overriders may depend on the signature). nameof(p) and passing p along count as uses. Signatures that aren't the method's to change are excluded with an explicit reason, counted in 'excludedCounts': override, interfaceImplementation, noBody (abstract/extern/partial definition/interface member), usedAsDelegate (method referenced as a method group), entryPoint (Main), discard ('_', '_1'), extensionThis (the 'this' parameter), generatedCode. Event-handler-shaped methods (object sender, EventArgs e) are reported with 'conventional: true'. Scope: whole solution, projectName, or filePath. Capped at maxResults ('truncated: true'). Read-only.")]
    public static Task<string> FindUnusedParameters(
        [Description("Optional project name to scan; empty = all projects")] string? projectName = null,
        [Description("Optional absolute file path to scan instead of a project")] string? filePath = null,
        [Description("Stop after this many reported parameters (default 200, max 5000)")] int maxResults = DefaultMaxResults)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var solution = WorkspaceHost.Current;
            var documents = ScopeDocuments(solution, projectName, filePath);

            var entryPointKeys = await HierarchyTools.GetEntryPointKeysAsync(solution);
            var hits = new List<ParameterHit>();
            var excluded = new SortedDictionary<string, int>(StringComparer.Ordinal);
            void Exclude(string reason, int count = 1)
            {
                if (count <= 0) return;
                excluded[reason] = excluded.GetValueOrDefault(reason) + count;
            }

            foreach (var document in documents)
            {
                var root = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;
                var generated = IsGeneratedDocument(document.FilePath, root);

                foreach (var node in root.DescendantNodes())
                {
                    var (declaration, parameterList) = node switch
                    {
                        MethodDeclarationSyntax m => ((SyntaxNode)m, m.ParameterList),
                        ConstructorDeclarationSyntax c => (c, c.ParameterList),
                        LocalFunctionStatementSyntax l => (l, l.ParameterList),
                        TypeDeclarationSyntax { ParameterList: { } pl } t and not RecordDeclarationSyntax => (t, pl),
                        _ => (null, null)
                    };
                    if (declaration is null || parameterList is null || parameterList.Parameters.Count == 0) continue;

                    var method = declaration is TypeDeclarationSyntax typeDecl
                        ? PrimaryConstructor(model, typeDecl)
                        : model.GetDeclaredSymbol(declaration) as IMethodSymbol;
                    if (method is null) continue;

                    var parameters = method.Parameters;
                    if (generated) { Exclude("generatedCode", parameters.Length); continue; }

                    var hasBody = declaration switch
                    {
                        BaseMethodDeclarationSyntax b => b.Body is not null || b.ExpressionBody is not null,
                        LocalFunctionStatementSyntax l => l.Body is not null || l.ExpressionBody is not null,
                        _ => true
                    };
                    if (!hasBody) { Exclude("noBody", parameters.Length); continue; }

                    // Parameters with no use anywhere in the declaration (outside its own parameter list).
                    var unused = UnusedParameters(model, declaration, parameterList, parameters);
                    if (unused.Count == 0) continue;

                    if (method.ContainingType?.TypeKind == TypeKind.Interface) { Exclude("noBody", unused.Count); continue; }
                    if (method.IsOverride) { Exclude("override", unused.Count); continue; }
                    if (method.MethodKind == MethodKind.Ordinary && IsInterfaceImplementation(method))
                    {
                        Exclude("interfaceImplementation", unused.Count);
                        continue;
                    }
                    if (entryPointKeys.Contains(HierarchyTools.Key(method)) || IsMainShaped(method))
                    {
                        Exclude("entryPoint", unused.Count);
                        continue;
                    }

                    var remaining = new List<IParameterSymbol>();
                    foreach (var p in unused)
                    {
                        if (IsDiscardName(p.Name)) Exclude("discard");
                        else if (IsExtensionThis(p)) Exclude("extensionThis");
                        else remaining.Add(p);
                    }
                    if (remaining.Count == 0) continue;

                    if (method.MethodKind is MethodKind.Ordinary or MethodKind.LocalFunction &&
                        await IsUsedAsDelegateAsync(solution, model, declaration, method))
                    {
                        Exclude("usedAsDelegate", remaining.Count);
                        continue;
                    }

                    var high = method.MethodKind == MethodKind.LocalFunction ||
                               (!DeletionTools.IsExternallyVisible(method) && !method.IsVirtual && !method.IsAbstract);
                    var conventional = IsEventHandlerShaped(method);
                    foreach (var p in remaining)
                    {
                        var span = p.Locations.First(l => l.IsInSource).GetLineSpan();
                        hits.Add(new ParameterHit(method, p, span.Path, span.StartLinePosition.Line + 1,
                            span.StartLinePosition.Character + 1, high, conventional));
                    }
                }
            }

            var ordered = hits
                .OrderBy(h => h.File, StringComparer.Ordinal).ThenBy(h => h.Line).ThenBy(h => h.Column)
                .ToList();
            var effectiveMax = Math.Clamp(maxResults, 1, MaxMaxResults);
            var truncated = ordered.Count > effectiveMax;
            var shown = ordered.Take(effectiveMax).ToList();

            var highConfidence = shown.Where(h => h.High).Select(h => h.ToResult()).ToList();
            var lowConfidence = shown.Where(h => !h.High).Select(h => h.ToResult()).ToList();

            return new
            {
                highConfidenceCount = highConfidence.Count,
                highConfidence,
                lowConfidenceCount = lowConfidence.Count,
                lowConfidence,
                totalFound = ordered.Count,
                truncated = truncated ? (bool?)true : null,
                excludedCounts = excluded,
                limitations = ParameterLimitationsNote
            };
        });
    }

    private sealed record ParameterHit(
        IMethodSymbol Method, IParameterSymbol Parameter, string File, int Line, int Column, bool High, bool Conventional)
    {
        public object ToResult() => new
        {
            method = NavigationTools.Describe(Method),
            parameter = Parameter.Name,
            index = Parameter.Ordinal,
            type = Parameter.Type.ToDisplayString(),
            file = File,
            line = Line,
            column = Column,
            confidence = High ? "high" : "low",
            conventional = Conventional ? (bool?)true : null
        };
    }

    private static List<Document> ScopeDocuments(Solution solution, string? projectName, string? filePath)
    {
        if (!string.IsNullOrEmpty(filePath))
        {
            return [WorkspaceHost.GetDocument(filePath)];
        }
        var projects = string.IsNullOrEmpty(projectName)
            ? solution.Projects.ToList()
            : solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrEmpty(projectName) && projects.Count == 0)
        {
            throw new InvalidOperationException($"Project not found in the loaded solution: '{projectName}'.");
        }
        return projects
            .SelectMany(p => p.Documents)
            .OrderBy(d => d.FilePath ?? "", StringComparer.Ordinal)
            .ToList();
    }

    private static IMethodSymbol? PrimaryConstructor(SemanticModel model, TypeDeclarationSyntax typeDecl)
    {
        if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol type) return null;
        return type.InstanceConstructors.FirstOrDefault(c =>
            c.DeclaringSyntaxReferences.Any(r => r.GetSyntax() == typeDecl));
    }

    private static List<IParameterSymbol> UnusedParameters(
        SemanticModel model, SyntaxNode declaration, ParameterListSyntax parameterList, IReadOnlyList<IParameterSymbol> parameters)
    {
        var pending = new HashSet<IParameterSymbol>(parameters, SymbolEqualityComparer.Default);
        var names = parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var identifier in declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (pending.Count == 0) break;
            if (!names.Contains(identifier.Identifier.ValueText)) continue;
            if (parameterList.Span.Contains(identifier.Span)) continue;

            var info = model.GetSymbolInfo(identifier);
            IEnumerable<ISymbol> candidates = info.Symbol is not null ? new[] { info.Symbol } : info.CandidateSymbols;
            foreach (var candidate in candidates)
            {
                if (candidate is IParameterSymbol p) pending.Remove(p.OriginalDefinition);
            }
        }
        return parameters.Where(p => pending.Contains(p)).ToList();
    }

    private static bool IsInterfaceImplementation(IMethodSymbol method)
    {
        if (method.ExplicitInterfaceImplementations.Length > 0) return true;
        var type = method.ContainingType;
        if (type is null) return false;
        return type.AllInterfaces
            .SelectMany(i => i.GetMembers())
            .Any(m => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(m), method));
    }

    private static bool IsMainShaped(IMethodSymbol method) =>
        method is { Name: "Main", IsStatic: true, MethodKind: MethodKind.Ordinary, Parameters.Length: 1 } &&
        method.Parameters[0].Type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_String };

    /// <summary>The 'this' receiver of a classic extension method (IParameterSymbol.IsThis is the hidden instance 'this', not this one).</summary>
    private static bool IsExtensionThis(IParameterSymbol p) =>
        p is { Ordinal: 0, ContainingSymbol: IMethodSymbol { IsExtensionMethod: true } };

    private static bool IsDiscardName(string name) =>
        name.Length > 0 && name[0] == '_' && name.Skip(1).All(char.IsAsciiDigit);

    private static bool IsEventHandlerShaped(IMethodSymbol method)
    {
        if (method.Parameters.Length != 2 || !method.ReturnsVoid) return false;
        if (method.Parameters[0].Type.SpecialType != SpecialType.System_Object) return false;
        for (var t = method.Parameters[1].Type; t is not null; t = t.BaseType)
        {
            if (t is { Name: "EventArgs", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } })
                return true;
        }
        return false;
    }

    /// <summary>True when the method is referenced anywhere other than as the target of an invocation.</summary>
    private static async Task<bool> IsUsedAsDelegateAsync(Solution solution, SemanticModel model, SyntaxNode declaration, IMethodSymbol method)
    {
        if (method.MethodKind == MethodKind.LocalFunction)
        {
            var scope = declaration.Parent ?? declaration;
            foreach (var identifier in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (identifier.Identifier.ValueText != method.Name) continue;
                if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, method)) continue;
                if (!IsInvocationTarget(identifier)) return true;
            }
            return false;
        }

        var references = await SymbolFinder.FindReferencesAsync(method, solution);
        foreach (var location in references.SelectMany(r => r.Locations))
        {
            var root = await location.Document.GetSyntaxRootAsync();
            var token = root?.FindToken(location.Location.SourceSpan.Start);
            if (token?.Parent is not { } node) continue;
            if (node is not SimpleNameSyntax name) continue;
            if (!IsInvocationTarget(name)) return true;
        }
        return false;
    }

    private static bool IsInvocationTarget(SimpleNameSyntax name)
    {
        SyntaxNode expression = name;
        if (name.Parent is MemberAccessExpressionSyntax member && member.Name == name) expression = member;
        else if (name.Parent is MemberBindingExpressionSyntax binding && binding.Name == name) expression = binding;
        return expression.Parent is InvocationExpressionSyntax invocation && invocation.Expression == expression;
    }

    private static bool IsGeneratedDocument(string? filePath, SyntaxNode root)
    {
        if (filePath is not null &&
            (filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
             filePath.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return root.GetLeadingTrivia().ToFullString().Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);
    }
}
