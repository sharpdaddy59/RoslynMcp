namespace FixtureLib.Catalog;

/// <summary>Second part of Sku (see Catalog.cs).</summary>
public partial class Sku
{
    public Price Cost { get; init; } = new Price(0m);
}

/// <summary>A price; it stays in FixtureLib.Catalog when Sku moves.</summary>
public record Price(decimal Amount)
{
    public Sku For(string code) => new Sku { Code = code, Cost = this };
}
