using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace RoslynMcp.Tests;

public class ToolCallFilterTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "newName":{"type":"string"},
          "line":{"type":["integer","null"]},
          "dryRun":{"type":"boolean"},
          "ratio":{"type":"number"},
          "addParameters":{"type":"array","items":{"type":"object"}}
        }}
        """).RootElement;

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Converts_stringified_scalars_and_arrays_to_the_declared_types()
    {
        var args = Args("""{"dryRun":"false","line":"12","ratio":"0.5","addParameters":"[{\"name\":\"x\"}]"}""");

        ToolCallFilter.CoerceArguments(Schema, args).Should().BeEmpty();

        args["dryRun"].ValueKind.Should().Be(JsonValueKind.False);
        args["line"].GetInt64().Should().Be(12);
        args["ratio"].GetDouble().Should().Be(0.5);
        args["addParameters"].ValueKind.Should().Be(JsonValueKind.Array);
        args["addParameters"][0].GetProperty("name").GetString().Should().Be("x");
    }

    [Fact]
    public void Converts_whole_floats_to_integers_and_scalars_to_strings()
    {
        var args = Args("""{"line":12.0,"newName":123}""");

        ToolCallFilter.CoerceArguments(Schema, args).Should().BeEmpty();

        args["line"].GetInt64().Should().Be(12);
        args["newName"].GetString().Should().Be("123");
    }

    [Fact]
    public void Leaves_correct_null_and_unknown_arguments_alone()
    {
        var args = Args("""{"dryRun":true,"line":null,"newName":"Foo","somethingElse":"true"}""");

        ToolCallFilter.CoerceArguments(Schema, args).Should().BeEmpty();

        args["dryRun"].ValueKind.Should().Be(JsonValueKind.True);
        args["line"].ValueKind.Should().Be(JsonValueKind.Null);
        args["somethingElse"].GetString().Should().Be("true");
    }

    [Fact]
    public void Reports_each_unconvertible_argument_by_name_and_type()
    {
        var args = Args("""{"dryRun":"maybe","line":"twelve","addParameters":"[{name: x}]"}""");

        var problems = ToolCallFilter.CoerceArguments(Schema, args);

        problems.Should().HaveCount(3);
        problems.Should().Contain(p => p.StartsWith("'dryRun' expects boolean, got string"));
        problems.Should().Contain(p => p.StartsWith("'line' expects integer, got string"));
        problems.Should().Contain(p => p.StartsWith("'addParameters' expects array, got string"));
    }

    [Fact]
    public void Finds_the_nearest_solution_above_a_file_path()
    {
        using var tmp = new TempTree();
        var sln = tmp.File("repo/App.slnx");
        tmp.File("App.sln"); // further up: must not win
        var cs = tmp.File("repo/src/Core/Thing.cs");

        ToolCallFilter.FindSolutionCandidates(cs, tmp.Root).Should().Equal(sln);
    }

    [Fact]
    public void Accepts_a_solution_path_passed_as_filePath_and_missing_files()
    {
        using var tmp = new TempTree();
        var sln = tmp.File("repo/App.slnx");

        ToolCallFilter.FindSolutionCandidates(sln, tmp.Root).Should().Equal(sln);
        ToolCallFilter.FindSolutionCandidates(Path.Combine(tmp.Root, "repo/NotThere.cs"), tmp.Root)
            .Should().Equal(sln);
    }

    [Fact]
    public void Lists_every_solution_when_ambiguous()
    {
        using var tmp = new TempTree();
        var a = tmp.File("A.sln");
        var b = tmp.File("B.slnx");

        ToolCallFilter.FindSolutionCandidates(null, tmp.Root).Should().Equal(a, b);
    }

    [Fact]
    public void Falls_back_to_a_single_csproj_in_the_working_directory()
    {
        using var tmp = new TempTree();
        var proj = tmp.File("Only.csproj");
        ToolCallFilter.FindSolutionCandidates(null, tmp.Root).Should().Equal(proj);

        tmp.File("Second.csproj");
        ToolCallFilter.FindSolutionCandidates(null, tmp.Root).Should().BeEmpty();
    }

    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("roslynmcp-filter-").FullName;

        public string File(string rel)
        {
            var path = Path.Combine(Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, "");
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
