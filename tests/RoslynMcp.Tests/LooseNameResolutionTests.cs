using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Build.Locator;
using RoslynMcp.Tools;
using Xunit;

namespace RoslynMcp.Tests;

/// <summary>
/// fullyQualifiedName fallback (RefactorTools.ResolveByFullyQualifiedNameAsync) and the
/// solution-file-as-filePath error (WorkspaceHost.GetDocument), via read-only get_symbol_info.
///
/// ANSWER KEY (FixtureSolution)
///   "Dog.Fetch"            -> FixtureLib.Dog.Fetch(string, int)   (namespace dropped)
///   "Wrong.Ns.Dog"         -> FixtureLib.Dog                      (namespace wrong)
///   "Dog"                  -> FixtureLib.Dog                      (bare type name)
///   "Product"              -> ambiguous: FixtureLib.Catalog.Product, FixtureLib.Catalog.Nested.Product
///   "Dog.NoSuchMember"     -> Symbol not found
///   filePath = the .slnx   -> "filePath must be a C# source file"
/// </summary>
[Collection("workspace")]
public class LooseNameResolutionTests : IAsyncLifetime
{
    private static readonly string SolutionPath = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureSolution.slnx"));

    static LooseNameResolutionTests()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }

    public async Task InitializeAsync()
    {
        EnsureRestored();
        var loaded = JsonDocument.Parse(await SolutionTools.LoadSolution(SolutionPath)).RootElement;
        loaded.GetProperty("projectCount").GetInt32().Should().Be(2);
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

    private static async Task<JsonElement> Info(string? fullyQualifiedName = null, string? filePath = null,
        int? line = null, int? column = null) =>
        JsonDocument.Parse(await SymbolInfoTools.GetSymbolInfo(filePath, line, column, fullyQualifiedName)).RootElement;

    private static string Display(JsonElement result)
    {
        if (result.TryGetProperty("error", out var error))
        {
            throw new Xunit.Sdk.XunitException($"Tool returned error: {error.GetString()}");
        }
        return result.GetProperty("symbol").GetProperty("display").GetString()!;
    }

    [Theory]
    [InlineData("Dog.Fetch", "FixtureLib.Dog.Fetch(string, int)")]
    [InlineData("Wrong.Ns.Dog", "FixtureLib.Dog")]
    [InlineData("Dog", "FixtureLib.Dog")]
    [InlineData("FixtureLib.Dog", "FixtureLib.Dog")]
    public async Task Resolves_partial_or_misqualified_names_when_unambiguous(string name, string expected)
    {
        Display(await Info(name)).Should().Be(expected);
    }

    [Fact]
    public async Task Lists_the_candidates_when_a_partial_name_is_ambiguous()
    {
        var error = (await Info("Product")).GetProperty("error").GetString();

        error.Should().Contain("ambiguous")
            .And.Contain("FixtureLib.Catalog.Product")
            .And.Contain("FixtureLib.Catalog.Nested.Product");
    }

    [Fact]
    public async Task Still_reports_names_that_match_nothing()
    {
        var error = (await Info("Dog.NoSuchMember")).GetProperty("error").GetString();

        error.Should().StartWith("Symbol not found: 'Dog.NoSuchMember'").And.Contain("symbol_search");
    }

    [Fact]
    public async Task Explains_what_to_pass_when_filePath_is_the_solution_file()
    {
        var error = (await Info(filePath: SolutionPath, line: 0, column: 0)).GetProperty("error").GetString();

        error.Should().StartWith("filePath must be a C# source file (.cs)")
            .And.Contain("FixtureSolution.slnx")
            .And.Contain("fullyQualifiedName");
    }
}
