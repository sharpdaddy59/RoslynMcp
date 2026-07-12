namespace FixtureLib;

/// <summary>
/// Fixture answer key: exactly TWO implementations exist (Greeter, ShoutingGreeter).
/// Tests assert these counts - update them if you change this file's contract.
/// </summary>
public interface IGreeter
{
    string Greet(string name);
}
