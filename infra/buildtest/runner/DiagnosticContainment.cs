using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

// Small native boundary: no command-line construction or process launching here.
public sealed class DiagnosticContainment : IDisposable
{
    private IntPtr job;
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool QueryInformationJobObject(IntPtr job, int kind, byte[] data, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("libc", SetLastError=true)] private static extern int kill(int pid, int signal);
    [DllImport("libc", SetLastError=true)] private static extern int getpgid(int pid);
    [DllImport("libc", SetLastError=true)] private static extern int getsid(int pid);
    public DiagnosticContainment() { job=CreateJobObject(IntPtr.Zero,null); if(job==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public void Assign(IntPtr process) { if(!AssignProcessToJobObject(job,process)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public bool Contains(IntPtr process) { if(!IsProcessInJob(process,job,out bool result)) throw new Win32Exception(Marshal.GetLastWin32Error()); return result; }
    public int[] MemberIds { get { byte[] data=new byte[65536]; if(!QueryInformationJobObject(job,3,data,(uint)data.Length,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error()); int count=BitConverter.ToInt32(data,4); int[] ids=new int[count]; for(int i=0;i<count;i++) ids[i]=IntPtr.Size==8 ? checked((int)BitConverter.ToInt64(data,8+i*8)) : BitConverter.ToInt32(data,8+i*4); return ids; } }
    public uint Active { get { byte[] data=new byte[48]; if(!QueryInformationJobObject(job,1,data,48,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error()); return BitConverter.ToUInt32(data,40); } }
    public void Kill() { if(!TerminateJobObject(job,1)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public static bool IsSessionLeader(int pid) => getpgid(pid)==pid && getsid(pid)==pid;
    public static void KillGroup(int pid) { if(kill(-pid,9)!=0 && Marshal.GetLastWin32Error()!=3) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public void Dispose() { if(job!=IntPtr.Zero) { CloseHandle(job); job=IntPtr.Zero; } }
}
