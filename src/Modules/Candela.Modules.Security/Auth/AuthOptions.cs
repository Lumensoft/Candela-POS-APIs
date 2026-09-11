namespace Candela.Modules.Security.Auth;

/// <summary>
/// The one Security:* setting login reads.
///
/// It was ConfigurationManager.AppSettings in the .NET Framework host; here it is
/// injected, same key, same default.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>
    /// True refuses users whose group has no shop rights configured at all. False — the
    /// default, and what the net48 host shipped with — lets them through and logs a
    /// warning, because an empty tblSecurityGroupShops means the screen was never filled
    /// in rather than that access was denied. See AuthRules.Decide.
    /// </summary>
    public bool StrictShopRights { get; set; }
}
