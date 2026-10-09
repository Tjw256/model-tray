using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
namespace LocalQwenTray;
internal sealed class ProcessRunner : ICommandRunner
{
    public async Task<CommandResult> Run(string file, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("Could not launch " + file);
        var output = Capture(process.StandardOutput, deadline.Token);
        var error = Capture(process.StandardError, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Process exited concurrently. */ }
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{file} command timed out after {timeout.TotalSeconds:g} seconds");
        }
    }
    static async Task<string> Capture(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder(); var buffer = new char[4096];
        while (true)
        {
            int n = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (n == 0) return text.ToString();
            int retain = Math.Min(n, Math.Max(0, 1024 * 1024 - text.Length));
            text.Append(buffer, 0, retain); // Keep draining even after bounded capture fills.
        }
    }
}
internal static class RuntimeTests
{
    public static async Task<int> Run()
    {
        try
        {
            var runner = new ProcessRunner();
            var version = await runner.Run("dotnet", ["--version"], TimeSpan.FromSeconds(15), default);
            if (version.ExitCode != 0 || !version.Output.StartsWith("10.")) throw new Exception("SDK capture failed");
            Console.WriteLine("PASS real process stdout and exit");
            var error = await runner.Run("cmd.exe", ["/d", "/c", "echo diagnostic 1>&2 & exit /b 7"], TimeSpan.FromSeconds(10), default);
            if (error.ExitCode != 7 || !error.Error.Contains("diagnostic")) throw new Exception("stderr/exit capture failed");
            Console.WriteLine("PASS real process stderr and nonzero exit");
            try
            {
                await runner.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"], TimeSpan.FromMilliseconds(250), default);
                throw new Exception("Timeout missing");
            }
            catch (TimeoutException) { Console.WriteLine("PASS bounded process timeout"); }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.Message); return 1; }
    }
}
