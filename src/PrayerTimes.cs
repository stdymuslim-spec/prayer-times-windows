// Prayer Times for Windows: a system tray app for Singapore's daily prayer times.
// Written in C# 5 so it builds with the compiler that ships with Windows (no SDK needed).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PrayerTimes
{
    class Prayer
    {
        public string Name;
        public DateTime Time; // Singapore wall-clock time

        public bool IsNotified { get { return Timetable.Notified.Contains(Name); } }
    }

    /// <summary>Daily times from timetable.json: "yyyy-MM-dd" -> prayer name -> "HH:mm" (Singapore time).</summary>
    class Timetable
    {
        public static readonly string[] Names = { "Subuh", "Syuruk", "Zohor", "Asar", "Maghrib", "Isyak" };
        /// <summary>Syuruk (sunrise) marks the end of Subuh, so it's shown in the menu but never notified.</summary>
        public static readonly HashSet<string> Notified = new HashSet<string> { "Subuh", "Zohor", "Asar", "Maghrib", "Isyak" };

        readonly Dictionary<string, Dictionary<string, string>> days;

        public Timetable(string path)
        {
            days = new Dictionary<string, Dictionary<string, string>>();
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                days = serializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception) { }
        }

        public static string DayKey(DateTime date)
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public DateTime? LastDay
        {
            get
            {
                string last = days.Keys.OrderBy(k => k, StringComparer.Ordinal).LastOrDefault();
                DateTime parsed;
                if (last != null && DateTime.TryParseExact(last, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                    return parsed;
                return null;
            }
        }

        public List<Prayer> PrayersOn(DateTime date)
        {
            var result = new List<Prayer>();
            Dictionary<string, string> times;
            if (!days.TryGetValue(DayKey(date), out times)) return result;
            foreach (string name in Names)
            {
                string text;
                if (!times.TryGetValue(name, out text)) continue;
                string[] parts = text.Split(':');
                int hour, minute;
                if (parts.Length != 2 || !int.TryParse(parts[0], out hour) || !int.TryParse(parts[1], out minute)) continue;
                result.Add(new Prayer { Name = name, Time = date.Date.AddHours(hour).AddMinutes(minute) });
            }
            return result;
        }

        public List<Prayer> PrayersFrom(DateTime date, int count)
        {
            var result = new List<Prayer>();
            for (int i = 0; i < count; i++) result.AddRange(PrayersOn(date.Date.AddDays(i)));
            return result;
        }
    }

    /// <summary>Plays an mp3 through the Windows multimedia API. Falls back to a system sound if the file is missing.</summary>
    class Sound
    {
        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        static extern int mciSendString(string command, StringBuilder result, int resultLength, IntPtr callback);

        readonly string path;
        readonly string alias;
        readonly System.Media.SystemSound fallback;

        public Sound(string path, string alias, System.Media.SystemSound fallback)
        {
            this.path = File.Exists(path) ? path : null;
            this.alias = alias;
            this.fallback = fallback;
        }

        public void Play()
        {
            if (path == null)
            {
                fallback.Play();
                return;
            }
            Stop();
            mciSendString("open \"" + path + "\" type mpegvideo alias " + alias, null, 0, IntPtr.Zero);
            mciSendString("play " + alias, null, 0, IntPtr.Zero);
        }

        public void Stop()
        {
            if (path == null) return;
            mciSendString("stop " + alias, null, 0, IntPtr.Zero);
            mciSendString("close " + alias, null, 0, IntPtr.Zero);
        }
    }

    static class Settings
    {
        const string Key = @"Software\PrayerTimes";

        public static bool Get(string name)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Key))
                return key != null && Convert.ToInt32(key.GetValue(name, 0)) == 1;
        }

        public static void Set(string name, bool value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Key))
                key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    static class LaunchAtLogin
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "PrayerTimes";

        public static bool Enabled
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(ValueName) != null;
            }
        }

        public static void Set(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(ValueName, false);
            }
        }
    }

    class TrayApp : ApplicationContext
    {
        const int ReminderMinutes = 10;

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        readonly TimeZoneInfo singapore = TimeZoneInfo.FindSystemTimeZoneById("Singapore Standard Time");
        readonly Timetable timetable;
        readonly Sound adhan;
        readonly Sound chime;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        /// <summary>Notifications already shown, so a wake-up within the same minute can't repeat one.</summary>
        readonly HashSet<string> shown = new HashSet<string>();
        string iconState;

        public TrayApp()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            timetable = new Timetable(Path.Combine(dir, "timetable.json"));
            adhan = new Sound(Path.Combine(dir, "call_to_prayer.mp3"), "ptadhan", System.Media.SystemSounds.Exclamation);
            chime = new Sound(Path.Combine(dir, "reminder.mp3"), "ptchime", System.Media.SystemSounds.Asterisk);

            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            menu.Opening += delegate { BuildMenu(); };
            tray.MouseUp += OnTrayMouseUp;
            // Clicking a notification stops its sound.
            tray.BalloonTipClicked += delegate { StopSounds(); };

            EnableLaunchAtLoginOnce();

            timer.Tick += delegate { Tick(); };
            SystemEvents.PowerModeChanged += delegate(object s, PowerModeChangedEventArgs e)
            {
                if (e.Mode == PowerModes.Resume) Tick();
            };
            SystemEvents.TimeChanged += delegate { Tick(); };
            SystemEvents.UserPreferenceChanged += delegate { UpdateIcon(Now()); };
            Tick();
        }

        DateTime Now()
        {
            return TimeZoneInfo.ConvertTime(DateTime.UtcNow, singapore);
        }

        // MARK: Notifications

        /// <summary>
        /// Runs at the start of every minute (prayer times are whole minutes): shows any notification that's due
        /// and refreshes the countdown.
        /// </summary>
        void Tick()
        {
            DateTime now = Now();
            if (!Settings.Get("paused"))
            {
                foreach (Prayer prayer in timetable.PrayersOn(now.Date).Where(p => p.IsNotified))
                {
                    string at = FormatClock(prayer.Time);
                    ShowIfDue(prayer.Name + "-reminder", prayer.Time.AddMinutes(-ReminderMinutes), now,
                        ReminderMinutes + " minutes to " + prayer.Name, prayer.Name + " at " + at, chime);
                    ShowIfDue(prayer.Name, prayer.Time, now,
                        "It's time for " + prayer.Name + ".", prayer.Name + " · " + at, adhan);
                }
            }
            UpdateIcon(now);

            // Re-arm for just after the next minute boundary so the timer never drifts.
            DateTime utc = DateTime.UtcNow;
            timer.Stop();
            timer.Interval = 60000 - (utc.Second * 1000 + utc.Millisecond) + 250;
            timer.Start();
        }

        /// <summary>Shows a notification during the minute it's due. Anything later (the PC was asleep) is skipped.</summary>
        void ShowIfDue(string id, DateTime time, DateTime now, string title, string body, Sound sound)
        {
            string key = Timetable.DayKey(time) + "-" + id;
            if (now < time || now >= time.AddMinutes(1) || shown.Contains(key)) return;
            shown.Add(key);
            Show(title, body, sound);
        }

        void Show(string title, string body, Sound sound)
        {
            tray.ShowBalloonTip(10000, title, body, ToolTipIcon.None);
            if (Settings.Get("silent")) return;
            sound.Play();
        }

        void StopSounds()
        {
            adhan.Stop();
            chime.Stop();
        }

        // MARK: Tray icon

        Prayer NextPrayer(DateTime now)
        {
            return timetable.PrayersFrom(now.Date, 2).FirstOrDefault(p => p.IsNotified && p.Time > now);
        }

        static string FormatClock(DateTime time)
        {
            return time.ToString("h:mm tt", CultureInfo.InvariantCulture);
        }

        void UpdateIcon(DateTime now)
        {
            bool paused = Settings.Get("paused");
            bool silent = Settings.Get("silent");
            bool lightTaskbar = TaskbarIsLight();

            Prayer next = NextPrayer(now);
            string text;
            if (next == null)
            {
                text = "No timetable";
            }
            else
            {
                int minutes = (int)Math.Ceiling((next.Time - now).TotalMinutes);
                string countdown = minutes >= 60 ? (minutes / 60) + "h " + (minutes % 60) + "m" : minutes + "m";
                text = next.Name + " in " + countdown;
            }
            if (paused) text += " (paused)";
            else if (silent) text += " (silent)";
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;

            string state = (paused ? "p" : "n") + (lightTaskbar ? "l" : "d");
            if (state == iconState) return;
            iconState = state;
            SetTrayIcon(DrawIcon(lightTaskbar ? Color.FromArgb(32, 32, 32) : Color.White, paused));
        }

        static bool TaskbarIsLight()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key != null && Convert.ToInt32(key.GetValue("SystemUsesLightTheme", 0)) == 1;
            }
            catch (Exception) { return false; }
        }

        /// <summary>A crescent moon with a star; struck through when notifications are paused.</summary>
        static Bitmap DrawIcon(Color color, bool paused)
        {
            const int size = 32;
            var bitmap = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(color))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var moon = new GraphicsPath())
                using (var bite = new GraphicsPath())
                {
                    moon.AddEllipse(3, 3, 24, 24);
                    bite.AddEllipse(10, 0, 24, 24);
                    using (var region = new Region(moon))
                    {
                        region.Exclude(bite);
                        g.FillRegion(brush, region);
                    }
                }
                g.FillEllipse(brush, 20, 14, 7, 7);
                if (paused)
                {
                    using (var pen = new Pen(color, 3f))
                        g.DrawLine(pen, 3, 29, 29, 3);
                }
            }
            return bitmap;
        }

        void SetTrayIcon(Bitmap bitmap)
        {
            IntPtr handle = bitmap.GetHicon();
            Icon old = tray.Icon;
            using (Icon temporary = Icon.FromHandle(handle))
                tray.Icon = (Icon)temporary.Clone();
            DestroyIcon(handle);
            bitmap.Dispose();
            if (old != null) old.Dispose();
        }

        // MARK: Menu

        void OnTrayMouseUp(object sender, MouseEventArgs e)
        {
            // Left click opens the same menu as right click.
            if (e.Button != MouseButtons.Left) return;
            MethodInfo show = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            if (show != null) show.Invoke(tray, null);
        }

        void BuildMenu()
        {
            menu.Items.Clear();
            DateTime now = Now();
            Prayer next = NextPrayer(now);
            List<Prayer> todays = timetable.PrayersOn(now.Date);

            menu.Items.Add(Label(now.ToString("dddd, d MMMM", CultureInfo.InvariantCulture)));
            if (todays.Count == 0) menu.Items.Add(Label("No prayer times for today"));
            foreach (Prayer prayer in todays)
            {
                bool isNext = next != null && prayer.Time == next.Time;
                var item = new ToolStripMenuItem(prayer.Name);
                item.ShortcutKeyDisplayString = FormatClock(prayer.Time);
                if (isNext) item.Font = new Font(menu.Font, FontStyle.Bold);
                if (prayer.Time <= now || !prayer.IsNotified) item.ForeColor = SystemColors.GrayText;
                menu.Items.Add(item);
            }

            DateTime? lastDay = timetable.LastDay;
            if (lastDay.HasValue && (lastDay.Value - now).TotalDays < 14)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(Label("⚠ Timetable ends " + lastDay.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture)));
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Silent (Pop-ups Only)", ToggleSilent, Settings.Get("silent")));
            menu.Items.Add(Item("Pause Notifications", TogglePaused, Settings.Get("paused")));
            menu.Items.Add(Item("Test 10-Minute Reminder", delegate { Show(ReminderMinutes + " minutes to " + TestPrayerName(), "Test notification", chime); }, false));
            menu.Items.Add(Item("Test Prayer Time", delegate { Show("It's time for " + TestPrayerName() + ".", "Test notification", adhan); }, false));
            menu.Items.Add(Item("Launch at Login", delegate { LaunchAtLogin.Set(!LaunchAtLogin.Enabled); }, LaunchAtLogin.Enabled));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Quit Prayer Times", Quit, false));
        }

        static ToolStripMenuItem Label(string text)
        {
            return new ToolStripMenuItem(text) { Enabled = false };
        }

        static ToolStripMenuItem Item(string text, EventHandler handler, bool on)
        {
            var item = new ToolStripMenuItem(text) { Checked = on };
            item.Click += handler;
            return item;
        }

        // MARK: Actions

        string TestPrayerName()
        {
            Prayer next = NextPrayer(Now());
            return next != null ? next.Name : "Zohor";
        }

        void ToggleSilent(object sender, EventArgs e)
        {
            bool silent = !Settings.Get("silent");
            Settings.Set("silent", silent);
            if (silent) StopSounds();
            Tick();
        }

        void TogglePaused(object sender, EventArgs e)
        {
            Settings.Set("paused", !Settings.Get("paused"));
            Tick();
        }

        void Quit(object sender, EventArgs e)
        {
            StopSounds();
            timer.Stop();
            tray.Visible = false; // otherwise the icon lingers in the tray until hovered
            tray.Dispose();
            ExitThread();
        }

        void EnableLaunchAtLoginOnce()
        {
            if (!Settings.Get("launchAtLoginConfigured"))
            {
                Settings.Set("launchAtLoginConfigured", true);
                LaunchAtLogin.Set(true);
            }
            else if (LaunchAtLogin.Enabled)
            {
                LaunchAtLogin.Set(true); // keeps the login entry pointing at this copy if the app was moved
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (new Mutex(true, "Local\\PrayerTimesTray", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.Run(new TrayApp());
            }
        }
    }
}
