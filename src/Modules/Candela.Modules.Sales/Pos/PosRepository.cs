using System.Data;
using System.Text;
using Candela.Modules.Sales.Pos.Dtos;
using Candela.Platform.Data;
using Dapper;

namespace Candela.Modules.Sales.Pos;

/// <summary>
/// Every statement is copied from the net48 PosController, aliases and parameter names
/// included.
///
/// One deliberate consolidation: the net48 GetCashStatus() had its own copy of the
/// open-shift query, identical to ComputeShiftBreakdown()'s except that it also selected
/// (and grouped by) m.POSDate into a local that was never returned. Both endpoints now
/// call ComputeShiftBreakdownAsync, so the dead column is gone and cash-status /
/// shift-status are guaranteed to report the same numbers — which is what the original
/// comment on ComputeShiftBreakdown said it existed to ensure.
/// </summary>
public sealed class PosRepository(IDb db) : IPosRepository
{
    public async Task<ShiftBreakdown> ComputeShiftBreakdownAsync(int shopId, string posCode,
        CancellationToken ct)
    {
        var bd = new ShiftBreakdown();

        const string openingSql = @"
SELECT isnull(ClosingCash, 0)
FROM   tblPOSCashManagement
WHERE  IsClosed = 1
  AND  POSCode  = @pos
  AND  ShopID   = @sid
  AND  POSDate  = (
        SELECT MAX(POSDate)
        FROM   tblPOSCashManagement
        WHERE  IsClosed = 1 AND POSCode = @pos AND ShopID = @sid
  )";
        bd.Opening = await db.ExecuteScalarAsync<double?>(openingSql, new { pos = posCode, sid = shopId }, ct) ?? 0;

        const string shiftSql = @"
SELECT TOP 1
    m.POSCashManagementID,
    m.OpeningTime,
    isnull(SUM(CASE WHEN d.Type = 'Received' THEN d.Amount ELSE 0 END), 0) AS cash_received,
    isnull(SUM(CASE WHEN d.Type = 'Skimmed'  THEN d.Amount ELSE 0 END), 0) AS cash_skimmed
FROM tblPOSCashManagement m
LEFT JOIN tblPOSCashManagementDetail d
    ON d.POSCashManagementID = m.POSCashManagementID AND d.ShopId = m.ShopID
WHERE m.IsClosed = 0
  AND m.POSCode  = @pos
  AND m.ShopID   = @sid
GROUP BY m.POSCashManagementID, m.OpeningTime";

        var shift = await db.QueryFirstOrDefaultAsync<ShiftRow>(shiftSql, new { pos = posCode, sid = shopId }, ct);
        if (shift is not null)
        {
            bd.ShiftOpen = true;
            bd.PosCashManagementId = shift.POSCashManagementID;
            bd.OpeningTime = shift.OpeningTime;
            bd.CashReceived = shift.cash_received;
            bd.CashSkimmed = shift.cash_skimmed;
        }

        if (bd.ShiftOpen)
        {
            // Activate_Gift_Card is a plain (unencrypted) tblRCMSConfiguration flag —
            // Utility.GetSystemConfigurationValue reads it with exactly this query.
            string giftCardOn = await db.QueryFirstOrDefaultAsync<string>(
                "SELECT config_value FROM tblRCMSConfiguration WHERE config_name = 'Activate_Gift_Card'",
                null, ct) ?? "";

            string salesSql;
            if (giftCardOn.ToUpper() == "TRUE")
            {
                salesSql = @"
SELECT isnull(SUM(Cash_amt), 0)
FROM   tblSales
WHERE  pos_code = @pos AND shop_id = @sid
  AND  sale_date BETWEEN @from AND @to
UNION ALL
SELECT isnull(SUM(Cash_amount), 0)
FROM   tblGiftCardLedger
WHERE  pos_code = @pos AND sale_shop_id = @sid
  AND  cash_amount > 0
  AND  sale_date BETWEEN @from AND @to";
            }
            else
            {
                salesSql = @"
SELECT isnull(SUM(Cash_amt), 0)
FROM   tblSales
WHERE  pos_code = @pos AND shop_id = @sid
  AND  sale_date BETWEEN @from AND @to";
            }

            // Dates are passed as the same "yyyy-MM-dd HH:mm:ss" strings the net48 code
            // built with AddWithValue, so SQL Server's conversion is unchanged.
            var rows = await db.QueryAsync<double?>(salesSql, new
            {
                pos = posCode,
                sid = shopId,
                from = bd.OpeningTime!.Value.ToString("yyyy-MM-dd HH:mm:ss"),
                to = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            }, ct);

            foreach (var r in rows)
                bd.CashSales += r ?? 0;
        }

        return bd;
    }

