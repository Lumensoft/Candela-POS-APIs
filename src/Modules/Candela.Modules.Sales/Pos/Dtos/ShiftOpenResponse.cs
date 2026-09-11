using Newtonsoft.Json;

namespace Candela.Modules.Sales.Pos.Dtos;

/// <summary>
/// POST /api/pos/shift-open. Idempotent: if a shift is already open the net48 endpoint
/// returned { success, already_open: true, opening, pos_code, shop_id } with no
/// <c>opened_at</c>; on a fresh open it added <c>opened_at</c>. Nullable, so
/// NullValueHandling.Ignore reproduces both.
/// </summary>
public sealed class ShiftOpenResponse
{
    [JsonProperty("success")]
    public bool Success { get; set; } = true;

    [JsonProperty("already_open")]
    public bool AlreadyOpen { get; set; }

    [JsonProperty("opened_at")]
    public string? OpenedAt { get; set; }

    [JsonProperty("opening")]
    public double Opening { get; set; }

    [JsonProperty("pos_code")]
    public string PosCode { get; set; } = "";

    [JsonProperty("shop_id")]
    public int ShopId { get; set; }
}
