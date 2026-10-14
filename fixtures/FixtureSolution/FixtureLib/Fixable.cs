using System.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace FixtureLib;

// v2.2 apply_code_fix fixture: three unnecessary usings (CS8019: System.Text,
// System.Collections.Concurrent, System.Net) and one used one (System.Diagnostics).
public class Fixable
{
    public long Time(Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        return stopwatch.ElapsedMilliseconds;
    }
}
