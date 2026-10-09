using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace LocalQwenTray;
// `LocalQwenTray.exe --mcp`: a Model Context Protocol server on stdin/stdout (newline-delimited JSON-RPC 2.0), so any
// MCP-capable agent (Codex, Claude Code, Hermes, OpenCode, ...) can delegate work to Local Qwen as a tool. It talks to
// the tray's gateway, so the model loads on demand, the tray's reasoning setting applies and requests use the free slot.
internal sealed class McpServer(string key, string endpoint, HttpClient http)
{
    const string Protocol = "2025-06-18";
    static readonly string[] Efforts = ["off", "low", "medium", "xhigh"];
    static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    { [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".webp"] = "image/webp", [".gif"] = "image/gif", [".bmp"] = "image/bmp" };
    public const long MaxImageBytes = 20 * 1024 * 1024;

    public async Task Run(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        string? line;
        while ((line = await input.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var reply = await Handle(line, ct);
            if (reply is null) continue;   // notifications get no reply
            await output.WriteLineAsync(reply.ToJsonString());
            await output.FlushAsync(ct);
        }
    }

    internal async Task<JsonObject?> Handle(string line, CancellationToken ct)
    {
        JsonObject? request;
        line = line.TrimStart('﻿');   // some shells (e.g. Windows PowerShell 5.1) prefix piped text with a BOM
        try { request = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return Error(null, -32700, "Parse error"); }
        if (request is null) return Error(null, -32600, "Invalid request");
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();
        if (id is null) return null;   // notification (e.g. notifications/initialized)
        try
        {
            return method switch
            {
                "initialize" => Result(id, new JsonObject
                {
                    ["protocolVersion"] = request["params"]?["protocolVersion"]?.GetValue<string>() ?? Protocol,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "local-qwen", ["version"] = "1.0" },
                    ["instructions"] = "Local Qwen3.8 27B running on this PC (private, no API cost, ~100-170 tok/s; loads on first use, which can take ~25 s). " +
                                       "Use it for delegated work: drafts, summaries, rewrites, simple code, reviews and image questions.",
                }),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = Tools() }),
                "tools/call" => Result(id, await Call(request["params"] as JsonObject ?? new JsonObject(), ct)),
                _ => Error(id, -32601, "Method not found: " + method),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Result(id, ToolText("Local Qwen error: " + Secrets.Redact(ex.Message, key), isError: true));
        }
    }

    static JsonArray Tools()
    {
        JsonObject Effort() => new() { ["type"] = "string", ["enum"] = new JsonArray(Efforts.Select(e => (JsonNode?)e).ToArray()),
            ["description"] = "Thinking effort: off (fastest, no thinking), low, medium, xhigh (most thorough). Default: the tray's setting." };
        return new JsonArray(
            new JsonObject
            {
                ["name"] = "ask_local_qwen",
                ["description"] = "Ask the local Qwen3.8 27B model and get its answer as text. Good for drafting, summarising, rewriting, translating, simple code and second opinions. Runs privately on this PC.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "The task or question, with all context Qwen needs (it cannot see your files)." },
                        ["system"] = new JsonObject { ["type"] = "string", ["description"] = "Optional system instructions (role, style, output format)." },
                        ["reasoning"] = Effort(),
                        ["max_tokens"] = new JsonObject { ["type"] = "integer", ["minimum"] = 16, ["maximum"] = 32768, ["description"] = "Answer length limit (default 4096)." },
                    },
                    ["required"] = new JsonArray("prompt"),
                },
            },
            new JsonObject
            {
                ["name"] = "ask_local_qwen_about_image",
                ["description"] = "Show an image file (png, jpg, webp, gif, bmp) to the local Qwen3.8 27B vision model and ask about it.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["image_path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path of the image file on this PC." },
                        ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "What to do with the image." },
                        ["reasoning"] = Effort(),
                        ["max_tokens"] = new JsonObject { ["type"] = "integer", ["minimum"] = 16, ["maximum"] = 32768 },
                    },
                    ["required"] = new JsonArray("image_path", "prompt"),
                },
            },
            new JsonObject
            {
                ["name"] = "local_qwen_status",
                ["description"] = "Check whether Local Qwen is reachable and which model and context it offers.",
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            });
    }

    async Task<JsonObject> Call(JsonObject p, CancellationToken ct)
    {
        var name = p["name"]?.GetValue<string>();
        var args = p["arguments"] as JsonObject ?? new JsonObject();
        switch (name)
        {
            case "local_qwen_status":
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, endpoint + "/models");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var res = await http.SendAsync(req, ct);
                if (!res.IsSuccessStatusCode) return ToolText($"Local Qwen tray answered HTTP {(int)res.StatusCode}.", isError: true);
                var model = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["data"]?[0];
                return ToolText($"Local Qwen is reachable at {endpoint}. Model: {model?["id"]}, context: {model?["meta"]?["n_ctx"]} tokens. It loads on the first request if it is asleep.");
            }
            case "ask_local_qwen":
            {
                var prompt = args["prompt"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(prompt)) return ToolText("Missing required argument: prompt", isError: true);
                var messages = new JsonArray();
                if (args["system"]?.GetValue<string>() is { Length: > 0 } system) messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });
                return await Chat(messages, args, ct);
            }
            case "ask_local_qwen_about_image":
            {
                var path = args["image_path"]?.GetValue<string>();
                var prompt = args["prompt"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(prompt)) return ToolText("Missing required arguments: image_path and prompt", isError: true);
                if (!File.Exists(path)) return ToolText("Image not found: " + path, isError: true);
                if (!ImageTypes.TryGetValue(Path.GetExtension(path), out var mime)) return ToolText("Unsupported image type (use png, jpg, webp, gif or bmp): " + path, isError: true);
                if (new FileInfo(path).Length > MaxImageBytes) return ToolText("Image is larger than 20 MB: " + path, isError: true);
                var data = Convert.ToBase64String(await File.ReadAllBytesAsync(path, ct));
                var messages = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = prompt },
                        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{mime};base64,{data}" } }),
                });
                return await Chat(messages, args, ct);
            }
            default:
                return ToolText("Unknown tool: " + name, isError: true);
        }
    }

    async Task<JsonObject> Chat(JsonArray messages, JsonObject args, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = Policy.Model, ["messages"] = messages, ["max_tokens"] = args["max_tokens"]?.GetValue<int>() ?? 4096 };
        var effort = args["reasoning"]?.GetValue<string>();
        if (effort == "off") body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
        else if (effort is "low" or "medium" or "xhigh") body["chat_template_kwargs"] = new JsonObject { ["reasoning_effort"] = effort };
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint + "/chat/completions") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var message = (JsonNode.Parse(text) as JsonObject)?["error"]?["message"]?.GetValue<string>() ?? text;
            return ToolText($"Local Qwen could not answer (HTTP {(int)res.StatusCode}): {message}", isError: true);
        }
        var choice = JsonNode.Parse(text)?["choices"]?[0];
        var answer = choice?["message"]?["content"]?.GetValue<string>() ?? "";
        if (choice?["finish_reason"]?.GetValue<string>() == "length")
            answer += "\n\n[Answer cut off at max_tokens; ask again with a higher max_tokens or reasoning \"off\" for a shorter path.]";
        return ToolText(answer.Length > 0 ? answer : "[Qwen returned no answer text; it may have used all max_tokens on thinking. Retry with a higher max_tokens or reasoning \"low\".]");
    }

    static JsonObject ToolText(string text, bool isError = false) =>
        new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = isError };
    static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    static JsonObject Error(JsonNode? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
