namespace Candela.Modules.Configuration.Products.Dtos;

/// <summary>
/// One row of any of Product Definition's dropdown sources (Category, Supplier, Acquire
/// Type, …). They are all the same shape on the wire — an id and a display name — so one
/// type serves all of them instead of a bespoke response per dropdown.
///
/// Id is always "the value the Product record round-trips" — for most lookups that is a
/// real identity column, but for AcquireType/PurchaseType/ManufactureType the desktop
/// switched to storing their Code column instead of the id (frmDefProduct.vb, CR#7615);
/// ProductLookupsRepository maps that quirk away here so nothing above this layer has to
/// know about it.
/// </summary>
public sealed class LookupItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}
