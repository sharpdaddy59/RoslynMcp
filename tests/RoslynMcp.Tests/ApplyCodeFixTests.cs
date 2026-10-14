using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Build.Locator;
using RoslynMcp.Tools;
using Xunit;

namespace RoslynMcp.Tests;

/// <summary>
/// v2.2 item C: list_code_fixes + apply_code_fix against the FixtureSolution, proving two
/// Roslyn fixes end to end: remove unnecessary usings (CS8019, served by the IDE0005 fixer) and
/// add the missing using for an unresolved type (CS0246, add-import). Fixture files:
/// FixtureLib/Fixable.cs (three unnecessary usings + one used), FixtureLib/NeedsImport.cs
/// (planted CS0246 for Widget) and FixtureLib/Imports/Widget.cs (its only candidate namespace).
///
/// Answer key for CS8019 across the whole fixture solution (pre-existing files included):
/// Box.cs line 1 `using System;` (duplicates the implicit global using), Fixable.cs lines 1, 2
/// and 4, Shapes.cs line 1 `using System;`. Nothing in FixtureApp. Baseline compile errors in
/// FixtureLib: CS0246 in Broken.cs (UndefinedType, no candidate namespace, never fixed) and
/// CS0246 in NeedsImport.cs (Widget). Tests that write restore every touched file in finally.
/// </summary>
[Collection("workspace")]  // WorkspaceHost is a process-wide singleton: classes that load solutions must not run in parallel
public class ApplyCodeFixTests : IAsyncLifetime
{
    private static readonly string FixtureRoot =
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution");
    private static readonly string SolutionPath = Path.GetFullPath(
        Path.Combine(FixtureRoot, "FixtureSolution.slnx"));
    private static readonly string FixtureLibDir = Path.Combine(FixtureRoot, "FixtureLib");

    private static readonly string FixableFile = Path.Combine(FixtureLibDir, "Fixable.cs");
    private static readonly string NeedsImportFile = Path.Combine(FixtureLibDir, "NeedsImport.cs");
    private static readonly string BoxFile = Path.Combine(FixtureLibDir, "Box.cs");
    private static readonly string ShapesFile = Path.Combine(FixtureLibDir, "Shapes.cs");

    private const string FixableAfterFix = """
        using System.Diagnostics;

        namespace FixtureLib;

        // v2.2 apply_code_fix fixture: three unnecessary usings (CS8019: System.Text,
        // System.Collections.Concurrent, System.Net) and one used one (System.Diagnostics).
        public class Fixable
        {
            public long Time(Action action)
            {
                var stopwatch = Stopwatch.StartNew();
                action();
                return stopwatch.ElapsedMilliseconds;
            }
        }

        """;

    private const string NeedsImportAfterFix = """
        using System.Diagnostics;
        using System.Text;
        using FixtureLib.Imports;

        namespace FixtureLib;

        // v2.2 apply_code_fix fixture: deliberately planted CS0246 - Widget lives in FixtureLib.Imports
        // and the using is missing. apply_code_fix CS0246 must add exactly `using FixtureLib.Imports;`.
        public class NeedsImport
        {
            public string Describe(Widget widget)
            {
                var stopwatch = Stopwatch.StartNew();
                var builder = new StringBuilder(widget.Name);
                builder.Append(stopwatch.ElapsedTicks >= 0 ? "!" : "?");
                return builder.ToString();
            }
        }

        """;

    static ApplyCodeFixTests()
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

    /// <summary>SHA-256 of every file under the fixture solution, bin/ and obj/ excluded (new or deleted files show up too).</summary>
    private static Dictionary<string, string> HashFixtures()
    {
        static bool IsBuildOutput(string path)
        {
            var relative = Path.GetRelativePath(FixtureRoot, path);
            var segments = relative.Split(Path.DirectorySeparatorChar);
            return segments.Any(s => s is "bin" or "obj");
        }

        return Directory.EnumerateFiles(FixtureRoot, "*", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(p))
            .ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
    }

    /// <summary>File names (not paths) whose bytes differ between two HashFixtures snapshots, sorted.</summary>
    private static List<string> ChangedFiles(Dictionary<string, string> before, Dictionary<string, string> after) =>
        before.Keys.Union(after.Keys)
            .Where(k => !before.TryGetValue(k, out var b) || !after.TryGetValue(k, out var a) || a != b)
            .Select(k => Path.GetFileName(k))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private static string Raw(JsonElement result, string property) =>
        result.TryGetProperty(property, out var value) ? value.GetRawText() : "<absent>";

    private static List<(int Line, string OldText, string NewText)> Edits(JsonElement result, string file) =>
        result.GetProperty("changes").EnumerateArray()
            .Single(c => c.GetProperty("file").GetString() == file)
            .GetProperty("edits").EnumerateArray()
            .Select(e => (e.GetProperty("line").GetInt32(), e.GetProperty("oldText").GetString()!, e.GetProperty("newText").GetString()!))
            .ToList();

