using Newtonsoft.Json;

namespace Candela.Modules.Sales.Pos.Dtos;

/// <summary>
/// Body of POST /api/pos/cash-skim and POST /api/pos/cash-receive — the two share this
/// shape, differing only in the ledger Type the service stamps.
///
/// The tablet posts { amount, notes }. [JsonProperty] pins the lower-case names so they
/// bind the same whether the payload comes from the tablet (through MVC) or is
/// re-serialised by LegacyHostClient on the hop to the legacy wrapper.
/// </summary>
public sealed class CashMovementRequest
{
    [JsonProperty("amount")]
    public decimal Amount { get; set; }

    [JsonProperty("notes")]
    public string? Notes { get; set; }
}
