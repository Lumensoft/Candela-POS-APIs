namespace Candela.Modules.Security.Auth;

/// <summary>
/// Every database read and write login needs.
///
/// The net48 controller ran all of this on one open SqlConnection but without a
/// transaction, so one connection per call is behaviourally the same. The one step that
/// genuinely needs atomicity — claiming a free tablet slot — is atomic on its own through
/// UPDATE TOP(1) ... OUTPUT, not through a transaction, and it stays that way.
///
/// Nothing here touches the Candela DAL, so none of it is forwarded to the legacy host.
/// </summary>
public interface IAuthRepository
{
    /// <summary>Credentials and group facts for a login id, or null when there is no usable row.</summary>
    Task<SecurityUserRow?> FindUserAsync(string username, CancellationToken ct);

    /// <summary>The frmSaleAndReturn control rights granted to a group.</summary>
    Task<IReadOnlyList<string>> GetControlRightsAsync(int groupId, CancellationToken ct);

    /// <summary>
    /// The control rights granted to a group on ANY Candela screen — the same
    /// tblSecurityForm / tblSecurityFormControl / tblSecurityControlRight join
    /// GetControlRightsAsync uses, generalised by form name (e.g. "frmDefCity") instead of
    /// being fixed to frmSaleAndReturn. This is what lets a back-office screen ask "what can
    /// this group do here" without a new query per screen.
    /// </summary>
    Task<IReadOnlyList<string>> GetFormControlRightsAsync(int groupId, string formName, CancellationToken ct);

    /// <summary>The security group a user belongs to, or 0 when the user row is missing.</summary>
    Task<int> GetGroupIdAsync(int userId, CancellationToken ct);

    /// <summary>The tablet registration for a device id, or null when this device is new.</summary>
    Task<DeviceRow?> FindDeviceAsync(string deviceId, CancellationToken ct);

    /// <summary>
    /// Claims one free pre-allocated tablet slot for this device, atomically. Null when
    /// every slot is taken.
    /// </summary>
    Task<DeviceRow?> ClaimFreeSlotAsync(string deviceId, CancellationToken ct);

    /// <summary>Shop name for the response, or empty when the shop row is missing.</summary>
    Task<string> GetShopNameAsync(int shopId, CancellationToken ct);

    /// <summary>
    /// The four facts AuthRules decides shop access on, or null when the query returned
    /// nothing — which the caller turns into "could not verify".
    /// </summary>
    Task<AuthRules.ShopAccessFacts?> GetShopAccessFactsAsync(int groupId, int shopId, CancellationToken ct);

    /// <summary>One raw (still encrypted) value from tblRCMSConfiguration, or null.</summary>
    Task<string?> GetConfigValueAsync(string configName, CancellationToken ct);

    /// <summary>The two per-user adjustment flags off TblSecurityUser.</summary>
    Task<AdjustmentRightsRow> GetAdjustmentRightsAsync(int userId, CancellationToken ct);

    /// <summary>Records a token signature as revoked until its own expiry.</summary>
    Task BlocklistTokenAsync(string signature, DateTime expiresAt, CancellationToken ct);
}

/// <summary>Credential and group row for one login id. Property names match the SELECT aliases.</summary>
public sealed class SecurityUserRow
{
    public int user_id { get; set; }
    public string? User_log_password { get; set; }
    public string? User_name { get; set; }
    public bool AllowPOSDiscountEditing { get; set; }
    public bool AllowPOSPriceEditing { get; set; }
    public bool ApplyAdjustment { get; set; }
    public bool ApplyOpenAdjustment { get; set; }
    public string GROUP_NAME { get; set; } = "";
    public int GROUP_TYPE { get; set; }
    public decimal SaleReturnLimit { get; set; }
    public int GROUP_ID { get; set; }
}

/// <summary>A tablet seat in tblComputerList.</summary>
public sealed class DeviceRow
{
    public int shop_id { get; set; }
    public string? POS_code { get; set; }
    public string? computer_name { get; set; }
    public string InvoicePrinterName { get; set; } = "";

    /// <summary>
    /// Only read on the already-registered path. The claim path selects rows that are
    /// inactive by definition and sets the flag as it claims them, so it does not return
    /// this column — it stays false there and is not consulted.
    /// </summary>
    public bool isTabActive { get; set; }
}

/// <summary>The two bit columns that decide whether a manual adjustment is allowed.</summary>
public sealed class AdjustmentRightsRow
{
    public bool ApplyAdjustment { get; set; }
    public bool ApplyOpenAdjustment { get; set; }
}
