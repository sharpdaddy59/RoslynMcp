using ImpactLib;
using Xunit;

namespace ImpactTests;

// analyze_change_impact fixture. Answer key lives in
// tests/RoslynMcp.Tests/AnalyzeChangeImpactTests.cs. Line numbers matter.

public class PipelineTests
{
    [Fact]
    public void ExecuteUppercases() => Assert.Equal("HI!", Pipeline.Execute(new UpperStage(), "hi"));

    [Fact]
    public void ExecuteViaHelper() => Assert.Equal("a", RunTrim(" a "));

    private static string RunTrim(string s) => Pipeline.Execute(new TrimStage(), s);

    [Theory]
    [InlineData(" a ")]
    public void TrimStageTrims(string input) => Assert.Equal("a", new TrimStage().Run(input));

    [Fact]
    public void ConfigHasDefaultName() => Assert.Equal("default", new Config().Name);

    // Test-shaped, but carries no test attribute: must not be reported as a test.
    public void NotATest() => Pipeline.Execute(new TrimStage(), "x");
}
