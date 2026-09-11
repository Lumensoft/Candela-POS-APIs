using Candela.Modules.Sales.Invoices.Dtos;
using Candela.Platform.Legacy;

namespace Candela.Modules.Sales.Invoices;

/// <summary>
/// Sales: the search grid and detail panel run here on SQL; the four writes — post a
/// sale, update one, void one, hard-delete one — go through SaleAndReturnDAL under the
/// same idempotency, credit-check, coupon, below-cost and adjustment-limit validations
/// the desktop applies, so the whole of each is forwarded to the legacy host and its
/// reply is passed back byte-for-byte.
/// </summary>
public interface ISalesService
{
    Task<SaleSearchResponse> SearchAsync(int shopId, int page, int pageSize,
        string? q, string? from, string? to, string? invoiceNo, CancellationToken ct);

    /// <summary>Null when the sale is not this shop's — the caller answers 404.</summary>
    Task<SaleDetailResponse?> GetSaleAsync(int saleId, int shopId, CancellationToken ct);

    /// <summary>POST /api/sales — forwarded to POST /legacy/sales.</summary>
    Task<LegacyRawResponse> ForwardPostAsync(string rawJsonBody, CancellationToken ct);

    /// <summary>PUT /api/sales/{id} — forwarded to PUT /legacy/sales/{id}.</summary>
    Task<LegacyRawResponse> ForwardUpdateAsync(int id, string rawJsonBody, CancellationToken ct);

    /// <summary>DELETE /api/sales/{id} — forwarded to DELETE /legacy/sales/{id} (soft void).</summary>
    Task<LegacyRawResponse> ForwardVoidAsync(int id, CancellationToken ct);

    /// <summary>DELETE /api/sales/{id}/hard — forwarded to DELETE /legacy/sales/{id}/hard.</summary>
    Task<LegacyRawResponse> ForwardHardDeleteAsync(int id, CancellationToken ct);
}
