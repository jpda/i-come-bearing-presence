
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.WindowsAzure.Storage.Table;
using ComeBearingPresence.Func;

[assembly: FunctionsStartup(typeof(Startup))]

namespace ComeBearingPresence.Func.Model
{
    public class TableTokenCacheItem : TableEntity
    {
        public string TokenData { get; set; }
    }
}
