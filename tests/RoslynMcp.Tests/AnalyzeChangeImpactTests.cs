using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Build.Locator;
using RoslynMcp.Tools;
using Xunit;

namespace RoslynMcp.Tests;

/*
 * analyze_change_impact answer key for fixtures/ImpactSolution (ImpactLib, ImpactApp -> ImpactLib,
 * ImpactTests -> ImpactLib). Prefixes: L = ImpactLib, T = ImpactTests.PipelineTests.
 *
 * ImpactLib.Pipeline.Execute (depth 3):
 *   dependents  ImpactApp:   ImpactApp.Program.Main          d1 caller (entry point)
 *               ImpactTests: T.ExecuteUppercases (12:17)     d1 caller
 *                            T.ExecuteViaHelper  (15:17)     d2 caller, reaches T.RunTrim
 *                            T.RunTrim           (17:27)     d1 caller
 *                            T.NotATest          (27:17)     d1 caller (no attribute: NOT a test)
 *   projectsToRecompile [ImpactApp, ImpactLib, ImpactTests]; nodeCount 5; note set (public)
 *   tests  T.ExecuteUppercases d1 [T.ExecuteUppercases, L.Pipeline.Execute]
 *          T.ExecuteViaHelper  d2 [T.ExecuteViaHelper, T.RunTrim, L.Pipeline.Execute]
 *   depth 1: the four d1 dependents, tests = [T.ExecuteUppercases] only.
 *   maxNodes 1: nodeCount 1, truncated true.
 *
 * ImpactLib.TrimStage.Run (depth 3):
 *   d1 L.Pipeline.Execute via IStage.Run(string); T.TrimStageTrims (direct)
 *   d2 Main, T.ExecuteUppercases, T.RunTrim, T.NotATest;  d3 T.ExecuteViaHelper
 *   tests  T.ExecuteUppercases d2 [T.ExecuteUppercases, L.Pipeline.Execute, L.TrimStage.Run]
 *          T.ExecuteViaHelper  d3 [T.ExecuteViaHelper, T.RunTrim, L.Pipeline.Execute, L.TrimStage.Run]
 *          T.TrimStageTrims    d1 [T.TrimStageTrims, L.TrimStage.Run]
 *
 * ImpactLib.IStage.Run: d1 = L.Pipeline.Execute (caller) + L.UpperStage.Run, L.TrimStage.Run (implementation).
 *
 * ImpactLib.Config (type): d1 references = L.Pipeline.Describe (signature), T.ConfigHasDefaultName;
 *   Config's own Name property is excluded; tests = [T.ConfigHasDefaultName d1 [.., L.Config]].
 *
 * ImpactLib.Helper.Shout (internal): d1 L.UpperStage.Run, d2 L.Pipeline.Execute, d3 the four callers;
 *   tests = [T.ExecuteUppercases d3]; T.ExecuteViaHelper would be d4 (beyond depth); note null.
 *
 * ImpactLib.Orphan.Nothing: no dependents, no tests, nodeCount 0,
 *   projectsToRecompile still [ImpactApp, ImpactLib, ImpactTests].
 */