    private static async Task<List<JsonElement>> ErrorsAsync(string projectName)
    {
        var diagnostics = Parse(await DiagnosticsTools.GetDiagnostics(projectName, "Error"));
        return diagnostics.GetProperty("diagnostics").EnumerateArray()
            .Where(d => d.GetProperty("severity").GetString() == "Error")
            .ToList();
    }

    private static string FileOf(JsonElement diagnostic) =>
        Path.GetFileName(diagnostic.GetProperty("location").GetProperty("file").GetString()!);

    // ---------------------------------------------------------------- list_code_fixes

    [Fact]
    public async Task ListCodeFixes_FileScope_ReportsThreeCS8019InFixable_AndMarksThemFixable()
    {
        var result = Parse(await CodeFixTools.ListCodeFixes(scope: "file", filePath: FixableFile));

        result.GetProperty("scope").GetString().Should().Be("file");
        var groups = result.GetProperty("diagnostics").EnumerateArray().ToList();
        groups.Select(g => g.GetProperty("id").GetString()).Should().Equal("CS8019");

        var cs8019 = groups.Single();
        cs8019.GetProperty("count").GetInt32().Should().Be(3);
        cs8019.GetProperty("severity").GetString().Should().Be("Hidden");
        cs8019.GetProperty("fixable").GetBoolean().Should().BeTrue();
        cs8019.GetProperty("providers").EnumerateArray().Select(p => p.GetString()).Should().Equal("RemoveUnnecessaryImports");
        cs8019.GetProperty("fixAllSupported").GetBoolean().Should().BeTrue();
        cs8019.GetProperty("locations").EnumerateArray()
            .Select(l => (l.GetProperty("line").GetInt32(), l.GetProperty("snippet").GetString()))
            .Should().Equal(
                (1, "using System.Text;"),
                (2, "using System.Collections.Concurrent;"),
                (4, "using System.Net;"));
    }

    [Fact]
    public async Task ListCodeFixes_SolutionScope_MatchesTheAnswerKey_AndFiltersById()
    {
        var all = Parse(await CodeFixTools.ListCodeFixes());
        var groups = all.GetProperty("diagnostics").EnumerateArray().ToList();
        groups.Select(g => g.GetProperty("id").GetString()).Should().BeInAscendingOrder(StringComparer.Ordinal);

        var cs8019 = groups.Single(g => g.GetProperty("id").GetString() == "CS8019");
        cs8019.GetProperty("count").GetInt32().Should().Be(5);
        cs8019.GetProperty("locations").EnumerateArray()
            .Select(l => $"{Path.GetFileName(l.GetProperty("file").GetString())}:{l.GetProperty("line").GetInt32()}")
            .Should().Equal("Box.cs:1", "Fixable.cs:1", "Fixable.cs:2", "Fixable.cs:4", "Shapes.cs:1");

        var cs0246 = groups.Single(g => g.GetProperty("id").GetString() == "CS0246");
        cs0246.GetProperty("count").GetInt32().Should().Be(2);
        cs0246.GetProperty("fixable").GetBoolean().Should().BeTrue();
        cs0246.GetProperty("providers").EnumerateArray().Select(p => p.GetString()).Should().Contain("AddImport");
        cs0246.GetProperty("locations").EnumerateArray()
            .Select(l => Path.GetFileName(l.GetProperty("file").GetString()))
            .Should().Equal("Broken.cs", "NeedsImport.cs");

        var hiddenExcluded = Parse(await CodeFixTools.ListCodeFixes(includeHidden: false));
        hiddenExcluded.GetProperty("diagnostics").EnumerateArray()
            .Should().NotContain(g => g.GetProperty("severity").GetString() == "Hidden");

        var filtered = Parse(await CodeFixTools.ListCodeFixes(diagnosticId: "IDE0005", scope: "project", projectName: "FixtureApp"));
        filtered.GetProperty("totalDiagnostics").GetInt32().Should().Be(0, "FixtureApp has no unnecessary usings");
    }

    // ---------------------------------------------------------------- apply_code_fix: CS8019

