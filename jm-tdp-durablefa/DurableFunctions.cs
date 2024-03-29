using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace jm_tdp_durablefa {
    public static class DurableFunctions {
        private readonly static HttpClient client = new HttpClient();

        [FunctionName("Start")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log) {
            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("StartOrchestration", null);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
            log.LogTrace("Http Trigger Received!");
            return starter.CreateCheckStatusResponse(req, instanceId);
        }

        [FunctionName("StartOrchestration")]
        public static async Task<string> RunOrchestrator([OrchestrationTrigger] IDurableOrchestrationContext context,
            ILogger log) {
            string[] websites = { "https://microsoft.com", "https://google.com", "https://yahoo.com" };
            var outputs = new Dictionary<string, string>();
            var sites = new List<string>();
            foreach (string site in websites) {
                log.LogTrace($"Orchestration queueing Activity for {site}");
                outputs.Add(site, await context.CallActivityAsync<string>("ScrapeWebsite", site));
                sites.Add(site);
                context.SetCustomStatus(new {
                    sites = sites,
                    isWaitingForExternal = false
                });
            }
            //Ping all three sites as a function chain.
            //await Scrape microsoft.com    Checkpoint 1
            //await Scrape google.com       Checkpoint 2
            //await Scrape yahoo.com        Checkpoint 3
            context.SetCustomStatus(new {
                sites = sites,
                isWaitingForExternal = true,
                externalURL = $"./api/addsite?instance={context.InstanceId}&site=<url>"
            });

            log.LogTrace($"Orchestration waiting for external response!");
            string addSite = await context.WaitForExternalEvent<string>("AddSite");
            //await External Event  Checkpoint 4
            while (addSite != null)
                try {
                    Uri uri = new Uri(addSite);
                    if (!outputs.ContainsKey(addSite)) {
                        log.LogTrace($"Orchestration queueing Activity for {addSite}");
                        outputs.Add(addSite, await context.CallActivityAsync<string>("ScrapeWebsite", uri.ToString()));
                        sites.Add(addSite);
                        context.SetCustomStatus(new {
                            sites = sites,
                            isWaitingForExternal = true,
                            externalURL = $"./api/addsite?instance={context.InstanceId}&site=<url>",
                            note = "Enter a null value for site to end."
                        });
                    }
                    log.LogTrace($"Orchestration waiting for external response!");
                    addSite = await context.WaitForExternalEvent<string>("AddSite");
                } catch (UriFormatException ufe) {
                    addSite = null;
                    log.LogTrace($"External response accepted to complete orchestration.");
                }

            return JsonSerializer.Serialize(outputs);
        }

        [FunctionName("ScrapeWebsite")]
        public static async Task<string> SayHello([ActivityTrigger] string name, ILogger log) {
            var homepage = await client.GetAsync(name);
            log.LogTrace($"Activity received: {homepage.ReasonPhrase} from {name}!");
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
            log.LogTrace($"External info received: {site} for instance: {instance}");
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
            log.LogInformation("Query",JsonSerializer.Serialize(result.DurableOrchestrationState));
            var hasRunning = result.DurableOrchestrationState.Any();
            if (hasRunning) {
                return new ObjectResult(result) {
                    StatusCode = 503,                           //Tell DevOps we're busy
                    Value = result.DurableOrchestrationState    //List an current instanceids
                };
            }
            return (ActionResult) new OkObjectResult(result.DurableOrchestrationState);
        }

        //OR we kill all pending/running orchestrations

        [FunctionName("KillAll")]
        public static async Task<IActionResult> KillAll(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient client,
            ILogger log) {
            var runtimeStatus = new List<OrchestrationRuntimeStatus>();

            runtimeStatus.Add(OrchestrationRuntimeStatus.Pending);
            runtimeStatus.Add(OrchestrationRuntimeStatus.Running);

            var result = await client.ListInstancesAsync(new OrchestrationStatusQueryCondition() { RuntimeStatus = runtimeStatus }, CancellationToken.None);
            log.LogInformation("Terminating", JsonSerializer.Serialize(result.DurableOrchestrationState));
            foreach (var status in result.DurableOrchestrationState)
                await client.TerminateAsync(status.InstanceId, "Forced");

            return (ActionResult) new OkObjectResult(new {
                complete = true
            });
        }

    }
}