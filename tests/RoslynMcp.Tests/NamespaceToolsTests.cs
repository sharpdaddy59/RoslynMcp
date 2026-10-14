// ANSWER KEY - move_type_to_namespace / rename_namespace (v2.2 item D)
//
// Fixture (all new files):
//   FixtureLib/Catalog.cs           block   namespace FixtureLib.Catalog { Product, partial Sku (part 1) }
//                                   + comment "// Lives in FixtureLib.Catalog on purpose" (leading trivia of Product)
//                                   + string literal "FixtureLib.Catalog" in Sku.Origin()
//   FixtureLib/Catalog.Part2.cs     file-scoped namespace FixtureLib.Catalog; { partial Sku (part 2), Price }
//   FixtureLib/Catalog/Nested.cs    file-scoped namespace FixtureLib.Catalog.Nested; { Shelf }
//   FixtureLib/Catalog/ShelfProduct.cs  namespace FixtureLib.Catalog.Nested; { Product }  (collision bait)
//   FixtureApp/CatalogUsages.cs     using FixtureLib.Catalog; using static FixtureLib.Catalog.Nested.Shelf;
//                                   using CatalogPrice = FixtureLib.Catalog.Price; a cref FixtureLib.Catalog.Sku;
//                                   two fully-qualified FixtureLib.Catalog.Sku, a simple-name Sku,
//                                   FixtureLib.Catalog.Nested.Shelf.Capacity
//
// 1. move_type_to_namespace FixtureLib.Catalog.Sku -> FixtureLib.Inventory  (gate accepts; 4 changed + 1 added file)
//    - Catalog.cs (block shared with Product): Sku part 1 moves, doc comment included, into a NEW block
//      'namespace FixtureLib.Inventory { using FixtureLib.Catalog; ... }' at the end of the file (it uses Product).
//      The comment and the "FixtureLib.Catalog" literal are byte-identical.
//    - Catalog.Part2.cs (FILE-SCOPED, shared with Price): C# forbids a second namespace in a file-scoped file,
//      so the Sku part moves to a NEW FILE FixtureLib/Sku.cs ('using FixtureLib.Catalog;' for Price,
//      'namespace FixtureLib.Inventory;'). Part2 keeps Price and gains 'using FixtureLib.Inventory;' (Price uses Sku).
//    - CatalogUsages.cs: both FixtureLib.Catalog.Sku and the cref become FixtureLib.Inventory.Sku; the simple-name
//      'Sku?' gets 'using FixtureLib.Inventory;' inserted in sorted position (line 2).
//    - FixtureLib.csproj unchanged. A dry run first reports the identical `changes` and writes nothing.
// 2. move_type_to_namespace to the namespace it is already in -> applied=false, nothing written.
//    Into FixtureLib.Catalog.Nested (holds another Product) -> refused naming the collision.
// 3. rename_namespace FixtureLib.Catalog -> FixtureLib.Inventory (Roslyn Renamer; gate accepts; 5 files):
//    the 4 declarations (Catalog.cs, Catalog.Part2.cs, Nested.cs -> FixtureLib.Inventory.Nested, ShelfProduct.cs)
//    and CatalogUsages' using, using static, alias, cref and 3 qualified references. Comment and literal untouched.
//    A dry run first reports the identical `changes` and writes nothing.
// 4. rename_namespace FixtureLib.Catalog -> FixtureLib.Inventory.Legacy (multi-segment: own syntax pass): same sites.
// 5. rename_namespace FixtureLib.Catalog -> FixtureLib (exists) without merge -> refused naming 'FixtureLib';
//    merge: true into the existing sibling FixtureLib.LegacyShapes -> dry run accepted (Renamer);
//    merge: true into FixtureLib -> applied (syntax pass): Catalog.cs 'namespace FixtureLib', Nested 'FixtureLib.Nested',
//    CatalogUsages 'using FixtureLib;' and FixtureLib.Sku / FixtureLib.Nested.Shelf references.
// 6. rename_namespace System.Text (metadata only) -> refused.
// Every test restores the fixture in finally and asserts the fixture tree (bin/obj excluded) is byte-identical.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Build.Locator;
using RoslynMcp.Tools;
using Xunit;

