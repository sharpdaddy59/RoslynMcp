using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Build.Locator;
using RoslynMcp.Tools;
using Xunit;

namespace RoslynMcp.Tests;

/// <summary>
/// find_callers coverage against the fixture's Pipeline.cs (FixtureLib) and
/// PipelineUsages.cs (FixtureApp) answer key. Read-only tool - no cleanup needed.
/// </summary>
[Collection("workspace")]  // WorkspaceHost is a process-wide singleton: classes that load solutions must not run in parallel
public class FindCallersTests : IAsyncLifetime
{
    private static readonly string SolutionPath = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureSolution.slnx"));
    private static readonly string PipelineFile = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureLib", "Pipeline.cs"));
    private static readonly string UsagesFile = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureApp", "PipelineUsages.cs"));

    static FindCallersTests()
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

    private static string Name(JsonElement node) => node.GetProperty("symbol").GetProperty("name").GetString()!;

    private static List<JsonElement> Callers(JsonElement node) =>
        node.GetProperty(node.TryGetProperty("roots", out _) ? "roots" : "callers").EnumerateArray().ToList();

    private static List<(string File, int Line, int Column)> CallSites(JsonElement node) =>
        node.GetProperty("callSites").EnumerateArray()
            .Select(s => (s.GetProperty("file").GetString()!, s.GetProperty("line").GetInt32(), s.GetProperty("column").GetInt32()))
            .ToList();

