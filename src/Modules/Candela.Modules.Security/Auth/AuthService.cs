using Candela.Modules.Security.Auth.Dtos;
using Candela.Platform.Security;
using Candela.Shared.Auth;
using Candela.Shared.Exceptions;
using Candela.Shared.Logging;

namespace Candela.Modules.Security.Auth;

/// <summary>
/// Ported from the net48 AuthController. Every message, status code and check order is
/// unchanged, including the three that were added to match the desktop:
///   * a user may not sign in on a tablet belonging to a shop their group has no rights to
///   * nobody authorises their own override
///   * an override needs the same group type and an entitlement to the cashier's shop
///
/// The password still arrives encrypted from the database and is decrypted here, compared
/// with StringComparison.Ordinal. Decryption now runs on .NET 10 through
/// Candela.Platform.Security.SymmetricEncryption, which is byte-verified against
/// .NET Framework (SymmetricEncryptionTests) — the library's own Decrypt could not be used
/// because it truncates anything longer than one 16-byte block on this runtime.
/// </summary>
public sealed class AuthService(IAuthRepository repo, AuthOptions options) : IAuthService
{
    /// <summary>The one message every credential failure returns — it must never say which half was wrong.</summary>
    private const string BadCredentials = "Invalid username or password";
    private const string BadSupervisorCredentials = "Invalid supervisor credentials";

    public async Task<LoginResponse> LoginAsync(LoginRequest req, CancellationToken ct)
    {
        // Step 1 — validate credentials against tblSecurityUser
        var user = await repo.FindUserAsync(req.Username!, ct);
        if (user is null)
            throw new UnauthorizedException(BadCredentials);

        if (string.IsNullOrEmpty(user.User_log_password))
            throw new UnauthorizedException(BadCredentials);

        var decrypted = SymmetricEncryption.Decrypt(user.User_log_password, "f");
        if (!req.Password!.Equals(decrypted, StringComparison.Ordinal))
            throw new UnauthorizedException(BadCredentials);

        // Step 1b — every frmSaleAndReturn control right for this group, in one query.
        // OrdinalIgnoreCase because the right names are compared by name, not by case.
        var grantedRights = new HashSet<string>(
            await repo.GetControlRightsAsync(user.GROUP_ID, ct), StringComparer.OrdinalIgnoreCase);

        bool hasBelowCostRight = grantedRights.Contains("BelowCostSales");

        // Step 2a — is this device already registered?
        int shopId = 0;
        string posCode = "", computerName = "", invoicePrinterName = "";

        var device = await repo.FindDeviceAsync(req.DeviceId!, ct);
        if (device is not null)
        {
            if (!device.isTabActive)
                throw new ForbiddenException("This tablet has been deactivated by administrator");

            shopId = device.shop_id;
            posCode = device.POS_code ?? "";
            computerName = device.computer_name ?? "";
            invoicePrinterName = device.InvoicePrinterName;
        }

        // Step 2b — new device: claim a free pre-allocated slot atomically
        if (shopId == 0)
        {
            var claimed = await repo.ClaimFreeSlotAsync(req.DeviceId!, ct)
                ?? throw new ForbiddenException("No tablet slots available. Please contact HO to add a tablet.");

            shopId = claimed.shop_id;
            posCode = claimed.POS_code ?? "";
            computerName = claimed.computer_name ?? "";
            invoicePrinterName = claimed.InvoicePrinterName;
        }

        // Step 3 — shop name for the response
        var shopName = await repo.GetShopNameAsync(shopId, ct);

        // Step 3b — may this user's group work at the shop this device belongs to?
        //
        // The shop comes from the device (tblComputerList), and before this check existed
        // a user from one shop could sign in on another shop's tablet and transact against
        // its stock and its cash drawer. Candela's own model for this is
        // tblSecurityGroupShops plus tblSecurityGroup.IsSelectedShop.
        var access = await CheckShopAccessAsync(user.GROUP_ID, shopId, ct);
        if (!access.Allowed)
        {
            AppLog.Warn("Login refused: user {0} (group {1}) at shop {2} - {3}",
                user.user_id, user.GROUP_ID, shopId, access.Reason);
            throw new ForbiddenException(access.Reason!);
        }
        if (access.UnconfiguredGroup)
        {
            AppLog.Warn(
                "Group {0} has no rows in tblSecurityGroupShops and IsSelectedShop=0, so user {1} " +
                "was allowed at shop {2} on the assumption the mapping was never configured. " +
                "Fill in Group Shop Rights, then set Security:StrictShopRights=true to enforce.",
                user.GROUP_ID, user.user_id, shopId);
        }

        // Step 4 — issue the JWT
        var controlRightsStr = string.Join(",", grantedRights);
        var token = JwtHelper.Generate(user.user_id, user.User_name ?? "", shopId, posCode,
            req.DeviceId!, user.GROUP_NAME, user.GROUP_TYPE, user.SaleReturnLimit,
            hasBelowCostRight, controlRightsStr);

        return new LoginResponse
        {
            Token = token,
            UserId = user.user_id,
            UserName = user.User_name ?? "",
            ShopId = shopId,
            ShopName = shopName,
            PosCode = posCode,
            ComputerName = computerName,
            InvoicePrinterName = invoicePrinterName,
            AllowDiscountEditing = user.AllowPOSDiscountEditing,
            AllowPriceEditing = user.AllowPOSPriceEditing,
            // Either flag grants the button; is_open_adjust separately decides whether the
            // amount is uncapped.
            CanAdjust = user.ApplyAdjustment || user.ApplyOpenAdjustment,
            IsOpenAdjust = user.ApplyOpenAdjustment,
            ControlRights = new List<string>(grantedRights),
        };
    }

