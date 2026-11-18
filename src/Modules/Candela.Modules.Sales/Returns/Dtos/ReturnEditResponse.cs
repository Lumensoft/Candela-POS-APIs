using Newtonsoft.Json;

namespace Candela.Modules.Sales.Returns.Dtos;

/// <summary>
/// GET /api/returns/{id} — everything the return screen needs to reopen a SAVED return for editing:
/// the return itself (header + lines), and — when it was made against an invoice — that original invoice's header and
/// lines in the same per-single shape POST /api/returns/validate gives, each with <c>max_qty</c>: how many units of it
/// may still be returned, not counting the return being edited (Candela's IsValidForReturn leaves the invoice being
/// updated out of "already returned").
/// </summary>
public sealed class ReturnEditResponse
{
    [JsonProperty("success")] public bool Success { get; set; } = true;

    /// <summary>The saved return's header: returning_invoice_no, source_shop_id, customer, tender, comments.</summary>
    [JsonProperty("return")] public Dictionary<string, object?> Return { get; set; } = new();

    /// <summary>The saved return's lines: product_item_id, signed quantity, return reason.</summary>
    [JsonProperty("lines")] public IReadOnlyList<Dictionary<string, object?>> Lines { get; set; }
        = new List<Dictionary<string, object?>>();

    /// <summary>The original invoice's header; null for a free return.</summary>
    [JsonProperty("sale")] public Dictionary<string, object?>? Sale { get; set; }

    /// <summary>The original invoice's lines (per single, tax included) with max_qty; empty for a free return.</summary>
    [JsonProperty("items")] public IReadOnlyList<Dictionary<string, object?>> Items { get; set; }
        = new List<Dictionary<string, object?>>();
}
