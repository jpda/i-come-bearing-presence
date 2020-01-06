using System;
using ComeBearingPresence.Func.Model;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;

[assembly: FunctionsStartup(typeof(ComeBearingPresence.Func.Startup))]

namespace ComeBearingPresence.Func
{
    public class Startup : FunctionsStartup
    {
        private const string CacheKey = "UserTokenCache";

        public override void Configure(IFunctionsHostBuilder builder)
        {
            var config = new ConfigurationBuilder().SetBasePath(Environment.CurrentDirectory).AddJsonFile("local.settings.json", optional: true).AddEnvironmentVariables().Build();//.AddAzureAppConfiguration(Environment.GetEnvironmentVariable("AZMAN-AAC-CONNECTION"), optional: true).Build();
            var aadConfig = config.GetSection("AzureAd");

            builder?.Services.AddHttpClient("graph", x =>
            {
                x.BaseAddress = new Uri("https://graph.microsoft.com/");
            });
            builder.Services.AddLogging();
            builder.Services.AddSingleton<IConfidentialClientApplication>(x =>
            {
                var sa = CloudStorageAccount.Parse(config["TableTokenCache:ConnectionString"]);

                var table = sa.CreateCloudTableClient().GetTableReference("MsalCache");
                table.CreateIfNotExistsAsync().Wait();

                var app = ConfidentialClientApplicationBuilder.Create(aadConfig["ClientId"]).WithRedirectUri(aadConfig["RedirectUrl"]).WithClientSecret(aadConfig["ClientSecret"]).WithTenantId(aadConfig["TenantId"]).Build();
                app.UserTokenCache.SetBeforeAccess(args =>
                {
                    // the cache here is sort of a mess. we need a per-user one for confidential apps, but we can't really have one because of how the cache is implemented :/
                    //var key = args.Account.HomeAccountId.ObjectId;

                    var op = TableOperation.Retrieve<TableTokenCacheItem>(args.ClientId, CacheKey);
                    var result = table.ExecuteAsync(op).Result;

                    if (result.HttpStatusCode < 300)
                    {
                        var userCache = result.Result as TableTokenCacheItem;
                        args.TokenCache.DeserializeMsalV3(Convert.FromBase64String(userCache.TokenData));
                    }
                });
                app.UserTokenCache.SetAfterAccess(args =>
                {
                    if (args.HasStateChanged)
                    {
                        var entity = new TableTokenCacheItem()
                        {
                            PartitionKey = args.ClientId,
                            RowKey = CacheKey,
                            TokenData = Convert.ToBase64String(args.TokenCache.SerializeMsalV3())
                        };
                        var op = TableOperation.InsertOrReplace(entity);
                        table.ExecuteAsync(op).Wait();
                    }
                });

                return app;
            });
        }
    }
}
