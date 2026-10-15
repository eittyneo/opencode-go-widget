using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OpenCodeQuotaWidget
{
    public class WindowMeter
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public double UsedUsd { get; set; }
        public double LimitUsd { get; set; }
        public double RemainUsd { get; set; }
        public double UsedPercent { get; set; }
        public double RemainPercent { get; set; }
        public string ResetsAtIso { get; set; }
        public string CountdownText { get; set; }
    }

    public class QuotaSnapshot
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string Plan { get; set; }
        public string UpdatedAt { get; set; }
        public List<WindowMeter> Meters { get; set; }

        public QuotaSnapshot()
        {
            Meters = new List<WindowMeter>();
        }
    }

    public class WidgetSettings
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double Opacity { get; set; }
        public bool TopMost { get; set; }
        public bool ShowRemaining { get; set; }
        public bool MiniMode { get; set; }
        public int SavedFullHeight { get; set; }
        public string Language { get; set; }

        public WidgetSettings()
        {
            X = -1;
            Y = -1;
            Width = 320;
            Height = 220;
            Opacity = 0.92;
            TopMost = true;
            ShowRemaining = true;
            MiniMode = false;
            SavedFullHeight = 220;
            Language = "auto";
        }
    }

    public class QuotaWidgetForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;
        private const int WM_NCHITTEST = 0x84;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        /// <summary>UI language: true = Chinese. Chosen by settings Language ("auto"/"en"/"zh") and the system locale.</summary>
        private static bool _isZh;

        private static string L(string en, string zh)
        {
            return _isZh ? zh : en;
        }

        private readonly string _baseDir;
        private readonly string _settingsPath;
        private readonly string _liveCachePath;
        private readonly bool _standaloneMode;

        private WidgetSettings _settings;
        private QuotaSnapshot _snapshot;
        private bool _isRefreshing = false;
        private bool _hasSeenOpenCode = false;
        private readonly DateTime _startTime = DateTime.UtcNow;

        private Rectangle _btnRefreshRect;
        private Rectangle _btnModeRect;
        private Rectangle _btnOpacityDownRect;
        private Rectangle _btnOpacityUpRect;
        private Rectangle _btnMiniRect;
        private Rectangle _btnPinRect;
        private string _hoverButton = null;

        private System.Windows.Forms.Timer _lifecycleTimer;
        private System.Windows.Forms.Timer _autoRefreshTimer;
        private System.Windows.Forms.Timer _countdownTimer;
        private FileSystemWatcher _cacheWatcher;
        private ContextMenuStrip _contextMenu;

        public QuotaWidgetForm(bool standaloneMode)
        {
            _standaloneMode = standaloneMode;
            _baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _settingsPath = Path.Combine(_baseDir, "widget-settings.json");
            _liveCachePath = Path.Combine(_baseDir, "quota-live.json");

            LoadSettings();

            string lang = (_settings.Language ?? "auto").Trim().ToLowerInvariant();
            _isZh = lang == "zh" || (lang != "en" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase));

            this.Text = "OpenCode Go Widget";
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.DoubleBuffered = true;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.BackColor = Color.FromArgb(20, 22, 28);
            this.ForeColor = Color.White;
            this.MinimumSize = new Size(240, 56);
            this.TopMost = _settings.TopMost;
            this.Opacity = Math.Max(0.25, Math.Min(1.0, _settings.Opacity));

            int initialW = Math.Max(240, _settings.Width);
            int initialH = _settings.MiniMode ? 56 : Math.Max(165, _settings.Height);
            this.Size = new Size(initialW, initialH);

            if (_settings.X >= 0 && _settings.Y >= 0 && IsOnScreen(_settings.X, _settings.Y, initialW, initialH))
            {
                this.StartPosition = FormStartPosition.Manual;
                this.Location = new Point(_settings.X, _settings.Y);
            }
            else
            {
                this.StartPosition = FormStartPosition.Manual;
                Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
                this.Location = new Point(workArea.Right - initialW - 24, workArea.Top + 48);
            }

            BuildContextMenu();
            SetupFileWatcher();

            _snapshot = new QuotaSnapshot
            {
                Ok = false,
                Plan = "OpenCode Go",
                UpdatedAt = L("loading", "加载中..."),
                Error = L("Fetching quota data...", "正在读取额度数据...")
            };

            _lifecycleTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _lifecycleTimer.Tick += CheckOpenCodeLifecycle;
            _lifecycleTimer.Start();

            _autoRefreshTimer = new System.Windows.Forms.Timer { Interval = 120000 };
            _autoRefreshTimer.Tick += async (s, e) => await RefreshQuotaAsync(false);
            _autoRefreshTimer.Start();

            _countdownTimer = new System.Windows.Forms.Timer { Interval = 30000 };
            _countdownTimer.Tick += (s, e) =>
            {
                UpdateCountdowns();
                this.Invalidate();
                TrimMemory();
            };
            _countdownTimer.Start();

            this.Shown += async (s, e) =>
            {
                if (!TryLoadFromCacheFile())
                {
                    await RefreshQuotaAsync(true);
                }
                else
                {
                    this.Invalidate();
                    await RefreshQuotaAsync(false);
                }
                TrimMemory();
            };

            this.LocationChanged += (s, e) =>
            {
                if (this.WindowState == FormWindowState.Normal)
                {
                    _settings.X = this.Left;
                    _settings.Y = this.Top;
                }
            };

            this.SizeChanged += (s, e) =>
            {
                if (this.WindowState == FormWindowState.Normal)
                {
                    _settings.Width = this.Width;
                    if (!_settings.MiniMode && this.Height >= 150)
                    {
                        _settings.Height = this.Height;
                        _settings.SavedFullHeight = this.Height;
                    }
                    UpdateRegion();
                    this.Invalidate();
                }
            };

            this.FormClosing += (s, e) => SaveSettings();
            UpdateRegion();
        }

        private static void TrimMemory()
        {
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
                GC.WaitForPendingFinalizers();
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }

        private bool IsOnScreen(int x, int y, int w, int h)
        {
            Rectangle rect = new Rectangle(x, y, w, h);
            foreach (Screen screen in Screen.AllScreens)
            {
                if (screen.WorkingArea.IntersectsWith(rect))
                    return true;
            }
            return false;
        }

        private void UpdateRegion()
        {
            try
            {
                using (GraphicsPath path = CreateRoundedRect(new Rectangle(0, 0, this.Width, this.Height), 12))
                {
                    Region old = this.Region;
                    this.Region = new Region(path);
                    if (old != null) old.Dispose();
                }
            }
            catch { }
        }

        private void BuildContextMenu()
        {
            _contextMenu = new ContextMenuStrip();
            _contextMenu.Items.Add(L("↻ Refresh now (F5)", "↻ 立即刷新额度 (F5)"), null, async (s, e) => await RefreshQuotaAsync(true));
            _contextMenu.Items.Add(L("⇄ Toggle Remaining / Used", "⇄ 切换 剩余 / 已用 显示"), null, (s, e) => ToggleDisplayMode());
            _contextMenu.Items.Add(L("▤ Toggle full / mini mode (double-click)", "▤ 切换 完整 / 迷你 模式 (双击)"), null, (s, e) => ToggleMiniMode());

            var topMostItem = new ToolStripMenuItem(L("📌 Always on top", "📌 窗口始终置顶")) { Checked = _settings.TopMost };
            topMostItem.Click += (s, e) =>
            {
                _settings.TopMost = !_settings.TopMost;
                this.TopMost = _settings.TopMost;
                topMostItem.Checked = _settings.TopMost;
                SaveSettings();
                this.Invalidate();
            };
            _contextMenu.Items.Add(topMostItem);

            var opacityMenu = new ToolStripMenuItem(L("◐ Opacity (Ctrl+scroll)", "◐ 调整透明度 (Ctrl+滚轮)"));
            int[] opacities = new[] { 100, 95, 90, 85, 80, 70, 60, 50, 40 };
            foreach (int pct in opacities)
            {
                int captured = pct;
                opacityMenu.DropDownItems.Add(captured + "%", null, (s, e) => SetOpacity(captured / 100.0));
            }
            _contextMenu.Items.Add(opacityMenu);

            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(L("✕ Hide & close widget", "✕ 隐藏关闭悬浮窗"), null, (s, e) => this.Close());
            this.ContextMenuStrip = _contextMenu;
        }

        private void SetupFileWatcher()
        {
            try
            {
                _cacheWatcher = new FileSystemWatcher(_baseDir, "quota-live.json");
                _cacheWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size;
                _cacheWatcher.Changed += (s, e) =>
                {
                    Thread.Sleep(80);
                    if (this.IsHandleCreated)
                    {
                        this.BeginInvoke((MethodInvoker)delegate
                        {
                            if (TryLoadFromCacheFile())
                                this.Invalidate();
                        });
                    }
                };
                _cacheWatcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        private void CheckOpenCodeLifecycle(object sender, EventArgs e)
        {
            if (_standaloneMode)
                return;

            try
            {
                Process[] procs = Process.GetProcessesByName("OpenCode");
                if (procs != null && procs.Length > 0)
                {
                    _hasSeenOpenCode = true;
                    foreach (var p in procs) p.Dispose();
                }
                else
                {
                    if (_hasSeenOpenCode || (DateTime.UtcNow - _startTime).TotalSeconds > 20)
                    {
                        SaveSettings();
                        Application.Exit();
                    }
                }
            }
            catch { }
        }

        private void SetOpacity(double value)
        {
            _settings.Opacity = Math.Max(0.25, Math.Min(1.0, Math.Round(value, 2)));
            this.Opacity = _settings.Opacity;
            SaveSettings();
            this.Invalidate();
        }

        private void ToggleDisplayMode()
        {
            _settings.ShowRemaining = !_settings.ShowRemaining;
            SaveSettings();
            this.Invalidate();
        }

        private void ToggleMiniMode()
        {
            _settings.MiniMode = !_settings.MiniMode;
            if (_settings.MiniMode)
            {
                _settings.SavedFullHeight = Math.Max(165, this.Height);
                this.Height = 56;
            }
            else
            {
                this.Height = Math.Max(165, _settings.SavedFullHeight);
            }
            SaveSettings();
            UpdateRegion();
            this.Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
            {
                double delta = e.Delta > 0 ? 0.05 : -0.05;
                SetOpacity(_settings.Opacity + delta);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.F5)
            {
                var _ = RefreshQuotaAsync(true);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string hit = HitTestButton(e.Location);
            if (hit != _hoverButton)
            {
                _hoverButton = hit;
                this.Cursor = hit != null ? Cursors.Hand : Cursors.Default;
                this.Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverButton != null)
            {
                _hoverButton = null;
                this.Cursor = Cursors.Default;
                this.Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                string hit = HitTestButton(e.Location);
                if (hit == "refresh")
                {
                    var _ = RefreshQuotaAsync(true);
                    return;
                }
                if (hit == "mode")
                {
                    ToggleDisplayMode();
                    return;
                }
                if (hit == "op_down")
                {
                    SetOpacity(_settings.Opacity - 0.05);
                    return;
                }
                if (hit == "op_up")
                {
                    SetOpacity(_settings.Opacity + 0.05);
                    return;
                }
                if (hit == "mini")
                {
                    ToggleMiniMode();
                    return;
                }
                if (hit == "pin")
                {
                    _settings.TopMost = !_settings.TopMost;
                    this.TopMost = _settings.TopMost;
                    SaveSettings();
                    this.Invalidate();
                    return;
                }

                ReleaseCapture();
                SendMessage(this.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && HitTestButton(e.Location) == null)
            {
                ToggleMiniMode();
            }
        }

        private string HitTestButton(Point pt)
        {
            if (_btnRefreshRect.Contains(pt)) return "refresh";
            if (_btnModeRect.Contains(pt)) return "mode";
            if (_btnOpacityDownRect.Contains(pt)) return "op_down";
            if (_btnOpacityUpRect.Contains(pt)) return "op_up";
            if (_btnMiniRect.Contains(pt)) return "mini";
            if (_btnPinRect.Contains(pt)) return "pin";
            return null;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST && (int)m.Result == 1)
            {
                Point pt = this.PointToClient(new Point(m.LParam.ToInt32() & 0xFFFF, m.LParam.ToInt32() >> 16));
                int grip = 7;

                if (!_settings.MiniMode)
                {
                    if (pt.X >= this.ClientSize.Width - 14 && pt.Y >= this.ClientSize.Height - 14)
                    {
                        m.Result = (IntPtr)HTBOTTOMRIGHT;
                        return;
                    }
                    if (pt.X <= grip && pt.Y <= grip) { m.Result = (IntPtr)HTTOPLEFT; return; }
                    if (pt.X >= this.ClientSize.Width - grip && pt.Y <= grip) { m.Result = (IntPtr)HTTOPRIGHT; return; }
                    if (pt.X <= grip && pt.Y >= this.ClientSize.Height - grip) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
                    if (pt.Y <= grip) { m.Result = (IntPtr)HTTOP; return; }
                    if (pt.Y >= this.ClientSize.Height - grip) { m.Result = (IntPtr)HTBOTTOM; return; }
                }
                if (pt.X <= grip) { m.Result = (IntPtr)HTLEFT; return; }
                if (pt.X >= this.ClientSize.Width - grip) { m.Result = (IntPtr)HTRIGHT; return; }
            }
        }

        private async Task RefreshQuotaAsync(bool forceRefresh)
        {
            if (_isRefreshing) return;
            _isRefreshing = true;
            this.Invalidate();

            try
            {
                QuotaSnapshot fetched = await Task.Run(() => FetchQuotaSnapshot(forceRefresh));
                if (fetched != null)
                {
                    _snapshot = fetched;
                    UpdateCountdowns();
                }
            }
            catch (Exception ex)
            {
                _snapshot = new QuotaSnapshot
                {
                    Ok = false,
                    Plan = "OpenCode Go",
                    UpdatedAt = DateTime.Now.ToString("HH:mm:ss"),
                    Error = ex.Message
                };
            }
            finally
            {
                _isRefreshing = false;
                this.Invalidate();
                TrimMemory();
            }
        }

        private QuotaSnapshot FetchQuotaSnapshot(bool forceRefresh)
        {
            try
            {
                string url = "http://127.0.0.1:19876/quota" + (forceRefresh ? "?refresh=1" : "");
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    QuotaSnapshot parsed = ParseQuotaJson(json);
                    if (parsed != null && (parsed.Ok || !string.IsNullOrEmpty(parsed.Error)))
                    {
                        return parsed;
                    }
                }
            }
            catch { }

            string fetcherScript = Path.Combine(_baseDir, "fetch-quota.mjs");
            if (File.Exists(fetcherScript))
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "node",
                        Arguments = "\"" + fetcherScript + "\"",
                        WorkingDirectory = _baseDir,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        StandardOutputEncoding = Encoding.UTF8
                    };
                    using (Process p = Process.Start(psi))
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(10000);
                        QuotaSnapshot parsed = ParseQuotaJson(output);
                        if (parsed != null)
                            return parsed;
                    }
                }
                catch { }
            }

            if (File.Exists(_liveCachePath))
            {
                try
                {
                    string json = File.ReadAllText(_liveCachePath, Encoding.UTF8);
                    QuotaSnapshot parsed = ParseQuotaJson(json);
                    if (parsed != null)
                        return parsed;
                }
                catch { }
            }

            return new QuotaSnapshot
            {
                Ok = false,
                Plan = "OpenCode Go",
                UpdatedAt = DateTime.Now.ToString("HH:mm:ss"),
                Error = L("Unable to fetch quota, click refresh to retry", "未能获取额度，请点击刷新重试")
            };
        }

        private bool TryLoadFromCacheFile()
        {
            try
            {
                if (!File.Exists(_liveCachePath)) return false;
                string json = File.ReadAllText(_liveCachePath, Encoding.UTF8);
                QuotaSnapshot parsed = ParseQuotaJson(json);
                if (parsed != null && parsed.Ok)
                {
                    _snapshot = parsed;
                    UpdateCountdowns();
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static string ExtractString(string src, string key, string fallback)
        {
            Match m = Regex.Match(src, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : fallback;
        }

        private static double ExtractDouble(string src, string key, double fallback)
        {
            Match m = Regex.Match(src, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)");
            double val;
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                return val;
            return fallback;
        }

        private static bool ExtractBool(string src, string key, bool fallback)
        {
            Match m = Regex.Match(src, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (m.Success)
                return string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
            return fallback;
        }

        private QuotaSnapshot ParseQuotaJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            var snap = new QuotaSnapshot
            {
                Ok = ExtractBool(json, "ok", false),
                Plan = ExtractString(json, "plan", "OpenCode Go"),
                UpdatedAt = ExtractString(json, "updatedAt", DateTime.Now.ToString("HH:mm:ss")),
                Error = ExtractString(json, "error", null)
            };

            Match metersMatch = Regex.Match(json, "\"meters\"\\s*:\\s*\\[(.*)\\]", RegexOptions.Singleline);
            if (metersMatch.Success)
            {
                string metersContent = metersMatch.Groups[1].Value;
                MatchCollection objs = Regex.Matches(metersContent, "\\{[^{}]+\\}");
                foreach (Match obj in objs)
                {
                    string block = obj.Value;
                    snap.Meters.Add(new WindowMeter
                    {
                        Key = ExtractString(block, "key", ""),
                        Label = ExtractString(block, "label", ""),
                        UsedUsd = ExtractDouble(block, "usedUsd", 0),
                        LimitUsd = ExtractDouble(block, "limitUsd", 0),
                        RemainUsd = ExtractDouble(block, "remainUsd", 0),
                        UsedPercent = ExtractDouble(block, "usedPercent", 0),
                        RemainPercent = ExtractDouble(block, "remainPercent", 0),
                        ResetsAtIso = ExtractString(block, "resetsAt", "")
                    });
                }
            }

            return snap;
        }

        private void UpdateCountdowns()
        {
            if (_snapshot == null || _snapshot.Meters == null) return;
            DateTime now = DateTime.UtcNow;
            foreach (var m in _snapshot.Meters)
            {
                DateTime resetUtc;
                if (DateTime.TryParse(m.ResetsAtIso, null, DateTimeStyles.AdjustToUniversal, out resetUtc))
                {
                    TimeSpan diff = resetUtc - now;
                    if (diff.TotalSeconds <= 0)
                    {
                        m.CountdownText = L("resetting soon", "即将重置");
                    }
                    else if (diff.TotalDays >= 1)
                    {
                        int d = (int)Math.Floor(diff.TotalDays);
                        int h = diff.Hours;
                        m.CountdownText = string.Format("{0}d {1}h", d, h);
                    }
                    else if (diff.TotalHours >= 1)
                    {
                        int h = (int)Math.Floor(diff.TotalHours);
                        int min = diff.Minutes;
                        m.CountdownText = string.Format("{0}h {1}m", h, min);
                    }
                    else
                    {
                        int min = Math.Max(1, (int)Math.Ceiling(diff.TotalMinutes));
                        m.CountdownText = string.Format("{0}m", min);
                    }
                }
                else
                {
                    m.CountdownText = "-";
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            int w = this.ClientSize.Width;
            int h = this.ClientSize.Height;

            using (LinearGradientBrush bgBrush = new LinearGradientBrush(
                new Rectangle(0, 0, w, h),
                Color.FromArgb(24, 27, 36),
                Color.FromArgb(16, 18, 24),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(bgBrush, 0, 0, w, h);
            }

            if (_settings.MiniMode)
            {
                PaintMiniMode(g, w, h);
            }
            else
            {
                PaintFullMode(g, w, h);
            }

            using (GraphicsPath borderPath = CreateRoundedRect(new Rectangle(0, 0, w - 1, h - 1), 12))
            using (Pen borderPen = new Pen(Color.FromArgb(58, 68, 90), 1f))
            {
                g.DrawPath(borderPen, borderPath);
            }
        }

        private void PaintMiniMode(Graphics g, int w, int h)
        {
            _btnModeRect = Rectangle.Empty;
            _btnOpacityDownRect = Rectangle.Empty;
            _btnOpacityUpRect = Rectangle.Empty;
            _btnPinRect = Rectangle.Empty;

            int btnW = 24;
            int btnH = 22;
            int rightX = w - 10;

            _btnMiniRect = new Rectangle(rightX - btnW, (h - btnH) / 2, btnW, btnH);
            rightX -= (btnW + 4);
            _btnRefreshRect = new Rectangle(rightX - btnW, (h - btnH) / 2, btnW, btnH);
            rightX -= (btnW + 8);

            DrawPillButton(g, _btnRefreshRect, _isRefreshing ? "…" : "↻", _hoverButton == "refresh", false);
            DrawPillButton(g, _btnMiniRect, "▾", _hoverButton == "mini", false);

            using (Font fBold = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
            using (Brush accentBrush = new SolidBrush(Color.FromArgb(96, 165, 250)))
            using (Brush textBrush = new SolidBrush(Color.FromArgb(230, 236, 245)))
            {
                int x = 12;
                string accent = _snapshot != null && !string.IsNullOrEmpty(_snapshot.Plan) && _snapshot.Plan.IndexOf("Plus", StringComparison.OrdinalIgnoreCase) >= 0 ? "Go+" : "Go";
                g.DrawString(accent, fBold, accentBrush, x, (h - fBold.Height) / 2);
                x += 28;

                if (_snapshot != null && _snapshot.Ok && _snapshot.Meters.Count > 0)
                {
                    foreach (var m in _snapshot.Meters)
                    {
                        double pct = _settings.ShowRemaining ? m.RemainPercent : m.UsedPercent;
                        string shortName = m.Key == "fiveHour" ? "5h" : (m.Key == "week" ? L("Wk", "周") : L("Mo", "月"));
                        Color c = GetQuotaColor(m.RemainPercent);

                        string itemText = string.Format("{0}:{1:0}%", shortName, pct);
                        SizeF sz = g.MeasureString(itemText, fBold);
                        if (x + sz.Width < rightX)
                        {
                            using (Brush b = new SolidBrush(c))
                            {
                                g.DrawString(itemText, fBold, b, x, (h - fBold.Height) / 2);
                            }
                            x += (int)sz.Width + 8;
                        }
                    }
                }
                else
                {
                    g.DrawString(_isRefreshing ? L("refreshing...", "刷新中...") : L("Click ↻ to refresh", "点击右侧 ↻ 刷新"), fBold, textBrush, x, (h - fBold.Height) / 2);
                }
            }
        }

        private void PaintFullMode(Graphics g, int w, int h)
        {
            float scale = Math.Max(0.85f, Math.Min(1.35f, w / 320f));
            int pad = (int)(12 * scale);

            int headerY = 10;
            int btnH = 22;

            int rightX = w - pad;
            _btnMiniRect = new Rectangle(rightX - 24, headerY, 24, btnH);
            rightX -= 28;

            _btnPinRect = new Rectangle(rightX - 24, headerY, 24, btnH);
            rightX -= 28;

            int refreshW = 56;
            _btnRefreshRect = new Rectangle(rightX - refreshW, headerY, refreshW, btnH);
            rightX -= (refreshW + 6);

            using (Brush dotBrush = new SolidBrush(_snapshot != null && _snapshot.Ok ? Color.FromArgb(52, 211, 153) : Color.FromArgb(251, 191, 36)))
            {
                g.FillEllipse(dotBrush, pad, headerY + 6, 8, 8);
            }

            using (Font titleFont = new Font("Microsoft YaHei UI", 9.5f * scale, FontStyle.Bold))
            using (Brush titleBrush = new SolidBrush(Color.FromArgb(240, 244, 250)))
            {
                string title = _snapshot != null && !string.IsNullOrEmpty(_snapshot.Plan) ? _snapshot.Plan : "OpenCode Go";
                g.DrawString(title, titleFont, titleBrush, pad + 13, headerY + 1);
            }

            DrawPillButton(g, _btnRefreshRect, _isRefreshing ? "…" : L("↻ Refresh", "↻ 刷新"), _hoverButton == "refresh", _isRefreshing);
            DrawPillButton(g, _btnPinRect, L("Top", "顶"), _hoverButton == "pin", _settings.TopMost);
            DrawPillButton(g, _btnMiniRect, "▴", _hoverButton == "mini", false);

            int footerH = 24;
            int footerY = h - footerH - 6;

            using (Font subFont = new Font("Microsoft YaHei UI", 7.8f, FontStyle.Regular))
            using (Brush subBrush = new SolidBrush(Color.FromArgb(140, 150, 170)))
            {
                string timeStr = _snapshot != null ? (L("Updated ", "更新: ") + _snapshot.UpdatedAt) : "";
                g.DrawString(timeStr, subFont, subBrush, pad, footerY + 4);
            }

            int fRight = w - pad - 8;
            _btnOpacityUpRect = new Rectangle(fRight - 20, footerY + 1, 20, 20);
            fRight -= 22;
            int opLabelW = 36;
            Rectangle opLabelRect = new Rectangle(fRight - opLabelW, footerY + 1, opLabelW, 20);
            fRight -= (opLabelW + 2);
            _btnOpacityDownRect = new Rectangle(fRight - 20, footerY + 1, 20, 20);
            fRight -= 26;

            int modeW = 64;
            _btnModeRect = new Rectangle(fRight - modeW, footerY + 1, modeW, 20);

            DrawPillButton(g, _btnModeRect, _settings.ShowRemaining ? L("Remaining", "显示:剩余") : L("Used", "显示:已用"), _hoverButton == "mode", false);
            DrawPillButton(g, _btnOpacityDownRect, "-", _hoverButton == "op_down", false);
            DrawPillButton(g, _btnOpacityUpRect, "+", _hoverButton == "op_up", false);

            using (Font opFont = new Font("Segoe UI", 7.8f, FontStyle.Regular))
            using (Brush opBrush = new SolidBrush(Color.FromArgb(175, 185, 205)))
            {
                StringFormat sfCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(string.Format("{0:0}%", _settings.Opacity * 100), opFont, opBrush, opLabelRect, sfCenter);
            }

            int contentTop = headerY + btnH + 8;
            int contentBottom = footerY - 4;
            int availH = Math.Max(60, contentBottom - contentTop);

            if (_snapshot == null || !_snapshot.Ok || _snapshot.Meters == null || _snapshot.Meters.Count == 0)
            {
                using (Font msgFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular))
                using (Brush msgBrush = new SolidBrush(Color.FromArgb(180, 190, 210)))
                {
                    string err = _snapshot != null && !string.IsNullOrEmpty(_snapshot.Error) ? _snapshot.Error : L("Fetching OpenCode Go quota...", "正在获取 OpenCode Go 额度...");
                    RectangleF errRect = new RectangleF(pad, contentTop + 12, w - pad * 2, availH - 16);
                    g.DrawString(err, msgFont, msgBrush, errRect);
                }
            }
            else
            {
                int count = _snapshot.Meters.Count;
                int rowH = availH / count;

                using (Font labelFont = new Font("Microsoft YaHei UI", 8.6f * Math.Min(1.15f, scale), FontStyle.Bold))
                using (Font detailFont = new Font("Microsoft YaHei UI", 8.0f * Math.Min(1.15f, scale), FontStyle.Regular))
                using (Brush labelBrush = new SolidBrush(Color.FromArgb(230, 236, 245)))
                using (Brush detailBrush = new SolidBrush(Color.FromArgb(165, 178, 200)))
                {
                    for (int i = 0; i < count; i++)
                    {
                        WindowMeter m = _snapshot.Meters[i];
                        int rowY = contentTop + i * rowH;

                        double displayPct = _settings.ShowRemaining ? m.RemainPercent : m.UsedPercent;
                        string pctWord = _settings.ShowRemaining ? L("left", "剩余") : L("used", "已用");
                        Color barColor = GetQuotaColor(m.RemainPercent);

                        string leftText = string.Format("{0} ({1})", m.Label, m.CountdownText);
                        string rightText = _settings.ShowRemaining
                            ? string.Format("${0:0.00}/${1:0} · {2:0.0}% {3}", m.RemainUsd, m.LimitUsd, displayPct, pctWord)
                            : string.Format("${0:0.00}/${1:0} · {2:0.0}% {3}", m.UsedUsd, m.LimitUsd, displayPct, pctWord);

                        g.DrawString(leftText, labelFont, labelBrush, pad, rowY);

                        SizeF rightSize = g.MeasureString(rightText, detailFont);
                        g.DrawString(rightText, detailFont, detailBrush, w - pad - rightSize.Width, rowY + 1);

                        int barY = rowY + (int)(19 * Math.Min(1.15f, scale));
                        int barH = Math.Max(7, Math.Min(12, rowH - (int)(26 * Math.Min(1.15f, scale))));
                        int barW = w - pad * 2;

                        Rectangle trackRect = new Rectangle(pad, barY, barW, barH);
                        using (GraphicsPath trackPath = CreateRoundedRect(trackRect, barH / 2))
                        using (Brush trackBrush = new SolidBrush(Color.FromArgb(40, 46, 62)))
                        {
                            g.FillPath(trackBrush, trackPath);
                        }

                        int fillW = (int)Math.Round(barW * Math.Max(0, Math.Min(100, displayPct)) / 100.0);
                        if (fillW > 0)
                        {
                            fillW = Math.Max(barH, fillW);
                            Rectangle fillRect = new Rectangle(pad, barY, Math.Min(barW, fillW), barH);
                            using (GraphicsPath fillPath = CreateRoundedRect(fillRect, barH / 2))
                            using (LinearGradientBrush fillBrush = new LinearGradientBrush(
                                fillRect,
                                ControlPaint.Light(barColor, 0.15f),
                                barColor,
                                LinearGradientMode.Horizontal))
                            {
                                g.FillPath(fillBrush, fillPath);
                            }
                        }
                    }
                }
            }

            using (Pen gripPen = new Pen(Color.FromArgb(90, 100, 120), 1.2f))
            {
                g.DrawLine(gripPen, w - 11, h - 5, w - 5, h - 11);
                g.DrawLine(gripPen, w - 8, h - 5, w - 5, h - 8);
            }
        }

        private Color GetQuotaColor(double remainPercent)
        {
            if (remainPercent <= 15) return Color.FromArgb(248, 113, 113);
            if (remainPercent <= 35) return Color.FromArgb(251, 191, 36);
            return Color.FromArgb(52, 211, 153);
        }

        private void DrawPillButton(Graphics g, Rectangle rect, string text, bool hover, bool active)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            Color bg = hover
                ? Color.FromArgb(65, 78, 105)
                : (active ? Color.FromArgb(45, 85, 140) : Color.FromArgb(36, 42, 56));
            Color border = active ? Color.FromArgb(96, 165, 250) : Color.FromArgb(58, 68, 88);

            using (GraphicsPath path = CreateRoundedRect(rect, 6))
            using (Brush b = new SolidBrush(bg))
            using (Pen p = new Pen(border, 1f))
            using (Font f = new Font("Microsoft YaHei UI", 7.8f, FontStyle.Regular))
            using (Brush tb = new SolidBrush(Color.FromArgb(230, 236, 248)))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
                StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, f, tb, rect, sf);
            }
        }

        private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            if (d <= 0 || bounds.Width < d || bounds.Height < d)
            {
                path.AddRectangle(bounds);
                return path;
            }
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath, Encoding.UTF8);
                    _settings = new WidgetSettings
                    {
                        X = (int)ExtractDouble(json, "X", -1),
                        Y = (int)ExtractDouble(json, "Y", -1),
                        Width = (int)ExtractDouble(json, "Width", 320),
                        Height = (int)ExtractDouble(json, "Height", 220),
                        Opacity = ExtractDouble(json, "Opacity", 0.92),
                        TopMost = ExtractBool(json, "TopMost", true),
                        ShowRemaining = ExtractBool(json, "ShowRemaining", true),
                        MiniMode = ExtractBool(json, "MiniMode", false),
                        SavedFullHeight = (int)ExtractDouble(json, "SavedFullHeight", 220),
                        Language = ExtractString(json, "Language", "auto")
                    };
                    return;
                }
            }
            catch { }
            _settings = new WidgetSettings();
        }

        private void SaveSettings()
        {
            try
            {
                string json = string.Format(
                    CultureInfo.InvariantCulture,
                    "{{\"X\":{0},\"Y\":{1},\"Width\":{2},\"Height\":{3},\"Opacity\":{4:0.00},\"TopMost\":{5},\"ShowRemaining\":{6},\"MiniMode\":{7},\"SavedFullHeight\":{8},\"Language\":\"{9}\"}}",
                    _settings.X,
                    _settings.Y,
                    _settings.Width,
                    _settings.Height,
                    _settings.Opacity,
                    _settings.TopMost ? "true" : "false",
                    _settings.ShowRemaining ? "true" : "false",
                    _settings.MiniMode ? "true" : "false",
                    _settings.SavedFullHeight,
                    _settings.Language
                );
                File.WriteAllText(_settingsPath, json, Encoding.UTF8);
            }
            catch { }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, @"Local\OpenCodeQuotaWidget_SingleInstance", out createdNew))
            {
                if (!createdNew)
                    return;

                try { SetProcessDPIAware(); } catch { }
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                bool standalone = false;
                if (args != null)
                {
                    foreach (string a in args)
                    {
                        if (string.Equals(a, "--standalone", StringComparison.OrdinalIgnoreCase))
                            standalone = true;
                    }
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new QuotaWidgetForm(standalone));
            }
        }
    }
}