    public async Task<WebLoginResponse> WebLoginAsync(WebLoginRequest req, CancellationToken ct)
    {
        // Same credential check as the tablet: same table, same cipher, same generic
        // message on any failure. What is deliberately absent is everything about a
        // tablet seat — FindDeviceAsync / ClaimFreeSlotAsync — and the shop-entitlement
        // gate that follows it, because neither concept applies to a browser tab signing
        // in to Configuration/Security/etc.
        var user = await repo.FindUserAsync(req.Username!, ct);
        if (user is null)
            throw new UnauthorizedException(BadCredentials);

        if (string.IsNullOrEmpty(user.User_log_password))
            throw new UnauthorizedException(BadCredentials);

        var decrypted = SymmetricEncryption.Decrypt(user.User_log_password, "f");
        if (!req.Password!.Equals(decrypted, StringComparison.Ordinal))
            throw new UnauthorizedException(BadCredentials);

        // shop_id 0, pos_code "" and device_id "WEB" — the same JwtHelper every POS token
        // goes through, so this token validates on Candela.Api exactly like a tablet's
        // does. The POS-only claims (scr_rights, below_cost_right) are left at their
        // defaults; back-office rights are looked up per screen, not baked into the token
        // — see GetFormRightsAsync.
        var token = JwtHelper.Generate(user.user_id, user.User_name ?? "", 0, "",
            "WEB", user.GROUP_NAME, user.GROUP_TYPE, user.SaleReturnLimit);

        AppLog.Info("Web login: user {0} ({1}) signed in to Candela_WebInterface.",
            user.user_id, user.User_name);

        return new WebLoginResponse
        {
            Token = token,
            UserId = user.user_id,
            UserName = user.User_name ?? "",
            GroupName = user.GROUP_NAME,
            GroupType = user.GROUP_TYPE,
        };
    }

    public async Task<FormRightsResponse> GetFormRightsAsync(int userId, string formName, CancellationToken ct)
    {
        var groupId = await repo.GetGroupIdAsync(userId, ct);

        // No group row for this user id: same shape as "nothing configured" rather than an
        // error, so a screen just renders read-only instead of the request failing.
        var controls = groupId == 0
            ? Array.Empty<string>()
            : await repo.GetFormControlRightsAsync(groupId, formName, ct);

        return new FormRightsResponse
        {
            FormName = formName,
            Controls = new List<string>(controls),
        };
    }

