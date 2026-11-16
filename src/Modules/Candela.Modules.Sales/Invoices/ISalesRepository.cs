using Candela.Modules.Sales.Invoices.Dtos;

namespace Candela.Modules.Sales.Invoices;

/// <summary>
/// The read side of sales — the invoice search grid and the invoice detail panel.
///
/// The net48 SalesController did both with plain SQL. The four write endpoints (void,
/// hard delete, update, post) all go through SaleAndReturnDAL, so they stay in the legacy
/// host and SalesService forwards to them.
/// </summary>
public interface ISalesRepository
{
    /// <summary>
    /// One page of invoice summary rows for this shop, plus the filtered total. <paramref name="posCode"/>
    /// limits it to that POS's invoices; blank means every POS of the shop (the desktop's "All POS" box).
    /// </summary>
    Task<SaleSearchResponse> SearchAsync(int shopId, string? posCode, int page, int pageSize,
        string? q, string? from, string? to, string? invoiceNo, CancellationToken ct);

    /// <summary>
    /// Invoice header + line items for the detail panel, or null when the sale is not
    /// this shop's — the caller turns null into the net48 404.
    /// </summary>
    Task<SaleDetailResponse?> GetSaleAsync(int saleId, int shopId, CancellationToken ct);
}
