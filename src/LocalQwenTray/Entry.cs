namespace LocalQwenTray;
internal static class Entry
{
    public static string Mode(string[] args)
    {
        string mode = "tray";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--write-icon") { if (++i >= args.Length) throw new ArgumentException("--write-icon requires a path"); mode = "--write-icon"; continue; }
            if (args[i] == "--ui-preview") { if (++i >= args.Length) throw new ArgumentException("--ui-preview requires a folder"); mode = "--ui-preview"; continue; }
            if (args[i] is not ("--start" or "--stop" or "--status" or "--self-test" or "--process-test" or "--ui-smoke-test")) throw new ArgumentException("Unknown argument: " + args[i]);
            if (mode != "tray") throw new ArgumentException("Choose only one command mode");
            mode = args[i];
        }
        return mode;
    }
}
internal static class UiSmokeTests
{
    // Renders the tray icons and the status panel (sleeping and ready) to PNGs, using an isolated test backend.
    public static int Preview(string dir)
    {
        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory(dir);
        foreach (var s in Enum.GetValues<TrayState>()) { using var b = TrayIcons.Render(64, s); b.Save(Path.Combine(dir, $"icon-{s}.png")); }
        var host = new TestHost();
        var supervisor = new Supervisor(new Controller(host));
        using var context = new TrayContext(supervisor, host, new SafeLog("preview", Path.Combine(dir, "state")), new AppSettings(), Path.Combine(dir, "state"), null, () => true, _ => { });
        void Shot(string name)
        {
            context.TogglePanel();
            var panel = Application.OpenForms.OfType<StatusPanel>().Single();
            panel.Location = new Point(-4000, -4000); panel.UpdateView(); Application.DoEvents();
            using var bmp = new Bitmap(panel.Width, panel.Height);
            panel.DrawToBitmap(bmp, new Rectangle(Point.Empty, panel.Size));
            bmp.Save(Path.Combine(dir, $"panel-{name}.png"));
            context.TogglePanel();
        }
        Shot("sleeping");
        supervisor.LoadNow(default).GetAwaiter().GetResult();
        using (supervisor.BeginRequest()) { }
        supervisor.RecordReply(512, 168.4);
        Shot("ready");
        context.ExitThread();
        return 0;
    }
    static ToolStripMenuItem Item(TrayContext c, string text) => c.Menu.Items.OfType<ToolStripMenuItem>().Single(x => x.Text == text);
    static async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        if (!condition()) throw new Exception("Timed out waiting for: " + what);
    }
    public static int Run()
    {
        ApplicationConfiguration.Initialize();
        var dir = Path.Combine(Path.GetTempPath(), "localqwen-ui-" + Guid.NewGuid());
        var host = new TestHost();
        var controller = new Controller(host);
        var supervisor = new Supervisor(controller);
        var settings = new AppSettings();
        bool autostart = false;
        using var context = new TrayContext(supervisor, host, new SafeLog("test-secret-value", dir), settings, dir, null, () => autostart, v => autostart = v, () => ["q5_K_M", "q4_K_M"]);
        using var timer = new System.Windows.Forms.Timer { Interval = 300 };
        int result = 0;
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                if (Application.OpenForms.Count != 0 || host.UpCalls != 0) throw new Exception("Startup created a window or loaded the model");
                Console.WriteLine("PASS real WinForms startup shows no window and loads nothing");
                var labels = context.Menu.Items.OfType<ToolStripMenuItem>().Select(x => x.Text).ToArray();
                if (!labels.Skip(1).SequenceEqual(UiSpec.MenuLabels.Skip(1))) throw new Exception("Menu mismatch: " + string.Join("|", labels));
                Console.WriteLine("PASS real tray menu labels");
                Item(context, "Load now").PerformClick();
                await Until(() => controller.Status == "Ready" && host.UpCalls == 1, "Load now");
                if (context.State != TrayState.Ready) throw new Exception("Tray state not Ready after load");
                Console.WriteLine("PASS Load now loads through the supervisor");
                context.RefreshView();
                Item(context, "Unload now").PerformClick();
                await Until(() => controller.Status == "Stopped" && host.StopCalls == 1, "Unload now");
                Console.WriteLine("PASS Unload now unloads and the menu toggles back");
                context.RefreshView();
                var contextMenu = Item(context, "Context");
                contextMenu.DropDownItems.OfType<ToolStripMenuItem>().Single(x => (int)x.Tag! == Policy.Ctx256).PerformClick();
                if (settings.ContextTokens != Policy.Ctx256 || host.Choice.Context != Policy.Ctx256 || AppSettings.Load(dir).ContextTokens != Policy.Ctx256) throw new Exception("256K choice not applied and saved");
                Item(context, "Model").DropDownItems.OfType<ToolStripMenuItem>().Single(x => (string)x.Tag! == "q4_K_M").PerformClick();
                if (host.Choice != new LaunchChoice("q4_K_M", Policy.Ctx256, 2)) throw new Exception("Q4 choice not applied");
                Item(context, "Load now").PerformClick();
                await Until(() => controller.Status == "Ready" && host.UpCalls == 2, "load with new choice");
                if (host.LastContext != Policy.Ctx256) throw new Exception("256K not used when VRAM allows");
                Console.WriteLine("PASS context and model choices apply to the next load and persist");
                int stops = host.StopCalls;
                context.RefreshView();
                Item(context, "Context").DropDownItems.OfType<ToolStripMenuItem>().Single(x => (int)x.Tag! == Policy.Ctx128).PerformClick();
                await Until(() => host.StopCalls == stops + 1 && controller.Status == "Stopped", "unload after choice change");
                Console.WriteLine("PASS changing the choice while loaded and idle unloads so the next request reloads it");
                host.Free = Policy.Need(Policy.Ctx256, 0) + Policy.HeadroomMiB - 1;
                Item(context, "Context").DropDownItems.OfType<ToolStripMenuItem>().Single(x => (int)x.Tag! == Policy.Ctx256).PerformClick();
                Item(context, "Model").DropDownItems.OfType<ToolStripMenuItem>().Single(x => (string)x.Tag! == "q5_K_M").PerformClick();
                await supervisor.LoadNow(default);
                if (host.LastContext != Policy.Ctx128 || !supervisor.IsReady) throw new Exception("256K should fall back to 128K when VRAM is short");
                Console.WriteLine("PASS 256K falls back to 128K when free VRAM is short");
                await supervisor.Unload(default); host.Free = 32000;
                Item(context, "Reasoning").DropDownItems.OfType<ToolStripMenuItem>().Single(x => (string)x.Tag! == "xhigh").PerformClick();
                if (settings.Reasoning != "xhigh" || AppSettings.Load(dir).Reasoning != "xhigh") throw new Exception("Reasoning choice not applied and saved");
                if (host.UpCalls != 3 || supervisor.IsReady) throw new Exception("Changing reasoning must not load or reload the model");
                Item(context, "Reasoning").DropDownItems.OfType<ToolStripMenuItem>().Single(x => (string)x.Tag! == "low").PerformClick();
                Console.WriteLine("PASS reasoning choice applies without reloading and persists");
                Item(context, "Parallel requests").DropDownItems.OfType<ToolStripMenuItem>().Single(x => (int)x.Tag! == 1).PerformClick();
                if (settings.ParallelRequests != 1 || host.Choice.Slots != 1 || AppSettings.Load(dir).ParallelRequests != 1) throw new Exception("Parallel choice not applied and saved");
                Item(context, "Parallel requests").DropDownItems.OfType<ToolStripMenuItem>().Single(x => (int)x.Tag! == 2).PerformClick();
                Console.WriteLine("PASS parallel-requests choice applies and persists");
                var idle = Item(context, UiSpec.MenuLabels[6]);
                idle.DropDownItems.OfType<ToolStripMenuItem>().Single(x => x.Text == "After 15 min").PerformClick();
                if (settings.IdleMinutes != 15 || supervisor.IdleTimeout != TimeSpan.FromMinutes(15) || AppSettings.Load(dir).IdleMinutes != 15) throw new Exception("Idle choice not applied and saved");
                idle.DropDownItems.OfType<ToolStripMenuItem>().Single(x => x.Text == "Never").PerformClick();
                if (supervisor.IdleTimeout is not null || AppSettings.Load(dir).IdleMinutes is not null) throw new Exception("Never not applied");
                Console.WriteLine("PASS auto-unload choice applies immediately and persists");
                Item(context, UiSpec.MenuLabels[7]).PerformClick();
                if (!autostart) throw new Exception("Start with Windows toggle not applied");
                Console.WriteLine("PASS Start with Windows toggle");
                context.TogglePanel();
                await Task.Delay(100);
                var panel = Application.OpenForms.OfType<StatusPanel>().SingleOrDefault();
                if (panel is not { Visible: true }) throw new Exception("Status panel did not open");
                context.TogglePanel();
                Console.WriteLine("PASS status panel opens and closes");
                host.DelayHook = token => Task.Delay(Timeout.InfiniteTimeSpan, token);
                Item(context, "Load now").PerformClick();
                await Until(() => controller.Status == "Loading model", "pending load");
                await Task.Delay(150);
                if (controller.Status != "Loading model" || context.State != TrayState.Loading) throw new Exception("Pending load should stay Loading");
                Console.WriteLine("PASS UI stays responsive during a pending load");
                host.StopHook = token => Task.Delay(150, token); // a realistic, non-instant engine exit
                context.Quit();
            }
            catch (Exception ex) { Console.WriteLine("FAIL " + ex.Message); result = 1; context.ExitThread(); }
        };
        timer.Start(); Application.Run(context);
        if (host.State.Running) { Console.WriteLine("FAIL quitting during a load must clean up the half-started engine"); result = 1; }
        else Console.WriteLine("PASS quit during a pending load cleans up the half-started engine");
        if (result == 0)
        {
            var loadedHost = new TestHost { State = new(true, true, 0), Health = new(true) };
            var loadedSupervisor = new Supervisor(new Controller(loadedHost));
            using var loadedContext = new TrayContext(loadedSupervisor, loadedHost, new SafeLog("test-secret-value", dir), new AppSettings(), dir, null, () => false, _ => { });
            using var exitTimer = new System.Windows.Forms.Timer { Interval = 200 };
            exitTimer.Tick += (_, _) => { exitTimer.Stop(); Item(loadedContext, UiSpec.MenuLabels[12]).PerformClick(); };
            exitTimer.Start(); Application.Run(loadedContext);
            if (loadedHost.StopCalls != 1 || loadedHost.State.Running) { Console.WriteLine("FAIL quitting a loaded tray must unload the model"); result = 1; }
            else Console.WriteLine("PASS quitting the tray unloads a loaded model");
        }
        try { Directory.Delete(dir, true); } catch (IOException) { }
        return result;
    }
}
