using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;

namespace CandelaPOS.Features.Auth
{
    /// <summary>
    /// Authorisation rules shared by login and supervisor override.
    ///
    /// Deliberately free of any Candela DLL dependency — plain ADO.NET and C# only —
    /// so this file compiles unchanged on .NET 10 when the API is split from the
    /// .NET Framework host. Anything that needs Utility.dll (password decryption,
    /// the encrypted Version flag) stays in the controller.
    /// </summary>
    public static class AuthRules
    {
        public sealed class ShopAccessResult
        {
            /// <summary>False means the caller must refuse the request.</summary>
            public bool Allowed { get; set; }

            /// <summary>Message safe to show the user. Null when allowed.</summary>
            public string Reason { get; set; }

            /// <summary>
            /// True when the group had no shop rights configured at all and we allowed
            /// the request on that basis. Worth logging: it is the difference between
            /// "this shop is permitted" and "nobody ever filled the table in".
            /// </summary>
            public bool UnconfiguredGroup { get; set; }
        }

        /// <summary>
        /// Set Security:StrictShopRights=true to refuse users whose group has no shop
        /// rights configured. Off by default on purpose — see CheckShopAccess.
        /// </summary>
        public static bool StrictShopRights
        {
            get
            {
                bool v;
                return bool.TryParse(ConfigurationManager.AppSettings["Security:StrictShopRights"], out v) && v;
            }
        }

        /// <summary>
        /// May a user in <paramref name="groupId"/> operate at <paramref name="shopId"/>?
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
        public static ShopAccessResult CheckShopAccess(SqlConnection con, int groupId, int shopId)
        {
            if (con == null) throw new ArgumentNullException("con");

            if (shopId <= 0)
                return Deny("This device is not assigned to a shop. Please contact Head Office.");

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

            int shopOpen = 0, allShops = 0, configured = 0, thisShop = 0;

            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.CommandTimeout = 15;
                cmd.Parameters.Add("@sid", SqlDbType.Int).Value = shopId;
                cmd.Parameters.Add("@gid", SqlDbType.Int).Value = groupId;

                using (var rdr = cmd.ExecuteReader())
                {
                    if (!rdr.Read())
                        return Deny("Could not verify shop access. Please contact Head Office.");

                    shopOpen   = Convert.ToInt32(rdr["ShopOpen"]);
                    allShops   = Convert.ToInt32(rdr["AllShops"]);
                    configured = Convert.ToInt32(rdr["ConfiguredCount"]);
                    thisShop   = Convert.ToInt32(rdr["ThisShop"]);
                }
            }

            if (shopOpen == 0)
                return Deny("This shop is closed and cannot be used.");

            if (allShops != 0) return Allow();     // group is granted every shop
            if (thisShop != 0) return Allow();     // group is granted this shop explicitly

            if (configured == 0 && !StrictShopRights)
                return new ShopAccessResult { Allowed = true, UnconfiguredGroup = true };

            return Deny("You are not authorised to work at this shop.");
        }

        private static ShopAccessResult Allow()
            => new ShopAccessResult { Allowed = true };

        private static ShopAccessResult Deny(string reason)
            => new ShopAccessResult { Allowed = false, Reason = reason };
    }
}
