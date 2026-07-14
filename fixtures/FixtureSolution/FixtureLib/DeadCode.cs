namespace FixtureLib;

public interface ITrigger
{
    void Fire();
}

/// <summary>
/// find_unused_members fixture: an answer key where the naive "zero references" signal
/// alone would be wrong for three of these five members.
/// </summary>
public class DeadCode : ITrigger
{
    // (a) HIGH: genuinely unused private method.
    private string UnusedHelper() => "dead";

    // (e) HIGH: genuinely unused private field.
    private readonly int _unusedField = 42;

    // (b) LOW, not HIGH: looks unused, but this method and its class are public - an
    // external assembly could be calling it even though nothing in this solution does.
    public string LooksUnusedButPublic() => "maybe used externally";

    // (c) must NOT be reported at all: only reachable through the ITrigger interface it
    // implements - an interface implementation is never "dead", it's dispatched through
    // the abstraction.
    void ITrigger.Fire() => Console.WriteLine("fired");

    // (d) must NOT be reported at all: carries an attribute - reflection-based
    // frameworks (serializers, DI containers) can reach this even with zero textual refs.
    [Obsolete]
    private void Deprecated()
    {
    }
}
