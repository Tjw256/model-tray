namespace LocalQwenTray;
internal static class Program
{
    static bool PortOpen(int port)
    {
        using var client = new System.Net.Sockets.TcpClient();
        try { return client.ConnectAsync("127.0.0.1", port).Wait(500) && client.Connected; }
        catch (AggregateException) { return false; }
    }
    // Earlier tray versions ran the owned engine directly on the public port. Stop it only when our ownership
    // ledger identifies it (never an unknown listener), so the gateway can take over :8000.
    static void MigrateLegacyEngine(NativeHost host, Controller controller, SafeLog log)
    {
        if (!host.Inspect(default).GetAwaiter().GetResult().Running) return;
        if (!PortOpen(Policy.PublicPort) || PortOpen(Policy.BackendPort)) return;
        log.Write($"Stopping the engine started by the previous tray version (it listened on :{Policy.PublicPort})");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        controller.Stop(deadline.Token).GetAwaiter().GetResult();
    }
    [STAThread]
    static int Main(string[] args)
    {
        SafeLog? log = null;
        string mode = "tray";
        try
        {
            mode = Entry.Mode(args);
            if (mode == "--write-icon") { TrayIcons.WriteIco(args[Array.IndexOf(args, "--write-icon") + 1]); return 0; }
            if (mode == "--ui-preview") return UiSmokeTests.Preview(args[Array.IndexOf(args, "--ui-preview") + 1]);
            if (mode == "--self-test") return SelfTests.Run();
            if (mode == "--process-test") return RuntimeTests.Run().GetAwaiter().GetResult();
            if (mode == "--ui-smoke-test") return UiSmokeTests.Run();
            // Keep the handle alive for the entire message loop. No named-mutex ownership/reentrancy assumptions.
            using var instance = mode == "tray" ? new SingleInstance("Local\\LocalQwenTray." + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value) : null;
            if (instance is { IsFirst: false }) return 0;
            var config = AppConfig.LoadOrCreate();
            var key = config.ApiKey;
            Policy.Model = config.ModelName;
            OllamaModels.Repository = config.OllamaRepository;
            log = new SafeLog(key);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
            var host = new NativeHost(config, new ProcessRunner(), http);
            var controller = new Controller(host);
            if (mode != "tray")
            {
                controller.Changed += text => { log.Write(text); Console.WriteLine(log.Redact(text)); };
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(16));
                var operation = mode switch
                {
                    "--start" => controller.Start(deadline.Token),
                    "--stop" => controller.Stop(deadline.Token),
                    _ => controller.Monitor(deadline.Token)
                };
                operation.GetAwaiter().GetResult();
                if (controller.Status.StartsWith("Error:"))
                {
                    try { log.Write(host.Diagnostics(deadline.Token).GetAwaiter().GetResult()); }
                    catch (Exception ex) { log.Write("Diagnostic capture failed: " + ex.Message); }
                    return 1;
                }
                return mode == "--start" && !controller.Status.StartsWith("Ready") ? 1 : 0;
            }
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, eventArgs) =>
            {
                log.Write(eventArgs.Exception.ToString());
                MessageBox.Show(log.Redact(eventArgs.Exception.Message), "Local Qwen error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            var stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalQwen");
            var settings = AppSettings.Load(stateDirectory);
            host.Choice = settings.Choice;
            var supervisor = new Supervisor(controller, sessions: host) { IdleTimeout = settings.IdleMinutes is int minutes ? TimeSpan.FromMinutes(minutes) : null };
            MigrateLegacyEngine(host, controller, log);
            Gateway? gateway = null;
            var gatewayLog = log;
            async Task<string?> EnsureGateway()
            {
                if (gateway is not null) return null;
                var candidate = new Gateway(supervisor, key, Policy.PublicPort, Policy.BackendPort, stateDirectory, gatewayLog.Write, () => settings.ReasoningOrDefault);
                try { await candidate.StartAsync(); gateway = candidate; gatewayLog.Write($"Listening on http://127.0.0.1:{Policy.PublicPort}/v1 (model loads on demand)"); return null; }
                catch (Exception ex)
                {
                    await candidate.DisposeAsync();
                    return $"Port {Policy.PublicPort} is used by another program, so Local Qwen can't listen. Close it and Local Qwen will retry. ({gatewayLog.Redact(ex.Message)})";
                }
            }
            using var context = new TrayContext(supervisor, host, log, settings, stateDirectory, EnsureGateway, apiKey: key);
            Application.Run(context);
            gateway?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            var safe = log?.Redact(ex.Message) ?? ex.Message;
            try { log?.Write(ex.ToString()); } catch (Exception logError) { safe += "\nLog write failed: " + logError.Message; }
            if (mode == "tray") MessageBox.Show(safe, "Local Qwen", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else Console.Error.WriteLine(safe);
            return 1;
        }
    }
}
