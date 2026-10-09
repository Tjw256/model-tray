using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace LocalQwenTray;
// `LocalQwenTray.exe --mcp`: a Model Context Protocol server on stdin/stdout (newline-delimited JSON-RPC 2.0), so any
// MCP-capable agent (Codex, Claude Code, Hermes, OpenCode, ...) can delegate work to Local Qwen as a tool. It talks to
// the tray's gateway, so the model loads on demand, the tray's reasoning setting applies and requests use the free slot.
// run_local_qwen_agent gives Qwen hands: OpenCode runs it in a folder with file tools only (read, search, edit). It gets no
// shell: a permission allowlist cannot fence a shell (tested: with echo denied, Qwen wrote outside via `python -c`). Commands
// such as tests are chosen by the calling agent (check_command) and run by this server; failures go back to Qwen for another round.
internal sealed record ProcessJob(string Executable, string Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, TimeSpan Timeout, bool MergeErrors, bool Utf8Output = false);
internal delegate Task<(int ExitCode, IReadOnlyList<string> Lines, bool TimedOut)> AgentRunner(ProcessJob job, CancellationToken ct);
internal sealed class McpServer(string key, string endpoint, HttpClient http, AgentRunner? agentRunner = null, Func<string?>? openCodeExecutable = null)
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
                ["name"] = "run_local_qwen_agent",
                ["description"] = "Hand a whole task to Local Qwen as an agent: it reads, searches and edits files inside working_directory and returns its summary plus the files it changed. " +
                                    "Qwen cannot run commands, use the web or edit outside that folder. To have its work tested, pass check_command: this server runs it in the folder after Qwen's edits " +
                                    "and gives failures back to Qwen for another round (up to max_rounds). Note the check runs with your permissions and executes code Qwen may have edited, so review " +
                                    "the diff first in untrusted projects. Good for well-defined chores (fix a failing test, add tests, small refactors, bulk edits).",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["task"] = new JsonObject { ["type"] = "string", ["description"] = "What to do, with acceptance criteria (for example: make test_calc.py pass)." },
                        ["working_directory"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path of the folder the agent may work in." },
                        ["check_command"] = new JsonObject { ["type"] = "string", ["description"] = "Optional command (cmd.exe syntax) that checks the work, for example: python -m pytest -q. Exit code 0 means done." },
                        ["max_rounds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5, ["description"] = "With check_command: how many times Qwen may work and be checked (default 3)." },
                        ["timeout_minutes"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 60, ["description"] = "Stop everything after this long (default 20)." },
                    },
                    ["required"] = new JsonArray("task", "working_directory"),
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
            case "run_local_qwen_agent":
                return await RunAgent(args, ct);
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

    async Task<JsonObject> RunAgent(JsonObject args, CancellationToken ct)
    {
        var task = args["task"]?.GetValue<string>();
        var dir = args["working_directory"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(task) || string.IsNullOrWhiteSpace(dir)) return ToolText("Missing required arguments: task and working_directory", isError: true);
        if (!Path.IsPathFullyQualified(dir) || !Directory.Exists(dir)) return ToolText("working_directory must be an existing absolute folder: " + dir, isError: true);
        var exe = (openCodeExecutable ?? OpenCodeClient.Executable)();
        if (exe is null) return ToolText("OpenCode is not installed (npm install -g opencode-ai), so the agent cannot run.", isError: true);
        var check = args["check_command"]?.GetValue<string>() is { Length: > 0 } c && c.Trim().Length > 0 ? c.Trim() : null;
        int maxRounds = check is null ? 1 : Math.Clamp(args["max_rounds"]?.GetValue<int>() ?? 3, 1, 5);
        var limit = TimeSpan.FromMinutes(Math.Clamp(args["timeout_minutes"]?.GetValue<int>() ?? 20, 1, 60));
        var run = agentRunner ?? RunProcess;
        var env = WorkerEnvironment(await ContextTokens(ct));
        var started = Stopwatch.StartNew();
        TimeSpan Left() => limit - started.Elapsed;
        var lines = new List<string>(); string? session = null; int exit = 0, rounds = 0; bool timedOut = false;
        (int Exit, string Output)? checkResult = null;
        var prompt = task + "\n\nYou have file tools only (read, search, edit); you cannot run commands or touch anything outside this folder." +
                     (check is null ? "" : $" When you finish, `{check}` is run in this folder to check your work; if it fails you get its output and another turn.");
        while (true)
        {
            if (Left() < TimeSpan.FromSeconds(5)) { timedOut = true; break; }
            rounds++;
            var cli = new List<string> { "run", "--pure", "--agent", WorkerAgent, "--model", $"{OpenCodeClient.ProviderId}/{Policy.Model}", "--auto", "--format", "json", "--dir", dir };
            if (session is not null) cli.AddRange(["--session", session]);
            cli.Add(prompt);
            var agent = await run(new ProcessJob(exe, JoinArguments(cli), dir, env, Left(), MergeErrors: false, Utf8Output: true), ct);
            lines.AddRange(agent.Lines); exit = agent.ExitCode;
            if (agent.TimedOut) { timedOut = true; break; }
            session ??= SessionId(agent.Lines);
            if (check is null) break;
            var checkLimit = Left() < TimeSpan.FromMinutes(10) ? Left() : TimeSpan.FromMinutes(10);
            var ran = await run(new ProcessJob(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/d /s /c \"{check}\"", dir, NoEnvironment, checkLimit, true), ct);
            checkResult = (ran.TimedOut ? -1 : ran.ExitCode, (ran.TimedOut ? "[check stopped: time limit]\n" : "") + string.Join("\n", ran.Lines));
            if (checkResult.Value.Exit == 0 || rounds >= maxRounds || session is null) break;
            prompt = $"`{check}` failed (exit {checkResult.Value.Exit}). End of its output:\n```\n{Tail(checkResult.Value.Output, 4000)}\n```\n" +
                     "Fix the cause so it passes. Do not delete, skip or weaken the checks.";
        }
        bool answered = lines.Any(l => l.Contains("\"type\":\"text\""));
        return ToolText(SummariseAgent(lines, exit, timedOut, started.Elapsed, dir, rounds, check, checkResult), isError: timedOut || (exit != 0 && !answered));
    }

    public const string WorkerAgent = "local-qwen-mcp";
    static readonly IReadOnlyDictionary<string, string> NoEnvironment = new Dictionary<string, string>();
    // Everything not listed is denied: bash, subagents (task), web, MCP servers. Edits outside the folder are refused.
    internal static JsonObject WorkerPermission() => new()
    {
        ["*"] = "deny", ["read"] = "allow", ["edit"] = "allow", ["glob"] = "allow", ["grep"] = "allow", ["list"] = "allow", ["todowrite"] = "allow",
        ["external_directory"] = "deny",
    };
    // The worker's whole OpenCode setup travels in environment variables, so it does not depend on the user's opencode.json.
    // Project-level config is ignored: Qwen could otherwise write itself an opencode.json with a looser policy for the next round.
    internal Dictionary<string, string> WorkerEnvironment(int context) => new()
    {
        ["OPENCODE_CONFIG_CONTENT"] = new JsonObject
        {
            ["provider"] = new JsonObject { [OpenCodeClient.ProviderId] = OpenCodeClient.ProviderEntry(key, context, endpoint) },
            ["agent"] = new JsonObject
            {
                [WorkerAgent] = new JsonObject
                {
                    ["description"] = "Local Qwen worker for the MCP run_local_qwen_agent tool: file tools only, inside its folder.",
                    ["mode"] = "primary",
                    ["model"] = $"{OpenCodeClient.ProviderId}/{Policy.Model}",
                    ["permission"] = WorkerPermission(),
                },
            },
        }.ToJsonString(),
        ["OPENCODE_PERMISSION"] = WorkerPermission().ToJsonString(),
        ["OPENCODE_DISABLE_PROJECT_CONFIG"] = "1",
        ["OPENCODE_DISABLE_CLAUDE_CODE"] = "1",
        ["OPENCODE_DISABLE_EXTERNAL_SKILLS"] = "1",
        ["OPENCODE_DISABLE_AUTOUPDATE"] = "1",
        ["OPENCODE_DISABLE_SHARE"] = "1",
    };
    async Task<int> ContextTokens(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint + "/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = await http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode && JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["data"]?[0]?["meta"]?["n_ctx"]?.GetValue<int>() is int n and > 0) return n;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException) { }
        return LaunchChoice.Default.Context;
    }
    static string? SessionId(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            try
            {
                var e = JsonNode.Parse(line);
                if ((e?["sessionID"] ?? e?["part"]?["sessionID"])?.GetValue<string>() is { Length: > 0 } id) return id;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        }
        return null;
    }
    // Windows command-line quoting (the rules CommandLineToArgvW and the C runtime parse back).
    internal static string JoinArguments(IEnumerable<string> args) => string.Join(" ", args.Select(a =>
    {
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', '\n', '"']) < 0) return a;
        var s = new StringBuilder("\""); int slashes = 0;
        foreach (var ch in a)
        {
            if (ch == '\\') { slashes++; continue; }
            s.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes).Append(ch); slashes = 0;
        }
        return s.Append('\\', slashes * 2).Append('"').ToString();
    }));
    static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];
    // Turns OpenCode's JSON event stream into a short report for the calling agent.
    internal static string SummariseAgent(IReadOnlyList<string> lines, int exit, bool timedOut, TimeSpan took, string dir,
        int rounds = 1, string? check = null, (int Exit, string Output)? checkResult = null)
    {
        var answer = new List<string>(); var changed = new List<string>(); var commands = new List<string>(); var failures = new List<string>(); int tools = 0;
        foreach (var line in lines)
        {
            JsonObject? e;
            try { e = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { continue; }
            var part = e?["part"];
            switch (e?["type"]?.GetValue<string>())
            {
                case "text":
                    if (part?["text"]?.GetValue<string>() is { Length: > 0 } t) answer.Add(t.Trim());
                    break;
                case "tool_use":
                    tools++;
                    var tool = part?["tool"]?.GetValue<string>(); var state = part?["state"]; var input = state?["input"];
                    var file = input?["filePath"]?.GetValue<string>() is { } f ? Shown(f) : null;
                    if (state?["status"]?.GetValue<string>() == "error")
                    {
                        var why = state?["error"]?.ToString() ?? "failed";
                        if (why.Contains("prevents you from using this specific tool call")) why = "denied (not permitted for the worker)";
                        var target = file ?? (input?["command"]?.GetValue<string>() is { } command ? Shorten(command, 60) : null);
                        failures.Add($"{tool}{(target is null ? "" : " " + target)}: {Shorten(why, 160)}");
                    }
                    else if (tool is "edit" or "write" or "patch" && file is not null && !changed.Contains(file)) changed.Add(file);
                    if (tool == "bash" && input?["command"]?.GetValue<string>() is { } cmd)
                        commands.Add($"{Shorten(cmd, 120)} (exit {state?["metadata"]?["exit"]?.ToString() ?? "?"})");
                    break;
            }
        }
        string Shown(string path)   // relative inside the folder, absolute outside it
        {
            var rel = Path.GetRelativePath(dir, path);
            return rel.StartsWith("..") || Path.IsPathRooted(rel) ? Path.GetFullPath(path, dir) : rel;
        }
        var report = new StringBuilder();
        report.AppendLine(timedOut ? $"Local Qwen agent STOPPED at the time limit ({took.TotalMinutes:F0} min); work may be incomplete."
                                   : $"Local Qwen agent finished in {took.TotalSeconds:F0} s ({tools} tool calls, {rounds} round{(rounds == 1 ? "" : "s")}, exit {exit}).");
        report.AppendLine().AppendLine("Answer:").AppendLine(answer.Count > 0 ? Shorten(string.Join("\n\n", answer), 6000) : "(no final answer text)");
        report.AppendLine().AppendLine("Files changed: " + (changed.Count > 0 ? string.Join(", ", changed) : "none"));
        if (commands.Count > 0) report.AppendLine("Commands run: " + string.Join("; ", commands.TakeLast(15)));
        if (failures.Count > 0) report.AppendLine("Tool errors: " + string.Join("; ", failures.TakeLast(5)));
        if (check is not null)
        {
            if (checkResult is not { } r) report.AppendLine($"Check `{check}`: not run.");
            else if (r.Exit == 0) report.AppendLine($"Check `{check}`: PASSED (exit 0).");
            else report.AppendLine($"Check `{check}`: FAILED (exit {r.Exit}) after {rounds} round{(rounds == 1 ? "" : "s")}. End of its output:").AppendLine(Tail(r.Output, 2000));
        }
        return report.ToString().TrimEnd();
    }
    static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    static async Task<(int, IReadOnlyList<string>, bool)> RunProcess(ProcessJob job, CancellationToken ct)
    {
        var info = new ProcessStartInfo(job.Executable, job.Arguments) { WorkingDirectory = job.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var (name, value) in job.Environment) info.Environment[name] = value;
        if (job.Utf8Output) info.StandardOutputEncoding = info.StandardErrorEncoding = new UTF8Encoding(false);   // OpenCode writes UTF-8, not the console code page
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + job.Executable);
        process.StandardInput.Close();
        var lines = new List<string>();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(job.Timeout);
        async Task Pump(StreamReader reader, bool keep)   // always drain both pipes so the child never blocks on a full one
        {
            string? l;
            while ((l = await reader.ReadLineAsync(limit.Token)) is not null) if (keep) lock (lines) lines.Add(l);
        }
        try
        {
            await Task.WhenAll(Pump(process.StandardOutput, true), Pump(process.StandardError, job.MergeErrors));
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            lock (lines) return (-1, lines.ToArray(), !ct.IsCancellationRequested);
        }
        return (process.ExitCode, lines, false);
    }
    static JsonObject ToolText(string text, bool isError = false) =>
        new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = isError };
    static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    static JsonObject Error(JsonNode? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
