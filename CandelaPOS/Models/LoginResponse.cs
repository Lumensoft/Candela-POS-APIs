using Newtonsoft.Json;

namespace CandelaPOS.Models
{
    public class LoginResponse
    {
        [JsonProperty("token")]
        public string Token { get; set; }

        [JsonProperty("user_id")]
        public int UserId { get; set; }

        [JsonProperty("user_name")]
        public string UserName { get; set; }

        [JsonProperty("shop_id")]
        public int ShopId { get; set; }

        [JsonProperty("shop_name")]
        public string ShopName { get; set; }

        [JsonProperty("pos_code")]
        public string PosCode { get; set; }

        [JsonProperty("allow_discount_editing")]
        public bool AllowDiscountEditing { get; set; }

        [JsonProperty("allow_price_editing")]
        public bool AllowPriceEditing { get; set; }
    }
}
