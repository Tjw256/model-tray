namespace LocalQwenTray;
internal static class SelfTests
{
    static readonly List<string> Results = [];
    public static void Check(string name, bool condition)
    {
        Results.Add($"{(condition ? "PASS" : "FAIL")} {name}");
        if (!condition) throw new Exception(name);
    }
    static async Task Lifecycle()
    {
        var host = new TestHost();
        var controller = new Controller(host);
        await controller.Monitor(default);
        Check("native monitor has no Docker dependency and never launches", controller.Status == "Stopped" && host.UpCalls == 0);
        await controller.Start(default);
        Check("native explicit start never launches Docker", host.UpCalls == 1 && controller.Status == "Ready");
        int before = host.InferenceProbes;
        for (int i = 0; i < 3; i++) await controller.Monitor(default);
        Check("monitor GET-only never inference or resize or sync", host.InferenceProbes == before && host.FreeCalls == 1 && host.UpCalls == 1 && host.SyncCalls == 1);
        host.Free = 1;
        await controller.Start(default);
        Check("owned loaded adoption does not count its own VRAM", controller.Status == "Ready" && host.FreeCalls == 1 && host.UpCalls == 1 && host.InferenceProbes == before + 1);
        await controller.Stop(default);
        Check("stop verifies stopped", host.StopCalls == 1 && controller.Status == "Stopped");
        var transientHost = new TestHost();
        int inspections = 0;
        transientHost.InspectHook = () => inspections++ switch
        {
            0 => new(false,false,0),
            1 => new(false,false,0),
            2 => new(true,true,0),
            _ => transientHost.State
        };
        var transientController = new Controller(transientHost);
        await transientController.Start(default);
        Check($"transient ownership observation during load is retried (status={transientController.Status}, stops={transientHost.StopCalls}, inspections={inspections})",transientController.Status == "Ready" && transientHost.StopCalls == 0);
        host.Health = new(true); host.State = new(false, false, 0);
        await controller.Start(default);
        Check("unknown listener cannot be adopted or killed", controller.Status.StartsWith("Error:") && host.UpCalls == 1 && host.StopCalls == 1);
        await controller.Stop(default);
        Check("unknown responding endpoint never falsely stopped", controller.Status.StartsWith("Error:") && host.StopCalls == 1);
        host.Health = new(false); host.ForeignListener = true;
        await controller.Stop(default);
        Check("unknown non-ready raw listener cannot be reported stopped", controller.Status.StartsWith("Error:") && host.StopCalls == 1);
        host.ForeignListener = false;
        await controller.Start(default);
        Check("low VRAM fails before runtime and preserves apps", host.UpCalls == 1 && controller.Status.Contains("VRAM") && controller.Status.Contains("Ollama"));
        host.Free = 32000; host.BecomesReady = false;
        using var cancel = new CancellationTokenSource();
        host.DelayHook = token => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await controller.Start(cancel.Token);
        Check("cancelled owned failed load is stopped and GPU released", !host.State.Running && host.StopCalls == 2 && controller.Status.Contains("cancel"));
        host.DelayHook = null; host.BecomesReady = true;
        await controller.Start(default);
        Check("reload samples a fresh GPU budget", host.FreeCalls == 4 && host.UpCalls == 3 && controller.Status == "Ready");
        host.Health = new(false, true);
        await controller.Monitor(default);
        Check("monitor mismatched model reports error", controller.Status.StartsWith("Error: model identity mismatch"));
        await controller.Stop(default);
    }
    static async Task SessionLifecycle()
    {
        var host = new TestHost();
        var controller = new Controller(host);
        var supervisor = new Supervisor(controller, sessions: host);
        await supervisor.EnsureReady(default);
        Check("a load the tray started restores the saved conversation", host.SessionEvents.SequenceEqual(new[] { "restore-while-running" }));
        supervisor.IdleTimeout = TimeSpan.FromMilliseconds(1); await Task.Delay(20); await supervisor.IdleTick(default);
        Check("idle unload saves the conversation before stopping the engine", host.SessionEvents.Last() == "save-while-running" && !host.State.Running);
        var adopted = new TestHost { State = new(true, true, 0), Health = new(true) };
        var adoptedSupervisor = new Supervisor(new Controller(adopted), sessions: adopted);
        await adoptedSupervisor.EnsureReady(default);
        Check("an adopted, already-running engine is never overwritten by a saved session", adopted.RestoreCalls == 0 && adoptedSupervisor.IsReady);
        await adoptedSupervisor.Shutdown(default);
        Check("quitting saves the conversation before unloading", adopted.SessionEvents.SequenceEqual(new[] { "save-while-running" }) && !adopted.State.Running);
    }
    public static int Run()
    {
        Results.Clear(); int result = 0;
        try
        {
            Check("native truthful Q5 model identity", Policy.Model == "qwen3.8-27b-uncensored-q5_k_m");
            Check("stopped engine reports Stopped", Policy.Classify(false,false,false,0) == "Stopped");
            Check("loading remains Loading", Policy.Classify(true,false,false,0) == "Loading model");
            Check("Ready requires identity and inference", Policy.Classify(true,true,false,0) == "Ready");
            Lifecycle().GetAwaiter().GetResult();
            SessionLifecycle().GetAwaiter().GetResult();
            BoundaryTests.Run().GetAwaiter().GetResult();
            GatewayTests.Run().GetAwaiter().GetResult();
            Check("tray launch only listens; it does not load the model", !UiSpec.StartsModelOnLaunch);
            Check("quitting the tray unloads the model it serves", UiSpec.StopsModelOnExit);
            Check("tray menu", UiSpec.MenuLabels.SequenceEqual(new[] {"Local Qwen","Load now","Context","Model","Reasoning","Unload when idle","Start with Windows","Copy API endpoint","Copy API key","Open log","Open config folder","Quit Local Qwen"}));
            Check("default choice is Q5_K_M at 128K and settings fall back safely", LaunchChoice.Default == new LaunchChoice("q5_K_M", 131072) && new AppSettings { ContextTokens = 999, Variant = "bogus" }.Choice == LaunchChoice.Default);
            Check("gateway owns the public port; engine is private", Policy.PublicPort == 8000 && Policy.BackendPort != Policy.PublicPort);
            Check("default auto-unload is 5 minutes and Never is offered", Policy.DefaultIdleMinutes == 5 && Policy.IdleChoices.Contains(null) && new AppSettings().IdleMinutes == 5);
            Check("icon renders for every state", Enum.GetValues<TrayState>().All(s => { using var b = TrayIcons.Render(16, s); return b.Width == 16; }));
            var mutexName = "Local\\LocalQwenTray.Test." + Environment.ProcessId;
            using var first = new SingleInstance(mutexName);
            using var second = new SingleInstance(mutexName);
            Check("duplicate tray rejected", first.IsFirst && !second.IsFirst);
            Check("private logs", new SafeLog("test").Path == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LocalQwen","tray.log") && new SafeLog("test", "X:\\t").Path == Path.Combine("X:\\t", "tray.log"));
            Check("default launch tray only", Entry.Mode([]) == "tray");
            Check("explicit CLI modes", Entry.Mode(["--start"]) == "--start" && Entry.Mode(["--stop"]) == "--stop" && Entry.Mode(["--write-icon", "x.ico"]) == "--write-icon");
        }
        catch (Exception ex) { Results.Add(ex.Message); result = 1; }
        string report = string.Join(Environment.NewLine,Results);
        Console.WriteLine(report);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"self-test-results.txt"),report);
        return result;
    }
}

