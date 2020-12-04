using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;

[assembly: FunctionsStartup(typeof(ComeBearingPresence.Func.Startup))]

namespace ComeBearingPresence.Func
{
    public class Startup : FunctionsStartup
    {
        private ILoggerFactory _loggerFactory;

        public override void Configure(IFunctionsHostBuilder builder)
        {
            var config = new ConfigurationBuilder().SetBasePath(Environment.CurrentDirectory).AddJsonFile("local.settings.json", optional: true).AddEnvironmentVariables().Build();
            builder?.Services.AddLogging();
            ConfigureServices(builder, config);
        }

        public void ConfigureServices(IFunctionsHostBuilder builder, IConfiguration config)
        {
            _loggerFactory = new LoggerFactory();
            var logger = _loggerFactory.CreateLogger<Startup>();

            var aadConfig = config?.GetSection("AzureAd");

            builder?.Services.AddHttpClient("graph", x =>
            {
                x.BaseAddress = new Uri("https://graph.microsoft.com/");
            });

            builder.Services.AddSingleton<CloudTable>(x =>
            {
                var sa = CloudStorageAccount.Parse(config["TableTokenCache:ConnectionString"]);
                var table = sa.CreateCloudTableClient().GetTableReference("MsalCache");
                table.CreateIfNotExistsAsync().Wait();
                return table;
            });

            builder.Services.AddSingleton<X509Certificate2>(x =>
            {
                logger.LogInformation("Setting up certificate");

                var managedIdentityAvailable = config["MSI_ENDPOINT"] != null
                || config["IDENTITY_ENDPOINT"] != null
                || Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT") != null
                || Environment.GetEnvironmentVariable("MSI_ENDPOINT") != null;

                logger.LogInformation($"Managed identity available: {managedIdentityAvailable}");

                X509Certificate2 msalCert;

                if (bool.Parse(config["USE_MSI"]))
                {
                    logger.LogInformation($"USE_MSI: {managedIdentityAvailable}, using MSI");
                    var secretClient = new SecretClient(new Uri(config["KeyVault:Endpoint"]), new ManagedIdentityCredential());
                    var certKey = secretClient.GetSecret(config["KeyVault:CertificateName"]).Value;
                    try
                    {
                        msalCert = new X509Certificate2(Convert.FromBase64String(certKey.Value));
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"Error using kv cert: {ex.Message}");
                        throw;
                    }
                }
                else
                {
                    logger.LogInformation($"USE_MSI: {managedIdentityAvailable}, using local certificate...");
                    try
                    {
                        //E6213E8767D78C384AD7312EB2049AB5CBA23D33

                        X509Store certStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                        certStore.Open(OpenFlags.ReadOnly);
                        X509Certificate2Collection certCollection = certStore.Certificates.Find(X509FindType.FindByThumbprint,"E6213E8767D78C384AD7312EB2049AB5CBA23D33", false);


                        msalCert = certCollection[0];

                        //var localCert = System.IO.File.ReadAllBytes(config["AzureAd:TestCertificatePath"]);
                        //logger.LogInformation("Got local cert bytes, creating x509 object");
                        //msalCert = new X509Certificate2(localCert, config["AzureAd:TestCertificatePassword"]);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"Error using local cert: {ex.Message}");
                        throw;
                    }
                }
                return msalCert;
            });

            builder.Services.Configure<MsalOptions>(x =>
            {
                x.ClientId = aadConfig["ClientId"];
                x.RedirectUri = aadConfig["RedirectUrl"];
                x.TenantId = aadConfig["TenantId"];
            });

            builder.Services.AddTransient<ITokenCacheAccessor, PerUserTableTokenCacheAccessor>();
            builder.Services.AddTransient<MsalClientFactory>();
        }
    }
}