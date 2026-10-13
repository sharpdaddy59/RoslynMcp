using FixtureLib;

namespace FixtureApp;

// find_callers fixture: the outermost callers of FixtureLib/Pipeline.cs.
public static class PipelineUsages
{
    public static string Exercise()
    {
        var twice = Pipeline.Twice(new HardTrim(), " A B ");
        var diamond = Pipeline.Diamond("d");
        var depth = Pipeline.Depth(3);
        var lower = new Lower().Run("X");
        return $"{twice}{diamond}{depth}{lower}";
    }
}
