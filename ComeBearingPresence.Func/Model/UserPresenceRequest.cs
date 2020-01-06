
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.WindowsAzure.Storage.Table;
using ComeBearingPresence.Func;

[assembly: FunctionsStartup(typeof(Startup))]

namespace ComeBearingPresence.Func.Model
{
    public class UserPresenceRequest : TableEntity
    {
        public string ObjectId { get; set; }
        public string Upn { get; set; }
        public string TenantId { get; set; }
    }
}
