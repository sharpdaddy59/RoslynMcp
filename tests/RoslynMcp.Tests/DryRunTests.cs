using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Tools;
using Xunit;

namespace RoslynMcp.Tests;

/// <summary>
/// dryRun coverage for every write tool (apply_rename, extract_interface, change_signature,
/// delete_member, move_type_to_file) and for MutationApplier.PreviewAsync. For each tool:
/// (a) a dry run reports dryRun/wouldApply and the exact `changes` list the real call then
/// reports; (b) the dry run leaves every fixture file (bin/obj excluded) byte-identical;
/// (c) a dry run of an edit the gate rejects reports the same conflicts (or stale file) as the
/// real call. The known-good and known-rejected cases are the ones the per-tool test classes
/// already prove. Tests that may write restore the fixture in finally.
/// </summary>
[Collection("workspace")]  // WorkspaceHost is a process-wide singleton: classes that load solutions must not run in parallel
public class DryRunTests : IAsyncLifetime
{
    private static readonly string FixtureRoot =
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution");
    private static readonly string SolutionPath = Path.GetFullPath(
        Path.Combine(FixtureRoot, "FixtureSolution.slnx"));
    private static readonly string FixtureLibDir = Path.Combine(FixtureRoot, "FixtureLib");
    private static readonly string FixtureAppDir = Path.Combine(FixtureRoot, "FixtureApp");
    private static readonly string CsprojFile = Path.Combine(FixtureLibDir, "FixtureLib.csproj");

    private static readonly string ProcessorFile = Path.Combine(FixtureLibDir, "Processor.cs");
    private static readonly string ProgramFile = Path.Combine(FixtureAppDir, "Program.cs");
    private static readonly string ExportableFile = Path.Combine(FixtureLibDir, "Exportable.cs");
    private static readonly string IExportableFile = Path.Combine(FixtureLibDir, "IExportable.cs");
    private static readonly string ShippingFile = Path.Combine(FixtureLibDir, "Shipping.cs");
    private static readonly string IShipperFile = Path.Combine(FixtureLibDir, "IShipper.cs");
    private static readonly string ShippingUsagesFile = Path.Combine(FixtureAppDir, "ShippingUsages.cs");
    private static readonly string RemovableFile = Path.Combine(FixtureLibDir, "Removable.cs");
    private static readonly string ShapesFile = Path.Combine(FixtureLibDir, "Shapes.cs");
    private static readonly string SquareFile = Path.Combine(FixtureLibDir, "Square.cs");

    static DryRunTests()
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

    private static string Raw(JsonElement result, string property) =>
        result.TryGetProperty(property, out var value) ? value.GetRawText() : "<absent>";

    private static void ShouldBeAcceptedDryRun(JsonElement dry)
    {
        dry.GetProperty("applied").GetBoolean().Should().BeFalse("a dry run never applies");
        dry.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        dry.GetProperty("wouldApply").GetBoolean().Should().BeTrue(dry.GetRawText());
        dry.GetProperty("changes").GetArrayLength().Should().BeGreaterThan(0);
        dry.GetProperty("changedFileCount").GetInt32().Should().Be(dry.GetProperty("changes").GetArrayLength());
    }

    private static void ShouldBeRejectedDryRun(JsonElement dry)
    {
        dry.GetProperty("applied").GetBoolean().Should().BeFalse();
        dry.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        dry.GetProperty("wouldApply").GetBoolean().Should().BeFalse(dry.GetRawText());
        dry.GetProperty("changedFileCount").GetInt32().Should().Be(0);
    }

    private static void ShouldMatchRealSuccess(JsonElement dry, JsonElement real)
    {
        real.GetProperty("applied").GetBoolean().Should().BeTrue(real.GetRawText());
        real.TryGetProperty("dryRun", out _).Should().BeFalse("a real call does not carry dry-run fields");
        real.TryGetProperty("wouldApply", out _).Should().BeFalse();
        Raw(dry, "changes").Should().Be(Raw(real, "changes"), "the dry run must describe exactly the edit the real call writes");
        dry.GetProperty("changedFileCount").GetInt32().Should().Be(real.GetProperty("changedFileCount").GetInt32());
    }

    private static void ShouldMatchRealRejection(JsonElement dry, JsonElement real)
    {
        real.GetProperty("applied").GetBoolean().Should().BeFalse();
        Raw(dry, "conflicts").Should().Be(Raw(real, "conflicts"));
        Raw(dry, "staleFile").Should().Be(Raw(real, "staleFile"));
        Raw(dry, "message").Should().Be(Raw(real, "message"));
    }

