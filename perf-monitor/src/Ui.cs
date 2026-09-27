// UI 层：无边框深色小窗，全部自绘（GDI+），双缓冲防闪烁。
// 排版与配色严格对齐《性能监测软件-需求文档.md》第 3 节。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;

namespace PerfMon
{
    internal static class Theme
    {
        public static readonly Color Win = Color.FromArgb(0x13, 0x15, 0x19);
        public static readonly Color Panel = Color.FromArgb(0x1A, 0x1D, 0x21);
        public static readonly Color Card = Color.FromArgb(0x22, 0x26, 0x2B);
        public static readonly Color Edge = Color.FromArgb(0x33, 0x38, 0x3F);
        public static readonly Color Track = Color.FromArgb(0x2C, 0x31, 0x37);
        public static readonly Color Accent = Color.FromArgb(0x4F, 0xD1, 0xC5);
        public static readonly Color Accent2 = Color.FromArgb(0x63, 0xB3, 0xED);
        public static readonly Color Warn = Color.FromArgb(0xF6, 0xAD, 0x55);
        public static readonly Color Danger = Color.FromArgb(0xFC, 0x81, 0x81);
        public static readonly Color Txt = Color.FromArgb(0xE6, 0xE8, 0xEA);
        public static readonly Color Txt2 = Color.FromArgb(0x8B, 0x94, 0x9E);
    }

    internal sealed class Ui
    {
        // ---- 版式常量（与效果图一致）----
        public const int W = 420;
        public const int HeaderH = 46;
        public const int Margin = 14;
        public const int Pad = 16;
        public const int GapCard = 12;
        public const int ContentX = Margin + Pad;      // 30
        public const int ContentW = W - 2 * ContentX;  // 400
        public const int CardW = W - 2 * Margin;       // 432
        public const int Head = 40, NumH = 50, BarH = 8, SPARKH = 20;

        public const int BIG_DESC = 5;   // 大数字下行部（与字体度量一致）

        public const int TOP = 60;       // 第一张卡的卡顶
        public const int GapN = 14, GapB = 10, GapS = 20, PadBottom = 16;

        public const int History = 60;

        readonly Form _form;
        readonly Collector _col;
        readonly Timer _timer;
        readonly Stopwatch _clock = Stopwatch.StartNew();

        // 历史曲线（定长环形缓冲，复用数组，避免每秒产生垃圾）
        readonly List<double> _cpuHist = new List<double>(History);
        readonly List<double> _memHist = new List<double>(History);
        readonly List<double> _gpuHist = new List<double>(History);
        readonly List<double> _dskHist = new List<double>(History);
        readonly List<double> _netHist = new List<double>(History);

        Sample _s = new Sample();
        int _cardTops0;
        bool _dragging; Point _dragStart;
        bool _pinned = true;
        Rectangle _rcPin, _rcMin, _rcClose;
        int _hoverBtn = -1;
        string _hover = null;          // 鼠标悬停提示
        Point _mouse;
        int _uiHeight;

        // 字体只创建一次
        readonly Font _fTitle = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
        readonly Font _fLabel = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
        readonly Font _fSmall = new Font("Microsoft YaHei UI", 9f);
        readonly Font _fTiny = new Font("Microsoft YaHei UI", 8f);
        readonly Font _fBig = new Font("Consolas", 29f, FontStyle.Bold);
        readonly Font _fUnit = new Font("Consolas", 11f);
        readonly Font _fMono = new Font("Consolas", 9f);
        readonly Font _fModel = new Font("Microsoft YaHei UI", 8.5f);

        public Ui(Form form, Collector col)
        {
            _form = form;
            _col = col;
            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => Tick();
        }

        public int UiHeight { get { return _uiHeight; } }

        public void Start()
        {
            Tick();
            _timer.Start();
        }

        public void Stop() { _timer.Stop(); }

