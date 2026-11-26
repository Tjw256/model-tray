using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
namespace LocalQwenTray;
// Loopback OpenAI-compatible front door. Clients keep using http://127.0.0.1:8000/v1 with the same key;
// the request is held while the model loads on demand, then streamed through to the private llama-server.
// For chat requests it also applies the tray's thinking defaults (see ApplyThinkingDefaults).
internal sealed class Gateway(Supervisor supervisor, string key, int publicPort, int backendPort, string stateDirectory, Action<string>? log = null, Func<string>? reasoning = null, AppConfig? config = null, NativeHost? native = null) : IAsyncDisposable
{
    WebApplication? app;
    readonly HttpClient backend = new(new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.None, PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30) }) { Timeout = Timeout.InfiniteTimeSpan };
    static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase) { "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Connection", "TE", "Trailer", "Host", "Authorization", "Content-Length" };
    string ModelsCachePath => Path.Combine(stateDirectory, "models-cache.json");
    readonly DiscoveryAnswers discovery = new(Path.Combine(stateDirectory, "discovery-cache.json"));
    int discoveryLoad = -1;
    const string DefaultProfile = "default";

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, publicPort);
            o.Limits.MaxRequestBodySize = null;          // base64 images can be large
            o.Limits.MinRequestBodyDataRate = null;
            o.Limits.MinResponseDataRate = null;         // long prefills send nothing for a while
            o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
        });
        app = builder.Build();
        app.Run(Handle);
        await app.StartAsync();
    }

    async Task Handle(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        if (path is "/health" or "/v1/health") { await Json(ctx, 200, "{\"status\":\"ok\"}"); return; }
        if (!Authorized(ctx.Request)) { await Error(ctx, 401, "Invalid API Key", "authentication_error"); return; }
        if (HttpMethods.IsGet(ctx.Request.Method) && path == "/v1/status")
        {
            GpuMemory? memory = null;
            try { if (native is not null) memory = await native.FreeVram(ctx.RequestAborted); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log?.Invoke("VRAM poll failed: " + ex.Message); }
            await Json(ctx, 200, System.Text.Json.JsonSerializer.Serialize(new { loaded_profile = supervisor.LoadedProfile, state = supervisor.IsUnloading ? "unloading" : supervisor.State.ToString().ToLowerInvariant(), busy = supervisor.InFlight > 0, in_flight = supervisor.InFlight, free_vram_mib = memory?.FreeMiB }));
            return;
        }
        if (HttpMethods.IsGet(ctx.Request.Method) && path == "/v1/profiles")
        {
            await Json(ctx, 200, System.Text.Json.JsonSerializer.Serialize(new { profiles = config?.Profiles.Select(p => new { name = p.Key, context_tokens = p.Value.ContextTokens, slots = p.Value.Slots, fingerprint_url = "/v1/profiles/" + p.Key + "/fingerprint" }) }));
            return;
        }
        if (HttpMethods.IsGet(ctx.Request.Method) && path.StartsWith("/v1/profiles/") && path.EndsWith("/fingerprint"))
        {
            string name = path["/v1/profiles/".Length..^"/fingerprint".Length];
            if (native is null || config?.Profiles.ContainsKey(name) != true) { await Error(ctx, 404, "Unknown profile", "invalid_request_error"); return; }
            try { await Json(ctx, 200, System.Text.Json.JsonSerializer.Serialize(await native.Fingerprint(name, ctx.RequestAborted))); }
            catch (Exception ex) when (ex is not OperationCanceledException) { await Error(ctx, 503, "Profile fingerprint unavailable: " + ex.Message, "server_error"); }
            return;
        }
        bool modelList = HttpMethods.IsGet(ctx.Request.Method) && path is "/v1/models" or "/models";
        if (modelList && config is not null)
        {
            var models = new List<object> { new { id = Policy.Model, @object = "model", owned_by = "llamacpp", meta = new { n_ctx = native?.CodingContext ?? Policy.Ctx128 } } };
            models.AddRange(config.Profiles.Select(p => (object)new { id = p.Key, @object = "model", owned_by = "llamacpp", meta = new { n_ctx = p.Value.ContextTokens } }));
            await Json(ctx, 200, System.Text.Json.JsonSerializer.Serialize(new { @object = "list", data = models }));
            return;
        }
        if (modelList && !supervisor.IsReady) { await Json(ctx, 200, CachedModels()); return; } // listing never wakes the GPU
        // Discovery probes never wake the GPU or switch profiles. A GET names no model, so it is answered for the default
        // profile, which is what such a GET used to load.
        if (HttpMethods.IsGet(ctx.Request.Method) && DiscoveryAnswers.Paths.Contains(path))
        {
            var answer = discovery.Get(DefaultProfile, path);
            ctx.Response.StatusCode = answer.Status;
            ctx.Response.ContentType = answer.ContentType;
            await ctx.Response.WriteAsync(answer.Body);
            return;
        }
        byte[]? requestBody = null;
        string? profile = null;
        if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
        {
            using var buffer = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
            requestBody = buffer.ToArray();
            try
            {
                if (JsonNode.Parse(requestBody) is JsonObject json && json["model"] is JsonValue model && model.TryGetValue<string>(out var name) && config?.Profiles.ContainsKey(name) == true) profile = name;
            }
            catch (System.Text.Json.JsonException) { /* the backend reports invalid input as before */ }
        }
        IDisposable lease;
        try { lease = await supervisor.AdmitRequest(profile, ctx.RequestAborted); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { return; }
        // A VRAM shortage has its own error type and says how much free VRAM the load needs: the request can go again
        // once that much is free, unlike after any other failure.
        catch (InsufficientVramException ex)
        {
            log?.Invoke("On-demand load failed: " + ex.Message);
            await Json(ctx, 503, System.Text.Json.JsonSerializer.Serialize(new { error = new { code = 503, message = "Local Qwen could not load: " + ex.Message, type = "insufficient_vram", need_mib = ex.NeedMiB } }));
            return;
        }
        catch (Exception ex) { log?.Invoke("On-demand load failed: " + ex.Message); await Error(ctx, 503, "Local Qwen could not load: " + ex.Message, "server_error"); return; }
        using (lease)
        try
        {
            // Only the default profile's answers are ever served, so exact-profile requests (Savannah's) skip the capture.
            if (profile is null) await RememberDiscovery(DefaultProfile);
            await Forward(ctx, path, modelList, requestBody, profile is null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Backend vanished (crash or external stop): refresh state so the next request reloads.
            log?.Invoke("Backend request failed: " + ex.Message);
            await supervisor.Controller.Monitor(CancellationToken.None);
            if (ctx.Response.HasStarted) ctx.Abort();
            else await Error(ctx, 502, "Local Qwen backend is not reachable; retry to reload it", "server_error");
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { /* client went away; upstream request is cancelled too */ }
    }

    bool Authorized(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header[7..].Trim() == key;
        return request.Headers.TryGetValue("x-api-key", out var alt) && alt.ToString() == key;
    }

    async Task Forward(HttpContext ctx, string path, bool cacheModels, byte[]? requestBody, bool applyDefaults)
    {
        var target = $"http://127.0.0.1:{backendPort}{path}{ctx.Request.QueryString}";
        using var upstream = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);
        bool chat = HttpMethods.IsPost(ctx.Request.Method) && path is "/v1/chat/completions" or "/chat/completions" or "/v1/responses";
        if (requestBody is not null)
        {
            upstream.Content = new ByteArrayContent(applyDefaults && chat && reasoning is not null ? ApplyThinkingDefaults(requestBody, reasoning()) : requestBody);
        }
        foreach (var (name, values) in ctx.Request.Headers)
        {
            if (HopHeaders.Contains(name) || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)) continue;
            if (!upstream.Headers.TryAddWithoutValidation(name, values.ToArray()) && upstream.Content is not null)
                upstream.Content.Headers.TryAddWithoutValidation(name, values.ToArray());
        }
        upstream.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await backend.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        ctx.Response.StatusCode = (int)response.StatusCode;
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            if (!HopHeaders.Contains(name)) ctx.Response.Headers[name] = values.ToArray();
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await using var body = await response.Content.ReadAsStreamAsync(ctx.RequestAborted);
        var buffer = new byte[16 * 1024];
        var tail = new TailBuffer(16 * 1024);
        var whole = cacheModels ? new MemoryStream() : null;
        int n;
        while ((n = await body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
        {
            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, n), ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted); // keep SSE token streaming live
            tail.Add(buffer, n);
            whole?.Write(buffer, 0, n);
        }
        if (whole is not null && response.IsSuccessStatusCode) TryWrite(ModelsCachePath, whole.ToArray());
        if (response.IsSuccessStatusCode) RecordTimings(tail.Text());
    }

    // Once per load, while this request's lease holds the profile in place, ask the engine the discovery paths and keep
    // its answers for that profile. A path the engine could not answer (timeout, dropped connection) keeps its previous
    // answer, and the next request on the same load asks again, so one slow reply can't leave /props stale for a whole load.
    async Task RememberDiscovery(string profile)
    {
        int load = supervisor.Loads;
        if (Interlocked.Exchange(ref discoveryLoad, load) == load) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var fresh = new Dictionary<string, DiscoveryAnswer>();
        bool unanswered = false;
        foreach (var path in DiscoveryAnswers.Paths)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{backendPort}{path}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await backend.SendAsync(request, deadline.Token);
                // 404 is the engine's real answer for a path it doesn't have; other errors are transient.
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                    fresh[path] = new((int)response.StatusCode, response.Content.Headers.ContentType?.ToString() ?? DiscoveryAnswers.NotFound.ContentType, await response.Content.ReadAsStringAsync(deadline.Token));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { unanswered = true; log?.Invoke($"Discovery answer for {path} not refreshed: {ex.Message}"); }
        }
        if (fresh.Count > 0) discovery.Remember(profile, fresh);
        if (unanswered) Interlocked.CompareExchange(ref discoveryLoad, -1, load);
    }

    // Thinking text is the slow part of Qwen3.8 output, so the tray sets the thinking effort (low/medium/xhigh) and
    // drops earlier turns' thinking from the history (as Qwen's official template does). Values the client sets
    // itself (chat_template_kwargs or a top-level reasoning_effort) are never overridden.
    internal static byte[] ApplyThinkingDefaults(byte[] body, string effort)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); } catch (System.Text.Json.JsonException) { return body; }
        if (root is not JsonObject request) return body;
        var kwargs = request["chat_template_kwargs"] as JsonObject;
        if (kwargs is null) { if (request.ContainsKey("chat_template_kwargs")) return body; request["chat_template_kwargs"] = kwargs = new JsonObject(); }
        if (!kwargs.ContainsKey("reasoning_effort") && !request.ContainsKey("reasoning_effort")) kwargs["reasoning_effort"] = effort;
        if (!kwargs.ContainsKey("preserve_thinking")) kwargs["preserve_thinking"] = false;
        return Encoding.UTF8.GetBytes(request.ToJsonString());
    }

    // llama-server reports timings in the final JSON (or final SSE chunk); surface the last reply's speed in the UI.
    void RecordTimings(string text)
    {
        var tps = Regex.Matches(text, "\"predicted_per_second\"\\s*:\\s*([0-9.]+)");
        var count = Regex.Matches(text, "\"predicted_n\"\\s*:\\s*([0-9]+)");
        if (tps.Count > 0 && count.Count > 0 &&
            double.TryParse(tps[^1].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate) &&
            int.TryParse(count[^1].Groups[1].Value, out var tokens) && tokens > 0)
            supervisor.RecordReply(tokens, rate);
    }

    string CachedModels()
    {
        try { if (File.Exists(ModelsCachePath)) return File.ReadAllText(ModelsCachePath); } catch (IOException) { }
        // First run before any load: minimal llama-server-shaped listing.
        return "{\"object\":\"list\",\"data\":[{\"id\":\"" + Policy.Model + "\",\"object\":\"model\",\"owned_by\":\"llamacpp\",\"aliases\":[\"" + Policy.Model + "\"],\"meta\":{\"n_ctx\":" + Policy.ContextCap + "}}]}";
    }

    static void TryWrite(string path, byte[] data)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var tmp = path + ".tmp"; File.WriteAllBytes(tmp, data); File.Move(tmp, path, true); }
        catch (IOException) { }
    }
    static Task Json(HttpContext ctx, int status, string json)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(json);
    }
    static Task Error(HttpContext ctx, int status, string message, string type) =>
        Json(ctx, status, System.Text.Json.JsonSerializer.Serialize(new { error = new { code = status, message, type } }));

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await app.StopAsync(deadline.Token); } catch (Exception) { /* shutting down regardless */ }
            await app.DisposeAsync();
        }
        backend.Dispose();
    }

    sealed class TailBuffer(int size)
    {
        readonly byte[] data = new byte[size];
        int length;
        public void Add(byte[] src, int n)
        {
            if (n >= size) { Array.Copy(src, n - size, data, 0, size); length = size; return; }
            int keep = Math.Min(length, size - n);
            Array.Copy(data, length - keep, data, 0, keep);
            Array.Copy(src, 0, data, keep, n);
            length = keep + n;
        }
        public string Text() => Encoding.UTF8.GetString(data, 0, length);
    }
}
