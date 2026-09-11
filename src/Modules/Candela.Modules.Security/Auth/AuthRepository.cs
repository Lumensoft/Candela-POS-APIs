using Candela.Platform.Data;

namespace Candela.Modules.Security.Auth;

/// <summary>
/// Every statement is copied from the net48 AuthController and AuthRules, aliases and
/// parameter names included.
///
/// The credential query selects User_log_password and the caller decrypts it. That is the
/// original design and it is kept: moving the comparison into SQL would mean sending the
/// typed password to the server, and decrypting in the repository would put a rule in the
/// layer that is only supposed to fetch.
/// </summary>
public sealed class AuthRepository(IDb db) : IAuthRepository
{
    public Task<SecurityUserRow?> FindUserAsync(string username, CancellationToken ct)
    {
        const string sql =
            "SELECT b.user_id, b.User_log_password, b.User_name," +
            " isnull(b.AllowPOSDiscountEditing,0) AS AllowPOSDiscountEditing," +
            " isnull(b.AllowPOSPriceEditing,0)    AS AllowPOSPriceEditing," +
            " isnull(b.ApplyAdjustment,0)         AS ApplyAdjustment," +
            " isnull(b.ApplyOpenAdjustment,0)     AS ApplyOpenAdjustment," +
            " isnull(a.GROUP_NAME,'')              AS GROUP_NAME," +
            " isnull(a.GROUP_TYPE,0)               AS GROUP_TYPE," +
            " isnull(a.SaleReturnLimit,0)          AS SaleReturnLimit," +
            " a.GROUP_ID                           AS GROUP_ID" +
            " FROM tblSecurityGroup a" +
            " INNER JOIN TblSecurityUser b ON a.GROUP_ID = b.GROUP_ID" +
            " WHERE b.user_log_id = @uid" +
            "   AND isnull(b.end_date, GETDATE()+1) >= DATEADD(dd,0,DATEDIFF(dd,0,GETDATE()))";

        // The original did reader.HasRows then a single Read(), so extra rows for one
        // login id were ignored rather than treated as an error. QueryFirstOrDefault.
        return db.QueryFirstOrDefaultAsync<SecurityUserRow>(sql, new { uid = username }, ct);
    }

    public Task<IReadOnlyList<string>> GetControlRightsAsync(int groupId, CancellationToken ct) =>
        GetFormControlRightsAsync(groupId, "frmSaleAndReturn", ct);

    public async Task<IReadOnlyList<string>> GetFormControlRightsAsync(int groupId, string formName,
        CancellationToken ct)
    {
        const string sql =
            "SELECT sfc.controlName " +
            "FROM tblSecurityControlRight scr " +
            "INNER JOIN tblSecurityFormControl sfc ON sfc.ControlId = scr.ControlId " +
            "INNER JOIN tblSecurityForm sf ON sf.FORM_ID = sfc.FormID " +
            "WHERE scr.GroupId = @gid AND sf.Form_Name_New = @form";

        return await db.QueryAsync<string>(sql, new { gid = groupId, form = formName }, ct);
    }

    public async Task<int> GetGroupIdAsync(int userId, CancellationToken ct)
    {
        const string sql = "SELECT GROUP_ID FROM TblSecurityUser WHERE user_id = @uid";

        return await db.ExecuteScalarAsync<int?>(sql, new { uid = userId }, ct) ?? 0;
    }

    public Task<DeviceRow?> FindDeviceAsync(string deviceId, CancellationToken ct)
    {
        const string sql =
            "SELECT computer_id, computer_name, shop_id, POS_code, isTabActive," +
            " isnull(InvoicePrinterName,'') AS InvoicePrinterName" +
            " FROM tblComputerList" +
            " WHERE deviceid = @uuid AND istablet = 1";

        return db.QueryFirstOrDefaultAsync<DeviceRow>(sql, new { uuid = deviceId }, ct);
    }

