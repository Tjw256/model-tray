namespace LocalQwenTray;
// A deterministic in-memory boundary: never invokes Docker, GPU tools or networking.
internal sealed class TestHost : IQwenHost
{
    public EngineState State = new(false, false, 0);
    public ProbeResult Health = new(false);
    public int Total = 32607, LastBudget, LastContext;
    public LaunchChoice Choice { get; set; } = LaunchChoice.Default;
    public (string Variant, int Context)? Loaded() => State.Running ? (Choice.Variant, LastContext) : null;
    public int Free = 32000, UpCalls, StopCalls, FreeCalls, Delays;
    public bool BecomesReady = true, ForeignListener;
    public Func<CancellationToken, Task>? DelayHook, StopHook;
    public Func<EngineState>? InspectHook;
    public Task<IDisposable> AcquireLease(CancellationToken ct) => Task.FromResult<IDisposable>(new MemoryStream());
    public Task<EngineState> Inspect(CancellationToken ct) => Task.FromResult(InspectHook?.Invoke() ?? State);
    public Task<GpuMemory> FreeVram(CancellationToken ct) { FreeCalls++; return Task.FromResult(new GpuMemory(Free, Total)); }
    public Task LaunchNative(GpuMemory memory, CancellationToken ct) { LastBudget = Policy.Budget(memory); LastContext = Policy.ChooseContext(LastBudget, Choice.Context); UpCalls++; State = new(true, true, 0); return Task.CompletedTask; }
    public async Task Stop(CancellationToken ct) { if (ForeignListener || !State.Running && (Health.Ready || Health.Mismatch)) throw new InvalidOperationException("Unknown listener still open"); if (State.Running) { StopCalls++; if (StopHook is not null) await StopHook(ct); } State = new(true, false, 0); Health = new(false); }
    public int InferenceProbes, PassiveProbes;
    public Task<ProbeResult> Probe(CancellationToken ct, bool verifyInference = true)
    {
        if (verifyInference) InferenceProbes++; else PassiveProbes++;
        return Task.FromResult(Health);
    }
    public Task Delay(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Delays++; if (DelayHook is not null) return DelayHook(ct); if (BecomesReady) Health = new(true); return Task.CompletedTask; }
    public Task VerifyResidency(ProbeResult probe,CancellationToken ct) => Task.CompletedTask;
    public int SyncCalls;
    public Task SyncContext(ProbeResult probe, CancellationToken ct) { SyncCalls++; return Task.CompletedTask; }
    public Task<string> Diagnostics(CancellationToken ct) => Task.FromResult("test diagnostics");
}
