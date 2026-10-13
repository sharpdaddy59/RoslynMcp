namespace FixtureLib;

/// <summary>
/// change_signature fixture (see tests/RoslynMcp.Tests/ChangeSignatureTests.cs and the
/// usages in FixtureApp/ShippingUsages.cs). The two-argument Quote forms a cascade of
/// exactly THREE declarations: this interface member, Shipper.Quote (virtual), and
/// ExpressShipper.Quote (override). Changing its signature must rewrite all three plus
/// every call site - and must never touch the unrelated one-argument Quote overload.
/// </summary>
public interface IShipper
{
    decimal Quote(string destination, int weightKg);
}

public class Shipper : IShipper
{
    // Reads weightKg in its body: removing that parameter must be REJECTED by the compile gate (CS0103).
    public virtual decimal Quote(string destination, int weightKg) => weightKg * 2.5m + destination.Length;

    // Unrelated overload with the same name: must never change, byte for byte.
    public decimal Quote(string destination) => destination.Length * 1.5m;

    // `reason` is never read: removing it is a LEGAL signature change that applies cleanly.
    public decimal Surcharge(decimal baseQuote, string reason) => baseQuote * 0.1m;

    // Used as a method group in ShippingUsages (Func<string, int, decimal> f = shipper.Estimate):
    // change_signature must refuse to touch it and report that location. Its body is also a
    // call site of the Quote cascade inside FixtureLib (cross-project rewriting).
    public decimal Estimate(string destination, int weightKg) => Quote(destination, weightKg) + 1;
}

public class ExpressShipper : Shipper
{
    // base.Quote(...) is a call site of the cascade and must be rewritten along with the override.
    public override decimal Quote(string destination, int weightKg) => base.Quote(destination, weightKg) * 2;
}
