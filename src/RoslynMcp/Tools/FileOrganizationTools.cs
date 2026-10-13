using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
public static class FileOrganizationTools
{
    [McpServerTool]
    [Description("Move one top-level type declaration out of a file that holds several types into its own file (default <TypeName>.cs, same directory, same project). The new file gets the source file's usings verbatim (global usings excepted - they already apply project-wide), the same namespace form (file-scoped or block, including usings nested inside the block), and the type moved verbatim with its doc comments and attributes. Refuses nested types, partial types split across files, an existing destination file, and no-op moves. Compiles the result first via MutationApplier and writes nothing if it wouldn't build.")]
    public static Task<string> MoveTypeToFile(
        [Description("Absolute path of the file containing the type (use with line+column)")] string? filePath = null,
        [Description("1-based line number on the type's declaration or any member of it (use with filePath)")] int? line = null,
        [Description("1-based column (use with filePath)")] int? column = null,
        [Description("Fully-qualified metadata name of the type instead of a position, e.g. 'FixtureLib.Square' or 'MyNs.Box`1'")] string? fullyQualifiedName = null,
        [Description("Optional file name for the new file, e.g. 'Square.cs' (default <TypeName>.cs, generic arity omitted). A bare file name only: the file is always created in the source file's directory.")] string? destinationFileName = null)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var solution = WorkspaceHost.Current;
            var (typeSymbol, typeSyntax) = await ResolveTypeAsync(solution, filePath, line, column, fullyQualifiedName);

            if (typeSymbol.ContainingType is not null)
            {
                throw new InvalidOperationException(
                    $"'{typeSymbol.ToDisplayString()}' is nested inside '{typeSymbol.ContainingType.ToDisplayString()}'; " +
                    "move_type_to_file only moves top-level types. To move the outer type, use its fullyQualifiedName or a position on its own declaration line.");
            }

            var declarationFiles = typeSymbol.DeclaringSyntaxReferences.Select(r => r.SyntaxTree.FilePath).Distinct().ToList();
            if (declarationFiles.Count > 1)
            {
                throw new InvalidOperationException(
                    $"'{typeSymbol.ToDisplayString()}' is partial with declarations in {declarationFiles.Count} files " +
                    $"({string.Join(", ", declarationFiles.Select(Path.GetFileName))}); move_type_to_file only moves a type declared in a single file.");
            }
            if (typeSymbol.DeclaringSyntaxReferences.Length > 1)
            {
                throw new InvalidOperationException(
                    $"'{typeSymbol.ToDisplayString()}' is partial with {typeSymbol.DeclaringSyntaxReferences.Length} declarations in the same file; merge them first.");
            }

            var sourcePath = typeSyntax.SyntaxTree.FilePath;
            var sourceDocumentId = solution.GetDocumentIdsWithFilePath(sourcePath).FirstOrDefault()
                ?? throw new InvalidOperationException($"File is not part of the loaded solution: {sourcePath}");
            var sourceDocument = solution.GetDocument(sourceDocumentId)!;

            var root = (CompilationUnitSyntax)(await typeSyntax.SyntaxTree.GetRootAsync());
            var topLevelTypes = TopLevelTypeDeclarations(root).ToList();
            var remainingTypes = topLevelTypes.Where(t => t != typeSyntax).Select(DeclarationName).ToList();

            var destinationName = ResolveDestinationName(destinationFileName, typeSymbol);
            var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
            var destinationPath = Path.Combine(sourceDirectory, destinationName);

            if (PathsEqual(destinationPath, sourcePath))
            {
                throw new InvalidOperationException(remainingTypes.Count == 0
                    ? $"'{typeSymbol.Name}' is already the only type in {Path.GetFileName(sourcePath)}; nothing to move."
                    : $"Destination '{destinationName}' is the source file itself; choose a different destinationFileName.");
            }
            if (remainingTypes.Count == 0)
            {
                throw new InvalidOperationException(
                    $"'{typeSymbol.Name}' is the only type in {Path.GetFileName(sourcePath)}; moving it would leave an empty file. " +
                    "Rename the file instead.");
            }
            if (File.Exists(destinationPath) || solution.GetDocumentIdsWithFilePath(destinationPath).Any())
            {
                throw new InvalidOperationException(
                    $"Destination file already exists: {destinationPath}. Pass a different destinationFileName.");
            }

            var sourceText = await sourceDocument.GetTextAsync();
            var newline = DetectNewline(sourceText.ToString());
            var newFileText = BuildNewFileText(root, typeSyntax, newline);
            var newSourceText = RemoveDeclaration(sourceText, typeSyntax);

