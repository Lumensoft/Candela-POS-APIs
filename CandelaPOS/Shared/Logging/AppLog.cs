using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web;

namespace CandelaPOS.Shared.Logging
{
    /// <summary>
    /// Minimal rolling-file logger. Deliberately dependency-free: shop servers are
    /// offline and adding a NuGet package would force every developer and every
    /// deployment to restore it.
    ///
    /// One file per day under App_Data\logs (App_Data is never served by IIS, so
    /// logs can't be fetched over HTTP). Files older than Logging:RetentionDays are
    /// deleted on first write of a new day — shop tills have small disks.
    ///
    /// Every method swallows its own errors. Logging must never be the reason a
    /// sale fails.
    /// </summary>
    public static class AppLog
    {
        private static readonly object Gate = new object();
        private static string _currentFile;
        private static DateTime _currentDate = DateTime.MinValue;

        private static int RetentionDays
        {
            get
            {
                int d;
                return int.TryParse(ConfigurationManager.AppSettings["Logging:RetentionDays"], out d) && d > 0
                    ? d : 14;
            }
        }

        public static void Info(string message, params object[] args)  => Write("INFO ", message, args, null);
        public static void Warn(string message, params object[] args)  => Write("WARN ", message, args, null);

        public static void Error(Exception ex, string message, params object[] args)
            => Write("ERROR", message, args, ex);

        /// <summary>
        /// Correlation id for the current request. Set by CorrelationIdHandler; falls
        /// back to "-" outside a request (e.g. Application_Start).
        /// </summary>
        public static string CorrelationId
        {
            get
            {
                try
                {
                    var ctx = HttpContext.Current;
                    var id = ctx?.Items["correlation_id"] as string;
                    return string.IsNullOrEmpty(id) ? "-" : id;
                }
                catch { return "-"; }
            }
        }

        private static void Write(string level, string message, object[] args, Exception ex)
        {
            try
            {
                string text;
                try   { text = args != null && args.Length > 0 ? string.Format(CultureInfo.InvariantCulture, message, args) : message; }
                catch { text = message; }   // bad format string must not lose the message

                var sb = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                    .Append(" [").Append(level).Append("] ")
                    .Append('[').Append(CorrelationId).Append("] ")
                    .Append(text);

                if (ex != null)
                    sb.AppendLine().Append("        ").Append(ex.ToString().Replace("\n", "\n        "));

                AppendLine(sb.ToString());
            }
            catch { /* logging must never throw */ }
        }

        private static void AppendLine(string line)
        {
            lock (Gate)
            {
                try
                {
                    var today = DateTime.Today;
                    if (today != _currentDate || _currentFile == null)
                    {
                        var dir = ResolveLogDir();
                        Directory.CreateDirectory(dir);
                        _currentFile = Path.Combine(dir, "pos-" + today.ToString("yyyy-MM-dd") + ".log");
                        _currentDate = today;
                        Purge(dir, today);
                    }
                    File.AppendAllText(_currentFile, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { /* disk full / permission denied — give up silently */ }
            }
        }

        private static string ResolveLogDir()
        {
            var configured = ConfigurationManager.AppSettings["Logging:Directory"];
            if (!string.IsNullOrWhiteSpace(configured)) return configured;

            try
            {
                var root = HttpContext.Current?.Server.MapPath("~/App_Data");
                if (!string.IsNullOrEmpty(root)) return Path.Combine(root, "logs");
            }
            catch { }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "logs");
        }

        private static void Purge(string dir, DateTime today)
        {
            try
            {
                var cutoff = today.AddDays(-RetentionDays);
                foreach (var f in Directory.GetFiles(dir, "pos-*.log"))
                {
                    DateTime stamp;
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (name.Length == 14 &&
                        DateTime.TryParseExact(name.Substring(4), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                               DateTimeStyles.None, out stamp) &&
                        stamp < cutoff)
                    {
                        File.Delete(f);
                    }
                }
            }
            catch { }
        }
    }
}
