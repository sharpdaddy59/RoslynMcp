using System.Diagnostics;
using System.Text;

namespace FixtureLib;

// v2.2 apply_code_fix fixture: deliberately planted CS0246 - Widget lives in FixtureLib.Imports
// and the using is missing. apply_code_fix CS0246 must add exactly `using FixtureLib.Imports;`.
public class NeedsImport
{
    public string Describe(Widget widget)
    {
        var stopwatch = Stopwatch.StartNew();
        var builder = new StringBuilder(widget.Name);
        builder.Append(stopwatch.ElapsedTicks >= 0 ? "!" : "?");
        return builder.ToString();
    }
}
