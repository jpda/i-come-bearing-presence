using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.WindowsAzure.Storage.Table;

using ComeBearingPresence.Func;
using System;

[assembly: FunctionsStartup(typeof(Startup))]

namespace ComeBearingPresence.Func
{
    public static class CloudTableExtensions
    {
        public static async Task<IEnumerable<T>> Query<T>(this CloudTable table, string filter) where T : TableEntity, new()
        {
            TableContinuationToken ct = null;
            var q = new TableQuery<T>().Where(filter);

            var a = new List<T>();

            do
            {
                var request = await table.ExecuteQuerySegmentedAsync(q, ct).ConfigureAwait(true);
                a.AddRange(request.Results);
            } while (ct != null);

            return a;
        }

        public static async Task<T> Retrieve<T>(this CloudTable table, string pkey, string rowkey) where T : TableEntity, new()
        {
            var t = new T
            {
                PartitionKey = pkey,
                RowKey = rowkey
            };
            return await Retrieve<T>(table, t).ConfigureAwait(true);
        }

        public static async Task<T> Retrieve<T>(this CloudTable table, T example) where T : TableEntity, new()
        {
            var q = TableOperation.Retrieve<T>(example.PartitionKey, example.RowKey);
            var result = await table.ExecuteAsync(q).ConfigureAwait(true);
            if (result.HttpStatusCode < 299)
            {
                return result.Result as T;
            }
            return null;
        }
    }
}