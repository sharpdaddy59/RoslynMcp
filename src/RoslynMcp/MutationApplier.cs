using Microsoft.CodeAnalysis;

namespace RoslynMcp;

/// <summary>One compile error that would be newly introduced by a candidate mutation.</summary>
public sealed record MutationDiagnostic(string Id, string Message, string? File, int? Line, int? Column)
{
    internal static MutationDiagnostic FromDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.IsInSource ? diagnostic.Location.GetLineSpan() : (FileLinePositionSpan?)null;
        return new MutationDiagnostic(
            diagnostic.Id,
            diagnostic.GetMessage(),
            span?.Path,
            span is null ? null : span.Value.StartLinePosition.Line + 1,
            span is null ? null : span.Value.StartLinePosition.Character + 1);
    }
}

/// <summary>Outcome of a mutation attempt. Exactly one of the failure reasons is set when Applied is false.</summary>
public sealed record MutationResult(
    bool Applied,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<MutationDiagnostic> NewErrors,
    string? StaleFile,
    string? Message)
{
    internal static MutationResult Success(IReadOnlyList<string> changedFiles) =>
        new(true, changedFiles, [], null, null);

    internal static MutationResult RejectedNewErrors(IReadOnlyList<MutationDiagnostic> errors) =>
        new(false, [], errors,
            null,
            $"Not applied: the edit would introduce {errors.Count} new compile error(s).");

    internal static MutationResult Stale(string filePath) =>
        new(false, [], [],
            filePath,
            $"Not applied: '{filePath}' changed on disk after the workspace was loaded. Reload the solution and retry.");

    internal static MutationResult WorkspaceRejected() =>
        new(false, [], [],
            null,
            "Not applied: the workspace rejected the changes (Workspace.TryApplyChanges returned false).");
}

/// <summary>
/// The single sanctioned path for write-side tools to persist edits. Every mutation is
/// computed by the caller as a candidate Solution; this service compiles the affected
/// projects of that candidate FIRST and only writes to disk if doing so introduces no
/// NEW compile errors relative to the baseline captured when the solution was loaded
/// (pre-existing errors never block unrelated edits). It also refuses to write if a
/// target file was modified on disk after load, since the candidate would then be
/// computed against stale content.
/// </summary>
public static class MutationApplier
{
    public static async Task<MutationResult> TryApplyAsync(
        Solution candidateSolution, CancellationToken cancellationToken = default)
    {
        var baseSolution = WorkspaceHost.Current;
        var projectChanges = candidateSolution.GetChanges(baseSolution).GetProjectChanges().ToList();

        var touchedDocumentIds = projectChanges
            .SelectMany(pc => pc.GetChangedDocuments().Concat(pc.GetRemovedDocuments()))
            .ToList();

        foreach (var documentId in touchedDocumentIds)
        {
            var filePath = baseSolution.GetDocument(documentId)?.FilePath;
            if (filePath is not null && WorkspaceHost.HasChangedOnDiskSinceSnapshot(filePath))
            {
                return MutationResult.Stale(filePath);
            }
        }

        var changedProjectIds = projectChanges.Select(pc => pc.ProjectId).ToHashSet();
        var dependencyGraph = candidateSolution.GetProjectDependencyGraph();
        var affectedProjectIds = changedProjectIds
            .SelectMany(id => dependencyGraph.GetProjectsThatTransitivelyDependOnThisProject(id).Append(id))
            .ToHashSet();

        var newErrors = new List<MutationDiagnostic>();
        foreach (var projectId in affectedProjectIds)
        {
            var project = candidateSolution.GetProject(projectId);
            if (project is null) continue;

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            var baselineErrors = WorkspaceHost.GetBaselineErrors(projectId);
            var candidateErrors = compilation.GetDiagnostics(cancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error);

            newErrors.AddRange(candidateErrors
                .Where(d => !baselineErrors.Contains(WorkspaceHost.DiagnosticKey(d)))
                .Select(MutationDiagnostic.FromDiagnostic));
        }

        if (newErrors.Count > 0)
        {
            return MutationResult.RejectedNewErrors(newErrors);
        }

        var changedFilePaths = touchedDocumentIds
            .Select(id => baseSolution.GetDocument(id)?.FilePath)
            .Concat(projectChanges.SelectMany(pc => pc.GetAddedDocuments())
                .Select(id => candidateSolution.GetDocument(id)?.FilePath))
            .OfType<string>()
            .Distinct()
            .ToList();

        if (!WorkspaceHost.TryApplyChanges(candidateSolution))
        {
            return MutationResult.WorkspaceRejected();
        }

        foreach (var filePath in changedFilePaths)
        {
            WorkspaceHost.RefreshFileSnapshot(filePath);
        }

        return MutationResult.Success(changedFilePaths);
    }
}
