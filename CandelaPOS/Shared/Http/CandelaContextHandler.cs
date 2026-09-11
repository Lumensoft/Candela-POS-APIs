using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CandelaPOS.Shared.Data;

namespace CandelaPOS.Shared.Http
{
    /// <summary>
    /// Prepares the globals the Candela DAL reads at call time — SQLHelper.CON_STR and
    /// the system-configuration hash table — once per request.
    ///
    /// Every action used to open with its own CandelaBootstrap.PrepareRequest() call:
    /// 58 of them, and a new endpoint that forgot one would fail in a way that depends
    /// on whatever the previous request happened to leave behind. Doing it in the
    /// pipeline makes it impossible to forget.
    ///
    /// Sits after CorsHandler so a preflight OPTIONS still short-circuits without
    /// touching the database, and before the controller so both public and
    /// authenticated routes are covered.
    ///
    /// Note this is safe here only because one server talks to exactly one shop
    /// database. CON_STR is a process-wide static on the DAL: if a single deployment
    /// ever had to serve more than one database, this is the first thing that would
    /// have to change.
    /// </summary>
    public class CandelaContextHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CandelaBootstrap.PrepareRequest();
            return base.SendAsync(request, cancellationToken);
        }
    }
}
