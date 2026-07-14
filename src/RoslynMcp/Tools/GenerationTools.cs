using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
public static class GenerationTools
{
    [McpServerTool]
    [Description("Generate an interface from a class's public instance members (methods, properties, indexers, events - not statics, not constructors) and add it to the class's base list. Writes IClassName.cs next to the class, carrying generic parameters/constraints and XML doc comments. Compiles the result first via MutationApplier and writes nothing if it wouldn't build.")]
    public static Task<string> ExtractInterface(
        [Description("Absolute path of the file containing the class")] string filePath,
        [Description("1-based line number of the class declaration (or any member of it)")] int line,
        [Description("1-based column")] int column,
        [Description("Optional: extract only these member names (default: all eligible public instance members)")] string[]? memberFilter = null,
        [Description("Add the generated interface to the class's base list (default true)")] bool addToBaseList = true)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var symbol = await NavigationTools.ResolveSymbolAsync(filePath, line, column);
            var classSymbol = (symbol as INamedTypeSymbol ?? symbol.ContainingType) is { TypeKind: TypeKind.Class } ct
                ? ct
                : throw new InvalidOperationException(
                    $"Position must be on a class declaration or a member of one (found {symbol.Kind} '{symbol.Name}').");

            var classDocument = WorkspaceHost.GetDocument(filePath);
            var generator = SyntaxGenerator.GetGenerator(classDocument);

            var eligibleMembers = classSymbol.GetMembers()
                .Where(m => m.DeclaredAccessibility == Accessibility.Public && !m.IsStatic && !m.IsImplicitlyDeclared)
                .Where(m => m is IPropertySymbol or IEventSymbol || m is IMethodSymbol { MethodKind: MethodKind.Ordinary })
                .ToList();

