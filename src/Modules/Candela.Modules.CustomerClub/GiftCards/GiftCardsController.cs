using Candela.Modules.CustomerClub.GiftCards.Dtos;
using Candela.Platform.Api;
using Candela.Shared;
using Candela.Shared.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Candela.Modules.CustomerClub.GiftCards;

/// <summary>
/// Gift cards, ported from the net48 GiftCardsController.
///
///   GET  /api/gift-cards/{q}/balance[?by=phone]  look a card up and read its balance
///   GET  /api/gift-cards/unsold                  next card number available to load
///   POST /api/gift-cards/topup                   load value onto a card
///   POST /api/gift-cards/validate                may this card pay this amount?
///   POST /api/gift-cards/redeem                  standalone redemption
///
/// All five are plain SQL, so none of them is forwarded to the legacy host. Routes,
/// status codes and bodies are unchanged — including the two endpoints that answer
/// without the usual success/data envelope, and validate's habit of reporting an unusable
/// card as 200 with valid=false rather than as an error.
/// </summary>
[Route("api/gift-cards")]
public sealed class GiftCardsController(IGiftCardsRepository giftCards) : CandelaControllerBase
{
    /// <summary>
    /// GET /api/gift-cards/{q}/balance — by=card (default) matches the alternate card
    /// number, the numeric card number, or the composite printed on the card; by=phone
    /// matches the mobile on tbldefCards.
    /// </summary>
    [HttpGet("{q}/balance")]
    public async Task<IActionResult> GetBalance(string q, [FromQuery] string by = "card",
        CancellationToken ct = default)
    {
        bool byPhone = string.Equals(by, "phone", StringComparison.OrdinalIgnoreCase);

        var data = await giftCards.GetBalanceAsync(q, byPhone, ct);

        if (data is null)
            // The original picked the message with a case-SENSITIVE `by == "phone"` while
            // the lookup above is case-insensitive, so `?by=Phone` searches by phone but
            // reports "Gift card not found". Kept as-is: it is the wording the tablet
            // already shows.
            throw new NotFoundException(by == "phone"
                ? $"No gift card found for mobile '{q}'."
                : $"Gift card '{q}' not found.");

        return new JsonResult(new { success = true, data });
    }

    /// <summary>
    /// GET /api/gift-cards/unsold — the next card the cashier can load, for shops that
    /// allow manual loading. The caller gates on that config; reaching here always runs.
    /// </summary>
    [HttpGet("unsold")]
    public async Task<IActionResult> GetUnsold(CancellationToken ct)
    {
        var cardNo = await giftCards.GetUnsoldCardNoAsync(ShopId, ct);

        if (string.IsNullOrWhiteSpace(cardNo))
            throw new NotFoundException("No unsold gift cards are available for this shop.");

        var data = new UnsoldGiftCardResponse { CardNo = cardNo };
        return new JsonResult(ApiResponse<UnsoldGiftCardResponse>.Ok(data));
    }

    /// <summary>
    /// POST /api/gift-cards/topup — one card, one positive ledger row, card marked Sold,
    /// and a cash debit when cash was taken.
    /// </summary>
    [HttpPost("topup")]
    public async Task<IActionResult> Topup([FromBody] GiftCardTopupRequest? req,
        CancellationToken ct)
    {
        if (req == null)
            return Fail(StatusCodes.Status400BadRequest, "Request body is required");

        if (string.IsNullOrWhiteSpace(req.CardNo))
            return Fail(StatusCodes.Status400BadRequest, "card_no is required");

        if (req.TopupAmount <= 0)
            return Fail(StatusCodes.Status400BadRequest, "topup_amount must be > 0");

        var data = await giftCards.TopupAsync(req, ShopId, UserId, PosCode, ct);

        return new JsonResult(ApiResponse<GiftCardTopupResponse>.Ok(data));
    }

    /// <summary>
    /// POST /api/gift-cards/validate — active, unexpired, and enough on it? Answers 200
    /// with valid=false for a card that exists but cannot be used, and 404 only when
    /// there is no such card. No envelope: the fields are at the top level.
    /// </summary>
    [HttpPost("validate")]
    public async Task<IActionResult> Validate([FromBody] GiftCardValidateRequest? req,
        CancellationToken ct)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.CardNo))
            return Fail(StatusCodes.Status400BadRequest, "card_no is required");

        var result = await giftCards.ValidateAsync(req, ct);

        if (result is null)
            // Not thrown as NotFoundException: this 404 carries { valid, reason }, not the
            // { error } body the middleware writes. The payment screen reads `reason`.
            return new JsonResult(new GiftCardValidateResponse
            {
                Valid = false,
                Reason = $"Gift card '{req.CardNo}' not found."
            })
            { StatusCode = StatusCodes.Status404NotFound };

        return new JsonResult(result);
    }

    /// <summary>
    /// POST /api/gift-cards/redeem — a redemption recorded on its own, outside any sale:
    /// a gift-card-to-cash refund, a manual adjustment. Redemptions that belong to a sale
    /// go through gift_card_payments[] on POST /api/sales instead.
    /// </summary>
    [HttpPost("redeem")]
    public async Task<IActionResult> Redeem([FromBody] GiftCardRedeemRequest? req,
        CancellationToken ct)
    {
        if (req == null || req.CardId <= 0)
            return Fail(StatusCodes.Status400BadRequest, "card_id is required");

        if (req.Amount <= 0)
            return Fail(StatusCodes.Status400BadRequest, "amount must be > 0");

        var result = await giftCards.RedeemAsync(req, ShopId, UserId, PosCode, ct);

        return new JsonResult(result);
    }
}
