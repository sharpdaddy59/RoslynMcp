using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Build.Locator;
using RoslynMcp.Tools;
using Xunit;

namespace RoslynMcp.Tests;

/// <summary>
/// Adversarial rename coverage for apply_rename against the in-repo fixture solution
/// (see fixtures/FixtureSolution/FixtureLib/Processor.cs). The scenario is deliberately
/// built so a naive text-substitution rename would give the wrong answer: renaming the
/// virtual Process method must follow it through the override and every call site, while
/// leaving a same-named string literal, a comment, an unrelated ProcessHelper type, and a
/// local variable named process untouched. Tests write real fixture files, so each
/// restores the original content in a finally block.
/// </summary>
[Collection("workspace")]  // WorkspaceHost is a process-wide singleton: classes that load solutions must not run in parallel
public class ApplyRenameTests : IAsyncLifetime
{
    private static readonly string SolutionPath = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureSolution.slnx"));
    private static readonly string FixtureLibDir =
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureLib");
    private static readonly string FixtureAppDir =
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureApp");

    static ApplyRenameTests()
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

    /// <summary>MSBuildWorkspace needs NuGet assets; a fresh clone (CI) hasn't restored the fixture.</summary>
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
        return element;
    }

    private static (string File, int Line, int Column) FindPosition(string filePath, string needle)
    {
        var lines = File.ReadAllLines(filePath);
        for (var i = 0; i < lines.Length; i++)
        {
            var idx = lines[i].IndexOf(needle, StringComparison.Ordinal);
            if (idx >= 0) return (filePath, i + 1, idx + 1);
        }
        throw new InvalidOperationException($"'{needle}' not found in {filePath}");
    }

    [Fact]
    public async Task ApplyRename_ProcessOnBaseClass_RenamesOverrideAndCallSites_ButNotStringsCommentsOrUnrelatedSymbols()
    {
        var processorFile = Path.Combine(FixtureLibDir, "Processor.cs");
        var helperFile = Path.Combine(FixtureLibDir, "ProcessHelper.cs");
        var programFile = Path.Combine(FixtureAppDir, "Program.cs");

        var processorBefore = await File.ReadAllTextAsync(processorFile);
        var helperBefore = await File.ReadAllTextAsync(helperFile);
        var programBefore = await File.ReadAllTextAsync(programFile);

        var (file, line, column) = FindPosition(processorFile, "Process(string input) => $\"base:");

        try
        {
            var result = Parse(await RefactorTools.ApplyRename("Execute", file, line, column));

            result.GetProperty("applied").GetBoolean().Should().BeTrue();
            result.GetProperty("changedFileCount").GetInt32().Should().Be(2, "Processor.cs and Program.cs both reference Process");

            var processorAfter = await File.ReadAllTextAsync(processorFile);
            var programAfter = await File.ReadAllTextAsync(programFile);

            // (a) base method, derived override, and every call site through either type renamed
            processorAfter.Should().Contain("public virtual string Execute(string input)");
            processorAfter.Should().Contain("public override string Execute(string input)");
            programAfter.Should().Contain("baseRef.Execute(\"via base\")");
            programAfter.Should().Contain("derivedRef.Execute(\"via derived\")");
            programAfter.Should().Contain("process.Execute(\"via local\")");

            // (b) never touched: unrelated member, comment, string literal, unrelated class, local variable
            processorAfter.Should().Contain("public string Handle(string input)");
            processorAfter.Should().Contain("Renaming the Process method");
            programAfter.Should().Contain("var process = derivedRef;");
            programAfter.Should().Contain("Console.WriteLine(\"Process\");");
            programAfter.Should().Contain("ProcessHelper.Describe()");
            (await File.ReadAllTextAsync(helperFile)).Should().Be(helperBefore, "ProcessHelper must never be touched");

            processorAfter.Should().NotContain("Process(string input)");
            programAfter.Should().NotContain(".Process(");
        }
        finally
        {
            await File.WriteAllTextAsync(processorFile, processorBefore);
            await File.WriteAllTextAsync(programFile, programBefore);
        }
    }

    [Fact]
    public async Task ApplyRename_ToNameCollidingWithExistingMember_AppliesNothingAndReportsTheConflict()
    {
        var processorFile = Path.Combine(FixtureLibDir, "Processor.cs");
        var before = await File.ReadAllTextAsync(processorFile);

        var (file, line, column) = FindPosition(processorFile, "Process(string input) => $\"base:");

        var result = Parse(await RefactorTools.ApplyRename("Handle", file, line, column));

        result.GetProperty("applied").GetBoolean().Should().BeFalse();
        result.GetProperty("conflicts").EnumerateArray()
            .Should().Contain(c => c.GetProperty("id").GetString() == "CS0111");

        (await File.ReadAllTextAsync(processorFile)).Should().Be(before, "a colliding rename must not be written");
    }
}
