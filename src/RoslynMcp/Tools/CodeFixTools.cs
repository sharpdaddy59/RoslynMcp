using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

/// <summary>
/// list_code_fixes (read-only) and apply_code_fix (write): Roslyn's own C# code fix providers
/// (Microsoft.CodeAnalysis.CSharp.Features, discovered by <see cref="CodeFixCatalog"/>) run
/// against the compiler's diagnostics, Hidden ones included. Every write goes through
/// <see cref="MutationApplier"/>.
/// </summary>
[McpServerToolType]
public static class CodeFixTools
{
    private const int MaxLocationsPerId = 20;
    private const int MaxChangedDocuments = 100;

    private enum FixScope { File, Project, Solution }

    private sealed record Found(DocumentId DocumentId, string FilePath, Diagnostic Diagnostic);

    private sealed record ScopeSelection(FixScope Scope, IReadOnlyList<ProjectId> ProjectIds, DocumentId? DocumentId);

    [McpServerTool(ReadOnly = true)]
    [Description("List the compiler diagnostics in a file, project or the whole solution (Hidden severity included by default, e.g. CS8019 unnecessary using), grouped by id with counts and locations, and for each id whether a Roslyn code fix exists (fixable, providers, fixAllSupported). Use it to see what apply_code_fix can clean up. Analyzer-only IDE diagnostics are not computed; CS8019 is reported and is fixed by Roslyn's IDE0005 remove-unnecessary-usings fixer (see fixedVia).")]
    public static Task<string> ListCodeFixes(
        [Description("Only report this diagnostic id (e.g. 'CS8019', 'CS0246'); empty = all ids")] string? diagnosticId = null,
        [Description("file | project | solution (default solution)")] string? scope = null,
        [Description("Absolute file path (required for scope=file; for scope=project it selects the file's project)")] string? filePath = null,
        [Description("Project name (for scope=project)")] string? projectName = null,
        [Description("Include Hidden-severity diagnostics such as CS8019 (default true)")] bool includeHidden = true)
    {
        return ToolJson.GuardAsync(async () =>
        {
            var solution = WorkspaceHost.Current;
            var selection = ResolveScope(solution, scope, filePath, projectName);
            var found = await CollectAsync(solution, selection,
                id => string.IsNullOrEmpty(diagnosticId) || MatchesRequestedId(id, diagnosticId),
                includeHidden, CancellationToken.None);

            var groups = found
                .GroupBy(f => f.Diagnostic.Id, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var providers = CodeFixCatalog.GetProviders(g.Key);
                    var fixerId = CodeFixCatalog.FixerIdFor(g.Key);
                    var first = g.First().Diagnostic;
                    return new
                    {
                        id = g.Key,
                        severity = first.Severity.ToString(),
                        message = first.GetMessage(),
                        count = g.Count(),
                        fixable = providers.Count > 0,
                        providers = providers.Select(p => p.Name).ToList(),
                        fixAllSupported = providers.Any(p => p.FixAllSupported),
                        fixedVia = !string.Equals(fixerId, g.Key, StringComparison.OrdinalIgnoreCase) && providers.Count > 0 ? fixerId : null,
                        locations = g.Take(MaxLocationsPerId).Select(f => WorkspaceHost.DescribeLocation(f.Diagnostic.Location)).ToList(),
                        truncatedLocations = g.Count() > MaxLocationsPerId ? (bool?)true : null
                    };
                })
                .ToList();

            return new
            {
                scope = selection.Scope.ToString().ToLowerInvariant(),
                filePath = selection.DocumentId is { } docId ? solution.GetDocument(docId)?.FilePath : null,
                projectName = selection.Scope == FixScope.Project ? solution.GetProject(selection.ProjectIds[0])?.Name : null,
                includeHidden,
                totalDiagnostics = found.Count,
                fixableDiagnostics = groups.Where(g => g.fixable).Sum(g => g.count),
                diagnostics = groups,
                note = "Compiler diagnostics only (semantic model, Hidden included when requested); analyzer-only IDE diagnostics are not computed. Files under bin/ and obj/ are excluded."
            };
        });
    }

    [McpServerTool]
    [Description("Apply a Roslyn code fix to every occurrence of one diagnostic id in a file, project or the solution, e.g. CS8019 (remove unnecessary usings) or CS0246 (add the missing using when exactly the right namespace exists). Uses Roslyn's own C# code fix providers: FixAll where the provider supports it, otherwise one diagnostic at a time with edits composed into one candidate. The result is compile-gated like every write tool (refused if it would introduce a NEW compile error) and refused if a touched file changed on disk since load. Run list_code_fixes first to see what is fixable; use dryRun to preview the exact edits.")]
    public static Task<string> ApplyCodeFix(
        [Description("Diagnostic id to fix, e.g. 'CS8019' or 'CS0246' (IDE0005 is accepted as an alias for CS8019; ids are case-insensitive)")] string diagnosticId,
        [Description("file | project | solution (default solution)")] string? scope = null,
        [Description("Absolute file path (required for scope=file; for scope=project it selects the file's project)")] string? filePath = null,
        [Description("Project name (for scope=project)")] string? projectName = null,
        [Description("Choose among several fix actions by case-insensitive title substring (e.g. 'using FixtureLib.Imports'); default: the first action offered")] string? fixTitle = null,
        [Description("Fix at most this many diagnostics (default 500); the rest are left in place")] int maxDiagnostics = 500,
        [Description("Preview only: compute the fix and run the compile gate, report the exact changes, write nothing (default false).")] bool dryRun = false)
    {
        return ToolJson.GuardAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(diagnosticId))
            {
                throw new ArgumentException("diagnosticId is required, e.g. 'CS8019' or 'CS0246'.");
            }
            if (maxDiagnostics < 1)
            {
                throw new ArgumentException("maxDiagnostics must be at least 1.");
            }

            diagnosticId = CodeFixCatalog.NormalizeRequestedId(diagnosticId);
            var ct = CancellationToken.None;
            var solution = WorkspaceHost.Current;
            var selection = ResolveScope(solution, scope, filePath, projectName);
            var scopeName = selection.Scope.ToString().ToLowerInvariant();
            var providers = CodeFixCatalog.GetProviders(diagnosticId);

            var found = await CollectAsync(solution, selection, id => MatchesRequestedId(id, diagnosticId), includeHidden: true, ct);

            object NotApplied(string message, int fixedCount = 0, string? title = null, string? provider = null) => new
            {
                diagnosticId,
                scope = scopeName,
                diagnosticsFound = found.Count,
                diagnosticsFixed = fixedCount,
                fixTitle = title,
                provider,
                applied = false,
                dryRun = RefactorTools.DryRunFlag(dryRun),
                wouldApply = dryRun ? false : (bool?)null,
                changedFileCount = 0,
                changes = Array.Empty<object>(),
                message
            };

            if (providers.Count == 0)
            {
                return NotApplied(
                    $"Not fixable: no Roslyn C# code fix provider handles '{diagnosticId}' " +
                    $"({CodeFixCatalog.ProviderCount} providers loaded). Nothing was written. " +
                    "Use list_code_fixes to see which diagnostics in scope are fixable.");
            }

            if (found.Count == 0)
            {
                return NotApplied($"No '{diagnosticId}' diagnostics in scope ({scopeName}); nothing to fix. Nothing was written.");
            }

            var targets = found.Take(maxDiagnostics).ToList();
            var truncated = found.Count > maxDiagnostics ? (bool?)true : null;

            // Pick the provider: the first (by export name) that offers a matching action for any
            // diagnostic in scope. Using a single provider keeps the fix uniform: a CS0246 with no
            // import candidate is left alone rather than "fixed" by a different provider (e.g.
            // Generate Type) behind the caller's back.
            CodeFixProviderEntry? chosen = null;
            CodeAction? firstAction = null;
            var offeredTitles = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var entry in providers)
            {
                foreach (var target in targets)
                {
                    var actions = await GetActionsAsync(entry.Provider, solution.GetDocument(target.DocumentId)!, target.Diagnostic, ct);
                    foreach (var a in actions) offeredTitles.Add(a.Title);
                    var match = SelectAction(actions, fixTitle);
                    if (match is null) continue;
                    chosen = entry;
                    firstAction = match;
                    break;
                }
                if (chosen is not null) break;
            }

            if (chosen is null || firstAction is null)
            {
                var offered = offeredTitles.Count > 0
                    ? $" Actions offered: {string.Join("; ", offeredTitles.Take(20).Select(t => $"'{t}'"))}."
                    : " No provider offered any action for these diagnostics.";
                return NotApplied(
                    (fixTitle is null
                        ? $"Not applied: {found.Count} '{diagnosticId}' diagnostic(s) found but no code fix action applies to them."
                        : $"Not applied: no fix action whose title contains '{fixTitle}'.") + offered + " Nothing was written.",
                    title: null,
                    provider: null);
            }

            Solution candidate;
            string mode;
            try
            {
                (candidate, mode) = await ComputeCandidateAsync(solution, selection, chosen, firstAction, targets, diagnosticId, fixTitle, ct);
            }
            catch (NonApplyChangesOperationException ex)
            {
                return NotApplied(ex.Message, title: firstAction.Title, provider: chosen.Name);
            }

            var remaining = await CountRemainingAsync(candidate, targets, diagnosticId, ct);
            var fixedCount = Math.Max(0, targets.Count - remaining);

            var changes = await DescribeLineChangesAsync(solution, candidate, MaxChangedDocuments);
            if (!candidate.GetChanges(solution).GetProjectChanges().Any())
            {
                return NotApplied(
                    $"Not applied: the '{firstAction.Title}' fix produced no changes. Nothing was written.",
                    title: firstAction.Title, provider: chosen.Name);
            }

            var result = await MutationApplier.ApplyOrPreviewAsync(candidate, dryRun);

            var message = result.Message;
            if (result.Accepted && targets.Count - fixedCount > 0)
            {
                var left = $"{targets.Count - fixedCount} of {targets.Count} targeted diagnostic(s) remain (no applicable fix from '{chosen.Name}').";
                message = message is null ? left : $"{message} {left}";
            }
            if (truncated == true)
            {
                var more = $"Only the first {maxDiagnostics} of {found.Count} diagnostic(s) were targeted (maxDiagnostics).";
                message = message is null ? more : $"{message} {more}";
            }

            return new
            {
                diagnosticId,
                scope = scopeName,
                diagnosticsFound = found.Count,
                diagnosticsFixed = result.Accepted ? fixedCount : 0,
                fixTitle = firstAction.Title,
                provider = chosen.Name,
                fixAll = mode == "fixAll" ? (bool?)true : null,
                truncated,
                applied = result.Applied,
                dryRun = RefactorTools.DryRunFlag(dryRun),
                wouldApply = RefactorTools.WouldApplyFlag(dryRun, result),
                changedFileCount = result.Accepted ? changes.Count : 0,
                changes = result.Accepted ? changes : [],
                conflicts = result.Accepted || result.NewErrors.Count == 0 ? null : result.NewErrors,
                staleFile = result.StaleFile,
                message
            };
        });
    }

    // ------------------------------------------------------------------ scope + diagnostics

    private static ScopeSelection ResolveScope(Solution solution, string? scope, string? filePath, string? projectName)
    {
        var parsed = (scope ?? "solution").Trim().ToLowerInvariant() switch
        {
            "" or "solution" => FixScope.Solution,
            "project" => FixScope.Project,
            "file" or "document" => FixScope.File,
            _ => throw new ArgumentException($"Unknown scope '{scope}'. Use file, project or solution.")
        };

        DocumentId? documentId = null;
        if (!string.IsNullOrEmpty(filePath))
        {
            var fullPath = Path.GetFullPath(filePath);
            documentId = solution.GetDocumentIdsWithFilePath(fullPath).FirstOrDefault()
                ?? throw new InvalidOperationException($"File is not part of the loaded solution: {fullPath}");
        }

        switch (parsed)
        {
            case FixScope.File:
                if (documentId is null) throw new ArgumentException("scope=file requires filePath.");
                return new ScopeSelection(FixScope.File, [documentId.ProjectId], documentId);

            case FixScope.Project:
                if (!string.IsNullOrEmpty(projectName))
                {
                    var project = solution.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException(
                            $"Project '{projectName}' not found. Projects: {string.Join(", ", solution.Projects.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal))}.");
                    return new ScopeSelection(FixScope.Project, [project.Id], null);
                }
                if (documentId is not null) return new ScopeSelection(FixScope.Project, [documentId.ProjectId], null);
                throw new ArgumentException("scope=project requires projectName (or a filePath inside the project).");

            default:
                return new ScopeSelection(FixScope.Solution,
                    solution.Projects
                        .Where(p => p.Language == LanguageNames.CSharp)
                        .OrderBy(p => p.Name, StringComparer.Ordinal)
                        .Select(p => p.Id)
                        .ToList(),
                    null);
        }
    }

    /// <summary>True if a diagnostic with this id answers a request for requestedId (case-insensitive; IDE0005 means CS8019).</summary>
    private static bool MatchesRequestedId(string diagnosticId, string requestedId) =>
        string.Equals(diagnosticId, CodeFixCatalog.NormalizeRequestedId(requestedId), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Compiler diagnostics (semantic model per document, so syntax + declaration + method-body
    /// diagnostics and CS8019 unused usings) for the documents in scope, unsuppressed, Hidden
    /// included on request, bin/ and obj/ documents (generated GlobalUsings, AssemblyInfo)
    /// excluded. Ordered by file path, then position, then id.
    /// </summary>
    private static async Task<List<Found>> CollectAsync(
        Solution solution, ScopeSelection selection, Func<string, bool> idFilter, bool includeHidden, CancellationToken ct)
    {
        var found = new List<Found>();
        foreach (var document in DocumentsInScope(solution, selection))
        {
            found.AddRange(await DocumentDiagnosticsAsync(document, idFilter, includeHidden, ct));
        }

        return found
            .OrderBy(f => f.FilePath, StringComparer.Ordinal)
            .ThenBy(f => f.Diagnostic.Location.SourceSpan.Start)
            .ThenBy(f => f.Diagnostic.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<Document> DocumentsInScope(Solution solution, ScopeSelection selection)
    {
        if (selection.DocumentId is { } documentId && selection.Scope == FixScope.File)
        {
            if (solution.GetDocument(documentId) is { } single) yield return single;
            yield break;
        }

        foreach (var projectId in selection.ProjectIds)
        {
            var project = solution.GetProject(projectId);
            if (project is null) continue;
            foreach (var document in project.Documents
                         .Where(d => d.FilePath is not null && !IsBuildOutput(project, d.FilePath))
                         .OrderBy(d => d.FilePath, StringComparer.Ordinal))
            {
                yield return document;
            }
        }
    }

    private static async Task<List<Found>> DocumentDiagnosticsAsync(
        Document document, Func<string, bool> idFilter, bool includeHidden, CancellationToken ct)
    {
        var model = await document.GetSemanticModelAsync(ct);
        if (model is null || document.FilePath is null) return [];

        return model.GetDiagnostics(cancellationToken: ct)
            .Where(d => !d.IsSuppressed
                        && d.Location.IsInSource
                        && d.Location.SourceTree == model.SyntaxTree
                        && (includeHidden || d.Severity != DiagnosticSeverity.Hidden)
                        && idFilter(d.Id))
            .Select(d => new Found(document.Id, document.FilePath, d))
            .ToList();
    }

    private static bool IsBuildOutput(Project project, string filePath)
    {
        var projectDir = project.FilePath is { } p ? Path.GetDirectoryName(p) : null;
        var relative = projectDir is null ? filePath : Path.GetRelativePath(projectDir, filePath);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(s => s is "bin" or "obj");
    }

    /// <summary>The targeted documents' matching diagnostics still present in the candidate.</summary>
    private static async Task<int> CountRemainingAsync(Solution candidate, List<Found> targets, string requestedId, CancellationToken ct)
    {
        var remaining = 0;
        foreach (var documentId in targets.Select(t => t.DocumentId).Distinct())
        {
            var document = candidate.GetDocument(documentId);
            if (document is null) continue;
            var originalInDoc = targets.Count(t => t.DocumentId == documentId);
            var now = (await DocumentDiagnosticsAsync(document, id => MatchesRequestedId(id, requestedId), true, ct)).Count;
            remaining += Math.Min(now, originalInDoc);
        }
        return remaining;
    }

    // ------------------------------------------------------------------ describing changes

    /// <summary>
    /// The candidate's edits per changed document as whole-line hunks ({line, oldText, newText},
    /// line 1-based in the old text, texts including their line breaks), from a line-level LCS
    /// diff. Code fixes mostly add or remove whole lines (usings), and Roslyn's character-level
    /// text changes would report those as fragments spanning line boundaries. Very large files
    /// fall back to the character-level description the other write tools use.
    /// </summary>
    private static async Task<List<object>> DescribeLineChangesAsync(Solution before, Solution after, int maxDocuments)
    {
        const long MaxDiffCells = 4_000_000;
        var changes = new List<object>();
        var changedIds = after.GetChanges(before).GetProjectChanges()
            .SelectMany(pc => pc.GetChangedDocuments())
            .Select(id => (Id: id, Path: before.GetDocument(id)?.FilePath ?? ""))
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .ToList();

        foreach (var (id, path) in changedIds)
        {
            if (changes.Count >= maxDocuments) break;
            var oldText = await before.GetDocument(id)!.GetTextAsync();
            var newText = await after.GetDocument(id)!.GetTextAsync();
            var oldLines = oldText.Lines.Select(l => oldText.ToString(l.SpanIncludingLineBreak)).ToArray();
            var newLines = newText.Lines.Select(l => newText.ToString(l.SpanIncludingLineBreak)).ToArray();

            if ((long)oldLines.Length * newLines.Length > MaxDiffCells)
            {
                var characterLevel = await RefactorTools.DescribeDocumentChangesAsync(
                    before, before.WithDocumentText(id, newText), 1);
                changes.AddRange(characterLevel);
                continue;
            }

            changes.Add(new { file = path, edits = LineHunks(oldLines, newLines) });
        }
        return changes;
    }

    private static List<object> LineHunks(string[] a, string[] b)
    {
        // lcs[i, j] = LCS length of a[i..] and b[j..]
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var hunks = new List<object>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y])
            {
                x++;
                y++;
                continue;
            }

            var startLine = x + 1;
            var removed = new System.Text.StringBuilder();
            var added = new System.Text.StringBuilder();
            while (x < a.Length || y < b.Length)
            {
                if (x < a.Length && y < b.Length && a[x] == b[y]) break;
                if (y >= b.Length || (x < a.Length && lcs[x + 1, y] >= lcs[x, y + 1]))
                {
                    removed.Append(a[x++]);
                }
                else
                {
                    added.Append(b[y++]);
                }
            }
            hunks.Add(new { line = startLine, oldText = removed.ToString(), newText = added.ToString() });
        }
        return hunks;
    }

    // ------------------------------------------------------------------ running the fixers

    /// <summary>The diagnostic as the fixer expects it: re-labelled with the fixer's id when aliased (CS8019 to RemoveUnnecessaryImportsFixable).</summary>
    private static Diagnostic ForFixer(Diagnostic diagnostic)
    {
        var fixerId = CodeFixCatalog.FixerIdFor(diagnostic.Id);
        if (string.Equals(fixerId, diagnostic.Id, StringComparison.Ordinal)) return diagnostic;

        var descriptor = new DiagnosticDescriptor(
            fixerId,
            diagnostic.Descriptor.Title,
            diagnostic.Descriptor.MessageFormat,
            diagnostic.Descriptor.Category,
            diagnostic.Severity,
            isEnabledByDefault: true,
            customTags: [WellKnownDiagnosticTags.Unnecessary]);
        return Diagnostic.Create(descriptor, diagnostic.Location, diagnostic.AdditionalLocations);
    }

    private static async Task<List<CodeAction>> GetActionsAsync(
        CodeFixProvider provider, Document document, Diagnostic diagnostic, CancellationToken ct)
    {
        var registered = new List<CodeAction>();
        var fixerDiagnostic = ForFixer(diagnostic);
        if (!provider.FixableDiagnosticIds.Contains(fixerDiagnostic.Id, StringComparer.OrdinalIgnoreCase)) return registered;

        var context = new CodeFixContext(document, fixerDiagnostic, (action, _) => registered.Add(action), ct);
        try
        {
            await provider.RegisterCodeFixesAsync(context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A provider that throws on this diagnostic simply offers nothing for it.
            return [];
        }

        // Flatten nested (grouped) actions: "Add using" with several candidates registers one
        // parent with a child per namespace. Leaves are what get applied.
        var leaves = new List<CodeAction>();
        void Flatten(CodeAction action)
        {
            if (action.NestedActions.IsDefaultOrEmpty) leaves.Add(action);
            else foreach (var nested in action.NestedActions) Flatten(nested);
        }
        foreach (var action in registered) Flatten(action);
        return leaves;
    }

    private static CodeAction? SelectAction(List<CodeAction> actions, string? fixTitle) =>
        fixTitle is null
            ? actions.FirstOrDefault()
            : actions.FirstOrDefault(a => a.Title.Contains(fixTitle, StringComparison.OrdinalIgnoreCase));

    private sealed class NonApplyChangesOperationException(string message) : Exception(message);

    private static async Task<Solution> ApplyActionAsync(CodeAction action, CancellationToken ct)
    {
        var operations = await action.GetOperationsAsync(ct);
        Solution? changed = null;
        foreach (var operation in operations)
        {
            if (operation is ApplyChangesOperation apply)
            {
                if (changed is not null)
                {
                    throw new NonApplyChangesOperationException(
                        $"Not applied: the fix '{action.Title}' produced more than one solution change; only single-change fixes are supported. Nothing was written.");
                }
                changed = apply.ChangedSolution;
            }
            else
            {
                throw new NonApplyChangesOperationException(
                    $"Not applied: the fix '{action.Title}' requires a non-text operation ({operation.GetType().Name}: {operation.Title}) that this tool does not perform. Nothing was written.");
            }
        }

        return changed ?? throw new NonApplyChangesOperationException(
            $"Not applied: the fix '{action.Title}' produced no solution change. Nothing was written.");
    }

    /// <summary>
    /// FixAll through the provider's FixAllProvider when it supports the scope; otherwise (or if
    /// FixAll yields nothing) one diagnostic at a time, re-deriving each document's diagnostics
    /// from the evolving candidate so successive edits compose.
    /// </summary>
    private static async Task<(Solution Candidate, string Mode)> ComputeCandidateAsync(
        Solution solution, ScopeSelection selection, CodeFixProviderEntry entry, CodeAction firstAction,
        List<Found> targets, string requestedId, string? fixTitle, CancellationToken ct)
    {
        var fixAllProvider = entry.Provider.GetFixAllProvider();
        var fixAllScope = selection.Scope switch
        {
            FixScope.File => FixAllScope.Document,
            FixScope.Project => FixAllScope.Project,
            _ => FixAllScope.Solution
        };

        if (fixAllProvider is not null && fixAllProvider.GetSupportedFixAllScopes().Contains(fixAllScope))
        {
            var fixerId = CodeFixCatalog.FixerIdFor(targets[0].Diagnostic.Id);
            var diagnosticProvider = new FixedDiagnosticProvider(targets.Select(t => (t.DocumentId, ForFixer(t.Diagnostic))).ToList());
            var firstDocument = solution.GetDocument(targets[0].DocumentId)!;
            var context = fixAllScope == FixAllScope.Document
                ? new FixAllContext(firstDocument, entry.Provider, fixAllScope, firstAction.EquivalenceKey, [fixerId], diagnosticProvider, ct)
                : new FixAllContext(firstDocument.Project, entry.Provider, fixAllScope, firstAction.EquivalenceKey, [fixerId], diagnosticProvider, ct);

            CodeAction? fixAllAction = null;
            try
            {
                fixAllAction = await fixAllProvider.GetFixAsync(context);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                fixAllAction = null; // fall back to per-diagnostic
            }

            if (fixAllAction is not null)
            {
                var fixAllCandidate = await ApplyActionAsync(fixAllAction, ct);
                if (fixAllCandidate.GetChanges(solution).GetProjectChanges().Any())
                {
                    return (fixAllCandidate, "fixAll");
                }
            }
        }

        var candidate = solution;
        foreach (var group in targets.GroupBy(t => t.DocumentId))
        {
            var budget = group.Count();
            var skip = 0;
            var attempts = 0;
            while (attempts < budget * 2)
            {
                attempts++;
                var document = candidate.GetDocument(group.Key);
                if (document is null) break;

                var current = (await DocumentDiagnosticsAsync(document, id => MatchesRequestedId(id, requestedId), true, ct))
                    .OrderBy(f => f.Diagnostic.Location.SourceSpan.Start)
                    .ToList();
                if (skip >= current.Count || budget - skip <= 0) break;

                var actions = await GetActionsAsync(entry.Provider, document, current[skip].Diagnostic, ct);
                var action = SelectAction(actions, fixTitle);
                if (action is null)
                {
                    skip++;
                    continue;
                }

                var next = await ApplyActionAsync(action, ct);
                if (!next.GetChanges(candidate).GetProjectChanges().Any())
                {
                    skip++;
                    continue;
                }
                candidate = next;
                budget--;
            }
        }

        return (candidate, "perDiagnostic");
    }

    /// <summary>Hands FixAll exactly the diagnostics this call targets (already re-labelled for the fixer).</summary>
    private sealed class FixedDiagnosticProvider(IReadOnlyList<(DocumentId DocumentId, Diagnostic Diagnostic)> diagnostics)
        : FixAllContext.DiagnosticProvider
    {
        public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken) =>
            Task.FromResult(diagnostics.Where(d => d.DocumentId == document.Id).Select(d => d.Diagnostic));

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult(Enumerable.Empty<Diagnostic>()); // compiler diagnostics here always have a source location

        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult(diagnostics.Where(d => d.DocumentId.ProjectId == project.Id).Select(d => d.Diagnostic));
    }
}
