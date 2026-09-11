using Candela.Modules.Sales.Pos.Dtos;

namespace Candela.Modules.Sales.Pos;

/// <summary>
/// The till's cash-flow reads, and the two writes that do not touch the DAL.
///
/// The net48 PosController split cleanly: cash-status / shift-status / shifts /
/// shift-detail and the shift-detail DELETE were all plain SQL, while cash-skim,
/// cash-receive, shift-open and shift-close went through POSCashManagmentDAL. The reads
/// and the DELETE live here; the DAL writes stay in the legacy host and PosService
/// forwards to them.
/// </summary>
public interface IPosRepository
{
    /// <summary>
    /// Opening cash, cash received, cash skimmed and cash sales for the open shift on this
    /// till — the figure both cash-status and shift-status are built from, and the one
    /// shift-close reconciles against.
    /// </summary>
    Task<ShiftBreakdown> ComputeShiftBreakdownAsync(int shopId, string posCode, CancellationToken ct);

    /// <summary>The most recently closed shift's summary for this till, or null when there is none.</summary>
    Task<LastClosedShift?> GetLastClosedShiftSummaryAsync(int shopId, string posCode, CancellationToken ct);

    /// <summary>One page of shift closings for this till, plus totals over the whole filtered set.</summary>
    Task<ShiftSearchResponse> SearchShiftsAsync(int shopId, string posCode,
        string? from, string? to, int page, int pageSize, CancellationToken ct);

    /// <summary>The Cash Received / Cash Skimmed entries under one shift closing.</summary>
    Task<ShiftDetailResponse> GetShiftDetailAsync(int closingId, int shopId, string posCode,
        CancellationToken ct);

    /// <summary>
    /// Removes one detail entry and rolls its amount back out of the shift's running
    /// CashReceived / CashSkimmed total, in one transaction. False when the entry does not
    /// exist for this closing/till — the caller turns that into 404, same as the net48
    /// "rows == 0 -> rollback -> NotFound".
    /// </summary>
    Task<bool> DeleteShiftDetailAsync(int closingId, int detailId, int shopId, string posCode,
        CancellationToken ct);
}

/// <summary>
/// The shift cash figures, mirroring the net48 PosController.ShiftBreakdown private class.
/// Populated from SQL only — the DAL is never involved in computing these.
/// </summary>
public sealed class ShiftBreakdown
{
    public bool ShiftOpen { get; set; }
    public int PosCashManagementId { get; set; }
    public double Opening { get; set; }
    public double CashReceived { get; set; }
    public double CashSkimmed { get; set; }
    public double CashSales { get; set; }
    public DateTime? OpeningTime { get; set; }
}
