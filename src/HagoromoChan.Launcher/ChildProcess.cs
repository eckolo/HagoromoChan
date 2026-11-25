using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using HagoromoChan.Contracts;

namespace HagoromoChan.Launcher;

// The child is assigned to its own kill-on-close job BEFORE its first instruction.
// A supervised stdio descendant therefore cannot escape through a Start/Assign race.
internal sealed class ChildProcess : IDisposable
{
    private readonly SafeFileHandle _job;
    private readonly SafeFileHandle _nativeProcess;
    private readonly Process _process;
    private readonly bool _privateConsole;
    public int Pid => _process.Id;
    public bool Alive => !_process.HasExited;
    public int? ExitCode => Alive ? null : _process.ExitCode;

    private ChildProcess(SafeFileHandle job, SafeFileHandle nativeProcess, Process process, bool privateConsole)
    {
        _job = job; _nativeProcess = nativeProcess; _process = process; _privateConsole = privateConsole;
    }

    public static ChildProcess Start(string exe, IReadOnlyList<string> args, string workDirectory,
        IReadOnlyDictionary<string, string?> overrides, string logPath, bool privateConsole = false)
    {
        using var stdout = OpenLog(logPath);
        using var stdin = OpenInput();
        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            environment[(string)entry.Key] = (string)entry.Value!;
        foreach (var (name, value) in overrides)
        {
            if (value is null) environment.Remove(name); else environment[name] = value;
        }
        var block = string.Join("\0", environment.Select(kv => kv.Key + "=" + kv.Value)) + "\0\0";
        var blockPtr = Marshal.StringToHGlobalUni(block);
        var job = CreateJobObject(IntPtr.Zero, null);
        SafeFileHandle? process = null;
        SafeFileHandle? thread = null;
        Process? managed = null;
        IntPtr attributes = IntPtr.Zero, jobList = IntPtr.Zero, handleList = IntPtr.Zero;
        var attributesInitialized = false;
        try
        {
            if (job.IsInvalid) throw LastError();
            var limits = new ExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x00002000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                throw LastError();

            // Windows 10+ assigns the job atomically in CreateProcess. Even a
            // manager crash during creation cannot leave an unassigned suspended child.
            IntPtr bytes = IntPtr.Zero;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref bytes);
            if (bytes == IntPtr.Zero) throw LastError();
            attributes = Marshal.AllocHGlobal(bytes);
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref bytes)) throw LastError();
            attributesInitialized = true;
            jobList = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobList, job.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, (UIntPtr)0x0002000D, jobList,
                (UIntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw LastError();
            handleList = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(handleList, 0, stdin.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size, stdout.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, (UIntPtr)0x00020002, handleList,
                (UIntPtr)(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero)) throw LastError();

            var startup = new StartupInfo
            {
                cb = (uint)Marshal.SizeOf<StartupInfoEx>(),
                dwFlags = 0x00000101, // STARTF_USESHOWWINDOW | STARTF_USESTDHANDLES
                wShowWindow = 0, // SW_HIDE; Desktop must explicitly show its own WPF window.
                hStdInput = stdin.DangerousGetHandle(),
                hStdOutput = stdout.DangerousGetHandle(),
                hStdError = stdout.DangerousGetHandle()
            };
            var command = new StringBuilder(string.Join(" ", new[] { RuntimeControl.QuoteWindowsArgument(exe) }.Concat(args.Select(RuntimeControl.QuoteWindowsArgument))));
            uint flags = 0x00000004 | 0x00000400 | 0x00080000 | (privateConsole ? 0x00000010u : 0x08000000u);
            // CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE/CREATE_NO_WINDOW.
            // Give a GUI app a normal initial show state (a hidden first ShowWindow could suppress its mascot).
            if (!privateConsole && Path.GetFileName(exe).Equals("HagoromoChan.Desktop.exe", StringComparison.OrdinalIgnoreCase))
                startup.dwFlags = 0x00000100;
            var extended = new StartupInfoEx { StartupInfo = startup, AttributeList = attributes };
            if (!CreateProcess(exe, command, IntPtr.Zero, IntPtr.Zero, true, flags,
                blockPtr, workDirectory, ref extended, out var info))
                throw LastError();
            process = new SafeFileHandle(info.hProcess, true);
            thread = new SafeFileHandle(info.hThread, true);
            managed = Process.GetProcessById((int)info.dwProcessId);
            _ = managed.SafeHandle; // Bind the managed Process to this specific process before resume.
            if (ResumeThread(thread) == uint.MaxValue) throw LastError();
            var child = new ChildProcess(job, process, managed, privateConsole);
            process = null; managed = null;
            return child;
        }
        catch
        {
            if (process is not null)
            {
                _ = TerminateJobObject(job, 1);
                _ = WaitForSingleObject(process, 5000);
            }
            job.Dispose();
            throw;
        }
        finally
        {
            thread?.Dispose(); process?.Dispose(); managed?.Dispose();
            if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (jobList != IntPtr.Zero) Marshal.FreeHGlobal(jobList);
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            // The buffer contains the tunnel runtime key when starting that child.
            for (var i = 0; i < block.Length; i++) Marshal.WriteInt16(blockPtr, i * 2, 0);
            Marshal.FreeHGlobal(blockPtr);
        }
    }

    // Only the console we created for our own tunnel root is ever signaled.
    // CTRL_C_EVENT with group 0 targets that private console, not the user's terminal.
    public async Task<bool> SignalConsoleAsync()
    {
        if (!_privateConsole || !Alive || GetConsoleCP() != 0) return false;
        if (!AttachConsole((uint)Pid)) return false;
        try
        {
            // A private console should contain only this manager (temporarily
            // attached) and processes belonging to the tunnel's job.
            var ids = new uint[256];
            var count = GetConsoleProcessList(ids, (uint)ids.Length);
            if (count == 0 || count > ids.Length || !ids.Take((int)count).Contains((uint)Pid)) return false;
            foreach (var pid in ids.Take((int)count))
            {
                if (pid == (uint)Environment.ProcessId) continue;
                using var member = OpenProcess(0x1000, false, pid);
                if (member.IsInvalid || !IsProcessInJob(member, _job, out var belongs) || !belongs) return false;
            }
            if (!SetConsoleCtrlHandler(IntPtr.Zero, true)) return false;
            if (!GenerateConsoleCtrlEvent(0, 0)) return false;
            await Task.Delay(300).ConfigureAwait(false);
            return true;
        }
        finally { _ = FreeConsole(); }
    }

    public async Task<bool> WaitEmptyAsync(TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        do
        {
            if (!QueryInformationJobObject(_job, 1, out var info,
                (uint)Marshal.SizeOf<BasicAccountingInformation>(), IntPtr.Zero))
                throw LastError();
            if (info.ActiveProcesses == 0) return true;
            await Task.Delay(100).ConfigureAwait(false);
        } while (deadline.Elapsed < timeout);
        return false;
    }

    public void Terminate() { if (!TerminateJobObject(_job, 1)) throw LastError(); }

    public void Dispose()
    {
        _job.Dispose(); // kernel fallback for any remaining child or descendant
        _nativeProcess.Dispose();
        _process.Dispose();
    }

    private static SafeFileHandle OpenLog(string path)
    {
        var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        var handle = CreateFile(path, 0x00000004, 7, ref sa, 4, 0x80, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw LastError(); }
        return handle;
    }

    private static SafeFileHandle OpenInput()
    {
        var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        var handle = CreateFile("NUL", 0x80000000, 3, ref sa, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw LastError(); }
        return handle;
    }

    private static Win32Exception LastError() => new(Marshal.GetLastWin32Error());

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimitInformation info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out BasicAccountingInformation info, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, UIntPtr attribute,
        IntPtr value, UIntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetConsoleProcessList([Out] uint[] ids, uint count);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(SafeFileHandle process, SafeFileHandle job, out bool result);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
        ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes attributes,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exit);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
    [DllImport("kernel32.dll")] private static extern uint GetConsoleCP();
}