            if (memberFilter is { Length: > 0 })
            {
                var wanted = memberFilter.ToHashSet(StringComparer.Ordinal);
                var filtered = eligibleMembers.Where(m => wanted.Contains(m.Name)).ToList();
                var missing = wanted.Except(filtered.Select(m => m.Name)).ToList();
                if (missing.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"memberFilter names not found as eligible public instance members of '{classSymbol.Name}': {string.Join(", ", missing)}");
                }
                eligibleMembers = filtered;
            }

            if (eligibleMembers.Count == 0)
            {
                throw new InvalidOperationException(
                    $"'{classSymbol.Name}' has no eligible public instance members (methods/properties/indexers/events) to extract.");
            }

            var interfaceName = "I" + classSymbol.Name;
            var interfaceMembers = eligibleMembers.Select(m => WithDocComment(BuildInterfaceMember(generator, m), m)).ToList();

            var interfaceDeclaration = generator.InterfaceDeclaration(
                interfaceName,
                typeParameters: classSymbol.TypeParameters.Select(tp => tp.Name),
                accessibility: Accessibility.Public,
                members: interfaceMembers);

            foreach (var typeParameter in classSymbol.TypeParameters)
            {
                interfaceDeclaration = ApplyTypeConstraint(generator, interfaceDeclaration, typeParameter);
            }

            var classSyntax = (ClassDeclarationSyntax)(await classSymbol.DeclaringSyntaxReferences
                .First(r => r.SyntaxTree.FilePath == classDocument.FilePath)
                .GetSyntaxAsync());

            var classRoot = await classDocument.GetSyntaxRootAsync()
                ?? throw new InvalidOperationException("No syntax tree available for the class's document.");
            var isFileScopedNamespace = classRoot.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()
                is FileScopedNamespaceDeclarationSyntax;

            var namespaceName = classSymbol.ContainingNamespace.IsGlobalNamespace
                ? null
                : classSymbol.ContainingNamespace.ToDisplayString();

            MemberDeclarationSyntax interfaceMember = (MemberDeclarationSyntax)interfaceDeclaration;
            MemberDeclarationSyntax rootMember = namespaceName is null
                ? interfaceMember
                : isFileScopedNamespace
                    ? SyntaxFactory.FileScopedNamespaceDeclaration(SyntaxFactory.ParseName(namespaceName)).AddMembers(interfaceMember)
                    : SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(namespaceName)).AddMembers(interfaceMember);

            var interfaceFileRoot = SyntaxFactory.CompilationUnit().AddMembers(rootMember).NormalizeWhitespace();
            var interfaceFileName = $"{interfaceName}.cs";
            var interfaceFilePath = Path.Combine(Path.GetDirectoryName(classDocument.FilePath!)!, interfaceFileName);

            var candidateSolution = WorkspaceHost.Current;

            if (addToBaseList)
            {
                var typeArgs = classSymbol.TypeParameters.Length == 0
                    ? ""
                    : "<" + string.Join(", ", classSymbol.TypeParameters.Select(tp => tp.Name)) + ">";
                var interfaceTypeExpression = SyntaxFactory.ParseTypeName(interfaceName + typeArgs);

                var editor = await DocumentEditor.CreateAsync(classDocument);
                editor.ReplaceNode(classSyntax, generator.AddInterfaceType(classSyntax, interfaceTypeExpression));
                candidateSolution = editor.GetChangedDocument().Project.Solution;
            }

            // Add via source text, not the SyntaxNode directly: a node produced by SyntaxGenerator
            // and NormalizeWhitespace carries annotations that - combined with copied XML doc
            // comment trivia - corrupt the compiler's doc-comment bookkeeping (spurious CS1569)
            // when attached straight onto a Document. Re-parsing from text sidesteps that.
            candidateSolution = candidateSolution.AddDocument(
                DocumentId.CreateNewId(classDocument.Project.Id),
                interfaceFileName,
                Microsoft.CodeAnalysis.Text.SourceText.From(interfaceFileRoot.ToFullString()),
                classDocument.Folders,
                interfaceFilePath);

            var result = await MutationApplier.TryApplyAsync(candidateSolution);

            return new
            {
                @class = NavigationTools.Describe(classSymbol),
                interfaceName,
                memberCount = eligibleMembers.Count,
                members = eligibleMembers.Select(m => m.Name).ToList(),
                applied = result.Applied,
                interfaceFilePath = result.Applied ? interfaceFilePath : null,
                addedToBaseList = result.Applied && addToBaseList,
                conflicts = result.Applied || result.NewErrors.Count == 0 ? null : result.NewErrors,
                staleFile = result.StaleFile,
                message = result.Message
            };
        });
    }

    private static SyntaxNode BuildInterfaceMember(SyntaxGenerator generator, ISymbol member) => member switch
    {
        IPropertySymbol { IsIndexer: true } indexer => generator.IndexerDeclaration(indexer),
        IPropertySymbol property => generator.PropertyDeclaration(property),
        IEventSymbol ev => generator.EventDeclaration(ev),
        IMethodSymbol method => generator.MethodDeclaration(method, statements: null),
        _ => throw new NotSupportedException($"Unsupported member kind for extract_interface: {member.Kind}")
    };

    /// <summary>Copies the member's XML doc comment (if any) onto the generated interface member.</summary>
    private static SyntaxNode WithDocComment(SyntaxNode generatedMember, ISymbol originalMember)
    {
        var originalSyntax = originalMember.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        var docTrivia = originalSyntax?.GetLeadingTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                        t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .ToList();
        if (docTrivia is not { Count: > 0 }) return generatedMember;

        // Re-parse as fresh trivia rather than reusing the structured nodes as-is: carrying
        // a DocumentationCommentTriviaSyntax across trees (and through NormalizeWhitespace)
        // corrupts its internal span bookkeeping and produces a bogus CS1569 at compile time.
        var freshTrivia = SyntaxFactory.ParseLeadingTrivia(string.Concat(docTrivia.Select(t => t.ToFullString())));
        return generatedMember.WithLeadingTrivia(freshTrivia);
    }

    private static SyntaxNode ApplyTypeConstraint(SyntaxGenerator generator, SyntaxNode declaration, ITypeParameterSymbol typeParameter)
    {
        var kind = SpecialTypeConstraintKind.None;
        if (typeParameter.HasReferenceTypeConstraint) kind |= SpecialTypeConstraintKind.ReferenceType;
        if (typeParameter.HasValueTypeConstraint) kind |= SpecialTypeConstraintKind.ValueType;
        if (typeParameter.HasConstructorConstraint) kind |= SpecialTypeConstraintKind.Constructor;

        var constraintTypes = typeParameter.ConstraintTypes.Select(generator.TypeExpression).ToArray();
        if (kind == SpecialTypeConstraintKind.None && constraintTypes.Length == 0) return declaration;

        return generator.WithTypeConstraint(declaration, typeParameter.Name, kind, constraintTypes);
    }
}