    // ---------------------------------------------------------------- apply_rename

    [Fact]
    public async Task ApplyRename_DryRun_ReportsTheRealChangesAndWritesNothing()
    {
        var processorBefore = await File.ReadAllBytesAsync(ProcessorFile);
        var programBefore = await File.ReadAllBytesAsync(ProgramFile);
        var (file, line, column) = FindPosition(ProcessorFile, "Process(string input) => $\"base:");

        try
        {
            var hashesBefore = HashFixtures();
            var dry = Parse(await RefactorTools.ApplyRename("Execute", file, line, column, dryRun: true));
            ShouldBeAcceptedDryRun(dry);
            dry.GetProperty("changedFileCount").GetInt32().Should().Be(2);
            dry.GetProperty("message").GetString().Should().StartWith("Dry run");
            HashFixtures().Should().Equal(hashesBefore, "a dry run must not write anything");

            var real = Parse(await RefactorTools.ApplyRename("Execute", file, line, column));
            ShouldMatchRealSuccess(dry, real);
        }
        finally
        {
            await File.WriteAllBytesAsync(ProcessorFile, processorBefore);
            await File.WriteAllBytesAsync(ProgramFile, programBefore);
        }
    }

    [Fact]
    public async Task ApplyRename_DryRunOfACollidingName_ReportsTheSameConflictsAsTheRealCall()
    {
        var processorBefore = await File.ReadAllBytesAsync(ProcessorFile);
        var (file, line, column) = FindPosition(ProcessorFile, "Process(string input) => $\"base:");

        try
        {
            var hashesBefore = HashFixtures();
            var dry = Parse(await RefactorTools.ApplyRename("Handle", file, line, column, dryRun: true));
            ShouldBeRejectedDryRun(dry);
            dry.GetProperty("conflicts").EnumerateArray()
                .Should().Contain(c => c.GetProperty("id").GetString() == "CS0111");
            HashFixtures().Should().Equal(hashesBefore);

            var real = Parse(await RefactorTools.ApplyRename("Handle", file, line, column));
            ShouldMatchRealRejection(dry, real);
            HashFixtures().Should().Equal(hashesBefore);
        }
        finally
        {
            await File.WriteAllBytesAsync(ProcessorFile, processorBefore);
        }
    }

    // ---------------------------------------------------------------- extract_interface

    [Fact]
    public async Task ExtractInterface_DryRun_ReportsTheRealChangesAndCreatesNoFile()
    {
        var classBefore = await File.ReadAllBytesAsync(ExportableFile);
        var csprojBefore = await File.ReadAllBytesAsync(CsprojFile);
        var (file, line, column) = FindPosition(ExportableFile, "Exportable");

        try
        {
            var hashesBefore = HashFixtures();
            var dry = Parse(await GenerationTools.ExtractInterface(file, line, column, dryRun: true));
            ShouldBeAcceptedDryRun(dry);
            dry.GetProperty("changedFileCount").GetInt32().Should().Be(2, "the class gains a base type and IExportable.cs is added");
            dry.GetProperty("interfaceFilePath").GetString().Should().Be(IExportableFile);
            File.Exists(IExportableFile).Should().BeFalse("a dry run must not create the interface file");
            HashFixtures().Should().Equal(hashesBefore, "a dry run must not write anything");

            var real = Parse(await GenerationTools.ExtractInterface(file, line, column));
            ShouldMatchRealSuccess(dry, real);
            var added = real.GetProperty("changes").EnumerateArray().Single(c => c.TryGetProperty("added", out _));
            added.GetProperty("edits")[0].GetProperty("newText").GetString()
                .Should().Be(await File.ReadAllTextAsync(IExportableFile), "the reported new-file text is what was written");
        }
        finally
        {
            await File.WriteAllBytesAsync(ExportableFile, classBefore);
            await File.WriteAllBytesAsync(CsprojFile, csprojBefore);
            if (File.Exists(IExportableFile)) File.Delete(IExportableFile);
        }
    }

