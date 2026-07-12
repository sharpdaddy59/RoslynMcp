namespace FixtureLib;

public class ShoutingGreeter : IGreeter
{
    public string Greet(string name)
    {
        return $"HELLO, {name.ToUpperInvariant()}!";
    }
}
