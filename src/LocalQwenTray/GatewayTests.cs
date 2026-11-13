using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
namespace LocalQwenTray;
// Real Kestrel gateway against a fake llama-server on random loopback ports: no GPU, no port 8000.
internal static class GatewayTests
{
    static string[] EmbeddingEngine_Args() => LocalQwenTray.EmbeddingEngine.BuildArguments("model.gguf", "embedding.log");
    static int FreePort() { var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    public static async Task Run()
    {
        const string key = "gw-test-key";
        int backendPort = FreePort(), publicPort = FreePort();
        string? backendAuth = null, backendBody = null;
        Action? crashBackend = null;
        int dieBeforeReply = 0;
        string J(string json, string effort = "low") => Encoding.UTF8.GetString(Gateway.ApplyThinkingDefaults(Encoding.UTF8.GetBytes(json), effort));
        SelfTests.Check("thinking defaults added to a plain chat request", J("{\"messages\":[]}") == "{\"messages\":[],\"chat_template_kwargs\":{\"reasoning_effort\":\"low\",\"preserve_thinking\":false}}");
        SelfTests.Check("client's own reasoning effort is never overridden", J("{\"chat_template_kwargs\":{\"reasoning_effort\":\"xhigh\"}}").Contains("\"reasoning_effort\":\"xhigh\"") && !J("{\"reasoning_effort\":\"medium\"}").Contains("\"low\""));
        SelfTests.Check("client's own preserve_thinking and enable_thinking kept", J("{\"chat_template_kwargs\":{\"preserve_thinking\":true,\"enable_thinking\":false}}").Contains("\"preserve_thinking\":true") && J("{\"chat_template_kwargs\":{\"enable_thinking\":false}}").Contains("\"enable_thinking\":false"));
        SelfTests.Check("non-JSON or non-object bodies pass through untouched", J("not json") == "not json" && J("[1,2]") == "[1,2]");
        SelfTests.Check("legacy top-level effort still receives preserve_thinking=false", J("{\"reasoning_effort\":\"high\"}") == "{\"reasoning_effort\":\"high\",\"chat_template_kwargs\":{\"preserve_thinking\":false}}");
        SelfTests.Check("legacy enable_thinking-only kwargs retain tray effort and history defaults", J("{\"chat_template_kwargs\":{\"enable_thinking\":true}}") == "{\"chat_template_kwargs\":{\"enable_thinking\":true,\"reasoning_effort\":\"low\",\"preserve_thinking\":false}}");
        SelfTests.Check("legacy reasoning_format alone retains tray defaults and max_tokens", J("{\"reasoning_format\":\"deepseek\",\"max_tokens\":19}") == "{\"reasoning_format\":\"deepseek\",\"max_tokens\":19,\"chat_template_kwargs\":{\"reasoning_effort\":\"low\",\"preserve_thinking\":false}}");
        var fake = WebApplication.CreateSlimBuilder();
        fake.Logging.ClearProviders();
        fake.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, backendPort));
        await using var backend = fake.Build();
        backend.Run(async ctx =>
        {
            backendAuth = ctx.Request.Headers.Authorization.ToString();
            if (ctx.Request.Path == "/v1/models") { await ctx.Response.WriteAsync("{\"data\":[{\"id\":\"" + Policy.Model + "\",\"meta\":{\"n_ctx\":131072},\"live\":true}]}"); return; }
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            backendBody = body;
            if (body.Contains("\"die_before_reply\":true") && Interlocked.Decrement(ref dieBeforeReply) >= 0) { crashBackend?.Invoke(); ctx.Abort(); return; }
            if (body.Contains("\"abort\":true"))
            {
                ctx.Response.ContentType = "text/event-stream";
                await ctx.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n");
                await ctx.Response.Body.FlushAsync();
                await Task.Delay(30);
                crashBackend?.Invoke();
                ctx.Abort();
                return;
            }
            if (body.Contains("\"stream\":true"))
            {
                ctx.Response.ContentType = "text/event-stream";
                await ctx.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n"); await ctx.Response.Body.FlushAsync();
                await Task.Delay(500);
                await ctx.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}],\"timings\":{\"predicted_n\":42,\"predicted_per_second\":171.5}}\n\ndata: [DONE]\n\n");
                return;
            }
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"choices\":[{\"message\":{\"content\":\"echo:" + body.Length + "\"}}],\"timings\":{\"cache_n\":5,\"prompt_n\":3,\"predicted_n\":7,\"predicted_per_second\":160.0}}");
        });
        await backend.StartAsync();

        var host = new TestHost();
        var supervisor = new Supervisor(new Controller(host));
        var dir = Path.Combine(Path.GetTempPath(), "localqwen-gw-" + Guid.NewGuid());
        string effort = "medium";
        await using var gateway = new Gateway(supervisor, key, publicPort, backendPort, dir, null, () => effort);
        await gateway.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{publicPort}"), Timeout = TimeSpan.FromSeconds(20) };
        HttpRequestMessage Req(HttpMethod m, string path, string? json = null, bool auth = true)
        {
            var r = new HttpRequestMessage(m, path);
            if (auth) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (json is not null) r.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return r;
        }

        SelfTests.Check("gateway health answers without a key or a load", (await client.GetAsync("/health")).IsSuccessStatusCode && host.UpCalls == 0);
        using (var r = await client.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{}", auth: false)))
            SelfTests.Check("gateway rejects a missing API key and loads nothing", r.StatusCode == HttpStatusCode.Unauthorized && host.UpCalls == 0);
        using (var r = await client.SendAsync(Req(HttpMethod.Get, "/v1/models")))
            SelfTests.Check("model listing while asleep never wakes the GPU", r.IsSuccessStatusCode && (await r.Content.ReadAsStringAsync()).Contains(Policy.Model) && host.UpCalls == 0);
        using (var r = await client.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{\"messages\":[]}")))
        {
            var text = await r.Content.ReadAsStringAsync();
            SelfTests.Check("first request loads the model on demand and is answered", r.IsSuccessStatusCode && text.Contains("echo:") && host.UpCalls == 1 && supervisor.IsReady);
        }
        SelfTests.Check("gateway authenticates to the private backend with the configured key", backendAuth == "Bearer " + key);
        SelfTests.Check("tray reasoning setting reaches llama-server on chat requests", backendBody is not null && backendBody.Contains("\"reasoning_effort\":\"medium\"") && backendBody.Contains("\"preserve_thinking\":false"));
        effort = "xhigh";
        using (var r = await client.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{\"messages\":[]}"))) { }
        SelfTests.Check("changing reasoning applies to the next request without a reload", backendBody!.Contains("\"reasoning_effort\":\"xhigh\"") && host.UpCalls == 1);
        SelfTests.Check("last reply speed captured from llama-server timings", supervisor.LastReply is { Tokens: 7, TokensPerSecond: 160.0 });
        using (var r = await client.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{\"stream\":true}"), HttpCompletionOption.ResponseHeadersRead))
        {
            var stream = await r.Content.ReadAsStreamAsync();
            var buffer = new byte[256];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = await stream.ReadAsync(buffer);
            var first = Encoding.UTF8.GetString(buffer, 0, n);
            SelfTests.Check($"SSE tokens stream through live (first chunk after {sw.ElapsedMilliseconds} ms)", first.Contains("Hel") && sw.ElapsedMilliseconds < 400);
            var rest = await new StreamReader(stream).ReadToEndAsync();
            SelfTests.Check("streamed reply completes and reports speed", rest.Contains("[DONE]") && supervisor.LastReply is { Tokens: 42 });
        }
        using (var r = await client.SendAsync(Req(HttpMethod.Get, "/v1/models")))
            SelfTests.Check("model listing while loaded is live and cached for sleep", (await r.Content.ReadAsStringAsync()).Contains("\"live\":true") && File.Exists(Path.Combine(dir, "models-cache.json")));
        supervisor.IdleTimeout = TimeSpan.FromMilliseconds(50);
        await Task.Delay(120);
        SelfTests.Check("idle model unloads after the timeout", await supervisor.IdleTick(default) && host.StopCalls == 1 && !supervisor.IsReady);
        using (var r = await client.SendAsync(Req(HttpMethod.Get, "/v1/models")))
            SelfTests.Check("cached live listing served after unload without reloading", (await r.Content.ReadAsStringAsync()).Contains("\"live\":true") && host.UpCalls == 1);
        int before = host.UpCalls;
        var parallel = Enumerable.Range(0, 3).Select(_ => client.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{}"))).ToArray();
        var answers = await Task.WhenAll(parallel);
        SelfTests.Check("concurrent first requests share one load", answers.All(a => a.IsSuccessStatusCode) && host.UpCalls == before + 1);
        foreach (var a in answers) a.Dispose();
        supervisor.IdleTimeout = null;
        await supervisor.Unload(default);
        host.Free = 1000;
        using (var r = await client.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{}")))
        {
            var text = await r.Content.ReadAsStringAsync();
            SelfTests.Check("a load that cannot fit returns a clear 503 instead of hanging", r.StatusCode == HttpStatusCode.ServiceUnavailable && text.Contains("VRAM"));
        }
        var profileHost = new TestHost();
        var profiles = new AppConfig();
        // Waits skip ahead on a shifted clock, so the background quiet minute passes instantly but is still recorded.
        var skipped = TimeSpan.Zero;
        var profileSupervisor = new Supervisor(new Controller(profileHost), clock: () => DateTimeOffset.Now + skipped, host: profileHost, delay: (d, _) => { skipped += d; return Task.CompletedTask; });
        int profilePort = FreePort();
        var lines = new List<string>();
        int embedStarts = 0;
        bool embedMissing = false;
        Task<int> EmbeddingEngine(CancellationToken _)
        {
            if (embedMissing) throw new FileNotFoundException("nomic-embed-text is not downloaded in Ollama");
            embedStarts++;
            return Task.FromResult(backendPort);
        }
        await using var profileGateway = new Gateway(profileSupervisor, key, profilePort, backendPort, dir, log: line => { lock (lines) lines.Add(line); }, reasoning: () => "low", config: profiles, embeddings: EmbeddingEngine);
        await profileGateway.StartAsync();
        using var profileClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{profilePort}"), Timeout = TimeSpan.FromSeconds(10) };
        using (var listing = await profileClient.SendAsync(Req(HttpMethod.Get, "/v1/models")))
            SelfTests.Check("profile listing advertises extraction and default without loading", (await listing.Content.ReadAsStringAsync()).Contains("savannah-extraction") && profileHost.UpCalls == 0);
        using (var status = await profileClient.SendAsync(Req(HttpMethod.Get, "/v1/status")))
            SelfTests.Check("status polls never load a model", (await status.Content.ReadAsStringAsync()).Contains("\"loaded_profile\":null") && profileHost.UpCalls == 0);
        using (var unauthorized = await profileClient.SendAsync(Req(HttpMethod.Get, "/v1/status", auth: false)))
            SelfTests.Check("profile status requires the gateway API key", unauthorized.StatusCode == HttpStatusCode.Unauthorized);
        using (var streaming = await profileClient.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{\"stream\":true}"), HttpCompletionOption.ResponseHeadersRead))
        {
            string json = "{ \"model\": \"savannah-extraction\", \"max_tokens\": 8192, \"reasoning_effort\": \"high\", \"reasoning\": {\"budget\": 4000}, \"chat_template_kwargs\": {\"enable_thinking\": true} }";
            var pending = profileClient.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", json));
            await Task.Delay(50);
            SelfTests.Check("profile switch does not stop an in-flight SSE response", !pending.IsCompleted && profileHost.StopCalls == 0);
            var completed = await streaming.Content.ReadAsStringAsync();
            using var result = await pending;
            SelfTests.Check("switch drains SSE then loads extraction once", completed.Contains("[DONE]") && result.IsSuccessStatusCode && profileHost.StopCalls == 1 && profileHost.UpCalls == 2 && profileHost.SelectedProfile == "savannah-extraction");
            SelfTests.Check("thinking controls and max_tokens pass through byte-for-byte", backendBody == json);
            SelfTests.Check("background extraction waited a quiet minute after the chat stream", skipped > ProfileAdmission.Quiet - TimeSpan.FromSeconds(1));
        }
        const string retry = "{\"model\":\"savannah-extraction\",\"chat_template_kwargs\":{\"enable_thinking\":false},\"max_tokens\":256}";
        using (var result = await profileClient.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", retry)))
            SelfTests.Check("thinking-off retry stays on the same exact profile", result.IsSuccessStatusCode && backendBody == retry && profileHost.UpCalls == 2);
        using (var status = await profileClient.SendAsync(Req(HttpMethod.Get, "/v1/status")))
            SelfTests.Check("status identifies the loaded extraction profile", (await status.Content.ReadAsStringAsync()).Contains("\"loaded_profile\":\"savannah-extraction\""));
        using (var listing = await profileClient.SendAsync(Req(HttpMethod.Get, "/v1/models")))
            SelfTests.Check("listing while extraction is loaded does not switch profiles", listing.IsSuccessStatusCode && profileHost.StopCalls == 1);
        using (var result = await profileClient.SendAsync(Req(HttpMethod.Post, "/v1/responses", "{\"model\":\"codex-auto-review\",\"max_tokens\":30}")))
            SelfTests.Check("legacy model names switch back to the default and retain tray defaults", result.IsSuccessStatusCode && profileHost.SelectedProfile is null && profileHost.StopCalls == 2 && backendBody!.Contains("\"reasoning_effort\":\"low\"") && backendBody.Contains("\"max_tokens\":30"));
        // A crash ends the process and closes its port, so a reload sees no foreign listener.
        crashBackend = () => { profileHost.State = new(true, false, 7); profileHost.Health = new(false); };
        using (var result = await profileClient.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{\"abort\":true}"), HttpCompletionOption.ResponseHeadersRead))
        {
            try { await result.Content.ReadAsStringAsync(); }
            catch (HttpRequestException) { /* a truncated SSE stream must reach the client as a failed response */ }
            for (int i = 0; i < 100 && profileSupervisor.State != TrayState.Error; i++) await Task.Delay(10);
            SelfTests.Check("mid-stream backend crash refreshes status immediately", profileSupervisor.State == TrayState.Error);
        }
        using (var reload = await profileClient.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{}"))) { }
        int loads = profileHost.UpCalls;
        dieBeforeReply = 1;
        using (var result = await profileClient.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{\"die_before_reply\":true,\"messages\":[{\"content\":\"private words\"}]}")))
            SelfTests.Check("engine death before the reply reloads once and retries the request", result.IsSuccessStatusCode && (await result.Content.ReadAsStringAsync()).Contains("echo:") && profileHost.UpCalls == loads + 1);
        dieBeforeReply = 2;
        using (var result = await profileClient.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{\"die_before_reply\":true}")))
            SelfTests.Check("a request that kills the engine twice gets 502 after one reload", result.StatusCode == HttpStatusCode.BadGateway && profileHost.UpCalls == loads + 2);
        int chatLoads = profileHost.UpCalls;
        const string embedJson = "{\"model\":\"nomic-embed-text\",\"input\":[\"search_query: a\",\"b\"]}";
        using (var result = await profileClient.SendAsync(Req(HttpMethod.Post, "/v1/embeddings", embedJson)))
            SelfTests.Check("embeddings go to the embedding engine untouched and never load the chat model", result.IsSuccessStatusCode && backendBody == embedJson && embedStarts == 1 && profileHost.UpCalls == chatLoads);
        dieBeforeReply = 1;
        using (var result = await profileClient.SendAsync(Req(HttpMethod.Post, "/v1/embeddings", "{\"input\":\"x\",\"die_before_reply\":true}")))
            SelfTests.Check("embedding engine death before the reply restarts it and retries once", result.IsSuccessStatusCode && embedStarts == 3);
        embedMissing = true;
        using (var result = await profileClient.SendAsync(Req(HttpMethod.Post, "/v1/embeddings", embedJson)))
            SelfTests.Check("an embedding engine that cannot start returns 503 with the reason", result.StatusCode == HttpStatusCode.ServiceUnavailable && (await result.Content.ReadAsStringAsync()).Contains("not downloaded"));
        using (var listing = await profileClient.SendAsync(Req(HttpMethod.Get, "/v1/models")))
            SelfTests.Check("model listing advertises the embedding model", (await listing.Content.ReadAsStringAsync()).Contains("\"id\":\"nomic-embed-text\""));
        SelfTests.Check("embedding engine runs CPU-only at the model's trained context", EmbeddingEngine_Args() is var e && e[Array.IndexOf(e, "--device") + 1] == "none" && e[Array.IndexOf(e, "-c") + 1] == "2048" && e[Array.IndexOf(e, "-ub") + 1] == "2048" && e.Contains("--embeddings"));
        string[] logged;
        lock (lines) logged = [.. lines];
        SelfTests.Check("one log line per request with model, profile, status and cache reuse",
            logged.Any(l => l.StartsWith("Request POST /v1/chat/completions model savannah-extraction profile savannah-extraction -> 200,") && l.Contains("prompt 3 new + 5 cached, output 7 at 160.0 tok/s"))
            && logged.Count(l => l.StartsWith("Request POST /v1/chat/completions model - profile default -> 200 after one engine reload,")) == 1
            && logged.Any(l => l.StartsWith("Request POST /v1/chat/completions model - profile default -> 502 after one engine reload,"))
            && logged.Any(l => l.StartsWith("Request POST /v1/embeddings model nomic-embed-text profile embeddings -> 200,"))
            && logged.Any(l => l.StartsWith("Request POST /v1/embeddings model - profile embeddings -> 200 after one engine restart,")));
        SelfTests.Check("request log never contains request content", !logged.Any(l => l.Contains("private words")));
        await backend.StopAsync();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
