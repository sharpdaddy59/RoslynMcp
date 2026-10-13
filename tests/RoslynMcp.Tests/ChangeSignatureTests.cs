using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Build.Locator;
using RoslynMcp.Tools;
using Xunit;

namespace RoslynMcp.Tests;

/// <summary>
/// Adversarial change_signature coverage against the in-repo fixture solution
/// (fixtures/FixtureSolution/FixtureLib/Shipping.cs + FixtureApp/ShippingUsages.cs).
/// The two-argument Quote is a cascade of exactly three declarations (interface member,
/// virtual base, override) with call sites through all three static types, positional and
/// named, plus a base.Quote(...) call and a cross-project call inside FixtureLib. A
/// same-named one-argument overload sits next to it and must never change. Tests write
/// real fixture files, so each restores the original content in a finally block.
/// </summary>
[Collection("workspace")]  // WorkspaceHost is a process-wide singleton: classes that load solutions must not run in parallel
public class ChangeSignatureTests : IAsyncLifetime
{
    private static readonly string SolutionPath = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureSolution.slnx"));
    private static readonly string FixtureLibDir =
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureLib");
    private static readonly string FixtureAppDir =
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureApp");

    private static readonly string ShippingFile = Path.Combine(FixtureLibDir, "Shipping.cs");
    private static readonly string UsagesFile = Path.Combine(FixtureAppDir, "ShippingUsages.cs");

    static ChangeSignatureTests()
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

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

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

    private static List<string> Cascade(JsonElement result) =>
        result.GetProperty("cascade").EnumerateArray().Select(c => c.GetString()!).ToList();

    private static readonly string[] ExpectedQuoteCascade =
    [
        "FixtureLib.IShipper.Quote(string, int)",
        "FixtureLib.Shipper.Quote(string, int)",
        "FixtureLib.ExpressShipper.Quote(string, int)"
    ];

    [Fact]
    public async Task ChangeSignature_AddParameterWithCallSiteValue_CascadesToInterfaceBaseOverrideAndEveryCallSite_NeverTheOneArgOverload()
    {
        var shippingBefore = await File.ReadAllTextAsync(ShippingFile);
        var usagesBefore = await File.ReadAllTextAsync(UsagesFile);

        // Entry point: the virtual base declaration, by position.
        var (file, line, column) = FindPosition(ShippingFile, "Quote(string destination, int weightKg) => weightKg");

        try
        {
            var result = Parse(await SignatureTools.ChangeSignature(
                file, line, column,
                addParameters: [new AddParameterSpec { Name = "insured", Type = "bool", CallSiteValue = "false" }]));

            result.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
            result.GetProperty("applied").GetBoolean().Should().BeTrue();
            Cascade(result).Should().BeEquivalentTo(ExpectedQuoteCascade);
            result.GetProperty("callSiteCount").GetInt32().Should().Be(5, "3 calls in ShippingUsages + base.Quote(...) + the call inside Estimate");
            result.GetProperty("changedFileCount").GetInt32().Should().Be(2);
            result.GetProperty("operations").GetProperty("after").GetString()
                .Should().Be("FixtureLib.Shipper.Quote(string destination, int weightKg, bool insured)");

            // Exact answer key: every line not listed here is byte-identical, including the one-arg overload.
            var expectedShipping = shippingBefore
                .Replace("decimal Quote(string destination, int weightKg);",
                         "decimal Quote(string destination, int weightKg, bool insured);")
                .Replace("public virtual decimal Quote(string destination, int weightKg) =>",
                         "public virtual decimal Quote(string destination, int weightKg, bool insured) =>")
                .Replace("public override decimal Quote(string destination, int weightKg) => base.Quote(destination, weightKg) * 2;",
                         "public override decimal Quote(string destination, int weightKg, bool insured) => base.Quote(destination, weightKg, false) * 2;")
                .Replace("=> Quote(destination, weightKg) + 1;",
                         "=> Quote(destination, weightKg, false) + 1;");
            expectedShipping.Should().NotBe(shippingBefore, "sanity: the answer key must actually differ");
            (await File.ReadAllTextAsync(ShippingFile)).Should().Be(expectedShipping);

            var expectedUsages = usagesBefore
                .Replace("viaInterface.Quote(\"Austin\", 2)", "viaInterface.Quote(\"Austin\", 2, false)")
                .Replace("viaBase.Quote(\"Dallas\", 5)", "viaBase.Quote(\"Dallas\", 5, false)")
                .Replace("viaDerived.Quote(weightKg: 3, destination: \"Houston\")",
                         "viaDerived.Quote(weightKg: 3, destination: \"Houston\", insured: false)");
            var usagesAfter = await File.ReadAllTextAsync(UsagesFile);
            usagesAfter.Should().Be(expectedUsages);
            usagesAfter.Should().Contain("viaBase.Quote(\"Waco\");", "the one-arg overload's call site must never change");
        }
        finally
        {
            await File.WriteAllTextAsync(ShippingFile, shippingBefore);
            await File.WriteAllTextAsync(UsagesFile, usagesBefore);
        }
    }

