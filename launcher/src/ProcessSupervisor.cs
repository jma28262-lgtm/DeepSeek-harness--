using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 用 Windows Job Object 托管所有子进程。
    ///
    /// 要解决的问题（实测）：模型服务由 start-model-server.ps1 以 Start-Process 分离启动，
    /// 而该脚本随即退出 —— 于是 llama-server / ollama 既不在 dsh 的进程树里，
    /// 也不在本进程树里。结果是：关掉启动器、甚至用 taskkill /T /F 杀 dsh，
    /// 模型服务依然活着，继续占用 11435/11434 端口与显存。
    ///
    /// Job Object 由内核保证：只要 Job 句柄关闭（包括本进程被任务管理器强杀，
    /// 内核回收句柄时），Job 内所有进程一律终止。这是唯一可靠的办法。
    /// </summary>
    public static class ProcessSupervisor
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static readonly object _lock = new object();
        private static IntPtr _job = IntPtr.Zero;
        private static bool _attempted;

        /// <summary>最近一次失败原因（Job Object 不可用时降级为普通进程管理，不影响功能）。</summary>
        public static string LastError { get; private set; }

        public static bool Active { get { return _job != IntPtr.Zero; } }

        public static void Init()
        {
            lock (_lock)
            {
                if (_attempted) return;
                _attempted = true;
                try
                {
                    _job = CreateJobObject(IntPtr.Zero, null);
                    if (_job == IntPtr.Zero) { LastError = "CreateJobObject 失败"; return; }

                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                    int len = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                    IntPtr p = Marshal.AllocHGlobal(len);
                    try
                    {
                        Marshal.StructureToPtr(info, p, false);
                        if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, p, (uint)len))
                        {
                            LastError = "SetInformationJobObject 失败";
                            CloseHandle(_job);
                            _job = IntPtr.Zero;
                            return;
                        }
                    }
                    finally { Marshal.FreeHGlobal(p); }
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    _job = IntPtr.Zero;
                }
            }
        }

        /// <summary>把进程挂进 Job。失败不抛异常（例如已在其他 Job 且不允许嵌套）。</summary>
        public static bool Assign(Process p)
        {
            if (p == null) return false;
            lock (_lock)
            {
                if (_job == IntPtr.Zero) return false;
                try
                {
                    if (!AssignProcessToJobObject(_job, p.Handle))
                    {
                        LastError = "AssignProcessToJobObject 失败（进程 " + p.Id + "）";
                        return false;
                    }
                    return true;
                }
                catch (Exception ex) { LastError = ex.Message; return false; }
            }
        }

        /// <summary>
        /// 把启动器自身放进 Job。之后它派生的所有进程（node、powershell、
        /// 以及 powershell 再 Start-Process 出来的 llama-server/ollama）
        /// 都会自动继承 Job 成员身份 —— 无需逐个 assign，也不存在竞态。
        /// </summary>
        public static bool AdoptSelf()
        {
            lock (_lock)
            {
                if (_job == IntPtr.Zero) return false;
                try
                {
                    if (!AssignProcessToJobObject(_job, Process.GetCurrentProcess().Handle))
                    {
                        LastError = "自我加入 Job 失败（可能已被外层 Job 托管）";
                        return false;
                    }
                    _adopted = true;
                    return true;
                }
                catch (Exception ex) { LastError = ex.Message; return false; }
            }
        }

        private static bool _adopted;

        /// <summary>是否已托管自身（用于 UI 状态展示）。</summary>
        public static bool Adopted { get { return _adopted; } }

        /// <summary>
        /// 关闭 Job 句柄 -> 内核终止 Job 内全部进程。退出时调用。
        /// </summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                if (_job == IntPtr.Zero) return;
                try { CloseHandle(_job); } catch { }
                _job = IntPtr.Zero;
            }
        }

        /// <summary>供日志使用的一行状态描述。</summary>
        public static string Describe()
        {
            if (Active) return "Job Object 已启用：退出时子进程将被内核一并终止";
            return "Job Object 未启用（" + (LastError == null ? "未知原因" : LastError) + "）：子进程需手工清理";
        }
    }
}
