using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Build.Locator;
using RoslynMcp.Tools;
using Xunit;

namespace RoslynMcp.Tests;

/// <summary>
/// extract_interface coverage against the in-repo fixture solution. Tests write real
/// fixture files and the new IWhatever.cs alongside them, so each restores the class
/// file and deletes the generated interface file in a finally block.
/// </summary>
[Collection("workspace")]  // WorkspaceHost is a process-wide singleton: classes that load solutions must not run in parallel
public class ExtractInterfaceTests : IAsyncLifetime
{
    private static readonly string SolutionPath = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureSolution.slnx"));
    private static readonly string FixtureLibDir =
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureLib");
    private static readonly string CsprojFile = Path.Combine(FixtureLibDir, "FixtureLib.csproj");

    static ExtractInterfaceTests()
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

    [Fact]
    public async Task ExtractInterface_FromExportable_YieldsCompilingInterfaceWithOnlyEligibleMembers()
    {
        var classFile = Path.Combine(FixtureLibDir, "Exportable.cs");
        var interfaceFile = Path.Combine(FixtureLibDir, "IExportable.cs");
        var classBefore = await File.ReadAllTextAsync(classFile);
        var csprojBefore = await File.ReadAllTextAsync(CsprojFile);

        try
        {
            var (file, line, column) = FindPosition(classFile, "Exportable");
            var result = Parse(await GenerationTools.ExtractInterface(file, line, column));

            result.GetProperty("applied").GetBoolean().Should().BeTrue();
            result.GetProperty("members").EnumerateArray().Select(m => m.GetString())
                .Should().BeEquivalentTo(["Name", "DoWork"]);

            File.Exists(interfaceFile).Should().BeTrue();
            var interfaceText = await File.ReadAllTextAsync(interfaceFile);
            interfaceText.Should().Contain("interface IExportable");
            interfaceText.Should().NotContain("Count", "the static member must never appear");
            interfaceText.Should().NotContain("Secret", "the internal member must never appear");

            (await File.ReadAllTextAsync(classFile)).Replace(" ", "").Should().Contain("classExportable:IExportable");

            // (1) the generated interface actually compiles as part of the solution
            var diagnostics = Parse(await DiagnosticsTools.GetDiagnostics("FixtureLib", "Error"));
            diagnostics.GetProperty("diagnostics").EnumerateArray()
                .Where(d => d.GetProperty("location").GetProperty("file").GetString() == interfaceFile)
                .Should().BeEmpty("the generated interface file must compile cleanly");
        }
        finally
        {
            await File.WriteAllTextAsync(classFile, classBefore);
            await File.WriteAllTextAsync(CsprojFile, csprojBefore);
            File.Delete(interfaceFile);
        }
    }

    [Fact]
    public async Task ExtractInterface_MemberFilter_ExtractsOnlyTheRequestedSubset()
    {
        var classFile = Path.Combine(FixtureLibDir, "Exportable.cs");
        var interfaceFile = Path.Combine(FixtureLibDir, "IExportable.cs");
        var classBefore = await File.ReadAllTextAsync(classFile);
        var csprojBefore = await File.ReadAllTextAsync(CsprojFile);

        try
        {
            var (file, line, column) = FindPosition(classFile, "Exportable");
            var result = Parse(await GenerationTools.ExtractInterface(file, line, column, memberFilter: ["DoWork"]));

            result.GetProperty("applied").GetBoolean().Should().BeTrue();
            result.GetProperty("members").EnumerateArray().Select(m => m.GetString())
                .Should().BeEquivalentTo(["DoWork"]);

            var interfaceText = await File.ReadAllTextAsync(interfaceFile);
            interfaceText.Should().Contain("DoWork");
            interfaceText.Should().NotContain("Name");
        }
        finally
        {
            await File.WriteAllTextAsync(classFile, classBefore);
            await File.WriteAllTextAsync(CsprojFile, csprojBefore);
            File.Delete(interfaceFile);
        }
    }

    [Fact]
    public async Task ExtractInterface_GenericClassWithConstraint_RoundTripsTheConstraintAndCompiles()
    {
        var classFile = Path.Combine(FixtureLibDir, "Box.cs");
        var interfaceFile = Path.Combine(FixtureLibDir, "IBox.cs");
        var classBefore = await File.ReadAllTextAsync(classFile);
        var csprojBefore = await File.ReadAllTextAsync(CsprojFile);

        try
        {
            var (file, line, column) = FindPosition(classFile, "Box<T>");
            var result = Parse(await GenerationTools.ExtractInterface(file, line, column));

            result.GetProperty("applied").GetBoolean().Should().BeTrue();

            var interfaceText = await File.ReadAllTextAsync(interfaceFile);
            interfaceText.Should().Contain("interface IBox<T>");
            interfaceText.Should().Contain("IComparable<T>", "the class's constraint must round-trip onto the interface");

            (await File.ReadAllTextAsync(classFile)).Replace(" ", "").Should().Contain("Box<T>:IBox<T>");
        }
        finally
        {
            await File.WriteAllTextAsync(classFile, classBefore);
            await File.WriteAllTextAsync(CsprojFile, csprojBefore);
            File.Delete(interfaceFile);
        }
    }

    [Fact]
    public async Task ExtractInterface_AddToBaseListFalse_GeneratesTheFileButLeavesTheClassAlone()
    {
        var classFile = Path.Combine(FixtureLibDir, "Exportable.cs");
        var interfaceFile = Path.Combine(FixtureLibDir, "IExportable.cs");
        var classBefore = await File.ReadAllTextAsync(classFile);
        var csprojBefore = await File.ReadAllTextAsync(CsprojFile);

        try
        {
            var (file, line, column) = FindPosition(classFile, "Exportable");
            var result = Parse(await GenerationTools.ExtractInterface(file, line, column, addToBaseList: false));

            result.GetProperty("applied").GetBoolean().Should().BeTrue();
            result.GetProperty("addedToBaseList").GetBoolean().Should().BeFalse();

            (await File.ReadAllTextAsync(classFile)).Should().Be(classBefore, "addToBaseList=false must leave the class file untouched");
            File.Exists(interfaceFile).Should().BeTrue();
        }
        finally
        {
            await File.WriteAllTextAsync(classFile, classBefore);
            await File.WriteAllTextAsync(CsprojFile, csprojBefore);
            if (File.Exists(interfaceFile)) File.Delete(interfaceFile);
        }
    }
}
