using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace LocalQwenTray;
internal static class OwnedNativeProcess
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    public static bool Matches(int pid, long startTimeUtcTicks, string exe)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return MatchesHandle(process,startTimeUtcTicks,exe);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public static bool MatchesIdentity(int pid, long startTimeUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTimeUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public static bool Exists(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public static bool MatchesHandle(Process process, long startTimeUtcTicks, string exe)
    {
        if(process.HasExited || process.StartTime.ToUniversalTime().Ticks != startTimeUtcTicks) return false;
        var name = new StringBuilder(32768);
        int size = name.Capacity;
        return QueryFullProcessImageName(process.Handle,0,name,ref size) &&
            string.Equals(Path.GetFullPath(name.ToString()),Path.GetFullPath(exe),StringComparison.OrdinalIgnoreCase);
    }
}