    [Fact]
    public async Task ApplyCodeFix_CS8019_FileScope_DryRunWritesNothing_ThenRealCallRemovesExactlyTheThreeUsings()
    {
        var fixableBefore = await File.ReadAllTextAsync(FixableFile);
        var hashesBefore = HashFixtures();
        try
        {
            var dry = Parse(await CodeFixTools.ApplyCodeFix("CS8019", scope: "file", filePath: FixableFile, dryRun: true));

            ChangedFiles(hashesBefore, HashFixtures()).Should().BeEmpty("a dry run never writes");
            dry.GetProperty("applied").GetBoolean().Should().BeFalse();
            dry.GetProperty("dryRun").GetBoolean().Should().BeTrue();
            dry.GetProperty("wouldApply").GetBoolean().Should().BeTrue(dry.GetRawText());
            dry.GetProperty("diagnosticsFound").GetInt32().Should().Be(3);
            dry.GetProperty("diagnosticsFixed").GetInt32().Should().Be(3);
            dry.GetProperty("provider").GetString().Should().Be("RemoveUnnecessaryImports");
            dry.GetProperty("fixTitle").GetString().Should().Be("Remove unnecessary usings");
            dry.GetProperty("changedFileCount").GetInt32().Should().Be(1);
            Edits(dry, FixableFile).Should().Equal(
                (1, "using System.Text;\nusing System.Collections.Concurrent;\n", ""),
                (4, "using System.Net;\n", ""));

            var real = Parse(await CodeFixTools.ApplyCodeFix("CS8019", scope: "file", filePath: FixableFile));

            real.GetProperty("applied").GetBoolean().Should().BeTrue(real.GetRawText());
            real.TryGetProperty("dryRun", out _).Should().BeFalse();
            real.TryGetProperty("wouldApply", out _).Should().BeFalse();
            Raw(real, "changes").Should().Be(Raw(dry, "changes"), "the dry run describes exactly what the real call writes");
            (await File.ReadAllTextAsync(FixableFile)).Should().Be(FixableAfterFix);
            ChangedFiles(hashesBefore, HashFixtures()).Should().Equal("Fixable.cs");

            var after = Parse(await CodeFixTools.ListCodeFixes(diagnosticId: "CS8019", scope: "file", filePath: FixableFile));
            after.GetProperty("totalDiagnostics").GetInt32().Should().Be(0);
        }
        finally
        {
            await File.WriteAllTextAsync(FixableFile, fixableBefore);
        }
    }

    [Fact]
    public async Task ApplyCodeFix_CS8019_ProjectScope_TouchesOnlyFilesWithUnnecessaryUsings()
    {
        var fixableBefore = await File.ReadAllTextAsync(FixableFile);
        var boxBefore = await File.ReadAllTextAsync(BoxFile);
        var shapesBefore = await File.ReadAllTextAsync(ShapesFile);
        var hashesBefore = HashFixtures();
        try
        {
            var result = Parse(await CodeFixTools.ApplyCodeFix("CS8019", scope: "project", projectName: "FixtureLib"));

            result.GetProperty("applied").GetBoolean().Should().BeTrue(result.GetRawText());
            result.GetProperty("diagnosticsFound").GetInt32().Should().Be(5);
            result.GetProperty("diagnosticsFixed").GetInt32().Should().Be(5);
            result.GetProperty("changedFileCount").GetInt32().Should().Be(3);

            ChangedFiles(hashesBefore, HashFixtures()).Should().Equal("Box.cs", "Fixable.cs", "Shapes.cs");
            (await File.ReadAllTextAsync(FixableFile)).Should().Be(FixableAfterFix);
            boxBefore.Should().StartWith("using System;\n\n");
            (await File.ReadAllTextAsync(BoxFile)).Should().Be(boxBefore["using System;\n\n".Length..]);
            shapesBefore.Should().StartWith("using System;\nusing Text = System.String;\n");
            (await File.ReadAllTextAsync(ShapesFile)).Should().Be(shapesBefore["using System;\n".Length..],
                "the used alias directive stays");
        }
        finally
        {
            await File.WriteAllTextAsync(FixableFile, fixableBefore);
            await File.WriteAllTextAsync(BoxFile, boxBefore);
            await File.WriteAllTextAsync(ShapesFile, shapesBefore);
        }
    }

    [Fact]
    public async Task ApplyCodeFix_CS8019_SolutionScopeDryRun_ViaIDE0005Alias_ListsTheSameThreeFilesAndWritesNothing()
    {
        var hashesBefore = HashFixtures();

        var dry = Parse(await CodeFixTools.ApplyCodeFix("IDE0005", scope: "solution", dryRun: true));

        ChangedFiles(hashesBefore, HashFixtures()).Should().BeEmpty();
        dry.GetProperty("diagnosticId").GetString().Should().Be("CS8019");
        dry.GetProperty("wouldApply").GetBoolean().Should().BeTrue(dry.GetRawText());
        dry.GetProperty("diagnosticsFound").GetInt32().Should().Be(5);
        dry.GetProperty("changes").EnumerateArray()
            .Select(c => Path.GetFileName(c.GetProperty("file").GetString()))
            .Should().Equal("Box.cs", "Fixable.cs", "Shapes.cs");
        Edits(dry, BoxFile).Should().Equal((1, "using System;\n\n", ""));
        Edits(dry, ShapesFile).Should().Equal((1, "using System;\n", ""));
    }

