using System.Globalization;

namespace PesaCore.Web.Client.Services;

// Presentation helper for monetary figures. The seed data uses bank-style
// account numbers (EQB001…) and KES-scale balances, so we format as KES with
// thousands separators and no decimals (whole-shilling display), using an
// invariant grouping so rendering is deterministic across browser locales.
public static class Money
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Kes(decimal amount) =>
        "KES " + amount.ToString("#,##0", Inv);

    // Split for typographic emphasis: large integer part + small fractional/suffix.
    public static (string whole, string cents) Parts(decimal amount)
    {
        var whole = decimal.Truncate(amount).ToString("#,##0", Inv);
        var cents = Math.Abs(amount - decimal.Truncate(amount))
            .ToString("0.00", Inv)[1..]; // ".00"
        return (whole, cents);
    }
}
