using System.Web.Http;
using CandelaPOS.Shared.Config;
using CandelaPOS.Shared.Data;
using CandelaPOS.Shared.Logging;

namespace CandelaPOS
{
    public class WebApiApplication : System.Web.HttpApplication
    {
        protected void Application_Start()
        {
            GlobalConfiguration.Configure(WebApiConfig.Register);
            CandelaBootstrap.Initialize();

            // Warn (never throw) about configuration that is dangerous to get wrong.
            // A till that refuses to start is worse than one on a weak key, but the
            // operator has to see it on every restart until it is fixed.
            ConfigChecks.Run();

            AppLog.Info("CandelaPOS API started.");
        }
    }
}
