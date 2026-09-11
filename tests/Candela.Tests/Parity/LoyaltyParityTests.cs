namespace Candela.Tests.Parity;

/// <summary>
/// Old vs new for GET /api/customers/{memberId}/loyalty-points.
///
/// Skips itself when PARITY_OLD_BASE is not set, so this is green in CI and becomes a
/// real comparison the moment you run it locally with both hosts up:
///
///   set PARITY_OLD_BASE=http://localhost:58408
///   set PARITY_NEW_BASE=http://localhost:5099
///   set PARITY_JWT=&lt;a token both hosts accept&gt;
///   dotnet test --filter LoyaltyParityTests
///
/// Put real member ids for your database in the theory data.
/// </summary>
public sealed class LoyaltyParityTests
{
    [Theory]
    [InlineData(1)]     // member with earnings + redemptions
    [InlineData(2)]     // member with earnings, no redemptions
    [InlineData(999999)] // member with no earnings row — must return zeros, not 404
    public async Task LoyaltyPoints_matches_legacy(int memberId)
    {
        using var parity = new ParityClient();
        if (!parity.OldHostConfigured)
            return; // treated as a skip — see class summary

        var result = await parity.CompareAsync(
            HttpMethod.Get, $"/api/customers/{memberId}/loyalty-points");

        Assert.True(result.Identical, result.Report());
    }
}