            var candidateSolution = solution
                .WithDocumentText(sourceDocumentId, newSourceText)
                .AddDocument(
                    DocumentId.CreateNewId(sourceDocument.Project.Id),
                    destinationName,
                    SourceText.From(newFileText, sourceText.Encoding ?? new UTF8Encoding(false)),
                    sourceDocument.Folders,
                    destinationPath);

            var sourceChanges = await RefactorTools.DescribeDocumentChangesAsync(solution, candidateSolution, maxDocuments: 10);
            var result = await MutationApplier.TryApplyAsync(candidateSolution);

            var changes = new List<object>();
            if (result.Applied)
            {
                changes.AddRange(sourceChanges);
                changes.Add(new
                {
                    file = destinationPath,
                    added = true,
                    edits = new[] { new { line = 1, oldText = "", newText = newFileText } }
                });
            }

            return new
            {
                type = NavigationTools.Describe(typeSymbol),
                sourceFile = sourcePath,
                destinationFile = destinationPath,
                applied = result.Applied,
                remainingTypesInSource = remainingTypes,
                changedFileCount = result.Applied ? changes.Count : 0,
                changes,
                conflicts = result.Applied || result.NewErrors.Count == 0 ? null : result.NewErrors,
                staleFile = result.StaleFile,
                message = result.Message
            };
        });
    }

    private static async Task<(INamedTypeSymbol Symbol, MemberDeclarationSyntax Syntax)> ResolveTypeAsync(
        Solution solution, string? filePath, int? line, int? column, string? fullyQualifiedName)
    {
        if (!string.IsNullOrEmpty(fullyQualifiedName))
        {
            var symbol = await RefactorTools.ResolveByFullyQualifiedNameAsync(solution, fullyQualifiedName);
            var type = symbol as INamedTypeSymbol ?? symbol.ContainingType
                ?? throw new InvalidOperationException($"'{fullyQualifiedName}' does not resolve to a type or a member of one.");
            var reference = type.DeclaringSyntaxReferences.FirstOrDefault()
                ?? throw new InvalidOperationException($"'{type.ToDisplayString()}' has no source declaration in the loaded solution.");
            return (type, (MemberDeclarationSyntax)await reference.GetSyntaxAsync());
        }

        var (document, position) = await WorkspaceHost.GetPositionAsync(
            filePath ?? throw new ArgumentException("Provide either filePath+line+column or fullyQualifiedName."),
            line ?? throw new ArgumentException("line is required when filePath is given."),
            column ?? throw new ArgumentException("column is required when filePath is given."));

        var root = await document.GetSyntaxRootAsync()
            ?? throw new InvalidOperationException("No syntax tree available for the document.");
        var token = root.FindToken(position);
        var declaration = token.Parent?.AncestorsAndSelf()
            .OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(IsTypeDeclaration)
            ?? throw new InvalidOperationException(
                $"No type declaration encloses {filePath}:{line}:{column}. Position on the type's declaration or one of its members.");

        var model = await document.GetSemanticModelAsync()
            ?? throw new InvalidOperationException("No semantic model available for the document.");
        var declared = model.GetDeclaredSymbol(declaration) as INamedTypeSymbol
            ?? throw new InvalidOperationException("Could not resolve the declared type at that position.");
        return (declared, declaration);
    }

    private static bool IsTypeDeclaration(SyntaxNode node) =>
        node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax;

    /// <summary>Type declarations not nested in another type, at any namespace depth.</summary>
    private static IEnumerable<MemberDeclarationSyntax> TopLevelTypeDeclarations(CompilationUnitSyntax root) =>
        root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<MemberDeclarationSyntax>()
            .Where(IsTypeDeclaration);

    private static string DeclarationName(MemberDeclarationSyntax declaration) => declaration switch
    {
        BaseTypeDeclarationSyntax t => t.Identifier.Text,
        DelegateDeclarationSyntax d => d.Identifier.Text,
        _ => declaration.ToString()
    };

    private static string ResolveDestinationName(string? destinationFileName, INamedTypeSymbol type)
    {
        if (string.IsNullOrWhiteSpace(destinationFileName)) return type.Name + ".cs";

        var name = destinationFileName.Trim();
        if (name.IndexOfAny(['/', '\\']) >= 0 || Path.GetFileName(name) != name)
        {
            throw new ArgumentException(
                $"destinationFileName must be a bare file name (the new file is always created next to the source file): '{destinationFileName}'.");
        }
        if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"destinationFileName must end in .cs: '{destinationFileName}'.");
        }
        return name;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string DetectNewline(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    /// <summary>
    /// Builds the new file's text: extern aliases + non-global usings of the compilation unit
    /// (verbatim), then each enclosing namespace in its original form - file-scoped
    /// <c>namespace X;</c> or block <c>namespace X { }</c> with its own nested usings - and
    /// finally the type declaration verbatim with its leading blank lines trimmed.
    /// Global usings are deliberately not copied: they already apply to every file in the
    /// project, and a duplicated global alias is a compile error (CS1537).
    /// </summary>
    private static string BuildNewFileText(CompilationUnitSyntax root, MemberDeclarationSyntax type, string newline)
    {
        var builder = new StringBuilder();

        var header = DirectivesText(root.Externs, root.Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None)));
        if (header.Length > 0)
        {
            builder.Append(EnsureTrailingNewline(header, newline)).Append(newline);
        }

        var namespaces = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().ToList();
        var closers = new Stack<string>();

        foreach (var ns in namespaces)
        {
            if (ns is FileScopedNamespaceDeclarationSyntax fileScoped)
            {
                builder.Append(fileScoped.WithoutLeadingTrivia().WithMembers(default).WithExterns(default).WithUsings(default)
                    .ToString()).Append(newline).Append(newline);
            }
            else if (ns is NamespaceDeclarationSyntax block)
            {
                var indent = IndentOf(block.NamespaceKeyword);
                builder.Append(indent)
                    .Append(block.NamespaceKeyword.Text).Append(' ').Append(block.Name.ToString()).Append(newline)
                    .Append(indent).Append('{').Append(newline);
                closers.Push(indent + "}" + newline);
            }

            var nested = DirectivesText(ns.Externs, ns.Usings);
            if (nested.Length > 0)
            {
                builder.Append(EnsureTrailingNewline(TrimLeadingBlankLines(nested), newline)).Append(newline);
            }
        }

        var typeText = type.WithLeadingTrivia(TrimLeadingBlankLines(type.GetLeadingTrivia())).ToFullString();
        builder.Append(EnsureTrailingNewline(typeText, newline));

        while (closers.Count > 0) builder.Append(closers.Pop());

        return builder.ToString();
    }

    private static string DirectivesText(IEnumerable<ExternAliasDirectiveSyntax> externs, IEnumerable<UsingDirectiveSyntax> usings) =>
        string.Concat(externs.Select(e => e.ToFullString()).Concat(usings.Select(u => u.ToFullString())));

    private static string IndentOf(SyntaxToken token) =>
        string.Concat(token.LeadingTrivia.Reverse().TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).Reverse().Select(t => t.ToFullString()));

    private static string EnsureTrailingNewline(string text, string newline) =>
        text.EndsWith('\n') ? text : text + newline;

    /// <summary>Drops leading blank lines (but keeps the indentation of the first real line).</summary>
    private static SyntaxTriviaList TrimLeadingBlankLines(SyntaxTriviaList trivia)
    {
        var lastEolBeforeContent = -1;
        for (var i = 0; i < trivia.Count; i++)
        {
            if (trivia[i].IsKind(SyntaxKind.EndOfLineTrivia)) lastEolBeforeContent = i;
            else if (!trivia[i].IsKind(SyntaxKind.WhitespaceTrivia)) break;
        }
        return lastEolBeforeContent < 0 ? trivia : SyntaxFactory.TriviaList(trivia.Skip(lastEolBeforeContent + 1));
    }

    private static string TrimLeadingBlankLines(string text)
    {
        var index = 0;
        var lineStart = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            if (text[index] == '\n') lineStart = index + 1;
            index++;
        }
        return text[lineStart..];
    }

    /// <summary>
    /// Removes the declaration's full span (leading trivia included, so the blank line that
    /// separated it from the previous member goes with it), then collapses any run of blank
    /// lines left at the splice point down to a single blank line.
    /// </summary>
    private static SourceText RemoveDeclaration(SourceText text, MemberDeclarationSyntax declaration)
    {
        var span = declaration.FullSpan;
        var full = text.ToString();
        var spliced = full[..span.Start] + full[span.End..];

        var start = span.Start;
        var end = span.Start;
        while (start > 0 && char.IsWhiteSpace(spliced[start - 1])) start--;
        while (end < spliced.Length && char.IsWhiteSpace(spliced[end])) end++;

        var gap = spliced[start..end];
        var newlineCount = gap.Count(c => c == '\n');
        if (newlineCount > 2 && start > 0)
        {
            var newline = DetectNewline(full);
            var lastNewline = gap.LastIndexOf('\n');
            var indentation = gap[(lastNewline + 1)..];
            spliced = spliced[..start] + newline + newline + indentation + spliced[end..];
        }

        return SourceText.From(spliced, text.Encoding, text.ChecksumAlgorithm);
    }
}
