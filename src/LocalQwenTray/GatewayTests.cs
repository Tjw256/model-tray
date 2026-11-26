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
    static int FreePort() { var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    public static async Task Run()
    {
        const string key = "gw-test-key";
        int backendPort = FreePort(), publicPort = FreePort();
        string? backendAuth = null, backendBody = null;
        Action? crashBackend = null;
        string propsTag = "first";   // which engine answered /props, to tell remembered answers apart
        int propsCalls = 0;          // how often the engine itself was asked /props
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
            // Shaped like llama-server b11517: /props exists, the other discovery paths are unknown (404).
            if (ctx.Request.Path == "/props") { Interlocked.Increment(ref propsCalls); ctx.Response.ContentType = "application/json; charset=utf-8"; await ctx.Response.WriteAsync("{\"default_generation_settings\":{\"n_ctx\":131072},\"engine\":\"" + propsTag + "\"}"); return; }
            if (DiscoveryAnswers.Paths.Contains(ctx.Request.Path.Value)) { ctx.Response.StatusCode = 404; ctx.Response.ContentType = "application/json; charset=utf-8"; await ctx.Response.WriteAsync("{\"error\":{\"message\":\"File Not Found\",\"type\":\"not_found_error\",\"code\":404}}"); return; }
            if (ctx.Request.Path == "/v1/models") { await ctx.Response.WriteAsync("{\"data\":[{\"id\":\"" + Policy.Model + "\",\"meta\":{\"n_ctx\":131072},\"live\":true}]}"); return; }
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            backendBody = body;
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
            await ctx.Response.WriteAsync("{\"choices\":[{\"message\":{\"content\":\"echo:" + body.Length + "\"}}],\"timings\":{\"predicted_n\":7,\"predicted_per_second\":160.0}}");
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

        async Task<Dictionary<string, (HttpStatusCode Status, string Type, string Body)>> Discover(HttpClient c)
        {
            var seen = new Dictionary<string, (HttpStatusCode, string, string)>();
            foreach (var path in DiscoveryAnswers.Paths.Append("/props?model=" + Policy.Model + "&autoload=false"))
            {
                using var r = await c.SendAsync(Req(HttpMethod.Get, path));
                seen[path] = (r.StatusCode, r.Content.Headers.ContentType?.ToString() ?? "", await r.Content.ReadAsStringAsync());
            }
            return seen;
        }
        // What llama-server answers: /props with its settings, a JSON 404 for the rest.
        bool EngineAnswers(Dictionary<string, (HttpStatusCode Status, string Type, string Body)> seen, string tag) =>
            seen.All(x => x.Key.StartsWith("/props") ? x.Value.Status == HttpStatusCode.OK && x.Value.Body.Contains("default_generation_settings") && x.Value.Body.Contains("\"engine\":\"" + tag + "\"")
                : x.Value.Status == HttpStatusCode.NotFound && x.Value.Body.Contains("not_found_error")) && seen.Values.All(x => x.Type.StartsWith("application/json"));
        SelfTests.Check("gateway health answers without a key or a load", (await client.GetAsync("/health")).IsSuccessStatusCode && host.UpCalls == 0);
        using (var r = await client.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{}", auth: false)))
            SelfTests.Check("gateway rejects a missing API key and loads nothing", r.StatusCode == HttpStatusCode.Unauthorized && host.UpCalls == 0);
        using (var r = await client.SendAsync(Req(HttpMethod.Get, "/v1/models")))
            SelfTests.Check("model listing while asleep never wakes the GPU", r.IsSuccessStatusCode && (await r.Content.ReadAsStringAsync()).Contains(Policy.Model) && host.UpCalls == 0);
        {
            var never = await Discover(client);
            SelfTests.Check("discovery paths never answered before get llama-server's 404 and load nothing",
                never.Values.All(x => x.Status == HttpStatusCode.NotFound && x.Body.Contains("not_found_error") && x.Type.StartsWith("application/json")) && host.UpCalls == 0 && !supervisor.IsReady);
        }
        using (var r = await client.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{\"messages\":[]}")))
        {
            var text = await r.Content.ReadAsStringAsync();
            SelfTests.Check("first request loads the model on demand and is answered", r.IsSuccessStatusCode && text.Contains("echo:") && host.UpCalls == 1 && supervisor.IsReady);
        }
        SelfTests.Check("gateway authenticates to the private backend with the configured key", backendAuth == "Bearer " + key);
        SelfTests.Check("a load remembers the engine's discovery answers", EngineAnswers(await Discover(client), "first") && File.Exists(Path.Combine(dir, "discovery-cache.json")) && host.UpCalls == 1);
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
        SelfTests.Check("asleep, every discovery path gets the engine's remembered answer and starts no engine", EngineAnswers(await Discover(client), "first") && host.UpCalls == 1 && !supervisor.IsReady);
        {
            int restartPort = FreePort();
            await using var restarted = new Gateway(supervisor, key, restartPort, backendPort, dir, null, () => effort);
            await restarted.StartAsync();
            using var restartClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{restartPort}"), Timeout = TimeSpan.FromSeconds(20) };
            SelfTests.Check("remembered discovery answers survive a tray restart", EngineAnswers(await Discover(restartClient), "first") && host.UpCalls == 1);
        }
        // A damaged or hand-edited answers file: unreadable JSON, missing fields or an impossible status.
        foreach (var damaged in new[] { "{not json", "{\"default\":{\"/props\":{\"Status\":200}}}", "{\"default\":{\"/props\":{\"Status\":7,\"ContentType\":\"x\",\"Body\":\"y\"}}}", "{\"default\":null}" })
        {
            var damagedDir = Path.Combine(Path.GetTempPath(), "localqwen-gw-" + Guid.NewGuid());
            Directory.CreateDirectory(damagedDir);
            File.WriteAllText(Path.Combine(damagedDir, "discovery-cache.json"), damaged);
            int damagedPort = FreePort();
            await using (var damagedGateway = new Gateway(supervisor, key, damagedPort, backendPort, damagedDir, null, () => effort))
            {
                await damagedGateway.StartAsync();
                using var damagedClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{damagedPort}"), Timeout = TimeSpan.FromSeconds(20) };
                var seen = await Discover(damagedClient);
                SelfTests.Check($"a damaged answers file ({damaged}) gives llama-server's 404 and loads nothing",
                    seen.Values.All(x => x.Status == HttpStatusCode.NotFound && x.Body.Contains("not_found_error")) && host.UpCalls == 1 && !supervisor.IsReady);
            }
            try { Directory.Delete(damagedDir, true); } catch (IOException) { }
        }
        propsTag = "second";
        int before = host.UpCalls;
        var parallel = Enumerable.Range(0, 3).Select(_ => client.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{}"))).ToArray();
        var answers = await Task.WhenAll(parallel);
        SelfTests.Check("concurrent first requests share one load", answers.All(a => a.IsSuccessStatusCode) && host.UpCalls == before + 1);
        SelfTests.Check("a generation request after discovery probes still starts the engine", before == 1 && supervisor.IsReady);
        SelfTests.Check("remembered discovery answers refresh when the engine loads again", EngineAnswers(await Discover(client), "second"));
        foreach (var a in answers) a.Dispose();
        supervisor.IdleTimeout = null;
        {
            // While a load is under way, probes are answered at once and join no load.
            await supervisor.Unload(default);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.DelayHook = async ct => { await gate.Task.WaitAsync(ct); host.Health = new(true); };
            int loadsBefore = host.UpCalls;
            var loading = client.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{}"));
            for (int i = 0; i < 200 && host.UpCalls == loadsBefore; i++) await Task.Delay(10);
            var during = await Discover(client);
            SelfTests.Check("while a load is under way, discovery is answered from memory and starts no second engine",
                EngineAnswers(during, "second") && host.UpCalls == loadsBefore + 1 && !loading.IsCompleted && !supervisor.IsReady);
            gate.SetResult();
            using var loaded = await loading;
            host.DelayHook = null;
            SelfTests.Check("the generation request waiting on that load is answered", loaded.IsSuccessStatusCode && supervisor.IsReady && host.UpCalls == loadsBefore + 1);
        }
        await supervisor.Unload(default);
        host.Free = 1000;
        using (var r = await client.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{}")))
        {
            var text = await r.Content.ReadAsStringAsync();
            SelfTests.Check("a load that cannot fit returns a clear 503 instead of hanging", r.StatusCode == HttpStatusCode.ServiceUnavailable && text.Contains("VRAM"));
            SelfTests.Check("a VRAM shortage has its own error type and the VRAM it needs, so clients can wait and retry",
                text.Contains("\"type\":\"insufficient_vram\"") && text.Contains($"\"need_mib\":{Policy.Need(Policy.Ctx64, 0) + Policy.HeadroomMiB}"));
        }
        var profileDir = Path.Combine(Path.GetTempPath(), "localqwen-gw-" + Guid.NewGuid());
        var profileHost = new TestHost();
        var profiles = new AppConfig();
        var profileSupervisor = new Supervisor(new Controller(profileHost), host: profileHost);
        int profilePort = FreePort();
        await using var profileGateway = new Gateway(profileSupervisor, key, profilePort, backendPort, profileDir, reasoning: () => "low", config: profiles);
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
            propsTag = "extraction";
            propsCalls = 0;
            var pending = profileClient.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", json));
            await Task.Delay(50);
            SelfTests.Check("profile switch does not stop an in-flight SSE response", !pending.IsCompleted && profileHost.StopCalls == 0);
            var completed = await streaming.Content.ReadAsStringAsync();
            using var result = await pending;
            SelfTests.Check("switch drains SSE then loads extraction once", completed.Contains("[DONE]") && result.IsSuccessStatusCode && profileHost.StopCalls == 1 && profileHost.UpCalls == 2 && profileHost.SelectedProfile == "savannah-extraction");
            SelfTests.Check("thinking controls and max_tokens pass through byte-for-byte", backendBody == json);
        }
        SelfTests.Check("discovery while extraction is loaded neither switches profiles nor answers with extraction's engine",
            EngineAnswers(await Discover(profileClient), "second") && profileHost.StopCalls == 1 && profileHost.UpCalls == 2 && profileHost.SelectedProfile == "savannah-extraction");
        SelfTests.Check("Savannah's extraction requests send the engine no discovery requests", propsCalls == 0 && !File.ReadAllText(Path.Combine(profileDir, "discovery-cache.json")).Contains("savannah-extraction"));
        const string retry = "{\"model\":\"savannah-extraction\",\"chat_template_kwargs\":{\"enable_thinking\":false},\"max_tokens\":256}";
        using (var result = await profileClient.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", retry)))
            SelfTests.Check("thinking-off retry stays on the same exact profile", result.IsSuccessStatusCode && backendBody == retry && profileHost.UpCalls == 2);
        using (var status = await profileClient.SendAsync(Req(HttpMethod.Get, "/v1/status")))
            SelfTests.Check("status identifies the loaded extraction profile", (await status.Content.ReadAsStringAsync()).Contains("\"loaded_profile\":\"savannah-extraction\""));
        using (var listing = await profileClient.SendAsync(Req(HttpMethod.Get, "/v1/models")))
            SelfTests.Check("listing while extraction is loaded does not switch profiles", listing.IsSuccessStatusCode && profileHost.StopCalls == 1);
        using (var result = await profileClient.SendAsync(Req(HttpMethod.Post, "/v1/responses", "{\"model\":\"codex-auto-review\",\"max_tokens\":30}")))
            SelfTests.Check("legacy model names switch back to the default and retain tray defaults", result.IsSuccessStatusCode && profileHost.SelectedProfile is null && profileHost.StopCalls == 2 && backendBody!.Contains("\"reasoning_effort\":\"low\"") && backendBody.Contains("\"max_tokens\":30"));
        crashBackend = () => profileHost.State = new(true, false, 7);
        using (var result = await profileClient.SendAsync(Req(HttpMethod.Post, "/v1/chat/completions", "{\"abort\":true}"), HttpCompletionOption.ResponseHeadersRead))
        {
            try { await result.Content.ReadAsStringAsync(); }
            catch (HttpRequestException) { /* a truncated SSE stream must reach the client as a failed response */ }
            for (int i = 0; i < 100 && profileSupervisor.State != TrayState.Error; i++) await Task.Delay(10);
            SelfTests.Check("mid-stream backend crash refreshes status immediately", profileSupervisor.State == TrayState.Error);
        }
        await backend.StopAsync();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        try { Directory.Delete(profileDir, true); } catch (IOException) { }
    }
}
