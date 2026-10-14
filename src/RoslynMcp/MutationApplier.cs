using System.Text;
using System.Xml;
using System.Xml.Linq;
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

/// <summary>
/// Outcome of a mutation attempt. Exactly one of the failure reasons is set when the edit was
/// (or, for a dry run, would be) rejected. A dry run (<see cref="MutationApplier.PreviewAsync"/>)
/// never writes: Applied is always false, DryRun is true, and WouldApply says whether the real
/// call would have written; ChangedFiles then lists the files that WOULD change.
/// </summary>
public sealed record MutationResult(
    bool Applied,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<MutationDiagnostic> NewErrors,
    string? StaleFile,
    string? Message,
    bool DryRun = false,
    bool WouldApply = false)
{
    /// <summary>True if the edit was applied, or (dry run) would be applied.</summary>
    public bool Accepted => Applied || WouldApply;

    internal static MutationResult Success(IReadOnlyList<string> changedFiles) =>
        new(true, changedFiles, [], null, null);

    internal static MutationResult WouldSucceed(IReadOnlyList<string> changedFiles) =>
        new(false, changedFiles, [], null,
            $"Dry run: the edit would be applied; {changedFiles.Count} file(s) would change. Nothing was written.",
            DryRun: true, WouldApply: true);

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
    /// <summary>Applies the candidate (dryRun false) or only previews it (dryRun true).</summary>
    public static Task<MutationResult> ApplyOrPreviewAsync(
        Solution candidateSolution, bool dryRun, CancellationToken cancellationToken = default) =>
        dryRun
            ? PreviewAsync(candidateSolution, cancellationToken)
            : TryApplyAsync(candidateSolution, cancellationToken);

    /// <summary>
    /// Dry run: runs exactly the checks TryApplyAsync runs (staleness of every touched file,
    /// compile gate against the load-time baseline) and reports what it would do, but never
    /// calls Workspace.TryApplyChanges and never touches the disk or WorkspaceHost.Current.
    /// A rejection carries the same NewErrors / StaleFile / Message the real call would return.
    /// </summary>
    public static async Task<MutationResult> PreviewAsync(
        Solution candidateSolution, CancellationToken cancellationToken = default)
    {
        var validation = await ValidateAsync(WorkspaceHost.Current, candidateSolution, cancellationToken);
        return validation.Rejection is { } rejection
            ? rejection with { DryRun = true, WouldApply = false }
            : MutationResult.WouldSucceed(validation.ChangedFilePaths);
    }

    public static async Task<MutationResult> TryApplyAsync(
        Solution candidateSolution, CancellationToken cancellationToken = default)
    {
        var baseSolution = WorkspaceHost.Current;
        var validation = await ValidateAsync(baseSolution, candidateSolution, cancellationToken);
        if (validation.Rejection is { } rejection)
        {
            return rejection;
        }

        var projectChanges = validation.ProjectChanges;
        var changedFilePaths = validation.ChangedFilePaths;

        // Workspace.TryApplyChanges rewrites the project file of any project gaining a document
        // (and MSBuild's save drops its final newline); snapshot the bytes so a net-zero rewrite
        // can be undone exactly after RemoveRedundantSdkCompileIncludes.
        var csprojSnapshots = projectChanges
            .Where(pc => pc.GetAddedDocuments().Any())
            .Select(pc => baseSolution.GetProject(pc.ProjectId)?.FilePath)
            .OfType<string>()
            .Where(File.Exists)
            .Distinct()
            .ToDictionary(path => path, File.ReadAllBytes);

        if (!WorkspaceHost.TryApplyChanges(candidateSolution))
        {
            return MutationResult.WorkspaceRejected();
        }

        foreach (var filePath in changedFilePaths)
        {
            WorkspaceHost.RefreshFileSnapshot(filePath);
        }

        foreach (var projectChange in projectChanges)
        {
            var addedPaths = projectChange.GetAddedDocuments()
                .Select(id => WorkspaceHost.Current.GetDocument(id)?.FilePath)
                .OfType<string>()
                .ToHashSet();
            if (addedPaths.Count == 0) continue;

            var csprojPath = WorkspaceHost.Current.GetProject(projectChange.ProjectId)?.FilePath;
            if (csprojPath is not null && File.Exists(csprojPath))
            {
                RemoveRedundantSdkCompileIncludes(csprojPath, addedPaths);
                if (csprojSnapshots.TryGetValue(csprojPath, out var originalBytes))
                {
                    RestoreIfOnlyTrailingWhitespaceDiffers(csprojPath, originalBytes);
                }
            }
        }

        return MutationResult.Success(changedFilePaths);
    }

    private sealed record Validation(
        IReadOnlyList<ProjectChanges> ProjectChanges,
        IReadOnlyList<string> ChangedFilePaths,
        MutationResult? Rejection);

    /// <summary>
    /// The shared, side-effect-free stage of every mutation: refuses if a touched file changed
    /// on disk since load, compiles every affected project of the candidate and rejects on any
    /// error not in the load-time baseline, and otherwise computes the files that would change.
    /// </summary>
    private static async Task<Validation> ValidateAsync(
        Solution baseSolution, Solution candidateSolution, CancellationToken cancellationToken)
    {
        var projectChanges = candidateSolution.GetChanges(baseSolution).GetProjectChanges().ToList();

        var touchedDocumentIds = projectChanges
            .SelectMany(pc => pc.GetChangedDocuments().Concat(pc.GetRemovedDocuments()))
            .ToList();

        foreach (var documentId in touchedDocumentIds)
        {
            var filePath = baseSolution.GetDocument(documentId)?.FilePath;
            if (filePath is not null && WorkspaceHost.HasChangedOnDiskSinceSnapshot(filePath))
            {
                return new Validation(projectChanges, [], MutationResult.Stale(filePath));
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
            return new Validation(projectChanges, [], MutationResult.RejectedNewErrors(newErrors));
        }

        var changedFilePaths = touchedDocumentIds
            .Select(id => baseSolution.GetDocument(id)?.FilePath)
            .Concat(projectChanges.SelectMany(pc => pc.GetAddedDocuments())
                .Select(id => candidateSolution.GetDocument(id)?.FilePath))
            .OfType<string>()
            .Distinct()
            .ToList();

        return new Validation(projectChanges, changedFilePaths, null);
    }

    /// <summary>
    /// If the project file now differs from its pre-mutation bytes only in trailing whitespace
    /// (MSBuild's save drops the final newline), put the original bytes back so the csproj is
    /// byte-identical whenever the mutation made no real change to it.
    /// </summary>
    private static void RestoreIfOnlyTrailingWhitespaceDiffers(string csprojPath, byte[] originalBytes)
    {
        var current = File.ReadAllBytes(csprojPath);
        if (current.AsSpan().SequenceEqual(originalBytes)) return;

        var currentText = Encoding.UTF8.GetString(current).TrimStart('﻿').TrimEnd();
        var originalText = Encoding.UTF8.GetString(originalBytes).TrimStart('﻿').TrimEnd();
        if (string.Equals(currentText, originalText, StringComparison.Ordinal))
        {
            File.WriteAllBytes(csprojPath, originalBytes);
        }
    }

    /// <summary>
    /// Workspace.TryApplyChanges adds an explicit <c>&lt;Compile Include&gt;</c> item for every
    /// newly-added document, even in SDK-style projects where the file is already picked up by
    /// the implicit glob - left in place, the next MSBuild-based build fails with "duplicate
    /// Compile items". Our own compile-before-write check wouldn't have caught that (it compiles
    /// in memory, not via MSBuild), so strip the now-redundant entries for files this mutation
    /// just added. Projects that opt out of default globbing keep their explicit items.
    /// </summary>
    private static void RemoveRedundantSdkCompileIncludes(string csprojPath, HashSet<string> addedFullPaths)
    {
        var xml = XDocument.Load(csprojPath, LoadOptions.PreserveWhitespace);
        var root = xml.Root;
        if (root is null || root.Attribute("Sdk") is null) return;

        var disablesDefaultItems = root.Descendants("EnableDefaultCompileItems")
            .Any(e => bool.TryParse(e.Value.Trim(), out var v) && !v);
        if (disablesDefaultItems) return;

        var projectDir = Path.GetDirectoryName(csprojPath)!;
        var changed = false;

        foreach (var itemGroup in root.Elements("ItemGroup").ToList())
        {
            foreach (var compileItem in itemGroup.Elements("Compile").ToList())
            {
                var include = (string?)compileItem.Attribute("Include");
                if (include is null) continue;

                var fullPath = Path.GetFullPath(Path.Combine(projectDir, include));
                if (addedFullPaths.Contains(fullPath))
                {
                    compileItem.Remove();
                    changed = true;
                }
            }

            if (!itemGroup.HasElements)
            {
                var precedingWhitespace = itemGroup.PreviousNode as XText;
                itemGroup.Remove();
                if (precedingWhitespace is { Value.Length: > 0 } && string.IsNullOrWhiteSpace(precedingWhitespace.Value))
                {
                    precedingWhitespace.Remove();
                }
            }
        }

        if (!changed) return;

        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false) };
        using var writer = XmlWriter.Create(csprojPath, settings);
        xml.Save(writer);
    }
}
