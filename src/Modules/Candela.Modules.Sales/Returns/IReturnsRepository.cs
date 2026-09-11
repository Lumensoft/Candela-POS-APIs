namespace Candela.Modules.Sales.Returns;

/// <summary>
/// The read side of returns — invoice validation and the preview re-pricing.
///
/// The net48 ReturnsController split three ways: <c>validate</c> and <c>preview</c> were
/// plain SQL (two of validate's calls went through SaleAndReturnDAL, but both are
/// single-value reads with no side effects — inlined here the same way QuoteController
/// inlined a dozen others), while <c>PostReturn</c> ran SaleAndReturnDAL.Add under a
/// per-invoice lock. PostReturn is NOT here: it stays whole in the legacy host so the
/// lock, the idempotency slot and the DAL write stay in one process.
/// </summary>
public interface IReturnsRepository
{
    /// <summary>
    /// SaleAndReturnDAL.IsValidInvoiceForReturn (DAL:433) inlined — true when the invoice
    /// exists for this shop and has at least one non-return line.
    /// </summary>
    Task<bool> IsValidInvoiceForReturnAsync(int shopId, int invoiceNo, CancellationToken ct);

    /// <summary>
    /// SaleAndReturnDAL.getCustomerAgainstInvoice (DAL:452) inlined — the formatted
    /// customer code for the invoice's member, or "" when the sale had no member.
    /// </summary>
    Task<string> GetCustomerAgainstInvoiceAsync(int shopId, int invoiceNo, CancellationToken ct);

    /// <summary>The invoice header for the return screen, or null when the sale is not this shop's.</summary>
    Task<Dictionary<string, object?>?> QuerySaleHeaderAsync(int shopId, int invoiceNo, CancellationToken ct);

    /// <summary>The invoice's original (non-return) line items.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> QuerySaleItemsAsync(int shopId, int invoiceNo, CancellationToken ct);

    /// <summary>Original line rows for the given products, keyed by product_item_id, for the preview.</summary>
    Task<IReadOnlyDictionary<int, Dictionary<string, object?>>> QuerySaleItemsForPreviewAsync(
        int shopId, int saleId, IReadOnlyList<int> productItemIds, CancellationToken ct);

    /// <summary>tblDefDiscounts.discount_duration for a discount, or 0.</summary>
    Task<double> GetDiscountDurationAsync(int discountId, CancellationToken ct);
}