    [Fact]
    public async Task ExtractInterface_DryRunOfADuplicateInterface_ReportsTheSameConflictsAsTheRealCall()
    {
        // Shipper already implements an IShipper declared in Shipping.cs: generating another
        // IShipper is a duplicate type the compile gate rejects.
        var shippingBefore = await File.ReadAllBytesAsync(ShippingFile);
        var csprojBefore = await File.ReadAllBytesAsync(CsprojFile);
        var (file, line, column) = FindPosition(ShippingFile, "class Shipper");

        try
        {
            var hashesBefore = HashFixtures();
            var dry = Parse(await GenerationTools.ExtractInterface(file, line, column + "class ".Length, dryRun: true));
            ShouldBeRejectedDryRun(dry);
            dry.GetProperty("conflicts").EnumerateArray()
                .Should().Contain(c => c.GetProperty("id").GetString() == "CS0101");
            HashFixtures().Should().Equal(hashesBefore);

            var real = Parse(await GenerationTools.ExtractInterface(file, line, column + "class ".Length));
            ShouldMatchRealRejection(dry, real);
            HashFixtures().Should().Equal(hashesBefore);
        }
        finally
        {
            await File.WriteAllBytesAsync(ShippingFile, shippingBefore);
            await File.WriteAllBytesAsync(CsprojFile, csprojBefore);
            if (File.Exists(IShipperFile)) File.Delete(IShipperFile);
        }
    }

    // ---------------------------------------------------------------- change_signature

    [Fact]
    public async Task ChangeSignature_DryRun_ReportsTheRealChangesAndWritesNothing()
    {
        var shippingBefore = await File.ReadAllBytesAsync(ShippingFile);
        var usagesBefore = await File.ReadAllBytesAsync(ShippingUsagesFile);
        AddParameterSpec[] add = [new AddParameterSpec { Name = "insured", Type = "bool", CallSiteValue = "false" }];

        try
        {
            var hashesBefore = HashFixtures();
            var dry = Parse(await SignatureTools.ChangeSignature(
                fullyQualifiedName: "FixtureLib.Shipper.Quote(string, int)", addParameters: add, dryRun: true));
            ShouldBeAcceptedDryRun(dry);
            dry.GetProperty("callSiteCount").GetInt32().Should().Be(5);
            HashFixtures().Should().Equal(hashesBefore, "a dry run must not write anything");

            var real = Parse(await SignatureTools.ChangeSignature(
                fullyQualifiedName: "FixtureLib.Shipper.Quote(string, int)", addParameters: add));
            ShouldMatchRealSuccess(dry, real);
        }
        finally
        {
            await File.WriteAllBytesAsync(ShippingFile, shippingBefore);
            await File.WriteAllBytesAsync(ShippingUsagesFile, usagesBefore);
        }
    }

    [Fact]
    public async Task ChangeSignature_DryRunRemovingAParameterStillRead_ReportsTheSameConflictsAsTheRealCall()
    {
        var shippingBefore = await File.ReadAllBytesAsync(ShippingFile);
        var usagesBefore = await File.ReadAllBytesAsync(ShippingUsagesFile);

        try
        {
            var hashesBefore = HashFixtures();
            var dry = Parse(await SignatureTools.ChangeSignature(
                fullyQualifiedName: "FixtureLib.IShipper.Quote", removeParameters: ["weightKg"], dryRun: true));
            ShouldBeRejectedDryRun(dry);
            dry.GetProperty("conflicts").EnumerateArray()
                .Should().Contain(c => c.GetProperty("id").GetString() == "CS0103");
            HashFixtures().Should().Equal(hashesBefore);

            var real = Parse(await SignatureTools.ChangeSignature(
                fullyQualifiedName: "FixtureLib.IShipper.Quote", removeParameters: ["weightKg"]));
            ShouldMatchRealRejection(dry, real);
            HashFixtures().Should().Equal(hashesBefore);
        }
        finally
        {
            await File.WriteAllBytesAsync(ShippingFile, shippingBefore);
            await File.WriteAllBytesAsync(ShippingUsagesFile, usagesBefore);
        }
    }

    [Fact]
    public async Task ChangeSignature_DryRunOfAMethodGroupRefusal_ReportsTheSameRefusalWithDryRunFlags()
    {
        AddParameterSpec[] add = [new AddParameterSpec { Name = "insured", Type = "bool", CallSiteValue = "false" }];
        var hashesBefore = HashFixtures();

        var dry = Parse(await SignatureTools.ChangeSignature(
            fullyQualifiedName: "FixtureLib.Shipper.Estimate", addParameters: add, dryRun: true));
        ShouldBeRejectedDryRun(dry);

        var real = Parse(await SignatureTools.ChangeSignature(
            fullyQualifiedName: "FixtureLib.Shipper.Estimate", addParameters: add));
        real.GetProperty("applied").GetBoolean().Should().BeFalse();
        Raw(dry, "unsupportedUsages").Should().Be(Raw(real, "unsupportedUsages"));
        Raw(dry, "message").Should().Be(Raw(real, "message"));
        HashFixtures().Should().Equal(hashesBefore);
    }

