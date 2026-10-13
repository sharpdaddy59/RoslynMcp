using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Build.Locator;
using RoslynMcp.Tools;
using Xunit;

namespace RoslynMcp.Tests;

/// <summary>
/// move_type_to_file coverage against the in-repo fixture's Shapes.cs (file-scoped
/// namespace, three types) and LegacyShapes.cs (block namespace with an inner using).
/// Tests write real fixture files, so each restores the source file and the csproj and
/// deletes the created file in a finally block.
/// </summary>
[Collection("workspace")]  // WorkspaceHost is a process-wide singleton: classes that load solutions must not run in parallel
public class MoveTypeToFileTests : IAsyncLifetime
{
    private static readonly string SolutionPath = Path.GetFullPath(
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureSolution.slnx"));
    private static readonly string FixtureLibDir =
        Path.Combine(TestPaths.RepoRoot, "fixtures", "FixtureSolution", "FixtureLib");
    private static readonly string CsprojFile = Path.Combine(FixtureLibDir, "FixtureLib.csproj");
    private static readonly string ShapesFile = Path.Combine(FixtureLibDir, "Shapes.cs");
    private static readonly string SquareFile = Path.Combine(FixtureLibDir, "Square.cs");
    private static readonly string LegacyShapesFile = Path.Combine(FixtureLibDir, "LegacyShapes.cs");
    private static readonly string TriangleFile = Path.Combine(FixtureLibDir, "Triangle.cs");

    static MoveTypeToFileTests()
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

    private static string ParseError(string json)
    {
        var element = JsonDocument.Parse(json).RootElement;
        element.TryGetProperty("error", out var error).Should().BeTrue($"expected a structured error, got: {json}");
        return error.GetString()!;
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
    public async Task MoveTypeToFile_SquareOutOfShapes_CreatesSquareCsWithUsingsNamespaceAndDocs()
    {
        var shapesBefore = await File.ReadAllBytesAsync(ShapesFile);
        var csprojBefore = await File.ReadAllBytesAsync(CsprojFile);

        try
        {
            // Position on a member (Area), not the declaration: resolves to the enclosing type
            var (file, line, column) = FindPosition(ShapesFile, "public double Area");
            var result = Parse(await FileOrganizationTools.MoveTypeToFile(file, line, column));

            result.GetProperty("applied").GetBoolean().Should().BeTrue(result.ToString());
            result.GetProperty("type").GetProperty("name").GetString().Should().Be("Square");
            result.GetProperty("sourceFile").GetString().Should().Be(ShapesFile);
            result.GetProperty("destinationFile").GetString().Should().Be(SquareFile);
            result.GetProperty("remainingTypesInSource").EnumerateArray().Select(e => e.GetString())
                .Should().Equal("Circle", "ShapeKind");
            result.GetProperty("changedFileCount").GetInt32().Should().Be(2);

            File.Exists(SquareFile).Should().BeTrue();
            var squareText = (await File.ReadAllTextAsync(SquareFile)).ReplaceLineEndings("\n");
            squareText.Should().Be(
                "using System;\n" +
                "using Text = System.String;\n" +
                "\n" +
                "namespace FixtureLib;\n" +
                "\n" +
                "/// <summary>A square with a given side length.</summary>\n" +
                "[Serializable]\n" +
                "public readonly record struct Square(double Side)\n" +
                "{\n" +
                "    public double Area => Math.Pow(Side, 2);\n" +
                "\n" +
                "    public Text Label() => $\"{ShapeKind.Angular} {Side}\";\n" +
                "}\n");

            var shapesText = (await File.ReadAllTextAsync(ShapesFile)).ReplaceLineEndings("\n");
            shapesText.Should().NotContain("record struct Square");
            shapesText.Should().NotContain("A square with a given side length");
            shapesText.Should().Contain("public sealed class Circle");
            shapesText.Should().Contain("public enum ShapeKind");
            shapesText.Should().NotContain("\n\n\n", "no doubled blank lines may be left behind");
            shapesText.Should().Contain("    public Square Bounds() => new Square(Radius * 2);\n}\n\npublic enum ShapeKind");

            (await File.ReadAllBytesAsync(CsprojFile)).Should().Equal(csprojBefore,
                "the SDK glob already includes Square.cs, so no explicit <Compile Include> may be left behind");
        }
        finally
        {
            await File.WriteAllBytesAsync(ShapesFile, shapesBefore);
            await File.WriteAllBytesAsync(CsprojFile, csprojBefore);
            if (File.Exists(SquareFile)) File.Delete(SquareFile);
        }
    }

    [Fact]
    public async Task MoveTypeToFile_FromBlockNamespace_KeepsBlockFormAndInnerUsing()
    {
        var legacyBefore = await File.ReadAllBytesAsync(LegacyShapesFile);
        var csprojBefore = await File.ReadAllBytesAsync(CsprojFile);

        try
        {
            var result = Parse(await FileOrganizationTools.MoveTypeToFile(
                fullyQualifiedName: "FixtureLib.LegacyShapes.Triangle"));

            result.GetProperty("applied").GetBoolean().Should().BeTrue(result.ToString());
            result.GetProperty("remainingTypesInSource").EnumerateArray().Select(e => e.GetString())
                .Should().Equal("Hexagon");

            var triangleText = (await File.ReadAllTextAsync(TriangleFile)).ReplaceLineEndings("\n");
            triangleText.Should().StartWith(
                "namespace FixtureLib.LegacyShapes\n" +
                "{\n" +
                "    using System.Text;\n" +
                "\n");
            triangleText.Should().Contain("    public class Triangle\n    {\n");
            triangleText.Should().EndWith("    }\n}\n");
            triangleText.Should().NotContain("Hexagon\n");
            triangleText.Should().NotContain("namespace FixtureLib.LegacyShapes;");

            var legacyText = (await File.ReadAllTextAsync(LegacyShapesFile)).ReplaceLineEndings("\n");
            legacyText.Should().NotContain("class Triangle");
            legacyText.Should().Contain("    using System.Text;\n\n    public class Hexagon");
            legacyText.Should().NotContain("\n\n\n");

            (await File.ReadAllBytesAsync(CsprojFile)).Should().Equal(csprojBefore);
        }
        finally
        {
            await File.WriteAllBytesAsync(LegacyShapesFile, legacyBefore);
            await File.WriteAllBytesAsync(CsprojFile, csprojBefore);
            if (File.Exists(TriangleFile)) File.Delete(TriangleFile);
        }
    }

    [Fact]
    public async Task MoveTypeToFile_DestinationAlreadyExists_RefusesAndWritesNothing()
    {
        var shapesBefore = await File.ReadAllBytesAsync(ShapesFile);
        const string placeholder = "// placeholder written by the test before calling move_type_to_file\n";

        try
        {
            await File.WriteAllTextAsync(SquareFile, placeholder);

            var error = ParseError(await FileOrganizationTools.MoveTypeToFile(fullyQualifiedName: "FixtureLib.Square"));

            error.Should().Contain("already exists");
            (await File.ReadAllTextAsync(SquareFile)).Should().Be(placeholder);
            (await File.ReadAllBytesAsync(ShapesFile)).Should().Equal(shapesBefore);
        }
        finally
        {
            await File.WriteAllBytesAsync(ShapesFile, shapesBefore);
            if (File.Exists(SquareFile)) File.Delete(SquareFile);
        }
    }

    [Fact]
    public async Task MoveTypeToFile_NestedType_RefusesAndWritesNothing()
    {
        var legacyBefore = await File.ReadAllBytesAsync(LegacyShapesFile);
        var sideFile = Path.Combine(FixtureLibDir, "Side.cs");

        var (file, line, column) = FindPosition(LegacyShapesFile, "public int Index");
        var error = ParseError(await FileOrganizationTools.MoveTypeToFile(file, line, column));

        error.Should().Contain("nested");
        File.Exists(sideFile).Should().BeFalse();
        (await File.ReadAllBytesAsync(LegacyShapesFile)).Should().Equal(legacyBefore);
    }

    [Fact]
    public async Task MoveTypeToFile_SoleTypeIntoItsOwnFileName_IsANoOpRefusal()
    {
        var greeterFile = Path.Combine(FixtureLibDir, "Greeter.cs");
        var greeterBefore = await File.ReadAllBytesAsync(greeterFile);

        var (file, line, column) = FindPosition(greeterFile, "class Greeter");
        var error = ParseError(await FileOrganizationTools.MoveTypeToFile(file, line, column));

        error.Should().Contain("nothing to move");
        (await File.ReadAllBytesAsync(greeterFile)).Should().Equal(greeterBefore);
    }

    [Fact]
    public async Task MoveTypeToFile_AfterMove_WorkspaceAndReadToolsSeeTheNewFile()
    {
        var shapesBefore = await File.ReadAllBytesAsync(ShapesFile);
        var csprojBefore = await File.ReadAllBytesAsync(CsprojFile);

        try
        {
            var result = Parse(await FileOrganizationTools.MoveTypeToFile(fullyQualifiedName: "FixtureLib.Square"));
            result.GetProperty("applied").GetBoolean().Should().BeTrue(result.ToString());

            WorkspaceHost.Current.GetDocumentIdsWithFilePath(SquareFile).Should().ContainSingle(
                "the moved type's new document must be in the live workspace without a reload");

            var search = Parse(await SearchTools.SymbolSearch("Square", "Struct"));
            var match = search.GetProperty("matches").EnumerateArray()
                .Single(m => m.GetProperty("symbol").GetProperty("name").GetString() == "Square");
            match.GetProperty("locations").EnumerateArray().Single()
                .GetProperty("file").GetString().Should().Be(SquareFile);
        }
        finally
        {
            await File.WriteAllBytesAsync(ShapesFile, shapesBefore);
            await File.WriteAllBytesAsync(CsprojFile, csprojBefore);
            if (File.Exists(SquareFile)) File.Delete(SquareFile);
        }
    }
}
