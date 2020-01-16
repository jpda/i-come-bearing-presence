using System.Threading.Tasks;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Identity.Client;

[assembly: FunctionsStartup(typeof(ComeBearingPresence.Func.Startup))]

namespace ComeBearingPresence.Func
{
    public interface ITokenCacheAccessor
    {
        void BeforeAccess(TokenCacheNotificationArgs args);
        void AfterAccess(TokenCacheNotificationArgs args);
        void Configure(string identifier);
        Task<bool> UpdateCacheKey(string partition, string temp, string actual);
    }
}
