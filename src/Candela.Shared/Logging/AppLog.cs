using System.Globalization;
using System.Text;

namespace Candela.Shared.Logging;

/// <summary>
/// Rolling-file logger, deliberately dependency-free for the same reason as its
/// .NET Framework counterpart: shop servers are offline, and every extra package is
/// one more thing to restore on each machine and each deployment.
///
/// One file per day under App_Data/logs, older files removed on the first write of a
/// new day because shop tills have small disks. Every method swallows its own errors:
/// logging must never be the reason a sale fails.
///
/// The correlation id flows on an AsyncLocal rather than HttpContext.Current, which
/// does not exist outside .NET Framework. CorrelationIdMiddleware sets it per request.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly AsyncLocal<string?> Correlation = new();

    private static string? _currentFile;
    private static DateTime _currentDate = DateTime.MinValue;

    private static string _logDirectory = Path.Combine(AppContext.BaseDirectory, "App_Data", "logs");
    private static int _retentionDays = 14;

    /// <summary>Called once at startup so the logger can honour appsettings.</summary>
    public static void Configure(string? directory, int retentionDays)
    {
        if (!string.IsNullOrWhiteSpace(directory)) _logDirectory = directory!;
        if (retentionDays > 0) _retentionDays = retentionDays;
    }

    /// <summary>Correlation id for the current request, or "-" outside one.</summary>
    public static string CorrelationId
    {
        get => string.IsNullOrEmpty(Correlation.Value) ? "-" : Correlation.Value!;
        set => Correlation.Value = value;
    }

    public static void Info(string message, params object?[] args) => Write("INFO ", message, args, null);
    public static void Warn(string message, params object?[] args) => Write("WARN ", message, args, null);
    public static void Error(Exception? ex, string message, params object?[] args) => Write("ERROR", message, args, ex);

    private static void Write(string level, string message, object?[] args, Exception? ex)
    {
        try
        {
            string text;
            try { text = args is { Length: > 0 } ? string.Format(CultureInfo.InvariantCulture, message, args) : message; }
            catch { text = message; }   // a bad format string must not lose the message

            var sb = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(" [").Append(level).Append("] ")
                .Append('[').Append(CorrelationId).Append("] ")
                .Append(text);

            if (ex is not null)
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
                if (today != _currentDate || _currentFile is null)
                {
                    Directory.CreateDirectory(_logDirectory);
                    _currentFile = Path.Combine(_logDirectory, "pos-" + today.ToString("yyyy-MM-dd") + ".log");
                    _currentDate = today;
                    Purge(today);
                }
                File.AppendAllText(_currentFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* disk full or permission denied - give up silently */ }
        }
    }

    private static void Purge(DateTime today)
    {
        try
        {
            var cutoff = today.AddDays(-_retentionDays);
            foreach (var f in Directory.GetFiles(_logDirectory, "pos-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (name.Length == 14 &&
                    DateTime.TryParseExact(name.Substring(4), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                           DateTimeStyles.None, out var stamp) &&
                    stamp < cutoff)
                {
                    File.Delete(f);
                }
            }
        }
        catch { }
    }
}
