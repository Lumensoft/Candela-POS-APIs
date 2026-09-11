namespace Candela.Tests.Parity;

/// <summary>
/// Old vs new for /api/customers. Skips itself when PARITY_OLD_BASE is unset.
///
///   set PARITY_OLD_BASE=http://localhost:58408
///   set PARITY_NEW_BASE=http://localhost:5099
///   set PARITY_JWT=&lt;token both hosts accept&gt;
///   dotnet test --filter CustomersParityTests
///
/// GET credit-outstanding is read-only and safe to run against a live shop. The other
/// two write: POST creates a real member row on BOTH databases and PUT overwrites a real
/// customer note, so they stay opt-in and belong on a scratch shop.
/// </summary>
public sealed class CustomersParityTests
{
    /// <summary>
    /// Set PARITY_CUSTOMER_ID to a member that exists in both databases. Without it the
    /// test would compare two 404s and pass without proving anything.
    /// </summary>
    private static string CustomerId =>
        Environment.GetEnvironmentVariable("PARITY_CUSTOMER_ID") ?? "1";

    [Fact]
    public async Task CreditOutstanding_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        var result = await parity.CompareAsync(
            HttpMethod.Get, $"/api/customers/{CustomerId}/credit-outstanding");

        Assert.True(result.Identical, result.Report());
    }

    [Fact]
    public async Task CreditOutstanding_unknown_customer_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        // Both hosts must answer 404 with the same { error: "Customer N not found." }.
        var result = await parity.CompareAsync(
            HttpMethod.Get, "/api/customers/999999999/credit-outstanding");

        Assert.True(result.Identical, result.Report());
    }

    [Fact(Skip = "Creates a real member row on both hosts — enable and point at a scratch shop to run.")]
    public async Task CreateCustomer_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        var customer = new
        {
            memberName = "Parity Test " + DateTime.Now.Ticks,
            memberTypeId = 1,
            phoneMobile = "03001234567",
            allowCredit = false,
            creditLimit = 0.0
        };

        var result = await parity.CompareAsync(HttpMethod.Post, "/api/customers", customer);

        // member_id / member_no are MAX+1 per shop, so they will differ between the two
        // databases. Everything else — success, shop_id, the envelope — must match.
        Assert.True(
            result.Diffs.All(d => d.Contains("member_id") || d.Contains("member_no")),
            result.Report());
    }

    [Fact(Skip = "Overwrites a real customer note on both hosts — enable deliberately.")]
    public async Task UpdateComments_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        var body = new { comments = "parity check" };

        var result = await parity.CompareAsync(
            HttpMethod.Put, $"/api/customers/{CustomerId}/comments", body);

        Assert.True(result.Identical, result.Report());
    }
}