    // ---------------------------------------------------------------- delete_member

    [Fact]
    public async Task DeleteMember_DryRun_ReportsTheRealChangesAndWritesNothing()
    {
        var removableBefore = await File.ReadAllBytesAsync(RemovableFile);
        var (file, line, column) = FindPosition(RemovableFile, "DeadHelper()");

        try
        {
            var hashesBefore = HashFixtures();
            var dry = Parse(await DeletionTools.DeleteMember(file, line, column, dryRun: true));
            ShouldBeAcceptedDryRun(dry);
            dry.GetProperty("changedFileCount").GetInt32().Should().Be(1);
            HashFixtures().Should().Equal(hashesBefore, "a dry run must not write anything");

            var real = Parse(await DeletionTools.DeleteMember(file, line, column));
            ShouldMatchRealSuccess(dry, real);
        }
        finally
        {
            await File.WriteAllBytesAsync(RemovableFile, removableBefore);
        }
    }

    [Fact]
    public async Task DeleteMember_DryRunWithForceOnAReferencedMember_ReportsTheSameConflictsAsTheRealCall()
    {
        var removableBefore = await File.ReadAllBytesAsync(RemovableFile);
        var (file, line, column) = FindPosition(RemovableFile, "LiveHelper() => \"live\"");

        try
        {
            var hashesBefore = HashFixtures();
            var dry = Parse(await DeletionTools.DeleteMember(file, line, column, force: true, dryRun: true));
            ShouldBeRejectedDryRun(dry);
            dry.GetProperty("conflicts").EnumerateArray()
                .Should().Contain(c => c.GetProperty("id").GetString() == "CS0103");
            HashFixtures().Should().Equal(hashesBefore);

            var real = Parse(await DeletionTools.DeleteMember(file, line, column, force: true));
            ShouldMatchRealRejection(dry, real);
            Raw(dry, "references").Should().Be(Raw(real, "references"));
            HashFixtures().Should().Equal(hashesBefore);
        }
        finally
        {
            await File.WriteAllBytesAsync(RemovableFile, removableBefore);
        }
    }

    [Fact]
    public async Task DeleteMember_DryRunOfAReferenceRefusal_ReportsTheSameRefusalWithDryRunFlags()
    {
        var (file, line, column) = FindPosition(RemovableFile, "LiveHelper() => \"live\"");
        var hashesBefore = HashFixtures();

        var dry = Parse(await DeletionTools.DeleteMember(file, line, column, dryRun: true));
        ShouldBeRejectedDryRun(dry);

        var real = Parse(await DeletionTools.DeleteMember(file, line, column));
        real.GetProperty("applied").GetBoolean().Should().BeFalse();
        Raw(dry, "references").Should().Be(Raw(real, "references"));
        Raw(dry, "message").Should().Be(Raw(real, "message"));
        HashFixtures().Should().Equal(hashesBefore);
    }

    // ---------------------------------------------------------------- move_type_to_file

    [Fact]
    public async Task MoveTypeToFile_DryRun_ReportsTheRealChangesAndCreatesNoFile()
    {
        var shapesBefore = await File.ReadAllBytesAsync(ShapesFile);
        var csprojBefore = await File.ReadAllBytesAsync(CsprojFile);

        try
        {
            var hashesBefore = HashFixtures();
            var dry = Parse(await FileOrganizationTools.MoveTypeToFile(fullyQualifiedName: "FixtureLib.Square", dryRun: true));
            ShouldBeAcceptedDryRun(dry);
            dry.GetProperty("changedFileCount").GetInt32().Should().Be(2, "Shapes.cs shrinks and Square.cs is added");
            File.Exists(SquareFile).Should().BeFalse("a dry run must not create the destination file");
            HashFixtures().Should().Equal(hashesBefore, "a dry run must not write anything (csproj included)");

            var real = Parse(await FileOrganizationTools.MoveTypeToFile(fullyQualifiedName: "FixtureLib.Square"));
            ShouldMatchRealSuccess(dry, real);
            File.Exists(SquareFile).Should().BeTrue();
        }
        finally
        {
            await File.WriteAllBytesAsync(ShapesFile, shapesBefore);
            await File.WriteAllBytesAsync(CsprojFile, csprojBefore);
            if (File.Exists(SquareFile)) File.Delete(SquareFile);
        }
    }

