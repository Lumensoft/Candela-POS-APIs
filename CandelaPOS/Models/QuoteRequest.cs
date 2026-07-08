using System.Collections.Generic;
using Newtonsoft.Json;

namespace CandelaPOS.Models
{
    public class QuoteRequest
    {
        [JsonProperty("customer_id")]
        public int CustomerId { get; set; }

        [JsonProperty("payment_type")]
        public string PaymentType { get; set; }

        // Cart-level coupon code (one per sale, not per line).
        // /quote validates it and distributes per-item discounts; /sales marks it Used.
        [JsonProperty("coupon_code")]
        public string CouponCode { get; set; }

        [JsonProperty("items")]
        public List<QuoteItem> Items { get; set; }
    }

    public class QuoteItem
    {
        [JsonProperty("product_item_id")]
        public int ProductItemId { get; set; }

        [JsonProperty("quantity")]
        public double Quantity { get; set; }

        [JsonProperty("unit_discount")]
        public double UnitDiscount { get; set; }

        [JsonProperty("discount_type")]
        public string DiscountType { get; set; }

        // Pack selling — PackSize = units per pack; ConFactor = same in most cases.
        // Passed to GetSKUDiscountValue as IsPack/PackSize (DAL:901-902).
        // When PackSize=6: discount_type "2" amount is multiplied by 6 inside the DAL.
        [JsonProperty("pack_size")]
        public double PackSize { get; set; }

        [JsonProperty("con_factor")]
        public double ConFactor { get; set; }
    }

    public class QuoteLineResult
    {
        [JsonProperty("product_item_id")]
        public int ProductItemId { get; set; }

        [JsonProperty("item_name")]
        public string ItemName { get; set; }

        [JsonProperty("quantity")]
        public double Quantity { get; set; }

        [JsonProperty("unit_rate")]
        public double UnitRate { get; set; }

        [JsonProperty("tagged_price")]
        public double TaggedPrice { get; set; }

        [JsonProperty("unit_discount")]
        public double UnitDiscount { get; set; }

        [JsonProperty("customer_discount_per_unit")]
        public double CustomerDiscountPerUnit { get; set; }

        // Populated when loyalty club is active and a loyalty % applies to this item.
        // This IS the customer_discount_per_unit (they occupy the same slot in the VAT base).
        [JsonProperty("loyalty_cash_discount_per_unit")]
        public double LoyaltyCashDiscountPerUnit { get; set; }

        [JsonProperty("vat_value")]
        public double VatValue { get; set; }

        [JsonProperty("vat_factor")]
        public double VatFactor { get; set; }

        [JsonProperty("vat_type")]
        public string VatType { get; set; }

        [JsonProperty("price_include_vat")]
        public bool PriceIncludeVat { get; set; }

        [JsonProperty("additional_tax")]
        public double AdditionalTax { get; set; }

        [JsonProperty("net_amount")]
        public double NetAmount { get; set; }

        // "X"/"Y"/"N"/"M"/"Q" = Buy-X-Get-Y promo category; "C" = coupon; "" = normal
        [JsonProperty("disc_category")]
        public string DiscCategory { get; set; }

        [JsonProperty("discount_id")]
        public int DiscountId { get; set; }

        // Per-unit share of the invoice-level marketing discount.
        // Populated when IsSubtractMarketingDiscount is True (affects VAT base).
        [JsonProperty("marketing_disc_per_unit")]
        public double MarketingDiscPerUnit { get; set; }

        // Additional-tax percent applied per unit (formula 1 — per item).
        // Zero when Additional_Sale_Tax_Formula=2 (on net total) or addl tax is disabled.
        // Echo back to /sales so tblSalesLineItems.additional_tax_percent is stored correctly.
        [JsonProperty("additional_tax_percent")]
        public double AdditionalTaxPercent { get; set; }

        // True when isShowTagPrice=True and a unit discount was applied.
        // Echo back to /sales — DAL uses this to reverse discounts against the tag price.
        [JsonProperty("discount_from_tag_price")]
        public bool DiscountFromTagPrice { get; set; }
    }

    public class QuoteResult
    {
        [JsonProperty("items")]
        public List<QuoteLineResult> Items { get; set; }

        [JsonProperty("gross_total")]
        public double GrossTotal { get; set; }

        [JsonProperty("total_discount")]
        public double TotalDiscount { get; set; }

        [JsonProperty("customer_discount")]
        public double CustomerDiscount { get; set; }

        [JsonProperty("marketing_discount")]
        public double MarketingDiscount { get; set; }

        [JsonProperty("vat_amount")]
        public double VatAmount { get; set; }

        [JsonProperty("additional_tax")]
        public double AdditionalTax { get; set; }

        [JsonProperty("net_total")]
        public double NetTotal { get; set; }

        // True when loyalty club is active and this customer has a member ID.
        [JsonProperty("is_loyalty_on")]
        public bool IsLoyaltyOn { get; set; }

        // Echoed back when a valid coupon was applied to this quote.
        [JsonProperty("coupon_no")]
        public string CouponNo { get; set; }

        [JsonProperty("coupon_discount")]
        public double CouponDiscount { get; set; }
    }
}
