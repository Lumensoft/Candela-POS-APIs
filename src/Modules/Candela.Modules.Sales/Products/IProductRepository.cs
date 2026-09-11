namespace Candela.Modules.Sales.Products;

/// <summary>
/// Data access for the Products slice.
///
/// Named Repository because that is the term the team already uses, but note it is a
/// plain data-access class, not the full repository pattern: no unit of work, no
/// change tracking, no aggregate roots. Dapper is already the abstraction over ADO.NET,
/// so putting another one on top would be ceremony.
///
/// It exists as an interface for one practical reason: the controller can be unit
/// tested without a database. The .NET Framework host could not be — it had 87
/// hand-rolled `new SqlConnection(...)` calls with nothing to substitute.
///
/// Reads go straight to SQL from here. Writes that must keep Candela's DataLog,
/// inventory posting and accounting correct do NOT belong here — they go through
/// the legacy host client instead.
/// </summary>
public interface IProductRepository
{
    /// <summary>Substitute items configured against a product, for this shop.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetAlternatesAsync(int productItemId, int shopId, CancellationToken ct);

    /// <summary>Batches with stock remaining, oldest expiry first.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetBatchesAsync(int productItemId, int shopId, CancellationToken ct);
}