    public async Task<SupervisorResponse> SupervisorAsync(SupervisorRequest req, int cashierUserId,
        int cashierShopId, int cashierGroupType, CancellationToken ct)
    {
        var supervisor = await repo.FindUserAsync(req.Username!, ct);
        if (supervisor is null)
            throw new UnauthorizedException(BadSupervisorCredentials);

        if (string.IsNullOrEmpty(supervisor.User_log_password))
            throw new UnauthorizedException(BadSupervisorCredentials);

        var decrypted = SymmetricEncryption.Decrypt(supervisor.User_log_password, "f");
        if (!req.Password!.Equals(decrypted, StringComparison.Ordinal))
            throw new UnauthorizedException(BadSupervisorCredentials);

        // Rule 1 — nobody authorises themselves.
        // frmOverrideLogin.vb:92
        //   If txtLoginID.Text.Trim.ToUpper = gObjUserInfo.LoginID.ToUpper Then Return False
        // Compared by user_id rather than by login string: same identity, and immune to
        // case or whitespace differences.
        if (supervisor.user_id == cashierUserId)
        {
            AppLog.Warn("Supervisor override refused: user {0} tried to authorise themselves at shop {1}",
                cashierUserId, cashierShopId);
            throw new ForbiddenException("You cannot authorise your own override. Ask another user to sign in.");
        }

        // Rules 2 and 3 — same group type, and entitled to this shop.
        // frmOverrideLogin.vb:94-101 skips both on Candela Basic (Version = 1).
        if (!await IsBasicEditionAsync(ct))
        {
            if (supervisor.GROUP_TYPE != cashierGroupType)
            {
                AppLog.Warn("Supervisor override refused: supervisor {0} group type {1} does not match " +
                            "cashier {2} group type {3} at shop {4}",
                    supervisor.user_id, supervisor.GROUP_TYPE, cashierUserId, cashierGroupType, cashierShopId);
                throw new ForbiddenException("Could not authorise because the user belongs to a different Group/Shop.");
            }

            var supAccess = await CheckShopAccessAsync(supervisor.GROUP_ID, cashierShopId, ct);
            if (!supAccess.Allowed)
            {
                AppLog.Warn("Supervisor override refused: supervisor {0} (group {1}) not entitled to shop {2} - {3}",
                    supervisor.user_id, supervisor.GROUP_ID, cashierShopId, supAccess.Reason);
                throw new ForbiddenException("Could not authorise because the user belongs to a different Group/Shop.");
            }
        }

        AppLog.Info("Supervisor override granted: supervisor {0} for user {1} at shop {2}",
            supervisor.user_id, cashierUserId, cashierShopId);

        return new SupervisorResponse
        {
            SupervisorId = supervisor.user_id,
            SupervisorName = supervisor.User_name ?? "",
        };
    }

    public async Task BlocklistTokenAsync(string rawToken, CancellationToken ct)
    {
        var sig = JwtHelper.ExtractSignature(rawToken);
        var expires = JwtHelper.ExtractExpiry(rawToken);

        if (string.IsNullOrEmpty(sig)) return;

        // A token whose exp could not be read still has to be revoked; 24h matches the
        // default token lifetime so the row goes stale on its own.
        if (expires == DateTime.MinValue) expires = DateTime.UtcNow.AddHours(24);

        await repo.BlocklistTokenAsync(sig, expires, ct);
    }

    private async Task<AuthRules.ShopAccessResult> CheckShopAccessAsync(int groupId, int shopId,
        CancellationToken ct)
    {
        if (shopId <= 0)
            return AuthRules.Decide(shopId, default, options.StrictShopRights);

        var facts = await repo.GetShopAccessFactsAsync(groupId, shopId, ct);
        if (facts is null)
            return AuthRules.CouldNotVerify();

        return AuthRules.Decide(shopId, facts.Value, options.StrictShopRights);
    }

    /// <summary>
    /// Candela edition flag, stored encrypted in tblRCMSConfiguration: 1 means Basic.
    /// frmOverrideLogin.vb:94 relaxes the group/shop pairing on Basic, so we match it.
    /// If the flag cannot be read we return false, which keeps the stricter path.
    /// </summary>
    private async Task<bool> IsBasicEditionAsync(CancellationToken ct)
    {
        try
        {
            var raw = await repo.GetConfigValueAsync("Version", ct);
            if (string.IsNullOrWhiteSpace(raw)) return false;

            return SymmetricEncryption.Decrypt(raw, "f") == "1";
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not read the Candela Version flag, applying strict override rules: {0}", ex.Message);
            return false;
        }
    }
}
