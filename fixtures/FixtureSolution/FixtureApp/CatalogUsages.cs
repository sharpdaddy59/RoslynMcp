using FixtureLib.Catalog;
using static FixtureLib.Catalog.Nested.Shelf;
using CatalogPrice = FixtureLib.Catalog.Price;

namespace FixtureApp;

/// <summary>
/// move_type_to_namespace / rename_namespace fixture (answer key in
/// tests/RoslynMcp.Tests/NamespaceToolsTests.cs): a using, a using static, an alias,
/// fully-qualified references, a cref (<see cref="FixtureLib.Catalog.Sku"/>) and a
/// simple-name reference into FixtureLib.Catalog.
/// </summary>
public static class CatalogUsages
{
    public static string Exercise()
    {
        var product = new Product { Name = "widget" };
        FixtureLib.Catalog.Sku sku = new FixtureLib.Catalog.Sku { Code = "W-1", Item = product };
        Sku? none = null;
        CatalogPrice price = sku.Cost;
        var shelves = FixtureLib.Catalog.Nested.Shelf.Capacity + Capacity;
        return $"{product.Name} {sku.Origin()} {price.Amount} {shelves} {price.For("W-2").Code} {none?.Code}";
    }
}
