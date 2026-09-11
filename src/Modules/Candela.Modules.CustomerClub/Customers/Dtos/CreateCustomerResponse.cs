using Newtonsoft.Json;

namespace Candela.Modules.CustomerClub.Customers.Dtos;

/// <summary>
/// The <c>data</c> body of POST /api/customers. The legacy endpoint returned an
/// anonymous object whose members were already snake_case, so the names are pinned with
/// [JsonProperty] rather than left to the camelCase resolver.
///
/// The caller uses member_id straight away as the sale's customer, so this is the one
/// field that must never change name.
/// </summary>
public sealed class CreateCustomerResponse
{
    [JsonProperty("member_id")]
    public int MemberId { get; set; }

    [JsonProperty("member_no")]
    public int MemberNo { get; set; }

    [JsonProperty("shop_id")]
    public int ShopId { get; set; }
}
