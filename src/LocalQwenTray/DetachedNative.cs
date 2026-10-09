using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace LocalQwenTray;
// No inherited console/pipe handles. The child writes its own --log-file and
// survives CLI/tray exit, while the ownership ledger identifies the exact PID.
internal static class DetachedNative
{
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb;
        public string? reserved,desktop,title;
        public int x,y,xSize,ySize,xCountChars,yCountChars,fillAttribute,flags;
        public short showWindow,reserved2;
        public IntPtr reservedPointer,stdInput,stdOutput,stdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation {public IntPtr process,thread;public uint pid,tid;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateProcessW(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,[MarshalAs(UnmanagedType.Bool)]bool inheritHandles,uint flags,IntPtr environment,string? directory,ref StartupInfo startup,out ProcessInformation process);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process,uint exitCode);
    static string Quote(string value)
    {
        var result=new StringBuilder("\"");int slashes=0;
        foreach(char c in value)
        {
            if(c=='\\'){slashes++;continue;}
            if(c=='"')result.Append('\\',slashes*2+1);else result.Append('\\',slashes);
            result.Append(c);slashes=0;
        }
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
    public static Process Start(ProcessStartInfo info)
    {
        string file=Path.GetFullPath(info.FileName);
        string env=string.Join('\0',info.Environment.OrderBy(x=>x.Key,StringComparer.OrdinalIgnoreCase).Select(x=>x.Key+"="+x.Value))+"\0\0";
        var environment=Marshal.StringToHGlobalUni(env);
        var startup=new StartupInfo{cb=Marshal.SizeOf<StartupInfo>()};
        ProcessInformation native=default;
        try
        {
            var command=new StringBuilder(string.Join(' ',new[]{Quote(file)}.Concat(info.ArgumentList.Select(Quote))));
            // DETACHED_PROCESS | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT.
            if(!CreateProcessW(file,command,IntPtr.Zero,IntPtr.Zero,false,0x8|0x4|0x400,environment,string.IsNullOrEmpty(info.WorkingDirectory)?null:info.WorkingDirectory,ref startup,out native))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Native detached launch failed");
            var process=Process.GetProcessById((int)native.pid);
            try
            {
                _=process.Handle; // Hold the exact handle before the process can exit.
                if(ResumeThread(native.thread)==uint.MaxValue)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Native resume failed");
                return process;
            }
            catch {TerminateProcess(native.process,1);process.Dispose();throw;}
        }
        finally
        {
            if(native.thread!=IntPtr.Zero)CloseHandle(native.thread);
            if(native.process!=IntPtr.Zero)CloseHandle(native.process);
            Marshal.FreeHGlobal(environment);
        }
    }
}
