using Newtonsoft.Json;

namespace Candela.Modules.Sales.Returns.Dtos;

/// <summary>
/// The body of POST /api/returns/validate:
///   { "success": true, "customer_code": "...", "sale": { ... } | null, "items": [ ... ] }
///
/// <c>sale</c> is the raw invoice-header row (its column aliases are the contract) and
/// can be null when the header query returns nothing even though the invoice validated —
/// the net48 endpoint returned it as null in that case rather than erroring, so that is
/// kept.
/// </summary>
public sealed class ValidateReturnResponse
{
    [JsonProperty("success")]
    public bool Success { get; set; } = true;

    [JsonProperty("customer_code")]
    public string CustomerCode { get; set; } = "";

    [JsonProperty("sale")]
    public Dictionary<string, object?>? Sale { get; set; }

    [JsonProperty("items")]
    public IReadOnlyList<Dictionary<string, object?>> Items { get; set; } = new List<Dictionary<string, object?>>();
}
