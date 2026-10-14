using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

/// <summary>
/// move_type_to_namespace and rename_namespace. Every decision is made on Roslyn symbols and
/// syntax (SymbolFinder for references, the semantic model for what a name binds to and what is
/// already in scope); edits are syntax-node replacements plus span splices at declaration
/// boundaries. Comments and string literals are never inspected, so they never change.
/// </summary>
[McpServerToolType]
public static class NamespaceTools
{
    private const int MaxChangedDocuments = 100;
    private const int MaxListedFiles = 5;

    /// <summary>Namespace-qualified type names without C# keyword aliases ('System.String', not 'string').</summary>
    private static readonly SymbolDisplayFormat QualifiedTypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    // ------------------------------------------------------------------ move_type_to_namespace

    [McpServerTool]
    [Description("Move one top-level type into another namespace, solution-wide and semantically: every declaration of the type (all partial parts, across files) moves, and every reference keeps resolving - fully-qualified references are re-qualified (Old.Ns.Type -> New.Ns.Type) and files that reach the type by simple name get 'using New.Ns;' when the new namespace is not already in scope. Declaration mechanics per file: if the namespace declaration holds only this type, the declaration itself is renamed (file-scoped or block); if other types share a block namespace, the type (with its doc comments and attributes) moves into a new 'namespace New.Ns { }' block at the end of the same file; if other types share a FILE-SCOPED namespace (C# forbids a second namespace there), the type moves into a new file <TypeName>.cs next to it. Names the moved type relied on from its old namespace get a using (or are qualified when a same-named type in the new namespace would capture them). Refuses nested types, metadata types, a no-op move, and a target namespace that already holds a type of the same name. Comments and string literals are never touched. Compiles the result first via MutationApplier and writes nothing if it wouldn't build.")]
    public static Task<string> MoveTypeToNamespace(
        [Description("The destination namespace, e.g. 'MyApp.Inventory' (created if it does not exist yet)")] string newNamespace,
        [Description("Absolute path of the file containing the type (use with line+column)")] string? filePath = null,
        [Description("1-based line number on the type's declaration or any member of it (use with filePath)")] int? line = null,
        [Description("1-based column (use with filePath)")] int? column = null,
        [Description("Fully-qualified metadata name of the type instead of a position, e.g. 'FixtureLib.Catalog.Sku' or 'MyNs.Box`1'")] string? fullyQualifiedName = null,
        [Description("Preview only: run every check and the compile gate, report the exact changes/conflicts the real call would produce, but write nothing (default false).")] bool dryRun = false)
    {
        return ToolJson.GuardAsync(async () =>
        {
            newNamespace = ValidateNamespaceName(newNamespace, nameof(newNamespace));
            var solution = WorkspaceHost.Current;

            if (!string.IsNullOrEmpty(fullyQualifiedName))
            {
                var resolved = await RefactorTools.ResolveByFullyQualifiedNameAsync(solution, fullyQualifiedName);
                if (resolved is INamedTypeSymbol { Locations: var locations } metadataType && !locations.Any(l => l.IsInSource))
                {
                    throw new InvalidOperationException(
                        $"'{metadataType.ToDisplayString(QualifiedTypeFormat)}' is defined in metadata ({metadataType.ContainingAssembly?.Name ?? "a referenced assembly"}), " +
                        "not in this solution's source; move_type_to_namespace only moves source types.");
                }
            }

            var (typeSymbol, _) = await FileOrganizationTools.ResolveTypeAsync(solution, filePath, line, column, fullyQualifiedName);

            if (typeSymbol.ContainingType is not null)
            {
                throw new InvalidOperationException(
                    $"'{typeSymbol.ToDisplayString()}' is nested inside '{typeSymbol.ContainingType.ToDisplayString()}'; " +
                    "move_type_to_namespace only moves top-level types (a nested type lives in its containing type, not in a namespace).");
            }
            if (typeSymbol.DeclaringSyntaxReferences.Length == 0)
            {
                throw new InvalidOperationException($"'{typeSymbol.ToDisplayString()}' has no source declaration in the loaded solution.");
            }

            var oldNamespace = FullName(typeSymbol.ContainingNamespace);
            var typeDisplay = typeSymbol.ToDisplayString();

            object Refuse(string message) => new
            {
                type = NavigationTools.Describe(typeSymbol),
                oldNamespace = DisplayNamespace(oldNamespace),
                newNamespace,
                applied = false,
                dryRun = RefactorTools.DryRunFlag(dryRun),
                wouldApply = dryRun ? false : (bool?)null,
                changedFileCount = 0,
                changes = Array.Empty<object>(),
                message
            };

            if (oldNamespace == newNamespace)
            {
                return Refuse($"Not applied: '{typeDisplay}' is already in namespace '{newNamespace}'. Nothing was written.");
            }

            var collision = await FindTypeCollisionAsync(solution, newNamespace, typeSymbol);
            if (collision is not null)
            {
                return Refuse(
                    $"Not applied: namespace '{newNamespace}' already contains a type named '{typeSymbol.Name}' ({collision}); " +
                    $"moving '{typeDisplay}' there would collide. Nothing was written.");
            }

            var plans = new Dictionary<DocumentId, DocumentPlan>();
            DocumentPlan PlanFor(Document document)
            {
                if (!plans.TryGetValue(document.Id, out var plan))
                {
                    plans[document.Id] = plan = new DocumentPlan(document);
                }
                return plan;
            }

            var globalUsingsByProject = new Dictionary<ProjectId, HashSet<string>>();
            async Task<HashSet<string>> GlobalUsingsAsync(Project project)
            {
                if (!globalUsingsByProject.TryGetValue(project.Id, out var set))
                {
                    globalUsingsByProject[project.Id] = set = await CollectGlobalUsingsAsync(project);
                }
                return set;
            }

            // 1. Declarations: choose the mechanics per enclosing namespace declaration.
            var declarations = new List<(Document Document, MemberDeclarationSyntax Syntax)>();
            foreach (var reference in typeSymbol.DeclaringSyntaxReferences)
            {
                var document = solution.GetDocument(reference.SyntaxTree)
                    ?? throw new InvalidOperationException($"Declaration file is not part of the loaded solution: {reference.SyntaxTree.FilePath}");
                declarations.Add((document, (MemberDeclarationSyntax)await reference.GetSyntaxAsync()));
            }

            var mechanics = new List<object>();
            var declarationSpans = declarations.ToLookup(d => d.Document.Id, d => d.Syntax.Span);

            foreach (var group in declarations.GroupBy(d => (d.Document.Id, Namespace: d.Syntax.Parent as BaseNamespaceDeclarationSyntax)))
            {
                var document = group.First().Document;
                var plan = PlanFor(document);
                var parts = group.Select(g => g.Syntax).ToList();
                var enclosing = group.Key.Namespace;
                var model = await document.GetSemanticModelAsync()
                    ?? throw new InvalidOperationException("No semantic model available for the document.");
                var globalUsings = await GlobalUsingsAsync(document.Project);

                var needed = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var part in parts)
                {
                    AnalyzeMovedDeclaration(part, typeSymbol, model, oldNamespace, newNamespace, globalUsings, needed, plan.Replacements);
                }

                var holdsOnlyThisType = enclosing is not null
                    && enclosing.Parent is CompilationUnitSyntax
                    && enclosing.Members.All(m => parts.Contains(m));

                if (holdsOnlyThisType)
                {
                    plan.NamespaceRenames.Add((enclosing!, newNamespace));
                    plan.TopUsings.UnionWith(needed);
                    mechanics.Add(new { file = document.FilePath, mechanics = "renamedNamespaceDeclaration" });
                }
                else if (enclosing is FileScopedNamespaceDeclarationSyntax)
                {
                    plan.MoveToNewFile.AddRange(parts);
                    plan.NewFileUsings.UnionWith(needed);
                }
                else
                {
                    plan.MoveToBlock.AddRange(parts);
                    plan.BlockUsings.UnionWith(needed);
                    mechanics.Add(new { file = document.FilePath, mechanics = "movedIntoNewBlockAtEndOfFile" });
                }
            }

            // 2. References: re-qualify qualified ones, add 'using newNamespace;' where a simple name would stop resolving.
            var referencedSymbols = await SymbolFinder.FindReferencesAsync(typeSymbol, solution);
            var seen = new HashSet<(DocumentId, TextSpan)>();
            foreach (var location in referencedSymbols.SelectMany(r => r.Locations))
            {
                if (location.IsImplicit || location.IsCandidateLocation || !location.Location.IsInSource) continue;
                if (!seen.Add((location.Document.Id, location.Location.SourceSpan))) continue;

                var document = location.Document;
                var root = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                if (root.FindNode(location.Location.SourceSpan, findInsideTrivia: true, getInnermostNodeForTie: true) is not SimpleNameSyntax name
                    || !NamesType(name, typeSymbol)
                    || model.GetAliasInfo(name) is not null)
                {
                    continue;
                }

                var plan = PlanFor(document);
                var qualifier = QualifierOf(name);
                if (qualifier is not null)
                {
                    if (model.GetSymbolInfo(qualifier).Symbol is INamespaceSymbol qualifierNamespace)
                    {
                        plan.Replacements[qualifier] = model.GetAliasInfo(qualifier) is not null
                            ? newNamespace
                            : RequalifiedText(qualifier, FullName(qualifierNamespace), newNamespace);
                    }
                    continue;
                }

                if (name.Parent is AliasQualifiedNameSyntax { Alias.Identifier.RawKind: (int)SyntaxKind.GlobalKeyword } globalQualified
                    && globalQualified.Name == name)
                {
                    plan.Replacements[globalQualified] = $"global::{newNamespace}.{name.ToString()}";
                    continue;
                }

                if (declarationSpans[document.Id].Any(span => span.Contains(name.Span))) continue;

                if (!IsNamespaceInScope(name, model, newNamespace, await GlobalUsingsAsync(document.Project)))
                {
                    plan.TopUsings.Add(newNamespace);
                }
            }

            // 3. Build the candidate solution.
            var candidate = solution;
            var addedFiles = new List<(string Path, string Text)>();
            var plannedPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var plan in plans.Values)
            {
                var (newText, newFile) = await ApplyPlanAsync(solution, plan, newNamespace, typeSymbol, plannedPaths);
                candidate = candidate.WithDocumentText(plan.Document.Id, newText);
                if (newFile is { } file)
                {
                    candidate = candidate.AddDocument(
                        DocumentId.CreateNewId(plan.Document.Project.Id),
                        Path.GetFileName(file.Path),
                        SourceText.From(file.Text, newText.Encoding ?? new UTF8Encoding(false)),
                        plan.Document.Folders,
                        file.Path);
                    addedFiles.Add(file);
                    mechanics.Add(new { file = plan.Document.FilePath, mechanics = "movedIntoNewFile", newFile = file.Path });
                }
            }

            var documentChanges = await RefactorTools.DescribeDocumentChangesAsync(solution, candidate, MaxChangedDocuments);
            var result = await MutationApplier.ApplyOrPreviewAsync(candidate, dryRun);

            var changes = new List<object>();
            if (result.Accepted)
            {
                changes.AddRange(documentChanges);
                changes.AddRange(addedFiles.Select(f => (object)new
                {
                    file = f.Path,
                    added = true,
                    edits = new[] { new { line = 1, oldText = "", newText = f.Text } }
                }));
            }

            return new
            {
                type = NavigationTools.Describe(typeSymbol),
                oldNamespace = DisplayNamespace(oldNamespace),
                newNamespace,
                declarations = mechanics,
                applied = result.Applied,
                dryRun = RefactorTools.DryRunFlag(dryRun),
                wouldApply = RefactorTools.WouldApplyFlag(dryRun, result),
                changedFileCount = result.Accepted ? changes.Count : 0,
                changes,
                conflicts = result.Accepted || result.NewErrors.Count == 0 ? null : result.NewErrors,
                staleFile = result.StaleFile,
                message = result.Message
            };
        });
    }

    private sealed class DocumentPlan(Document document)
    {
        public Document Document { get; } = document;
        /// <summary>Original node -> replacement name text (namespace re-qualifications, needed qualifications).</summary>
        public Dictionary<SyntaxNode, string> Replacements { get; } = new();
        public List<(BaseNamespaceDeclarationSyntax Declaration, string NewName)> NamespaceRenames { get; } = new();
        public List<MemberDeclarationSyntax> MoveToBlock { get; } = new();
        public List<MemberDeclarationSyntax> MoveToNewFile { get; } = new();
        /// <summary>Usings to add at compilation-unit level.</summary>
        public SortedSet<string> TopUsings { get; } = new(StringComparer.Ordinal);
        /// <summary>Usings the moved declarations need inside the new namespace block.</summary>
        public SortedSet<string> BlockUsings { get; } = new(StringComparer.Ordinal);
        /// <summary>Usings the moved declarations need in the new file.</summary>
        public SortedSet<string> NewFileUsings { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Finds what the moved declaration relied on from its OLD enclosing namespaces (names that
    /// resolved only because the declaration sat inside Old.Ns or one of its parents, and which the
    /// new namespace's parents do not supply). Types and extension methods get a using of their
    /// namespace (or full qualification when a same-named type in the new scope would capture the
    /// simple name); namespaces referenced by a relative simple name are fully qualified, because a
    /// using directive never brings child namespaces into scope.
    /// </summary>
    private static void AnalyzeMovedDeclaration(
        MemberDeclarationSyntax declaration, INamedTypeSymbol movedType, SemanticModel model,
        string oldNamespace, string newNamespace, ISet<string> globalUsings,
        ISet<string> neededUsings, Dictionary<SyntaxNode, string> replacements)
    {
        var oldChain = NamespaceChain(oldNamespace);
        var newChain = NamespaceChain(newNamespace);

        bool ReliedOnOldScope(string ns, SyntaxNode at) =>
            ns.Length > 0 && oldChain.Contains(ns) && !newChain.Contains(ns) && !IsImportedByUsing(at, model, ns, globalUsings);

        foreach (var name in declaration.DescendantNodes(descendIntoTrivia: true).OfType<SimpleNameSyntax>())
        {
            if (model.GetAliasInfo(name) is not null) continue;

            var info = model.GetSymbolInfo(name);
            var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
            if (symbol is null) continue;

            if (symbol is IMethodSymbol { ReducedFrom: not null } or IMethodSymbol { IsExtensionMethod: true })
            {
                var extensionNamespace = FullName(symbol.ContainingType.ContainingNamespace);
                if (ReliedOnOldScope(extensionNamespace, name)) neededUsings.Add(extensionNamespace);
                continue;
            }

            if (QualifierOf(name) is not null || name.Parent is AliasQualifiedNameSyntax aq && aq.Name == name) continue;

            if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor) symbol = constructor.ContainingType;

            switch (symbol)
            {
                case INamedTypeSymbol type when type.ContainingType is null
                                                && type.TypeKind != TypeKind.Error
                                                && !SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, movedType.OriginalDefinition):
                {
                    var typeNamespace = FullName(type.ContainingNamespace);
                    if (!ReliedOnOldScope(typeNamespace, name)) break;

                    var captured = name is IdentifierNameSyntax
                        && newChain.Any(ns => FindNamespace(model.Compilation, ns)?.GetTypeMembers(type.Name).Any() == true);
                    if (captured)
                    {
                        replacements[name] = $"{typeNamespace}.{name.Identifier.Text}";
                    }
                    else
                    {
                        neededUsings.Add(typeNamespace);
                    }
                    break;
                }
                case INamespaceSymbol { IsGlobalNamespace: false } ns:
                {
                    var parent = FullName(ns.ContainingNamespace);
                    if (parent.Length > 0 && oldChain.Contains(parent) && !newChain.Contains(parent))
                    {
                        replacements[name] = FullName(ns);
                    }
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Applies one document's plan: node replacements and namespace-declaration renames through a
    /// SyntaxEditor, usings added at compilation-unit level, then the moved declarations (located
    /// by annotation, so replacements inside them are kept) spliced out and appended as a new block
    /// or emitted as a new file.
    /// </summary>
    private static async Task<(SourceText Text, (string Path, string Text)? NewFile)> ApplyPlanAsync(
        Solution solution, DocumentPlan plan, string newNamespace, INamedTypeSymbol movedType, HashSet<string> plannedPaths)
    {
        var document = plan.Document;
        var originalText = await document.GetTextAsync();
        var originalRoot = (CompilationUnitSyntax)(await document.GetSyntaxRootAsync())!;
        var newline = FileOrganizationTools.DetectNewline(originalText.ToString());

        var moveAnnotation = new SyntaxAnnotation("RoslynMcp.MoveTypeToNamespace");
        var editor = new SyntaxEditor(originalRoot, solution.Services);

        var replacements = plan.Replacements
            .Where(r => !plan.Replacements.Keys.Any(other => other != r.Key && other.Span.Contains(r.Key.Span) && other.Span != r.Key.Span))
            .ToList();
        foreach (var (node, replacement) in replacements)
        {
            editor.ReplaceNode(node, (current, _) => SyntaxFactory.ParseName(replacement).WithTriviaFrom(current));
        }
        foreach (var (declaration, newName) in plan.NamespaceRenames)
        {
            editor.ReplaceNode(declaration.Name, (current, _) => SyntaxFactory.ParseName(newName).WithTriviaFrom(current));
        }
        foreach (var moved in plan.MoveToBlock.Concat(plan.MoveToNewFile))
        {
            editor.ReplaceNode(moved, (current, _) => current.WithAdditionalAnnotations(moveAnnotation));
        }

        var root = (CompilationUnitSyntax)editor.GetChangedRoot();
        root = AddUsings(root, plan.TopUsings, newline);

        var movedNodes = root.GetAnnotatedNodes(moveAnnotation).OfType<MemberDeclarationSyntax>().ToList();
        var text = SourceText.From(root.ToFullString(), originalText.Encoding, originalText.ChecksumAlgorithm);
        if (movedNodes.Count == 0)
        {
            return (text, null);
        }

        var movedTexts = movedNodes
            .Select(n => n.WithLeadingTrivia(FileOrganizationTools.TrimLeadingBlankLines(n.GetLeadingTrivia())).ToFullString())
            .Select(t => t.EndsWith('\n') ? t : t + newline)
            .ToList();
        foreach (var moved in movedNodes.OrderByDescending(n => n.FullSpan.Start))
        {
            text = FileOrganizationTools.RemoveDeclaration(text, moved);
        }

        if (plan.MoveToBlock.Count > 0)
        {
            var indent = IndentOf(movedNodes[0]);
            var block = new StringBuilder();
            block.Append(newline).Append("namespace ").Append(newNamespace).Append(newline).Append('{').Append(newline);

            var usings = new StringBuilder();
            foreach (var ns in plan.BlockUsings) usings.Append(indent).Append("using ").Append(ns).Append(';').Append(newline);
            foreach (var enclosing in movedNodes[0].Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse())
            {
                foreach (var directive in enclosing.Usings)
                {
                    usings.Append(directive.WithLeadingTrivia(FileOrganizationTools.TrimLeadingBlankLines(directive.GetLeadingTrivia())).ToFullString());
                }
            }
            if (usings.Length > 0) block.Append(usings).Append(newline);

            block.Append(string.Join(newline, movedTexts));
            block.Append('}').Append(newline);

            var current = text.ToString();
            if (!current.EndsWith('\n')) current += newline;
            return (SourceText.From(current + block, originalText.Encoding, originalText.ChecksumAlgorithm), null);
        }

        // File-scoped namespace shared with other types: C# allows only one namespace there, so the
        // moved part goes to a new file in the same directory, carrying the source file's usings.
        var sourcePath = document.FilePath ?? throw new InvalidOperationException("The declaring document has no file path.");
        var newPath = ChooseNewFilePath(solution, sourcePath, movedType, plannedPaths);
        var fileScoped = originalRoot.Members.OfType<FileScopedNamespaceDeclarationSyntax>().First();

        var header = new StringBuilder();
        foreach (var directive in originalRoot.Externs) header.Append(directive.ToFullString());
        foreach (var directive in originalRoot.Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None))) header.Append(directive.ToFullString());
        var existing = originalRoot.Usings.Concat(fileScoped.Usings).Where(IsPlainUsing).Select(u => NameText(u.Name!)).ToHashSet();
        foreach (var ns in plan.NewFileUsings.Where(ns => !existing.Contains(ns))) header.Append("using ").Append(ns).Append(';').Append(newline);

        var body = new StringBuilder();
        if (header.Length > 0) body.Append(header.ToString().TrimStart('\r', '\n')).Append(newline);
        body.Append("namespace ").Append(newNamespace).Append(';').Append(newline).Append(newline);
        if (fileScoped.Usings.Count > 0)
        {
            foreach (var directive in fileScoped.Usings)
            {
                body.Append(directive.WithLeadingTrivia(FileOrganizationTools.TrimLeadingBlankLines(directive.GetLeadingTrivia())).ToFullString());
            }
            body.Append(newline);
        }
        body.Append(string.Join(newline, movedTexts));

        return (text, (newPath, body.ToString()));
    }

    private static string ChooseNewFilePath(Solution solution, string sourcePath, INamedTypeSymbol type, HashSet<string> plannedPaths)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var candidates = new[]
        {
            Path.Combine(directory, type.Name + ".cs"),
            Path.Combine(directory, Path.GetFileNameWithoutExtension(sourcePath) + "." + type.Name + ".cs")
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path) && !solution.GetDocumentIdsWithFilePath(path).Any() && plannedPaths.Add(path))
            {
                return path;
            }
        }
        throw new InvalidOperationException(
            $"'{type.Name}' shares a file-scoped namespace with other types in {Path.GetFileName(sourcePath)}, so it must move to a new file, " +
            $"but both {string.Join(" and ", candidates.Select(Path.GetFileName))} already exist. Move it with move_type_to_file first.");
    }

    /// <summary>True if the simple name spells the type's name (or, for an attribute class, its name without the 'Attribute' suffix).</summary>
    private static bool NamesType(SimpleNameSyntax name, INamedTypeSymbol type)
    {
        var written = name.Identifier.ValueText;
        return written == type.Name
            || (type.Name.EndsWith("Attribute", StringComparison.Ordinal) && written + "Attribute" == type.Name);
    }

    /// <summary>The namespace (or alias) qualifying a type name: the Left of A.B.Type, the Expression of A.B.Type.Member access, the container of a qualified cref.</summary>
    private static SyntaxNode? QualifierOf(SimpleNameSyntax name) => name.Parent switch
    {
        QualifiedNameSyntax q when q.Right == name => q.Left,
        MemberAccessExpressionSyntax m when m.Name == name => m.Expression,
        NameMemberCrefSyntax { Parent: QualifiedCrefSyntax qc } nm when nm.Name == name => qc.Container,
        _ => null
    };

    private static async Task<string?> FindTypeCollisionAsync(Solution solution, string ns, INamedTypeSymbol type)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            var target = compilation is null ? null : FindNamespace(compilation, ns);
            var existing = target?.GetTypeMembers(type.Name, type.Arity).FirstOrDefault();
            if (existing is null) continue;

            var where = existing.Locations.FirstOrDefault(l => l.IsInSource) is { } sourceLocation
                ? Path.GetFileName(sourceLocation.SourceTree!.FilePath)
                : $"assembly {existing.ContainingAssembly?.Name}";
            return $"{existing.ToDisplayString()} in {where}";
        }
        return null;
    }

    private static bool IsNamespaceInScope(SyntaxNode at, SemanticModel model, string ns, ISet<string> globalUsings)
    {
        var enclosing = at.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (enclosing is not null && NamespaceChain(DeclaredName(enclosing)).Contains(ns)) return true;
        return IsImportedByUsing(at, model, ns, globalUsings);
    }

    /// <summary>True if a plain (non-alias, non-static) using directive visible at the node imports the namespace.</summary>
    private static bool IsImportedByUsing(SyntaxNode at, SemanticModel model, string ns, ISet<string> globalUsings)
    {
        if (globalUsings.Contains(ns)) return true;
        foreach (var scope in at.Ancestors())
        {
            var usings = scope switch
            {
                CompilationUnitSyntax unit => unit.Usings,
                BaseNamespaceDeclarationSyntax declaration => declaration.Usings,
                _ => default
            };
            foreach (var directive in usings)
            {
                if (!IsPlainUsing(directive)) continue;
                var bound = model.GetSymbolInfo(directive.Name!).Symbol as INamespaceSymbol;
                if ((bound is not null ? FullName(bound) : NameText(directive.Name!)) == ns) return true;
            }
        }
        return false;
    }

    private static async Task<HashSet<string>> CollectGlobalUsingsAsync(Project project)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in project.Documents)
        {
            if (await document.GetSyntaxRootAsync() is not CompilationUnitSyntax root) continue;
            foreach (var directive in root.Usings)
            {
                if (!directive.GlobalKeyword.IsKind(SyntaxKind.None) && directive.Alias is null
                    && directive.StaticKeyword.IsKind(SyntaxKind.None) && directive.Name is not null)
                {
                    set.Add(NameText(directive.Name));
                }
            }
        }
        return set;
    }

    // ------------------------------------------------------------------ rename_namespace

    [McpServerTool]
    [Description("Rename a namespace across the whole solution: every declaration (block and file-scoped, in every project) and every using, using static, alias and qualified reference, including child namespaces (A.B -> A.C takes A.B.D -> A.C.D with it). Last-segment renames under the same parent go through Roslyn's Renamer; anything else (e.g. 'A.B' -> 'A.X.Y') uses a symbol-driven syntax pass over declarations and namespace-bound names. Refuses when the new namespace already exists (in source or a referenced assembly) unless merge: true, when merging would put two same-named types in one namespace, and when the old namespace has no declarations in source. Comments and string literals are never touched. Compiles the result first via MutationApplier and writes nothing if it wouldn't build.")]
    public static Task<string> RenameNamespace(
        [Description("The namespace to rename, e.g. 'MyApp.Catalog'")] string oldNamespace,
        [Description("The new namespace name, e.g. 'MyApp.Inventory' or 'MyApp.Inventory.Legacy'")] string newNamespace,
        [Description("Allow renaming into a namespace that already exists, merging the two (default false)")] bool merge = false,
        [Description("Preview only: run every check and the compile gate, report the exact changes/conflicts the real call would produce, but write nothing (default false).")] bool dryRun = false)
    {
        return ToolJson.GuardAsync(async () =>
        {
            oldNamespace = ValidateNamespaceName(oldNamespace, nameof(oldNamespace));
            newNamespace = ValidateNamespaceName(newNamespace, nameof(newNamespace));
            var solution = WorkspaceHost.Current;

            object Refuse(string message) => new
            {
                oldNamespace,
                newNamespace,
                merge = merge ? true : (bool?)null,
                applied = false,
                dryRun = RefactorTools.DryRunFlag(dryRun),
                wouldApply = dryRun ? false : (bool?)null,
                changedFileCount = 0,
                changes = Array.Empty<object>(),
                message
            };

            if (oldNamespace == newNamespace)
            {
                return Refuse($"Not applied: the old and new namespace are both '{oldNamespace}'. Nothing was written.");
            }

            var declarations = await CollectNamespaceDeclarationsAsync(solution);
            var oldDeclarations = declarations.Where(d => IsSameOrChild(d.FullName, oldNamespace)).ToList();
            if (oldDeclarations.Count == 0)
            {
                var inMetadata = await ExistsAnywhereAsync(solution, oldNamespace);
                return Refuse(inMetadata
                    ? $"Not applied: '{oldNamespace}' has no namespace declarations in this solution's source (it exists only in referenced assemblies); rename_namespace only renames namespaces declared in source. Nothing was written."
                    : $"Not applied: namespace '{oldNamespace}' is not declared anywhere in the loaded solution. Nothing was written.");
            }

            var existingNew = await DescribeExistingNamespaceAsync(solution, declarations, newNamespace, oldNamespace);
            if (existingNew is not null && !merge)
            {
                return Refuse(
                    $"Not applied: namespace '{newNamespace}' already exists ({existingNew}); renaming '{oldNamespace}' to it would merge the two namespaces. " +
                    "Pass merge: true to do that deliberately. Nothing was written.");
            }
            if (existingNew is not null)
            {
                var typeCollisions = await FindMergeCollisionsAsync(solution, oldNamespace, newNamespace);
                if (typeCollisions.Count > 0)
                {
                    return Refuse(
                        $"Not applied: merging '{oldNamespace}' into '{newNamespace}' would put two types with the same name in one namespace: " +
                        $"{string.Join(", ", typeCollisions.Take(10))}. Nothing was written.");
                }
            }

            var oldSegments = oldNamespace.Split('.');
            var newSegments = newNamespace.Split('.');
            var lastSegmentOnly = oldSegments.Length == newSegments.Length
                && oldSegments.AsSpan(0, oldSegments.Length - 1).SequenceEqual(newSegments.AsSpan(0, newSegments.Length - 1));

            Solution candidate;
            string mechanics;
            if (lastSegmentOnly)
            {
                var namespaceSymbol = await FindSourceNamespaceSymbolAsync(solution, oldDeclarations[0].Document, oldNamespace);
                candidate = await Renamer.RenameSymbolAsync(solution, namespaceSymbol, new SymbolRenameOptions(), newSegments[^1]);
                mechanics = "renamer";
            }
            else
            {
                candidate = await RenameNamespaceBySyntaxAsync(solution, oldNamespace, newNamespace);
                mechanics = "syntax";
            }

            candidate = await RemoveDuplicateUsingsAsync(solution, candidate);

            var changes = await RefactorTools.DescribeDocumentChangesAsync(solution, candidate, MaxChangedDocuments);
            var result = await MutationApplier.ApplyOrPreviewAsync(candidate, dryRun);

            return new
            {
                oldNamespace,
                newNamespace,
                merge = merge ? true : (bool?)null,
                mechanics,
                declarationCount = oldDeclarations.Count,
                applied = result.Applied,
                dryRun = RefactorTools.DryRunFlag(dryRun),
                wouldApply = RefactorTools.WouldApplyFlag(dryRun, result),
                changedFileCount = result.Accepted ? changes.Count : 0,
                changes = result.Accepted ? changes : [],
                conflicts = result.Accepted || result.NewErrors.Count == 0 ? null : result.NewErrors,
                staleFile = result.StaleFile,
                message = result.Message
            };
        });
    }

    private sealed record NamespaceDeclaration(Document Document, BaseNamespaceDeclarationSyntax Syntax, string FullName);

    private static async Task<List<NamespaceDeclaration>> CollectNamespaceDeclarationsAsync(Solution solution)
    {
        var result = new List<NamespaceDeclaration>();
        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
        {
            if (await document.GetSyntaxRootAsync() is not { } root) continue;
            result.AddRange(root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
                .OfType<BaseNamespaceDeclarationSyntax>()
                .Select(d => new NamespaceDeclaration(document, d, DeclaredName(d))));
        }
        return result;
    }

    private static async Task<bool> ExistsAnywhereAsync(Solution solution, string ns)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is not null && FindNamespace(compilation, ns) is not null) return true;
        }
        return false;
    }

    /// <summary>Where the namespace already exists (source files and/or referenced assemblies), or null if it does not.</summary>
    private static async Task<string?> DescribeExistingNamespaceAsync(
        Solution solution, List<NamespaceDeclaration> declarations, string ns, string renamedNamespace)
    {
        // Declarations that are themselves being renamed (old namespace and its children) do not count.
        var sourceFiles = declarations
            .Where(d => IsSameOrChild(d.FullName, ns) && !IsSameOrChild(d.FullName, renamedNamespace))
            .Select(d => Path.GetFileName(d.Document.FilePath ?? d.Document.Name))
            .Distinct()
            .ToList();

        var sourceAssemblies = new HashSet<string>(StringComparer.Ordinal);
        var metadataAssemblies = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            sourceAssemblies.Add(project.AssemblyName);
        }
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            var found = compilation is null ? null : FindNamespace(compilation, ns);
            if (found is null) continue;
            foreach (var constituent in found.ConstituentNamespaces)
            {
                var assembly = constituent.ContainingAssembly?.Name;
                if (assembly is not null && !sourceAssemblies.Contains(assembly)
                    && (constituent.GetTypeMembers().Any() || constituent.GetNamespaceMembers().Any()))
                {
                    metadataAssemblies.Add(assembly);
                }
            }
        }

        if (sourceFiles.Count == 0 && metadataAssemblies.Count == 0) return null;

        var parts = new List<string>();
        if (sourceFiles.Count > 0) parts.Add("declared in " + ListWithMore(sourceFiles));
        if (metadataAssemblies.Count > 0) parts.Add("in referenced assembl" + (metadataAssemblies.Count == 1 ? "y " : "ies ") + ListWithMore(metadataAssemblies.ToList()));
        return string.Join("; ", parts);
    }

    private static string ListWithMore(IReadOnlyList<string> items) =>
        items.Count <= MaxListedFiles
            ? string.Join(", ", items)
            : string.Join(", ", items.Take(MaxListedFiles)) + $" and {items.Count - MaxListedFiles} more";

    /// <summary>Types under the old namespace (children included) whose name already exists at the merge destination.</summary>
    private static async Task<List<string>> FindMergeCollisionsAsync(Solution solution, string oldNamespace, string newNamespace)
    {
        var collisions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            var root = compilation is null ? null : FindNamespace(compilation, oldNamespace);
            if (root is null) continue;

            var pending = new Stack<INamespaceSymbol>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                foreach (var child in current.GetNamespaceMembers()) pending.Push(child);

                var targetName = newNamespace + FullName(current)[oldNamespace.Length..];
                if (IsSameOrChild(targetName, oldNamespace)) continue;
                var target = FindNamespace(compilation!, targetName);
                if (target is null) continue;

                foreach (var type in current.GetTypeMembers().Where(t => t.Locations.Any(l => l.IsInSource)))
                {
                    if (target.GetTypeMembers(type.Name, type.Arity).Any())
                    {
                        collisions.Add($"{targetName}.{type.Name}");
                    }
                }
            }
        }
        return collisions.ToList();
    }

    private static async Task<INamespaceSymbol> FindSourceNamespaceSymbolAsync(Solution solution, Document document, string ns)
    {
        var compilation = await document.Project.GetCompilationAsync()
            ?? throw new InvalidOperationException($"No compilation for project {document.Project.Name}.");
        return FindNamespace(compilation, ns)
            ?? throw new InvalidOperationException($"Namespace '{ns}' not found in project {document.Project.Name}.");
    }

    /// <summary>
    /// The general rename: rewrites every namespace declaration whose full name is Old or Old.*
    /// (relative to any enclosing block namespace), and every name or member-access chain whose
    /// symbol is the namespace Old or a child of it - usings, using static, alias targets, qualified
    /// type names, crefs. Alias identifiers are left alone (their directive is rewritten instead),
    /// and a name written relative to an enclosing namespace that does not mention Old's last
    /// segment needs no change because its context is renamed with it.
    /// </summary>
    private static async Task<Solution> RenameNamespaceBySyntaxAsync(Solution solution, string oldNamespace, string newNamespace)
    {
        var oldLast = oldNamespace.Split('.')[^1];
        var oldSegmentCount = oldNamespace.Split('.').Length;
        var candidate = solution;

        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
        {
            var root = await document.GetSyntaxRootAsync();
            if (root is null) continue;

            var mentions = root.DescendantTokens(descendIntoTrivia: true)
                .Any(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == oldLast);
            if (!mentions) continue;

            var model = await document.GetSemanticModelAsync();
            if (model is null) continue;

            var replacements = new Dictionary<SyntaxNode, string>();

            foreach (var declaration in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
                         .OfType<BaseNamespaceDeclarationSyntax>())
            {
                var outer = declaration.Parent is BaseNamespaceDeclarationSyntax parent ? DeclaredName(parent) : "";
                if (outer.Length > 0 && IsSameOrChild(outer, oldNamespace)) continue;

                var full = DeclaredName(declaration);
                if (!IsSameOrChild(full, oldNamespace)) continue;

                var renamed = newNamespace + full[oldNamespace.Length..];
                if (outer.Length == 0)
                {
                    replacements[declaration.Name] = renamed;
                }
                else if (renamed.StartsWith(outer + ".", StringComparison.Ordinal))
                {
                    replacements[declaration.Name] = renamed[(outer.Length + 1)..];
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Cannot rename: '{full}' is declared inside a 'namespace {outer}' block in {document.FilePath}, " +
                        $"and '{renamed}' is not under '{outer}'. Flatten that block (namespace {full}) first.");
                }
            }

            var matches = new List<(SyntaxNode Node, string Text)>();
            foreach (var node in root.DescendantNodes(descendIntoTrivia: true))
            {
                if (node is not (IdentifierNameSyntax or QualifiedNameSyntax or AliasQualifiedNameSyntax)
                    && !node.IsKind(SyntaxKind.SimpleMemberAccessExpression)) continue;
                if (!node.DescendantTokens().Any(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == oldLast)) continue;
                if (node.AncestorsAndSelf().Any(a => a.Parent is BaseNamespaceDeclarationSyntax nd && nd.Name == a)) continue;
                if (node.Ancestors().Any(a => a is NameEqualsSyntax)) continue;
                if (model.GetAliasInfo(node) is not null) continue;
                if (model.GetSymbolInfo(node).Symbol is not INamespaceSymbol ns) continue;

                var full = FullName(ns);
                if (!IsSameOrChild(full, oldNamespace)) continue;

                var written = NameSegments(node, out _);
                if (written is null) continue;
                var fullCount = full.Split('.').Length;
                if (fullCount - written.Count >= oldSegmentCount) continue; // does not spell out Old's segments

                matches.Add((node, RequalifiedText(node, full, newNamespace + full[oldNamespace.Length..])));
            }

            foreach (var (node, text) in matches)
            {
                if (matches.Any(other => other.Node != node && other.Node.Span.Contains(node.Span))) continue;
                replacements[node] = text;
            }

            if (replacements.Count == 0) continue;

            var editor = new SyntaxEditor(root, solution.Services);
            foreach (var (node, text) in replacements)
            {
                editor.ReplaceNode(node, (current, _) => SyntaxFactory.ParseName(text).WithTriviaFrom(current));
            }

            var originalText = await document.GetTextAsync();
            candidate = candidate.WithDocumentText(document.Id,
                SourceText.From(editor.GetChangedRoot().ToFullString(), originalText.Encoding, originalText.ChecksumAlgorithm));
        }

        return candidate;
    }

    /// <summary>
    /// After a merge two usings in one scope can become identical ('using A.B;' twice, CS0105).
    /// For every changed document, drops a using that the rename made identical to another using
    /// of the same scope (the untouched one is kept).
    /// </summary>
    private static async Task<Solution> RemoveDuplicateUsingsAsync(Solution before, Solution after)
    {
        var result = after;
        foreach (var documentId in after.GetChanges(before).GetProjectChanges().SelectMany(p => p.GetChangedDocuments()))
        {
            var oldRoot = await before.GetDocument(documentId)!.GetSyntaxRootAsync();
            var newDocument = after.GetDocument(documentId)!;
            var newRoot = await newDocument.GetSyntaxRootAsync();
            if (oldRoot is null || newRoot is null) continue;

            var originalUsingTexts = oldRoot.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Select(u => u.WithoutTrivia().ToString()).ToHashSet(StringComparer.Ordinal);

            var duplicates = new List<UsingDirectiveSyntax>();
            foreach (var scope in newRoot.DescendantNodesAndSelf().Where(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax))
            {
                var usings = scope switch
                {
                    CompilationUnitSyntax unit => unit.Usings,
                    BaseNamespaceDeclarationSyntax declaration => declaration.Usings,
                    _ => default
                };
                foreach (var group in usings.GroupBy(u => u.WithoutTrivia().ToString(), StringComparer.Ordinal).Where(g => g.Count() > 1))
                {
                    // Keep one directive: one that existed verbatim before the rename if any, else the first.
                    var keep = group.FirstOrDefault(u => originalUsingTexts.Contains(u.WithoutTrivia().ToString())
                        && oldRoot.FindNode(u.Span, getInnermostNodeForTie: true) is UsingDirectiveSyntax old
                        && old.WithoutTrivia().ToString() == u.WithoutTrivia().ToString()) ?? group.First();
                    duplicates.AddRange(group.Where(u => u != keep));
                }
            }
            if (duplicates.Count == 0) continue;

            var cleaned = newRoot.RemoveNodes(duplicates, SyntaxRemoveOptions.KeepNoTrivia)!;
            var text = await newDocument.GetTextAsync();
            result = result.WithDocumentText(documentId, SourceText.From(cleaned.ToFullString(), text.Encoding, text.ChecksumAlgorithm));
        }
        return result;
    }

    // ------------------------------------------------------------------ shared helpers

    private static string ValidateNamespaceName(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameter} is required, e.g. 'MyApp.Inventory'.");
        }
        var trimmed = value.Trim();
        if (trimmed.StartsWith("global::", StringComparison.Ordinal)) trimmed = trimmed["global::".Length..];
        foreach (var segment in trimmed.Split('.'))
        {
            if (!SyntaxFacts.IsValidIdentifier(segment) || SyntaxFacts.GetKeywordKind(segment) != SyntaxKind.None)
            {
                throw new ArgumentException(
                    $"{parameter} '{value}' is not a valid namespace name: '{segment}' is not a valid C# identifier (expected e.g. 'MyApp.Inventory').");
            }
        }
        return trimmed;
    }

    private static bool IsSameOrChild(string name, string ns) =>
        name == ns || name.StartsWith(ns + ".", StringComparison.Ordinal);

    /// <summary>Every non-global prefix of a namespace: "A.B.C" -> {A, A.B, A.B.C}.</summary>
    private static HashSet<string> NamespaceChain(string ns)
    {
        var chain = new HashSet<string>(StringComparer.Ordinal);
        if (ns.Length == 0) return chain;
        var segments = ns.Split('.');
        for (var i = 1; i <= segments.Length; i++) chain.Add(string.Join('.', segments.Take(i)));
        return chain;
    }

    private static string FullName(INamespaceSymbol ns) => ns.IsGlobalNamespace ? "" : ns.ToDisplayString();

    private static string DisplayNamespace(string ns) => ns.Length == 0 ? "<global namespace>" : ns;

    private static INamespaceSymbol? FindNamespace(Compilation compilation, string fullName)
    {
        var current = compilation.GlobalNamespace;
        foreach (var segment in fullName.Split('.'))
        {
            current = current.GetNamespaceMembers().FirstOrDefault(n => n.Name == segment);
            if (current is null) return null;
        }
        return current;
    }

    /// <summary>The full name a namespace declaration declares, enclosing block namespaces included.</summary>
    private static string DeclaredName(BaseNamespaceDeclarationSyntax declaration) =>
        string.Join('.', declaration.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>().Reverse()
            .SelectMany(d => NameSegments(d.Name, out _) ?? [d.Name.ToString()]));

    private static bool IsPlainUsing(UsingDirectiveSyntax directive) =>
        directive.Alias is null && directive.StaticKeyword.IsKind(SyntaxKind.None) && directive.Name is not null;

    private static string NameText(NameSyntax name) =>
        NameSegments(name, out _) is { } segments ? string.Join('.', segments) : name.ToString();

    /// <summary>
    /// The identifier segments a name or member-access chain spells out ("A.B.C" -> [A, B, C]);
    /// isGlobal is set for a leading global::. Null for anything else (generic names, extern aliases).
    /// </summary>
    private static List<string>? NameSegments(SyntaxNode node, out bool isGlobal)
    {
        isGlobal = false;
        switch (node)
        {
            case IdentifierNameSyntax identifier:
                return [identifier.Identifier.ValueText];
            case QualifiedNameSyntax qualified:
            {
                var left = NameSegments(qualified.Left, out isGlobal);
                var right = NameSegments(qualified.Right, out _);
                return left is null || right is null ? null : [.. left, .. right];
            }
            case AliasQualifiedNameSyntax aliasQualified when aliasQualified.Alias.Identifier.IsKind(SyntaxKind.GlobalKeyword):
                isGlobal = true;
                return NameSegments(aliasQualified.Name, out _);
            case MemberAccessExpressionSyntax memberAccess when memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression):
            {
                var left = NameSegments(memberAccess.Expression, out isGlobal);
                var right = NameSegments(memberAccess.Name, out _);
                return left is null || right is null ? null : [.. left, .. right];
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Replacement text for a node that names namespace <paramref name="fullName"/>, naming
    /// <paramref name="newFullName"/> instead. A leading global:: is kept; a name written relative to
    /// an enclosing namespace keeps the same elided prefix when the new name still starts with it,
    /// otherwise the new name is written in full.
    /// </summary>
    private static string RequalifiedText(SyntaxNode node, string fullName, string newFullName)
    {
        var written = NameSegments(node, out var isGlobal);
        if (isGlobal) return "global::" + newFullName;

        var full = fullName.Split('.');
        if (written is null || written.Count >= full.Length || !full.AsSpan(full.Length - written.Count).SequenceEqual(written.ToArray()))
        {
            return newFullName;
        }

        var elided = string.Join('.', full.Take(full.Length - written.Count));
        return newFullName.StartsWith(elided + ".", StringComparison.Ordinal) ? newFullName[(elided.Length + 1)..] : newFullName;
    }

    private static string IndentOf(SyntaxNode node) =>
        string.Concat(node.GetLeadingTrivia().Reverse().TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).Reverse().Select(t => t.ToFullString()));

    /// <summary>
    /// Adds plain 'using X;' directives at compilation-unit level (skipping ones already present):
    /// at the sorted position when the existing plain usings are sorted (System first), otherwise
    /// after the last plain using. A file with no usings gets them above its first namespace
    /// declaration, with the file's leading comments kept on top.
    /// </summary>
    private static CompilationUnitSyntax AddUsings(CompilationUnitSyntax root, IEnumerable<string> namespaces, string newline)
    {
        foreach (var ns in namespaces)
        {
            if (root.Usings.Any(u => IsPlainUsing(u) && u.GlobalKeyword.IsKind(SyntaxKind.None) && NameText(u.Name!) == ns)) continue;

            var directive = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns))
                .NormalizeWhitespace()
                .WithTrailingTrivia(SyntaxFactory.EndOfLine(newline));

            if (root.Usings.Count == 0)
            {
                var first = root.Members.FirstOrDefault();
                if (first is BaseNamespaceDeclarationSyntax)
                {
                    directive = directive.WithLeadingTrivia(first.GetLeadingTrivia());
                    root = root.ReplaceNode(first, first.WithLeadingTrivia(SyntaxFactory.EndOfLine(newline)));
                }
                else
                {
                    directive = directive.WithTrailingTrivia(SyntaxFactory.EndOfLine(newline), SyntaxFactory.EndOfLine(newline));
                }
                root = root.WithUsings(SyntaxFactory.SingletonList(directive));
                continue;
            }

            var usings = root.Usings;
            var plainIndexes = Enumerable.Range(0, usings.Count)
                .Where(i => IsPlainUsing(usings[i]) && usings[i].GlobalKeyword.IsKind(SyntaxKind.None))
                .ToList();
            var plainNames = plainIndexes.Select(i => NameText(usings[i].Name!)).ToList();
            var sorted = plainNames.Zip(plainNames.Skip(1)).All(p => CompareUsings(p.First, p.Second) <= 0);

            int index;
            if (plainIndexes.Count == 0)
            {
                index = 0;
            }
            else if (sorted)
            {
                var before = plainIndexes.Where((_, k) => CompareUsings(plainNames[k], ns) > 0).Cast<int?>().FirstOrDefault();
                index = before ?? plainIndexes[^1] + 1;
            }
            else
            {
                index = plainIndexes[^1] + 1;
            }

            if (index == 0)
            {
                directive = directive.WithLeadingTrivia(usings[0].GetLeadingTrivia());
                usings = usings.Replace(usings[0], usings[0].WithLeadingTrivia());
            }
            root = root.WithUsings(usings.Insert(index, directive));
        }
        return root;
    }

    private static int CompareUsings(string a, string b)
    {
        static bool IsSystem(string n) => n == "System" || n.StartsWith("System.", StringComparison.Ordinal);
        var bySystem = IsSystem(b).CompareTo(IsSystem(a));
        return bySystem != 0 ? bySystem : string.CompareOrdinal(a, b);
    }
}
