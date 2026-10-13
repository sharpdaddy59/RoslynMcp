namespace FixtureLib;

// find_callers fixture (incoming call hierarchy). Answer key lives in
// tests/RoslynMcp.Tests/FindCallersTests.cs; usages are in FixtureApp/PipelineUsages.cs.
// Line numbers matter: keep edits below the last member or update the tests.

public interface IStage { string Run(string s); }

public class Lower : IStage { public string Run(string s) => s.ToLowerInvariant(); }

public class Trim : IStage { public virtual string Run(string s) => s.Trim(); }

public class HardTrim : Trim { public override string Run(string s) => base.Run(s).Replace(" ", ""); }

public static class Pipeline
{
    // Interface dispatch: callers of IStage.Run reach every implementation.
    public static string Execute(IStage stage, string s) => stage.Run(s);

    public static string Twice(IStage stage, string s) => Execute(stage, Execute(stage, s));

    // Direct recursion: find_callers must report a cycle leaf, not loop.
    public static int Depth(int n) => n <= 0 ? 0 : 1 + Depth(n - 1);

    // Diamond: Core is reached from Diamond through both Left and Right.
    public static string Diamond(string s) => Left(s) + Right(s);

    static string Left(string s) => Core(s);

    static string Right(string s) => Core(s);

    static string Core(string s) => s;
}