        // ------------------------------------------------------------------ 数据
        void Tick()
        {
            try { _s = _col.Read(); }
            catch { /* 采集失败保留上一次的值，不让界面崩 */ }

            Push(_cpuHist, _s.CpuTotal);
            Push(_memHist, _s.MemTotalGB > 0 ? _s.MemUsedGB / _s.MemTotalGB : double.NaN);
            Push(_gpuHist, double.IsNaN(_s.GpuLoad) ? double.NaN : _s.GpuLoad / 100.0);
            Push(_dskHist, double.IsNaN(_s.DiskReadMBs) ? double.NaN : Math.Min(1.0, (_s.DiskReadMBs + _s.DiskWriteMBs) / 50.0));
            Push(_netHist, double.IsNaN(_s.NetDownMBs) ? double.NaN : Math.Min(1.0, (_s.NetDownMBs + _s.NetUpMBs) / 20.0));

            _form.Invalidate();
        }

        static void Push(List<double> list, double v)
        {
            if (list.Count >= History) list.RemoveAt(0);
            list.Add(double.IsNaN(v) ? 0 : v);
        }

        // ------------------------------------------------------------------ 排版
        // 卡高 = 公共块(HEAD+NUMH+BIG_DESC+GAPN+BARH+GAPB+SPARKH) + 内容起点 + 内容高 + 卡底留白。
        // 常量与 D:\DS\docs\build_mockup.py 一一对应，改一边必须同步另一边。
        // 主卡内容起点：大数字/进度条/曲线整块跑完之后，再空 GAPS
        const int ContentY = Head + NumH + BIG_DESC + GapN + BarH + GapB + SPARKH + GapS;
        // 磁盘/网络卡没有大数字块，起点是 HEAD + 10 之后再空 (GAPS-6)
        const int NetContentY = Head + 10 + (GapS - 6);

        int MetricCardH(int contentStart, int contentH)
        {
            return Head + NumH + BIG_DESC + GapN + BarH + GapB + SPARKH
                   + contentStart + contentH + PadBottom;
        }

        // 卡高直接取自效果图生成器 build_mockup.py 的实测值，
        // 不再各自推导系数 —— 两套算术各自演化正是之前布局失配的根源。
        int CpuCardH { get { return 205; } }
        int MemCardH { get { return 199; } }
        int GpuCardH { get { return 223 + 24; } }
        int NetCardH { get { return 138; } }

