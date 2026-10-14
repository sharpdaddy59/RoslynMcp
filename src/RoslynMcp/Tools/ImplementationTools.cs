using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

/// <summary>
/// v2.2 item E: generation after extraction. implement_interface, implement_abstract and
/// generate_overrides build member stubs with SyntaxGenerator, insert them at the end of the
/// type's first declaration, simplify + format only the inserted nodes, copy the contract's XML
/// doc comment (or emit <c>/// &lt;inheritdoc /&gt;</c>), and write through the compile gate.
/// </summary>
[McpServerToolType]
public static class ImplementationTools
{
    private const int MaxChangedDocuments = 10;
    private const string ThrowNote = "Stubs throw NotImplementedException; replace the bodies before shipping.";

    private static readonly SymbolDisplayFormat MemberFormat = SymbolDisplayFormat.CSharpErrorMessageFormat;

    // ---------------------------------------------------------------- implement_interface

    [McpServerTool]
    [Description("Generate stubs for every interface member a class/struct does not implement yet (including members inherited from base interfaces; members already satisfied by a base class count as implemented). Considers the interfaces in the type's base list, or only interfaceName. Stubs throw NotImplementedException, carry the interface member's XML doc comment (or /// <inheritdoc />), and go at the end of the type's first declaration. explicitly=true emits explicit interface implementations. Compiles the result first and writes nothing if it would add a compile error.")]
    public static Task<string> ImplementInterface(
        [Description("Absolute path of the file containing the type (use with line+column)")] string? filePath = null,
        [Description("1-based line of the type declaration or any member of it (use with filePath)")] int? line = null,
        [Description("1-based column (use with filePath)")] int? column = null,
        [Description("Fully-qualified metadata name of the type instead of a position, e.g. 'MyLib.MemoryRepository'")] string? fullyQualifiedName = null,
        [Description("Only this interface (simple name 'IRepository', constructed 'IRepository<string>' or fully-qualified); default: every interface in the base list")] string? interfaceName = null,
        [Description("Emit explicit interface implementations (default false: public implicit members)")] bool explicitly = false,
        [Description("Preview only: run every check and the compile gate, report the exact changes/conflicts the real call would produce, but write nothing (default false).")] bool dryRun = false)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var type = await ResolveTypeAsync(filePath, line, column, fullyQualifiedName);
            var interfaces = SelectInterfaces(type, interfaceName);

