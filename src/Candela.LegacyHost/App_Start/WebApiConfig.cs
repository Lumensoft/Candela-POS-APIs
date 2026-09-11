using System.Web.Http;
using System.Web.Http.ExceptionHandling;
using CandelaPOS.Shared.Auth;
using CandelaPOS.Shared.Errors;
using CandelaPOS.Shared.Http;
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

            // JSON only — remove XML formatter.
            // These settings ARE the wire contract the tablet app depends on
            // (camelCase keys, null properties omitted). Do not change them
            // without shipping a matching frontend release.
            config.Formatters.Remove(config.Formatters.XmlFormatter);
            config.Formatters.JsonFormatter.SerializerSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore
            };

            // Log every exception Web API sees that no controller handled.
            // An ExceptionLogger cannot alter the response, so this is additive only.
            config.Services.Add(typeof(IExceptionLogger), new GlobalExceptionLogger());

            // Handler order matters and is the reverse of what it looks like on the
            // way out. Correlation id is outermost so that even a CORS preflight and
            // an auth rejection carry an id we can grep for.
            //   1. CorrelationIdHandler — tag the request
            //   2. CorsHandler          — must precede auth so preflight OPTIONS never hits it
            //   3. CandelaContextHandler — set the DAL globals once per request
            //   4. LegacySecretHandler  - guards the internal /legacy/* channel
            //   5. LegacyContextHandler — copy X-Ctx-* into Request.Properties for /legacy/*
            //   6. JwtAuthHandler       — authenticate everything except /api/auth/login
            config.MessageHandlers.Insert(0, new CorrelationIdHandler());
            config.MessageHandlers.Insert(1, new CorsHandler());
            config.MessageHandlers.Insert(2, new CandelaContextHandler());
            config.MessageHandlers.Insert(3, new LegacySecretHandler());
            config.MessageHandlers.Insert(4, new LegacyContextHandler());
            config.MessageHandlers.Add(new JwtAuthHandler());
        }
    }
}
