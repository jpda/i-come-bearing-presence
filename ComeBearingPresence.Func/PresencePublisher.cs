using System;
using System.Collections.Generic;
using System.Globalization;
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
using System.Web.Http;

[assembly: FunctionsStartup(typeof(Startup))]

namespace ComeBearingPresence.Func
{
    public class PresencePublisher
    {
        private readonly IEnumerable<string> _scopes = new[] { "offline_access", "Presence.Read" };
        private readonly ILogger _log;
        private readonly MsalClientFactory _msalFactory;

        public PresencePublisher(ILoggerFactory logger, MsalClientFactory msalFactory)
        {
            _log = logger.CreateLogger<PresencePublisher>();
            _msalFactory = msalFactory;
        }

        [FunctionName("auth-start")]
        public async Task<IActionResult> AuthStart([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "auth/start")] HttpRequest req)
        {
            var scopes = new[] { "offline_access", "Presence.Read" };
            var authorizeUrl = await _msalFactory.Create().GetAuthorizationRequestUrl(scopes).WithExtraQueryParameters("response_mode=form_post").ExecuteAsync().ConfigureAwait(true);
            return new RedirectResult(authorizeUrl.ToString());
        }

        [FunctionName("auth-end")]
        public async Task<IActionResult> AuthenticationResponseReceived([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "auth/end")] HttpRequest req, [Table("%UserPresenceSubscribedUserTableName%", Connection = "UserPresenceStorageConnection")]CloudTable table)
        {
            string c = req?.Form["code"];

            if (string.IsNullOrEmpty(c) || table == null)
            {
                return new BadRequestResult();
            }

            try
            {
                var transientIdentity = $"{Guid.NewGuid().ToString()}.transient";
                var cca = _msalFactory.CreateWithTransientIdentity(transientIdentity);
                var result = await cca.AcquireTokenByAuthorizationCode(_scopes, c).ExecuteAsync().ConfigureAwait(true);
                var account = await cca.GetAccountAsync(result.Account.HomeAccountId.Identifier).ConfigureAwait(true);

                var switched = await _msalFactory.SwitchTransientKeyToActual(transientIdentity, result.Account.HomeAccountId.Identifier).ConfigureAwait(true);

                if (account != null)
                {
                    _log.LogInformation($"Found account: {account.HomeAccountId.Identifier}");
                    var a = new UserPresenceRequest()
                    {
                        PartitionKey = "UserAccount",
                        RowKey = account.HomeAccountId.Identifier,
                        ObjectId = account.HomeAccountId.ObjectId,
                        Upn = account.Username,
                        TenantId = account.HomeAccountId.TenantId
                    };

                    var op = TableOperation.InsertOrReplace(a);
                    await table.ExecuteAsync(op).ConfigureAwait(true);

                    var templatePath = System.IO.Path.Join(Environment.CurrentDirectory, @"..\..\..\Assets\authend.html");
                    var template = System.IO.File.ReadAllText(templatePath);
                    var content = string.Format(CultureInfo.InvariantCulture, template, $"authentication successful! thanks {a.Upn}! we'll start polling for your presence and notify your subscribers");
                    return new ContentResult() { Content = content, ContentType = "text/html", StatusCode = 200 };
                }
            }
            catch (Exception ex) //todo: catch something more specific here
            {
                _log.LogError($"{ex.Message}: {ex.StackTrace}");
                return new InternalServerErrorResult();
            }

            return new OkResult();
        }

        [FunctionName("get-last-presence")]
        public static async Task<IActionResult> GetLastPresence([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "presence/{user}/last")] HttpRequest req, string user, [Table("%UserPresenceStorageTableName%", Connection = "UserPresenceStorageConnection")]CloudTable table)
        {
            var last = await table.Retrieve<UserPresence>("LastPresence", user).ConfigureAwait(true);
            if (last == null) return new OkObjectResult(new UserPresence());

            return new OkObjectResult(last);
        }

        // todo: evaluate if durable would be better here
        [FunctionName("presence-refresh")]
        public async Task CheckPresenceForAll([TimerTrigger("0/30 * * * * *")]TimerInfo timer,
            [Table("%UserPresenceSubscribedUserTableName%", Connection = "UserPresenceStorageConnection")]CloudTable subscriberTable,
            [Table("%UserPresenceStorageTableName%", Connection = "UserPresenceStorageConnection")]CloudTable lastPresenceTable,
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
                var current = await GetPresenceFromGraph(a.RowKey, a.Upn).ConfigureAwait(true);
                var last = await lastPresenceTable.Retrieve<UserPresence>("LastPresence", a.Upn).ConfigureAwait(true);
                if (current != null && last != null && current.ObjectId == last.ObjectId && current.Activity == last.Activity && current.Availability == last.Availability) continue;

                var upsert = TableOperation.InsertOrReplace(current);
                await lastPresenceTable.ExecuteAsync(upsert).ConfigureAwait(true);

                // todo: create topic _per user_ if doesn't exist
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

        private async Task CreateUserTopic(string identifier)
        {
            // todo: since msi is here, let's do this
        }

        private async Task<UserPresence> GetPresenceFromGraph(string identifier, string upn)
        {
            if (string.IsNullOrEmpty(identifier) && string.IsNullOrEmpty(upn)) return null;

            var cca = _msalFactory.CreateForIdentifier(identifier);
            var account = await cca.GetAccountAsync(identifier).ConfigureAwait(true);
            AuthenticationResult token = null;

            try
            {
                if (account == null)
                {
                    token = await cca.AcquireTokenSilent(_scopes, upn).ExecuteAsync().ConfigureAwait(true);
                }
                else
                {
                    token = await cca.AcquireTokenSilent(_scopes, account).ExecuteAsync().ConfigureAwait(true);
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
        }
    }
}