namespace Candela.Modules.Security.Auth;

/// <summary>
/// Authorisation rules shared by login and supervisor override.
///
/// Ported from the net48 AuthRules, which was deliberately written without any Candela
/// DLL dependency so it could move here unchanged. Two things did change and nothing else:
/// the four counts it decides on now come from AuthRepository rather than a SqlConnection
/// it opens itself, and Security:StrictShopRights arrives as a parameter instead of off
/// ConfigurationManager. The order of the checks and every message are identical.
/// </summary>
public static class AuthRules
{
    public sealed class ShopAccessResult
    {
        /// <summary>False means the caller must refuse the request.</summary>
        public bool Allowed { get; set; }

        /// <summary>Message safe to show the user. Null when allowed.</summary>
        public string? Reason { get; set; }

        /// <summary>
        /// True when the group had no shop rights configured at all and we allowed
        /// the request on that basis. Worth logging: it is the difference between
        /// "this shop is permitted" and "nobody ever filled the table in".
        /// </summary>
        public bool UnconfiguredGroup { get; set; }
    }

    /// <summary>
    /// The four facts the decision needs, as read from the database.
    ///   ShopOpen        the shop exists and has no closing_date
    ///   AllShops        tblSecurityGroup.IsSelectedShop — group is granted every shop
    ///   ConfiguredCount how many rows the group has in tblSecurityGroupShops at all
    ///   ThisShop        whether one of them is this shop
    /// </summary>
    public readonly record struct ShopAccessFacts(int ShopOpen, int AllShops, int ConfiguredCount, int ThisShop);

    /// <summary>
    /// May a user in this group operate at this shop?
    ///
    /// Candela's own model, from the Group Shop Rights screen:
    ///   tblSecurityGroupShops       maps a group to the shops it may use
    ///   tblSecurityGroup.IsSelectedShop = 1 means every shop
    ///
    /// Two checks always apply because the desktop applies them too:
    ///   shop_id must be non-zero            (frmLogin.vb — "Issue # 447")
    ///   the shop must not be closed         (SecurityUserDAL.FindValidUser — Issue#3546,
    ///                                        GroupShopRightsDAL.GetAll — issue 1269)
    ///
    /// The group check is fail-open when the group has NO rows at all and
    /// IsSelectedShop is 0. That combination means the table was never filled in,
    /// not that access was denied, and refusing there would lock every user out of
    /// a site that has simply never opened that screen. Once a group has any rows,
    /// an administrator has expressed intent and we honour it exactly.
    /// Security:StrictShopRights=true removes the fail-open.
    /// </summary>
    public static ShopAccessResult Decide(int shopId, ShopAccessFacts facts, bool strictShopRights)
    {
        if (shopId <= 0)
            return Deny("This device is not assigned to a shop. Please contact Head Office.");

        if (facts.ShopOpen == 0)
            return Deny("This shop is closed and cannot be used.");

        if (facts.AllShops != 0) return Allow();     // group is granted every shop
        if (facts.ThisShop != 0) return Allow();     // group is granted this shop explicitly

        if (facts.ConfiguredCount == 0 && !strictShopRights)
            return new ShopAccessResult { Allowed = true, UnconfiguredGroup = true };

        return Deny("You are not authorised to work at this shop.");
    }

    /// <summary>
    /// The answer when the facts query returned nothing at all — the net48 version
    /// returned this from inside CheckShopAccess when the reader had no row.
    /// </summary>
    public static ShopAccessResult CouldNotVerify() =>
        Deny("Could not verify shop access. Please contact Head Office.");

    private static ShopAccessResult Allow() => new() { Allowed = true };

    private static ShopAccessResult Deny(string reason) => new() { Allowed = false, Reason = reason };
}
