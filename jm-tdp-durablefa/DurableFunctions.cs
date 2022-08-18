using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace jm_tdp_durablefa {
    public static class DurableFunctions {
        public static HttpClient client = new HttpClient();

        [FunctionName("Start")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log) {
            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("StartOrchestration", null);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }

        [FunctionName("StartOrchestration")]
        public static async Task<string> RunOrchestrator([OrchestrationTrigger] IDurableOrchestrationContext context) {
            string[] websites = { "https://microsoft.com", "https://google.com", "https://yahoo.com" };
            var outputs = new Dictionary<string, string>();

            foreach (string site in websites)
                outputs.Add(site, await context.CallActivityAsync<string>("ScrapeWebsite", site));
            //Ping all three sites as a function chain.
            //await Scrape microsoft.com    Checkpoint 1
            //await Scrape google.com       Checkpoint 2
            //await Scrape yahoo.com        Checkpoint 3

            string addSite = await context.WaitForExternalEvent<string>("AddSite");
            //await External Event  Checkpoint 4
            while (addSite != null)
                try {
                    Uri uri = new Uri(addSite);
                    if (!outputs.ContainsKey(addSite)) {
                        outputs.Add(addSite, await context.CallActivityAsync<string>("ScrapeWebsite", uri.ToString()));
                    }
                    addSite = await context.WaitForExternalEvent<string>("AddSite");
                } catch (UriFormatException ufe) {
                    addSite = null;
                }

            return JsonConvert.SerializeObject(outputs, Formatting.Indented);
        }

        [FunctionName("ScrapeWebsite")]
        public static async Task<string> SayHello([ActivityTrigger] string name, ILogger log) {
            var homepage = await client.GetAsync(name);
            return homepage.ReasonPhrase;
        }

        [FunctionName("AddSite")]
        public static async Task<string> AddSite(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
            [DurableClient] IDurableOrchestrationClient client,
            ILogger log) {
            log.LogInformation("C# HTTP trigger function processed a request.");

            string site = req.Query["site"];
            string instance = req.Query["instance"];
            await client.RaiseEventAsync(instance, "AddSite", site);
            return "Sent!";
        }

        [FunctionName("StatusCheck")]
        public static async Task<IActionResult> StatusCheck(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient client,
            ILogger log) {
            var runtimeStatus = new List<OrchestrationRuntimeStatus>();

            runtimeStatus.Add(OrchestrationRuntimeStatus.Pending);
            runtimeStatus.Add(OrchestrationRuntimeStatus.Running);

            var result = await client.ListInstancesAsync(new OrchestrationStatusQueryCondition() { RuntimeStatus = runtimeStatus }, CancellationToken.None);
            var hasRunning = result.DurableOrchestrationState.Any();
            if (hasRunning) {
                return new ObjectResult(result) {
                    StatusCode = 503
                };
            }
            return (ActionResult) new OkObjectResult(
                new {
                    isRunning = hasRunning
                }
            );
        }
    }
}