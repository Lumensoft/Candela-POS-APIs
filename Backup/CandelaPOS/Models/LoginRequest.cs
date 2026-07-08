using Newtonsoft.Json;

namespace CandelaPOS.Models
{
    public class LoginRequest
    {
        [JsonProperty("device_id")]
        public string DeviceId { get; set; }

        [JsonProperty("friendly_name")]
        public string FriendlyName { get; set; }

        [JsonProperty("device_model")]
        public string DeviceModel { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("password")]
        public string Password { get; set; }
    }
}
