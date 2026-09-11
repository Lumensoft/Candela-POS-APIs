using Candela.Modules.Accounting;
using Candela.Modules.Configuration;
using Candela.Modules.CustomerClub;
using Candela.Modules.Inventory;
using Candela.Modules.Production;
using Candela.Modules.Purchase;
using Candela.Modules.Sales;
using Candela.Modules.Security;
using Candela.Modules.Security.Auth;
using Candela.Modules.Utilities;
using Candela.Platform.Auth;
using Candela.Platform.Data;
using Candela.Platform.Http;
using Candela.Platform.Legacy;
using Candela.Platform.Printing;
using Candela.Shared.Auth;
using Candela.Shared.Logging;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// ── Logging ──────────────────────────────────────────────────────────────────────
AppLog.Configure(
    builder.Configuration["Logging:Directory"],
    builder.Configuration.GetValue("Logging:RetentionDays", 14));

// ── Wire contract ────────────────────────────────────────────────────────────────
// These two settings ARE the contract the tablet app depends on: camelCase keys and
// null properties omitted. Newtonsoft is used rather than System.Text.Json for the same
// reason — it is what the .NET Framework host serialises with, and swapping serialisers
// changes DateTime formatting, decimal rendering and null handling in ways no compiler
// would catch. Do not "modernise" this without a matching frontend release and a
// byte-for-byte response comparison.
//
// Controllers live in the module projects, not here, so each one has to be added as an
// application part or MVC will not find it.
builder.Services
    .AddControllers()
    .AddApplicationPart(typeof(ConfigurationModule).Assembly)
    .AddApplicationPart(typeof(SecurityModule).Assembly)
    .AddApplicationPart(typeof(InventoryModule).Assembly)
    .AddApplicationPart(typeof(PurchaseModule).Assembly)
    .AddApplicationPart(typeof(CustomerClubModule).Assembly)
    .AddApplicationPart(typeof(ProductionModule).Assembly)
    .AddApplicationPart(typeof(SalesModule).Assembly)
    .AddApplicationPart(typeof(AccountingModule).Assembly)
    .AddApplicationPart(typeof(UtilitiesModule).Assembly)
    .AddNewtonsoftJson(o =>
    {
        o.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
        o.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
    });

// ASP.NET Core would otherwise answer a failed model binding with an RFC 7807
// ProblemDetails body. The tablet reads response.data.error in 47 places and knows
// nothing about ProblemDetails, so keep the existing shape.
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(o =>
{
    o.InvalidModelStateResponseFactory = ctx =>
    {
        var message = ctx.ModelState
            .SelectMany(kv => kv.Value?.Errors.Select(e => e.ErrorMessage) ?? Array.Empty<string>())
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) ?? "Invalid request.";

        return new Microsoft.AspNetCore.Mvc.JsonResult(new { error = message })
        {
            StatusCode = StatusCodes.Status400BadRequest
        };
    };
});

// ── Auth ─────────────────────────────────────────────────────────────────────────
JwtHelper.Configure(
    builder.Configuration["Jwt:Secret"],
    builder.Configuration.GetValue("Jwt:ExpiryHours", 24),
    builder.Configuration["Jwt:Issuer"]);

// ── Data ─────────────────────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("CON_STR") ?? "";
builder.Services.AddSingleton<IDbConnectionFactory>(_ => new SqlConnectionFactory(connectionString));
builder.Services.AddScoped<IDb, Db>();

// ── Legacy host ──────────────────────────────────────────────────────────────────
// The only route from here into the Candela DAL. Writes that must keep the SQL log
// (which HO/shop replication replays), the activity log and inventory posting correct
// cannot be done with plain SQL from this process, so they are posted to the .NET
// Framework host over loopback and it calls the DAL.
var legacyOptions = new LegacyHostOptions
{
    BaseUrl = builder.Configuration["Legacy:BaseUrl"] ?? "",
    AppUrl = builder.Configuration["Legacy:AppUrl"] ?? "",
    SharedSecret = builder.Configuration["Legacy:SharedSecret"] ?? "",
    TimeoutSeconds = builder.Configuration.GetValue("Legacy:TimeoutSeconds", 60)
};
builder.Services.AddSingleton(legacyOptions);
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<ILegacyHostClient, LegacyHostClient>(c =>
{
    if (!string.IsNullOrWhiteSpace(legacyOptions.BaseUrl))
        c.BaseAddress = new Uri(legacyOptions.BaseUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(legacyOptions.TimeoutSeconds);
});

// The client LegacyProxy forwards unmigrated /api/* routes with. Separate from the one
// above because it targets the legacy app's root rather than its internal /legacy
// channel, and because it must not follow redirects or rewrite anything: whatever the
// legacy host answers is what the tablet has always received.
builder.Services.AddHttpClient(LegacyProxy.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(legacyOptions.TimeoutSeconds);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.None,
    UseCookies = false
});

