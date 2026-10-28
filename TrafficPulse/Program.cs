using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrafficPulse
{
    internal static class Program
    {
        public static string ApplicationDirectory => AppContext.BaseDirectory;

        [STAThread]
        static void Main()
        {
            EnsureIconExists();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApplicationContext());
        }

        private static void EnsureIconExists()
        {
            try
            {
                string iconPath = Path.Combine(ApplicationDirectory, "app.ico");
                if (!File.Exists(iconPath))
                {
                    string base64Icon = "AAABAAEAICAAAAAAIAAYAQAAFgAAAIlQTkcNChoKAAAADUlIRFIAAAAgAAAAIAgGAAAAc3p69AAAAN9JREFUeJxjZKi4/p9hAAHTQFo+6gAGBgYGFnSB/+0aNLeUsfIGnD34QgAGkF1JLYAtdAc8BEYdQJIDvjerM+xPlUMR+1CvRj8H/Pzzn4GFiZHBQYmLIkvJdgADAwND/Z7XDI0uIgPngH13vzEwMDAwOFIpFMhKhPV73jA0ulInFMhywIF73xj+/mNgcFKmPBRwloSEQP2eNwytbqIUO4DscuDQ/W8Mv/7+Z2BnYaTIASSFgEDjLRS+85xHFFnOwDDUSsJRB4w6YNQBtAA4CyJ6NM8ZGAZBCDCOdk4H2gEAhHcuOo4cfdcAAAAASUVORK5CYII=";
                    byte[] bytes = Convert.FromBase64String(base64Icon);
                    File.WriteAllBytes(iconPath, bytes);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Icon creation error: {ex}");
            }
        }
    }

    class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon trayIcon;
        private readonly Thread monitorThread;
        private readonly CancellationTokenSource monitorCancellation = new CancellationTokenSource();
        private readonly System.Windows.Forms.Timer tooltipTimer;
        private volatile string latestTooltip = "TrafficPulse Starting...";
        private StatsForm? statsForm;
        private readonly ToolStripMenuItem darkModeMenuItem;
        private readonly ToolStripMenuItem startupMenuItem;

        public TrayApplicationContext()
        {
            Icon appIcon = SystemIcons.Application;
            try
            {
                string iconPath = Path.Combine(Program.ApplicationDirectory, "app.ico");
                if (File.Exists(iconPath))
                {
                    appIcon = new Icon(iconPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Icon loading error: {ex}");
            }

            trayIcon = new NotifyIcon()
            {
                Icon = appIcon,
                Visible = true,
                Text = "TrafficPulse Starting..."
            };

            trayIcon.DoubleClick += (s, e) => ShowStatsForm();

            ContextMenuStrip contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Statistics", null, (s, e) => ShowStatsForm());

            bool isDark = TrafficManager.LoadSettings().IsDarkMode;
            darkModeMenuItem = new ToolStripMenuItem($"Dark Mode: {(isDark ? "On" : "Off")}", null, ToggleDarkMode);
            contextMenu.Items.Add(darkModeMenuItem);

            bool isStartup = TrafficManager.CheckStartupRegistry();
            startupMenuItem = new ToolStripMenuItem($"Start with Windows: {(isStartup ? "On" : "Off")}", null, ToggleStartup);
            contextMenu.Items.Add(startupMenuItem);

            contextMenu.Items.Add("Exit", null, Exit);
            trayIcon.ContextMenuStrip = contextMenu;

            tooltipTimer = new System.Windows.Forms.Timer { Interval = 500 };
            tooltipTimer.Tick += (s, e) =>
            {
                if (trayIcon.Visible && trayIcon.Text != latestTooltip)
                {
                    trayIcon.Text = latestTooltip;
                }
            };
            tooltipTimer.Start();

            monitorThread = new Thread(MonitorNetwork)
            {
                IsBackground = true
            };
            monitorThread.Start();
        }

        private void ToggleDarkMode(object? sender, EventArgs e)
        {
            var settings = TrafficManager.LoadSettings();
            settings.IsDarkMode = !settings.IsDarkMode;
            TrafficManager.SaveSettings(settings);

            darkModeMenuItem.Text = $"Dark Mode: {(settings.IsDarkMode ? "On" : "Off")}";

            if (statsForm != null && !statsForm.IsDisposed)
            {
                statsForm.ApplyTheme();
            }
        }

        private void ToggleStartup(object? sender, EventArgs e)
        {
            bool newState = TrafficManager.ToggleStartupRegistry();
            startupMenuItem.Text = $"Start with Windows: {(newState ? "On" : "Off")}";
        }

        private void ShowStatsForm()
        {
            if (statsForm == null || statsForm.IsDisposed)
            {
                statsForm = new StatsForm();
                statsForm.ThemeChanged += (s, e) =>
                {
                    bool isDark = TrafficManager.LoadSettings().IsDarkMode;
                    darkModeMenuItem.Text = $"Dark Mode: {(isDark ? "On" : "Off")}";
                };
            }
            statsForm.Show();
            statsForm.BringToFront();
        }

        private void MonitorNetwork()
        {
            Dictionary<string, (long Received, long Sent)>? previousTraffic = null;
            var sampleTimer = Stopwatch.StartNew();

            while (!monitorCancellation.IsCancellationRequested)
            {
                if (monitorCancellation.Token.WaitHandle.WaitOne(1000))
                    break;

                Dictionary<string, (long Received, long Sent)> currentTraffic;
                try
                {
                    currentTraffic = GetTrafficByInterface();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Network statistics error: {ex}");
                    continue;
                }

                if (previousTraffic == null)
                {
                    previousTraffic = currentTraffic;
                    sampleTimer.Restart();
                    continue;
                }

                double elapsedSeconds = Math.Max(sampleTimer.Elapsed.TotalSeconds, 0.001);
                sampleTimer.Restart();

                long downloadDelta = 0;
                long uploadDelta = 0;
                foreach (var (interfaceId, current) in currentTraffic)
                {
                    if (!previousTraffic.TryGetValue(interfaceId, out var previous))
                    {
                        // A new adapter already has cumulative counters. Establish a
                        // baseline rather than recording its history as current traffic.
                        continue;
                    }

                    long receivedDelta = current.Received - previous.Received;
                    long sentDelta = current.Sent - previous.Sent;
                    if (receivedDelta >= 0) downloadDelta += receivedDelta;
                    if (sentDelta >= 0) uploadDelta += sentDelta;
                }

                if (downloadDelta > 0 || uploadDelta > 0)
                {
                    TrafficManager.AddTraffic(downloadDelta, uploadDelta);
                }

                string FormatSpeed(long bytesPerSec)
                {
                    double megaBytes = bytesPerSec / 1048576.0;
                    if (megaBytes >= 1.0)
                        return $"{megaBytes:F1} MB/s";

                    return $"{bytesPerSec / 1024.0:F1} KB/s";
                }

                long downloadBytesPerSecond = (long)(downloadDelta / elapsedSeconds);
                long uploadBytesPerSecond = (long)(uploadDelta / elapsedSeconds);
                string tooltip = $"DL: {FormatSpeed(downloadBytesPerSecond)} | UL: {FormatSpeed(uploadBytesPerSecond)}";
                latestTooltip = tooltip.Length > 63 ? tooltip[..63] : tooltip;

                previousTraffic = currentTraffic;
            }
        }

        private static Dictionary<string, (long Received, long Sent)> GetTrafficByInterface()
        {
            var traffic = new Dictionary<string, (long Received, long Sent)>();
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (NetworkInterface ni in interfaces)
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (IsExcludedInterface(ni)) continue;

                IPInterfaceProperties props = ni.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;

                IPv4InterfaceStatistics stats = ni.GetIPv4Statistics();
                traffic[ni.Id] = (stats.BytesReceived, stats.BytesSent);
            }

            return traffic;
        }

        private static bool IsExcludedInterface(NetworkInterface networkInterface)
        {
            if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                return true;

            string adapterText = $"{networkInterface.Name} {networkInterface.Description}".ToLowerInvariant();
            string[] virtualAdapterMarkers =
            {
                "virtual", "vmware", "virtualbox", "hyper-v", "vethernet",
                "wsl", "vpn", "wireguard", "tap-windows", "tailscale", "zerotier"
            };

            return virtualAdapterMarkers.Any(adapterText.Contains);
        }

        private void Exit(object? sender, EventArgs e)
        {
            tooltipTimer.Stop();
            monitorCancellation.Cancel();
            if (monitorThread.IsAlive)
            {
                monitorThread.Join(2000);
            }
            trayIcon.Visible = false;
            if (statsForm != null && !statsForm.IsDisposed)
            {
                statsForm.Close();
            }
            tooltipTimer.Dispose();
            trayIcon.Dispose();
            Application.Exit();
        }
    }

    class SurfacePanel : Panel
    {
        internal Color BorderColor = Color.FromArgb(49, 65, 82);
        internal int CornerRadius = 10;

        public SurfacePanel()
        {
            DoubleBuffered = true;
            Padding = new Padding(1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 3 || Height < 3) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using GraphicsPath path = CreateRoundedRectangle(bounds, CornerRadius);
            using Pen pen = new Pen(BorderColor);
            e.Graphics.DrawPath(pen, path);
        }

        internal static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
        {
            int diameter = Math.Max(2, radius * 2);
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    class PulseLogoControl : Control
    {
        internal Color AccentColor = Color.FromArgb(24, 149, 247);

        public PulseLogoControl()
        {
            DoubleBuffered = true;
            Size = new Size(58, 58);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(3, 3, Width - 7, Height - 7);
            using GraphicsPath path = SurfacePanel.CreateRoundedRectangle(bounds, 10);
            using var background = new SolidBrush(Color.FromArgb(30, AccentColor));
            using var border = new Pen(AccentColor, 4);
            e.Graphics.FillPath(background, path);
            e.Graphics.DrawPath(border, path);

            Point[] pulse =
            {
                new(12, Height / 2), new(20, Height / 2), new(25, 18),
                new(32, 41), new(38, 25), new(47, 25)
            };
            using var pulsePen = new Pen(AccentColor, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            e.Graphics.DrawLines(pulsePen, pulse);
        }
    }

    class SummaryCard : SurfacePanel
    {
        private readonly Label iconLabel;
        private readonly Label titleLabel;
        private readonly Label valueLabel;
        private readonly Label trendLabel;

        public SummaryCard(string icon, string title)
        {
            iconLabel = new Label
            {
                Text = icon,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Symbol", 22, FontStyle.Bold),
                Location = new Point(20, 24),
                Size = new Size(58, 58)
            };
            titleLabel = new Label
            {
                Text = title,
                AutoSize = true,
                Font = new Font("Segoe UI", 10.5f),
                Location = new Point(94, 19)
            };
            valueLabel = new Label
            {
                Text = "0.00 GB",
                AutoSize = true,
                Font = new Font("Segoe UI", 22, FontStyle.Bold),
                Location = new Point(90, 43)
            };
            trendLabel = new Label
            {
                Text = "No previous data",
                AutoSize = true,
                Font = new Font("Segoe UI", 9),
                Location = new Point(94, 92)
            };
            Controls.AddRange(new Control[] { iconLabel, titleLabel, valueLabel, trendLabel });
        }

        public void SetValue(double gigabytes, double? comparisonPercent)
        {
            valueLabel.Text = $"{gigabytes:N2} GB";
            if (comparisonPercent.HasValue)
            {
                string arrow = comparisonPercent.Value >= 0 ? "▲" : "▼";
                trendLabel.Text = $"{arrow} {Math.Abs(comparisonPercent.Value):N0}%  vs previous period";
                trendLabel.ForeColor = comparisonPercent.Value >= 0
                    ? Color.FromArgb(65, 214, 132)
                    : Color.FromArgb(246, 106, 106);
            }
            else
            {
                trendLabel.Text = "No previous data";
            }
        }

        public void ApplyTheme(bool isDark, Color surface, Color border, Color text, Color muted, Color accent)
        {
            BackColor = surface;
            BorderColor = border;
            iconLabel.BackColor = isDark ? Color.FromArgb(24, 62, 93) : Color.FromArgb(220, 239, 255);
            iconLabel.ForeColor = accent;
            titleLabel.ForeColor = muted;
            valueLabel.ForeColor = text;
            if (trendLabel.Text == "No previous data") trendLabel.ForeColor = muted;
            Invalidate();
        }
    }

    class UsageChartControl : Control
    {
        private readonly List<(string Label, double Down, double Up)> points = new();
        internal Color GridColor = Color.FromArgb(42, 58, 74);
        internal Color TextColor = Color.FromArgb(174, 190, 207);
        internal Color DownloadColor = Color.FromArgb(35, 156, 247);
        internal Color UploadColor = Color.FromArgb(126, 187, 242);

        public UsageChartControl()
        {
            DoubleBuffered = true;
            Font = new Font("Segoe UI", 9);
        }

        public void SetData(IEnumerable<(string Label, double Down, double Up)> data)
        {
            points.Clear();
            points.AddRange(data);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle chart = new Rectangle(58, 24, Math.Max(10, Width - 82), Math.Max(10, Height - 62));
            double maximum = points.Count == 0 ? 1 : Math.Max(1, points.Max(p => Math.Max(p.Down, p.Up)));
            maximum = Math.Ceiling(maximum / 10.0) * 10.0;

            using var gridPen = new Pen(GridColor);
            using var textBrush = new SolidBrush(TextColor);
            using var downloadPen = new Pen(DownloadColor, 2.5f);
            using var uploadPen = new Pen(UploadColor, 2.5f);
            DrawLegend(e.Graphics, chart.Right - 190, 2, DownloadColor, "Download", textBrush);
            DrawLegend(e.Graphics, chart.Right - 92, 2, UploadColor, "Upload", textBrush);
            for (int i = 0; i <= 4; i++)
            {
                int y = chart.Bottom - (chart.Height * i / 4);
                e.Graphics.DrawLine(gridPen, chart.Left, y, chart.Right, y);
                string label = $"{maximum * i / 4:N0} GB";
                e.Graphics.DrawString(label, Font, textBrush, 4, y - 8);
            }

            if (points.Count == 0)
            {
                e.Graphics.DrawString("No usage data yet", Font, textBrush, chart.Left + 12, chart.Top + 20);
                return;
            }

            PointF[] downloadPoints = BuildPoints(chart, maximum, p => p.Down);
            PointF[] uploadPoints = BuildPoints(chart, maximum, p => p.Up);
            if (downloadPoints.Length > 1)
            {
                using var areaPath = new GraphicsPath();
                areaPath.AddLines(downloadPoints);
                areaPath.AddLine(downloadPoints[^1].X, downloadPoints[^1].Y, downloadPoints[^1].X, chart.Bottom);
                areaPath.AddLine(downloadPoints[^1].X, chart.Bottom, downloadPoints[0].X, chart.Bottom);
                areaPath.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb(48, DownloadColor));
                e.Graphics.FillPath(fill, areaPath);
            }
            if (downloadPoints.Length > 1) e.Graphics.DrawLines(downloadPen, downloadPoints);
            if (uploadPoints.Length > 1) e.Graphics.DrawLines(uploadPen, uploadPoints);

            for (int i = 0; i < points.Count; i++)
            {
                DrawPoint(e.Graphics, downloadPoints[i], DownloadColor);
                DrawPoint(e.Graphics, uploadPoints[i], UploadColor);
                SizeF size = e.Graphics.MeasureString(points[i].Label, Font);
                e.Graphics.DrawString(points[i].Label, Font, textBrush, downloadPoints[i].X - size.Width / 2, chart.Bottom + 8);
            }
        }

        private PointF[] BuildPoints(Rectangle chart, double maximum, Func<(string Label, double Down, double Up), double> selector)
        {
            var result = new PointF[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                float x = points.Count == 1 ? chart.Left + chart.Width / 2f : chart.Left + (chart.Width * i / (float)(points.Count - 1));
                float y = chart.Bottom - (float)(selector(points[i]) / maximum * chart.Height);
                result[i] = new PointF(x, y);
            }
            return result;
        }

        private static void DrawPoint(Graphics graphics, PointF point, Color color)
        {
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, point.X - 4, point.Y - 4, 8, 8);
        }

        private void DrawLegend(Graphics graphics, int x, int y, Color color, string label, Brush textBrush)
        {
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, x, y + 5, 9, 9);
            graphics.DrawString(label, Font, textBrush, x + 14, y);
        }
    }

    class BufferedDataGridView : DataGridView
    {
        public BufferedDataGridView()
        {
            DoubleBuffered = true;
        }
    }

    class StatsForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);

        private readonly Panel sidebar;
        private readonly Panel contentHost;
        private readonly Label pageTitle;
        private readonly Label pageSubtitle;
        private readonly Button dateRangeButton;
        private readonly Dictionary<string, Button> navButtons = new();
        private readonly SummaryCard downloadCard;
        private readonly SummaryCard uploadCard;
        private readonly SummaryCard totalCard;
        private readonly SurfacePanel chartSurface;
        private readonly SurfacePanel tableSurface;
        private readonly Label tableTitle;
        private readonly UsageChartControl usageChart;
        private readonly BufferedDataGridView statsGrid;
        private readonly System.Windows.Forms.Timer updateTimer;
        private string currentView = "Daily";

        public event EventHandler? ThemeChanged;

        public StatsForm()
        {
            Text = "TrafficPulse — Network Usage Statistics";
            Size = new Size(1180, 820);
            MinimumSize = new Size(960, 680);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);

            try
            {
                string iconPath = Path.Combine(Program.ApplicationDirectory, "app.ico");
                if (File.Exists(iconPath)) Icon = new Icon(iconPath);
            }
            catch (Exception ex)
            {
                TrafficManager.LogError("Icon loading error", ex);
            }

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            sidebar = BuildSidebar();
            contentHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(26, 22, 26, 24) };
            root.Controls.Add(sidebar, 0, 0);
            root.Controls.Add(contentHost, 1, 0);
            Controls.Add(root);

            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 138));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            contentHost.Controls.Add(content);

            Panel header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            pageTitle = new Label
            {
                Text = "Daily Usage",
                AutoSize = true,
                Location = new Point(2, 0),
                Font = new Font("Segoe UI", 25, FontStyle.Bold)
            };
            pageSubtitle = new Label
            {
                Text = "Network activity and bandwidth consumption",
                AutoSize = true,
                Location = new Point(4, 53),
                Font = new Font("Segoe UI", 11)
            };
            dateRangeButton = new Button
            {
                Text = "Recent activity",
                Size = new Size(250, 46),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Default
            };
            dateRangeButton.FlatAppearance.BorderSize = 1;
            header.Controls.AddRange(new Control[] { pageTitle, pageSubtitle, dateRangeButton });
            header.Resize += (s, e) => dateRangeButton.Location = new Point(header.ClientSize.Width - dateRangeButton.Width, 10);
            content.Controls.Add(header, 0, 0);

            var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 0, 0, 12) };
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
            downloadCard = new SummaryCard("↓", "Download") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
            uploadCard = new SummaryCard("↑", "Upload") { Dock = DockStyle.Fill, Margin = new Padding(4, 0, 4, 0) };
            totalCard = new SummaryCard("∑", "Total") { Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 0) };
            cards.Controls.Add(downloadCard, 0, 0);
            cards.Controls.Add(uploadCard, 1, 0);
            cards.Controls.Add(totalCard, 2, 0);
            content.Controls.Add(cards, 0, 1);

            chartSurface = new SurfacePanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(18, 12, 18, 12) };
            Label chartTitle = new Label { Text = "Usage Overview", Dock = DockStyle.Top, Height = 32, Font = new Font("Segoe UI", 12, FontStyle.Bold) };
            usageChart = new UsageChartControl { Dock = DockStyle.Fill };
            chartSurface.Controls.Add(usageChart);
            chartSurface.Controls.Add(chartTitle);
            content.Controls.Add(chartSurface, 0, 2);

            tableSurface = new SurfacePanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(1) };
            tableTitle = new Label { Text = "Daily Details", Dock = DockStyle.Top, Height = 46, Padding = new Padding(18, 13, 0, 0), Font = new Font("Segoe UI", 12, FontStyle.Bold) };
            statsGrid = BuildGrid();
            tableSurface.Controls.Add(statsGrid);
            tableSurface.Controls.Add(tableTitle);
            content.Controls.Add(tableSurface, 0, 3);

            ApplyTheme();
            LoadData();

            updateTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            updateTimer.Tick += (s, e) => LoadData();
            updateTimer.Start();
            FormClosed += (s, e) => updateTimer.Dispose();
        }

        private Panel BuildSidebar()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var logo = new PulseLogoControl { Location = new Point(81, 28) };
            var brand = new Label
            {
                Text = "TrafficPulse",
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 16, FontStyle.Bold),
                Location = new Point(10, 92),
                Size = new Size(200, 32)
            };
            var tagline = new Label
            {
                Text = "Network Usage Statistics",
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9.5f),
                Location = new Point(10, 126),
                Size = new Size(200, 26)
            };
            panel.Controls.AddRange(new Control[] { logo, brand, tagline });

            string[] views = { "Dashboard", "Daily", "Weekly", "Monthly", "Yearly" };
            string[] icons = { "⌂", "▣", "▥", "▥", "□" };
            for (int i = 0; i < views.Length; i++)
            {
                string view = views[i];
                var button = new Button
                {
                    Text = $"{icons[i]}     {view}",
                    Tag = view,
                    FlatStyle = FlatStyle.Flat,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font("Segoe UI", 11),
                    Location = new Point(12, 178 + i * 56),
                    Size = new Size(196, 48),
                    Padding = new Padding(16, 0, 0, 0),
                    Cursor = Cursors.Hand
                };
                button.FlatAppearance.BorderSize = 0;
                button.Click += (s, e) => SwitchView((string)((Button)s!).Tag!);
                navButtons[view] = button;
                panel.Controls.Add(button);
            }

            var settings = new Button
            {
                Text = "⚙     Toggle theme",
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 10.5f),
                Size = new Size(196, 48),
                Left = 12,
                Padding = new Padding(16, 0, 0, 0),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
                Cursor = Cursors.Hand
            };
            settings.Top = 700;
            settings.FlatAppearance.BorderSize = 0;
            settings.Click += (s, e) =>
            {
                AppSettings appSettings = TrafficManager.LoadSettings();
                appSettings.IsDarkMode = !appSettings.IsDarkMode;
                TrafficManager.SaveSettings(appSettings);
                ApplyTheme();
                ThemeChanged?.Invoke(this, EventArgs.Empty);
            };
            panel.Controls.Add(settings);
            panel.Resize += (s, e) => settings.Top = panel.ClientSize.Height - settings.Height - 18;
            return panel;
        }

        private static BufferedDataGridView BuildGrid()
        {
            var grid = new BufferedDataGridView
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                ColumnHeadersHeight = 40,
                RowTemplate = { Height = 38 },
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                ScrollBars = ScrollBars.Vertical,
                EnableHeadersVisualStyles = false
            };
            grid.Columns.Add("Period", "Date");
            grid.Columns.Add("Download", "Download");
            grid.Columns.Add("Upload", "Upload");
            grid.Columns.Add("Total", "Total");
            foreach (DataGridViewColumn column in grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            return grid;
        }

        private void SwitchView(string viewName)
        {
            currentView = viewName;
            UpdateNavButtonStyles();
            LoadData();
        }

        private void UpdateNavButtonStyles()
        {
            bool isDark = TrafficManager.LoadSettings().IsDarkMode;
            Color inactive = isDark ? Color.FromArgb(18, 29, 41) : Color.FromArgb(239, 244, 249);
            Color inactiveText = isDark ? Color.FromArgb(218, 228, 238) : Color.FromArgb(43, 56, 70);
            foreach (var (view, button) in navButtons)
            {
                bool active = view == currentView;
                button.BackColor = active ? Color.FromArgb(15, 132, 226) : inactive;
                button.ForeColor = active ? Color.White : inactiveText;
                button.Font = new Font("Segoe UI", 11, active ? FontStyle.Bold : FontStyle.Regular);
                button.FlatAppearance.MouseOverBackColor = active ? Color.FromArgb(15, 132, 226) : Color.FromArgb(35, 54, 72);
            }
        }

        public void ApplyTheme()
        {
            bool isDark = TrafficManager.LoadSettings().IsDarkMode;
            Color form = isDark ? Color.FromArgb(10, 18, 27) : Color.FromArgb(244, 247, 250);
            Color nav = isDark ? Color.FromArgb(18, 29, 41) : Color.FromArgb(232, 238, 244);
            Color surface = isDark ? Color.FromArgb(18, 29, 41) : Color.White;
            Color border = isDark ? Color.FromArgb(48, 65, 82) : Color.FromArgb(211, 220, 229);
            Color text = isDark ? Color.FromArgb(242, 247, 252) : Color.FromArgb(24, 34, 44);
            Color muted = isDark ? Color.FromArgb(166, 183, 200) : Color.FromArgb(91, 108, 124);
            Color accent = Color.FromArgb(35, 156, 247);

            BackColor = form;
            contentHost.BackColor = form;
            sidebar.BackColor = nav;
            foreach (Control control in sidebar.Controls)
            {
                if (control is Label label) label.ForeColor = label.Text == "TrafficPulse" ? text : muted;
                if (control is PulseLogoControl logo) { logo.AccentColor = accent; logo.Invalidate(); }
                if (control is Button button && !navButtons.Values.Contains(button))
                {
                    button.BackColor = nav;
                    button.ForeColor = muted;
                    button.FlatAppearance.MouseOverBackColor = isDark ? Color.FromArgb(35, 54, 72) : Color.FromArgb(216, 227, 237);
                }
            }
            pageTitle.ForeColor = text;
            pageSubtitle.ForeColor = muted;
            dateRangeButton.BackColor = surface;
            dateRangeButton.ForeColor = text;
            dateRangeButton.FlatAppearance.BorderColor = border;
            downloadCard.ApplyTheme(isDark, surface, border, text, muted, accent);
            uploadCard.ApplyTheme(isDark, surface, border, text, muted, accent);
            totalCard.ApplyTheme(isDark, surface, border, text, muted, accent);
            ApplySurfaceTheme(chartSurface, surface, border, text);
            ApplySurfaceTheme(tableSurface, surface, border, text);

            usageChart.BackColor = surface;
            usageChart.GridColor = isDark ? Color.FromArgb(42, 58, 74) : Color.FromArgb(220, 228, 236);
            usageChart.TextColor = muted;
            usageChart.Invalidate();

            statsGrid.BackgroundColor = surface;
            statsGrid.GridColor = border;
            statsGrid.DefaultCellStyle.BackColor = surface;
            statsGrid.DefaultCellStyle.ForeColor = text;
            statsGrid.DefaultCellStyle.SelectionBackColor = isDark ? Color.FromArgb(29, 58, 83) : Color.FromArgb(220, 239, 255);
            statsGrid.DefaultCellStyle.SelectionForeColor = text;
            statsGrid.AlternatingRowsDefaultCellStyle.BackColor = isDark ? Color.FromArgb(21, 34, 47) : Color.FromArgb(249, 251, 253);
            statsGrid.ColumnHeadersDefaultCellStyle.BackColor = isDark ? Color.FromArgb(25, 40, 55) : Color.FromArgb(236, 242, 247);
            statsGrid.ColumnHeadersDefaultCellStyle.ForeColor = text;
            UpdateNavButtonStyles();
            ApplyTitleBarTheme(isDark);
        }

        private static void ApplySurfaceTheme(SurfacePanel panel, Color surface, Color border, Color text)
        {
            panel.BackColor = surface;
            panel.BorderColor = border;
            foreach (Control child in panel.Controls)
            {
                if (child is Label label) label.ForeColor = text;
            }
            panel.Invalidate();
        }

        private void LoadData()
        {
            Dictionary<string, (long Down, long Up)> data = currentView switch
            {
                "Dashboard" or "Daily" => TrafficManager.GetDailyTraffic(),
                "Weekly" => TrafficManager.GetWeeklyTraffic(),
                "Monthly" => TrafficManager.GetMonthlyTraffic(),
                _ => TrafficManager.GetYearlyTraffic()
            };

            string dataView = currentView == "Dashboard" ? "Daily" : currentView;
            List<KeyValuePair<string, (long Down, long Up)>> rows = data
                .OrderByDescending(item => GetSortDate(item.Key, dataView))
                .ThenByDescending(item => item.Key)
                .ToList();

            pageTitle.Text = currentView == "Dashboard" ? "Dashboard" : $"{currentView} Usage";
            pageSubtitle.Text = currentView == "Dashboard"
                ? "A quick overview of your network activity"
                : "Network activity and bandwidth consumption";
            tableTitle.Text = currentView == "Dashboard" ? "Recent Details" : $"{currentView} Details";

            var current = rows.Count > 0 ? rows[0].Value : (Down: 0L, Up: 0L);
            var previous = rows.Count > 1 ? rows[1].Value : ((long Down, long Up)?)null;
            double currentDown = ToGigabytes(current.Down);
            double currentUp = ToGigabytes(current.Up);
            downloadCard.SetValue(currentDown, previous.HasValue ? PercentChange(current.Down, previous.Value.Down) : null);
            uploadCard.SetValue(currentUp, previous.HasValue ? PercentChange(current.Up, previous.Value.Up) : null);
            totalCard.SetValue(currentDown + currentUp, previous.HasValue ? PercentChange(current.Down + current.Up, previous.Value.Down + previous.Value.Up) : null);

            if (rows.Count == 0)
                dateRangeButton.Text = "No activity yet";
            else if (rows.Count == 1)
                dateRangeButton.Text = rows[0].Key;
            else
                dateRangeButton.Text = $"{rows[Math.Min(6, rows.Count - 1)].Key}  —  {rows[0].Key}";

            var chartData = rows.Take(7).Reverse().Select(item =>
                (ShortLabel(item.Key, dataView), ToGigabytes(item.Value.Down), ToGigabytes(item.Value.Up)));
            usageChart.SetData(chartData);

            statsGrid.SuspendLayout();
            statsGrid.Rows.Clear();
            foreach (var item in rows)
            {
                double down = ToGigabytes(item.Value.Down);
                double up = ToGigabytes(item.Value.Up);
                statsGrid.Rows.Add(item.Key, $"{down:N2} GB", $"{up:N2} GB", $"{down + up:N2} GB");
            }
            statsGrid.ClearSelection();
            statsGrid.ResumeLayout();
        }

        private static double ToGigabytes(long bytes) => bytes / 1024.0 / 1024.0 / 1024.0;

        private static double? PercentChange(long current, long previous)
        {
            if (previous == 0) return null;
            return (current - previous) * 100.0 / previous;
        }

        private static string ShortLabel(string key, string view)
        {
            if (view == "Daily" && DateTime.TryParseExact(key, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                return date.ToString("dd MMM", CultureInfo.InvariantCulture);
            if (view == "Weekly") return key.Replace(" - Week ", " W");
            return key.Length > 10 ? key[..10] : key;
        }

        private static DateTime GetSortDate(string key, string view)
        {
            if (view == "Daily" && DateTime.TryParseExact(key, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime daily))
                return daily;
            if (view == "Weekly")
            {
                string[] parts = key.Split(" - Week ", StringSplitOptions.None);
                if (parts.Length == 2 && int.TryParse(parts[0], out int year) && int.TryParse(parts[1], out int week))
                {
                    try { return ISOWeek.ToDateTime(year, week, DayOfWeek.Monday); }
                    catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
                }
            }
            if (view == "Monthly" && DateTime.TryParseExact(key, "MMMM yyyy", new CultureInfo("en-US"), DateTimeStyles.None, out DateTime monthly))
                return monthly;
            if (view == "Yearly" && int.TryParse(key, out int yearly))
                return new DateTime(yearly, 1, 1);
            return DateTime.MinValue;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTitleBarTheme(TrafficManager.LoadSettings().IsDarkMode);
        }

        private void ApplyTitleBarTheme(bool isDark)
        {
            if (!IsHandleCreated || !OperatingSystem.IsWindows()) return;
            try
            {
                int enabled = isDark ? 1 : 0;
                const int immersiveDarkMode = 20;
                DwmSetWindowAttribute(Handle, immersiveDarkMode, ref enabled, sizeof(int));
            }
            catch (Exception ex)
            {
                TrafficManager.LogError("Title bar theme could not be applied", ex);
            }
        }
    }

    class TrafficManager
    {
        private static readonly object lockObj = new object();
        private static readonly string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrafficPulse");
        private static readonly string dataFilePath = Path.Combine(dataDirectory, "TrafficPulseData.json");
        private static readonly string settingsFilePath = Path.Combine(dataDirectory, "TrafficPulseSettings.json");
        private const string AppName = "TrafficPulse";

        public static bool CheckStartupRegistry()
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    string? value = key?.GetValue(AppName) as string;
                    return IsCurrentExecutable(value);
                }
            }
            catch (Exception ex)
            {
                LogError("Startup setting could not be read", ex);
                return false;
            }
        }

        public static bool ToggleStartupRegistry()
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return false;

                    string? value = key.GetValue(AppName) as string;
                    if (IsCurrentExecutable(value))
                    {
                        key.DeleteValue(AppName, false);
                        return false;
                    }
                    else
                    {
                        string exePath = Application.ExecutablePath;
                        key.SetValue(AppName, $"\"{exePath}\"");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("Startup setting could not be changed", ex);
                return false;
            }
        }

        private static bool IsCurrentExecutable(string? registryValue)
        {
            if (string.IsNullOrWhiteSpace(registryValue)) return false;

            string registeredPath = registryValue.Trim().Trim('"');
            return string.Equals(
                Path.GetFullPath(registeredPath),
                Path.GetFullPath(Application.ExecutablePath),
                StringComparison.OrdinalIgnoreCase);
        }

        public static void AddTraffic(long downloadDelta, long uploadDelta)
        {
            lock (lockObj)
            {
                var records = LoadRecords();
                string currentDate = DateTime.Now.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

                var record = records.FirstOrDefault(r => r.Date == currentDate);
                if (record == null)
                {
                    record = new TrafficRecord { Date = currentDate, DownloadBytes = 0, UploadBytes = 0 };
                    records.Add(record);
                }

                record.DownloadBytes += downloadDelta;
                record.UploadBytes += uploadDelta;

                SaveRecords(records);
            }
        }

        public static AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                LogError("Settings could not be loaded", ex);
            }
            return new AppSettings();
        }

        public static void SaveSettings(AppSettings settings)
        {
            try
            {
                Directory.CreateDirectory(dataDirectory);
                string json = JsonSerializer.Serialize(settings);
                string temporaryPath = settingsFilePath + ".tmp";
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, settingsFilePath, true);
            }
            catch (Exception ex)
            {
                LogError("Settings could not be saved", ex);
            }
        }

        private static bool TryParseDate(string dateStr, out DateTime dt)
        {
            dt = default;
            if (string.IsNullOrEmpty(dateStr)) return false;

            if (DateTime.TryParseExact(dateStr, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                return true;

            if (DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                return true;

            return DateTime.TryParse(dateStr, out dt);
        }

        public static Dictionary<string, (long Down, long Up)> GetDailyTraffic()
        {
            var records = LoadRecords();
            var result = new Dictionary<string, (long Down, long Up)>();

            foreach (var r in records)
            {
                if (!string.IsNullOrEmpty(r.Date) && r.Date.Length > 5)
                {
                    if (TryParseDate(r.Date, out DateTime dt))
                    {
                        string displayDate = dt.ToString("dd-MM-yyyy");
                        if (!result.ContainsKey(displayDate)) result[displayDate] = (0, 0);
                        result[displayDate] = (result[displayDate].Down + r.DownloadBytes, result[displayDate].Up + r.UploadBytes);
                    }
                }
            }
            return result;
        }

        public static Dictionary<string, (long Down, long Up)> GetWeeklyTraffic()
        {
            var records = LoadRecords();
            var result = new Dictionary<string, (long Down, long Up)>();
            foreach (var r in records)
            {
                if (TryParseDate(r.Date, out DateTime dt))
                {
                    int isoYear = ISOWeek.GetYear(dt);
                    int weekOfYear = ISOWeek.GetWeekOfYear(dt);
                    string key = $"{isoYear} - Week {weekOfYear:00}";

                    if (!result.ContainsKey(key))
                        result[key] = (0, 0);

                    result[key] = (result[key].Down + r.DownloadBytes, result[key].Up + r.UploadBytes);
                }
            }
            return result;
        }

        public static Dictionary<string, (long Down, long Up)> GetMonthlyTraffic()
        {
            var records = LoadRecords();
            var result = new Dictionary<string, (long Down, long Up)>();
            CultureInfo enCulture = new CultureInfo("en-US");

            foreach (var r in records)
            {
                if (TryParseDate(r.Date, out DateTime dt))
                {
                    string key = dt.ToString("MMMM yyyy", enCulture);

                    if (!result.ContainsKey(key))
                        result[key] = (0, 0);

                    result[key] = (result[key].Down + r.DownloadBytes, result[key].Up + r.UploadBytes);
                }
            }
            return result;
        }

        public static Dictionary<string, (long Down, long Up)> GetYearlyTraffic()
        {
            var records = LoadRecords();
            var result = new Dictionary<string, (long Down, long Up)>();

            foreach (var r in records)
            {
                if (TryParseDate(r.Date, out DateTime dt))
                {
                    string key = dt.Year.ToString();
                    if (!result.ContainsKey(key))
                        result[key] = (0, 0);

                    result[key] = (result[key].Down + r.DownloadBytes, result[key].Up + r.UploadBytes);
                }
            }
            return result;
        }

        private static List<TrafficRecord> LoadRecords()
        {
            lock (lockObj)
            {
                try
                {
                    if (File.Exists(dataFilePath))
                    {
                        string json = File.ReadAllText(dataFilePath);
                        return JsonSerializer.Deserialize<List<TrafficRecord>>(json) ?? new List<TrafficRecord>();
                    }
                }
                catch (Exception ex)
                {
                    LogError("Traffic data could not be loaded", ex);
                }
                return new List<TrafficRecord>();
            }
        }

        private static void SaveRecords(List<TrafficRecord> records)
        {
            lock (lockObj)
            {
                try
                {
                    Directory.CreateDirectory(dataDirectory);
                    string json = JsonSerializer.Serialize(records);
                    string temporaryPath = dataFilePath + ".tmp";
                    File.WriteAllText(temporaryPath, json);
                    File.Move(temporaryPath, dataFilePath, true);
                }
                catch (Exception ex)
                {
                    LogError("Traffic data could not be saved", ex);
                }
            }
        }

        internal static void LogError(string message, Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"{message}: {ex}");
        }
    }

    class TrafficRecord
    {
        public string Date { get; set; } = string.Empty;
        public long DownloadBytes { get; set; }
        public long UploadBytes { get; set; }
    }

    class AppSettings
    {
        public bool IsDarkMode { get; set; } = false;
    }
}
