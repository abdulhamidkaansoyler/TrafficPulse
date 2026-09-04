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

        public static string ApplicationDirectory => AppContext.BaseDirectory;

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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Memory optimization error: {ex}");
            }
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

            while (!monitorCancellation.IsCancellationRequested)
            {
                if (monitorCancellation.Token.WaitHandle.WaitOne(1000))
                    break;
                counter++;

                if (counter >= 30)
                {
                    Program.MinimizeMemory();
                    counter = 0;
                }

                long currentBytesReceived = 0;
                long currentBytesSent = 0;
                try
                {
                    GetTotalTraffic(out currentBytesReceived, out currentBytesSent);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Network statistics error: {ex}");
                    continue;
                }

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
                string iconPath = Path.Combine(Program.ApplicationDirectory, "app.ico");
                if (File.Exists(iconPath))
                {
                    this.Icon = new Icon(iconPath);
                }
            }
            catch (Exception ex)
            {
                TrafficManager.LogError("Icon loading error", ex);
            }

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

                if (currentView == "Weekly")
                {
                    string[] parts = x.Key.Split(" - Week ", StringSplitOptions.None);
                    if (parts.Length == 2 && int.TryParse(parts[0], out int weekYear) && int.TryParse(parts[1], out int weekNumber))
                    {
                        DateTime januaryFourth = new DateTime(weekYear, 1, 4);
                        int daysFromMonday = ((int)januaryFourth.DayOfWeek + 6) % 7;
                        return januaryFourth.AddDays(-daysFromMonday + ((weekNumber - 1) * 7));
                    }
                }

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
        private static readonly string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrafficPulse");
        private static readonly string dataFilePath = Path.Combine(dataDirectory, "TrafficPulseData.json");
        private static readonly string settingsFilePath = Path.Combine(dataDirectory, "TrafficPulseSettings.json");
        private const string AppName = "TrafficPulse";

        public static bool CheckStartupRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    object value = key?.GetValue(AppName);
                    return value != null;
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
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return false;

                    object value = key.GetValue(AppName);
                    if (value != null)
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
                    DateTime thursday = dt.AddDays(3 - (((int)dt.DayOfWeek + 6) % 7));
                    int isoYear = thursday.Year;
                    DateTime firstThursday = new DateTime(isoYear, 1, 4);
                    int weekOfYear = 1 + (int)((thursday.Date - firstThursday.Date).TotalDays / 7);
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