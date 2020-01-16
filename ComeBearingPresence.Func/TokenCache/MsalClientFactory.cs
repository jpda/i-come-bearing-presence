using System.Threading.Tasks;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

[assembly: FunctionsStartup(typeof(ComeBearingPresence.Func.Startup))]

namespace ComeBearingPresence.Func
{
    public class MsalClientFactory
    {
        private readonly MsalOptions _config;
        private readonly ITokenCacheAccessor _tokenCacheAccessor;

        public MsalClientFactory(IOptions<MsalOptions> config, ITokenCacheAccessor tokenCacheAccessor)
        {
            _config = config.Value;
            _tokenCacheAccessor = tokenCacheAccessor;
        }

        public IConfidentialClientApplication Create()
        {
            var app = ConfidentialClientApplicationBuilder.Create(_config.ClientId).WithRedirectUri(_config.RedirectUri).WithTenantId(_config.TenantId).WithCertificate(_config.Certificate).Build();
            return app;
        }

        public IConfidentialClientApplication CreateForIdentifier(string identifier)
        {
            var app = ConfidentialClientApplicationBuilder.Create(_config.ClientId).WithRedirectUri(_config.RedirectUri).WithTenantId(_config.TenantId).WithCertificate(_config.Certificate).Build();
            _tokenCacheAccessor.Configure(identifier);
            app.AddPerUserTokenCache(_tokenCacheAccessor);
            return app;
        }

        public IConfidentialClientApplication CreateWithTransientIdentity(string transientId)
        {
            var app = ConfidentialClientApplicationBuilder.Create(_config.ClientId).WithRedirectUri(_config.RedirectUri).WithTenantId(_config.TenantId).WithCertificate(_config.Certificate).Build();
            _tokenCacheAccessor.Configure(transientId);
            app.AddPerUserTokenCache(_tokenCacheAccessor);
            return app;
        }

        public async Task<bool> SwitchTransientKeyToActual(string temp, string actual)
        {
            return await _tokenCacheAccessor.UpdateCacheKey(_config.ClientId, temp, actual).ConfigureAwait(true);
        }
    }
}
