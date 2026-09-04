using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrafficPulse
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);

        public static void MinimizeMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
                }
            }
            catch { }
        }

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
                if (!File.Exists("app.ico"))
                {
                    string base64Icon = "AAABAAEAICAAAAAAIAAYAQAAFgAAAIlQTkcNChoKAAAADUlIRFIAAAAgAAAAIAgGAAAAc3p69AAAAN9JREFUeJxjZKi4/p9hAAHTQFo+6gAGBgYGFnSB/+0aNLeUsfIGnD34QgAGkF1JLYAtdAc8BEYdQJIDvjerM+xPlUMR+1CvRj8H/Pzzn4GFiZHBQYmLIkvJdgADAwND/Z7XDI0uIgPngH13vzEwMDAwOFIpFMhKhPV73jA0ulInFMhywIF73xj+/mNgcFKmPBRwloSEQP2eNwytbqIUO4DscuDQ/W8Mv/7+Z2BnYaTIASSFgEDjLRS+85xHFFnOwDDUSsJRB4w6YNQBtAA4CyJ6NM8ZGAZBCDCOdk4H2gEAhHcuOo4cfdcAAAAASUVORK5CYII=";
                    byte[] bytes = Convert.FromBase64String(base64Icon);
                    File.WriteAllBytes("app.ico", bytes);
                }
            }
            catch { }
        }
    }

    class TrayApplicationContext : ApplicationContext
    {
        private NotifyIcon trayIcon;
        private Thread monitorThread;
        private StatsForm statsForm;
        private ToolStripMenuItem darkModeMenuItem;
        private ToolStripMenuItem startupMenuItem;

        public TrayApplicationContext()
        {
            Icon appIcon = SystemIcons.Application;
            try
            {
                if (File.Exists("app.ico"))
                {
                    appIcon = new Icon("app.ico");
                }
            }
            catch { }

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

            monitorThread = new Thread(MonitorNetwork)
            {
                IsBackground = true
            };
            monitorThread.Start();
        }

        private void ToggleDarkMode(object sender, EventArgs e)
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

        private void ToggleStartup(object sender, EventArgs e)
        {
            bool newState = TrafficManager.ToggleStartupRegistry();
            startupMenuItem.Text = $"Start with Windows: {(newState ? "On" : "Off")}";
        }

        private void ShowStatsForm()
        {
            if (statsForm == null || statsForm.IsDisposed)
            {
                statsForm = new StatsForm();
            }
            statsForm.Show();
            statsForm.BringToFront();
        }

        private void MonitorNetwork()
        {
            int counter = 0;
            long previousBytesReceived = 0;
            long previousBytesSent = 0;
            bool isFirstRun = true;

            while (true)
            {
                Thread.Sleep(1000);
                counter++;

                if (counter >= 30)
                {
                    Program.MinimizeMemory();
                    counter = 0;
                }

                long currentBytesReceived = 0;
                long currentBytesSent = 0;
                GetTotalTraffic(out currentBytesReceived, out currentBytesSent);

                if (isFirstRun)
                {
                    previousBytesReceived = currentBytesReceived;
                    previousBytesSent = currentBytesSent;
                    isFirstRun = false;
                    continue;
                }

                long downloadDelta = currentBytesReceived - previousBytesReceived;
                long uploadDelta = currentBytesSent - previousBytesSent;

                if (downloadDelta < 0) downloadDelta = 0;
                if (uploadDelta < 0) uploadDelta = 0;

                if (downloadDelta > 1073741824) downloadDelta = 0;
                if (uploadDelta > 1073741824) uploadDelta = 0;

                if (downloadDelta > 0 || uploadDelta > 0)
                {
                    TrafficManager.AddTraffic(downloadDelta, uploadDelta);
                }

                if (trayIcon != null && trayIcon.Visible)
                {
                    string FormatSpeed(long bytesPerSec)
                    {
                        double megaBytes = bytesPerSec / 1048576.0;
                        if (megaBytes >= 1.0)
                            return $"{megaBytes:F1} MB/s";

                        return $"{bytesPerSec / 1024.0:F1} KB/s";
                    }

                    string tooltip = $"DL: {FormatSpeed(downloadDelta)} | UL: {FormatSpeed(uploadDelta)}";
                    if (tooltip.Length > 63) tooltip = tooltip.Substring(0, 63);
                    trayIcon.Text = tooltip;
                }

                previousBytesReceived = currentBytesReceived;
                previousBytesSent = currentBytesSent;
            }
        }

        private void GetTotalTraffic(out long totalReceived, out long totalSent)
        {
            totalReceived = 0;
            totalSent = 0;
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (NetworkInterface ni in interfaces)
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                IPInterfaceProperties props = ni.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;

                IPv4InterfaceStatistics stats = ni.GetIPv4Statistics();
                totalReceived += stats.BytesReceived;
                totalSent += stats.BytesSent;
            }
        }

        private void Exit(object sender, EventArgs e)
        {
            trayIcon.Visible = false;
            if (statsForm != null && !statsForm.IsDisposed)
            {
                statsForm.Close();
            }
            Application.Exit();
        }
    }

    class BufferedListView : ListView
    {
        public BufferedListView()
        {
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(this, true, null);
        }
    }

    class StatsForm : Form
    {
        private Panel pnlNav;
        private Button btnDaily, btnWeekly, btnMonthly, btnYearly;
        private BufferedListView lvStats;
        private System.Windows.Forms.Timer updateTimer;
        private string currentView = "Daily";

        public StatsForm()
        {
            this.Text = "TrafficPulse - Network Usage Statistics";
            this.Width = 500;
            this.Height = 380;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;

            try
            {
                if (File.Exists("app.ico"))
                {
                    this.Icon = new Icon("app.ico");
                }
            }
            catch { }

            pnlNav = new Panel() { Height = 45, Dock = DockStyle.Top };

            btnDaily = CreateNavButton("Daily", 0);
            btnWeekly = CreateNavButton("Weekly", 1);
            btnMonthly = CreateNavButton("Monthly", 2);
            btnYearly = CreateNavButton("Yearly", 3);

            btnDaily.Click += (s, e) => SwitchView("Daily");
            btnWeekly.Click += (s, e) => SwitchView("Weekly");
            btnMonthly.Click += (s, e) => SwitchView("Monthly");
            btnYearly.Click += (s, e) => SwitchView("Yearly");

            pnlNav.Controls.Add(btnDaily);
            pnlNav.Controls.Add(btnWeekly);
            pnlNav.Controls.Add(btnMonthly);
            pnlNav.Controls.Add(btnYearly);

            lvStats = new BufferedListView()
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 10.5f, FontStyle.Regular)
            };
            lvStats.Columns.Add("Period", 164, HorizontalAlignment.Left);
            lvStats.Columns.Add("Download (GB)", 160, HorizontalAlignment.Left);
            lvStats.Columns.Add("Upload (GB)", 160, HorizontalAlignment.Left);

            this.Controls.Add(lvStats);
            this.Controls.Add(pnlNav);

            ApplyTheme();
            LoadData();

            updateTimer = new System.Windows.Forms.Timer();
            updateTimer.Interval = 3000;
            updateTimer.Tick += (s, e) => LoadData();
            updateTimer.Start();
        }

        private Button CreateNavButton(string text, int index)
        {
            var btn = new Button()
            {
                Text = text,
                Width = 115,
                Height = 33,
                Top = 6,
                Left = 10 + (index * 120),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
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
            Color activeBackColor = isDark ? Color.FromArgb(0, 122, 204) : Color.FromArgb(0, 120, 215);
            Color activeForeColor = Color.White;
            Color inactiveBackColor = isDark ? Color.FromArgb(55, 55, 58) : Color.FromArgb(220, 220, 220);
            Color inactiveForeColor = isDark ? Color.FromArgb(200, 200, 200) : Color.Black;

            SetButtonStyle(btnDaily, currentView == "Daily", activeBackColor, activeForeColor, inactiveBackColor, inactiveForeColor);
            SetButtonStyle(btnWeekly, currentView == "Weekly", activeBackColor, activeForeColor, inactiveBackColor, inactiveForeColor);
            SetButtonStyle(btnMonthly, currentView == "Monthly", activeBackColor, activeForeColor, inactiveBackColor, inactiveForeColor);
            SetButtonStyle(btnYearly, currentView == "Yearly", activeBackColor, activeForeColor, inactiveBackColor, inactiveForeColor);
        }

        private void SetButtonStyle(Button btn, bool isActive, Color actBack, Color actFore, Color inactBack, Color inactFore)
        {
            btn.BackColor = isActive ? actBack : inactBack;
            btn.ForeColor = isActive ? actFore : inactFore;
            btn.Font = new Font("Segoe UI", 9.5f, isActive ? FontStyle.Bold : FontStyle.Regular);
        }

        public void ApplyTheme()
        {
            bool isDark = TrafficManager.LoadSettings().IsDarkMode;

            Color formBack = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245);
            Color navBack = isDark ? Color.FromArgb(37, 37, 38) : Color.FromArgb(235, 235, 235);
            Color listBack = isDark ? Color.FromArgb(43, 43, 43) : Color.White;
            Color listFore = isDark ? Color.White : Color.Black;

            this.BackColor = formBack;
            pnlNav.BackColor = navBack;
            lvStats.BackColor = listBack;
            lvStats.ForeColor = listFore;

            UpdateNavButtonStyles();
        }

        private void LoadData()
        {
            Dictionary<string, (long Down, long Up)> data;
            if (currentView == "Daily") data = TrafficManager.GetDailyTraffic();
            else if (currentView == "Weekly") data = TrafficManager.GetWeeklyTraffic();
            else if (currentView == "Monthly") data = TrafficManager.GetMonthlyTraffic();
            else data = TrafficManager.GetYearlyTraffic();

            var sortedData = data.OrderByDescending(x =>
            {
                if (currentView == "Daily" && DateTime.TryParseExact(x.Key, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dDate))
                    return dDate;

                if (currentView == "Monthly" && DateTime.TryParseExact(x.Key, "MMMM yyyy", new CultureInfo("en-US"), DateTimeStyles.None, out DateTime mDate))
                    return mDate;

                if (currentView == "Yearly" && int.TryParse(x.Key, out int yDate))
                    return new DateTime(yDate, 1, 1);

                return DateTime.MinValue;
            }).ThenByDescending(x => x.Key);

            FillListView(lvStats, sortedData.ToDictionary(k => k.Key, v => v.Value));
        }

        private void FillListView(BufferedListView lv, Dictionary<string, (long Down, long Up)> data)
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var kvp in data)
            {
                double downGB = kvp.Value.Down / 1024.0 / 1024.0 / 1024.0;
                double upGB = kvp.Value.Up / 1024.0 / 1024.0 / 1024.0;

                var item = new ListViewItem(kvp.Key);
                item.SubItems.Add($"{downGB:F2} GB");
                item.SubItems.Add($"{upGB:F2} GB");
                lv.Items.Add(item);
            }
            lv.EndUpdate();
        }
    }

    class TrafficManager
    {
        private static readonly object lockObj = new object();
        private static string dataFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrafficPulseData.json");
        private static string settingsFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrafficPulseSettings.json");
        private static string appName = "TrafficPulse";

        public static bool CheckStartupRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    object value = key?.GetValue(appName);
                    return value != null;
                }
            }
            catch { return false; }
        }

        public static bool ToggleStartupRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return false;

                    object value = key.GetValue(appName);
                    if (value != null)
                    {
                        key.DeleteValue(appName, false);
                        return false;
                    }
                    else
                    {
                        string exePath = Application.ExecutablePath;
                        key.SetValue(appName, $"\"{exePath}\"");
                        return true;
                    }
                }
            }
            catch { return false; }
        }

        public static void AddTraffic(long downloadDelta, long uploadDelta)
        {
            lock (lockObj)
            {
                var records = LoadRecords();
                string currentDate = DateTime.Now.ToString("dd-MM-yyyy");

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
            catch { }
            return new AppSettings();
        }

        public static void SaveSettings(AppSettings settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings);
                File.WriteAllText(settingsFilePath, json);
            }
            catch { }
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
            CultureInfo ci = CultureInfo.CurrentCulture;

            foreach (var r in records)
            {
                if (TryParseDate(r.Date, out DateTime dt))
                {
                    int weekOfYear = ci.Calendar.GetWeekOfYear(dt, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
                    string key = $"{dt.Year} - Week {weekOfYear}";

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
            try
            {
                if (File.Exists(dataFilePath))
                {
                    string json = File.ReadAllText(dataFilePath);
                    return JsonSerializer.Deserialize<List<TrafficRecord>>(json) ?? new List<TrafficRecord>();
                }
            }
            catch { }
            return new List<TrafficRecord>();
        }

        private static void SaveRecords(List<TrafficRecord> records)
        {
            try
            {
                string json = JsonSerializer.Serialize(records);
                File.WriteAllText(dataFilePath, json);
            }
            catch { }
        }
    }

    class TrafficRecord
    {
        public string Date { get; set; }
        public long DownloadBytes { get; set; }
        public long UploadBytes { get; set; }
    }

    class AppSettings
    {
        public bool IsDarkMode { get; set; } = false;
    }
}