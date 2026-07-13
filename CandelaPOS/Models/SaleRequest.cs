using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace CandelaPOS.Models
{
    public class SaleRequest
    {
        /// <summary>Client-generated UUID — idempotency key. Resubmitting the same GUID returns the original sale_id.</summary>
        [JsonProperty("client_txn_guid")]
        public string ClientTxnGuid { get; set; }

        [JsonProperty("sale_date")]
        public DateTime SaleDate { get; set; }

        [JsonProperty("customer_id")]
        public int CustomerId { get; set; }           // 0 = walk-in

        [JsonProperty("walk_in_name")]
        public string WalkInName { get; set; }

        [JsonProperty("walk_in_phone")]
        public string WalkInPhone { get; set; }

        [JsonProperty("payment_type")]
        public string PaymentType { get; set; }       // "Cash" | "Card" | "Credit" | "Split" | "Mobile" | "GiftCard"

        [JsonProperty("gross_total")]
        public double GrossTotal { get; set; }

        [JsonProperty("net_total")]
        public double NetTotal { get; set; }

        [JsonProperty("customer_discount")]
        public double CustomerDiscount { get; set; }

        [JsonProperty("marketing_discount")]
        public double MarketingDiscount { get; set; }

        [JsonProperty("vat_amount")]
        public double VatAmount { get; set; }

        [JsonProperty("additional_tax")]
        public double AdditionalTax { get; set; }

        [JsonProperty("adjustment_amount")]
        public double AdjustmentAmount { get; set; }

        [JsonProperty("cash_amount")]
        public double CashAmount { get; set; }

        [JsonProperty("card_amount")]
        public double CardAmount { get; set; }

        [JsonProperty("credit_card_id")]
        public int CreditCardId { get; set; }

        [JsonProperty("credit_amount")]
        public double CreditAmount { get; set; }

        [JsonProperty("gift_card_no")]
        public string GiftCardNo { get; set; }

        [JsonProperty("gift_card_amount")]
        public double GiftCardAmount { get; set; }

        [JsonProperty("salesperson_id")]
        public int SalespersonId { get; set; }

        [JsonProperty("comments")]
        public string Comments { get; set; }

        // Set this when finalizing a previously parked hold — DAL will delete the hold row
        [JsonProperty("holding_sale_id")]
        public int HoldingSaleId { get; set; }

        // ── Mobile payment fields ──────────────────────────────────────────────────
        // FonePay: transaction_id required; vendor = "FonePay"; is_manual = true (cashier-entered)
        // Mirrors frmSaleAndReturn.vb:37086-37097 (AddMobiePayments → frmMobilePayment)
        [JsonProperty("transaction_id")]
        public string TransactionId { get; set; }

        [JsonProperty("vendor")]
        public string Vendor { get; set; }          // "FonePay"

        [JsonProperty("is_manual")]
        public bool IsManual { get; set; }          // true = cashier manually entered txn id

        // 543Pay: payment_id, resp_code, reference_num, mobile_num required
        // Mirrors frmSaleAndReturn.vb:37100-37116
        [JsonProperty("payment_id")]
        public string PaymentId { get; set; }

        [JsonProperty("resp_code")]
        public string RespCode { get; set; }

        [JsonProperty("resp_message")]
        public string RespMessage { get; set; }

        [JsonProperty("reference_num")]
        public string ReferenceNum { get; set; }

        [JsonProperty("mobile_num")]
        public string MobileNum { get; set; }       // stored in tblSales.Comments (issue #1502)

        // Coupon code applied to this sale. /sales calls CheckCouponStatus() to mark it Used.
        [JsonProperty("coupon_no")]
        public string CouponNo { get; set; }

        [JsonProperty("items")]
        public List<SaleLineItem> Items { get; set; }
    }

    public class SaleLineItem
    {
        [JsonProperty("product_item_id")]
        public int ProductItemId { get; set; }

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

        [JsonProperty("marketing_discount")]
        public double MarketingDiscount { get; set; }

        [JsonProperty("loyalty_cash_discount")]
        public double LoyaltyCashDiscount { get; set; }

        [JsonProperty("vat_value")]
        public double VatValue { get; set; }

        [JsonProperty("vat_factor")]
        public double VatFactor { get; set; }

        [JsonProperty("vat_type")]
        public string VatType { get; set; }

        [JsonProperty("price_include_vat")]
        public bool PriceIncludeVat { get; set; }

        [JsonProperty("additional_tax_percent")]
        public double AdditionalTaxPercent { get; set; }

        [JsonProperty("additional_tax")]
        public double AdditionalTax { get; set; }

        [JsonProperty("gross_amount")]
        public double GrossAmount { get; set; }

        [JsonProperty("net_amount")]
        public double NetAmount { get; set; }

        [JsonProperty("discount_id")]
        public int DiscountId { get; set; }

        [JsonProperty("disc_category")]
        public string DiscCategory { get; set; }

        // Batch tracking — written to tblSalesLineItems.ProductBatchNo (CR #8125)
        [JsonProperty("batch_no")]
        public string BatchNo { get; set; }

        // Pack selling — Con_Factor=pack size multiplier, PackSize=units per pack
        // Affects discount calc (SaleAndReturnDAL.vb:901) and inventory movement
        [JsonProperty("con_factor")]
        public double ConFactor { get; set; }

        [JsonProperty("pack_size")]
        public double PackSize { get; set; }

        // Nested/assembly item — composite product that explodes into sub-items on sale
        [JsonProperty("nested_item_id")]
        public int NestedItemId { get; set; }

        // True when isShowTagPrice=True and a unit discount was applied to the tag (VAT-inclusive) price.
        // DAL uses this to reverse the discount correctly during inventory cost calculation.
        // Echo back the value returned by /quote — do not compute independently.
        [JsonProperty("discount_from_tag_price")]
        public bool DiscountFromTagPrice { get; set; }

        // Exchange item inside a return/exchange transaction (Show_popup_on_return = FALSE).
        // When true: qty is NOT negated — the item is treated as a positive sale line (inventory out).
        // When false (default): qty is negated server-side (standard return behaviour).
        [JsonProperty("is_exchange_item")]
        public bool IsExchangeItem { get; set; }

        // Assembly/bundle component substitutions for this line item.
        // Non-null only when the cashier opened the Assembly tab and modified child items.
        // Mirrors SaleAndReturn.ListOfAssemblyItems (Model.SalesProductAssembly).
        // Each entry is one child component with the cashier-chosen qty and retail price.
        [JsonProperty("assembly_items")]
        public List<AssemblyItemDto> AssemblyItems { get; set; }
    }

    // One component row inside a bundle/assembly line item.
    // Maps to Model.SalesProductAssembly — fields mirror tblSalesAssembly columns.
    public class AssemblyItemDto
    {
        [JsonProperty("product_item_id")]
        public int ProductItemId { get; set; }    // child product (ProductIDPart)

        [JsonProperty("quantity")]
        public double Quantity { get; set; }

        [JsonProperty("retail_price")]
        public double RetailPrice { get; set; }   // stored as Product_Price in tblSalesAssembly
    }
}
