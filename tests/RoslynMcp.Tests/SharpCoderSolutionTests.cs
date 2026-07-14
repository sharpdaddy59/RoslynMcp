using System.Text.Json;
using FluentAssertions;
using Microsoft.Build.Locator;
using RoslynMcp.Tools;
using Xunit;

namespace RoslynMcp.Tests;

/// <summary>
/// Integration tests that exercise the MCP tool layer against a real solution
/// (SharpCoder). Every assertion is a known-true fact about that codebase, so a
/// pass means the tools return correct semantic answers, not just plausible JSON.
/// Tests chain tools the way a real agent would: search for a symbol, then feed
/// its declaration location into the position-based tools.
/// </summary>
[Collection("workspace")]  // WorkspaceHost is a process-wide singleton: classes that load solutions must not run in parallel
public class SharpCoderSolutionTests : IAsyncLifetime
{
    private static readonly string SolutionPath =
        Path.Combine(FindRepoRoot(), "..", "SharpCoder", "SharpCoder.sln");

    static SharpCoderSolutionTests()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }

    public async Task InitializeAsync()
    {
        // On CI there is no sibling SharpCoder checkout - skip (visibly) rather than fail;
        // FixtureSolutionTests provides the CI-safe coverage
        Skip.IfNot(File.Exists(Path.GetFullPath(SolutionPath)),
            "SharpCoder solution not found (sibling checkout required - runs locally only)");

        var result = Parse(await SolutionTools.LoadSolution(Path.GetFullPath(SolutionPath)));
        result.GetProperty("projectCount").GetInt32().Should().BeGreaterThan(3,
            "SharpCoder has Core, Mcp, UI, and test projects");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "RoslynMcp.slnx")) &&
               !File.Exists(Path.Combine(dir, "RoslynMcp.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("Repo root not found");
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

    private async Task<(string File, int Line, int Column)> FindDeclarationAsync(string name, string? kind = null)
    {
        var result = Parse(await SearchTools.SymbolSearch(name, kind));
        var match = result.GetProperty("matches").EnumerateArray()
            .First(m => m.GetProperty("symbol").GetProperty("name").GetString() == name);
        var loc = match.GetProperty("locations").EnumerateArray().First();
        return (loc.GetProperty("file").GetString()!,
                loc.GetProperty("line").GetInt32(),
                loc.GetProperty("column").GetInt32());
    }

    // NOTE: these tests originally asserted on ClaudeCliRunner, which SharpCoder
    // deleted on 2026-07-12 when it moved to the ClaudeCode.Net package - they
    // failed locally for two days while CI (which skips them) stayed green.
    // Retargeted to AgentOrchestrator, and where possible the assertions are now
    // structural facts rather than exact counts, so routine SharpCoder refactors
    // don't silently break this suite again.

    [SkippableFact]
    public async Task SymbolSearch_FindsAgentOrchestrator_InTheRightFile()
    {
        var (file, line, _) = await FindDeclarationAsync("AgentOrchestrator", "Class");

        file.Should().EndWith(Path.Combine("Services", "AgentOrchestrator.cs"));
        line.Should().BeGreaterThan(1);
    }

    [SkippableFact]
    public async Task FindReferences_AgentOrchestrator_FindsTheCompositionRoots()
    {
        var (file, line, column) = await FindDeclarationAsync("AgentOrchestrator", "Class");

        var result = Parse(await NavigationTools.FindReferences(file, line, column));

        var files = result.GetProperty("references").EnumerateArray()
            .Select(r => Path.GetFileName(r.GetProperty("file").GetString()!))
            .Distinct()
            .ToList();

        // Known-true: the class is instantiated in the UI's DI wiring and the
        // headless host (interface consumers reference IAgentOrchestrator instead)
        files.Should().Contain("App.axaml.cs");
        files.Should().Contain("Program.cs");
    }

    [SkippableFact]
    public async Task FindImplementations_IAgentOrchestrator_FindsAgentOrchestrator()
    {
        var (file, line, column) = await FindDeclarationAsync("IAgentOrchestrator", "Interface");

        var result = Parse(await NavigationTools.FindImplementations(file, line, column));

        result.GetProperty("implementations").EnumerateArray()
            .Select(i => i.GetProperty("symbol").GetProperty("name").GetString())
            .Should().Contain("AgentOrchestrator");
    }

    [SkippableFact]
    public async Task GoToDefinition_FromUsageSite_ResolvesToDeclaration()
    {
        // Find a *usage* of FeatureStatus in FeatureService, then ask for its definition
        var (statusFile, statusLine, statusCol) = await FindDeclarationAsync("FeatureStatus", "Enum");
        var refs = Parse(await NavigationTools.FindReferences(statusFile, statusLine, statusCol));
        var usage = refs.GetProperty("references").EnumerateArray()
            .First(r => r.GetProperty("file").GetString()!.EndsWith("FeatureService.cs"));

        var result = Parse(await NavigationTools.GoToDefinition(
            usage.GetProperty("file").GetString()!,
            usage.GetProperty("line").GetInt32(),
            usage.GetProperty("column").GetInt32()));

        result.GetProperty("definitions").EnumerateArray().First()
            .GetProperty("file").GetString().Should().EndWith("Feature.cs");
    }

    [SkippableFact]
    public async Task GetDiagnostics_SharpCoderCore_HasNoErrors()
    {
        var result = Parse(await DiagnosticsTools.GetDiagnostics("SharpCoder.Core", "Error"));

        var counts = result.GetProperty("counts");
        var hasErrors = counts.TryGetProperty("Error", out var errorCount) && errorCount.GetInt32() > 0;
        hasErrors.Should().BeFalse("SharpCoder.Core compiles cleanly");
    }

    [SkippableFact]
    public async Task DocumentOutline_AgentOrchestrator_ListsThePublicMethods()
    {
        var (file, _, _) = await FindDeclarationAsync("AgentOrchestrator", "Class");

        var result = Parse(await SearchTools.DocumentOutline(file));

        var members = result.GetProperty("types").EnumerateArray().First()
            .GetProperty("members").EnumerateArray()
            .Select(m => m.GetProperty("name").GetString())
            .ToList();

        members.Should().Contain(m => m!.StartsWith("StartSessionAsync("));
        members.Should().Contain(m => m!.StartsWith("StopSessionAsync("));
    }

    [SkippableFact]
    public async Task RenameSymbolPreview_DoesNotModifyAnyFile()
    {
        var (file, line, column) = await FindDeclarationAsync("SessionChainService", "Class");
        var contentBefore = await File.ReadAllTextAsync(file);

        var result = Parse(await RefactorTools.RenameSymbolPreview(file, line, column, "SessionChainManager"));

        result.GetProperty("changedFileCount").GetInt32().Should().BeGreaterThan(1,
            "the class is used in MainWindowViewModel and App wiring");
        (await File.ReadAllTextAsync(file)).Should().Be(contentBefore, "preview must not write");
    }

    [SkippableFact]
    public async Task FindReferences_OnBlankLine_ReturnsStructuredError()
    {
        // Out-of-range columns are deliberately clamped (agents give sloppy coordinates),
        // so the true no-symbol case is a blank line
        var (file, _, _) = await FindDeclarationAsync("AgentOrchestrator", "Class");
        var blankLine = (await File.ReadAllLinesAsync(file))
            .Select((text, i) => (text, line: i + 1))
            .First(l => string.IsNullOrWhiteSpace(l.text)).line;

        var raw = await NavigationTools.FindReferences(file, blankLine, 1);

        // Errors come back as JSON the agent can read, not protocol faults
        JsonDocument.Parse(raw).RootElement.TryGetProperty("error", out _).Should().BeTrue();
    }
}