    public async Task<LastClosedShift?> GetLastClosedShiftSummaryAsync(int shopId, string posCode,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP 1 POSCashManagementID, POSDate, CashCounted, CashDifference
FROM   tblPOSCashManagement
WHERE  IsClosed = 1 AND POSCode = @pos AND ShopID = @sid
ORDER BY POSDate DESC";

        var row = await db.QueryFirstOrDefaultAsync<LastClosedRow>(sql, new { pos = posCode, sid = shopId }, ct);
        if (row is null) return null;

        return new LastClosedShift
        {
            PosCashManagementId = row.POSCashManagementID,
            ClosedAt = row.POSDate.ToString("yyyy-MM-dd HH:mm:ss"),
            CashCounted = row.CashCounted,
            CashDifference = row.CashDifference
        };
    }

    public async Task<ShiftSearchResponse> SearchShiftsAsync(int shopId, string posCode,
        string? from, string? to, int page, int pageSize, CancellationToken ct)
    {
        const string baseSql = @"
SELECT
    m.POSCashManagementID AS closing_id,
    m.POSCode             AS pos_code,
    m.POSDate             AS closing_date,
    isnull(m.CashCounted, 0)   AS cash_counted,
    isnull(m.CashSubmitted, 0) AS cash_submitted,
    isnull(m.ClosingCash, 0)   AS closing_cash,
    m.IsClosed                 AS is_closed,
    isnull(m.NetSales, 0)      AS net_sales,
    isnull(m.Opening, 0)       AS opening,
    m.OpeningTime               AS opening_time,
    isnull(m.CashSkimmed, 0)   AS cash_skimmed,
    isnull(m.CashReceived, 0)  AS cash_received,
    isnull(m.CashCounted, 0) - (isnull(m.Opening,0) + isnull(m.CashReceived,0) + isnull(m.NetSales,0) - isnull(m.CashSkimmed,0)) AS cash_difference,
    isnull(u.User_name, '')    AS user_name
FROM tblPOSCashManagement m
LEFT JOIN tblSecurityUser u ON u.user_id = m.UserID
WHERE m.ShopID = @sid AND m.POSCode = @pos";

        var where = new StringBuilder(baseSql);
        var p = new DynamicParameters();
        p.Add("@sid", shopId);
        p.Add("@pos", posCode);

        if (!string.IsNullOrEmpty(from) && DateTime.TryParse(from, out DateTime fromDt))
        {
            where.Append(" AND m.POSDate >= @fromDt");
            p.Add("@fromDt", fromDt.Date);
        }
        if (!string.IsNullOrEmpty(to) && DateTime.TryParse(to, out DateTime toDt))
        {
            where.Append(" AND m.POSDate < @toDt");
            p.Add("@toDt", toDt.Date.AddDays(1));
        }

        int offset = (page - 1) * pageSize;
        string finalSql = where.ToString()
            + " ORDER BY m.POSCashManagementID DESC"
            + $" OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY";

        string countSql = "SELECT COUNT(*) FROM (" + where.ToString() + ") AS cnt";

        string totalsSql = @"
SELECT
    isnull(SUM(cash_counted), 0)    AS cash_counted,
    isnull(SUM(cash_submitted), 0)  AS cash_submitted,
    isnull(SUM(closing_cash), 0)    AS closing_cash,
    isnull(SUM(net_sales), 0)       AS net_sales,
    isnull(SUM(opening), 0)         AS opening,
    isnull(SUM(cash_skimmed), 0)    AS cash_skimmed,
    isnull(SUM(cash_received), 0)   AS cash_received,
    isnull(SUM(cash_difference), 0) AS cash_difference
FROM (" + where.ToString() + ") totals_src";

        int total = await db.ExecuteScalarAsync<int>(countSql, p, ct);

        var totalsRow = await db.QueryFirstOrDefaultAsync<ShiftSearchTotals>(totalsSql, p, ct)
                        ?? new ShiftSearchTotals();

        var data = await db.QueryRowsAsync(finalSql, p, ct);

        return new ShiftSearchResponse
        {
            Total = total,
            Page = page,
            PageSize = pageSize,
            Count = data.Count,
            Data = data,
            Totals = totalsRow
        };
    }

