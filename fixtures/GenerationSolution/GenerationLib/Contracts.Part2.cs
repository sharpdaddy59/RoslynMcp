namespace GenerationLib;

// Second part of Ledger: it carries the IAuditable base-list entry, so the "first
// declaration" (where generated stubs go) is not the part that names the interface.
public partial class Ledger : IAuditable
{
    public int Total { get; private set; }
}
