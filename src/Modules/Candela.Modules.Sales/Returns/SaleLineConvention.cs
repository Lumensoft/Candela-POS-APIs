namespace Candela.Modules.Sales.Returns;

/// <summary>
/// Reads a saved tblSalesLineItems row into the per-SINGLE-unit shape the web return screen works in.
///
/// The desktop app (and, since the sale save was aligned with it, the web app) write a line in Candela's
/// convention: a Pack line is priced per PACK (PriceForDiscount = rate × pack size, pro_vat and the
/// per-unit discount columns per pack), PriceAfterDiscount is WITHOUT tax with the tax kept separately in
/// pro_vat / VatChargedPerUnit, and the invoice adjustment is kept as a line total in product_adj_Discount
/// (included in PriceAfterDiscount only when the shop had IsSubtractAdjustmentDiscount on). Rows saved by
/// the web app before that alignment hold per-single values with the tax already inside PriceAfterDiscount;
/// they carry no VatChargedPerUnit, no per-pack price and no line adjustment, so they are left untouched.
///
/// The output (per single, tax included, adjustment excluded) is what ReturnCheckoutScreen / Sale.jsx
/// already expect; the invoice adjustment is still taken off separately from the header there.
///
/// A return never has a customer attached, so — exactly like Candela, which leaves CustDisc at 0 when it
/// loads the lines of a return — the original customer's discount no longer applies: it is added back to
/// the line and the VAT is recalculated on the larger base (VatFactor% × price after the discounts that
/// remain), instead of repeating the tax that was charged on the customer-discounted price.
/// </summary>
internal static class SaleLineConvention
{
    private const double Eps = 0.0005;

    public static void ToPerSingle(Dictionary<string, object?> row)
    {
        double qty          = Num(row, "quantity");
        double unitPrice    = Num(row, "unit_price");
        double priceForDisc = Num(row, "price_for_discount");
        double unitDisc     = Num(row, "unit_discount");
        double custRaw      = Num(row, "customer_discount_per_unit");
        double loyaltyRaw   = Num(row, "loyalty_cash_discount");
        double addTaxRaw    = Num(row, "additional_tax");
        double vatRaw       = Num(row, "vat_value");
        double padRaw       = Num(row, "price_after_discount");
        double vatCharged   = Num(row, "vat_charged_per_unit");
        double adjLine      = Num(row, "adjustment_on_line");
        double packSize     = Num(row, "pack_size");
        double vatFactor    = Num(row, "vat_factor");
        bool   isPackRow    = string.Equals(Str(row, "con_unit"), "Pack", StringComparison.OrdinalIgnoreCase);
        bool   priceInclVat = Num(row, "price_include_vat") != 0;
        bool   vatPercent   = string.Equals(Str(row, "vat_type"), "Percentage", StringComparison.OrdinalIgnoreCase)
                              && vatFactor > 0 && !priceInclVat;

        // Selling units per single: Candela prices a Pack line per pack, so its per-unit columns are × pack size.
        double f = (isPackRow && packSize > 0 && priceForDisc > unitPrice * (1 + 1e-9)) ? packSize : 1.0;

        bool taxSeparate  = vatCharged > 0;                       // tax lives in pro_vat, not in PriceAfterDiscount
        bool candelaStyle = taxSeparate || f > 1 || adjLine != 0; // anything the old web save never produced
        // A line with no tax has nothing hidden in PriceAfterDiscount, so the customer discount can be taken off
        // it whichever app wrote it; a taxed old-web line (tax inside PriceAfterDiscount) is left exactly as paid.
        if (!candelaStyle && !(vatRaw == 0 && custRaw != 0)) return;

        double mktPerUnit = qty != 0 ? Num(row, "marketing_on_line") * f / qty : 0;

        // Was the line's adjustment share folded into PriceAfterDiscount? Decided from the row itself.
        // PriceAfterDiscount is the VAT base, i.e. (price − unit disc − customer disc [− marketing share, when
        // the shop's IsSubtractMarketingDiscount is on] [+ adjustment share, when IsSubtractAdjustmentDiscount
        // is on]) per selling unit. Both the marketing and adjustment columns are line totals.
        double padNoAdj = padRaw;
        if (adjLine != 0 && qty != 0)
        {
            double basePad    = priceForDisc - unitDisc * f - custRaw;
            double adjPerUnit = adjLine * f / qty;
            foreach (double mkt in new[] { 0.0, mktPerUnit })
            {
                if (Math.Abs(padRaw - (basePad - mkt + adjPerUnit)) < Eps
                    && Math.Abs(padRaw - (basePad - mkt)) >= Eps)
                {
                    padNoAdj = padRaw - adjPerUnit;
                    break;
                }
            }
        }

        // No customer on a return: put the customer discount back — but only if it was actually part of the
        // stored price (a shop whose IsSubtractCustomerDiscount is off never took it off PriceAfterDiscount).
        double tax = vatRaw;
        if (custRaw != 0 && !priceInclVat)
        {
            double basePadUnit = priceForDisc - unitDisc * f;
            bool   custIncluded = false;
            foreach (double mkt in new[] { 0.0, mktPerUnit })
                if (Math.Abs(padNoAdj - (basePadUnit - custRaw - mkt)) < Eps) { custIncluded = true; break; }

            if (custIncluded)
            {
                // The stored tax was VatFactor% of the stored base (which includes any adjustment share);
                // recompute it on that base plus the customer discount. A fixed-amount VAT does not change.
                bool taxOnBase = vatPercent && Math.Abs(vatRaw - padRaw * vatFactor / 100.0) < 0.01;
                if (taxOnBase) tax = (padRaw + custRaw) * vatFactor / 100.0;
                padNoAdj += custRaw;
            }
        }

        // Per single, tax included, adjustment excluded, no customer discount.
        row["price_after_discount"]       = (padNoAdj + (taxSeparate ? tax : 0.0)) / f;
        row["vat_value"]                  = tax / f;
        row["customer_discount_per_unit"] = 0.0;
        row["loyalty_cash_discount"]      = 0.0;
        row["customer_discount"]          = 0.0;
        row["additional_tax"]             = addTaxRaw / f;
        // The line's share of the invoice adjustment per single (the column is a line total) — Candela
        // refunds this × the quantity returned, independent of any price change above.
        row["adjustment_per_unit"]        = qty != 0 ? adjLine / qty : 0.0;
    }

    // Column aliases differ in case between the validate and preview queries (Unit_price / unit_price).
    private static object? Find(Dictionary<string, object?> row, string key)
    {
        foreach (var kv in row)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }

    private static double Num(Dictionary<string, object?> row, string key)
    {
        var v = Find(row, key);
        return v is null || v is DBNull ? 0.0 : Convert.ToDouble(v);
    }

    private static string Str(Dictionary<string, object?> row, string key)
    {
        var v = Find(row, key);
        return v is null || v is DBNull ? "" : Convert.ToString(v) ?? "";
    }
}
