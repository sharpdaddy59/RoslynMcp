namespace FixtureLib;

public class Greeter : IGreeter
{
    public string Greet(string name)
    {
        return $"Hello, {name}!";
    }
}
