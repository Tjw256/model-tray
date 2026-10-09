using System.Net;
using System.Text.Json.Nodes;
namespace LocalQwenTray;
// The MCP server against a fake gateway: protocol handshake, tool list, request shape, errors, images. No GPU.
internal static class McpTests
{
    sealed class FakeGateway : HttpMessageHandler
    {
        public string? LastPath, LastBody, LastAuth;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Finish = "stop";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastPath = request.RequestUri!.AbsolutePath;
            LastAuth = request.Headers.Authorization?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            bool models = LastPath.EndsWith("/models");
            var json = models ? "{\"data\":[{\"id\":\"" + Policy.Model + "\",\"meta\":{\"n_ctx\":131072}}]}"
                : Status != HttpStatusCode.OK ? "{\"error\":{\"message\":\"Local Qwen could not load: Insufficient free VRAM\"}}"
                : "{\"choices\":[{\"message\":{\"content\":\"QWEN SAYS HI\"},\"finish_reason\":\"" + Finish + "\"}]}";
            return new HttpResponseMessage(models ? HttpStatusCode.OK : Status) { Content = new StringContent(json) };
        }
    }
    static string Text(JsonObject? reply) => reply?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
    static bool IsError(JsonObject? reply) => reply?["result"]?["isError"]?.GetValue<bool>() == true;
    static string Call(int id, string tool, string arguments) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}}";
    public static async Task Run()
    {
        var fake = new FakeGateway();
        using var http = new HttpClient(fake);
        var mcp = new McpServer("mcp-key", "http://127.0.0.1:8000/v1", http);
        Task<JsonObject?> Ask(string json) => mcp.Handle(json, default);

        var init = await Ask("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}");
        SelfTests.Check("MCP initialize advertises tools and echoes the protocol version",
            init?["result"]?["capabilities"]?["tools"] is not null && init?["result"]?["protocolVersion"]?.GetValue<string>() == "2025-06-18" && init?["result"]?["serverInfo"]?["name"]?.GetValue<string>() == "local-qwen");
        SelfTests.Check("MCP notifications get no reply", await Ask("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}") is null);

        var list = await Ask("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
        var names = list?["result"]?["tools"]?.AsArray().Select(t => t?["name"]?.GetValue<string>()).ToArray();
        SelfTests.Check("MCP lists ask_local_qwen, ask_local_qwen_about_image and local_qwen_status",
            names is not null && names.SequenceEqual(new[] { "ask_local_qwen", "ask_local_qwen_about_image", "local_qwen_status" }));

        var answer = await Ask(Call(3, "ask_local_qwen", "{\"prompt\":\"hi\",\"system\":\"be brief\",\"reasoning\":\"low\",\"max_tokens\":64}"));
        SelfTests.Check("ask_local_qwen returns Qwen's answer", Text(answer) == "QWEN SAYS HI" && !IsError(answer));
        SelfTests.Check("ask_local_qwen sends system + prompt, effort and limit to the gateway with the key",
            fake.LastPath == "/v1/chat/completions" && fake.LastAuth == "Bearer mcp-key" && fake.LastBody!.Contains("\"role\":\"system\"")
            && fake.LastBody.Contains("\"reasoning_effort\":\"low\"") && fake.LastBody.Contains("\"max_tokens\":64"));

        await Ask(Call(4, "ask_local_qwen", "{\"prompt\":\"hi\",\"reasoning\":\"off\"}"));
        SelfTests.Check("reasoning off disables thinking", fake.LastBody!.Contains("\"enable_thinking\":false"));

        fake.Finish = "length";
        SelfTests.Check("a cut-off answer says so", Text(await Ask(Call(5, "ask_local_qwen", "{\"prompt\":\"hi\"}"))).Contains("cut off"));
        fake.Finish = "stop";

        fake.Status = HttpStatusCode.ServiceUnavailable;
        var failed = await Ask(Call(6, "ask_local_qwen", "{\"prompt\":\"hi\"}"));
        SelfTests.Check("a gateway error becomes a readable tool error, not a crash", IsError(failed) && Text(failed).Contains("Insufficient free VRAM"));
        fake.Status = HttpStatusCode.OK;

        var png = Path.Combine(Path.GetTempPath(), "localqwen-mcp-" + Guid.NewGuid() + ".png");
        using (var bmp = TrayIcons.Render(32, null)) bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        var seen = await Ask(Call(7, "ask_local_qwen_about_image", "{\"image_path\":" + System.Text.Json.JsonSerializer.Serialize(png) + ",\"prompt\":\"what is this\"}"));
        SelfTests.Check("image tool sends the picture as a data URL with the question",
            Text(seen) == "QWEN SAYS HI" && fake.LastBody!.Contains("data:image/png;base64,") && fake.LastBody.Contains("what is this"));
        File.Delete(png);
        SelfTests.Check("missing image is reported", IsError(await Ask(Call(8, "ask_local_qwen_about_image", "{\"image_path\":\"C:/nope.png\",\"prompt\":\"x\"}"))));

        var status = await Ask(Call(9, "local_qwen_status", "{}"));
        SelfTests.Check("status reports model and context", Text(status).Contains(Policy.Model) && Text(status).Contains("131072"));
        SelfTests.Check("unknown methods get a JSON-RPC error", (await Ask("{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"nope\"}"))?["error"]?["code"]?.GetValue<int>() == -32601);
        SelfTests.Check("a leading byte-order mark is tolerated", (await Ask("﻿{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"ping\"}"))?["result"] is not null);
        SelfTests.Check("malformed input gets a parse error", (await Ask("not json"))?["error"]?["code"]?.GetValue<int>() == -32700);
    }
}