// ── Printing / hardware ──────────────────────────────────────────────────────────
// PrinterGuard vets every printer target the tablet supplies before the server opens a
// socket to it or hands it to the spooler. Same two settings the Web.config carried.
builder.Services.AddSingleton(new PrinterGuardOptions
{
    AllowedHosts = builder.Configuration["Printing:AllowedHosts"] ?? "",
    AllowedPorts = builder.Configuration["Printing:AllowedPorts"] ?? ""
});
builder.Services.AddSingleton<PrinterGuard>();

// ── Security ─────────────────────────────────────────────────────────────────────
// Whether a group with no shop rights configured at all is allowed through. See
// AuthRules.Decide for why the default is to allow and warn.
builder.Services.AddSingleton(new AuthOptions
{
    StrictShopRights = builder.Configuration.GetValue("Security:StrictShopRights", false)
});

// ── CORS ─────────────────────────────────────────────────────────────────────────
// Same rules as the .NET Framework host: origins come from configuration, with the dev
// ports as the fallback. In production the tablet is served from the same IIS site as
// the API, so this normally never fires.
const string CorsPolicy = "pos";
var allowedOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(o => o.TrimEnd('/'))
    .ToArray();

if (allowedOrigins.Length == 0)
    allowedOrigins = new[]
    {
        "http://localhost:3000", "http://localhost:3001",
        "http://localhost:5173", "http://localhost:5174"
    };

builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
    .WithOrigins(allowedOrigins)
    .WithHeaders("Content-Type", "Authorization", CorrelationIdMiddleware.HeaderName)
    .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
    .WithExposedHeaders(CorrelationIdMiddleware.HeaderName)
    .AllowCredentials()));

// ── Modules ──────────────────────────────────────────────────────────────────────
// Every module the application contains. Each one registers its own screens internally,
// so adding a screen means touching that module's folder and nothing here.
builder.Services
    .AddConfigurationModule()
    .AddSecurityModule()
    .AddInventoryModule()
    .AddPurchaseModule()
    .AddCustomerClubModule()
    .AddProductionModule()
    .AddSalesModule()
    .AddAccountingModule()
    .AddUtilitiesModule();

var app = builder.Build();

// ── Pipeline ─────────────────────────────────────────────────────────────────────
// Mirrors the handler order of the .NET Framework host:
//   exception handling  ->  correlation id  ->  CORS  ->  authentication  ->  endpoint
// Correlation id is outermost so a CORS preflight and an auth rejection both carry an
// id we can grep for.
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseCors(CorsPolicy);

// The built frontend, when it is deployed into wwwroot alongside the API (one IIS site).
// Serves index.html at "/" and the hashed assets directly, before auth — static files
// carry no session. Harmless when wwwroot is empty (dev: the frontend runs under Vite).
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseMiddleware<JwtAuthMiddleware>();
app.MapControllers();

// Status endpoints, outside the /api namespace so JwtAuthMiddleware ignores them:
//   GET /        a human-readable "it's up" page — only reached when NO frontend build
//                is in wwwroot (otherwise UseDefaultFiles serves index.html at "/").
//   GET /health  a machine check for the updater / load balancer — 200 with the version,
//                or 503 if the connection string is not configured.
app.MapGet("/", () => Results.Content(
    "<!doctype html><meta charset=utf-8><title>Candela.Api</title>" +
    "<body style=\"font:14px system-ui;display:grid;place-items:center;height:100vh;margin:0;background:#f6f6f6\">" +
    "<div style=\"background:#fff;border-radius:12px;padding:32px 48px;box-shadow:0 1px 4px #0002;text-align:center\">" +
    "<h1 style=\"margin:0 0 4px;color:#1f2937\">Candela.Api</h1>" +
    "<p style=\"margin:0 0 16px;color:#6b7280\">.NET " + Environment.Version + " &middot; POS API. Use the tablet app or Postman.</p>" +
    "<span style=\"background:#10b981;color:#fff;border-radius:6px;padding:4px 14px;font-weight:600\">Running</span>" +
    "</div>", "text/html"));

app.MapGet("/health", () =>
{
    bool dbConfigured = !string.IsNullOrWhiteSpace(connectionString);
    return Results.Json(new
    {
        status = dbConfigured ? "ok" : "degraded",
        db_configured = dbConfigured,
        runtime = "net" + Environment.Version.Major + "." + Environment.Version.Minor,
        legacy_app_url = legacyOptions.LegacyAppUrl(),
        time = DateTime.UtcNow.ToString("o"),
    }, statusCode: dbConfigured ? 200 : 503);
});

// Anything under /api that no controller above claims goes to the .NET Framework host
// untouched. This is what lets the tablet point at a single URL while the migration is
// still in progress — and afterwards, since printing never leaves .NET Framework.
// Registered last on purpose: MapFallback only runs when nothing else matched.
app.MapLegacyFallback();

SqlConnectionFactory.Validate(connectionString);
AppLog.Info("Candela.Api started on .NET {0}.", Environment.Version);

app.Run();
