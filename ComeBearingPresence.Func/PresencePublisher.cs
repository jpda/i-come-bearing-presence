using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Identity.Client;
using Microsoft.WindowsAzure.Storage.Table;

using ComeBearingPresence.Func;
using ComeBearingPresence.Func.Model;

[assembly: FunctionsStartup(typeof(Startup))]

namespace ComeBearingPresence.Func
{
    public class PresencePublisher
    {
        private readonly IConfidentialClientApplication _cca;
        private readonly IEnumerable<string> _scopes = new[] { "offline_access", "Presence.Read" };
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _log;

        public PresencePublisher(IConfidentialClientApplication cca, IHttpClientFactory httpClientFactory, ILoggerFactory logger)
        {
            _cca = cca;
            _httpClientFactory = httpClientFactory;
            _log = logger.CreateLogger<PresencePublisher>();
        }

        [FunctionName("auth-start")]
        public async Task<IActionResult> AuthStart([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "auth/start")] HttpRequest req)
        {
            var scopes = new[] { "offline_access", "Presence.Read" };

            var account = await _cca.GetAccountsAsync().ConfigureAwait(true);
            var tokenRequest = _cca.AcquireTokenSilent(scopes, account.FirstOrDefault());

            AuthenticationResult result;

            try
            {
                result = await tokenRequest.ExecuteAsync().ConfigureAwait(true);
            }
            catch (MsalUiRequiredException ex)
            {
                _log.LogError($"Ui required for msal: {ex}");
                var authorizeUrl = await _cca.GetAuthorizationRequestUrl(scopes).WithExtraQueryParameters("response_mode=form_post").ExecuteAsync().ConfigureAwait(true);
                return new RedirectResult(authorizeUrl.ToString());
            }

            if (result == null) { return new ForbidResult(); }
            return new OkObjectResult(result);
        }

