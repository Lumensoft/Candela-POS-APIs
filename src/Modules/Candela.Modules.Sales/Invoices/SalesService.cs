using Candela.Modules.Sales.Invoices.Dtos;
using Candela.Platform.Legacy;

namespace Candela.Modules.Sales.Invoices;

/// <summary>
/// Ported from the net48 SalesController. The two reads keep their SQL; the four writes
/// are forwarded whole — DELETE carries no body, PUT and POST carry the request as-is.
/// </summary>
public sealed class SalesService(ISalesRepository repo, ILegacyHostClient legacy) : ISalesService
{
    public Task<SaleSearchResponse> SearchAsync(int shopId, int page, int pageSize,
        string? q, string? from, string? to, string? invoiceNo, CancellationToken ct)
        => repo.SearchAsync(shopId, page, pageSize, q, from, to, invoiceNo, ct);

    public Task<SaleDetailResponse?> GetSaleAsync(int saleId, int shopId, CancellationToken ct)
        => repo.GetSaleAsync(saleId, shopId, ct);

    public Task<LegacyRawResponse> ForwardPostAsync(string rawJsonBody, CancellationToken ct)
        => legacy.SendRawAsync(HttpMethod.Post, "sales", rawJsonBody, ct);

    public Task<LegacyRawResponse> ForwardUpdateAsync(int id, string rawJsonBody, CancellationToken ct)
        => legacy.SendRawAsync(HttpMethod.Put, $"sales/{id}", rawJsonBody, ct);

    public Task<LegacyRawResponse> ForwardVoidAsync(int id, CancellationToken ct)
        => legacy.SendRawAsync(HttpMethod.Delete, $"sales/{id}", null, ct);

    public Task<LegacyRawResponse> ForwardHardDeleteAsync(int id, CancellationToken ct)
        => legacy.SendRawAsync(HttpMethod.Delete, $"sales/{id}/hard", null, ct);
}
