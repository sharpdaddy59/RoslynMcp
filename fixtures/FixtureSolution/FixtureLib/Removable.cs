namespace FixtureLib;

public interface IRemovable
{
    void Ping();
}

/// <summary>
/// delete_member fixture. Answer key: _b, DeadHelper and LooksDead have zero references
/// and are deletable; LiveHelper is called by Used() and must be refused without force
/// (and rejected by the compile gate with force); Ping implements IRemovable.Ping and must
/// be refused as a contract break.
/// </summary>
public class Removable : IRemovable
{
    private int _a = 1, _b = 2;

    /// <summary>Unused private helper - the find_unused_members to delete_member round trip.</summary>
    private string DeadHelper() => "dead";

    private string LiveHelper() => "live";

    public int Used() => _a + LiveHelper().Length;

    public string LooksDead() => "no references anywhere in the solution";

    public void Ping() => Console.WriteLine("pong");
}
