using System.Security.Cryptography;
using System.Text.Json;
namespace LocalQwenTray;
// Everything machine-specific lives in %LOCALAPPDATA%\LocalQwen\config.json (created on first run).
// Edit it to point at your llama-server build, a different Ollama model, or your own client-sync hook.
internal sealed class AppConfig
{
    // Bearer key clients must send to http://127.0.0.1:8000/v1 (random on first run).
    public string ApiKey { get; set; } = "";
    // Path to llama-server.exe (official llama.cpp Windows CUDA release). Default: <app>\llama.cpp\llama-server.exe
    public string LlamaServer { get; set; } = "";
    public string GpuDevice { get; set; } = "CUDA0";
    // Required name prevents accidentally using an integrated GPU's shared memory.
    public string GpuName { get; set; } = "";
    public bool SpeculativeDecoding { get; set; } = true;
    public bool EnableVision { get; set; } = true;
    // Optional Jinja chat template; empty = use the template embedded in the GGUF.
    public string ChatTemplate { get; set; } = "";
    // Ollama model whose downloaded blobs are loaded in place (tags such as q5_K_M / q4_K_M become the "Model" choices).
    public string OllamaRepository { get; set; } = "orcarouter/Qwen3.8-27B-Uncensored";
    // Model name advertised to clients (one stable name whichever quant is loaded).
    public string ModelName { get; set; } = "qwen3.8-27b-uncensored-q5_k_m";
    // Optional command run after a load whose context differs from the last one; "{context}" is replaced
    // by the allocated token count. Use it to update your clients' context settings.
    public string[]? ClientSyncCommand { get; set; }

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    public static string DefaultDirectory => Environment.GetEnvironmentVariable("MODEL_TRAY_HOME") is { Length: > 0 } custom ? Path.GetFullPath(custom) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalQwen");
    public static string PathIn(string dir) => Path.Combine(dir, "config.json");

    public static AppConfig LoadOrCreate(string? dir = null, string? appDirectory = null)
    {
        dir ??= DefaultDirectory; appDirectory ??= AppContext.BaseDirectory;
        var path = PathIn(dir);
        if (File.Exists(path))
        {
            var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? throw new InvalidOperationException("config.json is empty");
            if (string.IsNullOrWhiteSpace(loaded.ApiKey)) throw new InvalidOperationException($"config.json has no ApiKey: {path}");
            if (string.IsNullOrWhiteSpace(loaded.LlamaServer)) loaded.LlamaServer = DefaultServer(appDirectory);
            return loaded;
        }
        var created = new AppConfig
        {
            ApiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            LlamaServer = DefaultServer(appDirectory),
            ChatTemplate = File.Exists(Path.Combine(appDirectory, TemplateFile)) ? Path.Combine(appDirectory, TemplateFile) : "",
        };
        created.Save(dir);
        return created;
    }
    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(PathIn(dir), JsonSerializer.Serialize(this, Pretty));
    }
    public const string TemplateFile = "qwen38-codex-chat-template.jinja";
    static string DefaultServer(string appDirectory) => Path.Combine(appDirectory, "llama.cpp", "llama-server.exe");
}