    public Task<DeviceRow?> ClaimFreeSlotAsync(string deviceId, CancellationToken ct)
    {
        // UPDATE TOP(1) ... OUTPUT is what makes this safe without a transaction: two
        // tablets registering at the same moment cannot be handed the same slot.
        const string sql =
            "UPDATE TOP(1) tblComputerList" +
            " SET deviceid = @uuid, isTabActive = 1" +
            " OUTPUT inserted.shop_id, inserted.POS_code, inserted.computer_name," +
            "        isnull(inserted.InvoicePrinterName,'') AS InvoicePrinterName" +
            " WHERE istablet = 1 AND isTabActive = 0 AND deviceid IS NULL";

        return db.QueryFirstOrDefaultAsync<DeviceRow>(sql, new { uuid = deviceId }, ct);
    }

    public async Task<string> GetShopNameAsync(int shopId, CancellationToken ct)
    {
        const string sql = "SELECT shop_name FROM tblDefShops WHERE shop_id = @sid";

        // The original used ExecuteScalar then `?.ToString() ?? ""`, so a missing shop row
        // gave an empty name rather than an error.
        return await db.ExecuteScalarAsync<string>(sql, new { sid = shopId }, ct) ?? "";
    }

    public async Task<AuthRules.ShopAccessFacts?> GetShopAccessFactsAsync(int groupId, int shopId,
        CancellationToken ct)
    {
        const string sql =
            "SELECT" +
            "  ISNULL((SELECT COUNT(1) FROM tblDefShops" +
            "          WHERE shop_id = @sid AND closing_date IS NULL), 0)            AS ShopOpen," +
            "  ISNULL((SELECT ISNULL(IsSelectedShop, 0) FROM tblSecurityGroup" +
            "          WHERE GROUP_ID = @gid), 0)                                    AS AllShops," +
            "  ISNULL((SELECT COUNT(1) FROM tblSecurityGroupShops" +
            "          WHERE Group_Id = @gid), 0)                                    AS ConfiguredCount," +
            "  ISNULL((SELECT COUNT(1) FROM tblSecurityGroupShops" +
            "          WHERE Group_Id = @gid AND Shop_Id = @sid), 0)                 AS ThisShop";

        // 15s, as the original set on this command specifically: it gates every login, so
        // it must fail fast rather than hang the till.
        var row = await db.QueryFirstOrDefaultAsync<FactsRow>(sql, new { sid = shopId, gid = groupId },
            ct, timeoutSeconds: 15);

        if (row is null) return null;

        return new AuthRules.ShopAccessFacts(row.ShopOpen, row.AllShops, row.ConfiguredCount, row.ThisShop);
    }

    public Task<string?> GetConfigValueAsync(string configName, CancellationToken ct)
    {
        // The net48 path loaded every config row into a DataTable (cached per request) and
        // then looked one key up. One key is all any caller wants, so it is fetched
        // directly — the value returned is the same raw, still-encrypted string.
        const string sql = "SELECT config_value FROM tblRCMSConfiguration WHERE config_name = @name";

        return db.QueryFirstOrDefaultAsync<string>(sql, new { name = configName }, ct);
    }

    public async Task<AdjustmentRightsRow> GetAdjustmentRightsAsync(int userId, CancellationToken ct)
    {
        const string sql =
            "SELECT isnull(ApplyAdjustment, 0)    AS ApplyAdjustment," +
            "       isnull(ApplyOpenAdjustment, 0) AS ApplyOpenAdjustment" +
            " FROM TblSecurityUser WHERE user_id = @uid";

        // No row means both flags false — the original left its locals at false when the
        // reader had nothing, rather than failing.
        return await db.QueryFirstOrDefaultAsync<AdjustmentRightsRow>(sql, new { uid = userId }, ct)
               ?? new AdjustmentRightsRow();
    }

    public async Task BlocklistTokenAsync(string signature, DateTime expiresAt, CancellationToken ct)
    {
        const string sql =
            "INSERT INTO tblPOSTokenBlocklist (token_sig, expires_at) " +
            "VALUES (@sig, @exp)";

        await db.ExecuteAsync(sql, new { sig = signature, exp = expiresAt }, ct);
    }

    /// <summary>Binding target for the four counts; converted to the record struct above.</summary>
    private sealed class FactsRow
    {
        public int ShopOpen { get; set; }
        public int AllShops { get; set; }
        public int ConfiguredCount { get; set; }
        public int ThisShop { get; set; }
    }
}
