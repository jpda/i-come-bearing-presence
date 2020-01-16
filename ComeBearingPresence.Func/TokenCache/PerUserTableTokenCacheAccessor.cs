using System;
using System.Threading.Tasks;
using ComeBearingPresence.Func.Model;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Identity.Client;
using Microsoft.WindowsAzure.Storage.Table;

[assembly: FunctionsStartup(typeof(ComeBearingPresence.Func.Startup))]

namespace ComeBearingPresence.Func
{
    public class PerUserTableTokenCacheAccessor : ITokenCacheAccessor
    {
        private readonly CloudTable _table;
        private string _userCacheKey;

        public PerUserTableTokenCacheAccessor(CloudTable table)
        {
            _table = table;
        }

        public void Configure(string userCacheIdentifier)
        {
            _userCacheKey = userCacheIdentifier;
        }

        public void BeforeAccess(TokenCacheNotificationArgs args)
        {
            if (string.IsNullOrEmpty(_userCacheKey)) throw new ArgumentNullException(_userCacheKey);

            var op = TableOperation.Retrieve<TableTokenCacheItem>(args.ClientId, args.IsApplicationCache ? args.ClientId : _userCacheKey);
            var result = _table.ExecuteAsync(op).Result;

            if (result.HttpStatusCode < 300)
            {
                var userCache = result.Result as TableTokenCacheItem;
                args.TokenCache.DeserializeMsalV3(Convert.FromBase64String(userCache.TokenData));
            }
        }

        public void AfterAccess(TokenCacheNotificationArgs args)
        {
            if (string.IsNullOrEmpty(_userCacheKey)) throw new ArgumentNullException(_userCacheKey);

            if (args.HasStateChanged)
            {
                var entity = new TableTokenCacheItem()
                {
                    PartitionKey = args.ClientId,
                    RowKey = args.IsApplicationCache ? args.ClientId : _userCacheKey,
                    TokenData = Convert.ToBase64String(args.TokenCache.SerializeMsalV3())
                };
                var op = TableOperation.InsertOrReplace(entity);
                _table.ExecuteAsync(op).Wait();
            }
        }

        public async Task<bool> UpdateCacheKey(string partition, string old, string now)
        {
            var op = TableOperation.Retrieve<TableTokenCacheItem>(partition, old);
            var result = await _table.ExecuteAsync(op).ConfigureAwait(true);

            if (result.HttpStatusCode < 300)
            {
                var oldItem = result.Result as TableTokenCacheItem;

                var newItem = new TableTokenCacheItem()
                {
                    PartitionKey = oldItem.PartitionKey,
                    RowKey = now,
                    TokenData = oldItem.TokenData,
                };
                var insert = await _table.ExecuteAsync(TableOperation.Insert(newItem)).ConfigureAwait(true);
                if (insert.HttpStatusCode < 300)
                {
                    var delete = await _table.ExecuteAsync(TableOperation.Delete(oldItem)).ConfigureAwait(true);
                    return delete.HttpStatusCode < 300;
                }
            }
            return false;
        }
    }
}
