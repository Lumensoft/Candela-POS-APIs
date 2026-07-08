using System;
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
        private static DataTable _configCache;
        private static readonly object _lock = new object();

        public static string ConnectionString =>
            ConfigurationManager.ConnectionStrings["CON_STR"].ConnectionString;

        public static void Initialize()
        {
            LoadConfigCache();
        }

        /// <summary>
        /// Must be called at the start of every request that touches the DAL.
        /// Sets the two globals Add() depends on.
        /// </summary>
        public static void PrepareRequest()
        {
            SQLHelper.CON_STR = ConnectionString;

            if (_configCache == null)
                LoadConfigCache();

            gObjMyAppHashTable[EnumHashTableKeyConstants.GetSystemConfigurationList.ToString()] = _configCache;
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
