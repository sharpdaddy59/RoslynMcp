using FixtureLib;

namespace FixtureApp;

/// <summary>
/// change_signature fixture answer key (see FixtureLib/Shipping.cs). The two-argument Quote
/// cascade is called exactly THREE times here: through the interface (positional), through
/// the base type (positional), and through the derived type with NAMED arguments in swapped
/// order. The one-argument Quote overload is called once and must never change. Estimate is
/// both called normally and used as a method group - the latter is the refusal case.
/// </summary>
public static class ShippingUsages
{
    public static decimal Exercise()
    {
        IShipper viaInterface = new Shipper();
        Shipper viaBase = new ExpressShipper();
        ExpressShipper viaDerived = new ExpressShipper();

        var total = 0m;
        total += viaInterface.Quote("Austin", 2);
        total += viaBase.Quote("Dallas", 5);
        total += viaDerived.Quote(weightKg: 3, destination: "Houston");
        total += viaBase.Quote("Waco");
        total += viaBase.Surcharge(total, "fuel");
        total += viaBase.Estimate("Tyler", 4);
        Func<string, int, decimal> estimate = viaBase.Estimate;
        total += estimate("Laredo", 1);
        return total;
    }
}