            var generated = new List<ISymbol>();
            var satisfied = new List<ISymbol>();
            var seenSignatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var iface in interfaces)
            {
                foreach (var member in iface.GetMembers().Where(IsImplementableInterfaceMember))
                {
                    var implementation = type.FindImplementationForInterfaceMember(member);
                    if (implementation is not null)
                    {
                        satisfied.Add(member);
                        continue;
                    }
                    // Two interfaces asking for the same signature are satisfied by one implicit member.
                    if (!explicitly && !seenSignatures.Add(SignatureKey(member)))
                    {
                        continue;
                    }
                    generated.Add(member);
                }
            }

            var common = new
            {
                type = NavigationTools.Describe(type),
                interfaces = interfaces.Select(i => i.ToDisplayString(MemberFormat)).ToList(),
                alreadyImplemented = satisfied.Select(m => m.ToDisplayString(MemberFormat)).ToList()
            };

            if (generated.Count == 0)
            {
                return Refusal(common, dryRun,
                    $"Nothing to generate: '{type.Name}' already implements every member of {string.Join(", ", common.interfaces)}. Nothing was written.");
            }

            var plan = await PlanInsertionAsync(type);
            var generator = plan.Generator;
            var throwStatement = ThrowNotImplemented(generator);

            var stubs = generated
                .Select(m => new Stub(m, BuildInterfaceStub(generator, m, explicitly, throwStatement), DocLinesOf(m)))
                .ToList();

            var outcome = await InsertAndApplyAsync(type, plan, stubs, needsNotImplemented: true, dryRun);
            return Result(common, outcome, generated, dryRun, extra: new { @explicit = explicitly ? true : (bool?)null });
        });
    }

    // ---------------------------------------------------------------- implement_abstract

    [McpServerTool]
    [Description("Generate 'override' stubs for every abstract member inherited from the base-class chain that the class does not override yet. Stubs throw NotImplementedException, keep the base member's accessibility, carry its XML doc comment (or /// <inheritdoc />), and go at the end of the class's first declaration. Works even if the class itself is abstract. Refuses if the class has no abstract base. Compiles the result first and writes nothing if it would add a compile error.")]
    public static Task<string> ImplementAbstract(
        [Description("Absolute path of the file containing the class (use with line+column)")] string? filePath = null,
        [Description("1-based line of the class declaration or any member of it (use with filePath)")] int? line = null,
        [Description("1-based column (use with filePath)")] int? column = null,
        [Description("Fully-qualified metadata name of the class instead of a position, e.g. 'MyLib.Circle'")] string? fullyQualifiedName = null,
        [Description("Preview only: run every check and the compile gate, report the exact changes/conflicts the real call would produce, but write nothing (default false).")] bool dryRun = false)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var type = await ResolveTypeAsync(filePath, line, column, fullyQualifiedName);
            var abstractBases = BaseChain(type).Where(b => b.IsAbstract).ToList();

            var common = new
            {
                type = NavigationTools.Describe(type),
                abstractBases = abstractBases.Select(b => b.ToDisplayString(MemberFormat)).ToList()
            };

            if (abstractBases.Count == 0)
            {
                return Refusal(common, dryRun,
                    $"Not applied: '{type.Name}' has no abstract base class (base chain: {string.Join(" -> ", BaseChain(type).Select(b => b.ToDisplayString(MemberFormat)))}). Nothing was written.");
            }

            var missing = OverridableBaseMembers(type).Where(m => m.IsAbstract).ToList();
            if (missing.Count == 0)
            {
                return Refusal(common, dryRun,
                    $"Nothing to generate: '{type.Name}' already overrides every abstract member of {string.Join(", ", common.abstractBases)}. Nothing was written.");
            }

            var plan = await PlanInsertionAsync(type);
            var throwStatement = ThrowNotImplemented(plan.Generator);
            var stubs = missing
                .Select(m => new Stub(m, BuildOverrideStub(plan.Generator, type, m, callBase: false, throwStatement), DocLinesOf(m)))
                .ToList();

            var outcome = await InsertAndApplyAsync(type, plan, stubs, needsNotImplemented: true, dryRun);
            return Result(common, outcome, missing, dryRun, extra: null);
        });
    }

    // ---------------------------------------------------------------- generate_overrides

    [McpServerTool]
    [Description("Generate 'override' members for virtual (non-abstract, non-sealed, not yet overridden) members of the base chain, including object's Equals/GetHashCode/ToString. Bodies call the base member (base.X(...)), so behaviour is unchanged until you edit them. Pass members (names) to pick; without members it REFUSES and lists the candidates unless all=true. Carries the base member's XML doc comment (or /// <inheritdoc />). Compiles the result first and writes nothing if it would add a compile error.")]
    public static Task<string> GenerateOverrides(
        [Description("Absolute path of the file containing the type (use with line+column)")] string? filePath = null,
        [Description("1-based line of the type declaration or any member of it (use with filePath)")] int? line = null,
        [Description("1-based column (use with filePath)")] int? column = null,
        [Description("Fully-qualified metadata name of the type instead of a position")] string? fullyQualifiedName = null,
        [Description("Names of the virtual members to override, e.g. [\"ToString\"] (all overloads of a name are generated)")] string[]? members = null,
        [Description("Override every candidate when members is not given (default false: refuse and list candidates)")] bool all = false,
        [Description("Preview only: run every check and the compile gate, report the exact changes/conflicts the real call would produce, but write nothing (default false).")] bool dryRun = false)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var type = await ResolveTypeAsync(filePath, line, column, fullyQualifiedName);
            var candidates = OverridableBaseMembers(type).Where(m => !m.IsAbstract).ToList();
            var candidateNames = candidates.Select(m => m.ToDisplayString(MemberFormat)).ToList();

            var common = new { type = NavigationTools.Describe(type), candidates = candidateNames };

            if (candidates.Count == 0)
            {
                return Refusal(common, dryRun,
                    $"Nothing to generate: '{type.Name}' has no overridable virtual members left. Nothing was written.");
            }

            List<ISymbol> selected;
            if (members is { Length: > 0 })
            {
                var wanted = members.ToHashSet(StringComparer.Ordinal);
                selected = candidates.Where(m => wanted.Contains(m.Name)).ToList();
                var unknown = wanted.Except(selected.Select(m => m.Name)).ToList();
                if (unknown.Count > 0)
                {
                    return Refusal(common, dryRun,
                        $"Not applied: no overridable virtual member named {string.Join(", ", unknown.Select(u => $"'{u}'"))} on the base chain of '{type.Name}'. See candidates. Nothing was written.");
                }
            }
            else if (all)
            {
                selected = candidates;
            }
            else
            {
                return Refusal(common, dryRun,
                    $"Not applied: pass members (names from candidates) or all: true. Refusing to override all {candidates.Count} virtual member(s) by default. Nothing was written.");
            }

            var plan = await PlanInsertionAsync(type);
            var stubs = selected
                .Select(m => new Stub(m, BuildOverrideStub(plan.Generator, type, m, callBase: true, throwStatement: null), DocLinesOf(m)))
                .ToList();

            var outcome = await InsertAndApplyAsync(type, plan, stubs, needsNotImplemented: false, dryRun);
            return Result(new { type = common.type }, outcome, selected, dryRun, extra: null);
        });
    }

    // ---------------------------------------------------------------- shared: target resolution

    private static async Task<INamedTypeSymbol> ResolveTypeAsync(string? filePath, int? line, int? column, string? fullyQualifiedName)
    {
        var symbol = !string.IsNullOrEmpty(fullyQualifiedName)
            ? await RefactorTools.ResolveByFullyQualifiedNameAsync(WorkspaceHost.Current, fullyQualifiedName)
            : await NavigationTools.ResolveSymbolAsync(
                filePath ?? throw new ArgumentException("Provide either filePath+line+column or fullyQualifiedName."),
                line ?? throw new ArgumentException("line is required when filePath is given."),
                column ?? throw new ArgumentException("column is required when filePath is given."));

        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType
            ?? throw new InvalidOperationException($"'{symbol.Name}' is not a type or a member of one.");

        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            throw new InvalidOperationException(
                $"'{type.Name}' is a {type.TypeKind.ToString().ToLowerInvariant()}; generation targets a class or struct.");
        }
        if (type.DeclaringSyntaxReferences.Length == 0)
        {
            throw new InvalidOperationException($"'{type.ToDisplayString()}' is not declared in source in the loaded solution.");
        }
        return type;
    }

    private static List<INamedTypeSymbol> SelectInterfaces(INamedTypeSymbol type, string? interfaceName)
    {
        var declared = type.Interfaces;
        if (declared.Length == 0)
        {
            throw new InvalidOperationException($"'{type.Name}' declares no interfaces in its base list.");
        }

        IEnumerable<INamedTypeSymbol> roots = declared;
        if (!string.IsNullOrWhiteSpace(interfaceName))
        {
            var expanded = Expand(declared);
            var match = expanded.FirstOrDefault(i => NameMatches(i, interfaceName.Trim()))
                ?? throw new InvalidOperationException(
                    $"'{type.Name}' does not implement an interface named '{interfaceName}'. Its interfaces: {string.Join(", ", expanded.Select(i => i.ToDisplayString(MemberFormat)))}.");
            roots = [match];
        }

        return Expand(roots);

        static List<INamedTypeSymbol> Expand(IEnumerable<INamedTypeSymbol> interfaces) => interfaces
            .SelectMany(i => new[] { i }.Concat(i.AllInterfaces))
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .ToList();

        static bool NameMatches(INamedTypeSymbol i, string name) =>
            i.Name == name ||
            i.ToDisplayString(MemberFormat) == name ||
            i.ToDisplayString() == name ||
            i.OriginalDefinition.ToDisplayString(MemberFormat) == name ||
            i.OriginalDefinition.ToDisplayString() == name;
    }

    private static bool IsImplementableInterfaceMember(ISymbol member) =>
        member.IsAbstract && member switch
        {
            IMethodSymbol { MethodKind: MethodKind.Ordinary } => true,
            IPropertySymbol or IEventSymbol => true,
            _ => false
        };

    private static string SignatureKey(ISymbol member) => member switch
    {
        IMethodSymbol m => $"M:{m.Name}`{m.TypeParameters.Length}({string.Join(",", m.Parameters.Select(p => p.RefKind + p.Type.ToDisplayString()))})",
        IPropertySymbol { IsIndexer: true } p => $"I:({string.Join(",", p.Parameters.Select(x => x.Type.ToDisplayString()))})",
        _ => $"{member.Kind}:{member.Name}"
    };

    private static IEnumerable<INamedTypeSymbol> BaseChain(INamedTypeSymbol type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            yield return t;
        }
    }

    /// <summary>
    /// The most-derived overridable (abstract, virtual, or non-sealed override) members of the base
    /// chain that neither the type nor an intermediate base already overrides, base-nearest first.
    /// Inaccessible members, finalizers and accessor methods are excluded.
    /// </summary>
    private static List<ISymbol> OverridableBaseMembers(INamedTypeSymbol type)
    {
        var covered = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var member in type.GetMembers())
        {
            CoverChain(member, covered);
        }

        var result = new List<ISymbol>();
        foreach (var baseType in BaseChain(type))
        {
            foreach (var member in baseType.GetMembers())
            {
                if (covered.Contains(member.OriginalDefinition)) continue;

                var kindOk = member switch
                {
                    IMethodSymbol { MethodKind: MethodKind.Ordinary } => true,
                    IPropertySymbol or IEventSymbol => true,
                    _ => false
                };
                var overridable = member.IsAbstract || member.IsVirtual || (member.IsOverride && !member.IsSealed);
                if (kindOk && overridable && !member.IsStatic && IsAccessibleForOverride(member, type))
                {
                    result.Add(member);
                }
                CoverChain(member, covered);
            }
        }
        return result;

        static void CoverChain(ISymbol member, HashSet<ISymbol> covered)
        {
            for (var s = member; s is not null; s = Overridden(s))
            {
                covered.Add(s.OriginalDefinition);
            }
        }

        static ISymbol? Overridden(ISymbol s) => s switch
        {
            IMethodSymbol m => m.OverriddenMethod,
            IPropertySymbol p => p.OverriddenProperty,
            IEventSymbol e => e.OverriddenEvent,
            _ => null
        };
    }

    private static bool IsAccessibleForOverride(ISymbol member, INamedTypeSymbol type) => member.DeclaredAccessibility switch
    {
        Accessibility.Private => false,
        Accessibility.Internal or Accessibility.ProtectedAndInternal =>
            SymbolEqualityComparer.Default.Equals(member.ContainingAssembly, type.ContainingAssembly) ||
            member.ContainingAssembly.GivesAccessTo(type.ContainingAssembly),
        _ => true
    };

    /// <summary>An override keeps the base accessibility, except protected internal from another assembly becomes protected.</summary>
    private static Accessibility OverrideAccessibility(ISymbol member, INamedTypeSymbol type) =>
        member.DeclaredAccessibility == Accessibility.ProtectedOrInternal &&
        !SymbolEqualityComparer.Default.Equals(member.ContainingAssembly, type.ContainingAssembly)
            ? Accessibility.Protected
            : member.DeclaredAccessibility;

    // ---------------------------------------------------------------- shared: stub construction

    private sealed record Stub(ISymbol Source, SyntaxNode Node, IReadOnlyList<string>? DocLines);

    private static SyntaxNode ThrowNotImplemented(SyntaxGenerator generator) =>
        generator.ThrowStatement(generator.ObjectCreationExpression(generator.IdentifierName("NotImplementedException")));

    private static SyntaxNode BuildInterfaceStub(SyntaxGenerator generator, ISymbol member, bool explicitly, SyntaxNode throwStatement)
    {
        SyntaxNode[] Throws() => [throwStatement];
        var modifiers = member.IsStatic ? DeclarationModifiers.Static : DeclarationModifiers.None;

        SyntaxNode declaration = member switch
        {
            IMethodSymbol method => generator.MethodDeclaration(method, Throws()),
            IPropertySymbol { IsIndexer: true } indexer => generator.IndexerDeclaration(indexer,
                indexer.GetMethod is null ? null : Throws(), indexer.SetMethod is null ? null : Throws()),
            IPropertySymbol property => generator.PropertyDeclaration(property,
                property.GetMethod is null ? null : Throws(), property.SetMethod is null ? null : Throws()),
            IEventSymbol ev when explicitly => generator.CustomEventDeclaration(ev.Name, generator.TypeExpression(ev.Type),
                addAccessorStatements: Throws(), removeAccessorStatements: Throws()),
            IEventSymbol ev => generator.EventDeclaration(ev),
            _ => throw new NotSupportedException($"Unsupported interface member kind: {member.Kind}")
        };

        declaration = generator.WithModifiers(declaration, modifiers);
        declaration = StripConstraintClauses(declaration);

        // The symbol-based factories copy the interface member's 'abstract' modifier and so drop
        // the bodies; put them back now that the declaration is a concrete member.
        declaration = member switch
        {
            IMethodSymbol => generator.WithStatements(declaration, Throws()),
            IPropertySymbol p => WithAccessorBodies(generator, declaration, p, Throws, Throws),
            _ => declaration
        };

        if (explicitly)
        {
            return generator.AsPrivateInterfaceImplementation(
                declaration, generator.TypeExpression(member.ContainingType), member.Name);
        }
        return generator.WithAccessibility(declaration, Accessibility.Public);
    }

    private static SyntaxNode BuildOverrideStub(
        SyntaxGenerator generator, INamedTypeSymbol type, ISymbol member, bool callBase, SyntaxNode? throwStatement)
    {
        Func<SyntaxNode[]> Body(Func<SyntaxNode[]> baseCall) => callBase ? baseCall : () => [throwStatement!];
        var baseAccess = generator.MemberAccessExpression(generator.BaseExpression(), generator.IdentifierName(member.Name));
        var value = generator.IdentifierName("value");

        SyntaxNode declaration;
        switch (member)
        {
            case IMethodSymbol method:
            {
                var body = Body(() =>
                {
                    var target = method.TypeParameters.Length == 0
                        ? baseAccess
                        : generator.MemberAccessExpression(generator.BaseExpression(),
                            generator.GenericName(method.Name, method.TypeParameters.Select(tp => generator.IdentifierName(tp.Name))));
                    var call = generator.InvocationExpression(target, method.Parameters.Select(p =>
                        generator.Argument(p.RefKind == RefKind.In ? RefKind.None : p.RefKind, generator.IdentifierName(p.Name))));
                    return method.ReturnsVoid ? [generator.ExpressionStatement(call)] : [generator.ReturnStatement(call)];
                });
                declaration = generator.MethodDeclaration(method, body());
                declaration = generator.WithModifiers(declaration, DeclarationModifiers.Override);
                declaration = generator.WithStatements(declaration, body());
                break;
            }
            case IPropertySymbol property:
            {
                var access = property.IsIndexer ? IndexerAccess(generator, property) : baseAccess;
                var getBody = Body(() => [generator.ReturnStatement(access)]);
                var setBody = Body(() => [generator.AssignmentStatement(access, value)]);
                declaration = property.IsIndexer
                    ? generator.IndexerDeclaration(property, getBody(), setBody())
                    : generator.PropertyDeclaration(property, getBody(), setBody());
                declaration = generator.WithModifiers(declaration, DeclarationModifiers.Override);
                declaration = WithAccessorBodies(generator, declaration, property, getBody, setBody);
                break;
            }
            case IEventSymbol ev:
                declaration = generator.CustomEventDeclaration(ev.Name, generator.TypeExpression(ev.Type),
                    modifiers: DeclarationModifiers.Override,
                    addAccessorStatements: Body(() => [generator.AddEventHandler(baseAccess, value)])(),
                    removeAccessorStatements: Body(() => [generator.RemoveEventHandler(baseAccess, value)])());
                break;
            default:
                throw new NotSupportedException($"Unsupported member kind for an override: {member.Kind}");
        }

        declaration = generator.WithAccessibility(declaration, OverrideAccessibility(member, type));
        return StripConstraintClauses(declaration);
    }

    /// <summary>
    /// The symbol-based SyntaxGenerator factories copy the source member's modifiers; for an abstract
    /// (or interface) member that drops the bodies, so they are re-applied once the modifiers are reset.
    /// </summary>
    private static SyntaxNode WithAccessorBodies(
        SyntaxGenerator generator, SyntaxNode declaration, IPropertySymbol property, Func<SyntaxNode[]> getBody, Func<SyntaxNode[]> setBody)
    {
        if (property.GetMethod is not null) declaration = generator.WithGetAccessorStatements(declaration, getBody());
        if (property.SetMethod is not null) declaration = generator.WithSetAccessorStatements(declaration, setBody());
        return declaration;
    }

    private static SyntaxNode IndexerAccess(SyntaxGenerator generator, IPropertySymbol indexer) =>
        generator.ElementAccessExpression(generator.BaseExpression(),
            indexer.Parameters.Select(p => generator.Argument(generator.IdentifierName(p.Name))));

    /// <summary>Overrides and explicit implementations inherit constraints; repeating them is CS0460.</summary>
    private static SyntaxNode StripConstraintClauses(SyntaxNode declaration) =>
        declaration is MethodDeclarationSyntax { ConstraintClauses.Count: > 0 } method
            ? method.WithConstraintClauses(default)
            : declaration;

    /// <summary>The member's source XML doc comment as trimmed lines, or null (caller emits inheritdoc).</summary>
    private static IReadOnlyList<string>? DocLinesOf(ISymbol member)
    {
        var syntax = member.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        var declaration = syntax?.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        var docTrivia = declaration?.GetLeadingTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                        t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .ToList();
        if (docTrivia is not { Count: > 0 }) return null;

        var lines = string.Concat(docTrivia.Select(t => t.ToFullString()))
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        return lines.Count > 0 ? lines : null;
    }

    // ---------------------------------------------------------------- shared: insertion + gate

    private sealed record InsertionPlan(
        Document Document,
        TypeDeclarationSyntax Declaration,
        SyntaxGenerator Generator,
        IReadOnlyList<string> DeclarationFiles);

    private sealed record Outcome(
        MutationResult Result,
        List<object> Changes,
        string InsertedInto,
        IReadOnlyList<string> DeclarationFiles,
        bool AddedUsingSystem);

    private static async Task<InsertionPlan> PlanInsertionAsync(INamedTypeSymbol type)
    {
        var solution = WorkspaceHost.Current;
        var first = type.DeclaringSyntaxReferences[0];
        var document = solution.GetDocument(first.SyntaxTree)
            ?? throw new InvalidOperationException($"The first declaration of '{type.Name}' is not in a document of the loaded solution.");
        var declaration = await first.GetSyntaxAsync() as TypeDeclarationSyntax
            ?? throw new InvalidOperationException($"'{type.Name}' has no type declaration body to insert into.");

        var files = type.DeclaringSyntaxReferences
            .Select(r => r.SyntaxTree.FilePath)
            .Distinct()
            .ToList();
        return new InsertionPlan(document, declaration, SyntaxGenerator.GetGenerator(document), files);
    }

    private static async Task<Outcome> InsertAndApplyAsync(
        INamedTypeSymbol type, InsertionPlan plan, List<Stub> stubs, bool needsNotImplemented, bool dryRun)
    {
        var solution = WorkspaceHost.Current;
        var document = plan.Document;
        var generator = plan.Generator;
        var root = await document.GetSyntaxRootAsync()
            ?? throw new InvalidOperationException("No syntax tree available for the type's document.");

        var addUsingSystem = false;
        if (needsNotImplemented)
        {
            var model = await document.GetSemanticModelAsync()
                ?? throw new InvalidOperationException("No semantic model available for the type's document.");
            var position = plan.Declaration.CloseBraceToken.SpanStart;
            addUsingSystem = !model.LookupNamespacesAndTypes(position, name: "NotImplementedException")
                .Any(s => s.ToDisplayString() == "System.NotImplementedException");
        }

        // One annotation per stub so the doc comment pass can find each member after formatting.
        var markers = stubs.Select((_, i) => new SyntaxAnnotation("RoslynMcp.GeneratedStub", i.ToString())).ToList();
        var annotated = stubs
            .Select((s, i) => s.Node.WithAdditionalAnnotations(markers[i], Simplifier.Annotation, Formatter.Annotation))
            .ToList();

        var newRoot = root.ReplaceNode(plan.Declaration, generator.AddMembers(plan.Declaration, annotated));
        if (addUsingSystem)
        {
            newRoot = generator.AddNamespaceImports(newRoot,
                generator.NamespaceImportDeclaration("System").WithAdditionalAnnotations(Formatter.Annotation));
        }

        // Simplify (global::System.String -> string) and format ONLY the inserted nodes.
        var working = document.WithSyntaxRoot(newRoot);
        working = await Simplifier.ReduceAsync(working, Simplifier.Annotation);
        working = await Formatter.FormatAsync(working, Formatter.Annotation);

        // Doc comments go in as text after formatting: attaching structured doc trivia to nodes
        // that then move between trees corrupts the compiler's doc bookkeeping (see extract_interface).
        var formattedRoot = (await working.GetSyntaxRootAsync())!;
        var text = await working.GetTextAsync();
        var eol = DetectEndOfLine(await document.GetTextAsync());
        var docInsertions = new List<TextChange>();
        for (var i = 0; i < stubs.Count; i++)
        {
            var node = formattedRoot.GetAnnotatedNodes(markers[i]).Single();
            var lineInfo = text.Lines.GetLineFromPosition(node.SpanStart);
            var indent = text.ToString(TextSpan.FromBounds(lineInfo.Start, node.SpanStart));
            if (!string.IsNullOrWhiteSpace(indent)) indent = "";
            var lines = stubs[i].DocLines ?? ["/// <inheritdoc />"];
            docInsertions.Add(new TextChange(new TextSpan(lineInfo.Start, 0),
                string.Concat(lines.Select(l => indent + l + eol))));
        }

        // Re-enter through text so the candidate is a clean parse of exactly what will be written.
        var candidate = solution.WithDocumentText(document.Id, text.WithChanges(docInsertions));

        var changes = await RefactorTools.DescribeDocumentChangesAsync(solution, candidate, MaxChangedDocuments);
        var result = await MutationApplier.ApplyOrPreviewAsync(candidate, dryRun);
        return new Outcome(result, changes, document.FilePath!, plan.DeclarationFiles, addUsingSystem);
    }

    private static string DetectEndOfLine(SourceText text)
    {
        foreach (var line in text.Lines)
        {
            var breakLength = line.EndIncludingLineBreak - line.End;
            if (breakLength > 0) return text.ToString(TextSpan.FromBounds(line.End, line.EndIncludingLineBreak));
        }
        return "\n";
    }

    // ---------------------------------------------------------------- shared: JSON

    private static object Refusal(object common, bool dryRun, string message) => Merge(common, new
    {
        applied = false,
        dryRun = RefactorTools.DryRunFlag(dryRun),
        wouldApply = dryRun ? false : (bool?)null,
        changedFileCount = 0,
        changes = Array.Empty<object>(),
        message
    });

    private static object Result(object common, Outcome outcome, IReadOnlyList<ISymbol> generated, bool dryRun, object? extra)
    {
        var result = outcome.Result;
        var partial = outcome.DeclarationFiles.Count > 1;

        string? message = result.Message;
        if (result.Accepted)
        {
            var where = partial
                ? $"Stubs {(result.Applied ? "inserted" : "would be inserted")} into the first declaration of partial type in '{outcome.InsertedInto}' (declared in {outcome.DeclarationFiles.Count} files)."
                : null;
            var tail = result.Applied ? null : result.Message;
            message = string.Join(" ", new[] { tail, where }.Where(s => s is not null));
            if (message.Length == 0) message = null;
        }
        else if (result.NewErrors.Count > 0)
        {
            message += " Nothing was written.";
        }

        return Merge(common, new
        {
            generated = generated.Select(m => m.ToDisplayString(MemberFormat)).ToList()
        }, extra, new
        {
            insertedInto = result.Accepted ? outcome.InsertedInto : null,
            partialDeclarationFiles = partial ? outcome.DeclarationFiles : null,
            addedUsingSystem = result.Accepted && outcome.AddedUsingSystem ? true : (bool?)null,
            applied = result.Applied,
            dryRun = RefactorTools.DryRunFlag(dryRun),
            wouldApply = RefactorTools.WouldApplyFlag(dryRun, result),
            changedFileCount = result.Accepted ? outcome.Changes.Count : 0,
            changes = result.Accepted ? outcome.Changes : [],
            conflicts = result.Accepted || result.NewErrors.Count == 0 ? null : result.NewErrors,
            staleFile = result.StaleFile,
            message
        });
    }

    /// <summary>Flattens anonymous objects into one ordered JSON object, dropping null values like ToolJson does.</summary>
    private static Dictionary<string, object?> Merge(params object?[] parts)
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (part is null) continue;
            foreach (var property in part.GetType().GetProperties())
            {
                var value = property.GetValue(part);
                if (value is not null) merged[property.Name] = value;
            }
        }
        return merged;
    }
}
