using Candela.Modules.Sales.Pos.Dtos;

namespace Candela.Modules.Sales.Pos;

/// <summary>
/// The till's cash-flow operations.
///
/// The reads (cash-status, shift-status) are assembled here from the repository's
/// breakdown. The four writes go through POSCashManagmentDAL, which only runs on .NET
/// Framework, so they are forwarded to the legacy host — with one exception: shift-close
/// computes its breakdown here first (so cash-status, shift-status and the close all use
/// the one query) and does the "is a shift even open" check here, then forwards.
/// </summary>
public interface IPosService
{
    /// <summary>GET /api/pos/cash-status — the breakdown without <c>net_sales</c>.</summary>
    Task<ShiftStatusResponse> GetCashStatusAsync(int shopId, string posCode, CancellationToken ct);

    /// <summary>GET /api/pos/shift-status — the same breakdown, plus <c>net_sales</c>.</summary>
    Task<ShiftStatusResponse> GetShiftStatusAsync(int shopId, string posCode, CancellationToken ct);

    /// <summary>GET /api/pos/shifts — a page of this till's closings, plus filtered totals.</summary>
    Task<ShiftSearchResponse> GetShiftsAsync(int shopId, string posCode,
        string? from, string? to, int page, int pageSize, CancellationToken ct);

    /// <summary>GET /api/pos/shifts/{closingId}/detail — the Received / Skimmed entries under a closing.</summary>
    Task<ShiftDetailResponse> GetShiftDetailAsync(int closingId, int shopId, string posCode,
        CancellationToken ct);

    /// <summary>DELETE a shift detail entry, rolling its amount back out. False when not this till's.</summary>
    Task<bool> DeleteShiftDetailAsync(int closingId, int detailId, int shopId, string posCode,
        CancellationToken ct);

    /// <summary>POST /api/pos/cash-skim — records a mid-shift removal (DAL, via the legacy host).</summary>
    Task<CashMovementResponse> CashSkimAsync(CashMovementRequest req, CancellationToken ct);

    /// <summary>POST /api/pos/cash-receive — records a mid-shift top-up (DAL, via the legacy host).</summary>
    Task<CashMovementResponse> CashReceiveAsync(CashMovementRequest req, CancellationToken ct);

    /// <summary>POST /api/pos/shift-open — opens the till shift, idempotently (DAL, via the legacy host).</summary>
    Task<ShiftOpenResponse> OpenShiftAsync(CancellationToken ct);

    /// <summary>
    /// POST /api/pos/shift-close. Throws BusinessRuleException (422) when no shift is open
    /// — same status and message as the net48 endpoint — before forwarding the close.
    /// </summary>
    Task<ShiftCloseResponse> CloseShiftAsync(ShiftCloseRequest req, int shopId, string posCode,
        CancellationToken ct);
}
