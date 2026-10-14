using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Completion;

namespace RoslynMcp;

/// <summary>One discovered C# code fix provider, with its MEF export name.</summary>
internal sealed record CodeFixProviderEntry(string Name, CodeFixProvider Provider)
{
    public bool FixAllSupported => Provider.GetFixAllProvider() is not null;
}

/// <summary>
/// Discovers the Roslyn C# <see cref="CodeFixProvider"/>s shipped in Microsoft.CodeAnalysis.Features
/// and Microsoft.CodeAnalysis.CSharp.Features. They are MEF exports
/// (<c>[ExportCodeFixProvider(LanguageNames.CSharp, ...)]</c>), but almost all of them have a
/// parameterless (importing) constructor, so plain reflection is enough: no MEF container is
/// built for the providers themselves. The language services those providers call into
/// (add-import, remove-unnecessary-imports, ...) DO come from the workspace's MEF host. No
/// change to <see cref="WorkspaceHost"/> is needed for that: <c>MSBuildWorkspace.Create()</c>
/// composes <c>MSBuildMefHostServices.DefaultAssemblies</c>, which already lists
/// Microsoft.CodeAnalysis.Features and Microsoft.CodeAnalysis.CSharp.Features and loads them by
/// name whenever they are deployed next to the app (verified on 5.6.0; see docs/v2.2-code-fixes.md).
/// </summary>
internal static class CodeFixCatalog
{
    /// <summary>
    /// Compiler diagnostic ids whose Roslyn fixer is registered under a different id. The
    /// compiler's CS8019 ("unnecessary using directive", Hidden) is the finding Roslyn's IDE0005
    /// analyzer re-reports; that analyzer also emits a hidden companion diagnostic with id
    /// "RemoveUnnecessaryImportsFixable", which is the only id the remove-unnecessary-imports
    /// fixer declares. The fixer ignores the diagnostic's specifics (it removes every unnecessary
    /// using of the document via the remove-unnecessary-imports service), so CS8019 diagnostics
    /// are handed to it re-labelled with that id.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> CompilerToFixerId =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CS8019"] = "RemoveUnnecessaryImportsFixable",
        };

    /// <summary>Ids a caller may ask for that name a compiler diagnostic under another id (IDE0005 is CS8019).</summary>
    private static readonly IReadOnlyDictionary<string, string> RequestAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["IDE0005"] = "CS8019",
            ["RemoveUnnecessaryImportsFixable"] = "CS8019",
        };

    /// <summary>The compiler diagnostic id a requested id stands for (IDE0005 to CS8019; otherwise trimmed and upper-cased).</summary>
    internal static string NormalizeRequestedId(string requestedId)
    {
        var trimmed = requestedId.Trim();
        return RequestAliases.TryGetValue(trimmed, out var compilerId) ? compilerId : trimmed.ToUpperInvariant();
    }

    private static readonly Lazy<Catalog> Instance = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The two Features assemblies the providers are discovered in.</summary>
    internal static ImmutableArray<Assembly> FeatureAssemblies { get; } =
    [
        typeof(CompletionService).Assembly, // Microsoft.CodeAnalysis.Features
        Assembly.Load(new AssemblyName("Microsoft.CodeAnalysis.CSharp.Features")),
    ];

    /// <summary>Provider types that could not be instantiated (type name: reason). For diagnostics only.</summary>
    internal static IReadOnlyList<string> SkippedProviders => Instance.Value.Skipped;

    internal static int ProviderCount => Instance.Value.All.Count;

    /// <summary>
    /// The providers that fix <paramref name="diagnosticId"/> (directly, or through the
    /// <see cref="CompilerToFixerId"/> alias), ordered by export name (ordinal) for determinism.
    /// </summary>
    internal static IReadOnlyList<CodeFixProviderEntry> GetProviders(string diagnosticId)
    {
        var index = Instance.Value.ById;
        var fixerId = FixerIdFor(diagnosticId);
        return index.TryGetValue(fixerId, out var providers) ? providers : [];
    }

    /// <summary>The id the fixer is registered under for a diagnostic id (the id itself unless aliased).</summary>
    internal static string FixerIdFor(string diagnosticId) =>
        CompilerToFixerId.TryGetValue(diagnosticId, out var alias) ? alias : diagnosticId;

    private sealed record Catalog(
        IReadOnlyList<CodeFixProviderEntry> All,
        IReadOnlyDictionary<string, IReadOnlyList<CodeFixProviderEntry>> ById,
        IReadOnlyList<string> Skipped);

    private static Catalog Build()
    {
        var all = new List<CodeFixProviderEntry>();
        var skipped = new List<string>();

        foreach (var assembly in FeatureAssemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.OfType<Type>().ToArray();
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsGenericTypeDefinition || !typeof(CodeFixProvider).IsAssignableFrom(type))
                {
                    continue;
                }

                ExportCodeFixProviderAttribute? export;
                try
                {
                    export = type.GetCustomAttribute<ExportCodeFixProviderAttribute>(inherit: false);
                }
                catch (Exception ex)
                {
                    skipped.Add($"{type.FullName}: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (export is null || !export.Languages.Contains(LanguageNames.CSharp)) continue;

                try
                {
                    if (type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is null)
                    {
                        skipped.Add($"{type.FullName}: no parameterless constructor");
                        continue;
                    }

                    var provider = (CodeFixProvider)Activator.CreateInstance(type, nonPublic: true)!;
                    _ = provider.FixableDiagnosticIds; // some providers compute this lazily; fail here, not later
                    all.Add(new CodeFixProviderEntry(string.IsNullOrEmpty(export.Name) ? type.Name : export.Name, provider));
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                    skipped.Add($"{type.FullName}: {inner.GetType().Name}: {inner.Message}");
                }
            }
        }

        var byId = new Dictionary<string, List<CodeFixProviderEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in all)
        {
            foreach (var id in entry.Provider.FixableDiagnosticIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!byId.TryGetValue(id, out var list)) byId[id] = list = [];
                list.Add(entry);
            }
        }

        return new Catalog(
            all,
            byId.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<CodeFixProviderEntry>)kv.Value
                    .OrderBy(e => e.Name, StringComparer.Ordinal)
                    .ThenBy(e => e.Provider.GetType().FullName, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase),
            skipped);
    }
}
