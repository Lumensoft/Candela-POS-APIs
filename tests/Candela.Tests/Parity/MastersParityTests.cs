namespace Candela.Tests.Parity;

/// <summary>
/// Old vs new for /api/masters. Skips itself when PARITY_OLD_BASE is unset.
///
///   set PARITY_OLD_BASE=http://localhost:58408
///   set PARITY_NEW_BASE=http://localhost:5099
///   set PARITY_JWT=&lt;token both hosts accept&gt;
///   dotnet test --filter MastersParityTests
///
/// Every endpoint here is a read, so the whole file is safe to run against a live shop —
/// this is the one slice that can be verified end to end without writing anything.
/// </summary>
public sealed class MastersParityTests
{
    /// <summary>
    /// The endpoints that take no arguments. Theory rather than one test each, because
    /// the point is coverage of the list: if a route is added to the controller it should
    /// be added here too.
    /// </summary>
    [Theory]
    [InlineData("/api/masters/products")]
    [InlineData("/api/masters/customers")]
    [InlineData("/api/masters/employees")]
    [InlineData("/api/masters/credit-cards")]
    [InlineData("/api/masters/member-types")]
    [InlineData("/api/masters/customer-groups")]
    [InlineData("/api/masters/payment-methods")]
    [InlineData("/api/masters/config")]
    [InlineData("/api/masters/line-items")]
    [InlineData("/api/masters/blocked-products")]
    [InlineData("/api/masters/promotion-products")]
    [InlineData("/api/masters/shops")]
    [InlineData("/api/masters/departments")]
    [InlineData("/api/masters/adjustment-reasons")]
    [InlineData("/api/masters/return-reasons")]
    public async Task Endpoint_matches_legacy(string path)
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        var result = await parity.CompareAsync(HttpMethod.Get, path);
        Assert.True(result.Identical, result.Report());
    }

    [Fact]
    public async Task Products_delta_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        // Exercises the conditional delta clause, which is appended to the SQL only when
        // ?since= parses — the one branch a plain full-list call never reaches.
        var result = await parity.CompareAsync(HttpMethod.Get,
            "/api/masters/products?since=2020-01-01T00:00:00");

        Assert.True(result.Identical, result.Report());
    }

    [Fact]
    public async Task Customers_search_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        // ?q= switches the query to TOP 50 plus three LIKE clauses.
        var q = Environment.GetEnvironmentVariable("PARITY_CUSTOMER_SEARCH") ?? "a";

        var result = await parity.CompareAsync(HttpMethod.Get, $"/api/masters/customers?q={q}");
        Assert.True(result.Identical, result.Report());
    }

    [Fact]
    public async Task ProductScan_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        var code = Environment.GetEnvironmentVariable("PARITY_PRODUCT_CODE");
        if (string.IsNullOrWhiteSpace(code)) return;   // nothing meaningful to scan for

        var result = await parity.CompareAsync(HttpMethod.Get, $"/api/masters/products/scan?q={code}");
        Assert.True(result.Identical, result.Report());
    }

    [Fact]
    public async Task Batches_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        var id = Environment.GetEnvironmentVariable("PARITY_PRODUCT_ITEM_ID") ?? "1";

        var result = await parity.CompareAsync(HttpMethod.Get,
            $"/api/masters/batches?product_item_id={id}");

        Assert.True(result.Identical, result.Report());
    }

    [Fact]
    public async Task AssemblyItems_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        var id = Environment.GetEnvironmentVariable("PARITY_PRODUCT_ITEM_ID") ?? "1";

        var result = await parity.CompareAsync(HttpMethod.Get,
            $"/api/masters/assembly-items?product_item_id={id}");

        Assert.True(result.Identical, result.Report());
    }

    [Fact]
    public async Task Batches_without_id_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        // Both must answer 400 { error: "product_item_id is required" }.
        var result = await parity.CompareAsync(HttpMethod.Get, "/api/masters/batches");
        Assert.True(result.Identical, result.Report());
    }

    [Fact]
    public async Task Scan_without_q_matches_legacy()
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured) return;

        // Both must answer 400 { error: "q is required" }.
        var result = await parity.CompareAsync(HttpMethod.Get, "/api/masters/products/scan");
        Assert.True(result.Identical, result.Report());
    }
}
