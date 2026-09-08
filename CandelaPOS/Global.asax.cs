using System.Web.Http;
using CandelaPOS.Shared.Data;

namespace CandelaPOS
{
    public class WebApiApplication : System.Web.HttpApplication
    {
        protected void Application_Start()
        {
            GlobalConfiguration.Configure(WebApiConfig.Register);
            CandelaBootstrap.Initialize();
        }
    }
}