        public string DumpLayout()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Head={0} NumH={1} BigDesc={2} GapN={3} BarH={4} GapB={5} SparkH={6} GapS={7} " +
                "ContentY={8} NetContentY={9}\nCPU卡={10} 内存卡={11} GPU卡={12} 磁盘卡={13} " +
                "GapCard={14} PadBottom={15} HeaderH={16} TOP={17} UiHeight={18}",
                Head, NumH, BIG_DESC, GapN, BarH, GapB, SPARKH, GapS,
                ContentY, NetContentY, CpuCardH, MemCardH, GpuCardH, NetCardH,
                GapCard, PadBottom, HeaderH, TOP, _uiHeight);
        }

        public void Measure()
        {
            // 与效果图同一个恒等式：H = TOP + Σ卡高 + GAP*(卡数-1) + PAD_BOTTOM + 26
            _uiHeight = TOP + CpuCardH + GapCard + MemCardH + GapCard
                        + GpuCardH + GapCard + NetCardH + PadBottom + 26;
        }

        Rectangle CardRect(int index, int top, int h)
        {
            return new Rectangle(Margin, top, CardW, h);
        }

        // ------------------------------------------------------------------ 绘制
        public void Paint(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.Win);

            int cpuH = CpuCardH, memH = MemCardH, gpuH = GpuCardH;
            int netH = NetCardH;

            int y = HeaderH + 14;
            DrawCpu(g, y, cpuH); y += cpuH + GapCard;
            DrawMem(g, y, memH); y += memH + GapCard;
            DrawGpu(g, y, gpuH); y += gpuH + GapCard;
            DrawNet(g, y, netH); y += netH + PadBottom;

            DrawFooter(g, y);
            DrawHeader(g);
        }

        void DrawHeader(Graphics g)
        {
            using (var b = new SolidBrush(Theme.Panel))
                g.FillRectangle(b, 0, 0, W, HeaderH - 3);
            using (var p = new Pen(Theme.Edge))
                g.DrawLine(p, 0, HeaderH - 3, W, HeaderH - 3);

            int cy = (HeaderH - 3) / 2 + 1;
            using (var b = new SolidBrush(Theme.Accent))
                g.FillEllipse(b, Margin + 8, cy - 4, 8, 8);
            using (var b = new SolidBrush(Theme.Txt))
                g.DrawString("性能监测", _fTitle, b, Margin + 24, cy - _fTitle.Height / 2f - 1);

            int bx = W - Margin - 14;
            _rcClose = new Rectangle(bx - 13, cy - 13, 26, 26);
            _rcMin = new Rectangle(bx - 49, cy - 13, 26, 26);
            _rcPin = new Rectangle(bx - 85, cy - 13, 26, 26);

            // 置顶（开启时描边高亮）
            using (var b = new SolidBrush(Theme.Track))
                g.FillEllipse(b, _rcPin);
            if (_pinned)
            {
                using (var p = new Pen(Theme.Accent, 1.2f)) g.DrawEllipse(p, _rcPin);
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, _rcPin.X + 9, _rcPin.Y + 7, 8, 8);
            }
            using (var b = new SolidBrush(_hoverBtn == 0 ? Theme.Accent : Theme.Txt2))
            {
                g.FillRectangle(b, _rcMin.X + 7, _rcMin.Y + 12, 12, 2);
                using (var p = new Pen(b, 1.8f))
                {
                    g.DrawLine(p, _rcClose.X + 8, _rcClose.Y + 8, _rcClose.Right - 8, _rcClose.Bottom - 8);
                    g.DrawLine(p, _rcClose.Right - 8, _rcClose.Y + 8, _rcClose.X + 8, _rcClose.Bottom - 8);
                }
            }
        }

        void CardBack(Graphics g, Rectangle r)
        {
            using (var path = Round(r, 11))
            {
                using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Edge, 1f)) g.DrawPath(p, path);
            }
        }

        /// <summary>型号名过长会压到卡片标题，这里做精简与截断。</summary>
        static string ShortModel(string s, int maxPx)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("(R)", "").Replace("(TM)", "").Replace("CPU", "").Replace("  ", " ").Trim();
            int at = s.IndexOf(" @ ");
            if (at > 0) s = s.Substring(0, at).Trim();
            if (s.Length > maxPx) s = s.Substring(0, maxPx) + "…";
            return s;
        }

        void CardHead(Graphics g, Rectangle r, string title, string model, string right, Color accent)
        {
            using (var b = new SolidBrush(accent))
                g.FillRectangle(b, r.X + Pad, r.Y + 14, 3, 15);
            using (var b = new SolidBrush(accent))
                g.DrawString(title, _fLabel, b, r.X + Pad + 12, r.Y + 15);

            float xr = r.Right - Pad;
            if (!string.IsNullOrEmpty(right))
            {
                var sz = g.MeasureString(right, _fModel);
                g.DrawString(right, _fModel, Brushes.Gray, xr - sz.Width, r.Y + 17);
                if (!string.IsNullOrEmpty(model))
                {
                    using (var b = new SolidBrush(Theme.Txt2))
                    {
                        var m = g.MeasureString(model, _fModel);
                        g.DrawString(model, _fModel, b, xr - sz.Width - 14 - m.Width, r.Y + 17);
                    }
                }
            }
            else if (!string.IsNullOrEmpty(model))
            {
                using (var b = new SolidBrush(Theme.Txt2))
                {
                    var m = g.MeasureString(model, _fModel);
                    g.DrawString(model, _fModel, b, xr - m.Width, r.Y + 17);
                }
            }
        }

        /// <summary>大数字 + 单位 + 进度条 + 曲线（三张主卡共用）</summary>
        void MetricBlock(Graphics g, Rectangle r, string value, double pct, Color accent,
                         List<double> hist, string peakLabel)
        {
            int x = r.X + Pad;
            int nb = r.Y + Head + NumH;
            using (var b = new SolidBrush(Theme.Txt))
                g.DrawString(value, _fBig, b, x, nb - _fBig.Height + 4);
            var vw = g.MeasureString(value, _fBig).Width;
            using (var b = new SolidBrush(Theme.Txt2))
                g.DrawString("%", _fUnit, b, x + vw - 2, nb - _fUnit.Height - 2);

            int by = nb + GapN + 2;
            DrawBar(g, x, by, ContentW, BarH, pct, accent);

            int sy = by + BarH + GapB;
            DrawSpark(g, x, sy, ContentW, SPARKH, hist, accent, peakLabel);
        }

        void DrawCpu(Graphics g, int top, int h)
        {
            var r = CardRect(0, top, h);
            CardBack(g, r);
            CardHead(g, r, "CPU", ShortModel(_s.CpuName, 26), "总负载", Theme.Accent);
            double pct = double.IsNaN(_s.CpuTotal) ? 0 : _s.CpuTotal;
            MetricBlock(g, r, double.IsNaN(_s.CpuTotal) ? "--" : ((int)Math.Round(pct)).ToString(),
                        pct / 100.0, Theme.Accent, _cpuHist,
                        "峰值 " + Pct(_cpuHist));

            int y = r.Y + ContentY;
            double peak = 0;
            var cores = _s.CpuCores;
            int n = cores.Length > 0 ? cores.Length : 1;
            for (int i = 0; i < cores.Length; i++)
                if (!double.IsNaN(cores[i]) && cores[i] > peak) peak = cores[i];

            // 标签与峰值都放左侧同一行，彻底避开与右上「总负载」文字相撞
            using (var b = new SolidBrush(Theme.Txt2))
                g.DrawString("每核心　最高 " + ((int)Math.Round(peak)) + "%", _fSmall, b, r.X + Pad, y + 2);

            int stripX = r.X + Pad + 108, stripW = ContentW - 108;
            float gap = 6f, bw = (stripW - gap * (n - 1)) / n;
            int barH = 22;
            for (int i = 0; i < n; i++)
            {
                float bx = stripX + i * (bw + gap);
                using (var b = new SolidBrush(Theme.Track))
                    FillRound(g, bx, y - 2, bw, barH, 2.5f, b);
                double v = i < cores.Length && !double.IsNaN(cores[i]) ? cores[i] : 0;
                float fh = Math.Max(3f, (float)(v / 100.0 * barH));
                using (var b = new SolidBrush(v > 80 ? Theme.Warn : Theme.Accent))
                    FillRound(g, bx, y - 2 + (barH - fh), bw, fh, 2.5f, b);
            }
        }

        void DrawMem(Graphics g, int top, int h)
        {
            var r = CardRect(1, top, h);
            CardBack(g, r);
            CardHead(g, r, "内存", "共 " + Fmt(_s.MemTotalGB, 1) + " GB", "占用率", Theme.Accent2);
            double pct = (_s.MemTotalGB > 0) ? _s.MemUsedGB / _s.MemTotalGB : 0;
            MetricBlock(g, r, double.IsNaN(_s.MemUsedGB) ? "--" : ((int)Math.Round(pct * 100)).ToString(),
                        pct, Theme.Accent2, _memHist, "峰值 " + Pct(_memHist));

            int y = r.Y + ContentY;
            using (var b = new SolidBrush(Theme.Txt2))
            {
                string t = "已用 " + Fmt(_s.MemUsedGB, 1) + " GB   ·   可用 " +
                           Fmt(_s.MemTotalGB - _s.MemUsedGB, 1) + " GB   ·   共 " + Fmt(_s.MemTotalGB, 1) + " GB";
                g.DrawString(t, _fSmall, b, r.X + Pad, y);
            }
        }

        void DrawGpu(Graphics g, int top, int h)
        {
            var r = CardRect(2, top, h);
            CardBack(g, r);
            string model = string.IsNullOrEmpty(_s.GpuName) ? (_s.HasNvidia ? "NVIDIA GPU" : "未检测到 NVIDIA 显卡") : _s.GpuName;
            CardHead(g, r, "GPU", ShortModel(model, 24), "3D 负载", Theme.Accent2);
            double pct = double.IsNaN(_s.GpuLoad) ? 0 : _s.GpuLoad / 100.0;
            MetricBlock(g, r, double.IsNaN(_s.GpuLoad) ? "--" : ((int)Math.Round(_s.GpuLoad)).ToString(),
                        pct, Theme.Accent2, _gpuHist, "峰值 " + Pct(_gpuHist));

            int y = r.Y + ContentY;
            // GPU 明细：两列 × 三行，列宽写死，长数值绝不会挤到旁边标签
            int c1 = r.X + Pad + 2;
            int c2 = r.X + Pad + 158;
            int rowH = 24;
            Mini(g, c1, y, "显存", Fmt(_s.VramUsedMB, 0) + " / " + Fmt(_s.VramTotalMB, 0));
            Mini(g, c2, y, "温度", Fmt(_s.GpuTempC, 0) + " ℃");
            Mini(g, c1, y + rowH, "功耗", Fmt(_s.GpuPowerW, 0) + "W / " + Fmt(_s.GpuPowerCapW, 0) + "W");
            Mini(g, c2, y + rowH, "风扇", Fmt(_s.GpuFanPct, 0) + " %");
            Mini(g, c1, y + rowH * 2, "频率", Fmt(_s.GpuClockMHz, 0) + " MHz");
            Mini(g, c2, y + rowH * 2, "驱动", _s.HasNvidia ? "PDH + SMI" : "PDH");
        }

        void DrawNet(Graphics g, int top, int h)
        {
            var r = CardRect(3, top, h);
            CardBack(g, r);
            CardHead(g, r, "磁盘 / 网络", "单位 MB/s", null, Theme.Accent);
            int y = r.Y + NetContentY;
            Row(g, r, y, "磁盘 C:", "读", _s.DiskReadMBs, "写", _s.DiskWriteMBs, _dskHist, Theme.Accent);
            Row(g, r, y + 32, "网络", "下", _s.NetDownMBs, "上", _s.NetUpMBs, _netHist, Theme.Accent2);
        }

        void Row(Graphics g, Rectangle r, int y, string group, string l1, double v1,
                 string l2, double v2, List<double> hist, Color c)
        {
            using (var b = new SolidBrush(Theme.Accent))
                g.DrawString(group, _fSmall, b, r.X + Pad, y + 2);
            int vx = r.X + Pad + 66;
            using (var b = new SolidBrush(Theme.Txt2))
                g.DrawString(l1, _fSmall, b, vx, y + 2);
            using (var b = new SolidBrush(Theme.Txt))
                g.DrawString(Fmt(v1, 1), _fMono, b, vx + 20, y + 3);
            vx += 52;
            using (var b = new SolidBrush(Theme.Txt2))
                g.DrawString(l2, _fSmall, b, vx, y + 2);
            using (var b = new SolidBrush(Theme.Txt))
                g.DrawString(Fmt(v2, 1), _fMono, b, vx + 20, y + 3);

            int sparkW = Math.Max(60, r.Right - Pad - (r.X + Pad + 268));
            DrawSpark(g, r.X + Pad + 268, y - 1, sparkW, 20, hist, c, null);
        }

        void DrawFooter(Graphics g, int y)
        {
            using (var p = new Pen(Theme.Edge))
                g.DrawLine(p, Margin, y, W - Margin, y);

            double selfMB = 0;
            try { using (var me = Process.GetCurrentProcess()) { me.Refresh(); selfMB = me.WorkingSet64 / 1048576.0; } }
            catch { }
            string left = "本程序内存 " + selfMB.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            string right = "每秒刷新";
            var lw = g.MeasureString(left, _fMono).Width;
            var rw = g.MeasureString(right, _fSmall).Width;
            float gap = 34, total = lw + gap + rw;
            float sx = (W - total) / 2f;

            using (var b = new SolidBrush(Theme.Txt2))
            {
                g.DrawString(left, _fMono, b, sx, y + 8);
                g.DrawString(right, _fSmall, b, sx + lw + gap, y + 7);
            }
            using (var b = new SolidBrush(Theme.Accent))
                g.FillEllipse(b, sx + lw + gap / 2f - 3.5f, y + 12, 7, 7);
        }

        // ------------------------------------------------------------------ 小部件
        void Mini(Graphics g, int x, int y, string label, string value)
        {
            using (var b = new SolidBrush(Theme.Txt2))
                g.DrawString(label, _fSmall, b, x, y + 2);
            using (var b = new SolidBrush(Theme.Txt))
                g.DrawString(value, _fMono, b, x + 64, y + 3);
        }

        void DrawBar(Graphics g, int x, int y, int w, int h, double pct, Color c)
        {
            using (var b = new SolidBrush(Theme.Track))
                FillRound(g, x, y, w, h, h / 2f, b);
            double p = Math.Max(0, Math.Min(1, pct));
            float fw = (float)(p * w);
            if (fw >= 1)
            {
                if (fw < h) fw = h;
                using (var b = new SolidBrush(LoadColor(c, p)))
                    FillRound(g, x, y, fw, h, h / 2f, b);
            }
        }

        static Color LoadColor(Color baseColor, double p)
        {
            if (p >= 0.92) return Theme.Danger;
            if (p >= 0.80) return Theme.Warn;
            return baseColor;
        }

        void DrawSpark(Graphics g, int x, int y, int w, int h, List<double> vals, Color c, string label)
        {
            using (var p = new Pen(Theme.Edge, 1f)) g.DrawLine(p, x, y + h, x + w, y + h);
            using (var p = new Pen(Theme.Edge, 0.6f)) g.DrawLine(p, x, y, x + w, y);

            if (vals.Count >= 2)
            {
                double lo = double.MaxValue, hi = double.MinValue;
                for (int i = 0; i < vals.Count; i++) { if (vals[i] < lo) lo = vals[i]; if (vals[i] > hi) hi = vals[i]; }
                double rng = hi - lo;
                var pts = new PointF[vals.Count];
                for (int i = 0; i < vals.Count; i++)
                {
                    double t = rng < 1e-9 ? 0.5 : (vals[i] - lo) / rng * 0.85;
                    pts[i] = new PointF(x + w * i / (float)(vals.Count - 1), (float)(y + h - t * h));
                }
                using (var p = new Pen(c, 2f))
                {
                    p.LineJoin = LineJoin.Round; p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    g.DrawLines(p, pts);
                }
            }
            if (label != null)
            {
                using (var b = new SolidBrush(Theme.Txt2))
                {
                    var sz = g.MeasureString(label, _fTiny);
                    g.DrawString(label, _fTiny, b, x + w - sz.Width, y + 3);
                }
            }
        }

        static string Pct(List<double> vals)
        {
            double hi = 0;
            for (int i = 0; i < vals.Count; i++) if (vals[i] > hi) hi = vals[i];
            return ((int)Math.Round(hi * 100)) + "%";
        }

        static string Fmt(double v, int digits)
        {
            if (double.IsNaN(v)) return "--";
            return v.ToString("0." + new string('0', digits), CultureInfo.InvariantCulture);
        }

        public static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            if (rad * 2 > r.Height) rad = r.Height / 2;
            if (rad * 2 > r.Width) rad = r.Width / 2;
            p.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
            p.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
            p.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
            p.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
            p.CloseFigure();
            return p;
        }

        static void FillRound(Graphics g, float x, float y, float w, float h, float rad, Brush b)
        {
            if (w < 1 || h < 1) return;
            using (var p = Round(new Rectangle((int)Math.Round(x), (int)Math.Round(y),
                                               (int)Math.Round(w), (int)Math.Round(h)), (int)rad))
                g.FillPath(b, p);
        }

        // ------------------------------------------------------------------ 交互
        public void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (_rcClose.Contains(e.Location)) { _form.Close(); return; }
            if (_rcMin.Contains(e.Location)) { _form.WindowState = FormWindowState.Minimized; return; }
            if (_rcPin.Contains(e.Location)) { _pinned = !_pinned; _form.TopMost = _pinned; _form.Invalidate(); return; }
            _dragging = true;
            _dragStart = e.Location;
        }

        public void OnMouseMove(MouseEventArgs e)
        {
            _mouse = e.Location;
            int btn = _rcClose.Contains(e.Location) ? 2 : (_rcMin.Contains(e.Location) ? 1 :
                      (_rcPin.Contains(e.Location) ? 0 : -1));
            if (btn != _hoverBtn) { _hoverBtn = btn; _form.Invalidate(); }

            if (_dragging)
            {
                var sp = _form.PointToScreen(e.Location);
                _form.Location = new Point(sp.X - _dragStart.X, sp.Y - _dragStart.Y);
            }
        }

        public void OnMouseUp() { _dragging = false; }

        public void OnResize()
        {
            try
            {
                using (var p = Round(new Rectangle(0, 0, _form.ClientSize.Width, _form.ClientSize.Height), 12))
                    _form.Region = new Region(p);
            }
            catch { }
        }
    }
}
