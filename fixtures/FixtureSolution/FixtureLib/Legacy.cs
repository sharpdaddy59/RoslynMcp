namespace FixtureLib;

public class Legacy
{
    public int Compute()
    {
        // Deliberately planted CS0219 (assigned but unused) - the diagnostics
        // test asserts this exact warning is found at this location
        var unusedButAssigned = 42;
        return 7;
    }
}
