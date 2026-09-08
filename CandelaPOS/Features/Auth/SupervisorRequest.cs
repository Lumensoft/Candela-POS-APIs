using Newtonsoft.Json;

namespace CandelaPOS.Features.Auth
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
