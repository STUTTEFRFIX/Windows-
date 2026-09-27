// 程序入口：创建无边框深色小窗，接上采集层与界面层。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PerfMon
{
    internal sealed class MainForm : Form
    {
        readonly Collector _col;
        readonly Ui _ui;

        public MainForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Win;
            DoubleBuffered = true;
            KeyPreview = true;
            Text = "性能监测";
            ShowInTaskbar = true;
            TopMost = true;

            _col = new Collector();
            _ui = new Ui(this, _col);
            _ui.Measure();
            ClientSize = new Size(Ui.W, _ui.UiHeight);

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            Paint += (s, e) => _ui.Paint(e.Graphics);
            MouseDown += (s, e) => _ui.OnMouseDown(e);
            MouseMove += (s, e) => _ui.OnMouseMove(e);
            MouseUp += (s, e) => _ui.OnMouseUp();
            Shown += (s, e) => {
                try {
                    System.IO.File.WriteAllText(@"D:\DS\perf-monitor\layout.txt",
                        "ClientSize=" + ClientSize.Width + "x" + ClientSize.Height +
                        "  UiHeight=" + _ui.UiHeight +
                        "  ClientRectangle=" + ClientRectangle.Width + "x" + ClientRectangle.Height,
                        new System.Text.UTF8Encoding(false));
                } catch { }
                _ui.OnResize(); _ui.Start();
            };
            FormClosed += (s, e) => { _ui.Stop(); _col.Dispose(); };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--layout")
            {
                var col2 = new Collector();
                var ui2 = new Ui(new Form(), col2);
                ui2.Measure();
                System.IO.File.WriteAllText(@"D:\DS\perf-monitor\layout2.txt",
                    "UiHeight=" + ui2.UiHeight + "\n" + ui2.DumpLayout(),
                    new System.Text.UTF8Encoding(false));
                col2.Dispose();
                return;
            }
            if (args.Length > 0 && args[0] == "--selftest")
            {
                SelfTest(args.Length > 1 ? args[1] : null);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        /// <summary>无界面自检：连续采样若干次并把结果写到文件，用于验证采集层。</summary>
        static void SelfTest(string outPath)
        {
            var sb = new System.Text.StringBuilder();
            using (var col = new Collector())
            {
                sb.AppendLine("=== 初始化 ===");
                sb.Append(col.InitReport);
                sb.AppendLine("CPU: " + col.CpuName);
                sb.AppendLine("GPU: " + col.GpuName + "  nvidia-smi=" + col.HasNvidia);
                for (int i = 0; i < 4; i++)
                {
                    System.Threading.Thread.Sleep(1100);
                    var s = col.Read();
                    sb.AppendLine("--- 第 " + (i + 1) + " 次采样 ---");
                    sb.AppendLine("  CPU总(求和口径)=" + N(s.CpuTotal) + "%  CPU总(时间口径)=" + N(col.CpuLoadByTime) + "%  每核心=" + string.Join(",", Array.ConvertAll(s.CpuCores, N)));
                    sb.AppendLine("  诊断: " + col.DebugCalc);
                    sb.AppendLine("  内存 已用=" + N(s.MemUsedGB) + "GB / 共=" + N(s.MemTotalGB) + "GB");
                    sb.AppendLine("  GPU负载=" + N(s.GpuLoad) + "%  显存=" + N(s.VramUsedMB) + "/" + N(s.VramTotalMB) + "MB");
                    sb.AppendLine("  GPU 温度=" + N(s.GpuTempC) + "℃ 功耗=" + N(s.GpuPowerW) + "/" + N(s.GpuPowerCapW) + "W 风扇=" + N(s.GpuFanPct) + "% 频率=" + N(s.GpuClockMHz) + "MHz");
                    sb.AppendLine("  磁盘 读=" + N(s.DiskReadMBs) + " 写=" + N(s.DiskWriteMBs) + " MB/s");
                    sb.AppendLine("  网络 下=" + N(s.NetDownMBs) + " 上=" + N(s.NetUpMBs) + " MB/s");
                }
            }
            string text = sb.ToString();
            if (!string.IsNullOrEmpty(outPath))
                System.IO.File.WriteAllText(outPath, text, new System.Text.UTF8Encoding(false));
            else
                Console.WriteLine(text);
        }

        static string N(double v)
        {
            return double.IsNaN(v) ? "NaN" : v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
