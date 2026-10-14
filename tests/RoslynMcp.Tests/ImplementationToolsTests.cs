using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Build.Locator;
using RoslynMcp.Tools;
using Xunit;

namespace RoslynMcp.Tests;

/*
 * v2.2 item E answer key: implement_interface / implement_abstract / generate_overrides against
 * fixtures/GenerationSolution (one project, GenerationLib). The fixture deliberately does NOT
 * compile: its 7 baseline errors are exactly the members the tools must generate.
 *
 *   MemoryRepository : IRepository<string> (IRepository<T> : IReadOnlyRepo<T>)
 *     implemented already:  IRepository<string>.Name, IRepository<string>.Add(string)
 *     missing (5x CS0535):  Find(string) [has XML doc], Current {get;set;}, event Changed,
 *                           this[int] {get;set;}, IReadOnlyRepo<string>.Count {get;} (inherited)
 *   Circle2 : Shape2         Area() overridden; Perimeter {get;} [doc] missing (1x CS0534);
 *                           virtual Describe() [doc] is a generate_overrides candidate
 *   Ledger (partial)         Contracts.cs part (FIRST declaration, implements Record) +
 *                           Contracts.Part2.cs part (carries ": IAuditable"); LastAudit missing (1x CS0535)
 *   Plain : INamed           fully implemented; base is object (no abstract base)
 *
 *   implement_interface(MemoryRepository)             -> 5 stubs in Contracts.cs, errors 7 -> 2
 *   implement_interface(MemoryRepository, explicitly) -> 5 explicit stubs, errors 7 -> 2
 *   implement_interface(MemoryRepository, "IReadOnlyRepo") -> Count only, errors 7 -> 6
 *   implement_abstract(Circle2)                       -> Perimeter override with its doc, errors 7 -> 6
 *   implement_abstract(Plain)                         -> refused, no abstract base
 *   generate_overrides(Circle2)                       -> refused, candidates
 *        [Shape2.Describe(), object.Equals(object?), object.GetHashCode(), object.ToString()]
 *   generate_overrides(Circle2, ["ToString"])          -> public override string? ToString() { return base.ToString(); }
 *                                                       gate accepts, errors stay 7
 *   implement_interface(Plain)                        -> refused, "Nothing to generate", alreadyImplemented [INamed.Name]
 *   implement_interface(Ledger)                       -> LastAudit stub in Contracts.cs (first declaration), Part2 untouched
 *
 * Every test snapshots the fixture tree (bin/obj excluded), restores it in finally and asserts
 * the tree is byte-identical afterwards.
 */
