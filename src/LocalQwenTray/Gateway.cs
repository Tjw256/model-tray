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
// /v1/embeddings goes to the separate embedding engine (embeddings starts it and returns its port); it never wakes the GPU.
internal sealed class Gateway(Supervisor supervisor, string key, int publicPort, int backendPort, string stateDirectory, Action<string>? log = null, Func<string>? reasoning = null, AppConfig? config = null, NativeHost? native = null, Func<CancellationToken, Task<int>>? embeddings = null) : IAsyncDisposable
{
    WebApplication? app;
    readonly HttpClient backend = new(new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.None, PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30) }) { Timeout = Timeout.InfiniteTimeSpan };
    static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase) { "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Connection", "TE", "Trailer", "Host", "Authorization", "Content-Length" };
    string ModelsCachePath => Path.Combine(stateDirectory, "models-cache.json");

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
            await Json(ctx, 200, System.Text.Json.JsonSerializer.Serialize(new { profiles = config?.Profiles.Select(p => new { name = p.Key, context_tokens = p.Value.ContextTokens, slots = p.Value.Slots, background = p.Value.Background, fingerprint_url = "/v1/profiles/" + p.Key + "/fingerprint" }) }));
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
            if (embeddings is not null) models.Add(new { id = Policy.EmbeddingModel, @object = "model", owned_by = "llamacpp", meta = new { n_ctx = 2048 } });
            await Json(ctx, 200, System.Text.Json.JsonSerializer.Serialize(new { @object = "list", data = models }));
            return;
        }
        if (modelList && !supervisor.IsReady) { await Json(ctx, 200, CachedModels()); return; } // listing never wakes the GPU
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        byte[]? requestBody = null;
        string? profile = null, requested = null;
        bool background = false;
        if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
        {
            using var buffer = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
            requestBody = buffer.ToArray();
            try
            {
                if (JsonNode.Parse(requestBody) is JsonObject json && json["model"] is JsonValue model && model.TryGetValue<string>(out var name))
                {
                    requested = name;
                    if (config?.Profiles.TryGetValue(name, out var named) == true) (profile, background) = (name, named.Background);
                }
            }
            catch (System.Text.Json.JsonException) { /* the backend reports invalid input as before */ }
        }
        if (embeddings is not null && HttpMethods.IsPost(ctx.Request.Method) && path is "/v1/embeddings" or "/embeddings")
        {
            await Embed(ctx, path, requestBody, requested, started, embeddings);
            return;
        }
        IDisposable lease;
        try { lease = await supervisor.AdmitRequest(profile, ctx.RequestAborted, background); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { return; }
        catch (Exception ex) { log?.Invoke("On-demand load failed: " + ex.Message); await Error(ctx, 503, "Local Qwen could not load: " + ex.Message, "server_error"); return; }
        var admitted = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        string outcome = "client gone", timings = "";
        bool retried = false;
        using (lease)
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                try { (outcome, timings) = await Forward(ctx, path, modelList, requestBody, profile is null, backendPort); break; }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    // Backend vanished (crash or external stop): refresh state so the next request reloads.
                    log?.Invoke("Backend request failed: " + ex.Message);
                    await supervisor.Controller.Monitor(CancellationToken.None);
                    if (ctx.Response.HasStarted) { ctx.Abort(); outcome = "aborted mid-reply"; break; }
                    // Nothing reached the client and the engine is gone: reload the same profile and
                    // retry once. A second failure (say a request that crashes the engine) is returned.
                    if (attempt == 1 && !supervisor.IsReady)
                    {
                        retried = true;
                        ctx.Response.Clear();
                        log?.Invoke("Engine died before replying; reloading and retrying the request once");
                        try { await supervisor.EnsureReady(ctx.RequestAborted); continue; }
                        catch (Exception reload) when (reload is not OperationCanceledException)
                        {
                            outcome = "503";
                            await Error(ctx, 503, "Local Qwen could not reload after the engine stopped: " + reload.Message, "server_error");
                            break;
                        }
                    }
                    outcome = "502";
                    await Error(ctx, 502, "Local Qwen backend is not reachable; retry to reload it", "server_error");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { /* client went away; upstream request is cancelled too */ }
        finally
        {
            var total = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            log?.Invoke(FormattableString.Invariant($"Request {ctx.Request.Method} {path} model {Token(requested)} profile {profile ?? "default"} -> {outcome}{(retried ? " after one engine reload" : "")}, waited {admitted.TotalSeconds:0.0} s, total {total.TotalSeconds:0.0} s{timings}, client {Token(ctx.Request.Headers.UserAgent.ToString())}"));
        }
    }

    // Embeddings bypass admission: the engine is separate, small and CPU-only, so it never waits for or switches the
    // chat model. A request that finds the engine dead before the first byte restarts it and is retried once.
    async Task Embed(HttpContext ctx, string path, byte[]? requestBody, string? requested, long started, Func<CancellationToken, Task<int>> engine)
    {
        string outcome = "client gone";
        bool retried = false;
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                int port;
                try { port = await engine(ctx.RequestAborted); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log?.Invoke("Embedding engine could not start: " + ex.Message);
                    outcome = "503";
                    await Error(ctx, 503, "Local Qwen embedding engine could not start: " + ex.Message, "server_error");
                    break;
                }
                try { (outcome, _) = await Forward(ctx, path, false, requestBody, false, port); break; }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    log?.Invoke("Embedding request failed: " + ex.Message);
                    if (ctx.Response.HasStarted) { ctx.Abort(); outcome = "aborted mid-reply"; break; }
                    if (attempt == 1) { retried = true; ctx.Response.Clear(); continue; }
                    outcome = "502";
                    await Error(ctx, 502, "Local Qwen embedding engine is not reachable", "server_error");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { /* client went away */ }
        finally
        {
            var total = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            log?.Invoke(FormattableString.Invariant($"Request {ctx.Request.Method} {path} model {Token(requested)} profile embeddings -> {outcome}{(retried ? " after one engine restart" : "")}, total {total.TotalSeconds:0.0} s, client {Token(ctx.Request.Headers.UserAgent.ToString())}"));
        }
    }

    // Client-supplied names go into the log as one short token without spaces or control characters.
    static string Token(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var token = new string(value.Trim().Split(' ')[0].Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/' or ':').Take(48).ToArray());
        return token.Length == 0 ? "-" : token;
    }

    bool Authorized(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header[7..].Trim() == key;
        return request.Headers.TryGetValue("x-api-key", out var alt) && alt.ToString() == key;
    }

    // Returns the status sent to the client and llama-server's timing summary for the request log.
    async Task<(string Status, string Timings)> Forward(HttpContext ctx, string path, bool cacheModels, byte[]? requestBody, bool applyDefaults, int port)
    {
        var target = $"http://127.0.0.1:{port}{path}{ctx.Request.QueryString}";
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
        return (((int)response.StatusCode).ToString(), response.IsSuccessStatusCode ? RecordTimings(tail.Text()) : "");
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

    // llama-server reports timings in the final JSON (or final SSE chunk); surface the last reply's speed in the UI
    // and summarize prompt, prompt-cache reuse and output for the request log.
    string RecordTimings(string text)
    {
        string? Last(string field) => Regex.Matches(text, "\"" + field + "\"\\s*:\\s*([0-9.]+)") is { Count: > 0 } m ? m[^1].Groups[1].Value : null;
        if (!int.TryParse(Last("predicted_n"), out var tokens)) return "";
        double.TryParse(Last("predicted_per_second"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate);
        if (tokens > 0 && rate > 0) supervisor.RecordReply(tokens, rate);
        // prompt_n counts only the tokens processed for this request; cache_n the ones reused from the slot.
        return FormattableString.Invariant($", prompt {Last("prompt_n") ?? "?"} new + {Last("cache_n") ?? "?"} cached, output {tokens} at {rate:0.0} tok/s");
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
