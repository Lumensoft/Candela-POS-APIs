namespace Candela.Tests.Parity;

/// <summary>
/// Golden-master harness for POST /api/sales/quote — the pricing engine.
///
/// Quote is NOT migrated. It is 1,813 lines of discount / VAT / loyalty arithmetic, and
/// unlike every other slice there is no byte-identical SQL to diff and no verbatim
/// DAL-write to forward — porting it means re-transcribing the maths, and a single
/// flipped comparison would misprice every invoice. So it stays on the .NET Framework
/// host, reached through LegacyProxy, until this harness has been run against a real
/// database and a real net48 host and every number matches.
///
/// How to use it
/// -------------
///  1. Point PARITY_OLD_BASE at the running net48 host and PARITY_NEW_BASE at Candela.Api
///     (both must see the same shop database), and PARITY_JWT at a token both accept.
///  2. Put realistic carts in quote-fixtures.json next to this file — one object per line
///     under "carts": every discount path you care about (plain, customer-type, X/Y,
///     coupon, below-cost, slab VAT, loyalty redemption, adjustment, multi-tender).
///  3. dotnet test --filter QuoteParityTests
///
/// The test fails on the FIRST cart whose response differs, printing the structural diff
/// so the offending field is obvious. Only when this is green for a representative fixture
/// set should QuoteController be ported and this file's [Fact] switched from Skip.
/// </summary>
public sealed class QuoteParityTests
{
    private static string FixturesPath =>
        Path.Combine(AppContext.BaseDirectory, "Parity", "quote-fixtures.json");

    [Fact]
    public async Task Every_fixture_cart_prices_identically()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;   // no old host wired -> nothing to compare

        if (!File.Exists(FixturesPath))
        {
            // Not a failure: the fixtures are captured per-deployment. Make the gap loud.
            Assert.Fail(
                $"No quote fixtures at {FixturesPath}. Capture real POST /api/sales/quote " +
                "request bodies from the running net48 host before relying on this harness.");
            return;
        }

        var doc = Newtonsoft.Json.Linq.JObject.Parse(await File.ReadAllTextAsync(FixturesPath));
        var carts = doc["carts"] as Newtonsoft.Json.Linq.JArray
                    ?? new Newtonsoft.Json.Linq.JArray();

        Assert.True(carts.Count > 0, "quote-fixtures.json has no carts.");

        int n = 0;
        foreach (var cart in carts)
        {
            n++;
            var result = await parity.CompareAsync(HttpMethod.Post, "/api/sales/quote", cart);
            Assert.True(result.Identical, $"Cart #{n} priced differently:\n{result.Report()}");
        }
    }
}
