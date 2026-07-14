namespace FixtureLib;

/// <summary>
/// extract_interface fixture: only Name and DoWork are eligible public instance members.
/// Count is static and Secret is internal - neither must ever appear in the generated
/// interface.
/// </summary>
public class Exportable
{
    public static int Count;

    public string Name { get; set; } = "";

    /// <summary>Combines the name with an amount.</summary>
    public string DoWork(int amount) => $"{Name}:{amount}";

    internal string Secret() => "shh";
}
