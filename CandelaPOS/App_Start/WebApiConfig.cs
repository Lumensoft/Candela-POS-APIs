using System.Web.Http;
using CandelaPOS.Infrastructure;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace CandelaPOS
{
    public static class WebApiConfig
    {
        public static void Register(HttpConfiguration config)
        {
            // Attribute routing first
            config.MapHttpAttributeRoutes();

            // Convention-based fallback
            config.Routes.MapHttpRoute(
                name: "DefaultApi",
                routeTemplate: "api/{controller}/{id}",
                defaults: new { id = RouteParameter.Optional }
            );

            // JSON only — remove XML formatter
            config.Formatters.Remove(config.Formatters.XmlFormatter);
            config.Formatters.JsonFormatter.SerializerSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore
            };

            // CORS — must be first so preflight OPTIONS never hits auth
            config.MessageHandlers.Insert(0, new CorsHandler());

            // JWT auth on every request except /api/auth/login
            config.MessageHandlers.Add(new JwtAuthHandler());
        }
    }
}
