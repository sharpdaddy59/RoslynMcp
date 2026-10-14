namespace FixtureLib;

// find_unused_parameters fixture (see tests/RoslynMcp.Tests/SymbolInfoToolsTests.cs).
// Answer key for THIS file - reported (tier):
//   ParamsSample(label, unusedSeed)       -> unusedSeed  (low: public ctor)
//   OneUsedOneUnused(count, unused)       -> unused      (low)
//   OnSomethingHappened(sender, e)        -> sender, e   (low, conventional: true)
//   WithLocalFunction > Twice(x, ignored) -> ignored     (high: local function)
//   PrivateHelper(a, b)                   -> b           (high: private)
//   Scaled(this sample, factor)           -> factor      (low)
//   ParamsBase.Score(points, bonus)       -> bonus       (low: virtual)
// Never reported: nameof(value) is a use; PassThrough(amount) passes it on (a use);
// the discard '_' (excluded: discard); 'this' of IsReady (excluded: extensionThis);
// Transform is used as a method group (excluded: usedAsDelegate);
// ParamsBase.Handle has no body (excluded: noBody); ParamsDerived overrides (excluded: override);
// ParamsSink.Accept implements IParamsSink (excluded: interfaceImplementation);
// ParamsTool.Main(string[] args) (excluded: entryPoint).
public class ParamsSample
{
    public ParamsSample(string label, int unusedSeed)
    {
        Label = label;
    }

    public string Label { get; }

    public int OneUsedOneUnused(int count, string unused) => count * 2;

    public string OnlyInNameof(int value) => nameof(value);

    public int PassThrough(int amount) => OneUsedOneUnused(amount, Label);

    public int WithDiscard(int keep, int _) => keep;

    public void OnSomethingHappened(object? sender, EventArgs e)
    {
    }

    public int WithLocalFunction(int seed)
    {
        return Twice(seed, 3);

        static int Twice(int x, int ignored) => x * 2;
    }

    public int Transform(int input, int unusedFactor) => input + 1;

    public Func<int, int, int> GetTransform() => Transform;

    public int CallsPrivateHelper() => PrivateHelper(1, 2);

    private static int PrivateHelper(int a, int b) => a;
}

public static class ParamsExtensions
{
    public static bool IsReady(this ParamsSample sample) => true;

    public static int Scaled(this ParamsSample sample, int factor) => sample.Label.Length;
}

public abstract class ParamsBase
{
    public abstract void Handle(int code);

    public virtual int Score(int points, int bonus) => points;
}

public class ParamsDerived : ParamsBase
{
    public override void Handle(int code)
    {
    }

    public override int Score(int points, int bonus) => bonus;
}

public interface IParamsSink
{
    void Accept(string payload, int priority);
}

public class ParamsSink : IParamsSink
{
    public string Last { get; private set; } = "";

    public void Accept(string payload, int priority) => Last = payload;
}

public static class ParamsTool
{
    public static void Main(string[] args)
    {
    }
}
