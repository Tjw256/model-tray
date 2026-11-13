using System.Diagnostics;
using System.Text.Json;
namespace LocalQwenTray;
internal record EmbeddingIdentity(int Pid, long StartTimeUtcTicks, string Executable, string Model);
// A second, small llama-server for /v1/embeddings (nomic-embed-text from Ollama's blob by default), separate from
// the chat engine. It runs on the CPU (--device none): embeddings keep working while another program holds the GPU,
// and they never take VRAM from the chat model's context. Measured on b11517 (4 CPU threads): about 300 MB of RAM,
// 37 short texts/s. Started by the first request, stopped when the tray quits. Ownership works like the chat
// engine's: PID + start time + executable, and an unknown listener on its port is never stopped.
internal sealed class EmbeddingEngine(AppConfig config, HttpClient http, string? stateDirectory = null)
{
    readonly SemaphoreSlim gate = new(1, 1);
    string StateDirectory => stateDirectory ?? AppConfig.DefaultDirectory;
    string StatePath => Path.Combine(StateDirectory, "embedding-process.json");
    public string LogPath => Path.Combine(StateDirectory, "embedding-engine.log");
    string Executable => Path.GetFullPath(config.LlamaServer);
    string Model => string.IsNullOrWhiteSpace(config.EmbeddingModel)
        ? OllamaModels.Find("latest", repository: "library/nomic-embed-text")?.Model
            ?? throw new FileNotFoundException("nomic-embed-text is not downloaded in Ollama; run: ollama pull nomic-embed-text, or set EmbeddingModel in config.json to a GGUF path")
        : Path.GetFullPath(config.EmbeddingModel);

    // nomic-embed-text is trained on 2048 tokens; llama-server rejects longer inputs with HTTP 400 either way.
    public static string[] BuildArguments(string model, string log) =>
        ["-m", model, "--embeddings", "--device", "none", "-c", "2048", "-b", "2048", "-ub", "2048", "-np", "1", "-t", "4", "-tb", "4",
         "--host", "127.0.0.1", "--port", Policy.EmbeddingPort.ToString(), "--alias", Policy.EmbeddingModel, "--no-webui", "--log-colors", "off", "--log-file", log];

    // Returns the port once the engine answers /health, starting it if needed.
    public async Task<int> EnsureReady(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            bool owned = Owned() is not null;
            if (owned && await Healthy(ct)) return Policy.EmbeddingPort;
            if (owned) await StopOwned(ct);   // crashed or hung: replace it
            if (await Healthy(ct)) throw new InvalidOperationException($"Unowned listener on embedding port {Policy.EmbeddingPort}; no process was stopped");
            await Launch(ct);
            return Policy.EmbeddingPort;
        }
        finally { gate.Release(); }
    }

    public async Task Stop(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await StopOwned(ct); }
        finally { gate.Release(); }
    }

    async Task Launch(CancellationToken ct)
    {
        var model = Model;
        if (!File.Exists(model)) throw new FileNotFoundException($"Embedding model not found at {model}; fix EmbeddingModel in config.json");
        if (!File.Exists(Executable)) throw new FileNotFoundException($"llama-server.exe not found at {Executable}; set LlamaServer in {AppConfig.PathIn(StateDirectory)}");
        Directory.CreateDirectory(StateDirectory);
        if (File.Exists(LogPath)) File.Copy(LogPath, LogPath + ".previous", true);
        File.WriteAllText(LogPath, "");
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Executable)! };
        NativeHost.ConfigureEnvironment(info, config.ApiKey);
        foreach (var arg in BuildArguments(model, LogPath)) info.ArgumentList.Add(arg);
        using var process = DetachedNative.Start(info);
        var identity = new EmbeddingIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, Executable, model);
        try
        {
            for (int i = 0; i < 50 && !OwnedNativeProcess.Matches(identity.Pid, identity.StartTimeUtcTicks, Executable); i++)
            {
                if (process.HasExited) throw new InvalidOperationException("Embedding engine exited before ownership could be verified; see " + LogPath);
                await Task.Delay(100, ct);
            }
            if (!OwnedNativeProcess.Matches(identity.Pid, identity.StartTimeUtcTicks, Executable)) throw new InvalidOperationException("Embedding engine ownership could not be verified after launch");
            var staged = StatePath + ".tmp";
            File.WriteAllText(staged, JsonSerializer.Serialize(identity)); File.Move(staged, StatePath, true);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            while (!await Healthy(deadline.Token))
            {
                if (process.HasExited) throw new InvalidOperationException($"Embedding engine exited during startup (code {process.ExitCode}); see {LogPath}");
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill();
            throw ex is OperationCanceledException ? new TimeoutException("Embedding engine did not answer /health within 60 s; see " + LogPath) : ex;
        }
    }

    EmbeddingIdentity? Owned()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            var id = JsonSerializer.Deserialize<EmbeddingIdentity>(File.ReadAllText(StatePath));
            return id is not null && string.Equals(Path.GetFullPath(id.Executable), Executable, StringComparison.OrdinalIgnoreCase)
                && OwnedNativeProcess.Matches(id.Pid, id.StartTimeUtcTicks, Executable) ? id : null;
        }
        catch (JsonException) { return null; }
    }

    async Task StopOwned(CancellationToken ct)
    {
        if (Owned() is not { } id) return;
        using var process = Process.GetProcessById(id.Pid);
        // Recheck this handle, not just the PID, immediately before termination.
        if (!OwnedNativeProcess.MatchesHandle(process, id.StartTimeUtcTicks, Executable)) return;
        process.Kill();
        await process.WaitForExitAsync(ct);
    }

    async Task<bool> Healthy(CancellationToken ct)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probe.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{Policy.EmbeddingPort}/health", probe.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
}
