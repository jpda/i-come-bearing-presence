using System.Security.Cryptography.X509Certificates;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;

[assembly: FunctionsStartup(typeof(ComeBearingPresence.Func.Startup))]

namespace ComeBearingPresence.Func
{
    public class MsalOptions
    {
        public string ClientId { get; set; }
        public string RedirectUri { get; set; }
        public string TenantId { get; set; }
        public X509Certificate2 Certificate { get; set; }
    }
}
