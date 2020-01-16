using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Identity.Client;

[assembly: FunctionsStartup(typeof(ComeBearingPresence.Func.Startup))]

namespace ComeBearingPresence.Func
{
    public static class TokenCacheExtensions
    {
        public static void AddPerUserTokenCache(this IConfidentialClientApplication app, ITokenCacheAccessor cache)
        {
            app.AppTokenCache.SetBeforeAccess(cache.BeforeAccess);
            app.UserTokenCache.SetBeforeAccess(cache.BeforeAccess);
            app.AppTokenCache.SetAfterAccess(cache.AfterAccess);
            app.UserTokenCache.SetAfterAccess(cache.AfterAccess);
        }
    }
}
