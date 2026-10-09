namespace LocalQwenTray;
internal sealed class SafeLog(string key)
{
    public string Path => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalQwen", "tray.log");
    public string Redact(string text) => Secrets.Redact(text, key);
    public void Write(string text)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (File.Exists(Path) && new FileInfo(Path).Length > 2 * 1024 * 1024) File.Move(Path, Path + ".previous", true);
        File.AppendAllText(Path, $"{DateTimeOffset.Now:O} {Redact(text)}{Environment.NewLine}");
    }
}
