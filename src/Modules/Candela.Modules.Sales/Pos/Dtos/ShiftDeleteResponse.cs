using Newtonsoft.Json;

namespace Candela.Modules.Sales.Pos.Dtos;

/// <summary>
/// DELETE /api/pos/shifts/{closingId}. Echoes what the legacy wrapper reports once
/// POSCashManagmentDAL.DeleteShift has removed the closing.
/// </summary>
public sealed class ShiftDeleteResponse
{
    [JsonProperty("success")]
    public bool Success { get; set; } = true;

    [JsonProperty("closing_id")]
    public int ClosingId { get; set; }

    [JsonProperty("pos_code")]
    public string PosCode { get; set; } = "";

    [JsonProperty("shop_id")]
    public int ShopId { get; set; }
}
