using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Build.Locator;
using RoslynMcp.Tools;
using Xunit;

namespace RoslynMcp.Tests;

/// <summary>
/// get_symbol_info, get_type_hierarchy and find_unused_parameters against the fixture's
/// FixtureLib/Animals.cs and FixtureLib/Params.cs. Read-only tools - no cleanup needed.
///
/// ANSWER KEY
/// get_symbol_info
///   FixtureLib.Dog: kind class, public, no modifiers, baseType FixtureLib.Animal,
///     interfaces [FixtureLib.IAnimal, FixtureLib.ITrainable], one declaration Animals.cs:29:14,
///     isFromMetadata false, no doc comment, no attributes.
///   FixtureLib.Dog.Fetch: method, public, [virtual], returns int, parameters
///     (item: string) (maxTries: int = 3), docComment = summary + param XML,
///     attributes [System.ComponentModel.DescriptionAttribute("fetch")].
///   FixtureLib.Puppy.Fetch: [override], overrides FixtureLib.Dog.Fetch(string, int).
///   FixtureLib.Dog.Train: implements [FixtureLib.ITrainable.Train(string)].
///   FixtureLib.Puppy: [sealed].
///   System.String: class, [sealed], declarations [], isFromMetadata true.
///   Params.cs:59:48 ('sample' of Scaled): parameter, type FixtureLib.ParamsSample, modifiers [this], ordinal 0.
/// get_type_hierarchy
///   Animal: bases [object (metadata)], interfaces [IAnimal], derived Dog (depth 1), Puppy (depth 2).
///   IAnimal: bases [], derived Animal (implementation, 1), ITrainable (derivedInterface, 1),
///     Dog (derivedClass via Animal, 2), Puppy (derivedClass via Dog, 3).
///   Puppy: bases Dog, Animal, object. Dog.Speak: Dog's hierarchy with a note.
///   Animal with maxNodes=1: [Dog], truncated.
/// find_unused_parameters (whole solution)
///   high: Params.cs Twice.ignored (local function), PrivateHelper.b
///   low:  ParamsSample..ctor.unusedSeed, OneUsedOneUnused.unused, OnSomethingHappened.sender + .e
///         (conventional), Scaled.factor, ParamsBase.Score.bonus, Shipping.cs Shipper.Surcharge.reason
///   excludedCounts: discard 1, entryPoint 1, extensionThis 1, interfaceImplementation 1,
///         noBody 8 (ParamsBase.Handle 1, IParamsSink.Accept 2, ITrainable.Train 1, plus 4
///         interface-member parameters in pre-existing fixtures), override 3 (ParamsDerived.Handle.code,
///         ParamsDerived.Score.points, Puppy.Fetch.maxTries), usedAsDelegate 1 (Transform.unusedFactor)
///   never reported: OnlyInNameof.value (nameof), PassThrough.amount (passed on), WithDiscard._
/// </summary>
[Collection("workspace")]  // WorkspaceHost is a process-wide singleton: classes that load solutions must not run in parallel
public class SymbolInfoToolsTests : IAsyncLifetime
{
    private static readonly string SolutionPath = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureSolution.slnx"));
    private static readonly string ParamsFile = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureLib", "Params.cs"));

