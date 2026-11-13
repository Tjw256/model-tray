using System.Text.Json;
namespace LocalQwenTray;

// Named profiles are exact launches. Arguments contain model-specific options so new
// architectures do not require changes to the gateway or the default Qwen policy.
internal sealed class ModelProfile
{
    public string ModelFile { get; set; } = "";
    public string? Projector { get; set; }
    public string? ChatTemplate { get; set; }
    public int ContextTokens { get; set; } = 32768;
    public int Slots { get; set; } = 1;
    public int RequiredFreeMiB { get; set; } = 25000;
    public string[] Arguments { get; set; } = [];
    // Optional llama-server.exe for this profile; empty uses the tray's. Lets a benchmarked
    // profile stay on its build while the default engine is upgraded.
    public string? LlamaServer { get; set; }
    // A background profile yields the card: see ProfileAdmission.
    public bool Background { get; set; }
    public string Engine(AppConfig config) => Path.GetFullPath(string.IsNullOrWhiteSpace(LlamaServer) ? config.LlamaServer : LlamaServer);

    public static ModelProfile Savannah() => new()
    {
        Background = true,
        ModelFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models", "blobs", "sha256-1a0d6fe2e48417f1571a57a7104b4d5da0c45c3d00de18fa7aa48affca5a24d7"),
        Arguments = ["-ngl", "999", "--load-mode", "none", "-t", "4", "-tb", "4", "-b", "512", "-ub", "512", "-fa", "on", "-ctk", "f16", "-ctv", "f16", "--spec-type", "draft-mtp", "--spec-draft-sampling", "probabilistic", "--no-host", "--spec-draft-n-max", "5", "--spec-draft-ngl", "999", "--spec-draft-threads", "4", "--spec-draft-threads-batch", "4", "--spec-draft-type-k", "f16", "--spec-draft-type-v", "f16", "-lv", "4", "--override-tensor", "token_embd.weight=CUDA0", "--reasoning-format", "deepseek", "--slots"]
    };

    public void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "default" || name == Policy.Model || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') || string.IsNullOrWhiteSpace(ModelFile)
            || ContextTokens < 1 || Slots < 1 || RequiredFreeMiB < 1 || Arguments is null || Arguments.Any(a => a is null))
            throw new InvalidOperationException("Invalid model profile: " + name);
        // Keep transport, authentication and the declared model/context under tray control.
        // Equals syntax is forbidden too, including llama.cpp's long option aliases.
        string[] reserved = ["-m", "--model", "--mmproj", "-c", "--ctx-size", "-np", "--parallel", "--host", "--port", "-a", "--alias", "--api-key", "--api-key-file", "--chat-template", "--chat-template-file", "--jinja", "--no-jinja", "--log-file", "--slot-save-path", "-mu", "--model-url", "-dr", "--docker-repo", "-hf", "-hfr", "--hf-repo", "-hff", "--hf-file", "--models-dir", "--models-preset", "--models-max", "--models-autoload", "--no-models-autoload", "--spec-draft-model", "-md", "--model-draft", "--spec-draft-hf", "-hfd", "-hfrd", "--hf-repo-draft", "--lora", "--lora-scaled", "--control-vector"];
        if (Arguments.Any(a => reserved.Contains(a.Split('=')[0]) || a.Contains("\0")))
            throw new InvalidOperationException("Profile arguments override a managed option: " + name);
        if (Arguments.Any(a => a.Split('=')[0] == "--fit")) throw new InvalidOperationException("Profile fitting is disabled: " + name);
        string[] credentialsOrTools = ["--hf-token", "-hft", "--ssl-key-file", "--ssl-cert-file", "--tools", "--mcp-servers-config", "--mcp-servers-json"];
        if (Arguments.Any(a => credentialsOrTools.Contains(a.Split('=')[0]))) throw new InvalidOperationException("Profile arguments cannot configure credentials or server tools: " + name);
    }

    public string[] BuildArguments(string name, string log) =>
        ["-m", Path.GetFullPath(ModelFile), "--fit", "off", "-c", ContextTokens.ToString(), "-np", Slots.ToString(),
         ..(Projector is null ? Array.Empty<string>() : ["--mmproj", Path.GetFullPath(Projector)]),
         ..Arguments, "--host", "127.0.0.1", "--port", Policy.BackendPort.ToString(), "--alias", name, "--jinja",
         ..(ChatTemplate is null ? Array.Empty<string>() : ["--chat-template-file", Path.GetFullPath(ChatTemplate)]),
         "--log-colors", "off", "--log-file", log];
}

// A writer turnstile prevents a stream of old-profile requests from starving a
// pending switch. Same-profile requests still run concurrently. Cancellation
// removes a waiting request without interrupting any active stream.
// Background requests enter only when no interactive request is waiting or running
// and the interactive side has been quiet for Quiet; a running background request
// is never interrupted. Interactive requests count as waiting from arrival, so a
// background request cannot take the turn ahead of one.
internal sealed class ProfileAdmission(Func<string?, CancellationToken, Task> select, Func<DateTimeOffset>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(Policy.BackgroundQuietSeconds);
    readonly SemaphoreSlim turn = new(1, 1);
    readonly object sync = new();
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);
    readonly Func<TimeSpan, CancellationToken, Task> pause = delay ?? Task.Delay;
    TaskCompletionSource drained = Completed(), interactiveDone = Completed();
    string? current;
    int active, interactive;
    DateTimeOffset quietSince = DateTimeOffset.MinValue;
    static TaskCompletionSource Completed() { var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); t.SetResult(); return t; }

    public async Task<IDisposable> Enter(string? profile, CancellationToken ct, bool background = false)
    {
        if (!background) lock (sync) { if (interactive++ == 0) interactiveDone = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        bool admitted = false;
        try
        {
            while (true)
            {
                if (background) await WaitForQuiet(ct);
                await turn.WaitAsync(ct);
                try
                {
                    // An interactive request may have arrived between the quiet check and the turn.
                    if (background && !IsQuiet()) continue;
                    Task wait;
                    lock (sync) wait = current == profile ? Task.CompletedTask : drained.Task;
                    await wait.WaitAsync(ct);
                    await select(profile, ct);
                    lock (sync)
                    {
                        current = profile;
                        if (active++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    admitted = true;
                    return new Lease(this, !background);
                }
                finally { turn.Release(); }
            }
        }
        finally { if (!background && !admitted) EndInteractive(); }
    }
    bool IsQuiet() { lock (sync) return interactive == 0 && now() - quietSince >= Quiet; }
    async Task WaitForQuiet(CancellationToken ct)
    {
        while (true)
        {
            Task? busy = null;
            TimeSpan left;
            lock (sync)
            {
                if (interactive > 0) busy = interactiveDone.Task;
                left = quietSince + Quiet - now();
            }
            if (busy is not null) await busy.WaitAsync(ct);
            else if (left > TimeSpan.Zero) await pause(left, ct);
            else return;
        }
    }
    void EndInteractive()
    {
        lock (sync)
        {
            if (--interactive > 0) return;
            quietSince = now();
            interactiveDone.TrySetResult();
        }
    }
    sealed class Lease(ProfileAdmission owner, bool interactive) : IDisposable
    {
        int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (owner.sync) { if (--owner.active == 0) owner.drained.TrySetResult(); }
            if (interactive) owner.EndInteractive();
        }
    }
}
