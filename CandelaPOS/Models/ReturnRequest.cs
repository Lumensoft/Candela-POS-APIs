using System.Collections.Generic;
using Newtonsoft.Json;

namespace CandelaPOS.Models
{
    public class ValidateReturnRequest
    {
        [JsonProperty("invoice_no")]
        public int InvoiceNo { get; set; }
    }

    public class ReturnRequest
    {
        [JsonProperty("client_txn_guid")]
        public string ClientTxnGuid { get; set; }

        [JsonProperty("returning_invoice_no")]
        public int ReturningInvoiceNo { get; set; }

        [JsonProperty("payment_type")]
        public string PaymentType { get; set; }

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

        [JsonProperty("comments")]
        public string Comments { get; set; }

        [JsonProperty("customer_id")]
        public int CustomerId { get; set; }

        // Credit note — when provided, the return amount is loaded onto a gift card.
        // frmSaleAndReturn.vb:38760 saveCreditNote(); SaleAndReturnDAL.Add() overload
        // with myCreditNoteDataTable. Leave gift_card_id=0 for cash/card refund.
        [JsonProperty("credit_note_gift_card_id")]
        public int CreditNoteGiftCardId { get; set; }

        [JsonProperty("credit_note_gift_card_no")]
        public int CreditNoteGiftCardNo { get; set; }

        [JsonProperty("credit_note_member_name")]
        public string CreditNoteMemberName { get; set; }

        [JsonProperty("credit_note_phone")]
        public string CreditNotePhone { get; set; }

        // Items must have NEGATIVE quantities
        [JsonProperty("items")]
        public List<SaleLineItem> Items { get; set; }
    }
}