        [FunctionName("auth-end")]
        public async Task<IActionResult> AuthenticationResponseReceived([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "auth/end")] HttpRequest req, [Table("%UserPresenceSubscribedUserTableName%", Connection = "UserPresenceStorageConnection")]IAsyncCollector<UserPresenceRequest> users)
        {
            string c = req?.Form["code"];

            if (string.IsNullOrEmpty(c))
            {
                return new BadRequestResult();
            }

            await _cca.AcquireTokenByAuthorizationCode(_scopes, c).ExecuteAsync().ConfigureAwait(true);
            var accounts = await _cca.GetAccountsAsync().ConfigureAwait(true);
            if (accounts.Any())
            {
                var account = accounts.Single();
                var a = new UserPresenceRequest()
                {
                    PartitionKey = "UserAccount",
                    RowKey = account.HomeAccountId.Identifier,
                    ObjectId = account.HomeAccountId.ObjectId,
                    Upn = account.Username,
                    TenantId = account.HomeAccountId.TenantId
                };
                await users.AddAsync(a).ConfigureAwait(true);


                var templatePath = System.IO.Path.Join(Environment.CurrentDirectory, @"..\..\Assets\authend.html");
                _log.LogDebug($"Fetching html template from {templatePath}");
                var template = System.IO.File.ReadAllText(templatePath);
                var content = string.Format(CultureInfo.InvariantCulture, template, $"authentication successful! thanks {a.Upn}! we'll start polling for your presence and notify your subscribers");
                return new ContentResult() { Content = content, ContentType = "text/html", StatusCode = 200 };
            }

            return new OkResult();
        }

        [FunctionName("get-live-presence")]
        public async Task<IActionResult> GetLivePresence([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "presence/{user}/live")] HttpRequest req, string user)
        {
            return new OkObjectResult(await GetPresenceFromGraph(user).ConfigureAwait(true));
        }

        [FunctionName("get-last-presence")]
        public static async Task<IActionResult> GetLastPresence([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "presence/{user}/last")] HttpRequest req, string user, [Table("%UserPresenceStorageTableName%", Connection = "UserPresenceStorageConnection")]CloudTable table)
        {
            var last = await table.Retrieve<UserPresence>("LastPresence", user).ConfigureAwait(true);
            if (last == null) return new OkObjectResult(new UserPresence());

            return new OkObjectResult(last);
        }

        //todo: evaluate if durable would be better here
        [FunctionName("presence-refresh")]
        public async Task CheckPresenceForAll([TimerTrigger("0/30 * * * * *")]TimerInfo timer,
            [Table("%UserPresenceSubscribedUserTableName%", Connection = "UserPresenceRequestStorageConnection")]CloudTable subscriberTable,
            [Table("%UserPresenceStorageTableName%", Connection = "UserPresenceRequestStorageConnection")]CloudTable lastPresenceTable,
            Binder serviceBusBinder
            )
        {
            await CheckAndUpdatePresence(subscriberTable, lastPresenceTable, serviceBusBinder).ConfigureAwait(true);
            return;
        }

        [FunctionName("presence-refresh-sync")]
        public async Task<IActionResult> CheckPresenceForAllSync([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "presence/all")] HttpRequest req,
           [Table("%UserPresenceSubscribedUserTableName%", Connection = "UserPresenceStorageConnection")]CloudTable subscriberTable,
           [Table("%UserPresenceStorageTableName%", Connection = "UserPresenceStorageConnection")]CloudTable lastPresenceTable,
           //[ServiceBus("presence", Connection = "PresenceTopicConnection", EntityType = Microsoft.Azure.WebJobs.ServiceBus.EntityType.Topic)]IAsyncCollector<UserPresence> pub
           Binder serviceBusBinder
           )
        {
            await CheckAndUpdatePresence(subscriberTable, lastPresenceTable, serviceBusBinder).ConfigureAwait(true);
            return new OkResult();
        }

        private async Task CheckAndUpdatePresence(CloudTable subscriberTable, CloudTable lastPresenceTable, Binder serviceBusBinder)
        {
            if (subscriberTable == null || lastPresenceTable == null || serviceBusBinder == null) return;
            // get subscriber list, e.g., the list of presence checks
            var rows = await subscriberTable.Query<UserPresenceRequest>("PartitionKey eq 'UserAccount'").ConfigureAwait(true);

            foreach (var a in rows)
            {
                _log.LogInformation($"found user: {a.PartitionKey}: {a.RowKey}, {a.Upn}");
                var current = await GetPresenceFromGraph(a.Upn).ConfigureAwait(true);
                var last = await lastPresenceTable.Retrieve<UserPresence>("LastPresence", a.Upn).ConfigureAwait(true);
                if (current != null && last != null && current.ObjectId == last.ObjectId && current.Activity == last.Activity && current.Availability == last.Availability) continue;

                var upsert = TableOperation.InsertOrReplace(current);
                await lastPresenceTable.ExecuteAsync(upsert).ConfigureAwait(true);

                //todo: create topic if doesn't exist
                // notify subscribers
                var attributes = new Attribute[]
                {
                    new ServiceBusAccountAttribute("UserPresenceTopicNamespaceConnection"),
                    new ServiceBusAttribute(current.ObjectId, Microsoft.Azure.WebJobs.ServiceBus.EntityType.Topic)
                };

                var collector = await serviceBusBinder.BindAsync<IAsyncCollector<UserPresence>>(attributes).ConfigureAwait(true);
                await collector.AddAsync(current).ConfigureAwait(true);
                await collector.FlushAsync().ConfigureAwait(true);
            }
        }

        private async Task<UserPresence> GetPresenceFromGraph(string user)
        {
            var userId = user ?? "john@jpd.ms";
            var account = await _cca.GetAccountAsync(userId).ConfigureAwait(true);

            AuthenticationResult token = null;

            try
            {
                if (account == null)
                {
                    token = await _cca.AcquireTokenSilent(_scopes, userId).ExecuteAsync().ConfigureAwait(true);
                }
                else
                {
                    token = await _cca.AcquireTokenSilent(_scopes, account).ExecuteAsync().ConfigureAwait(true);
                }
            }
            catch (MsalUiRequiredException ex)
            {
                _log.LogError(ex.Message);
                return null;
            }

            //todo: evaluate graph client vs. just doing it on my own. it's a single call. need retry/backoff here too

            var gc = new GraphServiceClient(new DelegateAuthenticationProvider(x =>
            {
                x.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
                return Task.FromResult(0);
            }));

            var t = await gc.Users[token.Account.HomeAccountId.ObjectId].Presence.Request().GetAsync().ConfigureAwait(true);

            return new UserPresence()
            {
                PartitionKey = "LastPresence",
                RowKey = token.Account.Username,
                Activity = t.Activity,
                Availability = t.Availability,
                Upn = token.Account.Username,
                ObjectId = t.Id,
                Timestamp = DateTime.UtcNow
            };

            var c = _httpClientFactory.CreateClient("graph");
            c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
            var presenceApi = new Uri($"/beta/users/{token.Account.HomeAccountId.ObjectId}/presence", UriKind.Relative);
            var response = await c.GetAsync(presenceApi).ConfigureAwait(true);

            switch (response.StatusCode)
            {
                case System.Net.HttpStatusCode.NotFound:
                case System.Net.HttpStatusCode.BadRequest:
                    {
                        break;
                    }
                default: { break; }
            }

            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            var data = System.Text.Json.JsonDocument.Parse(content);

            return new UserPresence()
            {
                Activity = data.RootElement.GetProperty("activity").GetString(),
                Availability = data.RootElement.GetProperty("availability").GetString(),
                Upn = token.Account.Username,
                ObjectId = data.RootElement.GetProperty("id").GetString()
            };
        }
    }
}