using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Build.Locator;
using RoslynMcp.Tools;
using Xunit;

namespace RoslynMcp.Tests;

/// <summary>
/// Self-contained integration tests against the in-repo fixture solution
/// (fixtures/FixtureSolution). Unlike SharpCoderSolutionTests, these run anywhere -
/// including CI - and assert against the fixture's planted answer key (see the
/// comments in the fixture sources).
/// </summary>
[Collection("workspace")]  // WorkspaceHost is a process-wide singleton: classes that load solutions must not run in parallel
public class FixtureSolutionTests : IAsyncLifetime
{
    private static readonly string SolutionPath = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureSolution.slnx"));

    static FixtureSolutionTests()
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

    /// <summary>
    /// MSBuildWorkspace needs NuGet assets to resolve references; a fresh clone (CI)
    /// hasn't restored the fixture. One-time, idempotent.
    /// </summary>
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

    [Fact]
    public async Task FindImplementations_IGreeter_FindsExactlyTheTwoPlantedImplementations()
    {
        var (file, line, column) = await FindDeclarationAsync("IGreeter", "Interface");

        var result = Parse(await NavigationTools.FindImplementations(file, line, column));

        var names = result.GetProperty("implementations").EnumerateArray()
            .Select(i => i.GetProperty("symbol").GetProperty("name").GetString())
            .ToList();
        names.Should().BeEquivalentTo(["Greeter", "ShoutingGreeter"]);
    }

    [Fact]
    public async Task FindReferences_Greeter_FindsThePlantedUsagesInProgram()
    {
        var (file, line, column) = await FindDeclarationAsync("Greeter", "Class");

        var result = Parse(await NavigationTools.FindReferences(file, line, column));

        var programRefs = result.GetProperty("references").EnumerateArray()
            .Where(r => r.GetProperty("file").GetString()!.EndsWith("Program.cs"))
            .ToList();
        // Answer key: variable type + constructor = exactly 2
        programRefs.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetDiagnostics_FindsThePlantedWarning_AtItsExactLocation()
    {
        var result = Parse(await DiagnosticsTools.GetDiagnostics("FixtureLib", "Warning"));

        var planted = result.GetProperty("diagnostics").EnumerateArray()
            .Single(d => d.GetProperty("id").GetString() == "CS0219");
        planted.GetProperty("location").GetProperty("file").GetString().Should().EndWith("Legacy.cs");
        planted.GetProperty("location").GetProperty("line").GetInt32().Should().Be(9);
    }

    [Fact]
    public async Task GoToDefinition_FromProgramUsage_LandsInGreeterCs()
    {
        var (file, line, column) = await FindDeclarationAsync("Greeter", "Class");
        var refs = Parse(await NavigationTools.FindReferences(file, line, column));
        var usage = refs.GetProperty("references").EnumerateArray()
            .First(r => r.GetProperty("file").GetString()!.EndsWith("Program.cs"));

        var result = Parse(await NavigationTools.GoToDefinition(
            usage.GetProperty("file").GetString()!,
            usage.GetProperty("line").GetInt32(),
            usage.GetProperty("column").GetInt32()));

        result.GetProperty("definitions").EnumerateArray().First()
            .GetProperty("file").GetString().Should().EndWith("Greeter.cs");
    }

    [Fact]
    public async Task DocumentOutline_Greeter_ListsTheGreetMethod()
    {
        var (file, _, _) = await FindDeclarationAsync("Greeter", "Class");

        var result = Parse(await SearchTools.DocumentOutline(file));

        result.GetProperty("types").EnumerateArray().First()
            .GetProperty("members").EnumerateArray()
            .Select(m => m.GetProperty("name").GetString())
            .Should().Contain(m => m!.StartsWith("Greet("));
    }

    [Fact]
    public async Task RenameSymbolPreview_IGreeter_TouchesAllFourFiles_WritesNone()
    {
        var (file, line, column) = await FindDeclarationAsync("IGreeter", "Interface");
        var before = await File.ReadAllTextAsync(file);

        var result = Parse(await RefactorTools.RenameSymbolPreview(file, line, column, "INamer"));

        // Declaration + two implementations + Program.cs
        result.GetProperty("changedFileCount").GetInt32().Should().Be(4);
        (await File.ReadAllTextAsync(file)).Should().Be(before);
    }
}

internal static class TestPaths
{
    public static string RepoRoot { get; } = Find();

    private static string Find()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "RoslynMcp.slnx")) &&
               !File.Exists(Path.Combine(dir, "RoslynMcp.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("Repo root not found");
    }
}
