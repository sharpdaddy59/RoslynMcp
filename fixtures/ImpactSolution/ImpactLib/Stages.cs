namespace ImpactLib;

// analyze_change_impact fixture. Answer key lives in
// tests/RoslynMcp.Tests/AnalyzeChangeImpactTests.cs. Line numbers matter.

public interface IStage { string Run(string s); }

public class UpperStage : IStage { public string Run(string s) => Helper.Shout(s); }

public class TrimStage : IStage { public string Run(string s) => s.Trim(); }

// Used only from UpperStage.Run.
internal static class Helper { public static string Shout(string s) => s.ToUpperInvariant() + "!"; }
