using System.ComponentModel.DataAnnotations;

namespace Candela.Modules.Configuration.Products.Dtos;

/// <summary>
/// Body of POST/PUT api/configuration/products — the Core Product tab
/// (frmDefProduct.vb, EnumTabPages.TabPgProduct), non-assortment mode: one product, one
/// SKU, no size/colour matrix. A product with sizes and colours is Phase 2 — see the
/// module's README note in ProductsController.
///
/// Field names mirror Model.Configuration.Product (the VB model both this and the
/// desktop write through), so mapping one to the other in the legacy host stays a
/// straight assignment per field, not a translation.
/// </summary>
public sealed class ProductRequest
{
    [Required(ErrorMessage = "Line item is required.")]
    public int LineItemId { get; set; }

    [Required(ErrorMessage = "Product code is required.")]
    [StringLength(50, ErrorMessage = "Product code is too long.")]
    public string ProductCode { get; set; } = "";

    [Required(ErrorMessage = "Product name is required.")]
    [StringLength(200, ErrorMessage = "Product name is too long.")]
    public string ItemName { get; set; } = "";

    // ── Classification — Category/SubCategory/ProductGroup/Variable1-2 are Line-Item
    // scoped, Variable3-5 are global. Whether each one is actually mandatory is driven by
    // tblDefScreenCustomization (Formid = 0) — see ProductService.ValidateAsync — so none
    // of them carry [Required] here; a 0 means "not chosen" and is valid unless that
    // config table says otherwise for this install.
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

    // ── Tax ──────────────────────────────────────────────────────────────────────────
    public int SaleTaxCodeId { get; set; }
    [Range(0, 100, ErrorMessage = "Sales tax cannot exceed 100%.")]
    public double SaleTax { get; set; }
    [Range(0, 100, ErrorMessage = "VAT cannot exceed 100%.")]
    public double Vat { get; set; }
    /// <summary>true = VAT is a fixed value, false = VAT is a percentage (optVATValue/optVATPercentage).</summary>
    public bool VatIsValue { get; set; }
    public bool TaxAtRetailPrice { get; set; }

    // ── Pricing ──────────────────────────────────────────────────────────────────────
    public double Price { get; set; }
    public double WholeSalePrice { get; set; }
    public double AverageCost { get; set; }
    public bool UserPrice { get; set; }
    public bool FloatingPrice { get; set; }
    public int CurrencyId { get; set; }

    // ── Flags ────────────────────────────────────────────────────────────────────────
    public bool Active { get; set; } = true;
    public bool IsDefault { get; set; }
    public bool NoBarcodePrint { get; set; }
    public bool RfidEnabled { get; set; }
    public bool AllowBelowCost { get; set; }
    public bool AllowPriceChange { get; set; }
    public bool IsWebItem { get; set; }
    public bool ItemNotForDiscount { get; set; }
    /// <summary>optBasic (true) vs optDesigned (false) — "Basic/Designed" on the desktop.</summary>
    public bool IsBasicType { get; set; } = true;

    public double NumberOfPieces { get; set; }

    [StringLength(50)]
    public string? HsCode { get; set; }
    [StringLength(50)]
    public string? VendorCode { get; set; }
    [StringLength(1000)]
    public string? TechnicalDetails { get; set; }
    [StringLength(500)]
    public string? Comments { get; set; }
    [StringLength(500)]
    public string? InternalComments { get; set; }
}
