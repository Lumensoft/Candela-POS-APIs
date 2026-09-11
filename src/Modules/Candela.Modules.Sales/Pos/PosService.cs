using Candela.Modules.Sales.Pos.Dtos;
using Candela.Platform.Legacy;
using Candela.Shared.Exceptions;

namespace Candela.Modules.Sales.Pos;

/// <summary>
/// Ported from the net48 PosController. The cash breakdown maths, the field names, the
/// 422 on "no open shift" and the forwarded DAL calls are all unchanged.
/// </summary>
public sealed class PosService(IPosRepository repo, ILegacyHostClient legacy) : IPosService
{
    public Task<ShiftStatusResponse> GetCashStatusAsync(int shopId, string posCode, CancellationToken ct)
        => BuildStatusAsync(shopId, posCode, includeNetSales: false, ct);

    public Task<ShiftStatusResponse> GetShiftStatusAsync(int shopId, string posCode, CancellationToken ct)
        => BuildStatusAsync(shopId, posCode, includeNetSales: true, ct);

    private async Task<ShiftStatusResponse> BuildStatusAsync(int shopId, string posCode,
        bool includeNetSales, CancellationToken ct)
    {
        var bd = await repo.ComputeShiftBreakdownAsync(shopId, posCode, ct);

        double availableCash = bd.Opening + bd.CashReceived + bd.CashSales - bd.CashSkimmed;

        // last_closed only when there is no open shift — same as both net48 endpoints.
        var lastClosed = bd.ShiftOpen
            ? null
            : await repo.GetLastClosedShiftSummaryAsync(shopId, posCode, ct);

        return new ShiftStatusResponse
        {
            ShiftOpen = bd.ShiftOpen,
            Opening = bd.Opening,
            CashReceived = bd.CashReceived,
            CashSales = bd.CashSales,
            NetSales = includeNetSales ? bd.CashSales : null,
            CashSkimmed = bd.CashSkimmed,
            AvailableCash = availableCash,
            ShiftSince = bd.ShiftOpen && bd.OpeningTime.HasValue
                ? bd.OpeningTime.Value.ToString("yyyy-MM-dd HH:mm")
                : null,
            PosCode = posCode,
            ShopId = shopId,
            LastClosed = lastClosed,
            PosCashManagementId = bd.ShiftOpen ? bd.PosCashManagementId : null,
        };
    }

    // Pure reads and the DELETE pass straight through to the repository — no rules, but
    // kept on the service so the controller has one Pos dependency, not two.
    public Task<ShiftSearchResponse> GetShiftsAsync(int shopId, string posCode,
        string? from, string? to, int page, int pageSize, CancellationToken ct)
        => repo.SearchShiftsAsync(shopId, posCode, from, to, page, pageSize, ct);

    public Task<ShiftDetailResponse> GetShiftDetailAsync(int closingId, int shopId, string posCode,
        CancellationToken ct)
        => repo.GetShiftDetailAsync(closingId, shopId, posCode, ct);

    public Task<bool> DeleteShiftDetailAsync(int closingId, int detailId, int shopId, string posCode,
        CancellationToken ct)
        => repo.DeleteShiftDetailAsync(closingId, detailId, shopId, posCode, ct);

    public Task<CashMovementResponse> CashSkimAsync(CashMovementRequest req, CancellationToken ct)
        => legacy.PostAsync<CashMovementResponse>("pos/cash-skim", req, ct);

    public Task<CashMovementResponse> CashReceiveAsync(CashMovementRequest req, CancellationToken ct)
        => legacy.PostAsync<CashMovementResponse>("pos/cash-receive", req, ct);

    public Task<ShiftOpenResponse> OpenShiftAsync(CancellationToken ct)
        // No body: the legacy wrapper reads user_id / shop_id / pos_code off the X-Ctx-*
        // headers the client attaches, exactly as the net48 endpoint read them off the JWT.
        => legacy.PostAsync<ShiftOpenResponse>("pos/shift-open", new { }, ct);

    public async Task<ShiftCloseResponse> CloseShiftAsync(ShiftCloseRequest req, int shopId,
        string posCode, CancellationToken ct)
    {
        // The breakdown is computed here so cash-status, shift-status and the close all
        // read the same query — and so the "is anything open" check is answered before a
        // hop to the legacy host.
        var bd = await repo.ComputeShiftBreakdownAsync(shopId, posCode, ct);

        if (!bd.ShiftOpen)
            throw new BusinessRuleException("No open shift to close for this till.");

        var now = req.ClosingDate ?? DateTime.Now;
        double cashSubmitted = req.CashSubmitted;
        double closingCash = req.CashCounted - cashSubmitted;

        // Everything POSCashManagmentDAL.UpdateShiftClosing needs to build its model, plus
        // the optional denomination count. The legacy wrapper trusts these figures — they
        // are this process's breakdown, already computed above.
        var forward = new ShiftCloseLegacyRequest
        {
            PosCashManagementId = bd.PosCashManagementId,
            Opening = bd.Opening,
            CashReceived = bd.CashReceived,
            CashSkimmed = bd.CashSkimmed,
            NetSales = bd.CashSales,
            CashCounted = req.CashCounted,
            CashSubmitted = cashSubmitted,
            ClosingCash = closingCash,
            Notes = req.Notes ?? string.Empty,
            ClosedAt = now.ToString("yyyy-MM-dd HH:mm:ss"),
            Denominations = req.Denominations,
        };

        return await legacy.PostAsync<ShiftCloseResponse>("pos/shift-close", forward, ct);
    }
}
