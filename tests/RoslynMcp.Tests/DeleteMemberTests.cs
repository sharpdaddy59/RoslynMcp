using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Build.Locator;
using RoslynMcp.Tools;
using Xunit;

namespace RoslynMcp.Tests;

/// <summary>
/// delete_member coverage against fixtures/FixtureSolution/FixtureLib/Removable.cs and
/// FixtureApp/RemovableUsages.cs. Answer key: DeadHelper, _b and LooksDead have zero
/// references and are deletable; LiveHelper is called from Used() (refused without force,
/// rejected by the compile gate with CS0103 with force); Ping implements IRemovable.Ping
/// (refused as a contract break). Tests that may write restore the fixture in finally.
/// </summary>
[Collection("workspace")]  // WorkspaceHost is a process-wide singleton: classes that load solutions must not run in parallel
public class DeleteMemberTests : IAsyncLifetime
{
    private static readonly string SolutionPath = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureSolution.slnx"));
    private static readonly string FixtureLibDir =
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureLib");
    private static readonly string FixtureAppDir =
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureApp");
    private static readonly string RemovableFile = Path.Combine(FixtureLibDir, "Removable.cs");
    private static readonly string UsagesFile = Path.Combine(FixtureAppDir, "RemovableUsages.cs");

    static DeleteMemberTests()
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

    private static Task<string> DeleteAt(string needle, bool force = false)
    {
        var (file, line, column) = FindPosition(RemovableFile, needle);
        return DeletionTools.DeleteMember(file, line, column, force: force);
    }

    [Fact]
    public async Task DeleteMember_UnusedPrivateMethod_RemovesOnlyThatDeclarationAndItsDocComment()
    {
        var before = await File.ReadAllTextAsync(RemovableFile);
        var expected = before.Replace(
            "\n    /// <summary>Unused private helper - the find_unused_members to delete_member round trip.</summary>\n" +
            "    private string DeadHelper() => \"dead\";\n",
            "");
        expected.Should().NotBe(before, "the fixture must contain the exact DeadHelper text this test removes");

        try
        {
            var result = Parse(await DeleteAt("DeadHelper()"));

            result.GetProperty("applied").GetBoolean().Should().BeTrue();
            result.GetProperty("kind").GetString().Should().Be("method");
            result.GetProperty("changedFileCount").GetInt32().Should().Be(1);
            result.TryGetProperty("note", out _).Should().BeFalse("a private member has no external consumers");

            var after = await File.ReadAllTextAsync(RemovableFile);
            after.Should().Be(expected);
            after.Should().NotContain("string DeadHelper()");
        }
        finally
        {
            await File.WriteAllTextAsync(RemovableFile, before);
        }
    }

    [Fact]
    public async Task DeleteMember_OneDeclaratorOfAMultiDeclaratorField_RemovesOnlyThatDeclarator()
    {
        var before = await File.ReadAllTextAsync(RemovableFile);

        try
        {
            var result = Parse(await DeleteAt("_b = 2"));

            result.GetProperty("applied").GetBoolean().Should().BeTrue();
            result.GetProperty("kind").GetString().Should().Be("field");

            var after = await File.ReadAllTextAsync(RemovableFile);
            after.Should().Contain("    private int _a = 1;\n");
            after.Should().Be(before.Replace("private int _a = 1, _b = 2;", "private int _a = 1;"));
        }
        finally
        {
            await File.WriteAllTextAsync(RemovableFile, before);
        }
    }

    [Fact]
    public async Task DeleteMember_ReferencedMethodWithoutForce_RefusesAndListsTheCallSite()
    {
        var before = await File.ReadAllTextAsync(RemovableFile);
        var (_, callLine, callColumn) = FindPosition(RemovableFile, "LiveHelper().Length");

        try
        {
            var result = Parse(await DeleteAt("LiveHelper() => \"live\""));

            result.GetProperty("applied").GetBoolean().Should().BeFalse();
            result.GetProperty("changedFileCount").GetInt32().Should().Be(0);
            result.GetProperty("message").GetString().Should().Contain("force: true");

            var references = result.GetProperty("references").EnumerateArray().ToList();
            references.Should().HaveCount(1);
            references[0].GetProperty("file").GetString().Should().Be(RemovableFile);
            references[0].GetProperty("line").GetInt32().Should().Be(callLine);
            references[0].GetProperty("column").GetInt32().Should().Be(callColumn);
            references[0].GetProperty("snippet").GetString().Should().Contain("public int Used()");

            (await File.ReadAllTextAsync(RemovableFile)).Should().Be(before, "a refused delete must not write");
        }
        finally
        {
            await File.WriteAllTextAsync(RemovableFile, before);
        }
    }

    [Fact]
    public async Task DeleteMember_ReferencedMethodWithForce_IsRejectedByTheCompileGateWithCS0103()
    {
        var before = await File.ReadAllTextAsync(RemovableFile);

        try
        {
            var result = Parse(await DeleteAt("LiveHelper() => \"live\"", force: true));

            result.GetProperty("applied").GetBoolean().Should().BeFalse();
            result.GetProperty("changedFileCount").GetInt32().Should().Be(0);
            result.GetProperty("conflicts").EnumerateArray()
                .Should().Contain(c => c.GetProperty("id").GetString() == "CS0103");

            (await File.ReadAllTextAsync(RemovableFile)).Should().Be(before, "the compile gate must block the write");
        }
        finally
        {
            await File.WriteAllTextAsync(RemovableFile, before);
        }
    }

