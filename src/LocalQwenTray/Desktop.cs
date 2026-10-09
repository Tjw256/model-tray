using System.Diagnostics;
using Microsoft.Win32;
namespace LocalQwenTray;
internal sealed class SingleInstance : IDisposable
{
    readonly Mutex mutex;
    public bool IsFirst { get; }
    public SingleInstance(string name) { mutex = new Mutex(false, name, out bool created); IsFirst = created; }
    public void Dispose() => mutex.Dispose();
}
internal static class UiSpec
{
    // Menu as shown while the model is asleep (the load item reads "Unload now" while loaded).
    public static readonly string[] MenuLabels = ["Local Qwen", "Load now", "Context", "Model", "Unload when idle", "Start with Windows", "Copy API endpoint", "Copy API key", "Open log", "Open config folder", "Quit Local Qwen"];
    public static bool StartsModelOnLaunch => false;   // the tray only listens; the first request loads the model
    public static bool StopsModelOnExit => true;       // without the gateway the model is unreachable, so quitting unloads it
}
internal static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "LocalQwenTray";
    public static bool IsEnabled() { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(Name) is string; }
    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(Name, "\"" + Environment.ProcessPath + "\"");
        else k.DeleteValue(Name, false);
    }
}
// What the status panel and menu can see and do.
internal interface TrayActions
{
    TrayState State { get; }
    int InFlight { get; }
    TimeSpan? UnloadsIn { get; }
    ReplyStats? LastReply { get; }
    string ErrorText { get; }
    int? IdleMinutes { get; }
    bool Autostart { get; }
    LaunchChoice Choice { get; }                        // used by the next load
    (string Variant, int Context)? Loaded { get; }      // what is running now
    IReadOnlyList<string> InstalledVariants { get; }
    void SetContext(int tokens);
    void SetVariant(string variant);
    void SetIdle(int? minutes);
    void SetAutostart(bool enabled);
    Task Load();
    Task Unload();
    void OpenLog();
}
internal sealed class TrayContext : ApplicationContext, TrayActions
{
    readonly Supervisor supervisor;
    readonly IQwenHost host;
    readonly SafeLog log;
    readonly AppSettings settings;
    readonly string stateDirectory;
    readonly Func<Task<string?>>? ensureGateway;
    readonly Func<bool> getAutostart;
    readonly Action<bool> setAutostart;
    readonly Func<IReadOnlyList<string>> installedVariants;
    readonly NotifyIcon icon;
    readonly ContextMenuStrip menu = new();
    internal ContextMenuStrip Menu => menu;
    readonly Dictionary<TrayState, Icon> icons = [];
    readonly System.Windows.Forms.Timer timer = new() { Interval = 15000 };
    readonly CancellationTokenSource lifetime = new();
    readonly SynchronizationContext ui;
    readonly ToolStripMenuItem header, loadUnload, contextMenu, modelMenu, idleMenu, autostartItem;
    StatusPanel? panel;
    TrayState? shownState;
    string? gatewayError, lastBalloon;
    bool exiting;
    internal Task? Quitting { get; private set; }