    [Fact]
    public async Task ChangeSignature_RemoveParameterStillReadInABody_IsRejectedByTheCompileGate_AndWritesNothing()
    {
        var shippingBefore = await File.ReadAllTextAsync(ShippingFile);
        var usagesBefore = await File.ReadAllTextAsync(UsagesFile);

        try
        {
            // Entry point: the interface member, by fully-qualified name (cascade must flow DOWN to the implementations).
            var result = Parse(await SignatureTools.ChangeSignature(
                fullyQualifiedName: "FixtureLib.IShipper.Quote",
                removeParameters: ["weightKg"]));

            result.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
            result.GetProperty("applied").GetBoolean().Should().BeFalse();
            Cascade(result).Should().BeEquivalentTo(ExpectedQuoteCascade);
            result.GetProperty("changedFileCount").GetInt32().Should().Be(0);
            result.GetProperty("conflicts").EnumerateArray()
                .Should().Contain(c => c.GetProperty("id").GetString() == "CS0103",
                    "Shipper.Quote's body still reads weightKg");
            result.GetProperty("message").GetString().Should().StartWith("Not applied");

            (await File.ReadAllTextAsync(ShippingFile)).Should().Be(shippingBefore, "a rejected edit must not be written");
            (await File.ReadAllTextAsync(UsagesFile)).Should().Be(usagesBefore, "a rejected edit must not be written");
        }
        finally
        {
            await File.WriteAllTextAsync(ShippingFile, shippingBefore);
            await File.WriteAllTextAsync(UsagesFile, usagesBefore);
        }
    }

    [Fact]
    public async Task ChangeSignature_RemoveUnreadParameter_AppliesToDeclarationAndCallSite()
    {
        var shippingBefore = await File.ReadAllTextAsync(ShippingFile);
        var usagesBefore = await File.ReadAllTextAsync(UsagesFile);

        try
        {
            var result = Parse(await SignatureTools.ChangeSignature(
                fullyQualifiedName: "FixtureLib.Shipper.Surcharge",
                removeParameters: ["reason"]));

            result.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
            result.GetProperty("applied").GetBoolean().Should().BeTrue();
            Cascade(result).Should().BeEquivalentTo(["FixtureLib.Shipper.Surcharge(decimal, string)"]);
            result.GetProperty("callSiteCount").GetInt32().Should().Be(1);
            result.GetProperty("changedFileCount").GetInt32().Should().Be(2);

            (await File.ReadAllTextAsync(ShippingFile)).Should().Be(shippingBefore.Replace(
                "public decimal Surcharge(decimal baseQuote, string reason) =>",
                "public decimal Surcharge(decimal baseQuote) =>"));
            (await File.ReadAllTextAsync(UsagesFile)).Should().Be(usagesBefore.Replace(
                "viaBase.Surcharge(total, \"fuel\")",
                "viaBase.Surcharge(total)"));
        }
        finally
        {
            await File.WriteAllTextAsync(ShippingFile, shippingBefore);
            await File.WriteAllTextAsync(UsagesFile, usagesBefore);
        }
    }

