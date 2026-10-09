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
internal sealed class Gateway(Supervisor supervisor, string key, int publicPort, int backendPort, string stateDirectory, Action<string>? log = null, Func<string>? reasoning = null) : IAsyncDisposable
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
        bool modelList = HttpMethods.IsGet(ctx.Request.Method) && path is "/v1/models" or "/models";
        if (modelList && !supervisor.IsReady) { await Json(ctx, 200, CachedModels()); return; } // listing never wakes the GPU
        using var lease = supervisor.BeginRequest();
        try { await supervisor.EnsureReady(ctx.RequestAborted); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { return; }
        catch (Exception ex) { log?.Invoke("On-demand load failed: " + ex.Message); await Error(ctx, 503, "Local Qwen could not load: " + ex.Message, "server_error"); return; }
        try { await Forward(ctx, path, modelList); }
        catch (HttpRequestException ex) when (!ctx.Response.HasStarted)
        {
            // Backend vanished (crash or external stop): refresh state so the next request reloads.
            log?.Invoke("Backend request failed: " + ex.Message);
            _ = supervisor.Controller.Monitor(CancellationToken.None);
            await Error(ctx, 502, "Local Qwen backend is not reachable; retry to reload it", "server_error");
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { /* client went away; upstream request is cancelled too */ }
    }

    bool Authorized(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header[7..].Trim() == key;
        return request.Headers.TryGetValue("x-api-key", out var alt) && alt.ToString() == key;
    }

    async Task Forward(HttpContext ctx, string path, bool cacheModels)
    {
        var target = $"http://127.0.0.1:{backendPort}{path}{ctx.Request.QueryString}";
        using var upstream = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);
        bool hasBody = ctx.Request.ContentLength > 0 || ctx.Request.Headers.TransferEncoding.Count > 0;
        bool chat = HttpMethods.IsPost(ctx.Request.Method) && path is "/v1/chat/completions" or "/chat/completions" or "/v1/responses";
        if (hasBody && chat && reasoning is not null)
        {
            using var requestBody = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(requestBody, ctx.RequestAborted);
            upstream.Content = new ByteArrayContent(ApplyThinkingDefaults(requestBody.ToArray(), reasoning()));
        }
        else if (hasBody)
        {
            upstream.Content = new StreamContent(ctx.Request.Body);
            if (ctx.Request.ContentLength is long length) upstream.Content.Headers.ContentLength = length;
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
