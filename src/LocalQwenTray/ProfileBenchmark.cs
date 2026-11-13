using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace LocalQwenTray;

// Explicit, disruptive verification mode. Never part of verify_all.py. Uses the
// installed binary/key, isolated tray state, synthetic prompts and one GPU process
// at a time. The live tray is left alone and its occupied backend port is refused.
internal static class ProfileBenchmark
{
    static async Task GuardGpu(ProcessRunner runner, CancellationToken ct)
    {
        // WDDM's query-compute-apps also lists desktop C+G applications. The full
        // table distinguishes compute-only jobs; independently reject model processes.
        var result = await runner.Run("nvidia-smi", [], TimeSpan.FromSeconds(15), ct);
        bool modelProcess = new[] { "llama-server", "ollama_llama_server" }.Any(name => Process.GetProcessesByName(name).Length > 0);
        if (result.ExitCode != 0 || !result.Output.Contains("Processes:") || Regex.IsMatch(result.Output,@"\sC\s") || modelProcess)
            throw new InvalidOperationException("GPU compute/model process detected or GPU status unavailable; no test model was started");
        var memory = await runner.Run("nvidia-smi", ["--query-gpu=memory.free", "--format=csv,noheader,nounits", "--id=0"], TimeSpan.FromSeconds(15), ct);
        if (memory.ExitCode != 0 || !int.TryParse(memory.Output.Trim(),out var free) || free < 25000)
            throw new InvalidOperationException("Less than 25000 MiB free VRAM or memory snapshot unavailable; no test model was started");
        var listener = new TcpListener(IPAddress.Loopback, Policy.BackendPort);
        try { listener.Start(); } finally { listener.Stop(); }
    }
    static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    static HttpRequestMessage Request(string url, string key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }
    static async Task<JsonElement> Generate(HttpClient client, string endpoint, string key, CancellationToken ct)
    {
        using var request = Request(endpoint + "/completion", key, new
        {
            model = "savannah-extraction", temperature = 0, seed = 42, n_predict = 256,
            cache_prompt = false, return_tokens = true,
            prompt = "<|im_start|>system\nYou are a precise assistant. Answer in English.<|im_end|>\n<|im_start|>user\nExplain how a queue safely switches between two language model profiles while preserving active requests. Use one short paragraph.<|im_end|>\n<|im_start|>assistant\n<think>\n"
        });
        var timer = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = body.RootElement.Clone();
        if (!root.TryGetProperty("tokens", out var tokens) || tokens.GetArrayLength() == 0 || string.IsNullOrWhiteSpace(root.GetProperty("content").GetString())) throw new InvalidOperationException("Benchmark response has no generated token IDs/content");
        var timings = root.GetProperty("timings");
        if (timings.GetProperty("predicted_n").GetInt32() != tokens.GetArrayLength() || timings.GetProperty("predicted_per_second").GetDouble() <= 0) throw new InvalidOperationException("Invalid benchmark timing/token counts");
        Console.WriteLine($"{tokens.GetArrayLength()} tokens, {timings.GetProperty("predicted_per_second").GetDouble():F2} decode tok/s, {tokens.GetArrayLength() / timer.Elapsed.TotalSeconds:F2} end-to-end tok/s");
        return root;
    }
    public static async Task<int> Run(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new InvalidOperationException("Use a new, empty benchmark output folder");
        Directory.CreateDirectory(output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = deadline.Token;
        var config = JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(AppConfig.PathIn(AppConfig.DefaultDirectory), ct)) ?? throw new InvalidDataException("Missing installed Model Tray config");
        var profile = config.Profiles["savannah-extraction"];
        profile.Validate("savannah-extraction");
        var runner = new ProcessRunner();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        await GuardGpu(runner, ct);
        // Independent baseline: flags transcribed from Savannah's saved :8001
        // command, with only host, port, alias and log destination changed.
        string[] directArgs = ["-m", profile.ModelFile, "-ngl", "999", "--fit", "off", "--load-mode", "none", "-c", "32768", "-np", "1", "-t", "4", "-tb", "4", "-b", "512", "-ub", "512", "-fa", "on", "-ctk", "f16", "-ctv", "f16", "--spec-type", "draft-mtp", "--spec-draft-sampling", "probabilistic", "--no-host", "--spec-draft-n-max", "5", "--spec-draft-ngl", "999", "--spec-draft-threads", "4", "--spec-draft-threads-batch", "4", "--spec-draft-type-k", "f16", "--spec-draft-type-v", "f16", "-lv", "4", "--override-tensor", "token_embd.weight=CUDA0", "--host", "127.0.0.1", "--port", Policy.BackendPort.ToString(), "--alias", "savannah-extraction", "--jinja", "--reasoning-format", "deepseek", "--slots", "--log-colors", "off", "--log-file", Path.Combine(output,"direct.log")];
        await File.WriteAllTextAsync(Path.Combine(output,"direct-arguments.json"),JsonSerializer.Serialize(directArgs),ct);
        var start = new ProcessStartInfo(config.LlamaServer) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(config.LlamaServer)! };
        NativeHost.ConfigureEnvironment(start, config.ApiKey);
        foreach (var arg in directArgs) start.ArgumentList.Add(arg);
        JsonElement direct;
        using (var process = DetachedNative.Start(start))
        {
            try
            {
                while (true)
                {
                    if (process.HasExited) throw new InvalidOperationException("Direct llama-server exited during startup");
                    try
                    {
                        using var health = await client.GetAsync($"http://127.0.0.1:{Policy.BackendPort}/health", ct);
                        if (health.IsSuccessStatusCode) break;
                    }
                    catch (HttpRequestException) { /* the owned child has not opened its listener yet */ }
                    await Task.Delay(500, ct);
                }
                Console.WriteLine("Direct llama-server:");
                direct = await Generate(client, $"http://127.0.0.1:{Policy.BackendPort}", config.ApiKey, ct);
                await File.WriteAllTextAsync(Path.Combine(output,"direct.json"), direct.GetRawText(), ct);
            }
            finally
            {
                // This process object owns a handle to the exact child we started.
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        await GuardGpu(runner, ct);
        var host = new NativeHost(config, runner, client, Path.Combine(output,"state"));
        var supervisor = new Supervisor(new Controller(host), host: host);
        int port = FreePort();
        await using var gateway = new Gateway(supervisor, config.ApiKey, port, Policy.BackendPort, Path.Combine(output,"state"), config: config, native: host);
        await gateway.StartAsync();
        Console.WriteLine($"Temporary gateway: http://127.0.0.1:{port}");
        try
        {
            Console.WriteLine("Model Tray savannah-extraction:");
            var proxied = await Generate(client, $"http://127.0.0.1:{port}", config.ApiKey, ct);
            await File.WriteAllTextAsync(Path.Combine(output,"gateway.json"), proxied.GetRawText(), ct);
            bool equal = direct.GetProperty("tokens").EnumerateArray().Select(t => t.GetInt32()).SequenceEqual(proxied.GetProperty("tokens").EnumerateArray().Select(t => t.GetInt32()));
            if (!equal) throw new InvalidOperationException("Direct/profile generated token IDs differ; receipts retained");
            await File.WriteAllTextAsync(Path.Combine(output,"fingerprint.json"), JsonSerializer.Serialize(await host.Fingerprint("savannah-extraction",ct)),ct);
            Console.WriteLine("PASS direct and profile output token IDs are identical");
            return 0;
        }
        finally { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await supervisor.Shutdown(cleanup.Token); }
    }
}