    [Fact]
    public async Task ChangeSignature_Reorder_SwapsPositionalArguments_LeavesNamedArgumentsTextuallyUntouched()
    {
        var shippingBefore = await File.ReadAllTextAsync(ShippingFile);
        var usagesBefore = await File.ReadAllTextAsync(UsagesFile);

        // Entry point: the OVERRIDE, by position (cascade must flow UP to the base and the interface).
        var (file, line, column) = FindPosition(ShippingFile, "Quote(string destination, int weightKg) => base.Quote");

        try
        {
            var result = Parse(await SignatureTools.ChangeSignature(
                file, line, column,
                newOrder: ["weightKg", "destination"]));

            result.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
            result.GetProperty("applied").GetBoolean().Should().BeTrue();
            Cascade(result).Should().BeEquivalentTo(ExpectedQuoteCascade);
            result.GetProperty("changedFileCount").GetInt32().Should().Be(2);

            var expectedShipping = shippingBefore
                .Replace("decimal Quote(string destination, int weightKg);",
                         "decimal Quote(int weightKg, string destination);")
                .Replace("public virtual decimal Quote(string destination, int weightKg) =>",
                         "public virtual decimal Quote(int weightKg, string destination) =>")
                .Replace("public override decimal Quote(string destination, int weightKg) => base.Quote(destination, weightKg) * 2;",
                         "public override decimal Quote(int weightKg, string destination) => base.Quote(weightKg, destination) * 2;")
                .Replace("=> Quote(destination, weightKg) + 1;",
                         "=> Quote(weightKg, destination) + 1;");
            (await File.ReadAllTextAsync(ShippingFile)).Should().Be(expectedShipping);

            var expectedUsages = usagesBefore
                .Replace("viaInterface.Quote(\"Austin\", 2)", "viaInterface.Quote(2, \"Austin\")")
                .Replace("viaBase.Quote(\"Dallas\", 5)", "viaBase.Quote(5, \"Dallas\")");
            var usagesAfter = await File.ReadAllTextAsync(UsagesFile);
            usagesAfter.Should().Be(expectedUsages);
            usagesAfter.Should().Contain("viaDerived.Quote(weightKg: 3, destination: \"Houston\")", "named arguments are left textually untouched");

            // The named call site still compiles against the reordered signature.
            var diagnostics = Parse(await DiagnosticsTools.GetDiagnostics("FixtureApp", "Error"));
            diagnostics.GetProperty("diagnostics").EnumerateArray().Should().BeEmpty();
        }
        finally
        {
            await File.WriteAllTextAsync(ShippingFile, shippingBefore);
            await File.WriteAllTextAsync(UsagesFile, usagesBefore);
        }
    }