    // ---------------------------------------------------------------- apply_code_fix: CS0246

    [Fact]
    public async Task ApplyCodeFix_CS0246_AddsExactlyTheMissingUsing_InSortedPosition_AndTheFileCompiles()
    {
        var needsImportBefore = await File.ReadAllTextAsync(NeedsImportFile);
        var hashesBefore = HashFixtures();
        try
        {
            var errorsBefore = await ErrorsAsync("FixtureLib");
            errorsBefore.Select(FileOf).Should().BeEquivalentTo(["Broken.cs", "NeedsImport.cs"]);

            var result = Parse(await CodeFixTools.ApplyCodeFix("CS0246", scope: "file", filePath: NeedsImportFile));

            result.GetProperty("applied").GetBoolean().Should().BeTrue(result.GetRawText());
            result.GetProperty("provider").GetString().Should().Be("AddImport");
            result.GetProperty("fixTitle").GetString().Should().Be("using FixtureLib.Imports;");
            result.GetProperty("diagnosticsFound").GetInt32().Should().Be(1);
            result.GetProperty("diagnosticsFixed").GetInt32().Should().Be(1);
            Edits(result, NeedsImportFile).Should().Equal((3, "", "using FixtureLib.Imports;\n"));

            (await File.ReadAllTextAsync(NeedsImportFile)).Should().Be(NeedsImportAfterFix);
            ChangedFiles(hashesBefore, HashFixtures()).Should().Equal("NeedsImport.cs");

            var errorsAfter = await ErrorsAsync("FixtureLib");
            errorsAfter.Should().HaveCount(errorsBefore.Count - 1, "the gate accepted the fix and the project lost exactly that error");
            errorsAfter.Select(FileOf).Should().Equal("Broken.cs");
        }
        finally
        {
            await File.WriteAllTextAsync(NeedsImportFile, needsImportBefore);
        }
    }

    [Fact]
    public async Task ApplyCodeFix_CS0246_ProjectScopeDryRun_FixesNeedsImport_AndLeavesTheUnresolvableBrokenAlone()
    {
        var hashesBefore = HashFixtures();

        var dry = Parse(await CodeFixTools.ApplyCodeFix("CS0246", scope: "project", projectName: "FixtureLib", dryRun: true));

        ChangedFiles(hashesBefore, HashFixtures()).Should().BeEmpty();
        dry.GetProperty("wouldApply").GetBoolean().Should().BeTrue(dry.GetRawText());
        dry.GetProperty("provider").GetString().Should().Be("AddImport", "one provider for the whole call, never Generate Type for Broken.cs");
        dry.GetProperty("diagnosticsFound").GetInt32().Should().Be(2);
        dry.GetProperty("diagnosticsFixed").GetInt32().Should().Be(1);
        dry.GetProperty("changes").EnumerateArray()
            .Select(c => c.GetProperty("file").GetString())
            .Should().Equal(NeedsImportFile);
        dry.GetProperty("message").GetString().Should().Contain("1 of 2 targeted diagnostic(s) remain");
    }

    // ---------------------------------------------------------------- not fixable / nothing to fix

    [Fact]
    public async Task ApplyCodeFix_UnknownDiagnosticId_ReturnsNotFixable_AndWritesNothing()
    {
        var hashesBefore = HashFixtures();

        var result = Parse(await CodeFixTools.ApplyCodeFix("XX9999"));

        result.GetProperty("applied").GetBoolean().Should().BeFalse();
        result.GetProperty("diagnosticsFound").GetInt32().Should().Be(0);
        result.GetProperty("changedFileCount").GetInt32().Should().Be(0);
        result.GetProperty("message").GetString().Should().StartWith("Not fixable: no Roslyn C# code fix provider handles 'XX9999'");
        ChangedFiles(hashesBefore, HashFixtures()).Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyCodeFix_FixableIdWithNoOccurrences_ReportsZeroFound_AndWritesNothing()
    {
        var hashesBefore = HashFixtures();

        // CS0168 (declared but never used) has a Roslyn fixer (RemoveUnusedVariable) but no occurrence in the fixture.
        var result = Parse(await CodeFixTools.ApplyCodeFix("CS0168", scope: "solution"));

        result.GetProperty("applied").GetBoolean().Should().BeFalse();
        result.GetProperty("diagnosticsFound").GetInt32().Should().Be(0);
        result.GetProperty("diagnosticsFixed").GetInt32().Should().Be(0);
        result.GetProperty("changes").GetArrayLength().Should().Be(0);
        result.GetProperty("message").GetString().Should().Be("No 'CS0168' diagnostics in scope (solution); nothing to fix. Nothing was written.");
        ChangedFiles(hashesBefore, HashFixtures()).Should().BeEmpty();
    }
}
