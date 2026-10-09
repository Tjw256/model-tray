namespace LocalQwenTray;
internal record GpuMemory(int FreeMiB, int TotalMiB);
internal record EngineState(bool Exists, bool Running, int ExitCode);
internal record ProbeResult(bool Ready, bool Mismatch = false, string Detail = "");
internal interface IQwenHost
{
    Task<IDisposable> AcquireLease(CancellationToken ct);
    Task<EngineState> Inspect(CancellationToken ct);
    Task<GpuMemory> FreeVram(CancellationToken ct);
    Task LaunchNative(GpuMemory memory, CancellationToken ct);
    Task Stop(CancellationToken ct);
    Task<ProbeResult> Probe(CancellationToken ct, bool verifyInference = true);
    Task Delay(CancellationToken ct);
    Task VerifyResidency(ProbeResult probe, CancellationToken ct);
    Task SyncContext(ProbeResult probe, CancellationToken ct);
    Task<string> Diagnostics(CancellationToken ct);
    LaunchChoice Choice { get; set; }                    // what the next load uses
    (string Variant, int Context)? Loaded();             // what the running engine actually loaded
}
internal sealed class Controller(IQwenHost host)
{
    readonly SemaphoreSlim gate = new(1,1);
    public string Status { get; private set; } = "Checking status";
    public event Action<string>? Changed;
    static string Ready(ProbeResult probe) => string.IsNullOrEmpty(probe.Detail) ? "Ready" : $"Ready ({probe.Detail}; new client session may be needed)";
    void Set(string value) { if (Status == value) return; Status = value; Changed?.Invoke(value); }
    public async Task Monitor(CancellationToken ct)
    {
        if (!await gate.WaitAsync(0,ct)) return;
        try
        {
            var state = await host.Inspect(ct);
            var probe = state.Running ? await host.Probe(ct,false) : new ProbeResult(false);
            var next = Policy.Classify(state.Running,probe.Ready,probe.Mismatch,state.ExitCode);
            if (next == "Ready") next = Ready(probe);
            if (next != "Stopped" || !Status.StartsWith("Error:")) Set(next);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Set("Error: status check failed: " + ex.Message); }
        finally { gate.Release(); }
    }
    public async Task Start(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        bool started = false;
        IDisposable? lease = null;
        try
        {
            lease = await host.AcquireLease(token);
            Set("Checking native process ownership");
            var state = await host.Inspect(token);
            if (!state.Running)
            {
                var existing = await host.Probe(token,false);
                if (existing.Ready || existing.Mismatch) throw new InvalidOperationException($"Unowned listener on internal port {Policy.BackendPort}; cannot adopt or stop an unknown server");
                Set("Checking GPU memory");
                var memory = await host.FreeVram(token);
                int budget = Policy.Budget(memory);
                Set($"Starting native Qwen (GPU budget {budget} MiB)");
                await host.LaunchNative(memory,token);
                started = true;
            }
            Set("Loading model");
            while (true)
            {
                state = await host.Inspect(token);
                if (!state.Running)
                {
                    await host.Delay(token);
                    state = await host.Inspect(token);
                    if (!state.Running) throw new InvalidOperationException("Owned native process exited while loading; check logs");
                }
                var probe = await host.Probe(token);
                if (probe.Mismatch) throw new InvalidOperationException("model identity mismatch; expected " + Policy.Model);
                if (probe.Ready) { await host.VerifyResidency(probe,token); await host.SyncContext(probe,token); Set(Ready(probe)); return; }
                await host.Delay(token);
            }
        }
        catch (Exception ex)
        {
            string error = ex is OperationCanceledException ? (ct.IsCancellationRequested ? "Start cancelled" : "Error: native startup timed out after 3 minutes") : "Error: " + ex.Message;
            if (started)
            {
                try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await host.Stop(cleanup.Token); }
                catch (Exception cleanup) { error = "Error: startup cleanup failed: " + cleanup.Message + "; " + error; }
            }
            Set(error);
        }
        finally { lease?.Dispose(); gate.Release(); }
    }
    public async Task Stop(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var lease = await host.AcquireLease(ct);
            Set("Stopping owned native Qwen");
            var state = await host.Inspect(ct);
            await host.Stop(ct);
            if ((await host.Inspect(ct)).Running) throw new InvalidOperationException("Owned process still running after Stop");
            var probe = await host.Probe(ct,false);
            if (probe.Ready || probe.Mismatch) throw new InvalidOperationException("Unknown endpoint still responding; it was not stopped");
            Set("Stopped");
        }
        catch (Exception ex) { Set("Error: stop failed: " + ex.Message); }
        finally { gate.Release(); }
    }
}

