using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using DAL;
using static Utility.Utility;

namespace CandelaPOS.Infrastructure
{
    /// <summary>
    /// Loads Candela globals that DAL methods read at call time.
    /// Call Initialize() once at Application_Start; call PrepareRequest()
    /// before any DAL call within a request.
    /// </summary>
    public static class CandelaBootstrap
    {
        private static volatile DataTable _configCache;
        private static readonly object _lock = new object();
        private static System.Threading.Timer _configRefreshTimer;

        public static string ConnectionString =>
            ConfigurationManager.ConnectionStrings["CON_STR"].ConnectionString;

        public static void Initialize()
        {
            LoadConfigCache();
            // Stored in a static field so the GC does not collect it.
            _configRefreshTimer = new System.Threading.Timer(_ => RefreshConfigCache(), null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
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

            var cache = _configCache;
            if (cache == null)
            {
                LoadConfigCache();
                cache = _configCache;
            }

            gObjMyAppHashTable[EnumHashTableKeyConstants.GetSystemConfigurationList.ToString()] = cache;
        }

        /// <summary>
        /// Reloads tblRCMSConfiguration into the static cache.
        /// Safe to call periodically; uses a lock to avoid concurrent reloads.
        /// </summary>
        public static void RefreshConfigCache()
        {
            lock (_lock)
            {
                LoadConfigCache();
            }
        }

        public static Dictionary<string, string> GetRCMSConfig()
        {
            var cache = _configCache;
            if (cache == null)
            {
                LoadConfigCache();
                cache = _configCache;
            }
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow row in cache.Rows)
            {
                var name  = row["Configuration Name"]?.ToString();
                var value = row["Configuration Value"]?.ToString() ?? "";
                if (name != null) d[name] = value;
            }
            return d;
        }

        private static void LoadConfigCache()
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
            _configCache = dt;
        }
    }
}