    public async Task<ShiftDetailResponse> GetShiftDetailAsync(int closingId, int shopId,
        string posCode, CancellationToken ct)
    {
        const string sql = @"
SELECT d.POSCashManagementDetailID, d.Amount, d.DetailDate, d.Notes, d.Type
FROM tblPOSCashManagement m
INNER JOIN tblPOSCashManagementDetail d
    ON d.POSCashManagementID = m.POSCashManagementID AND d.ShopId = m.ShopID
WHERE m.POSCashManagementID = @closingId AND m.ShopID = @sid AND m.POSCode = @pos
ORDER BY d.DetailDate";

        var rows = await db.QueryAsync<DetailRow>(sql,
            new { closingId, sid = shopId, pos = posCode }, ct);

        var result = new ShiftDetailResponse { ClosingId = closingId };

        foreach (var r in rows)
        {
            var entry = new ShiftDetailEntry
            {
                Id = r.POSCashManagementDetailID,
                Amount = r.Amount,
                DetailDate = r.DetailDate.ToString("yyyy-MM-dd HH:mm:ss"),
                Notes = r.Notes ?? "",
            };

            if (r.Type == "Received") result.Received.Add(entry);
            else if (r.Type == "Skimmed") result.Skimmed.Add(entry);
        }

        return result;
    }

    public async Task<bool> DeleteShiftDetailAsync(int closingId, int detailId, int shopId,
        string posCode, CancellationToken ct)
    {
        try
        {
            return await db.InTransactionAsync<bool>(async (con, tx) =>
            {
                const string lookupSql = @"
SELECT d.Amount, d.Type
FROM tblPOSCashManagement m
INNER JOIN tblPOSCashManagementDetail d
    ON d.POSCashManagementID = m.POSCashManagementID AND d.ShopId = m.ShopID
WHERE m.POSCashManagementID = @closingId AND m.ShopID = @sid AND m.POSCode = @pos
      AND d.POSCashManagementDetailID = @detailId";

                var entry = await con.QueryFirstOrDefaultAsync<DeleteLookupRow>(new CommandDefinition(
                    lookupSql, new { closingId, sid = shopId, pos = posCode, detailId }, tx,
                    cancellationToken: ct));

                if (entry is null)
                    throw new DetailNotFound();   // rolls the (empty) transaction back -> caller 404s

                await con.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM tblPOSCashManagementDetail WHERE POSCashManagementDetailID = @detailId",
                    new { detailId }, tx, cancellationToken: ct));

                // Column name is interpolated, never a parameter — it is one of two
                // compile-time constants, chosen by the row's own Type, exactly as the
                // net48 code did.
                string aggregateColumn = entry.Type == "Received" ? "CashReceived" : "CashSkimmed";
                await con.ExecuteAsync(new CommandDefinition(
                    $"UPDATE tblPOSCashManagement SET {aggregateColumn} = ISNULL({aggregateColumn}, 0) - @amount " +
                    "WHERE POSCashManagementID = @closingId AND ShopID = @sid",
                    new { amount = entry.Amount, closingId, sid = shopId }, tx, cancellationToken: ct));

                return true;
            }, ct);
        }
        catch (DetailNotFound)
        {
            return false;
        }
    }

    /// <summary>Internal signal: no matching detail row, roll back and let the caller 404.</summary>
    private sealed class DetailNotFound : Exception;

    // Column names match the SELECT aliases so Dapper binds by name.
    private sealed class ShiftRow
    {
        public int POSCashManagementID { get; set; }
        public DateTime OpeningTime { get; set; }
        public double cash_received { get; set; }
        public double cash_skimmed { get; set; }
    }

    private sealed class LastClosedRow
    {
        public int POSCashManagementID { get; set; }
        public DateTime POSDate { get; set; }
        public double CashCounted { get; set; }
        public double CashDifference { get; set; }
    }

    private sealed class DetailRow
    {
        public int POSCashManagementDetailID { get; set; }
        public double Amount { get; set; }
        public DateTime DetailDate { get; set; }
        public string? Notes { get; set; }
        public string Type { get; set; } = "";
    }

    private sealed class DeleteLookupRow
    {
        public double Amount { get; set; }
        public string Type { get; set; } = "";
    }
}