[Collection("workspace")]  // WorkspaceHost is a process-wide singleton: classes that load solutions must not run in parallel
public class ImplementationToolsTests : IAsyncLifetime
{
    private static readonly string FixtureRoot = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "GenerationSolution"));
    private static readonly string SolutionPath = Path.Combine(FixtureRoot, "GenerationSolution.slnx");
    private static readonly string LibDir = Path.Combine(FixtureRoot, "GenerationLib");
    private static readonly string ContractsFile = Path.Combine(LibDir, "Contracts.cs");
    private static readonly string Part2File = Path.Combine(LibDir, "Contracts.Part2.cs");

    private const int BaselineErrors = 7;

    private const string MemoryRepositoryAnchor = "    public void Add(string item) => _items.Add(item);\n";
    private const string Circle2Anchor = "    public override double Area() => Math.PI * Radius * Radius;\n";
    private const string LedgerAnchor = "    public void Record(string entry) => Total++;\n";

    private const string ImplicitStubs = """

            /// <summary>Finds the item stored under <paramref name="key"/>.</summary>
            /// <param name="key">The lookup key.</param>
            public string? Find(string key)
            {
                throw new NotImplementedException();
            }

            /// <inheritdoc />
            public string? Current
            {
                get
                {
                    throw new NotImplementedException();
                }

                set
                {
                    throw new NotImplementedException();
                }
            }

            /// <inheritdoc />
            public event EventHandler? Changed;

            /// <inheritdoc />
            public string this[int index]
            {
                get
                {
                    throw new NotImplementedException();
                }

                set
                {
                    throw new NotImplementedException();
                }
            }

            /// <inheritdoc />
            public int Count
            {
                get
                {
                    throw new NotImplementedException();
                }
            }

        """;

    private const string ExplicitStubs = """

            /// <summary>Finds the item stored under <paramref name="key"/>.</summary>
            /// <param name="key">The lookup key.</param>
            string? IRepository<string>.Find(string key)
            {
                throw new NotImplementedException();
            }

            /// <inheritdoc />
            string? IRepository<string>.Current
            {
                get
                {
                    throw new NotImplementedException();
                }

                set
                {
                    throw new NotImplementedException();
                }
            }

            /// <inheritdoc />
            event EventHandler? IRepository<string>.Changed
            {
                add
                {
                    throw new NotImplementedException();
                }

                remove
                {
                    throw new NotImplementedException();
                }
            }

            /// <inheritdoc />
            string IRepository<string>.this[int index]
            {
                get
                {
                    throw new NotImplementedException();
                }

                set
                {
                    throw new NotImplementedException();
                }
            }

            /// <inheritdoc />
            int IReadOnlyRepo<string>.Count
            {
                get
                {
                    throw new NotImplementedException();
                }
            }

        """;

    private const string PerimeterStub = """

            /// <summary>Length of the outline.</summary>
            public override double Perimeter
            {
                get
                {
                    throw new NotImplementedException();
                }
            }

        """;

    private const string ToStringOverride = """

            /// <inheritdoc />
            public override string? ToString()
            {
                return base.ToString();
            }

        """;

    private const string LastAuditStub = """

            /// <inheritdoc />
            public DateTime LastAudit
            {
                get
                {
                    throw new NotImplementedException();
                }
            }

        """;

    private static readonly string[] MemoryRepositoryMissing =
    [
        "GenerationLib.IRepository<string>.Find(string)",
        "GenerationLib.IRepository<string>.Current",
        "GenerationLib.IRepository<string>.Changed",
        "GenerationLib.IRepository<string>.this[int]",
        "GenerationLib.IReadOnlyRepo<string>.Count"
    ];

    private static readonly string[] MemoryRepositorySatisfied =
    [
        "GenerationLib.IRepository<string>.Name",
        "GenerationLib.IRepository<string>.Add(string)"
    ];

    static ImplementationToolsTests()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }

    public async Task InitializeAsync()
    {
        EnsureRestored();
        var result = Parse(await SolutionTools.LoadSolution(SolutionPath));
        result.GetProperty("projectCount").GetInt32().Should().Be(1);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static void EnsureRestored()
    {
        if (File.Exists(Path.Combine(LibDir, "obj", "project.assets.json"))) return;

        var psi = new ProcessStartInfo("dotnet", $"restore \"{SolutionPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit(120_000);
        process.ExitCode.Should().Be(0, "fixture restore must succeed for the tests to run");
    }

    private static JsonElement Parse(string json)
    {
        var element = JsonDocument.Parse(json).RootElement;
        if (element.TryGetProperty("error", out var error))
        {
            throw new Xunit.Sdk.XunitException($"Tool returned error: {error.GetString()}");
        }
        return element;
    }

    private static bool IsBuildOutput(string path) =>
        Path.GetRelativePath(FixtureRoot, path).Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj");

    /// <summary>Raw bytes of every fixture file (bin/obj excluded): the restore point for finally blocks.</summary>
    private static Dictionary<string, byte[]> SnapshotFixtures() =>
        Directory.EnumerateFiles(FixtureRoot, "*", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(p))
            .ToDictionary(p => p, File.ReadAllBytes);

    private static Dictionary<string, string> HashFixtures() =>
        Directory.EnumerateFiles(FixtureRoot, "*", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(p))
            .ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

    private static List<string> ChangedFiles(Dictionary<string, string> before, Dictionary<string, string> after) =>
        before.Keys.Union(after.Keys)
            .Where(k => !before.TryGetValue(k, out var b) || !after.TryGetValue(k, out var a) || a != b)
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList()!;

    /// <summary>Puts every fixture file back exactly and removes any file a test created.</summary>
    private static void Restore(Dictionary<string, byte[]> snapshot)
    {
        foreach (var path in Directory.EnumerateFiles(FixtureRoot, "*", SearchOption.AllDirectories)
                     .Where(p => !IsBuildOutput(p) && !snapshot.ContainsKey(p)))
        {
            File.Delete(path);
        }
        foreach (var (path, bytes) in snapshot)
        {
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                File.WriteAllBytes(path, bytes);
            }
        }
    }

    private static async Task<List<JsonElement>> ErrorsAsync()
    {
        var diagnostics = Parse(await DiagnosticsTools.GetDiagnostics("GenerationLib", "Error"));
        return diagnostics.GetProperty("diagnostics").EnumerateArray()
            .Where(d => d.GetProperty("severity").GetString() == "Error")
            .ToList();
    }

    private static List<string> Strings(JsonElement result, string property) =>
        result.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToList();

    private static string Raw(JsonElement result, string property) =>
        result.TryGetProperty(property, out var value) ? value.GetRawText() : "<absent>";

    private static string InsertAfter(string text, string anchor, string block)
    {
        var index = text.IndexOf(anchor, StringComparison.Ordinal);
        index.Should().BeGreaterThanOrEqualTo(0, $"anchor '{anchor.Trim()}' must exist in the fixture");
        return text.Insert(index + anchor.Length, block);
    }

    private static void ShouldBeRefusal(JsonElement result, string messageFragment)
    {
        result.GetProperty("applied").GetBoolean().Should().BeFalse(result.GetRawText());
        result.GetProperty("changedFileCount").GetInt32().Should().Be(0);
        result.GetProperty("changes").GetArrayLength().Should().Be(0);
        result.GetProperty("message").GetString().Should().Contain(messageFragment);
    }

    /// <summary>Runs <paramref name="body"/> and guarantees the fixture tree is byte-identical afterwards.</summary>
    private static async Task WithFixtureRestored(Func<Task> body)
    {
        var snapshot = SnapshotFixtures();
        var hashes = HashFixtures();
        try
        {
            await body();
        }
        finally
        {
            Restore(snapshot);
        }
        ChangedFiles(hashes, HashFixtures()).Should().BeEmpty("the fixture must be restored byte-for-byte");
    }

    // ---------------------------------------------------------------- implement_interface

    [Fact]
    public async Task ImplementInterface_MemoryRepository_DryRunThenReal_GeneratesExactlyTheMissingMembers()
    {
        await WithFixtureRestored(async () =>
        {
            var original = await File.ReadAllTextAsync(ContractsFile);
            (await ErrorsAsync()).Should().HaveCount(BaselineErrors);
            var hashesBefore = HashFixtures();

            var dry = Parse(await ImplementationTools.ImplementInterface(fullyQualifiedName: "GenerationLib.MemoryRepository", dryRun: true));

            ChangedFiles(hashesBefore, HashFixtures()).Should().BeEmpty("a dry run writes nothing");
            dry.GetProperty("applied").GetBoolean().Should().BeFalse();
            dry.GetProperty("dryRun").GetBoolean().Should().BeTrue();
            dry.GetProperty("wouldApply").GetBoolean().Should().BeTrue(dry.GetRawText());
            Strings(dry, "generated").Should().Equal(MemoryRepositoryMissing);

            var real = Parse(await ImplementationTools.ImplementInterface(fullyQualifiedName: "GenerationLib.MemoryRepository"));

            real.GetProperty("applied").GetBoolean().Should().BeTrue(real.GetRawText());
            real.TryGetProperty("dryRun", out _).Should().BeFalse();
            real.TryGetProperty("wouldApply", out _).Should().BeFalse();
            Raw(dry, "changes").Should().Be(Raw(real, "changes"), "the dry run describes exactly the edit the real call writes");
            real.GetProperty("changedFileCount").GetInt32().Should().Be(1);
            Strings(real, "interfaces").Should().Equal("GenerationLib.IRepository<string>", "GenerationLib.IReadOnlyRepo<string>");
            Strings(real, "generated").Should().Equal(MemoryRepositoryMissing);
            Strings(real, "alreadyImplemented").Should().Equal(MemoryRepositorySatisfied);
            real.GetProperty("insertedInto").GetString().Should().Be(ContractsFile);
            real.TryGetProperty("partialDeclarationFiles", out _).Should().BeFalse();
            real.TryGetProperty("addedUsingSystem", out _).Should().BeFalse("NotImplementedException resolves through the implicit global usings");

            (await File.ReadAllTextAsync(ContractsFile)).Should().Be(InsertAfter(original, MemoryRepositoryAnchor, ImplicitStubs));
            ChangedFiles(hashesBefore, HashFixtures()).Should().Equal("Contracts.cs");

            var errorsAfter = await ErrorsAsync();
            errorsAfter.Should().HaveCount(2, "the five CS0535 errors are gone; Circle2's CS0534 and Ledger's CS0535 remain");
            errorsAfter.Select(e => e.GetProperty("id").GetString()).Should().BeEquivalentTo(["CS0534", "CS0535"]);
            errorsAfter.Select(e => e.GetProperty("message").GetString()).Should().NotContain(m => m!.Contains("MemoryRepository"));
        });
    }

    [Fact]
    public async Task ImplementInterface_Explicitly_GeneratesExplicitImplementations()
    {
        await WithFixtureRestored(async () =>
        {
            var original = await File.ReadAllTextAsync(ContractsFile);
            var (line, column) = PositionOf(ContractsFile, "class MemoryRepository", "MemoryRepository");

            var result = Parse(await ImplementationTools.ImplementInterface(filePath: ContractsFile, line: line, column: column, explicitly: true));

            result.GetProperty("applied").GetBoolean().Should().BeTrue(result.GetRawText());
            result.GetProperty("explicit").GetBoolean().Should().BeTrue();
            Strings(result, "generated").Should().Equal(MemoryRepositoryMissing);
            (await File.ReadAllTextAsync(ContractsFile)).Should().Be(InsertAfter(original, MemoryRepositoryAnchor, ExplicitStubs));
            (await ErrorsAsync()).Should().HaveCount(2);
        });
    }

    [Fact]
    public async Task ImplementInterface_InterfaceNameFilter_GeneratesOnlyThatInterfacesMembers()
    {
        await WithFixtureRestored(async () =>
        {
            var result = Parse(await ImplementationTools.ImplementInterface(
                fullyQualifiedName: "GenerationLib.MemoryRepository", interfaceName: "IReadOnlyRepo"));

            result.GetProperty("applied").GetBoolean().Should().BeTrue(result.GetRawText());
            Strings(result, "interfaces").Should().Equal("GenerationLib.IReadOnlyRepo<string>");
            Strings(result, "generated").Should().Equal("GenerationLib.IReadOnlyRepo<string>.Count");
            (await ErrorsAsync()).Should().HaveCount(BaselineErrors - 1);
        });
    }

    [Fact]
    public async Task ImplementInterface_Plain_RefusesNothingToGenerate()
    {
        await WithFixtureRestored(async () =>
        {
            var result = Parse(await ImplementationTools.ImplementInterface(fullyQualifiedName: "GenerationLib.Plain"));

            ShouldBeRefusal(result, "Nothing to generate");
            Strings(result, "alreadyImplemented").Should().Equal("GenerationLib.INamed.Name");
            Strings(result, "interfaces").Should().Equal("GenerationLib.INamed");
            result.TryGetProperty("generated", out _).Should().BeFalse();
        });
    }

    [Fact]
    public async Task ImplementInterface_PartialType_StubsLandInTheFirstDeclarationsFile_AndTheResultNamesIt()
    {
        await WithFixtureRestored(async () =>
        {
            var original = await File.ReadAllTextAsync(ContractsFile);
            var part2Before = await File.ReadAllTextAsync(Part2File);

            var result = Parse(await ImplementationTools.ImplementInterface(fullyQualifiedName: "GenerationLib.Ledger"));

            result.GetProperty("applied").GetBoolean().Should().BeTrue(result.GetRawText());
            Strings(result, "generated").Should().Equal("GenerationLib.IAuditable.LastAudit");
            Strings(result, "alreadyImplemented").Should().Equal("GenerationLib.IAuditable.Record(string)");
            result.GetProperty("insertedInto").GetString().Should().Be(ContractsFile,
                "the first declaration is the Contracts.cs part, even though Contracts.Part2.cs names the interface");
            Strings(result, "partialDeclarationFiles").Should().Equal(ContractsFile, Part2File);
            result.GetProperty("message").GetString().Should().Contain(ContractsFile).And.Contain("partial");

            (await File.ReadAllTextAsync(ContractsFile)).Should().Be(InsertAfter(original, LedgerAnchor, LastAuditStub));
            (await File.ReadAllTextAsync(Part2File)).Should().Be(part2Before);
            (await ErrorsAsync()).Should().HaveCount(BaselineErrors - 1);
        });
    }

    // ---------------------------------------------------------------- implement_abstract

    [Fact]
    public async Task ImplementAbstract_Circle2_GeneratesTheMissingOverrideWithItsDocComment()
    {
        await WithFixtureRestored(async () =>
        {
            var original = await File.ReadAllTextAsync(ContractsFile);

            var dry = Parse(await ImplementationTools.ImplementAbstract(fullyQualifiedName: "GenerationLib.Circle2", dryRun: true));
            dry.GetProperty("wouldApply").GetBoolean().Should().BeTrue(dry.GetRawText());
            (await File.ReadAllTextAsync(ContractsFile)).Should().Be(original);

            var result = Parse(await ImplementationTools.ImplementAbstract(fullyQualifiedName: "GenerationLib.Circle2"));

            result.GetProperty("applied").GetBoolean().Should().BeTrue(result.GetRawText());
            Raw(dry, "changes").Should().Be(Raw(result, "changes"));
            Strings(result, "abstractBases").Should().Equal("GenerationLib.Shape2");
            Strings(result, "generated").Should().Equal("GenerationLib.Shape2.Perimeter");
            (await File.ReadAllTextAsync(ContractsFile)).Should().Be(InsertAfter(original, Circle2Anchor, PerimeterStub));

            var errors = await ErrorsAsync();
            errors.Should().HaveCount(BaselineErrors - 1);
            errors.Select(e => e.GetProperty("id").GetString()).Should().NotContain("CS0534");
        });
    }

    [Fact]
    public async Task ImplementAbstract_Plain_RefusesNoAbstractBase()
    {
        await WithFixtureRestored(async () =>
        {
            var result = Parse(await ImplementationTools.ImplementAbstract(fullyQualifiedName: "GenerationLib.Plain"));

            ShouldBeRefusal(result, "has no abstract base class");
            result.GetProperty("abstractBases").GetArrayLength().Should().Be(0);
        });
    }

    // ---------------------------------------------------------------- generate_overrides

    [Fact]
    public async Task GenerateOverrides_WithoutMembers_RefusesAndListsCandidates()
    {
        await WithFixtureRestored(async () =>
        {
            var result = Parse(await ImplementationTools.GenerateOverrides(fullyQualifiedName: "GenerationLib.Circle2"));

            ShouldBeRefusal(result, "pass members");
            Strings(result, "candidates").Should().Equal(
                "GenerationLib.Shape2.Describe()",
                "object.Equals(object?)",
                "object.GetHashCode()",
                "object.ToString()");

            var unknown = Parse(await ImplementationTools.GenerateOverrides(fullyQualifiedName: "GenerationLib.Circle2", members: ["Area"]));
            ShouldBeRefusal(unknown, "no overridable virtual member named 'Area'");
            unknown.GetProperty("candidates").GetArrayLength().Should().Be(4);
        });
    }

    [Fact]
    public async Task GenerateOverrides_ToString_GeneratesABaseCallingOverride_AndTheGateAccepts()
    {
        await WithFixtureRestored(async () =>
        {
            var original = await File.ReadAllTextAsync(ContractsFile);

            var result = Parse(await ImplementationTools.GenerateOverrides(fullyQualifiedName: "GenerationLib.Circle2", members: ["ToString"]));

            result.GetProperty("applied").GetBoolean().Should().BeTrue(result.GetRawText());
            Strings(result, "generated").Should().Equal("object.ToString()");
            result.TryGetProperty("candidates", out _).Should().BeFalse();
            (await File.ReadAllTextAsync(ContractsFile)).Should().Be(InsertAfter(original, Circle2Anchor, ToStringOverride));
            (await ErrorsAsync()).Should().HaveCount(BaselineErrors, "the override adds no error and fixes none");
        });
    }

    private static (int Line, int Column) PositionOf(string filePath, string lineNeedle, string token)
    {
        var lines = File.ReadAllLines(filePath);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(lineNeedle, StringComparison.Ordinal))
            {
                return (i + 1, lines[i].IndexOf(token, StringComparison.Ordinal) + 1);
            }
        }
        throw new InvalidOperationException($"'{lineNeedle}' not found in {filePath}");
    }
}
