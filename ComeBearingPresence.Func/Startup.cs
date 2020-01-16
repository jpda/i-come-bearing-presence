using System;
using System.Security.Cryptography.X509Certificates;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;

[assembly: FunctionsStartup(typeof(ComeBearingPresence.Func.Startup))]

namespace ComeBearingPresence.Func
{
    public class Startup : FunctionsStartup
    {
        public override void Configure(IFunctionsHostBuilder builder)
        {
            var config = new ConfigurationBuilder().SetBasePath(Environment.CurrentDirectory).AddJsonFile("local.settings.json", optional: true).AddEnvironmentVariables().Build();
            var aadConfig = config.GetSection("AzureAd");

            builder?.Services.AddHttpClient("graph", x =>
            {
                x.BaseAddress = new Uri("https://graph.microsoft.com/");
            });
            builder.Services.AddLogging();
            builder.Services.AddSingleton<CloudTable>(x =>
            {
                var sa = CloudStorageAccount.Parse(config["TableTokenCache:ConnectionString"]);
                var table = sa.CreateCloudTableClient().GetTableReference("MsalCache");
                table.CreateIfNotExistsAsync().Wait();
                return table;
            });

            builder.Services.Configure<MsalOptions>(x =>
            {
                x.ClientId = aadConfig["ClientId"];
                x.RedirectUri = aadConfig["RedirectUrl"];
                x.TenantId = aadConfig["TenantId"];

                if (Environment.GetEnvironmentVariable("MSI_ENDPOINT") != null)
                {
                    var secretClient = new SecretClient(new Uri(config["KeyVault:Endpoint"]), new ManagedIdentityCredential());
                    var certKey = secretClient.GetSecret(config["KeyVault:CertificateName"]).Value;
                    x.Certificate = new X509Certificate2(Convert.FromBase64String(certKey.Value));
                }
                else
                {
                    x.Certificate = new X509Certificate2(System.IO.File.ReadAllBytes(config["AzureAd:TestCertificatePath"]), config["AzureAd:TestCertificatePassword"]);
                }
            });

            builder.Services.AddTransient<ITokenCacheAccessor, PerUserTableTokenCacheAccessor>();
            builder.Services.AddTransient<MsalClientFactory>();
        }
    }
}
