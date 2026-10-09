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
        SelfTests.Check("MCP lists the chat, image, agent and status tools",
            names is not null && names.SequenceEqual(new[] { "ask_local_qwen", "ask_local_qwen_about_image", "run_local_qwen_agent", "local_qwen_status" }));

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
        string[] recorded =
        [
            "{\"type\": \"tool_use\", \"sessionID\": \"ses_test\", \"part\": {\"tool\": \"read\", \"state\": {\"status\": \"completed\", \"input\": {\"filePath\": \"C:\\\\work\\\\proj\\\\calc.py\"}}}}",
            "{\"type\": \"tool_use\", \"sessionID\": \"ses_test\", \"part\": {\"tool\": \"edit\", \"state\": {\"status\": \"completed\", \"input\": {\"filePath\": \"C:\\\\work\\\\proj\\\\calc.py\", \"oldString\": \"/ (len(values) - 1)\", \"newString\": \"/ len(values)\"}}}}",
            "{\"type\": \"tool_use\", \"sessionID\": \"ses_test\", \"part\": {\"tool\": \"bash\", \"state\": {\"status\": \"error\", \"input\": {\"command\": \"python test_calc.py\"}, \"error\": \"The user has specified a rule which prevents you from using this specific tool call\"}}}",
            "{\"type\": \"tool_use\", \"sessionID\": \"ses_test\", \"part\": {\"tool\": \"write\", \"state\": {\"status\": \"error\", \"input\": {\"filePath\": \"C:\\\\outside\\\\x.txt\"}, \"error\": \"permission denied: external_directory\"}}}",
            "{\"type\": \"text\", \"sessionID\": \"ses_test\", \"part\": {\"text\": \"Fixed calc.py:2 - the mean divided by len(values) - 1.\"}}",
            "{\"type\": \"step_finish\", \"sessionID\": \"ses_test\", \"part\": {\"reason\": \"stop\"}}"
        ];
        var agentDir = Path.Combine(Path.GetTempPath(), "localqwen-agent-" + Guid.NewGuid()); Directory.CreateDirectory(agentDir);
        string[] Recorded() => recorded.Select(r => r.Replace("C:\\\\work\\\\proj", agentDir.Replace("\\", "\\\\"))).ToArray();
        string Folder() => System.Text.Json.JsonSerializer.Serialize(agentDir);
        var jobs = new List<ProcessJob>();
        var withAgent = new McpServer("mcp-key", "http://127.0.0.1:8000/v1", http,
            (job, _) => { jobs.Add(job); return Task.FromResult<(int, IReadOnlyList<string>, bool)>((0, Recorded(), false)); },
            () => "C:/opencode/opencode.exe");
        var run = await withAgent.Handle(Call(20, "run_local_qwen_agent", "{\"task\":\"make the test pass\",\"working_directory\":" + Folder() + ",\"timeout_minutes\":5}"), default);
        var report = Text(run);
        var ran = jobs.Single();
        SelfTests.Check("agent tool runs OpenCode's worker agent once in the folder with JSON output",
            ran.Executable == "C:/opencode/opencode.exe" && ran.WorkingDirectory == agentDir && ran.Timeout <= TimeSpan.FromMinutes(5) && ran.Timeout > TimeSpan.FromMinutes(4)
            && ran.Arguments.StartsWith("run --pure --agent local-qwen-mcp --model local-qwen/" + Policy.Model + " --auto --format json --dir ") && ran.Arguments.Contains("\"make the test pass"));
        var config = JsonNode.Parse(ran.Environment["OPENCODE_CONFIG_CONTENT"]);
        var permission = config?["agent"]?["local-qwen-mcp"]?["permission"];
        SelfTests.Check("the worker gets file tools only: no shell, subagents or web, nothing outside the folder",
            permission?["*"]?.GetValue<string>() == "deny" && permission?["edit"]?.GetValue<string>() == "allow" && permission?["read"]?.GetValue<string>() == "allow"
            && permission?["external_directory"]?.GetValue<string>() == "deny" && permission?["bash"] is null && permission?["task"] is null
            && ran.Environment["OPENCODE_PERMISSION"].Contains("\"*\":\"deny\"") && !ran.Environment["OPENCODE_PERMISSION"].Contains("bash"));
        SelfTests.Check("a project opencode.json cannot loosen the worker's policy", ran.Environment.GetValueOrDefault("OPENCODE_DISABLE_PROJECT_CONFIG") == "1");
        var provider = config?["provider"]?["local-qwen"];
        SelfTests.Check("the worker brings its own provider (gateway, key, loaded context) instead of relying on the user's opencode.json",
            provider?["options"]?["baseURL"]?.GetValue<string>() == "http://127.0.0.1:8000/v1" && provider?["options"]?["apiKey"]?.GetValue<string>() == "mcp-key"
            && provider?["models"]?[Policy.Model]?["limit"]?["context"]?.GetValue<int>() == 131072);
        SelfTests.Check("agent report has Qwen's answer, changed files, rounds and no check",
            !IsError(run) && report.Contains("Fixed calc.py:2") && report.Contains("Files changed: calc.py") && report.Contains("4 tool calls, 1 round,") && !report.Contains("Check `"));
        SelfTests.Check("denied shell and outside-folder actions show up as tool errors with their target, not as changed files",
            report.Contains("Tool errors: bash python test_calc.py: denied (not permitted for the worker)") && report.Contains("write C:\\outside\\x.txt: permission denied: external_directory")
            && report.Contains("Files changed: calc.py" + Environment.NewLine));

        // check_command: run by the server after Qwen's turn; a failure goes back to Qwen in the same session.
        jobs.Clear(); int checks = 0; bool alwaysFail = false;
        var checking = new McpServer("mcp-key", "http://127.0.0.1:8000/v1", http, (job, _) =>
        {
            jobs.Add(job);
            if (job.Executable.EndsWith("opencode.exe")) return Task.FromResult<(int, IReadOnlyList<string>, bool)>((0, Recorded(), false));
            checks++;
            return Task.FromResult<(int, IReadOnlyList<string>, bool)>(alwaysFail || checks == 1 ? (1, ["FAILED: test_add expected 5 got -1"], false) : (0, ["OK"], false));
        }, () => "C:/opencode/opencode.exe");
        var passed = Text(await checking.Handle(Call(24, "run_local_qwen_agent", "{\"task\":\"fix add\",\"working_directory\":" + Folder() + ",\"check_command\":\"python -m pytest -q\"}"), default));
        SelfTests.Check("check_command runs in the folder after Qwen's turn as a plain shell, without the worker's OpenCode settings",
            jobs.Count == 4 && jobs[1].Arguments == "/d /s /c \"python -m pytest -q\"" && jobs[1].WorkingDirectory == agentDir && jobs[1].MergeErrors
            && !jobs[1].Environment.ContainsKey("OPENCODE_CONFIG_CONTENT") && jobs[0].Arguments.Contains("python -m pytest -q") && jobs[0].Utf8Output && !jobs[1].Utf8Output);
        SelfTests.Check("a failed check goes back to Qwen in the same session with its output",
            jobs[2].Arguments.Contains("--session ses_test") && jobs[2].Arguments.Contains("FAILED: test_add expected 5 got -1") && !jobs[0].Arguments.Contains("--session"));
        SelfTests.Check("the report says the check passed and after how many rounds", passed.Contains("Check `python -m pytest -q`: PASSED") && passed.Contains("2 rounds"));
        jobs.Clear(); checks = 0; alwaysFail = true;
        var gaveUp = await checking.Handle(Call(25, "run_local_qwen_agent", "{\"task\":\"fix add\",\"working_directory\":" + Folder() + ",\"check_command\":\"python -m pytest -q\",\"max_rounds\":2}"), default);
        SelfTests.Check("a check that keeps failing stops after max_rounds and shows its output",
            jobs.Count == 4 && Text(gaveUp).Contains("FAILED (exit 1) after 2 rounds") && Text(gaveUp).Contains("expected 5 got -1") && !IsError(gaveUp));

        SelfTests.Check("command-line arguments are quoted the way Windows programs parse them",
            McpServer.JoinArguments(["plain", "a b", "q\"x", "C:\\my dir\\", ""]) == "plain \"a b\" \"q\\\"x\" \"C:\\my dir\\\\\" \"\"");
        SelfTests.Check("agent needs an existing absolute folder", IsError(await withAgent.Handle(Call(21, "run_local_qwen_agent", "{\"task\":\"x\",\"working_directory\":\"relative\\\\dir\"}"), default)));
        var noOpenCode = new McpServer("k", "http://127.0.0.1:8000/v1", http, (_, _) => throw new Exception("must not run"), () => null);
        SelfTests.Check("missing OpenCode is reported, nothing runs", Text(await noOpenCode.Handle(Call(22, "run_local_qwen_agent", "{\"task\":\"x\",\"working_directory\":" + Folder() + "}"), default)).Contains("not installed"));
        var slow = new McpServer("k", "http://127.0.0.1:8000/v1", http, (_, _) => Task.FromResult<(int, IReadOnlyList<string>, bool)>((-1, Array.Empty<string>(), true)), () => "C:/oc.exe");
        var stopped = await slow.Handle(Call(23, "run_local_qwen_agent", "{\"task\":\"x\",\"working_directory\":" + Folder() + ",\"check_command\":\"exit 0\"}"), default);
        SelfTests.Check("an agent that hits the time limit is stopped and reported, and the check is not run", IsError(stopped) && Text(stopped).Contains("STOPPED") && Text(stopped).Contains("not run"));
        Directory.Delete(agentDir, true);
        SelfTests.Check("a leading byte-order mark is tolerated", (await Ask("﻿{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"ping\"}"))?["result"] is not null);
        SelfTests.Check("malformed input gets a parse error", (await Ask("not json"))?["error"]?["code"]?.GetValue<int>() == -32700);
    }
}
