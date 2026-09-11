using Candela.Modules.CustomerClub.Customers.Dtos;
using Candela.Platform.Api;
using Candela.Shared;
using Candela.Shared.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Candela.Modules.CustomerClub.Customers;

/// <summary>
/// Walk-in customer capture and credit lookup, ported from the net48 CustomersController.
///
///   POST /api/customers                        creates the member
///   GET  /api/customers/{id}/credit-outstanding live credit position
///   PUT  /api/customers/{id}/comments           the persistent customer note
///
/// All three are plain SQL — none of them goes through CustomerDAL — so nothing here is
/// forwarded to the legacy host. Routes, status codes and bodies are unchanged.
///
/// The request parameters are declared nullable on purpose. With [ApiController] a
/// non-nullable [FromBody] parameter is implicitly required, and an empty body would be
/// rejected by the model-state filter with its own wording; nullable lets the request
/// reach the method so the original "Request body is required" message is what the
/// caller still sees.
///
/// No service class: the only rules are three argument checks, and pushing them into a
/// service would add a file without adding a decision.
/// </summary>
[Route("api/customers")]
public sealed class CustomersController(ICustomersRepository customers) : CandelaControllerBase
{
    /// <summary>
    /// POST /api/customers — create the customer the cashier just typed in and hand back
    /// the ids, so the caller can attach the new member to the sale in progress.
    /// </summary>
    [HttpPost("")]
    public async Task<IActionResult> CreateCustomer([FromBody] CreateCustomerRequest? req,
        CancellationToken ct)
    {
        if (req == null)
            return Fail(StatusCodes.Status400BadRequest, "Request body is required");

        if (string.IsNullOrWhiteSpace(req.MemberName))
            return Fail(StatusCodes.Status400BadRequest, "member_name is required");

        if (req.MemberTypeId <= 0)
            return Fail(StatusCodes.Status400BadRequest, "member_type_id is required");

        var data = await customers.CreateAsync(req, ShopId, UserId, ct);

        return new JsonResult(ApiResponse<CreateCustomerResponse>.Ok(data));
    }

    /// <summary>
    /// GET /api/customers/{id}/credit-outstanding — total credit billed minus receipts
    /// received, refreshed every time a customer is selected at the till.
    ///
    /// Returns the four fields at the top level, with no success/data envelope, because
    /// that is what the net48 endpoint returned and what the checkout screen reads.
    /// </summary>
    [HttpGet("{id:int}/credit-outstanding")]
    public async Task<IActionResult> GetCreditOutstanding(int id, CancellationToken ct)
    {
        var data = await customers.GetCreditOutstandingAsync(id, ct);
        if (data is null)
            throw new NotFoundException($"Customer {id} not found.");

        return new JsonResult(data);
    }

    /// <summary>
    /// PUT /api/customers/{id}/comments — overwrite the customer's persistent note.
    /// Scoped to the caller's shop, as before: a cashier may only annotate their own
    /// shop's customers even though the credit lookup above reads across shops.
    /// </summary>
    [HttpPut("{id:int}/comments")]
    public async Task<IActionResult> UpdateCustomerComments(int id,
        [FromBody] UpdateCustomerCommentsRequest? req, CancellationToken ct)
    {
        if (req == null)
            return Fail(StatusCodes.Status400BadRequest, "Request body is required");

        var updated = await customers.UpdateCommentsAsync(id, ShopId, req.Comments, ct);
        if (!updated)
            throw new NotFoundException($"Customer {id} not found.");

        return new JsonResult(new { success = true });
    }
}