[Collection("workspace")]  // WorkspaceHost is a process-wide singleton: classes that load solutions must not run in parallel
public class AnalyzeChangeImpactTests : IAsyncLifetime
{
    private static readonly string SolutionDir = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "ImpactSolution"));
    private static readonly string SolutionPath = Path.Combine(SolutionDir, "ImpactSolution.slnx");
    private static readonly string PipelineFile = Path.Combine(SolutionDir, "ImpactLib", "Pipeline.cs");
    private static readonly string TestsFile = Path.Combine(SolutionDir, "ImpactTests", "PipelineTests.cs");
    private static readonly string FixtureSolutionPath = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureSolution.slnx"));

    private const string T = "ImpactTests.PipelineTests.";
    private const string Execute = "ImpactLib.Pipeline.Execute";
    private static readonly string[] AllProjects = ["ImpactApp", "ImpactLib", "ImpactTests"];

    static AnalyzeChangeImpactTests()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }

    public async Task InitializeAsync()
    {
        EnsureRestored(SolutionPath, "ImpactLib", "ImpactApp", "ImpactTests");
        var result = Parse(await SolutionTools.LoadSolution(SolutionPath));
        result.GetProperty("projectCount").GetInt32().Should().Be(3);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static void EnsureRestored(string solutionPath, params string[] projects)
    {
        var dir = Path.GetDirectoryName(solutionPath)!;
        if (projects.All(p => File.Exists(Path.Combine(dir, p, "obj", "project.assets.json")))) return;

        var psi = new ProcessStartInfo("dotnet", $"restore \"{solutionPath}\"")
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

    private static List<string> Strings(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetString()!).ToList();

    private static string? Str(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) ? value.GetString() : null;

    /// <summary>(project, symbol, distance) for every dependent, in output order.</summary>
    private static List<(string Project, string Symbol, int Distance)> Dependents(JsonElement result) =>
        result.GetProperty("dependents").EnumerateArray()
            .SelectMany(g => g.GetProperty("members").EnumerateArray()
                .Select(m => (g.GetProperty("project").GetString()!, m.GetProperty("symbol").GetString()!, m.GetProperty("distance").GetInt32())))
            .ToList();

    private static JsonElement Member(JsonElement result, string symbol) =>
        result.GetProperty("dependents").EnumerateArray()
            .SelectMany(g => g.GetProperty("members").EnumerateArray())
            .Single(m => m.GetProperty("symbol").GetString() == symbol);

    private static List<(string Symbol, int Distance, List<string> Path)> Tests(JsonElement result) =>
        result.GetProperty("tests").EnumerateArray()
            .Select(t => (t.GetProperty("symbol").GetString()!, t.GetProperty("distance").GetInt32(), Strings(t.GetProperty("path"))))
            .ToList();

    [Fact]
    public async Task Execute_ReportsDependentsProjectsAndTestsWithShortestPaths()
    {
        var result = Parse(await ImpactTools.AnalyzeChangeImpact(fullyQualifiedName: Execute));

        result.GetProperty("targets").EnumerateArray().Single().GetProperty("name").GetString().Should().Be("Execute");
        result.GetProperty("depth").GetInt32().Should().Be(3);
        result.GetProperty("truncated").GetBoolean().Should().BeFalse();
        result.GetProperty("nodeCount").GetInt32().Should().Be(5);
        Strings(result.GetProperty("projectsToRecompile")).Should().Equal(AllProjects);
        Str(result, "note").Should().Contain("External consumers");

        Dependents(result).Should().Equal(
            ("ImpactApp", "ImpactApp.Program.Main", 1),
            ("ImpactTests", T + "ExecuteUppercases", 1),
            ("ImpactTests", T + "ExecuteViaHelper", 2),
            ("ImpactTests", T + "RunTrim", 1),
            ("ImpactTests", T + "NotATest", 1));

        var main = Member(result, "ImpactApp.Program.Main");
        main.GetProperty("isEntryPoint").GetBoolean().Should().BeTrue();
        main.GetProperty("relation").GetString().Should().Be("caller");
        main.GetProperty("kind").GetString().Should().Be("Method");

        var viaHelper = Member(result, T + "ExecuteViaHelper");
        Str(viaHelper, "reaches").Should().Be(T + "RunTrim");
        viaHelper.GetProperty("file").GetString().Should().Be(TestsFile);
        viaHelper.GetProperty("line").GetInt32().Should().Be(15);
        viaHelper.GetProperty("column").GetInt32().Should().Be(17);

        var tests = Tests(result);
        tests.Select(t => (t.Symbol, t.Distance)).Should().Equal(
            (T + "ExecuteUppercases", 1),
            (T + "ExecuteViaHelper", 2));
        tests[0].Path.Should().Equal(T + "ExecuteUppercases", Execute);
        tests[1].Path.Should().Equal(T + "ExecuteViaHelper", T + "RunTrim", Execute);

        var first = result.GetProperty("tests")[0];
        first.GetProperty("project").GetString().Should().Be("ImpactTests");
        first.GetProperty("file").GetString().Should().Be(TestsFile);
        first.GetProperty("line").GetInt32().Should().Be(12);
    }

    [Fact]
    public async Task Execute_ByPosition_MatchesTheFullyQualifiedResult()
    {
        var byPosition = await ImpactTools.AnalyzeChangeImpact(PipelineFile, 9, 26);
        var byName = await ImpactTools.AnalyzeChangeImpact(fullyQualifiedName: Execute);
        byPosition.Should().Be(byName);
    }

    [Fact]
    public async Task Execute_DepthOne_CutsTheTwoHopTest()
    {
        var result = Parse(await ImpactTools.AnalyzeChangeImpact(fullyQualifiedName: Execute, depth: 1));

        result.GetProperty("depth").GetInt32().Should().Be(1);
        Dependents(result).Select(d => d.Symbol).Should().Equal(
            "ImpactApp.Program.Main", T + "ExecuteUppercases", T + "RunTrim", T + "NotATest");
        var test = Tests(result).Should().ContainSingle().Subject;
        test.Symbol.Should().Be(T + "ExecuteUppercases");
        test.Path.Should().Equal(T + "ExecuteUppercases", Execute);
    }

    [Fact]
    public async Task Execute_MaxNodesOne_IsTruncated()
    {
        var result = Parse(await ImpactTools.AnalyzeChangeImpact(fullyQualifiedName: Execute, maxNodes: 1));

        result.GetProperty("truncated").GetBoolean().Should().BeTrue();
        result.GetProperty("nodeCount").GetInt32().Should().Be(1);
        Dependents(result).Should().ContainSingle();
    }

    [Fact]
    public async Task StageRun_ReachesThePipelineFactThroughInterfaceDispatch()
    {
        var result = Parse(await ImpactTools.AnalyzeChangeImpact(fullyQualifiedName: "ImpactLib.TrimStage.Run"));

        var execute = Member(result, Execute);
        execute.GetProperty("distance").GetInt32().Should().Be(1);
        Str(execute, "via").Should().Be("IStage.Run(string)");
        Member(result, T + "TrimStageTrims").GetProperty("distance").GetInt32().Should().Be(1);
        Str(Member(result, T + "TrimStageTrims"), "via").Should().BeNull();

        var tests = Tests(result);
        tests.Select(t => (t.Symbol, t.Distance)).Should().Equal(
            (T + "ExecuteUppercases", 2),
            (T + "ExecuteViaHelper", 3),
            (T + "TrimStageTrims", 1));
        tests[0].Path.Should().Equal(T + "ExecuteUppercases", Execute, "ImpactLib.TrimStage.Run");
        tests[1].Path.Should().Equal(T + "ExecuteViaHelper", T + "RunTrim", Execute, "ImpactLib.TrimStage.Run");
        tests[2].Path.Should().Equal(T + "TrimStageTrims", "ImpactLib.TrimStage.Run");
        tests.Select(t => t.Symbol).Should().NotContain(T + "NotATest");
    }

    [Fact]
    public async Task InterfaceMember_ListsImplementationsAsDependents()
    {
        var result = Parse(await ImpactTools.AnalyzeChangeImpact(fullyQualifiedName: "ImpactLib.IStage.Run", depth: 1));

        var lib = Dependents(result).Where(d => d.Project == "ImpactLib").Select(d => d.Symbol).ToList();
        lib.Should().Equal(Execute, "ImpactLib.UpperStage.Run", "ImpactLib.TrimStage.Run");
        Member(result, "ImpactLib.UpperStage.Run").GetProperty("relation").GetString().Should().Be("implementation");
        Member(result, Execute).GetProperty("relation").GetString().Should().Be("caller");
    }

    [Fact]
    public async Task ConfigType_ListsReferencingMembersAndTheConfigOnlyTest()
    {
        var result = Parse(await ImpactTools.AnalyzeChangeImpact(fullyQualifiedName: "ImpactLib.Config"));

        result.GetProperty("targets")[0].GetProperty("kind").GetString().Should().Be("NamedType");
        Dependents(result).Should().Equal(
            ("ImpactLib", "ImpactLib.Pipeline.Describe", 1),
            ("ImpactTests", T + "ConfigHasDefaultName", 1));
        Member(result, "ImpactLib.Pipeline.Describe").GetProperty("relation").GetString().Should().Be("reference");

        var test = Tests(result).Should().ContainSingle().Subject;
        test.Path.Should().Equal(T + "ConfigHasDefaultName", "ImpactLib.Config");
        Strings(result.GetProperty("projectsToRecompile")).Should().Equal(AllProjects);
    }

    [Fact]
    public async Task InternalHelper_StopsAtDepthAndHasNoExternalNote()
    {
        var result = Parse(await ImpactTools.AnalyzeChangeImpact(fullyQualifiedName: "ImpactLib.Helper.Shout"));

        Member(result, "ImpactLib.UpperStage.Run").GetProperty("distance").GetInt32().Should().Be(1);
        Member(result, Execute).GetProperty("distance").GetInt32().Should().Be(2);
        Dependents(result).Select(d => d.Symbol).Should().NotContain(T + "ExecuteViaHelper", "it is 4 hops away");

        var test = Tests(result).Should().ContainSingle().Subject;
        test.Distance.Should().Be(3);
        test.Path.Should().Equal(T + "ExecuteUppercases", Execute, "ImpactLib.UpperStage.Run", "ImpactLib.Helper.Shout");
        result.TryGetProperty("note", out _).Should().BeFalse("Helper is internal");
    }

    [Fact]
    public async Task Orphan_HasNoDependentsOrTestsButStillListsRecompiledProjects()
    {
        var result = Parse(await ImpactTools.AnalyzeChangeImpact(fullyQualifiedName: "ImpactLib.Orphan.Nothing"));

        result.GetProperty("dependents").GetArrayLength().Should().Be(0);
        result.GetProperty("tests").GetArrayLength().Should().Be(0);
        result.GetProperty("nodeCount").GetInt32().Should().Be(0);
        result.GetProperty("truncated").GetBoolean().Should().BeFalse();
        Strings(result.GetProperty("projectsToRecompile")).Should().Equal(AllProjects);
    }

    [Fact]
    public async Task Batch_MergesTargets()
    {
        var result = Parse(await ImpactTools.AnalyzeChangeImpact(
            fullyQualifiedNames: ["ImpactLib.Orphan.Nothing", "ImpactLib.Config"]));

        result.GetProperty("targets").EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .Should().Equal("Config", "Nothing");
        Tests(result).Select(t => t.Symbol).Should().Equal(T + "ConfigHasDefaultName");
    }

    [Fact]
    public async Task BadInput_IsReportedAsErrors()
    {
        var none = JsonDocument.Parse(await ImpactTools.AnalyzeChangeImpact()).RootElement;
        none.TryGetProperty("error", out _).Should().BeTrue();

        var ns = JsonDocument.Parse(await ImpactTools.AnalyzeChangeImpact(PipelineFile, 1, 12)).RootElement;
        ns.GetProperty("error").GetString().Should().Contain("type, method");

        var deep = Parse(await ImpactTools.AnalyzeChangeImpact(fullyQualifiedName: Execute, depth: 50));
        deep.GetProperty("depth").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task FixtureSolution_LevelOneMatchesFindCallers()
    {
        EnsureRestored(FixtureSolutionPath, "FixtureLib", "FixtureApp");
        Parse(await SolutionTools.LoadSolution(FixtureSolutionPath)).GetProperty("projectCount").GetInt32().Should().Be(2);

        foreach (var name in new[] { "FixtureLib.Lower.Run", "FixtureLib.Pipeline.Execute", "FixtureLib.Pipeline.Core" })
        {
            var callers = Parse(await HierarchyTools.FindCallers(fullyQualifiedName: name, depth: 1))
                .GetProperty("roots").EnumerateArray()
                .Select(r => r.GetProperty("symbol"))
                .Select(s => $"{s.GetProperty("containingType").GetString()}.{s.GetProperty("name").GetString()}")
                .Order(StringComparer.Ordinal)
                .ToList();

            var impact = Parse(await ImpactTools.AnalyzeChangeImpact(fullyQualifiedName: name, depth: 1));
            var levelOne = Dependents(impact).Where(d => d.Distance == 1).Select(d => d.Symbol)
                .Order(StringComparer.Ordinal).ToList();

            callers.Should().NotBeEmpty();
            levelOne.Should().Equal(callers, $"level-1 callers of {name} must agree with find_callers");
        }
    }
}
