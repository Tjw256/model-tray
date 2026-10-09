using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace LocalQwenTray;
internal sealed record Palette(Color Back, Color Card, Color Text, Color Muted, Color Border, Color Accent)
{
    public static bool SystemUsesLight()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is not int v || v != 0; }
        catch (Exception) { return true; }
    }
    public static Palette Current() => SystemUsesLight()
        ? new(Color.FromArgb(249, 250, 251), Color.White, Color.FromArgb(17, 24, 39), Color.FromArgb(107, 114, 128), Color.FromArgb(229, 231, 235), Color.FromArgb(109, 90, 230))
        : new(Color.FromArgb(28, 28, 32), Color.FromArgb(39, 39, 45), Color.FromArgb(243, 244, 246), Color.FromArgb(156, 163, 175), Color.FromArgb(63, 63, 70), Color.FromArgb(124, 99, 246));
}
// Compact flyout above the tray: what Local Qwen is doing, and the few things you can change.
// Built from auto-sizing layout panels so it stays correct at any display scaling.
internal sealed class StatusPanel : Form
{
    readonly TrayActions actions;
    readonly Palette p = Palette.Current();
    readonly Label stateTitle = new(), stateDetail = new(), lastReply = new();
    readonly Panel dot = new();
    readonly Button primary = new(), logs = new(), copy = new();
    readonly ComboBox idle = new(), contextBox = new(), modelBox = new(), reasoningBox = new();
    readonly Label modelHint = new();
    readonly CheckBox autostart = new();
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    readonly string endpoint = $"http://127.0.0.1:{Policy.PublicPort}/v1";
    bool updating;
    int D(int logical) => LogicalToDeviceUnits(logical);
    public StatusPanel(TrayActions actions)
    {
        this.actions = actions;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Font; Font = new Font("Segoe UI", 9.5f);
        BackColor = p.Back; ForeColor = p.Text; KeyPreview = true; Text = "Local Qwen";
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var root = Column(); root.Padding = new Padding(D(16)); root.MinimumSize = new Size(D(400), 0); root.MaximumSize = new Size(D(400), 0);

        // Header: icon + title + subtitle
        var header = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, D(12)) };
        var logo = new PictureBox { Image = TrayIcons.Render(D(40), null), Size = new Size(D(40), D(40)), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, D(10), 0) };
        var titles = Column();
        titles.Controls.Add(Lbl("Local Qwen", new Font("Segoe UI Semibold", 13f), p.Text));
        titles.Controls.Add(Lbl("Qwen3.8 27B · vision · MTP", Font, p.Muted));
        header.Controls.Add(logo, 0, 0); header.Controls.Add(titles, 1, 0);
        root.Controls.Add(header);

        // Status card
        var card = new RoundedPanel(p.Card, p.Border) { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(D(14), D(10), D(14), D(10)), Margin = new Padding(0, 0, 0, D(12)), Dock = DockStyle.Fill };
        var cardGrid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, BackColor = p.Card, Dock = DockStyle.Fill };
        dot.Size = new Size(D(11), D(11)); dot.Margin = new Padding(0, D(7), D(10), 0); dot.BackColor = p.Card; dot.Tag = p.Muted;
        dot.Paint += (_, e) => { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using var b = new SolidBrush((Color)dot.Tag!); e.Graphics.FillEllipse(b, 0, 0, dot.Width - 1, dot.Height - 1); };
        stateTitle.Font = new Font("Segoe UI Semibold", 11f); stateTitle.AutoSize = true; stateTitle.BackColor = p.Card; stateTitle.Margin = Padding.Empty;
        stateDetail.ForeColor = p.Muted; stateDetail.AutoSize = true; stateDetail.BackColor = p.Card; stateDetail.MaximumSize = new Size(D(270), 0); stateDetail.Margin = new Padding(0, D(2), 0, 0);
        cardGrid.Controls.Add(dot, 0, 0); cardGrid.Controls.Add(stateTitle, 1, 0); cardGrid.Controls.Add(stateDetail, 1, 1);
        card.Controls.Add(cardGrid);
        root.Controls.Add(card);

        // Details
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, D(8)) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void Row(string label, Control value, Control? extra = null)
        {
            int r = grid.RowCount++;
            var l = Lbl(label, Font, p.Muted); l.Margin = new Padding(0, D(5), D(14), D(5));
            value.Margin = new Padding(0, D(5), 0, D(5)); value.Anchor = AnchorStyles.Left;
            grid.Controls.Add(l, 0, r); grid.Controls.Add(value, 1, r);
            if (extra is not null) grid.Controls.Add(extra, 2, r);
        }
        Style(copy, false, "Copy"); copy.AutoSize = true; copy.Margin = new Padding(D(6), D(1), 0, D(1));
        copy.Click += (_, _) => { Clipboard.SetText(endpoint); copy.Text = "Copied"; };
        Row("Endpoint", Lbl(endpoint, Font, p.Text), copy);
        Combo(contextBox); foreach (var c in Policy.ContextChoices) contextBox.Items.Add($"{Policy.ContextLabel(c)} tokens");
        contextBox.SelectedIndexChanged += (_, _) => { if (!updating) actions.SetContext(Policy.ContextChoices[contextBox.SelectedIndex]); };
        Row("Context", contextBox);
        Combo(modelBox);
        modelBox.SelectedIndexChanged += (_, _) => { if (!updating && modelBox.SelectedItem is VariantItem v) actions.SetVariant(v.Variant); };
        Row("Model", modelBox);
        modelHint.AutoSize = true; modelHint.ForeColor = p.Muted; modelHint.MaximumSize = new Size(D(250), 0);
        Row("", modelHint);
        Combo(reasoningBox); foreach (var r in Policy.ReasoningChoices) reasoningBox.Items.Add(UiSpec.ReasoningText(r));
        reasoningBox.SelectedIndexChanged += (_, _) => { if (!updating) actions.SetReasoning(Policy.ReasoningChoices[reasoningBox.SelectedIndex]); };
        Row("Reasoning", reasoningBox);
        lastReply.AutoSize = true; Row("Last reply", lastReply);
        Combo(idle);
        foreach (var m in Policy.IdleChoices) idle.Items.Add(IdleText(m));
        idle.SelectedIndexChanged += (_, _) => { if (!updating) actions.SetIdle(Policy.IdleChoices[idle.SelectedIndex]); };
        Row("Unload when idle", idle);
        root.Controls.Add(grid);

        autostart.Text = "Start with Windows"; autostart.AutoSize = true; autostart.ForeColor = p.Text; autostart.Margin = new Padding(0, 0, 0, D(14));
        autostart.CheckedChanged += (_, _) => { if (!updating) actions.SetAutostart(autostart.Checked); };
        root.Controls.Add(autostart);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        Style(primary, true, "Load now"); primary.MinimumSize = new Size(D(190), D(34)); primary.AutoSize = true; primary.Margin = new Padding(0, 0, D(8), 0);
        primary.Click += async (_, _) => { primary.Enabled = false; try { await (actions.State is TrayState.Ready or TrayState.Busy ? actions.Unload() : actions.Load()); } finally { if (!IsDisposed) primary.Enabled = true; } };
        Style(logs, false, "Open log"); logs.MinimumSize = new Size(D(110), D(34)); logs.AutoSize = true; logs.Margin = Padding.Empty;
        logs.Click += (_, _) => actions.OpenLog();
        buttons.Controls.AddRange([primary, logs]);
        root.Controls.Add(buttons);

        Controls.Add(root);
        refresh.Tick += (_, _) => UpdateView();
        Deactivate += (_, _) => Hide();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
    }
    void Combo(ComboBox b) { b.DropDownStyle = ComboBoxStyle.DropDownList; b.FlatStyle = FlatStyle.Flat; b.BackColor = p.Card; b.ForeColor = p.Text; b.Width = D(175); }
    sealed record VariantItem(string Variant) { public override string ToString() => OllamaModels.Describe(Variant); }
    static TableLayoutPanel Column() => new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
    static Label Lbl(string text, Font font, Color color) => new() { Text = text, Font = font, ForeColor = color, AutoSize = true, Margin = Padding.Empty };
    static string IdleText(int? m) => m switch { null => "Never (keep loaded)", 1 => "After 1 minute", 60 => "After 1 hour", _ => $"After {m} minutes" };
    void Style(Button b, bool accent, string text)
    {
        b.Text = text; b.FlatStyle = FlatStyle.Flat; b.Cursor = Cursors.Hand; b.UseVisualStyleBackColor = false;
        b.BackColor = accent ? p.Accent : p.Card; b.ForeColor = accent ? Color.White : p.Text;
        b.FlatAppearance.BorderSize = accent ? 0 : 1; b.FlatAppearance.BorderColor = p.Border;
        b.Padding = new Padding(D(8), D(2), D(8), D(2));
        if (accent) b.Font = new Font("Segoe UI Semibold", 9.5f);
    }
    public void ShowNearTray()
    {
        UpdateView();
        copy.Text = "Copy";
        if (!IsHandleCreated) CreateControl();
        PerformLayout();
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - D(12), area.Bottom - Height - D(12));
        Show(); Activate(); refresh.Start();
    }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) refresh.Stop(); }
    public void UpdateView()
    {
        if (IsDisposed) return;
        updating = true;
        try
        {
            var s = actions.State;
            dot.Tag = TrayIcons.StateColor(s); dot.Invalidate();
            (stateTitle.Text, stateDetail.Text) = Describe(s);
            var r = actions.LastReply;
            lastReply.Text = r is null ? "—" : $"{r.TokensPerSecond:F0} tok/s · {r.Tokens} tokens · {Ago(DateTimeOffset.Now - r.At)}";
            idle.SelectedIndex = Array.IndexOf(Policy.IdleChoices, actions.IdleMinutes) is var i and >= 0 ? i : 1;
            autostart.Checked = actions.Autostart;
            var choice = actions.Choice;
            contextBox.SelectedIndex = Math.Max(0, Array.IndexOf(Policy.ContextChoices, choice.Context));
            reasoningBox.SelectedIndex = Math.Max(0, Array.IndexOf(Policy.ReasoningChoices, actions.Reasoning));
            var installed = actions.InstalledVariants;
            if (!modelBox.Items.Cast<VariantItem>().Select(x => x.Variant).SequenceEqual(installed))
            { modelBox.Items.Clear(); foreach (var v in installed) modelBox.Items.Add(new VariantItem(v)); }
            modelBox.SelectedIndex = Math.Max(0, installed.ToList().IndexOf(choice.Variant));
            var missing = Policy.Variants.Except(installed).ToArray();
            modelHint.Text = missing.Length == 0 ? "Changes apply on the next load" : $"{string.Join(", ", missing.Select(m => m.ToUpperInvariant()))} not downloaded yet";
            primary.Text = s is TrayState.Ready or TrayState.Busy ? "Unload now" : s == TrayState.Loading ? "Loading…" : "Load now";
            primary.Enabled = s != TrayState.Loading;
        }
        finally { updating = false; }
    }
    (string, string) Describe(TrayState s) => s switch
    {
        TrayState.Sleeping => ("Sleeping", "Loads automatically on the next request"),
        TrayState.Loading => ("Loading model…", "Requests wait and continue when it's ready"),
        TrayState.Busy => ("Generating", Loaded() + (actions.InFlight == 1 ? " · 1 request" : $" · {actions.InFlight} requests")),
        TrayState.Ready => ("Ready", Loaded() + (actions.UnloadsIn is TimeSpan t ? $" · unloads in {Span(t)} if idle" : " · stays loaded")),
        _ => ("Needs attention", actions.ErrorText),
    };
    // What is running, and why it differs from the choice (256K falls back to 128K when VRAM is short).
    string Loaded()
    {
        if (actions.Loaded is not { } l) return "";
        string text = $"{l.Variant.ToUpperInvariant()} · {Policy.ContextLabel(l.Context)}";
        if (l.Context < actions.Choice.Context) text += $" (not enough free VRAM for {Policy.ContextLabel(actions.Choice.Context)})";
        else if (l.Variant != actions.Choice.Variant || l.Context != actions.Choice.Context) text += " (new choice applies on next load)";
        return text;
    }
    static string Span(TimeSpan t) => t.TotalMinutes >= 1 ? $"{Math.Ceiling(t.TotalMinutes):F0} min" : $"{Math.Max(1, (int)t.TotalSeconds)} s";
    static string Ago(TimeSpan t) => t.TotalSeconds < 60 ? "just now" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min ago" : $"{(int)t.TotalHours} h ago";

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); // Windows 11 rounded corners
    }
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } } // WS_EX_TOOLWINDOW: no Alt-Tab entry
    protected override void Dispose(bool disposing) { if (disposing) refresh.Dispose(); base.Dispose(disposing); }

    sealed class RoundedPanel : Panel
    {
        readonly Color fill, border;
        public RoundedPanel(Color fill, Color border) { this.fill = fill; this.border = border; DoubleBuffered = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? fill);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1); int d = LogicalToDeviceUnits(14);
            using var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90); path.CloseFigure();
            using var b = new SolidBrush(fill); using var pen = new Pen(border);
            e.Graphics.FillPath(b, path); e.Graphics.DrawPath(pen, path);
        }
    }
}