    [Fact]
    public async Task DeleteMember_InterfaceImplementationWithoutForce_RefusesNamingTheContract()
    {
        var before = await File.ReadAllTextAsync(RemovableFile);
        var usagesBefore = await File.ReadAllTextAsync(UsagesFile);

        try
        {
            var result = Parse(await DeleteAt("Ping() => Console"));

            result.GetProperty("applied").GetBoolean().Should().BeFalse();
            result.GetProperty("message").GetString().Should().Contain("IRemovable.Ping");
            result.GetProperty("contracts").EnumerateArray()
                .Should().ContainSingle(c => c.GetString()!.Contains("IRemovable.Ping"));
            result.TryGetProperty("references", out _).Should().BeFalse(
                "the call goes through IRemovable.Ping, which survives deleting the implementation");

            (await File.ReadAllTextAsync(RemovableFile)).Should().Be(before);
            (await File.ReadAllTextAsync(UsagesFile)).Should().Be(usagesBefore);
        }
        finally
        {
            await File.WriteAllTextAsync(RemovableFile, before);
            await File.WriteAllTextAsync(UsagesFile, usagesBefore);
        }
    }

    [Fact]
    public async Task DeleteMember_PublicMemberWithZeroReferences_AppliesWithAnExternalConsumerNote()
    {
        var before = await File.ReadAllTextAsync(RemovableFile);
        var expected = before.Replace(
            "\n    public string LooksDead() => \"no references anywhere in the solution\";\n", "");
        expected.Should().NotBe(before);

        try
        {
            var result = Parse(await DeleteAt("LooksDead()"));

            result.GetProperty("applied").GetBoolean().Should().BeTrue();
            result.GetProperty("changedFileCount").GetInt32().Should().Be(1);
            result.GetProperty("note").GetString().Should().Contain("External consumers");

            (await File.ReadAllTextAsync(RemovableFile)).Should().Be(expected);
        }
        finally
        {
            await File.WriteAllTextAsync(RemovableFile, before);
        }
    }

    [Fact]
    public async Task DeleteMember_RoundTripWithFindUnusedMembers_EntryDisappearsAfterDeletion()
    {
        var before = await File.ReadAllTextAsync(RemovableFile);

        static List<string> HighConfidenceNames(JsonElement result) => result.GetProperty("highConfidence")
            .EnumerateArray()
            .Select(e => e.GetProperty("symbol").GetProperty("name").GetString()!)
            .ToList();

        try
        {
            var unusedBefore = Parse(await AnalysisTools.FindUnusedMembers("FixtureLib"));
            HighConfidenceNames(unusedBefore).Should().Contain("DeadHelper");

            var deletion = Parse(await DeletionTools.DeleteMember(fullyQualifiedName: "FixtureLib.Removable.DeadHelper"));
            deletion.GetProperty("applied").GetBoolean().Should().BeTrue();

            // The live workspace was updated by the apply; also reload from disk to prove the file itself changed.
            HighConfidenceNames(Parse(await AnalysisTools.FindUnusedMembers("FixtureLib"))).Should().NotContain("DeadHelper");
            Parse(await SolutionTools.LoadSolution(SolutionPath));
            var unusedAfter = HighConfidenceNames(Parse(await AnalysisTools.FindUnusedMembers("FixtureLib")));
            unusedAfter.Should().NotContain("DeadHelper");
            unusedAfter.Should().Contain("_b", "only DeadHelper was deleted");
        }
        finally
        {
            await File.WriteAllTextAsync(RemovableFile, before);
        }
    }
    [Fact]
    public async Task DeleteMember_OnlyTypeInFile_AppliesAndReportsFileNowEmptyWithoutDeletingTheFile()
    {
        var usagesBefore = await File.ReadAllTextAsync(UsagesFile);

        try
        {
            var result = Parse(await DeletionTools.DeleteMember(fullyQualifiedName: "RemovableUsages"));

            result.GetProperty("applied").GetBoolean().Should().BeTrue();
            result.GetProperty("kind").GetString().Should().Be("class");
            result.GetProperty("fileNowEmpty").GetBoolean().Should().BeTrue();

            File.Exists(UsagesFile).Should().BeTrue("this version never deletes files");
            (await File.ReadAllTextAsync(UsagesFile)).Should().Be("using FixtureLib;\n");
        }
        finally
        {
            await File.WriteAllTextAsync(UsagesFile, usagesBefore);
        }
    }

    [Fact]
    public async Task DeleteMember_ReferencedTypeWithoutForce_RefusesAndListsReferencesFromOtherProjects()
    {
        var before = await File.ReadAllTextAsync(RemovableFile);

        var result = Parse(await DeletionTools.DeleteMember(fullyQualifiedName: "FixtureLib.Removable"));

        result.GetProperty("applied").GetBoolean().Should().BeFalse();
        result.GetProperty("references").EnumerateArray()
            .Should().Contain(r => r.GetProperty("file").GetString() == UsagesFile &&
                                   r.GetProperty("snippet").GetString()!.Contains("new Removable()"));
        (await File.ReadAllTextAsync(RemovableFile)).Should().Be(before);
    }
}
