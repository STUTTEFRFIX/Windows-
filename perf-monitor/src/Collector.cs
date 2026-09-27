// 采集层（重写版）
//   本机实测 PerformanceCounter 完全不可用（全部抛 InvalidOperationException），
//   因此主数据源改为不依赖性能计数器的方案：
//     CPU 总/每核心占用 -> NtQuerySystemInformation(SystemProcessorPerformanceInformation) 差值
//     内存             -> GlobalMemoryStatusEx
//     GPU 全部指标      -> nvidia-smi（一次调用取回 负载/显存/温度/功耗/风扇/频率）
//     网络             -> NetworkInterface 字节数差值
//     磁盘             -> 性能计数器（若不可用则该行显示 --）
//   任何一路失败都只让对应指标显示 "--"，绝不让程序崩溃。
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PerfMon
{
    internal sealed class Sample
    {
        public double CpuTotal = double.NaN;
        public double[] CpuCores = new double[0];
        public double MemUsedGB = double.NaN;
        public double MemTotalGB = double.NaN;
        public double GpuLoad = double.NaN;
        public double VramUsedMB = double.NaN;
        public double VramTotalMB = double.NaN;
        public double GpuTempC = double.NaN;
        public double GpuPowerW = double.NaN;
        public double GpuPowerCapW = double.NaN;
        public double GpuFanPct = double.NaN;
        public double GpuClockMHz = double.NaN;
        public double DiskReadMBs = double.NaN;
        public double DiskWriteMBs = double.NaN;
        public double NetDownMBs = double.NaN;
        public double NetUpMBs = double.NaN;
        public string CpuName = "";
        public int CoreCount = 0;
        public string GpuName = "";
        public bool HasNvidia = false;
    }

    internal sealed class Collector : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct PROC_PERF
        {
            public long IdleTime, KernelTime, UserTime, DpcTime, InterruptTime;
            public uint InterruptCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("ntdll.dll")]
        static extern int NtQuerySystemInformation(int cls, IntPtr info, int len, out int retLen);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buf);

        [StructLayout(LayoutKind.Sequential)]
        struct FILETIME { public uint Low, High; }

        [DllImport("kernel32.dll")]
        static extern void GetSystemTimeAsFileTime(out FILETIME t);

        [DllImport("kernel32.dll")]
        static extern int GetSystemTimeAdjustment(out uint adj, out uint inc, out bool disabled);

        static double FileTimeToDouble(FILETIME t)
        {
            return ((long)t.High << 32 | t.Low);
        }

        // Windows 的 SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION 因 8 字节对齐实际占 48 字节
        // （6*8 + 4 + 4 填充）。用 Marshal.SizeOf 会得到 40，步长错位会让第 2 个核心之后
        // 全部读错 —— 必须用 48 做步长。
        const int RecSize = 48;

        readonly int _cores;
        readonly PROC_PERF[] _prev;

        bool _havePrev;
        string _cpuName = "", _gpuName = "";
        bool _hasNvidia;
        string _smiPath = "";
        double _vramTotalMB = double.NaN, _powerCapW = double.NaN;
        DateTime _smiNext = DateTime.MinValue;
        readonly Sample _smi = new Sample();
        string _smiRaw = "";
        PerformanceCounter[] _diskPc;
        long _prevNetDown = -1, _prevNetUp = -1;
        DateTime _prevNetTime = DateTime.MinValue;

        public string InitReport = "";

        public Collector()
        {
            var log = new StringBuilder();
            _cores = Environment.ProcessorCount;
            _prev = new PROC_PERF[_cores];
            _cpuName = ReadCpuName();
            _gpuName = ReadGpuName();
            _smiPath = FindNvidiaSmi();
            _hasNvidia = _smiPath.Length > 0;
            if (_hasNvidia) QuerySmi(true);
            double dummyTotal; double[] dummyCores;
            ReadCpu(out dummyTotal, out dummyCores);
            SampleNet(out _prevNetDown, out _prevNetUp);
            _prevNetTime = DateTime.UtcNow;
            EnableDiskFallback();
            log.Append("核心数=").Append(_cores)
               .Append("  nvidia-smi=").Append(_hasNvidia)
               .Append("  磁盘计数器=").Append(_diskPc != null)
               .AppendLine();
            InitReport = log.ToString();
        }

        public string CpuName { get { return _cpuName; } }
        public string GpuName { get { return _gpuName; } }
        public bool HasNvidia { get { return _hasNvidia; } }
        public string SmiRaw { get { return _smiRaw; } }

        public string DebugCalc = "";

        void ReadCpu(out double total, out double[] perCore)
        {
            total = double.NaN;
            perCore = new double[_cores];
            for (int i = 0; i < _cores; i++) perCore[i] = double.NaN;

            FILETIME ft;
            GetSystemTimeAsFileTime(out ft);
            double nowHns = FileTimeToDouble(ft);          // 100ns 单位

            int size = RecSize * _cores;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int ret;
                if (NtQuerySystemInformation(8, buf, size, out ret) != 0) return;
                double sumBusy = 0, sumAll = 0;
                double sumIdle = 0, sumKernel = 0, sumUser = 0;
                for (int i = 0; i < _cores; i++)
                {
                    var cur = (PROC_PERF)Marshal.PtrToStructure((IntPtr)((long)buf + i * RecSize), typeof(PROC_PERF));
                    if (_havePrev)
                    {
                        var p = _prev[i];
                        double dIdle = cur.IdleTime - p.IdleTime;
                        double dKernel = cur.KernelTime - p.KernelTime;
                        double dUser = cur.UserTime - p.UserTime;
                        // 关键：KernelTime 里已经包含 IdleTime，必须减掉才是真正的忙时，
                        // 否则 CPU 占用会被显著高估（实测高估一倍：58% vs 真实 28%）
                        double dBusy = dKernel + dUser - dIdle;
                        if (dBusy < 0) dBusy = 0;
                        double dAll = dIdle + dBusy;
                        if (dAll > 0)
                        {
                            double load = 100.0 * dBusy / dAll;
                            if (load < 0) load = 0;
                            if (load > 100) load = 100;
                            perCore[i] = load;
                            sumBusy += dBusy; sumAll += dAll;
                        }
                        sumIdle += dIdle; sumKernel += dKernel; sumUser += dUser;
                    }
                    _prev[i] = cur;
                }
                if (sumAll > 0) total = 100.0 * sumBusy / sumAll;

                if (_havePrevTime && nowHns > _prevTimeHns)
                {
                    double dt = nowHns - _prevTimeHns;
                    // 口径 A：占用 = (间隔 - 空闲) / 间隔 —— 与任务管理器同一口径
                    double busyA = dt * _cores - sumIdle;
                    double loadA = 100.0 * busyA / (dt * _cores);
                    if (loadA < 0) loadA = 0;
                    if (loadA > 100) loadA = 100;
                    _loadByTime = loadA;
                    DebugCalc = string.Format(CultureInfo.InvariantCulture,
                        "间隔={0:N0}(100ns) 空闲={1:N0} 内核={2:N0} 用户={3:N0} | 空闲占比={4:N1}% | 口径A(时间)={5:N1}% 口径B(求和)={6:N1}%",
                        dt, sumIdle, sumKernel, sumUser,
                        100.0 * sumIdle / (dt * _cores),
                        loadA, total);
                }
                _prevTimeHns = nowHns;
                _havePrevTime = true;
                _havePrev = true;
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
        }

        double _loadByTime = double.NaN;
        double _prevTimeHns;
        bool _havePrevTime;

        /// <summary>用「(间隔 - 空闲) / 间隔」口径得到的 CPU 占用，与任务管理器一致。</summary>
        public double CpuLoadByTime { get { return _loadByTime; } }

        void ReadMem(out double usedGB, out double totalGB)
        {
            usedGB = totalGB = double.NaN;
            try
            {
                var m = new MEMORYSTATUSEX();
                m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (!GlobalMemoryStatusEx(ref m) || m.ullTotalPhys == 0) return;
                totalGB = m.ullTotalPhys / 1073741824.0;
                usedGB = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
            }
            catch { }
        }

        static readonly string[] SmiFields = {
            "utilization.gpu", "memory.used", "memory.total", "temperature.gpu",
            "power.draw", "power.limit", "fan.speed", "clocks.sm"
        };

        void QuerySmi(bool force)
        {
            if (!_hasNvidia) return;
            if (!force && DateTime.UtcNow < _smiNext) return;
            _smiNext = DateTime.UtcNow.AddMilliseconds(900);
            try
            {
                string outp = RunSmi("--query-gpu=" + string.Join(",", SmiFields) + " --format=csv,noheader,nounits");
                if (outp == null) return;
                _smiRaw = outp.Trim().Replace("\r", "").Replace("\n", " | ");
                var first = outp.Trim().Split('\n')[0].Trim();
                var parts = first.Split(',');
                if (parts.Length < 8) return;
                _smi.GpuLoad = ParseD(parts[0]);
                _smi.VramUsedMB = ParseD(parts[1]);
                _smi.VramTotalMB = ParseD(parts[2]);
                _smi.GpuTempC = ParseD(parts[3]);
                _smi.GpuPowerW = ParseD(parts[4]);
                double cap = ParseD(parts[5]);
                if (!double.IsNaN(cap)) _powerCapW = cap;
                _smi.GpuFanPct = ParseD(parts[6]);
                _smi.GpuClockMHz = ParseD(parts[7]);
                if (!double.IsNaN(_smi.VramTotalMB)) _vramTotalMB = _smi.VramTotalMB;
                if (string.IsNullOrEmpty(_gpuName))
                {
                    string n = RunSmi("--query-gpu=name --format=csv,noheader");
                    if (n != null) _gpuName = n.Trim().Split('\n')[0].Trim();
                }
            }
            catch { }
        }

        string RunSmi(string args)
        {
            try
            {
                var psi = new ProcessStartInfo(_smiPath, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(2500)) { try { p.Kill(); } catch { } return null; }
                    return o;
                }
            }
            catch { return null; }
        }

        static double ParseD(string s)
        {
            if (string.IsNullOrEmpty(s)) return double.NaN;
            s = s.Trim();
            if (s.IndexOf("N/A", StringComparison.OrdinalIgnoreCase) >= 0) return double.NaN;
            double v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return double.NaN;
        }

        void SampleNet(out long down, out long up)
        {
            down = up = -1;
            try
            {
                long d = 0, u = 0;
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    try
                    {
                        var st = ni.GetIPStatistics();
                        d += st.BytesReceived;
                        u += st.BytesSent;
                    }
                    catch { }
                }
                down = d; up = u;
            }
            catch { }
        }

        void EnableDiskFallback()
        {
            try
            {
                _diskPc = new[] {
                    new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true),
                    new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true)
                };
                _diskPc[0].NextValue(); _diskPc[1].NextValue();
            }
            catch { _diskPc = null; }
        }

        public Sample Read()
        {
            var s = new Sample();
            s.CpuName = _cpuName;
            s.CoreCount = _cores;
            s.GpuName = _gpuName;
            s.HasNvidia = _hasNvidia;

            double cpuTotal; double[] cores;
            ReadCpu(out cpuTotal, out cores);
            // 用时间口径（与任务管理器一致），求和口径仅作为每核心的兜底
            s.CpuTotal = double.IsNaN(_loadByTime) ? cpuTotal : _loadByTime;
            s.CpuCores = cores;

            double usedGB, totalGB;
            ReadMem(out usedGB, out totalGB);
            s.MemUsedGB = usedGB;
            s.MemTotalGB = totalGB;

            QuerySmi(false);
            s.GpuLoad = _smi.GpuLoad;
            s.VramUsedMB = _smi.VramUsedMB;
            s.VramTotalMB = double.IsNaN(_smi.VramTotalMB) ? _vramTotalMB : _smi.VramTotalMB;
            s.GpuTempC = _smi.GpuTempC;
            s.GpuPowerW = _smi.GpuPowerW;
            s.GpuPowerCapW = _powerCapW;
            s.GpuFanPct = _smi.GpuFanPct;
            s.GpuClockMHz = _smi.GpuClockMHz;

            long d2, u2;
            SampleNet(out d2, out u2);
            var now = DateTime.UtcNow;
            double dt = (now - _prevNetTime).TotalSeconds;
            if (dt > 0.2 && _prevNetDown >= 0 && d2 >= _prevNetDown)
            {
                s.NetDownMBs = (d2 - _prevNetDown) / 1048576.0 / dt;
                s.NetUpMBs = (u2 - _prevNetUp) / 1048576.0 / dt;
            }
            _prevNetDown = d2; _prevNetUp = u2; _prevNetTime = now;

            if (_diskPc != null)
            {
                try
                {
                    s.DiskReadMBs = _diskPc[0].NextValue() / 1048576.0;
                    s.DiskWriteMBs = _diskPc[1].NextValue() / 1048576.0;
                }
                catch { _diskPc = null; }
            }

            return s;
        }

        static string ReadCpuName()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    if (k != null) return Convert.ToString(k.GetValue("ProcessorNameString")).Trim();
            }
            catch { }
            return "CPU";
        }

        static string ReadGpuName()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (k != null)
                    {
                        foreach (var sub in k.GetSubKeyNames())
                            using (var sk = k.OpenSubKey(sub))
                            {
                                if (sk == null) continue;
                                var d = Convert.ToString(sk.GetValue("DriverDesc"));
                                if (!string.IsNullOrEmpty(d) &&
                                    d.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0)
                                    return d.Trim();
                            }
                    }
                }
            }
            catch { }
            return "";
        }

        static string FindNvidiaSmi()
        {
            foreach (var p in new[] {
                Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\nvidia-smi.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\NVIDIA Corporation\NVSMI\nvidia-smi.exe") })
            {
                try { if (System.IO.File.Exists(p)) return p; } catch { }
            }
            return "";
        }

        public void Dispose()
        {
            if (_diskPc != null)
                foreach (var c in _diskPc) try { c.Dispose(); } catch { }
        }
    }
}
