namespace Candela.Modules.Configuration.Products.Dtos;

/// <summary>The full Core Product record, as returned by GET .../products/{id}.</summary>
public sealed class ProductResponse
{
    public int ProductId { get; set; }
    public int LineItemId { get; set; }
    public string ProductCode { get; set; } = "";
    public string ItemName { get; set; } = "";

    public int CategoryId { get; set; }
    public int SubCategoryId { get; set; }
    public int ProductGroupId { get; set; }
    public int Variable1Id { get; set; }
    public int Variable2Id { get; set; }
    public int Variable3Id { get; set; }
    public int Variable4Id { get; set; }
    public int Variable5Id { get; set; }
    public int CalendarSeasonId { get; set; }

    public int SupplierId { get; set; }
    public int AcquireType { get; set; }
    public int PurchaseType { get; set; }
    public int ManufactureType { get; set; }

    public int PurConUnit { get; set; }
    public double PurConFactor { get; set; }

    public int SaleTaxCodeId { get; set; }
    public double SaleTax { get; set; }
    public double Vat { get; set; }
    public bool VatIsValue { get; set; }
    public bool TaxAtRetailPrice { get; set; }

    public double Price { get; set; }
    public double WholeSalePrice { get; set; }
    public double AverageCost { get; set; }
    public bool UserPrice { get; set; }
    public bool FloatingPrice { get; set; }
    public int CurrencyId { get; set; }

    public bool Active { get; set; }
    public bool IsDefault { get; set; }
    public bool NoBarcodePrint { get; set; }
    public bool RfidEnabled { get; set; }
    public bool AllowBelowCost { get; set; }
    public bool AllowPriceChange { get; set; }
    public bool IsWebItem { get; set; }
    public bool ItemNotForDiscount { get; set; }
    public bool IsBasicType { get; set; }

    public double NumberOfPieces { get; set; }

    public string? HsCode { get; set; }
    public string? VendorCode { get; set; }
    public string? TechnicalDetails { get; set; }
    public string? Comments { get; set; }
    public string? InternalComments { get; set; }

    public string? EnteredBy { get; set; }
    public DateTime? EnteredDate { get; set; }
    public string? EditedBy { get; set; }
    public DateTime? EditedDate { get; set; }
}
