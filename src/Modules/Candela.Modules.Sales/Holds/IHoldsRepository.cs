namespace Candela.Modules.Sales.Holds;

/// <summary>
/// Parked-cart storage.
///
/// GET and DELETE go straight to SQL — the net48 HoldsController already did them that
/// way, not through the DAL, because the holding tables (tblSalesHolding /
/// tblSalesLineItemsHolding) are shop-local scratch and are not replicated.
///
/// Parking a cart is the exception: it runs through SaleAndReturnDAL.AddToHold, so that
/// stays in the legacy host and this repository does not do the write.
/// </summary>
public interface IHoldsRepository
{
    /// <summary>
    /// Every parked cart for the shop, newest first, each with its line items attached
    /// under an "items" key — the exact shape the net48 endpoint produced.
    /// </summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetHoldsAsync(int shopId, CancellationToken ct);

    /// <summary>
    /// Discards a parked cart: its lines, then its header, in one transaction.
    /// Returns false when no header matched (id/shop), which the caller turns into 404 —
    /// same as the net48 endpoint's "rows == 0 -> rollback -> NotFound".
    /// </summary>
    Task<bool> DeleteHoldAsync(int holdId, int shopId, CancellationToken ct);
}
