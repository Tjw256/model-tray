using System.Text.Json;
namespace LocalQwenTray;
internal enum TrayState { Sleeping, Loading, Ready, Busy, Error }
internal record ReplyStats(int Tokens, double TokensPerSecond, DateTimeOffset At);
// Persisted per-user preferences (idle timeout). Missing or unreadable file means defaults.
internal sealed class AppSettings
{
    public int? IdleMinutes { get; set; } = Policy.DefaultIdleMinutes;
    public int ContextTokens { get; set; } = Policy.Ctx128;
    public string Variant { get; set; } = Policy.DefaultVariant;
    public string Reasoning { get; set; } = Policy.DefaultReasoning;
    public int ParallelRequests { get; set; } = Policy.DefaultSlots;
    [System.Text.Json.Serialization.JsonIgnore] public string ReasoningOrDefault => Policy.ReasoningChoices.Contains(Reasoning) ? Reasoning : Policy.DefaultReasoning;
    [System.Text.Json.Serialization.JsonIgnore] public LaunchChoice Choice => new(Policy.Variants.Contains(Variant) ? Variant : Policy.DefaultVariant, Policy.ContextChoices.Contains(ContextTokens) ? ContextTokens : Policy.Ctx128, Policy.SlotChoices.Contains(ParallelRequests) ? ParallelRequests : Policy.DefaultSlots);
    static string PathFor(string dir) => Path.Combine(dir, "settings.json");
    public static AppSettings Load(string dir)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathFor(dir))) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(PathFor(dir), JsonSerializer.Serialize(this));
    }
}
// On-demand lifecycle: the first request loads the model (one shared load for concurrent callers),
// in-flight requests are counted, and the model is unloaded after IdleTimeout with nothing in flight.
internal sealed class Supervisor(Controller controller, Func<DateTimeOffset>? clock = null, IQwenHost? sessions = null)
{
    readonly object sync = new();
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);
    readonly CancellationTokenSource lifetime = new();
    Task? load;
    int inFlight;
    public Controller Controller => controller;
    public TimeSpan? IdleTimeout { get; set; } = TimeSpan.FromMinutes(Policy.DefaultIdleMinutes);
    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.MinValue;
    public ReplyStats? LastReply { get; private set; }
    public int InFlight { get { lock (sync) return inFlight; } }
    public event Action? Changed;
    public bool IsReady => controller.Status.StartsWith("Ready");
    public TrayState State
    {
        get
        {
            var s = controller.Status;
            if (s.StartsWith("Error:")) return TrayState.Error;
            if (s.StartsWith("Ready")) return InFlight > 0 ? TrayState.Busy : TrayState.Ready;
            if (s is "Stopped" or "Checking status" or "Start cancelled") return InFlight > 0 ? TrayState.Loading : TrayState.Sleeping;
            return TrayState.Loading;
        }
    }
    // Time until the idle unload, or null when nothing is scheduled.
    public TimeSpan? UnloadsIn
    {
        get
        {
            if (!IsReady || InFlight > 0 || IdleTimeout is null || LastActivity == DateTimeOffset.MinValue) return null;
            var left = LastActivity + IdleTimeout.Value - now();
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }
    public IDisposable BeginRequest()
    {
        lock (sync) { inFlight++; LastActivity = now(); }
        Changed?.Invoke();
        return new Release(this);
    }
    sealed class Release(Supervisor owner) : IDisposable
    {
        int done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 1) return;
            lock (owner.sync) { owner.inFlight--; owner.LastActivity = owner.now(); }
            owner.Changed?.Invoke();
        }
    }
    public void RecordReply(int tokens, double tokensPerSecond) { LastReply = new(tokens, tokensPerSecond, now()); Changed?.Invoke(); }
    // Loads the model if needed. Concurrent callers share one load; the caller's cancellation only stops its own wait.
    public async Task EnsureReady(CancellationToken ct)
    {
        if (IsReady) return;
        Task current;
        lock (sync)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            current = load is { IsCompleted: false } ? load : load = LoadAndRestore(lifetime.Token);
        }
        await current.WaitAsync(ct);
        if (!IsReady) throw new InvalidOperationException(controller.Status.StartsWith("Error: ") ? controller.Status["Error: ".Length..] : controller.Status);
        lock (sync) LastActivity = now();
    }
    // Restores the conversation saved at the last unload, but only into an engine this load actually started.
    async Task LoadAndRestore(CancellationToken ct)
    {
        await controller.Start(ct);
        if (sessions is not null && IsReady && controller.LastStartLaunched)
        {
            try { await sessions.RestoreSession(ct); } catch (Exception) when (!ct.IsCancellationRequested) { /* a lost cache only costs a re-read */ }
        }
    }
    async Task SaveThenStop(CancellationToken ct)
    {
        if (sessions is not null && IsReady)
        {
            try { await sessions.SaveSession(ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
        }
        await controller.Stop(ct);
    }
    // Called periodically. Returns true when it unloaded the model.
    public async Task<bool> IdleTick(CancellationToken ct)
    {
        lock (sync)
        {
            if (!IsReady || inFlight > 0 || IdleTimeout is null || load is { IsCompleted: false }) return false;
            if (LastActivity == DateTimeOffset.MinValue) { LastActivity = now(); return false; } // adopted at startup: start the clock now
            if (now() - LastActivity < IdleTimeout.Value) return false;
        }
        await SaveThenStop(ct);
        Changed?.Invoke();
        return true;
    }
    public Task Unload(CancellationToken ct) => SaveThenStop(ct);
    // Quit: abandon any pending load (its own cleanup stops a half-started engine), then unload.
    public async Task Shutdown(CancellationToken ct)
    {
        Task? pending;
        lock (sync) { lifetime.Cancel(); pending = load; }
        if (pending is not null) { try { await pending.WaitAsync(ct); } catch (Exception) when (!ct.IsCancellationRequested) { } }
        await SaveThenStop(ct);
    }
    public async Task LoadNow(CancellationToken ct) { await EnsureReady(ct); lock (sync) LastActivity = now(); }
}
