using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
namespace LocalQwenTray;
// One drawing for every surface: a violet "Q" tile, plus a status dot for the tray.
internal static class TrayIcons
{
    public static readonly Color TileTop = Color.FromArgb(124, 92, 255), TileBottom = Color.FromArgb(59, 130, 246);
    public static Color StateColor(TrayState s) => s switch
    {
        TrayState.Sleeping => Color.FromArgb(156, 163, 175),
        TrayState.Loading => Color.FromArgb(245, 158, 11),
        TrayState.Ready => Color.FromArgb(34, 197, 94),
        TrayState.Busy => Color.FromArgb(56, 189, 248),
        _ => Color.FromArgb(239, 68, 68),
    };
    public static Bitmap Render(int size, TrayState? state)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float s = size, pad = s * 0.04f;
        var tile = new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad);
        using (var path = Rounded(tile, s * 0.24f))
        using (var fill = new LinearGradientBrush(tile, TileTop, TileBottom, LinearGradientMode.ForwardDiagonal))
            g.FillPath(fill, path);
        // "Q": a ring with a short tail, drawn with pens so it stays crisp at 16 px.
        float stroke = Math.Max(1.6f, s * 0.115f);
        using (var pen = new Pen(Color.White, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float r = s * 0.235f, cx = s * 0.48f, cy = s * 0.47f;
            g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
            g.DrawLine(pen, s * 0.58f, s * 0.58f, s * 0.75f, s * 0.75f);
        }
        if (state is TrayState st)
        {
            float d = Math.Max(5f, s * 0.36f), x = s - d - s * 0.02f, y = s - d - s * 0.02f, ring = Math.Max(1f, s * 0.06f);
            using var back = new SolidBrush(Color.FromArgb(24, 24, 27));
            using var dot = new SolidBrush(StateColor(st));
            g.FillEllipse(back, x - ring, y - ring, d + 2 * ring, d + 2 * ring);
            g.FillEllipse(dot, x, y, d, d);
        }
        return bmp;
    }
    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    public static Icon ToIcon(Bitmap bmp)
    {
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }
    // Multi-resolution .ico (PNG-compressed frames) for the executable; used by --write-icon at build time.
    public static void WriteIco(string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
        var frames = sizes.Select(sz => { using var b = Render(sz, null); using var ms = new MemoryStream(); b.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ms.ToArray(); }).ToArray();
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(frames[i].Length); w.Write(offset); offset += frames[i].Length;
        }
        foreach (var f in frames) w.Write(f);
    }
}
