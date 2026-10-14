namespace FixtureLib.Catalog
{
    // Lives in FixtureLib.Catalog on purpose
    /// <summary>A catalog product (namespace-tools fixture; see tests/RoslynMcp.Tests/NamespaceToolsTests.cs).</summary>
    public class Product
    {
        public string Name { get; init; } = "";
    }

    /// <summary>First part of a partial type split across Catalog.cs and Catalog.Part2.cs.</summary>
    public partial class Sku
    {
        public string Code { get; init; } = "";

        public Product? Item { get; init; }

        public string Origin() => "FixtureLib.Catalog";
    }
}
