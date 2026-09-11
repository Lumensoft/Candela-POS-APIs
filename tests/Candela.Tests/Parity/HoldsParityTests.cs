namespace Candela.Tests.Parity;

/// <summary>
/// Old vs new for /api/holds. Skips itself when PARITY_OLD_BASE is unset.
///
///   set PARITY_OLD_BASE=http://localhost:58408
///   set PARITY_NEW_BASE=http://localhost:5099
///   set PARITY_JWT=&lt;token both hosts accept&gt;
///   dotnet test --filter HoldsParityTests
///
/// GET is the safe one to run first — read only. POST parks a real cart on both hosts,
/// so run it against a scratch shop, and DELETE the two holds it creates afterwards.
/// </summary>
public sealed class HoldsParityTests
{
    [Fact]
    public async Task GetHolds_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        var result = await parity.CompareAsync(HttpMethod.Get, "/api/holds");
        Assert.True(result.Identical, result.Report());
    }

    [Fact(Skip = "Writes a real hold on both hosts — enable and point at a scratch shop to run.")]
    public async Task ParkSale_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        var cart = new
        {
            payment_type = "Cash",
            gross_total = 100.0,
            net_total = 100.0,
            items = new[]
            {
                new { product_item_id = 1, quantity = 1.0, unit_rate = 100.0, tagged_price = 100.0, net_amount = 100.0 }
            }
        };

        var result = await parity.CompareAsync(HttpMethod.Post, "/api/holds", cart);
        // hold_id will differ between the two databases — the diff should be limited to
        // $.data.hold_id and nothing else.
        Assert.True(
            result.Diffs.Count == 0 || result.Diffs is [var only] && only.Contains("hold_id"),
            result.Report());
    }
}
