using Newtonsoft.Json;

namespace CandelaPOS.Models
{
    public class SupervisorRequest
    {
        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("password")]
        public string Password { get; set; }

        [JsonProperty("scope")]
        public string Scope { get; set; }
    }
}
