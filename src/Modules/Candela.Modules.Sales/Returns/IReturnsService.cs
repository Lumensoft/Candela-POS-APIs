using Candela.Modules.Sales.Returns.Dtos;
using Candela.Platform.Legacy;

namespace Candela.Modules.Sales.Returns;

/// <summary>
/// Returns processing.
///
/// <c>validate</c> and <c>preview</c> run entirely here on SQL. <c>PostReturn</c> is
/// forwarded whole to the legacy host: it holds a per-invoice lock across the quantity
/// check and SaleAndReturnDAL.Add, and it has a recovery path that treats a
/// SaleID-was-assigned secondary failure as success — splitting any of that across two
/// processes would change behaviour, so the entire method stays where the DAL is, and its
/// response comes back byte-for-byte.
/// </summary>
public interface IReturnsService
{
    /// <summary>
    /// POST /api/returns/validate — is the invoice returnable, and what did it contain.
    /// Throws BusinessRuleException (422) with the net48 wording when it is not.
    /// </summary>
    Task<ValidateReturnResponse> ValidateAsync(int invoiceNo, int shopId, int sourceShopId,
        CancellationToken ct);

    /// <summary>
    /// POST /api/returns/preview — re-evaluate qty-threshold discounts for a proposed
    /// return cart. Returns the corrected item rows.
    /// </summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> PreviewAsync(ReturnPreviewRequest req,
        int shopId, int sourceShopId, CancellationToken ct);

    /// <summary>
    /// POST /api/returns — forwarded verbatim to /legacy/returns, which runs the whole
    /// original PostReturn. The status and JSON body are returned exactly as the legacy
    /// host produced them.
    /// </summary>
    Task<LegacyRawResponse> ForwardPostReturnAsync(string rawJsonBody, CancellationToken ct);
}