    [Fact]
    public async Task MoveTypeToFile_DryRunWithAStaleSourceFile_ReportsTheSameStaleRefusalAsTheRealCall()
    {
        // A move always compiles, so the gate's other refusal is exercised instead: the source
        // file is touched on disk after load (same bytes, newer write time) and must be refused.
        var shapesBefore = await File.ReadAllBytesAsync(ShapesFile);
        var csprojBefore = await File.ReadAllBytesAsync(CsprojFile);

        try
        {
            File.SetLastWriteTimeUtc(ShapesFile, DateTime.UtcNow.AddMinutes(5));
            var hashesBefore = HashFixtures();

            var dry = Parse(await FileOrganizationTools.MoveTypeToFile(fullyQualifiedName: "FixtureLib.Square", dryRun: true));
            ShouldBeRejectedDryRun(dry);
            dry.GetProperty("staleFile").GetString().Should().Be(ShapesFile);
            HashFixtures().Should().Equal(hashesBefore);

            var real = Parse(await FileOrganizationTools.MoveTypeToFile(fullyQualifiedName: "FixtureLib.Square"));
            ShouldMatchRealRejection(dry, real);
            HashFixtures().Should().Equal(hashesBefore);
            File.Exists(SquareFile).Should().BeFalse();
        }
        finally
        {
            await File.WriteAllBytesAsync(ShapesFile, shapesBefore);
            await File.WriteAllBytesAsync(CsprojFile, csprojBefore);
            if (File.Exists(SquareFile)) File.Delete(SquareFile);
        }
    }

    // ---------------------------------------------------------------- MutationApplier.PreviewAsync

    [Fact]
    public async Task PreviewAsync_ValidEdit_WouldApplyButLeavesWorkspaceAndDiskUntouched()
    {
        var filePath = Path.Combine(FixtureLibDir, "Legacy.cs");
        var before = await File.ReadAllBytesAsync(filePath);
        var text = await File.ReadAllTextAsync(filePath);

        try
        {
            var currentBefore = WorkspaceHost.Current;
            var hashesBefore = HashFixtures();
            var candidate = WorkspaceHost.GetDocument(filePath)
                .WithText(SourceText.From(text.Replace("return 7;", "return 8;"))).Project.Solution;

            var result = await MutationApplier.PreviewAsync(candidate);

            result.Applied.Should().BeFalse();
            result.DryRun.Should().BeTrue();
            result.WouldApply.Should().BeTrue();
            result.ChangedFiles.Should().Equal(filePath);
            result.NewErrors.Should().BeEmpty();
            result.Message.Should().StartWith("Dry run: the edit would be applied; 1 file(s) would change.");
            WorkspaceHost.Current.Should().BeSameAs(currentBefore, "a preview must not touch the live workspace");
            HashFixtures().Should().Equal(hashesBefore, "a preview must not write anything");
        }
        finally
        {
            await File.WriteAllBytesAsync(filePath, before);
        }
    }

    [Fact]
    public async Task PreviewAsync_EditIntroducingACompileError_ReportsTheSameErrorsAsTryApplyAsync()
    {
        var filePath = Path.Combine(FixtureLibDir, "Greeter.cs");
        var before = await File.ReadAllBytesAsync(filePath);
        var text = await File.ReadAllTextAsync(filePath);

        try
        {
            var candidate = WorkspaceHost.GetDocument(filePath)
                .WithText(SourceText.From(text.Replace("return $\"Hello, {name}!\";", "return 42;"))).Project.Solution;

            var preview = await MutationApplier.PreviewAsync(candidate);
            var real = await MutationApplier.TryApplyAsync(candidate);

            preview.DryRun.Should().BeTrue();
            preview.WouldApply.Should().BeFalse();
            preview.Applied.Should().BeFalse();
            preview.NewErrors.Should().Contain(e => e.Id == "CS0029");
            preview.NewErrors.Should().Equal(real.NewErrors);
            preview.Message.Should().Be(real.Message);
            preview.ChangedFiles.Should().BeEmpty();
            real.DryRun.Should().BeFalse();
            real.WouldApply.Should().BeFalse();
            (await File.ReadAllBytesAsync(filePath)).Should().Equal(before);
        }
        finally
        {
            await File.WriteAllBytesAsync(filePath, before);
        }
    }
}
