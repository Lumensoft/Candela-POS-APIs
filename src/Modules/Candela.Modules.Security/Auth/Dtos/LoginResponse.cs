using Newtonsoft.Json;

namespace Candela.Modules.Security.Auth.Dtos;

/// <summary>
/// The <c>data</c> body of POST /api/auth/login.
///
/// The login flow is the one place the React app reads the response field by field —
/// it writes all of this into IndexedDB and runs the whole shift off it. Renaming or
/// dropping any member here breaks the till silently, so the names and the declaration
/// order are exactly the net48 LoginResponse.
/// </summary>
public sealed class LoginResponse
{
    [JsonProperty("token")] public string Token { get; set; } = "";
    [JsonProperty("user_id")] public int UserId { get; set; }
    [JsonProperty("user_name")] public string UserName { get; set; } = "";
    [JsonProperty("shop_id")] public int ShopId { get; set; }
    [JsonProperty("shop_name")] public string ShopName { get; set; } = "";
    [JsonProperty("pos_code")] public string PosCode { get; set; } = "";
    [JsonProperty("allow_discount_editing")] public bool AllowDiscountEditing { get; set; }
    [JsonProperty("allow_price_editing")] public bool AllowPriceEditing { get; set; }
    [JsonProperty("can_adjust")] public bool CanAdjust { get; set; }
    [JsonProperty("is_open_adjust")] public bool IsOpenAdjust { get; set; }
    [JsonProperty("computer_name")] public string ComputerName { get; set; } = "";
    [JsonProperty("invoice_printer_name")] public string InvoicePrinterName { get; set; } = "";
    [JsonProperty("control_rights")] public List<string> ControlRights { get; set; } = new();
}
