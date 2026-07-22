using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Web;
using DAL;
using static Utility.Utility;

namespace CandelaPOS.Infrastructure
{
    /// <summary>
    /// Loads Candela globals that DAL methods read at call time.
    /// Call Initialize() once at Application_Start; call PrepareRequest()
    /// before any DAL call within a request.
    /// Config is read fresh from tblRCMSConfiguration on every request (once per request,
    /// cached in HttpContext.Items) so DB changes take effect immediately with no restart.
    /// </summary>
    public static class CandelaBootstrap
    {
        private const string RequestCfgKey = "_rcmsDt";

        public static string ConnectionString =>
            ConfigurationManager.ConnectionStrings["CON_STR"].ConnectionString;

        public static void Initialize()
        {
            try { EnsureSchema(); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("CandelaBootstrap.EnsureSchema failed: {0}", ex);
            }
        }

        private static void EnsureSchema()
        {
            using (var con = new SqlConnection(ConnectionString))
            {
                con.Open();
                new SqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'tblPOSTokenBlocklist')
CREATE TABLE tblPOSTokenBlocklist (
    id          INT IDENTITY(1,1) PRIMARY KEY,
    token_sig   VARCHAR(512)  NOT NULL,
    blocked_at  DATETIME      NOT NULL DEFAULT GETDATE(),
    expires_at  DATETIME      NOT NULL,
    INDEX IX_POSBlocklistSig (token_sig)
)", con).ExecuteNonQuery();

                new SqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'tblPOSIdempotency')
CREATE TABLE tblPOSIdempotency (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    client_txn_guid VARCHAR(64)  NOT NULL,
    sale_id         INT          NOT NULL DEFAULT 0,
    shop_id         INT          NOT NULL,
    created_at      DATETIME     NOT NULL DEFAULT GETDATE(),
    UNIQUE INDEX IX_POSIdempotencyGuid (client_txn_guid, shop_id)
)", con).ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Must be called at the start of every request that touches the DAL.
        /// Sets the two globals Add() depends on.
        /// </summary>
        public static void PrepareRequest()
        {
            SQLHelper.CON_STR = ConnectionString;
            gObjMyAppHashTable[EnumHashTableKeyConstants.GetSystemConfigurationList.ToString()] = GetConfigDataTable();
        }

        /// <summary>
        /// Returns tblRCMSConfiguration as a key/value dictionary.
        /// Reads from DB once per request; subsequent calls within the same request
        /// return the per-request cached copy from HttpContext.Items.
        /// </summary>
        public static Dictionary<string, string> GetRCMSConfig()
        {
            var dt = GetConfigDataTable();
            var d  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow row in dt.Rows)
            {
                var name  = row["Configuration Name"]?.ToString();
                var value = row["Configuration Value"]?.ToString() ?? "";
                if (name != null) d[name] = value;
            }
            return d;
        }

        // Returns config DataTable for the current request.
        // Loaded from DB on first call; subsequent calls within the same request reuse
        // the copy stored in HttpContext.Items — one DB read per request, never stale.
        private static DataTable GetConfigDataTable()
        {
            var ctx = HttpContext.Current;
            if (ctx?.Items[RequestCfgKey] is DataTable cached)
                return cached;

            var dt = LoadConfigFromDb();
            if (ctx != null)
                ctx.Items[RequestCfgKey] = dt;
            return dt;
        }

        private static DataTable LoadConfigFromDb()
        {
            var dt = new DataTable(EnumHashTableKeyConstants.GetSystemConfigurationList.ToString());
            using (var da = new SqlDataAdapter(
                "SELECT config_no AS [Configuration No]," +
                "       config_name AS [Configuration Name]," +
                "       config_value AS [Configuration Value]" +
                " FROM tblRCMSConfiguration ORDER BY config_no",
                ConnectionString))
            {
                da.Fill(dt);
            }
            return dt;
        }
    }
}
