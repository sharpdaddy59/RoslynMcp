using System.Diagnostics;
using FluentAssertions;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp;
using Xunit;

namespace RoslynMcp.Tests;

/// <summary>
/// Proves the compile-before-write contract against the in-repo fixture solution:
/// a candidate edit is only ever written to disk if it introduces no NEW compile
/// errors relative to the load-time baseline, and never if the target file was
/// touched on disk after load. Tests that write mutate real fixture files, so each
/// restores the original content in a finally block.
/// </summary>
[Collection("workspace")]  // WorkspaceHost is a process-wide singleton: classes that load solutions must not run in parallel
public class MutationApplierTests : IAsyncLifetime
{
    private static readonly string SolutionPath = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureSolution.slnx"));
    private static readonly string FixtureLibDir =
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureLib");

    static MutationApplierTests()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }

    public async Task InitializeAsync()
    {
        EnsureRestored();
        await WorkspaceHost.LoadAsync(SolutionPath);
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

    private static async Task<Microsoft.CodeAnalysis.Solution> ReplaceFileTextAsync(string filePath, string newContent)
    {
        var document = WorkspaceHost.GetDocument(filePath);
        return document.WithText(SourceText.From(newContent)).Project.Solution;
    }

    [Fact]
    public async Task TryApplyAsync_EditIntroducingACompileError_IsNotWrittenAndNamesTheError()
    {
        var filePath = Path.Combine(FixtureLibDir, "Greeter.cs");
        var before = await File.ReadAllTextAsync(filePath);
        var broken = before.Replace(
            "return $\"Hello, {name}!\";",
            "return 42;"); // string-returning method - CS0029, a brand-new error

        var candidate = await ReplaceFileTextAsync(filePath, broken);

        var result = await MutationApplier.TryApplyAsync(candidate);

        result.Applied.Should().BeFalse();
        result.NewErrors.Should().Contain(e => e.Id == "CS0029");
        (await File.ReadAllTextAsync(filePath)).Should().Be(before, "a candidate with new errors must not be written");
    }

    [Fact]
    public async Task TryApplyAsync_ValidEdit_IsWrittenAndReflectedOnDisk()
    {
        var filePath = Path.Combine(FixtureLibDir, "Legacy.cs");
        var before = await File.ReadAllTextAsync(filePath);
        var edited = before.Replace("return 7;", "return 8;");

        try
        {
            var candidate = await ReplaceFileTextAsync(filePath, edited);

            var result = await MutationApplier.TryApplyAsync(candidate);

            result.Applied.Should().BeTrue();
            result.ChangedFiles.Should().Contain(filePath);
            (await File.ReadAllTextAsync(filePath)).Should().Be(edited);
        }
        finally
        {
            await File.WriteAllTextAsync(filePath, before);
        }
    }

    [Fact]
    public async Task TryApplyAsync_PreExistingBaselineError_DoesNotBlockAnUnrelatedValidEdit()
    {
        // Broken.cs (in this same project) plants a pre-existing CS0246 - confirm it's
        // really there via the diagnostics tool before relying on it below.
        var project = WorkspaceHost.Current.Projects.Single(p => p.Name == "FixtureLib");
        var compilation = await project.GetCompilationAsync();
        compilation!.GetDiagnostics().Should().Contain(d => d.Id == "CS0246");

        var filePath = Path.Combine(FixtureLibDir, "Greeter.cs");
        var before = await File.ReadAllTextAsync(filePath);
        var edited = "// unrelated valid comment\n" + before;

        try
        {
            var candidate = await ReplaceFileTextAsync(filePath, edited);

            var result = await MutationApplier.TryApplyAsync(candidate);

            result.Applied.Should().BeTrue("the only error introduced pre-dates load and is unrelated to this edit");
            (await File.ReadAllTextAsync(filePath)).Should().Be(edited);
        }
        finally
        {
            await File.WriteAllTextAsync(filePath, before);
        }
    }

    [Fact]
    public async Task TryApplyAsync_FileChangedOnDiskSinceLoad_RefusesWithStaleFile()
    {
        var filePath = Path.Combine(FixtureLibDir, "ShoutingGreeter.cs");
        var before = await File.ReadAllTextAsync(filePath);

        try
        {
            // Simulate an external edit landing after the workspace was loaded.
            await File.WriteAllTextAsync(filePath, before + "\n// touched externally\n");
            File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(5));

            var candidate = await ReplaceFileTextAsync(filePath, before.Replace("HELLO", "HOWDY"));

            var result = await MutationApplier.TryApplyAsync(candidate);

            result.Applied.Should().BeFalse();
            result.StaleFile.Should().Be(filePath);
        }
        finally
        {
            await File.WriteAllTextAsync(filePath, before);
        }
    }
}