namespace RoslynMcp.Tests;

[Collection("workspace")]  // WorkspaceHost is a process-wide singleton: classes that load solutions must not run in parallel
public class NamespaceToolsTests : IAsyncLifetime
{
    private static readonly string FixtureRoot = Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution");
    private static readonly string SolutionPath = Path.GetFullPath(Path.Combine(FixtureRoot, "FixtureSolution.slnx"));
    private static readonly string FixtureLibDir = Path.Combine(FixtureRoot, "FixtureLib");
    private static readonly string FixtureAppDir = Path.Combine(FixtureRoot, "FixtureApp");

    private static readonly string CatalogFile = Path.Combine(FixtureLibDir, "Catalog.cs");
    private static readonly string Part2File = Path.Combine(FixtureLibDir, "Catalog.Part2.cs");
    private static readonly string NestedFile = Path.Combine(FixtureLibDir, "Catalog", "Nested.cs");
    private static readonly string ShelfProductFile = Path.Combine(FixtureLibDir, "Catalog", "ShelfProduct.cs");
    private static readonly string SkuFile = Path.Combine(FixtureLibDir, "Sku.cs");
    private static readonly string UsagesFile = Path.Combine(FixtureAppDir, "CatalogUsages.cs");
    private static readonly string CsprojFile = Path.Combine(FixtureLibDir, "FixtureLib.csproj");

    private const string CommentLine = "    // Lives in FixtureLib.Catalog on purpose\n";
    private const string LiteralLine = "        public string Origin() => \"FixtureLib.Catalog\";\n";

    private Dictionary<string, byte[]> _snapshot = new();

