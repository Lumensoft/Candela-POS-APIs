using Newtonsoft.Json;

namespace Candela.Modules.Sales.Pos.Dtos;

/// <summary>
/// Optional detailed note count sent with a shift close. The tablet sends lower-case keys
/// ({ d5000, d1000, ... other }); the net48 DenominationsDto had no attributes and relied
/// on case-insensitive binding. The names are pinned here so the .NET10 -> legacy hop is
/// deterministic too, and the values still land on tblPOSShiftCashCount's Denom_* columns
/// unchanged.
/// </summary>
public sealed class DenominationsDto
{
    [JsonProperty("d5000")] public int D5000 { get; set; }
    [JsonProperty("d1000")] public int D1000 { get; set; }
    [JsonProperty("d500")] public int D500 { get; set; }
    [JsonProperty("d100")] public int D100 { get; set; }
    [JsonProperty("d50")] public int D50 { get; set; }
    [JsonProperty("d20")] public int D20 { get; set; }
    [JsonProperty("d10")] public int D10 { get; set; }
    [JsonProperty("d5")] public int D5 { get; set; }
    [JsonProperty("other")] public double Other { get; set; }
}
