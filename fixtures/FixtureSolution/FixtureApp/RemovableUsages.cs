using FixtureLib;

/// <summary>delete_member fixture: the only callers of Removable.Used() and IRemovable.Ping().</summary>
public static class RemovableUsages
{
    public static int Exercise()
    {
        var removable = new Removable();
        IRemovable contract = removable;
        contract.Ping();
        return removable.Used();
    }
}