    private static bool Flag(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.GetBoolean();

    private static string? Via(JsonElement node) =>
        node.TryGetProperty("via", out var via) ? via.GetString() : null;

    [Fact]
    public async Task FindCallers_Core_WalksTheDiamondWithExactCallSites()
    {
        // Position-based lookup: the 'Core' identifier on line 32 of Pipeline.cs.
        var result = Parse(await HierarchyTools.FindCallers(PipelineFile, 32, 19, depth: 3));

        result.GetProperty("target").GetProperty("name").GetString().Should().Be("Core");
        result.GetProperty("truncated").GetBoolean().Should().BeFalse();

        var roots = Callers(result);
        roots.Select(Name).Should().Equal("Left", "Right");
        CallSites(roots[0]).Should().Equal((PipelineFile, 28, 37));
        CallSites(roots[1]).Should().Equal((PipelineFile, 30, 38));

        var viaLeft = Callers(roots[0]).Should().ContainSingle().Subject;
        Name(viaLeft).Should().Be("Diamond");
        CallSites(viaLeft).Should().Equal((PipelineFile, 26, 47));
        Flag(viaLeft, "seenBefore").Should().BeFalse();

        var viaRight = Callers(roots[1]).Should().ContainSingle().Subject;
        Name(viaRight).Should().Be("Diamond");
        CallSites(viaRight).Should().Equal((PipelineFile, 26, 57));
        Flag(viaRight, "seenBefore").Should().BeTrue("Diamond was already shown under Left");

        foreach (var diamond in new[] { viaLeft, viaRight })
        {
            var usage = Callers(diamond).Should().ContainSingle().Subject;
            Name(usage).Should().Be("Exercise");
            usage.GetProperty("symbol").GetProperty("containingType").GetString().Should().Be("FixtureApp.PipelineUsages");
            CallSites(usage).Should().Equal((UsagesFile, 11, 32));
            Callers(usage).Should().BeEmpty("depth 3 stops here (and nothing calls Exercise anyway)");
        }

        result.GetProperty("nodeCount").GetInt32().Should().Be(6);
    }

    [Fact]
    public async Task FindCallers_RecursiveDepth_ShowsACycleLeafAndTerminates()
    {
        var result = Parse(await HierarchyTools.FindCallers(fullyQualifiedName: "FixtureLib.Pipeline.Depth", depth: 10));

        var roots = Callers(result);
        roots.Select(Name).Should().Equal("Exercise", "Depth");

        CallSites(roots[0]).Should().Equal((UsagesFile, 12, 30));
        Flag(roots[0], "cycle").Should().BeFalse();

        var self = roots[1];
        Flag(self, "cycle").Should().BeTrue();
        CallSites(self).Should().Equal((PipelineFile, 23, 56));
        Callers(self).Should().BeEmpty("a cycle leaf is never expanded");

        result.GetProperty("nodeCount").GetInt32().Should().Be(2);
        result.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task FindCallers_HardTrimRun_FindsExecuteThroughTheInterfaceOnlyWithDispatch()
    {
        var withDispatch = Parse(await HierarchyTools.FindCallers(fullyQualifiedName: "FixtureLib.HardTrim.Run"));

        var roots = Callers(withDispatch);
        var execute = roots.Should().ContainSingle(
            "the only caller is Execute via IStage.Run; HardTrim's own base.Run() targets Trim.Run non-virtually").Subject;
        Name(execute).Should().Be("Execute");
        Via(execute).Should().Be("IStage.Run(string)");
        execute.GetProperty("viaKind").GetString().Should().Be("interface");
        CallSites(execute).Should().Equal((PipelineFile, 18, 67));

        var twice = Callers(execute).Should().ContainSingle().Subject;
        Name(twice).Should().Be("Twice");
        Via(twice).Should().BeNull();
        CallSites(twice).Should().Equal((PipelineFile, 20, 59), (PipelineFile, 20, 74));

        var usage = Callers(twice).Should().ContainSingle().Subject;
        Name(usage).Should().Be("Exercise");
        CallSites(usage).Should().Equal((UsagesFile, 10, 30));

        var group = withDispatch.GetProperty("dispatchGroup").EnumerateArray()
            .Select(g => g.GetProperty("member").GetString()).ToList();
        group.Should().Contain(["Trim.Run(string)", "IStage.Run(string)"]);

        var direct = Parse(await HierarchyTools.FindCallers(
            fullyQualifiedName: "FixtureLib.HardTrim.Run", includeDispatch: false));
        Callers(direct).Should().BeEmpty("nothing calls HardTrim.Run on a HardTrim-typed receiver");
        direct.GetProperty("nodeCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task FindCallers_LowerRun_HasTheDirectCallerPlusExecuteViaTheInterface()
    {
        var result = Parse(await HierarchyTools.FindCallers(fullyQualifiedName: "FixtureLib.Lower.Run", depth: 3));

        var roots = Callers(result);
        roots.Select(Name).Should().Equal("Exercise", "Execute");

        Via(roots[0]).Should().BeNull("new Lower().Run(\"X\") is a direct call");
        CallSites(roots[0]).Should().Equal((UsagesFile, 13, 33));

        Via(roots[1]).Should().Be("IStage.Run(string)");
        CallSites(roots[1]).Should().Equal((PipelineFile, 18, 67));

        // Execute <- Twice <- Exercise again: a diamond, so the second Exercise is marked.
        var twice = Callers(roots[1]).Should().ContainSingle().Subject;
        Name(twice).Should().Be("Twice");
        var exerciseAgain = Callers(twice).Should().ContainSingle().Subject;
        Name(exerciseAgain).Should().Be("Exercise");
        Flag(exerciseAgain, "seenBefore").Should().BeTrue();

        var directOnly = Parse(await HierarchyTools.FindCallers(
            fullyQualifiedName: "FixtureLib.Lower.Run", includeDispatch: false));
        Callers(directOnly).Select(Name).Should().Equal("Exercise");
    }

    [Fact]
    public async Task FindCallers_MaxNodes_TruncatesBreadthFirst()
    {
        var result = Parse(await HierarchyTools.FindCallers(fullyQualifiedName: "FixtureLib.Pipeline.Core", maxNodes: 2));

        result.GetProperty("truncated").GetBoolean().Should().BeTrue();
        result.GetProperty("nodeCount").GetInt32().Should().Be(2);

        var roots = Callers(result);
        roots.Select(Name).Should().Equal("Left", "Right");
        roots.Should().OnlyContain(r => Callers(r).Count == 0);
    }

    [Fact]
    public async Task FindCallers_ClampsDepthAndReportsBadInputAsErrors()
    {
        var deep = Parse(await HierarchyTools.FindCallers(fullyQualifiedName: "FixtureLib.Pipeline.Core", depth: 50));
        deep.GetProperty("depth").GetInt32().Should().Be(10);

        var shallow = Parse(await HierarchyTools.FindCallers(fullyQualifiedName: "FixtureLib.Pipeline.Core", depth: 0));
        shallow.GetProperty("depth").GetInt32().Should().Be(1);

        var badPosition = JsonDocument.Parse(await HierarchyTools.FindCallers(PipelineFile, 9999, 1)).RootElement;
        badPosition.GetProperty("error").GetString().Should().Contain("out of range");

        var notCallable = JsonDocument.Parse(
            await HierarchyTools.FindCallers(fullyQualifiedName: "FixtureLib.Pipeline")).RootElement;
        notCallable.GetProperty("error").GetString().Should().Contain("method, constructor, property, or event");

        var noArgs = JsonDocument.Parse(await HierarchyTools.FindCallers()).RootElement;
        noArgs.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public async Task FindCallers_TopLevelStatementsAreMarkedAsTheEntryPoint()
    {
        // Existing fixture: Program.cs (top-level statements) calls ProcessHelper.Describe().
        var result = Parse(await HierarchyTools.FindCallers(fullyQualifiedName: "FixtureLib.ProcessHelper.Describe"));

        var entry = Callers(result).Should().ContainSingle().Subject;
        Flag(entry, "isEntryPoint").Should().BeTrue();
        entry.GetProperty("symbol").GetProperty("display").GetString().Should().Be("Program (top-level statements)");
    }
}