    static SymbolInfoToolsTests()
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
        result.GetProperty("projectCount").GetInt32().Should().Be(2);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static void EnsureRestored()
    {
        var assetsPath = Path.Combine(Path.GetDirectoryName(SolutionPath)!,
            "FixtureLib", "obj", "project.assets.json");
        if (File.Exists(assetsPath)) return;

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

    private static List<string> Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToList();

    private static bool Has(JsonElement element, string property) => element.TryGetProperty(property, out _);

    // ------------------------------------------------------------------ get_symbol_info

    [Fact]
    public async Task GetSymbolInfo_Dog_ReportsKindBasesSortedInterfacesAndTheDeclaration()
    {
        var info = Parse(await SymbolInfoTools.GetSymbolInfo(fullyQualifiedName: "FixtureLib.Dog"));

        info.GetProperty("symbol").GetProperty("display").GetString().Should().Be("FixtureLib.Dog");
        info.GetProperty("kind").GetString().Should().Be("class");
        info.GetProperty("accessibility").GetString().Should().Be("public");
        Strings(info, "modifiers").Should().BeEmpty();
        info.GetProperty("containingNamespace").GetString().Should().Be("FixtureLib");
        Has(info, "containingType").Should().BeFalse();
        info.GetProperty("baseType").GetString().Should().Be("FixtureLib.Animal");
        Strings(info, "interfaces").Should().Equal("FixtureLib.IAnimal", "FixtureLib.ITrainable");
        info.GetProperty("isFromMetadata").GetBoolean().Should().BeFalse();
        Has(info, "docComment").Should().BeFalse();
        Strings(info, "attributes").Should().BeEmpty();

        var declarations = info.GetProperty("declarations").EnumerateArray().ToList();
        declarations.Should().HaveCount(1);
        Path.GetFileName(declarations[0].GetProperty("file").GetString()).Should().Be("Animals.cs");
        declarations[0].GetProperty("line").GetInt32().Should().Be(29);
        declarations[0].GetProperty("column").GetInt32().Should().Be(14);

        var puppy = Parse(await SymbolInfoTools.GetSymbolInfo(fullyQualifiedName: "FixtureLib.Puppy"));
        Strings(puppy, "modifiers").Should().Equal("sealed");
        puppy.GetProperty("baseType").GetString().Should().Be("FixtureLib.Dog");
    }

    [Fact]
    public async Task GetSymbolInfo_MethodWithDocCommentAndAttribute_ReadsBoth()
    {
        var info = Parse(await SymbolInfoTools.GetSymbolInfo(fullyQualifiedName: "FixtureLib.Dog.Fetch"));

        info.GetProperty("kind").GetString().Should().Be("method");
        info.GetProperty("accessibility").GetString().Should().Be("public");
        Strings(info, "modifiers").Should().Equal("virtual");
        info.GetProperty("containingType").GetString().Should().Be("FixtureLib.Dog");
        info.GetProperty("returnType").GetString().Should().Be("int");
        Has(info, "overrides").Should().BeFalse();

        var parameters = info.GetProperty("parameters").EnumerateArray().ToList();
        parameters.Select(p => p.GetProperty("name").GetString()).Should().Equal("item", "maxTries");
        parameters.Select(p => p.GetProperty("type").GetString()).Should().Equal("string", "int");
        Has(parameters[0], "default").Should().BeFalse();
        parameters[1].GetProperty("default").GetString().Should().Be("3");

        info.GetProperty("docComment").GetString().Should().Be(
            "<summary>Fetches the item and returns how many tries it took.</summary>\n<param name=\"item\">What to fetch.</param>");
        Strings(info, "attributes").Should().Equal("System.ComponentModel.DescriptionAttribute(\"fetch\")");

        var overrider = Parse(await SymbolInfoTools.GetSymbolInfo(fullyQualifiedName: "FixtureLib.Puppy.Fetch"));
        Strings(overrider, "modifiers").Should().Equal("override");
        overrider.GetProperty("overrides").GetString().Should().Be("FixtureLib.Dog.Fetch(string, int)");

        var train = Parse(await SymbolInfoTools.GetSymbolInfo(fullyQualifiedName: "FixtureLib.Dog.Train"));
        Strings(train, "implements").Should().Equal("FixtureLib.ITrainable.Train(string)");
    }

    [Fact]
    public async Task GetSymbolInfo_SystemString_WorksFromMetadata()
    {
        var info = Parse(await SymbolInfoTools.GetSymbolInfo(fullyQualifiedName: "System.String"));

        info.GetProperty("kind").GetString().Should().Be("class");
        Strings(info, "modifiers").Should().Equal("sealed");
        info.GetProperty("containingNamespace").GetString().Should().Be("System");
        info.GetProperty("baseType").GetString().Should().Be("object");
        info.GetProperty("declarations").GetArrayLength().Should().Be(0);
        info.GetProperty("isFromMetadata").GetBoolean().Should().BeTrue();
        var interfaces = Strings(info, "interfaces");
        interfaces.Should().Contain("System.IComparable");
        interfaces.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public async Task GetSymbolInfo_ExtensionThisParameter_ReportsParameterShape()
    {
        // Params.cs line 59: "    public static int Scaled(this ParamsSample sample, int factor) ..." - 'sample' at column 48.
        var info = Parse(await SymbolInfoTools.GetSymbolInfo(ParamsFile, 59, 48));

        info.GetProperty("symbol").GetProperty("name").GetString().Should().Be("sample");
        info.GetProperty("kind").GetString().Should().Be("parameter");
        info.GetProperty("type").GetString().Should().Be("FixtureLib.ParamsSample");
        Strings(info, "modifiers").Should().Equal("this");
        info.GetProperty("ordinal").GetInt32().Should().Be(0);
        info.GetProperty("containingMember").GetString().Should().Be("FixtureLib.ParamsExtensions.Scaled(FixtureLib.ParamsSample, int)");
        info.GetProperty("declarations").GetArrayLength().Should().Be(1);

        var method = Parse(await SymbolInfoTools.GetSymbolInfo(fullyQualifiedName: "FixtureLib.ParamsExtensions.Scaled"));
        Strings(method, "modifiers").Should().Equal("static", "extension");
        method.GetProperty("parameters")[0].GetProperty("modifier").GetString().Should().Be("this");
    }

    // ------------------------------------------------------------------ get_type_hierarchy

    private static List<(string Symbol, int Depth, string Relation, string Via)> Derived(JsonElement result) =>
        result.GetProperty("derived").EnumerateArray()
            .Select(n => (n.GetProperty("symbol").GetString()!, n.GetProperty("depth").GetInt32(),
                n.GetProperty("relation").GetString()!, n.GetProperty("via").GetString()!))
            .ToList();

    [Fact]
    public async Task GetTypeHierarchy_Animal_WalksUpToObjectAndDownTransitively()
    {
        var result = Parse(await SymbolInfoTools.GetTypeHierarchy(fullyQualifiedName: "FixtureLib.Animal"));

        result.GetProperty("kind").GetString().Should().Be("class");
        var bases = result.GetProperty("bases").EnumerateArray().ToList();
        bases.Select(b => b.GetProperty("symbol").GetString()).Should().Equal("object");
        bases[0].GetProperty("isFromMetadata").GetBoolean().Should().BeTrue();
        result.GetProperty("interfaces").EnumerateArray().Select(i => i.GetProperty("symbol").GetString())
            .Should().Equal("FixtureLib.IAnimal");

        Derived(result).Should().Equal(
            ("FixtureLib.Dog", 1, "derivedClass", "FixtureLib.Animal"),
            ("FixtureLib.Puppy", 2, "derivedClass", "FixtureLib.Dog"));
        result.GetProperty("derived").EnumerateArray()
            .Select(n => Path.GetFileName(n.GetProperty("file").GetString())).Should().Equal("Animals.cs", "Animals.cs");
        Has(result, "truncated").Should().BeFalse();

        var puppy = Parse(await SymbolInfoTools.GetTypeHierarchy(fullyQualifiedName: "FixtureLib.Puppy"));
        puppy.GetProperty("bases").EnumerateArray().Select(b => b.GetProperty("symbol").GetString())
            .Should().Equal("FixtureLib.Dog", "FixtureLib.Animal", "object");
        puppy.GetProperty("interfaces").EnumerateArray().Select(i => i.GetProperty("symbol").GetString())
            .Should().Equal("FixtureLib.IAnimal", "FixtureLib.ITrainable");
        puppy.GetProperty("derivedCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task GetTypeHierarchy_IAnimal_ListsDerivedInterfacesAndImplementations()
    {
        var result = Parse(await SymbolInfoTools.GetTypeHierarchy(fullyQualifiedName: "FixtureLib.IAnimal"));

        result.GetProperty("kind").GetString().Should().Be("interface");
        result.GetProperty("bases").GetArrayLength().Should().Be(0);
        Derived(result).Should().Equal(
            ("FixtureLib.Animal", 1, "implementation", "FixtureLib.IAnimal"),
            ("FixtureLib.ITrainable", 1, "derivedInterface", "FixtureLib.IAnimal"),
            ("FixtureLib.Dog", 2, "derivedClass", "FixtureLib.Animal"),
            ("FixtureLib.Puppy", 3, "derivedClass", "FixtureLib.Dog"));

        var trainable = Parse(await SymbolInfoTools.GetTypeHierarchy(fullyQualifiedName: "FixtureLib.ITrainable"));
        trainable.GetProperty("bases").EnumerateArray().Select(b => b.GetProperty("symbol").GetString())
            .Should().Equal("FixtureLib.IAnimal");
        Derived(trainable).Should().Equal(
            ("FixtureLib.Dog", 1, "implementation", "FixtureLib.ITrainable"),
            ("FixtureLib.Puppy", 2, "derivedClass", "FixtureLib.Dog"));
    }

    [Fact]
    public async Task GetTypeHierarchy_MaxNodesAndMemberSymbols()
    {
        var capped = Parse(await SymbolInfoTools.GetTypeHierarchy(fullyQualifiedName: "FixtureLib.Animal", maxNodes: 1));
        Derived(capped).Should().Equal(("FixtureLib.Dog", 1, "derivedClass", "FixtureLib.Animal"));
        capped.GetProperty("truncated").GetBoolean().Should().BeTrue();

        var member = Parse(await SymbolInfoTools.GetTypeHierarchy(fullyQualifiedName: "FixtureLib.Dog.Speak"));
        member.GetProperty("type").GetProperty("display").GetString().Should().Be("FixtureLib.Dog");
        member.GetProperty("note").GetString().Should().Contain("containing type FixtureLib.Dog");
        Derived(member).Should().Equal(("FixtureLib.Puppy", 1, "derivedClass", "FixtureLib.Dog"));
    }

    // ------------------------------------------------------------------ find_unused_parameters

    private static List<string> Hits(JsonElement tier) =>
        tier.EnumerateArray()
            .Select(h => $"{Path.GetFileName(h.GetProperty("file").GetString())}:{h.GetProperty("line").GetInt32()}:" +
                         $"{h.GetProperty("method").GetProperty("name").GetString()}.{h.GetProperty("parameter").GetString()}" +
                         $"#{h.GetProperty("index").GetInt32()}" +
                         (h.TryGetProperty("conventional", out var c) && c.GetBoolean() ? " (conventional)" : ""))
            .ToList();

    [Fact]
    public async Task FindUnusedParameters_WholeSolution_MatchesTheExactAnswerKey()
    {
        var result = Parse(await SymbolInfoTools.FindUnusedParameters());

        Hits(result.GetProperty("highConfidence")).Should().Equal(
            "Params.cs:43:Twice.ignored#1",
            "Params.cs:52:PrivateHelper.b#1");

        Hits(result.GetProperty("lowConfidence")).Should().Equal(
            "Params.cs:20:.ctor.unusedSeed#1",
            "Params.cs:27:OneUsedOneUnused.unused#1",
            "Params.cs:35:OnSomethingHappened.sender#0 (conventional)",
            "Params.cs:35:OnSomethingHappened.e#1 (conventional)",
            "Params.cs:59:Scaled.factor#1",
            "Params.cs:66:Score.bonus#1",
            "Shipping.cs:24:Surcharge.reason#1");

        result.GetProperty("highConfidenceCount").GetInt32().Should().Be(2);
        result.GetProperty("lowConfidenceCount").GetInt32().Should().Be(7);
        result.GetProperty("totalFound").GetInt32().Should().Be(9);
        Has(result, "truncated").Should().BeFalse();
        result.GetProperty("highConfidence").EnumerateArray()
            .Select(h => h.GetProperty("confidence").GetString()).Should().AllBe("high");

        var excluded = result.GetProperty("excludedCounts").EnumerateObject()
            .Select(p => $"{p.Name}={p.Value.GetInt32()}").ToList();
        excluded.Should().Equal(
            "discard=1",
            "entryPoint=1",
            "extensionThis=1",
            "interfaceImplementation=1",
            "noBody=8",
            "override=3",
            "usedAsDelegate=1");

        var everything = Hits(result.GetProperty("highConfidence")).Concat(Hits(result.GetProperty("lowConfidence"))).ToList();
        everything.Should().NotContain(h => h.Contains("OnlyInNameof"), "nameof(value) counts as a use");
        everything.Should().NotContain(h => h.Contains("PassThrough"), "passing a parameter along counts as a use");
        everything.Should().NotContain(h => h.Contains("._#"), "discards are never reported");
        everything.Should().NotContain(h => h.Contains("Transform"), "method-group use excludes the signature");
        everything.Should().NotContain(h => h.Contains("Main"), "Main(string[] args) is an entry point");

        result.GetProperty("limitations").GetString().Should().Contain("reflection");
    }

    [Fact]
    public async Task FindUnusedParameters_FileScopeAndMaxResults()
    {
        var file = Parse(await SymbolInfoTools.FindUnusedParameters(filePath: ParamsFile));
        file.GetProperty("totalFound").GetInt32().Should().Be(8);
        Hits(file.GetProperty("lowConfidence")).Should().NotContain(h => h.StartsWith("Shipping.cs"));

        var capped = Parse(await SymbolInfoTools.FindUnusedParameters(projectName: "FixtureLib", maxResults: 3));
        capped.GetProperty("truncated").GetBoolean().Should().BeTrue();
        capped.GetProperty("totalFound").GetInt32().Should().Be(9);
        Hits(capped.GetProperty("highConfidence")).Concat(Hits(capped.GetProperty("lowConfidence")))
            .Should().BeEquivalentTo("Params.cs:20:.ctor.unusedSeed#1", "Params.cs:27:OneUsedOneUnused.unused#1",
                "Params.cs:35:OnSomethingHappened.sender#0 (conventional)");

        var app = Parse(await SymbolInfoTools.FindUnusedParameters(projectName: "FixtureApp"));
        app.GetProperty("totalFound").GetInt32().Should().Be(0);
    }
}
