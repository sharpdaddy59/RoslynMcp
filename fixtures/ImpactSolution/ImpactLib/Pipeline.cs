namespace ImpactLib;

// analyze_change_impact fixture. Answer key lives in
// tests/RoslynMcp.Tests/AnalyzeChangeImpactTests.cs. Line numbers matter.

public static class Pipeline
{
    // Interface dispatch: reaches every IStage implementation.
    public static string Execute(IStage stage, string s) => stage.Run(s);

    // References Config in its signature without calling anything on Config's type.
    public static string Describe(Config config) => config.Name;
}

public sealed class Config { public string Name { get; set; } = "default"; }

// Zero dependents, zero tests.
public static class Orphan { public static void Nothing() { } }
