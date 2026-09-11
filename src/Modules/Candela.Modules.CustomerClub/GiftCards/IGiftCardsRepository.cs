using Candela.Modules.CustomerClub.GiftCards.Dtos;

namespace Candela.Modules.CustomerClub.GiftCards;

/// <summary>
/// Gift card lookup, top-up and redemption.
///
/// All five net48 endpoints were raw SQL against tbldefCards / tblGiftCardLedger /
/// tblAccountTransactions — none of them called GiftCardGenerationDAL — so the whole
/// slice moves here and nothing is forwarded to the legacy host. Redemptions that belong
/// to a sale are the exception, and they never came through this controller: they go
/// through gift_card_payments[] on POST /api/sales, where the DAL writes the ledger row
/// inside the sale transaction and keeps the SQL log correct.
/// </summary>
public interface IGiftCardsRepository
{
    /// <summary>
    /// Card details plus balance, by card number or by mobile. Null when nothing matched.
    /// The row is returned as raw columns because the SELECT's aliases are the contract.
    /// </summary>
    Task<Dictionary<string, object?>?> GetBalanceAsync(string q, bool byPhone, CancellationToken ct);

    /// <summary>
    /// The next unsold card number for the shop, already composite-formatted, or null
    /// when the shop has none left.
    /// </summary>
    Task<string?> GetUnsoldCardNoAsync(int shopId, CancellationToken ct);

    /// <summary>
    /// Writes the top-up: a positive ledger row, the card marked Sold, and — when cash
    /// was taken — the matching debit on tblAccountTransactions, all in one transaction.
    /// Throws NotFoundException when the card number does not resolve.
    /// </summary>
    Task<GiftCardTopupResponse> TopupAsync(GiftCardTopupRequest req, int shopId, int userId,
        string posCode, CancellationToken ct);

    /// <summary>
    /// Whether the card can be used, and for how much. Null means no such card, which the
    /// caller answers with 404 — every other outcome, including "deactivated" and "no
    /// balance", is a 200 carrying valid=false, as the net48 endpoint did.
    /// </summary>
    Task<GiftCardValidateResponse?> ValidateAsync(GiftCardValidateRequest req, CancellationToken ct);

    /// <summary>
    /// Writes a standalone redemption as a negative ledger row and returns the balance
    /// after it. Throws NotFoundException for an unknown card and ValidationException
    /// when the card is deactivated or the balance will not cover the amount.
    /// </summary>
    Task<GiftCardRedeemResponse> RedeemAsync(GiftCardRedeemRequest req, int shopId, int userId,
        string posCode, CancellationToken ct);
}
