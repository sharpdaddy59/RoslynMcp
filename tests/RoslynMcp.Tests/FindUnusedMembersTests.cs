using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Build.Locator;
using RoslynMcp.Tools;
using Xunit;

namespace RoslynMcp.Tests;

/// <summary>
/// find_unused_members coverage against the in-repo fixture's DeadCode.cs answer key.
/// Read-only tool - no file mutation, no cleanup needed.
/// </summary>
[Collection("workspace")]  // WorkspaceHost is a process-wide singleton: classes that load solutions must not run in parallel
public class FindUnusedMembersTests : IAsyncLifetime
{
    private static readonly string SolutionPath = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureSolution.slnx"));
    private static readonly string FixtureLibDir =
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureLib");

    static FindUnusedMembersTests()
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
        var assetsPath = Path.Combine(FixtureLibDir, "obj", "project.assets.json");
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

    private static IEnumerable<string> Names(JsonElement tier) =>
        tier.EnumerateArray().Select(e => e.GetProperty("symbol").GetProperty("name").GetString()!);

    [Fact]
    public async Task FindUnusedMembers_DeadCodeFixture_MatchesTheExactAnswerKey()
    {
        var result = Parse(await AnalysisTools.FindUnusedMembers("FixtureLib"));

        var high = Names(result.GetProperty("highConfidence")).ToList();
        var low = Names(result.GetProperty("lowConfidence")).ToList();
        var everyReported = high.Concat(low).ToList();

        // (a) genuinely unused private method - HIGH
        high.Should().Contain("UnusedHelper");

        // (e) unused private field - HIGH
        high.Should().Contain("_unusedField");

        // (b) unused-looking public method on a public class - LOW, not HIGH
        low.Should().Contain("LooksUnusedButPublic");
        high.Should().NotContain("LooksUnusedButPublic");

        // (c) private method reachable only through an implemented interface - never reported
        everyReported.Should().NotContain("Fire");

        // (d) attributed member - never reported regardless of reference count
        everyReported.Should().NotContain("Deprecated");
    }

    [Fact]
    public async Task FindUnusedMembers_AlwaysEndsWithTheLimitationsNote()
    {
        var result = Parse(await AnalysisTools.FindUnusedMembers("FixtureLib"));

        var limitations = result.GetProperty("limitations").GetString()!;
        limitations.Should().Contain("reflection");
        limitations.Should().Contain("dependency-injection");
        limitations.Should().Contain("serialization");
        limitations.Should().Contain("external consumers");
    }
}