    static NamespaceToolsTests()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }

    public async Task InitializeAsync()
    {
        EnsureRestored();
        _snapshot = SnapshotFixtures();
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

    private static string ParseError(string json)
    {
        var element = JsonDocument.Parse(json).RootElement;
        element.TryGetProperty("error", out var error).Should().BeTrue($"expected a structured error, got: {json}");
        return error.GetString()!;
    }

    private static bool IsBuildOutput(string path) =>
        Path.GetRelativePath(FixtureRoot, path).Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj");

    private static Dictionary<string, byte[]> SnapshotFixtures() =>
        Directory.EnumerateFiles(FixtureRoot, "*", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(p))
            .ToDictionary(p => p, File.ReadAllBytes);

    private static Dictionary<string, string> HashFixtures() =>
        SnapshotFixtures().ToDictionary(kv => kv.Key, kv => Convert.ToHexString(SHA256.HashData(kv.Value)));

    /// <summary>Puts every snapshotted fixture file back and deletes files the test created, then proves the tree is byte-identical.</summary>
    private void RestoreAndVerifyFixtures()
    {
        foreach (var path in Directory.EnumerateFiles(FixtureRoot, "*", SearchOption.AllDirectories).Where(p => !IsBuildOutput(p)))
        {
            if (!_snapshot.ContainsKey(path)) File.Delete(path);
        }
        foreach (var (path, bytes) in _snapshot)
        {
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(path, bytes);
        }

        var expected = _snapshot.ToDictionary(kv => kv.Key, kv => Convert.ToHexString(SHA256.HashData(kv.Value)));
        HashFixtures().Should().BeEquivalentTo(expected, "the fixture tree (bin/obj excluded) must be byte-identical after the test");
    }

    private static string Text(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");

    private static string Raw(JsonElement result, string property) =>
        result.TryGetProperty(property, out var value) ? value.GetRawText() : "<absent>";

    private static void ShouldBeRefusedWithoutWriting(JsonElement result)
    {
        result.GetProperty("applied").GetBoolean().Should().BeFalse(result.GetRawText());
        result.GetProperty("changedFileCount").GetInt32().Should().Be(0);
        result.GetProperty("changes").GetArrayLength().Should().Be(0);
    }

    // --------------------------------------------------------------------- move_type_to_namespace

    [Fact]
    public async Task MoveTypeToNamespace_PartialSkuAcrossTwoFiles_MovesBothPartsAndRewritesEveryReference()
    {
        var catalogBefore = Text(CatalogFile);
        var usagesBefore = Text(UsagesFile);
        var csprojBefore = await File.ReadAllBytesAsync(CsprojFile);

        try
        {
            var hashesBefore = HashFixtures();
            var dry = Parse(await NamespaceTools.MoveTypeToNamespace("FixtureLib.Inventory", fullyQualifiedName: "FixtureLib.Catalog.Sku", dryRun: true));
            dry.GetProperty("applied").GetBoolean().Should().BeFalse();
            dry.GetProperty("dryRun").GetBoolean().Should().BeTrue();
            dry.GetProperty("wouldApply").GetBoolean().Should().BeTrue(dry.GetRawText());
            HashFixtures().Should().BeEquivalentTo(hashesBefore, "a dry run writes nothing");

            var result = Parse(await NamespaceTools.MoveTypeToNamespace("FixtureLib.Inventory", fullyQualifiedName: "FixtureLib.Catalog.Sku"));

            result.GetProperty("applied").GetBoolean().Should().BeTrue(result.GetRawText());
            result.TryGetProperty("dryRun", out _).Should().BeFalse();
            result.GetProperty("oldNamespace").GetString().Should().Be("FixtureLib.Catalog");
            result.GetProperty("newNamespace").GetString().Should().Be("FixtureLib.Inventory");
            Raw(dry, "changes").Should().Be(Raw(result, "changes"), "the dry run must describe exactly the edit the real call writes");
            result.GetProperty("changedFileCount").GetInt32().Should().Be(4, "Catalog.cs, Catalog.Part2.cs and CatalogUsages.cs change and Sku.cs is added; nothing else");
            result.GetProperty("changes").EnumerateArray().Select(c => Path.GetFileName(c.GetProperty("file").GetString()))
                .Should().BeEquivalentTo("Catalog.cs", "Catalog.Part2.cs", "CatalogUsages.cs", "Sku.cs");

            var mechanics = result.GetProperty("declarations").EnumerateArray()
                .ToDictionary(d => Path.GetFileName(d.GetProperty("file").GetString()!), d => d.GetProperty("mechanics").GetString());
            mechanics.Should().BeEquivalentTo(new Dictionary<string, string?>
            {
                ["Catalog.cs"] = "movedIntoNewBlockAtEndOfFile",
                ["Catalog.Part2.cs"] = "movedIntoNewFile"
            });

            Text(CatalogFile).Should().Be(
                "namespace FixtureLib.Catalog\n" +
                "{\n" +
                CommentLine +
                "    /// <summary>A catalog product (namespace-tools fixture; see tests/RoslynMcp.Tests/NamespaceToolsTests.cs).</summary>\n" +
                "    public class Product\n" +
                "    {\n" +
                "        public string Name { get; init; } = \"\";\n" +
                "    }\n" +
                "}\n" +
                "\n" +
                "namespace FixtureLib.Inventory\n" +
                "{\n" +
                "    using FixtureLib.Catalog;\n" +
                "\n" +
                "    /// <summary>First part of a partial type split across Catalog.cs and Catalog.Part2.cs.</summary>\n" +
                "    public partial class Sku\n" +
                "    {\n" +
                "        public string Code { get; init; } = \"\";\n" +
                "\n" +
                "        public Product? Item { get; init; }\n" +
                "\n" +
                LiteralLine +
                "    }\n" +
                "}\n");
            catalogBefore.Should().Contain(CommentLine).And.Contain(LiteralLine);

            Text(Part2File).Should().Be(
                "using FixtureLib.Inventory;\n" +
                "\n" +
                "namespace FixtureLib.Catalog;\n" +
                "\n" +
                "/// <summary>A price; it stays in FixtureLib.Catalog when Sku moves.</summary>\n" +
                "public record Price(decimal Amount)\n" +
                "{\n" +
                "    public Sku For(string code) => new Sku { Code = code, Cost = this };\n" +
                "}\n");

            Text(SkuFile).Should().Be(
                "using FixtureLib.Catalog;\n" +
                "\n" +
                "namespace FixtureLib.Inventory;\n" +
                "\n" +
                "/// <summary>Second part of Sku (see Catalog.cs).</summary>\n" +
                "public partial class Sku\n" +
                "{\n" +
                "    public Price Cost { get; init; } = new Price(0m);\n" +
                "}\n");

            Text(UsagesFile).Should().Be(usagesBefore
                .Replace("using FixtureLib.Catalog;\n", "using FixtureLib.Catalog;\nusing FixtureLib.Inventory;\n")
                .Replace("<see cref=\"FixtureLib.Catalog.Sku\"/>", "<see cref=\"FixtureLib.Inventory.Sku\"/>")
                .Replace("        FixtureLib.Catalog.Sku sku = new FixtureLib.Catalog.Sku {",
                         "        FixtureLib.Inventory.Sku sku = new FixtureLib.Inventory.Sku {"));
            Text(UsagesFile).Split('\n')[1].Should().Be("using FixtureLib.Inventory;");

            (await File.ReadAllBytesAsync(CsprojFile)).Should().Equal(csprojBefore, "the SDK glob picks up Sku.cs; no <Compile Include> may be left behind");
        }
        finally
        {
            RestoreAndVerifyFixtures();
        }
    }

    [Fact]
    public async Task MoveTypeToNamespace_ToItsCurrentNamespace_IsRefusedAndWritesNothing()
    {
        try
        {
            var hashesBefore = HashFixtures();
            var result = Parse(await NamespaceTools.MoveTypeToNamespace("FixtureLib.Catalog", fullyQualifiedName: "FixtureLib.Catalog.Sku"));

            ShouldBeRefusedWithoutWriting(result);
            result.GetProperty("message").GetString().Should().Contain("already in namespace 'FixtureLib.Catalog'");
            HashFixtures().Should().BeEquivalentTo(hashesBefore);
        }
        finally
        {
            RestoreAndVerifyFixtures();
        }
    }

    [Fact]
    public async Task MoveTypeToNamespace_IntoNamespaceWithSameNamedType_IsRefusedNamingTheCollision()
    {
        try
        {
            var hashesBefore = HashFixtures();
            var result = Parse(await NamespaceTools.MoveTypeToNamespace("FixtureLib.Catalog.Nested", fullyQualifiedName: "FixtureLib.Catalog.Product"));

            ShouldBeRefusedWithoutWriting(result);
            result.GetProperty("message").GetString().Should()
                .Contain("already contains a type named 'Product'").And.Contain("ShelfProduct.cs");
            HashFixtures().Should().BeEquivalentTo(hashesBefore);
        }
        finally
        {
            RestoreAndVerifyFixtures();
        }
    }

    [Fact]
    public async Task MoveTypeToNamespace_NestedOrMetadataType_IsRefused()
    {
        try
        {
            var hashesBefore = HashFixtures();

            ParseError(await NamespaceTools.MoveTypeToNamespace("FixtureLib.Elsewhere", fullyQualifiedName: "FixtureLib.LegacyShapes.Hexagon+Side"))
                .Should().Contain("nested inside 'FixtureLib.LegacyShapes.Hexagon'");
            ParseError(await NamespaceTools.MoveTypeToNamespace("FixtureLib.Elsewhere", fullyQualifiedName: "System.String"))
                .Should().Contain("'System.String' is defined in metadata");

            HashFixtures().Should().BeEquivalentTo(hashesBefore);
        }
        finally
        {
            RestoreAndVerifyFixtures();
        }
    }

    // --------------------------------------------------------------------- rename_namespace

    [Fact]
    public async Task RenameNamespace_CatalogToInventory_RenamesDeclarationsChildrenAndEveryReference()
    {
        var catalogBefore = Text(CatalogFile);
        var part2Before = Text(Part2File);
        var nestedBefore = Text(NestedFile);
        var shelfProductBefore = Text(ShelfProductFile);
        var usagesBefore = Text(UsagesFile);

        try
        {
            var hashesBefore = HashFixtures();
            var dry = Parse(await NamespaceTools.RenameNamespace("FixtureLib.Catalog", "FixtureLib.Inventory", dryRun: true));
            dry.GetProperty("wouldApply").GetBoolean().Should().BeTrue(dry.GetRawText());
            HashFixtures().Should().BeEquivalentTo(hashesBefore, "a dry run writes nothing");

            var result = Parse(await NamespaceTools.RenameNamespace("FixtureLib.Catalog", "FixtureLib.Inventory"));

            result.GetProperty("applied").GetBoolean().Should().BeTrue(result.GetRawText());
            result.GetProperty("mechanics").GetString().Should().Be("renamer");
            result.GetProperty("declarationCount").GetInt32().Should().Be(4);
            result.GetProperty("changedFileCount").GetInt32().Should().Be(5);
            Raw(dry, "changes").Should().Be(Raw(result, "changes"));

            Text(CatalogFile).Should().Be(catalogBefore.Replace("namespace FixtureLib.Catalog\n{", "namespace FixtureLib.Inventory\n{"));
            Text(CatalogFile).Should().Contain(CommentLine, "comments are never touched").And.Contain(LiteralLine, "string literals are never touched");
            Text(CatalogFile).Split('\n')[0].Should().Be("namespace FixtureLib.Inventory");

            Text(Part2File).Should().Be(part2Before.Replace("namespace FixtureLib.Catalog;\n", "namespace FixtureLib.Inventory;\n"));
            Text(NestedFile).Should().Be(nestedBefore.Replace("namespace FixtureLib.Catalog.Nested;\n", "namespace FixtureLib.Inventory.Nested;\n"));
            Text(ShelfProductFile).Should().Be(shelfProductBefore.Replace("namespace FixtureLib.Catalog.Nested;\n", "namespace FixtureLib.Inventory.Nested;\n"));
            Text(NestedFile).Split('\n')[0].Should().Be("namespace FixtureLib.Inventory.Nested;");

            Text(UsagesFile).Should().Be(ExpectedUsages(usagesBefore, "FixtureLib.Inventory"));
        }
        finally
        {
            RestoreAndVerifyFixtures();
        }
    }

    [Fact]
    public async Task RenameNamespace_MultiSegmentTarget_UsesTheSyntaxPassAndRewritesTheSameSites()
    {
        var catalogBefore = Text(CatalogFile);
        var nestedBefore = Text(NestedFile);
        var usagesBefore = Text(UsagesFile);

        try
        {
            var result = Parse(await NamespaceTools.RenameNamespace("FixtureLib.Catalog", "FixtureLib.Inventory.Legacy"));

            result.GetProperty("applied").GetBoolean().Should().BeTrue(result.GetRawText());
            result.GetProperty("mechanics").GetString().Should().Be("syntax");
            result.GetProperty("changedFileCount").GetInt32().Should().Be(5);

            Text(CatalogFile).Should().Be(catalogBefore.Replace("namespace FixtureLib.Catalog\n{", "namespace FixtureLib.Inventory.Legacy\n{"));
            Text(CatalogFile).Should().Contain(CommentLine).And.Contain(LiteralLine);
            Text(Part2File).Split('\n')[0].Should().Be("namespace FixtureLib.Inventory.Legacy;");
            Text(NestedFile).Should().Be(nestedBefore.Replace("namespace FixtureLib.Catalog.Nested;\n", "namespace FixtureLib.Inventory.Legacy.Nested;\n"));
            Text(ShelfProductFile).Split('\n')[0].Should().Be("namespace FixtureLib.Inventory.Legacy.Nested;");
            Text(UsagesFile).Should().Be(ExpectedUsages(usagesBefore, "FixtureLib.Inventory.Legacy"));
        }
        finally
        {
            RestoreAndVerifyFixtures();
        }
    }

    [Fact]
    public async Task RenameNamespace_ToExistingNamespace_RefusedWithoutMergeAndAppliedWithMerge()
    {
        var usagesBefore = Text(UsagesFile);

        try
        {
            var hashesBefore = HashFixtures();
            var refused = Parse(await NamespaceTools.RenameNamespace("FixtureLib.Catalog", "FixtureLib"));

            ShouldBeRefusedWithoutWriting(refused);
            refused.GetProperty("message").GetString().Should()
                .Contain("namespace 'FixtureLib' already exists").And.Contain("merge: true");
            HashFixtures().Should().BeEquivalentTo(hashesBefore);

            // Merging into an existing SIBLING keeps the last-segment shape, so Renamer handles it.
            var siblingDry = Parse(await NamespaceTools.RenameNamespace("FixtureLib.Catalog", "FixtureLib.LegacyShapes", merge: true, dryRun: true));
            siblingDry.GetProperty("wouldApply").GetBoolean().Should().BeTrue(siblingDry.GetRawText());
            siblingDry.GetProperty("mechanics").GetString().Should().Be("renamer");
            siblingDry.GetProperty("changedFileCount").GetInt32().Should().Be(5);
            HashFixtures().Should().BeEquivalentTo(hashesBefore);

            var merged = Parse(await NamespaceTools.RenameNamespace("FixtureLib.Catalog", "FixtureLib", merge: true));

            merged.GetProperty("applied").GetBoolean().Should().BeTrue(merged.GetRawText());
            merged.GetProperty("merge").GetBoolean().Should().BeTrue();
            merged.GetProperty("mechanics").GetString().Should().Be("syntax");
            merged.GetProperty("changedFileCount").GetInt32().Should().Be(5);

            Text(CatalogFile).Split('\n')[0].Should().Be("namespace FixtureLib");
            Text(CatalogFile).Should().Contain(CommentLine).And.Contain(LiteralLine);
            Text(Part2File).Split('\n')[0].Should().Be("namespace FixtureLib;");
            Text(NestedFile).Split('\n')[0].Should().Be("namespace FixtureLib.Nested;");
            Text(UsagesFile).Should().Be(ExpectedUsages(usagesBefore, "FixtureLib"));
        }
        finally
        {
            RestoreAndVerifyFixtures();
        }
    }

    [Fact]
    public async Task RenameNamespace_NamespaceWithNoSourceDeclarations_IsRefused()
    {
        try
        {
            var hashesBefore = HashFixtures();
            var result = Parse(await NamespaceTools.RenameNamespace("System.Text", "System.Texts"));

            ShouldBeRefusedWithoutWriting(result);
            result.GetProperty("message").GetString().Should().Contain("'System.Text' has no namespace declarations in this solution's source");
            HashFixtures().Should().BeEquivalentTo(hashesBefore);
        }
        finally
        {
            RestoreAndVerifyFixtures();
        }
    }

    /// <summary>CatalogUsages.cs after FixtureLib.Catalog is renamed to <paramref name="ns"/>: exactly these seven sites change.</summary>
    private static string ExpectedUsages(string before, string ns)
    {
        var expected = before
            .Replace("using FixtureLib.Catalog;\n", $"using {ns};\n")
            .Replace("using static FixtureLib.Catalog.Nested.Shelf;\n", $"using static {ns}.Nested.Shelf;\n")
            .Replace("using CatalogPrice = FixtureLib.Catalog.Price;\n", $"using CatalogPrice = {ns}.Price;\n")
            .Replace("<see cref=\"FixtureLib.Catalog.Sku\"/>", $"<see cref=\"{ns}.Sku\"/>")
            .Replace("        FixtureLib.Catalog.Sku sku = new FixtureLib.Catalog.Sku {", $"        {ns}.Sku sku = new {ns}.Sku {{")
            .Replace("var shelves = FixtureLib.Catalog.Nested.Shelf.Capacity", $"var shelves = {ns}.Nested.Shelf.Capacity");
        expected.Should().NotBe(before);
        // Prose mentioning the old namespace (the doc summary) is not code and must survive.
        expected.Should().Contain("simple-name reference into FixtureLib.Catalog.");
        return expected;
    }
}
