namespace Candela.Tests.Parity;

/// <summary>
/// Old vs new for /api/gift-cards. Skips itself when PARITY_OLD_BASE is unset.
///
///   set PARITY_OLD_BASE=http://localhost:58408
///   set PARITY_NEW_BASE=http://localhost:5099
///   set PARITY_JWT=&lt;token both hosts accept&gt;
///   set PARITY_GIFT_CARD_NO=&lt;a card number that exists&gt;
///   dotnet test --filter GiftCardsParityTests
///
/// balance, unsold and validate are read-only and safe to run live. topup and redeem
/// write money onto real cards on BOTH databases, so they stay opt-in.
/// </summary>
public sealed class GiftCardsParityTests
{
    private static string CardNo =>
        Environment.GetEnvironmentVariable("PARITY_GIFT_CARD_NO") ?? "1";

    [Fact]
    public async Task Balance_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        var result = await parity.CompareAsync(HttpMethod.Get, $"/api/gift-cards/{CardNo}/balance");
        Assert.True(result.Identical, result.Report());
    }

    [Fact]
    public async Task Balance_unknown_card_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        // Both must answer 404 { error: "Gift card 'NO-SUCH-CARD' not found." }
        var result = await parity.CompareAsync(HttpMethod.Get, "/api/gift-cards/NO-SUCH-CARD/balance");
        Assert.True(result.Identical, result.Report());
    }

    [Fact]
    public async Task Unsold_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        // Either both find the same next card, or both 404 with the same message.
        var result = await parity.CompareAsync(HttpMethod.Get, "/api/gift-cards/unsold");
        Assert.True(result.Identical, result.Report());
    }

    [Fact]
    public async Task Validate_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        // Reads only. Covers the shape that has no envelope and reports an unusable card
        // as 200 + valid:false rather than as an error.
        var result = await parity.CompareAsync(HttpMethod.Post, "/api/gift-cards/validate",
            new { card_no = CardNo, amount = 0 });

        Assert.True(result.Identical, result.Report());
    }

    [Fact]
    public async Task Validate_unknown_card_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        // The odd one: 404 carrying { valid: false, reason: … } and no "error" key.
        var result = await parity.CompareAsync(HttpMethod.Post, "/api/gift-cards/validate",
            new { card_no = "NO-SUCH-CARD", amount = 0 });

        Assert.True(result.Identical, result.Report());
    }

    [Fact(Skip = "Loads real value onto a card on both hosts — enable and point at a scratch shop to run.")]
    public async Task Topup_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        var result = await parity.CompareAsync(HttpMethod.Post, "/api/gift-cards/topup",
            new { card_no = CardNo, topup_amount = 100.0, cash_amount = 100.0, card_amount = 0.0 });

        // ledger_id is MAX+1 per shop and will differ between the two databases.
        Assert.True(result.Diffs.All(d => d.Contains("ledger_id")), result.Report());
    }

    [Fact(Skip = "Spends real value on a card on both hosts — enable deliberately.")]
    public async Task Redeem_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        var cardId = Environment.GetEnvironmentVariable("PARITY_GIFT_CARD_ID") ?? "1";

        var result = await parity.CompareAsync(HttpMethod.Post, "/api/gift-cards/redeem",
            new { card_id = int.Parse(cardId), amount = 10.0 });

        Assert.True(result.Diffs.All(d => d.Contains("ledger_id")), result.Report());
    }
}