    public TrayContext(Supervisor supervisor, IQwenHost host, SafeLog log, AppSettings settings, string stateDirectory,
        Func<Task<string?>>? ensureGateway = null, Func<bool>? getAutostart = null, Action<bool>? setAutostart = null, Func<IReadOnlyList<string>>? installedVariants = null, string? apiKey = null)
    {
        this.supervisor = supervisor; this.host = host; this.log = log; this.settings = settings; this.stateDirectory = stateDirectory;
        this.ensureGateway = ensureGateway;
        this.getAutostart = getAutostart ?? StartupRegistration.IsEnabled;
        this.setAutostart = setAutostart ?? StartupRegistration.Set;
        this.installedVariants = installedVariants ?? (() => OllamaModels.Tags());
        host.Choice = settings.Choice;
        header = new(UiSpec.MenuLabels[0]) { Enabled = false, Font = new Font(menu.Font, FontStyle.Bold) };
        loadUnload = new(UiSpec.MenuLabels[1]);
        contextMenu = new(UiSpec.MenuLabels[2]);
        foreach (var tokens in Policy.ContextChoices)
        {
            var item = new ToolStripMenuItem($"{Policy.ContextLabel(tokens)} tokens" + (tokens == Policy.Ctx256 ? " (needs ~29.4 GB free VRAM)" : "")) { Tag = tokens };
            item.Click += (_, _) => SetContext((int)item.Tag!);
            contextMenu.DropDownItems.Add(item);
        }
        modelMenu = new(UiSpec.MenuLabels[3]);
        idleMenu = new(UiSpec.MenuLabels[4]);
        foreach (var m in Policy.IdleChoices)
        {
            var item = new ToolStripMenuItem(m is null ? "Never" : m == 60 ? "After 1 hour" : $"After {m} min") { Tag = m };
            item.Click += (_, _) => SetIdle((int?)item.Tag);
            idleMenu.DropDownItems.Add(item);
        }
        autostartItem = new(UiSpec.MenuLabels[5]);
        var copy = new ToolStripMenuItem(UiSpec.MenuLabels[6]);
        var copyKey = new ToolStripMenuItem(UiSpec.MenuLabels[7]) { Enabled = apiKey is not null };
        var openLog = new ToolStripMenuItem(UiSpec.MenuLabels[8]);
        var openConfig = new ToolStripMenuItem(UiSpec.MenuLabels[9]);
        var quit = new ToolStripMenuItem(UiSpec.MenuLabels[10]);
        menu.Items.AddRange([header, new ToolStripSeparator(), loadUnload, contextMenu, modelMenu, idleMenu, autostartItem, new ToolStripSeparator(), copy, copyKey, openLog, openConfig, new ToolStripSeparator(), quit]);
        copyKey.Click += (_, _) => { if (apiKey is not null) Clipboard.SetText(apiKey); };
        openConfig.Click += (_, _) => { Directory.CreateDirectory(stateDirectory); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + stateDirectory + "\"") { UseShellExecute = false })?.Dispose(); };
        loadUnload.Click += async (_, _) => await (supervisor.IsReady ? Unload() : Load());
        autostartItem.Click += (_, _) => SetAutostart(!autostartItem.Checked);
        copy.Click += (_, _) => Clipboard.SetText($"http://127.0.0.1:{Policy.PublicPort}/v1");
        openLog.Click += (_, _) => OpenLog();
        quit.Click += (_, _) => Quit();
        menu.Opening += (_, _) => RefreshView();
        _ = menu.Handle;
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        icon = new NotifyIcon { Icon = IconFor(TrayState.Sleeping), Text = "Local Qwen", ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };
        supervisor.Controller.Changed += OnStatusChanged;
        supervisor.Changed += OnChanged;
        timer.Tick += async (_, _) => await Tick();
        timer.Start();
        _ = Tick();   // observe only: adopt an already-loaded model, never load one here
        RefreshView();
    }

    // ---- TrayActions ----
    public TrayState State => gatewayError is not null ? TrayState.Error : supervisor.State;
    public int InFlight => supervisor.InFlight;
    public TimeSpan? UnloadsIn => supervisor.UnloadsIn;
    public ReplyStats? LastReply => supervisor.LastReply;
    public string ErrorText => gatewayError ?? log.Redact(supervisor.Controller.Status.StartsWith("Error: ") ? supervisor.Controller.Status[7..] : supervisor.Controller.Status);
    public int? IdleMinutes => settings.IdleMinutes;
    public bool Autostart => getAutostart();
    public LaunchChoice Choice => settings.Choice;
    public (string Variant, int Context)? Loaded => supervisor.IsReady ? host.Loaded() : null;
    public IReadOnlyList<string> InstalledVariants => installedVariants();
    public void SetContext(int tokens) => ApplyChoice(() => settings.ContextTokens = tokens);
    public void SetVariant(string variant) => ApplyChoice(() => settings.Variant = variant);
    // The choice applies to the next load. If the model is loaded and idle, unload now so the next request reloads it.
    void ApplyChoice(Action change)
    {
        var before = settings.Choice;
        change();
        host.Choice = settings.Choice;
        try { settings.Save(stateDirectory); } catch (Exception ex) { log.Write("Saving settings failed: " + ex.Message); }
        if (settings.Choice != before && supervisor.IsReady && supervisor.InFlight == 0 && host.Loaded() is { } running &&
            (running.Variant != settings.Choice.Variant || running.Context != settings.Choice.Context)) _ = Unload();
        RefreshView();
    }
    public void SetIdle(int? minutes)
    {
        settings.IdleMinutes = minutes;
        supervisor.IdleTimeout = minutes is int m ? TimeSpan.FromMinutes(m) : null;
        try { settings.Save(stateDirectory); } catch (Exception ex) { log.Write("Saving settings failed: " + ex.Message); }
        RefreshView();
    }
    public void SetAutostart(bool enabled)
    {
        try { setAutostart(enabled); } catch (Exception ex) { Report(ex); }
        RefreshView();
    }
    public async Task Load()
    {
        try { await supervisor.LoadNow(lifetime.Token); }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex) { log.Write("Load failed: " + ex.Message); } // state + balloon already show the reason
        RefreshView();
    }
    public async Task Unload()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancel.CancelAfter(TimeSpan.FromSeconds(90));
        try { await supervisor.Unload(cancel.Token); }
        catch (OperationCanceledException) { if (!exiting) Report(new TimeoutException("Unload timed out; check the log")); }
        catch (Exception ex) { Report(ex); }
        RefreshView();
    }
    public void OpenLog() => _ = ViewLogs();

    // ---- view ----
    void OnStatusChanged(string text) { log.Write(log.Redact(text)); OnChanged(); }
    void OnChanged() { if (!exiting) ui.Post(_ => RefreshView(), null); }
    Icon IconFor(TrayState s)
    {
        if (!icons.TryGetValue(s, out var i))
        {
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using var bmp = TrayIcons.Render(size, s);
            icons[s] = i = TrayIcons.ToIcon(bmp);
        }
        return i;
    }
    internal void RefreshView()
    {
        if (exiting) return;
        var s = State;
        if (shownState != s) { icon.Icon = IconFor(s); shownState = s; }
        string word = s switch { TrayState.Sleeping => "Sleeping", TrayState.Loading => "Loading…", TrayState.Ready => "Ready", TrayState.Busy => "Generating", _ => "Needs attention" };
        string tip = "Local Qwen — " + word + (s == TrayState.Sleeping ? " (loads on request)" : "");
        icon.Text = tip.Length > 63 ? tip[..63] : tip;
        if (Quitting is null) header.Text = "Local Qwen — " + word;
        loadUnload.Text = supervisor.IsReady ? "Unload now" : s == TrayState.Loading ? "Loading…" : UiSpec.MenuLabels[1];
        loadUnload.Enabled = s != TrayState.Loading;
        foreach (ToolStripMenuItem item in idleMenu.DropDownItems) item.Checked = (int?)item.Tag == settings.IdleMinutes;
        foreach (ToolStripMenuItem item in contextMenu.DropDownItems) item.Checked = (int)item.Tag! == settings.Choice.Context;
        var installed = installedVariants();
        modelMenu.DropDownItems.Clear();
        foreach (var v in Policy.Variants.Union(installed))
        {
            bool have = installed.Contains(v);
            var item = new ToolStripMenuItem(OllamaModels.Describe(v) + (have ? "" : " (not downloaded)")) { Checked = v == settings.Choice.Variant, Enabled = have, Tag = v };
            item.Click += (_, _) => SetVariant((string)item.Tag!);
            modelMenu.DropDownItems.Add(item);
        }
        autostartItem.Checked = getAutostart();
        if (s == TrayState.Error && ErrorText != lastBalloon)
        {
            lastBalloon = ErrorText;
            icon.ShowBalloonTip(8000, "Local Qwen", ErrorText[..Math.Min(240, ErrorText.Length)], ToolTipIcon.Warning);
        }
        if (s != TrayState.Error) lastBalloon = null;
        if (panel is { Visible: true }) panel.UpdateView();
    }
    internal void TogglePanel()
    {
        panel ??= new StatusPanel(this);
        if (panel.Visible) panel.Hide(); else panel.ShowNearTray();
    }
    async Task Tick()
    {
        if (exiting) return;
        try
        {
            if (ensureGateway is not null) gatewayError = await ensureGateway();
            await supervisor.Controller.Monitor(lifetime.Token);
            await supervisor.IdleTick(lifetime.Token);
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex) { log.Write("Background check failed: " + ex.Message); }
        RefreshView();
    }
    async Task ViewLogs()
    {
        try
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            cancel.CancelAfter(TimeSpan.FromSeconds(20));
            try { log.Write(await host.Diagnostics(cancel.Token)); }
            catch (Exception ex) { log.Write("Diagnostic capture failed: " + ex.Message); }
            if (!exiting)
            {
                var info = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
                info.ArgumentList.Add(log.Path);
                using var process = Process.Start(info);
            }
        }
        catch (Exception ex) { Report(ex); }
    }
    void Report(Exception ex)
    {
        log.Write(ex.ToString());
        if (!exiting) MessageBox.Show(log.Redact(ex.Message), "Local Qwen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    // Quit unloads the model first (clients cannot reach it without the gateway), then leaves the message loop.
    internal void Quit()
    {
        if (Quitting is not null) return;
        header.Text = "Local Qwen — Quitting…";
        Quitting = QuitAsync();
    }
    async Task QuitAsync()
    {
        timer.Stop();
        panel?.Hide();
        try { using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60)); await supervisor.Shutdown(cancel.Token); }
        catch (Exception ex) { log.Write("Unload during quit failed: " + ex.Message); }
        ExitThread();
    }
    protected override void ExitThreadCore()
    {
        if (exiting) return;
        exiting = true; timer.Stop(); lifetime.Cancel();
        supervisor.Controller.Changed -= OnStatusChanged;
        supervisor.Changed -= OnChanged;
        icon.Visible = false; icon.Dispose(); menu.Dispose(); timer.Dispose(); panel?.Dispose();
        foreach (var i in icons.Values) i.Dispose();
        base.ExitThreadCore();
    }
}
