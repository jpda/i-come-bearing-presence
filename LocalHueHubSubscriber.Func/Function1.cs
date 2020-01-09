using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace LocalHueHubSubscriber.Func
{

    public class UserPresence
    {
        public string ObjectId { get; set; }
        public string Upn { get; set; }
        public string Availability { get; set; }
        public string Activity { get; set; }
    }

    public static class Function1
    {
        private static readonly Dictionary<string, string> _stateColorMap = new Dictionary<string, string>()
        {
            { "Available", "25500" },
            { "AvailableIdle", "25500" },
            { "Away", "9000" },
            { "BeRightBack", "9000" },
            { "Busy", "500" },
            { "BusyIdle", "500" },
            { "DoNotDisturb", "500" }
        };

        [FunctionName("presence-subscriber")]
        public static async Task Run([ServiceBusTrigger("%ServiceBusTopicId%", "%ServiceBusSubscriptionId%", Connection = "PresenceTopicEndpoint")]UserPresence presence, ILogger log)
        {
            var config = new ConfigurationBuilder().SetBasePath(Environment.CurrentDirectory).AddJsonFile("local.settings.json", optional: true).AddEnvironmentVariables().Build();
            log.LogInformation($"Hub: {config["LocalHueHubConnection"]}");
            await SetLight(presence.Availability, config["LocalHueHubConnection"]);
        }

        private static async Task SetLight(string status, string endpoint)
        {
            var url = endpoint;
            var jdoc = "{{\"on\":true, \"hue\":{0}, \"sat\":255, \"bri\":254, \"effect\": \"none\"}}";
            var offWork = "{\"on\":true, \"effect\":\"colorloop\"}";
            var payload = offWork;

            Console.WriteLine($"setting light with status {status}");

            if (_stateColorMap.ContainsKey(status))
            {
                Console.WriteLine($"found {status} in map, setting light to {_stateColorMap[status]}");
                payload = string.Format(jdoc, _stateColorMap[status]);
            }

            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await new HttpClient().PutAsync(url, content);
            Console.WriteLine($"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }
}