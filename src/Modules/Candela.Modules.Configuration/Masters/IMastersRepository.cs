namespace Candela.Modules.Configuration.Masters;

/// <summary>
/// The master-data reads the tablet syncs into IndexedDB on login, plus the small
/// lookups the sale screen asks for on demand.
///
/// Every method is a SELECT — the net48 MastersController had no writes at all — so the
/// whole slice moved to .NET 10 in one piece with nothing left behind in the legacy host.
///
/// They all return rows as dictionaries rather than typed DTOs, because the column
/// aliases in each query ARE the contract: the tablet stores these objects in IndexedDB
/// key by key. A typed model would have to restate 30-odd column names per query and
/// would drift from the SQL the first time a column is added.
/// </summary>
public interface IMastersRepository
{
    /// <summary>Full product catalogue for the shop, or only rows touched since a timestamp.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetProductsAsync(int shopId, DateTime? since, CancellationToken ct);

    /// <summary>Scan-bar lookup: one product, product_code match ranked above barcode match.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> ScanProductAsync(int shopId, string q, CancellationToken ct);

    /// <summary>Older barcode/code search kept for backward compatibility. Up to 10 rows.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> SearchProductAsync(int shopId, string? barcode, string? code, CancellationToken ct);

    /// <summary>Customers visible to this shop, with live credit outstanding. q= switches to search mode.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetCustomersAsync(int shopId, DateTime? since, string? q, CancellationToken ct);

    /// <summary>Salespeople for the salesperson-assign modal.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetEmployeesAsync(int shopId, DateTime? since, CancellationToken ct);

    /// <summary>Card types this shop accepts.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetCreditCardsAsync(int shopId, DateTime? since, CancellationToken ct);

    /// <summary>Customer tiers — drives the discount branch in /quote.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetMemberTypesAsync(DateTime? since, CancellationToken ct);

    /// <summary>Customer groups for the Add Customer dropdown.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetCustomerGroupsAsync(CancellationToken ct);

    /// <summary>Mobile payment providers and their enabled flags.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetPaymentMethodsAsync(CancellationToken ct);

    /// <summary>Shop config and global config as key/value rows, each tagged with its source.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetConfigAsync(int shopId, CancellationToken ct);

    /// <summary>Product departments for the category tabs.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetLineItemsAsync(CancellationToken ct);

    /// <summary>Batches with stock left, in FEFO order.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetBatchesAsync(int productItemId, CancellationToken ct);

    /// <summary>Default child components of an assembly product, with current retail prices.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetAssemblyItemsAsync(int productItemId, int shopId, CancellationToken ct);

    /// <summary>Products this shop may not sell.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetBlockedProductsAsync(int shopId, CancellationToken ct);

    /// <summary>Currently-running cross-sell promotions.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetPromotionProductsAsync(CancellationToken ct);

    /// <summary>Products on a Stock Transfer Request addressed to this shop.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetStrProductsAsync(string strNo, int shopId, CancellationToken ct);

    /// <summary>All shops, for the cross-shop return modal.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetShopsAsync(CancellationToken ct);

    /// <summary>Shop departments, for the patient/prescription modal.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetDepartmentsAsync(int shopId, CancellationToken ct);

    /// <summary>Adjustment reasons, shown when a non-zero adjustment is entered.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetAdjustmentReasonsAsync(CancellationToken ct);

    /// <summary>Return reasons, required per line when EnforceSaleReturnReason is on.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetReturnReasonsAsync(CancellationToken ct);
}