    [Fact]
    public async Task ChangeSignature_CombinedOperations_AddThenReorder_NewArgumentLeadsPositionallyAndNamedArgumentsStayPut()
    {
        var shippingBefore = await File.ReadAllTextAsync(ShippingFile);
        var usagesBefore = await File.ReadAllTextAsync(UsagesFile);

        try
        {
            var result = Parse(await SignatureTools.ChangeSignature(
                fullyQualifiedName: "FixtureLib.Shipper.Quote(string, int)",
                addParameters: [new AddParameterSpec { Name = "insured", Type = "bool", CallSiteValue = "false" }],
                newOrder: ["insured", "weightKg", "destination"]));

            result.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
            result.GetProperty("applied").GetBoolean().Should().BeTrue();
            Cascade(result).Should().BeEquivalentTo(ExpectedQuoteCascade);
            result.GetProperty("operations").GetProperty("after").GetString()
                .Should().Be("FixtureLib.Shipper.Quote(bool insured, int weightKg, string destination)");

            var shippingAfter = await File.ReadAllTextAsync(ShippingFile);
            shippingAfter.Should().Contain("decimal Quote(bool insured, int weightKg, string destination);");
            shippingAfter.Should().Contain("public virtual decimal Quote(bool insured, int weightKg, string destination) =>");
            shippingAfter.Should().Contain("public override decimal Quote(bool insured, int weightKg, string destination) => base.Quote(false, weightKg, destination) * 2;");
            shippingAfter.Should().Contain("public decimal Quote(string destination) => destination.Length * 1.5m;");

            var usagesAfter = await File.ReadAllTextAsync(UsagesFile);
            usagesAfter.Should().Contain("viaInterface.Quote(false, 2, \"Austin\")");
            usagesAfter.Should().Contain("viaBase.Quote(false, 5, \"Dallas\")");
            usagesAfter.Should().Contain("viaDerived.Quote(false, weightKg: 3, destination: \"Houston\")",
                "the new parameter is first in the final order, so its argument can lead positionally; the existing named arguments stay textually untouched");
            usagesAfter.Should().Contain("viaBase.Quote(\"Waco\");");
        }
        finally
        {
            await File.WriteAllTextAsync(ShippingFile, shippingBefore);
            await File.WriteAllTextAsync(UsagesFile, usagesBefore);
        }
    }

    [Fact]
    public async Task ChangeSignature_MethodUsedAsMethodGroup_RefusesWithTheLocation_AndWritesNothing()
    {
        var shippingBefore = await File.ReadAllTextAsync(ShippingFile);
        var usagesBefore = await File.ReadAllTextAsync(UsagesFile);
        var (_, methodGroupLine, _) = FindPosition(UsagesFile, "Func<string, int, decimal> estimate = viaBase.Estimate;");

        try
        {
            var result = Parse(await SignatureTools.ChangeSignature(
                fullyQualifiedName: "FixtureLib.Shipper.Estimate",
                addParameters: [new AddParameterSpec { Name = "insured", Type = "bool", CallSiteValue = "false" }]));

            result.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
            result.GetProperty("applied").GetBoolean().Should().BeFalse();
            result.GetProperty("changedFileCount").GetInt32().Should().Be(0);
            result.GetProperty("message").GetString().Should().Contain("cannot be rewritten safely");

            var unsupported = result.GetProperty("unsupportedUsages").EnumerateArray().ToList();
            unsupported.Should().HaveCount(1);
            unsupported[0].GetProperty("file").GetString().Should().Be(UsagesFile);
            unsupported[0].GetProperty("line").GetInt32().Should().Be(methodGroupLine);
            unsupported[0].GetProperty("reason").GetString().Should().Contain("method group");

            (await File.ReadAllTextAsync(ShippingFile)).Should().Be(shippingBefore);
            (await File.ReadAllTextAsync(UsagesFile)).Should().Be(usagesBefore);
        }
        finally
        {
            await File.WriteAllTextAsync(ShippingFile, shippingBefore);
            await File.WriteAllTextAsync(UsagesFile, usagesBefore);
        }
    }

    [Fact]
    public async Task ChangeSignature_AddWithoutCallSiteValueOrDefault_ErrorsBeforeTouchingAnything()
    {
        var shippingBefore = await File.ReadAllTextAsync(ShippingFile);
        var usagesBefore = await File.ReadAllTextAsync(UsagesFile);

        var result = Parse(await SignatureTools.ChangeSignature(
            fullyQualifiedName: "FixtureLib.IShipper.Quote",
            addParameters: [new AddParameterSpec { Name = "insured", Type = "bool" }]));

        result.GetProperty("error").GetString().Should().Contain("callSiteValue");
        (await File.ReadAllTextAsync(ShippingFile)).Should().Be(shippingBefore);
        (await File.ReadAllTextAsync(UsagesFile)).Should().Be(usagesBefore);
    }
}
