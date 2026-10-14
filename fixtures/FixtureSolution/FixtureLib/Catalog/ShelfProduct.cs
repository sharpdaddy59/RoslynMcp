namespace FixtureLib.Catalog.Nested;

/// <summary>
/// Same simple name as FixtureLib.Catalog.Product on purpose: moving that type into
/// FixtureLib.Catalog.Nested must be refused as a name collision.
/// </summary>
public sealed class Product
{
    public int Slot { get; init; }
}
