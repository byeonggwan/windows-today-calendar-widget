// BWUI — app host, settings, autostart and text. See DESIGN.md §6, §8.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

// Targeting 4.8 turns on WPF's per-monitor DPI scaling (DESIGN.md §6.8, C11).
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
// The app version in the exe's file properties, so BWHub can tell which version is installed (DESIGN.md §6.11).
[assembly: System.Reflection.AssemblyInformationalVersion(Bwui.AppInfo.Version)]

namespace Bwui
{
    public static class App
    {
        public const string Version = "0.1.0";

        /// <summary>True while rendering a --snapshot image: no timers, no saving, no fetching.</summary>
        public static bool IsSnapshot { get; private set; }

        /// <summary>Snapshot arguments after the theme, e.g. "--snapshot a.png dark ring" → ["ring"].</summary>
        public static string[] SnapshotArgs { get; private set; }

        /// <summary>--debug-menu: start above other windows with the menu open (development only).</summary>
        public static bool DebugMenu { get; private set; }

        /// <summary>
        /// Runs a widget. Handles single instance (C14), software rendering, theme watching, and:
        ///   --snapshot &lt;png&gt; [light|dark] [extra…]   render a preview and exit (C15)
        ///   --contrast &lt;txt&gt;                       write the palette contrast report and exit (C2)
        /// </summary>
        public static void Run(string appId, string[] args, Func<BwuiWindow> create)
        {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            args = args ?? new string[0];

            if (args.Length >= 2 && args[0] == "--contrast")
            {
                File.WriteAllText(args[1], Palette.ContrastReport(), new UTF8Encoding(false));
                return;
            }

            if (args.Length >= 2 && args[0] == "--snapshot")
            {
                IsSnapshot = true;
                var extra = new List<string>();
                for (int i = 3; i < args.Length; i++) extra.Add(args[i]);
                SnapshotArgs = extra.ToArray();
                var w = create();
                Theme.Mode = args.Length >= 3 && args[2] == "dark" ? ThemeMode.Dark : ThemeMode.Light;
                w.SaveSnapshot(args[1]);
                return;
            }

            DebugMenu = Array.IndexOf(args, "--debug-menu") >= 0;
            Updater.AfterUpdate(args);
            bool created;
            using (var mx = new Mutex(true, "Local\\" + appId + "_Single", out created))
            {
                if (!created) return;
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                Theme.Watch();
                app.Run(create());
            }
        }

        /// <summary>A refresh interval, doubled on battery (C16).</summary>
        public static TimeSpan Interval(TimeSpan normal)
        {
            try
            {
                if (SystemParameters.PowerLineStatus == PowerLineStatus.Offline) return TimeSpan.FromTicks(normal.Ticks * 2);
            }
            catch { }
            return normal;
        }

        public static string DataDir(string appId)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appId);
        }
    }

    /// <summary>%LOCALAPPDATA%\&lt;AppId&gt;\settings.txt, key=value lines (DESIGN.md §6.5).</summary>
    public sealed class Settings
    {
        public readonly string Dir, FilePath;
        readonly Dictionary<string, string> map = new Dictionary<string, string>();

        public Settings(string appId)
        {
            Dir = App.DataDir(appId);
            FilePath = Path.Combine(Dir, "settings.txt");
            try { Directory.CreateDirectory(Dir); } catch { }
            Reload();
        }

        /// <summary>Reads the file again, e.g. after BWHub changed it (DESIGN.md §6.11).</summary>
        public void Reload()
        {
            map.Clear();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) map[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { }
        }

        public string Get(string key, string def = null)
        {
            string v;
            return map.TryGetValue(key, out v) ? v : def;
        }

        public bool GetBool(string key, bool def)
        {
            var v = Get(key);
            return v == null ? def : v != "0";
        }

        public double? GetDouble(string key)
        {
            double d;
            var v = Get(key);
            if (v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        public void Set(string key, string value) { map[key] = value; Save(); }
        public void SetBool(string key, bool value) { Set(key, value ? "1" : "0"); }
        public void SetDouble(string key, double value) { Set(key, value.ToString(CultureInfo.InvariantCulture)); }

        void Save()
        {
            if (App.IsSnapshot) return;
            try
            {
                var keys = new List<string>(map.Keys);
                keys.Sort(StringComparer.Ordinal);
                var lines = new List<string>();
                foreach (var k in keys) lines.Add(k + "=" + map[k]);
                File.WriteAllLines(FilePath, lines.ToArray(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    /// <summary>HKCU Run entry named after the AppId.</summary>
    public static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsEnabled(string appId)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(appId) != null;
            }
            catch { return false; }
        }

        public static void Set(string appId, bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(appId, "\"" + System.Reflection.Assembly.GetEntryAssembly().Location + "\"");
                    else k.DeleteValue(appId, false);
                }
            }
            catch { }
        }
    }

    /// <summary>Built-in Korean/English strings and time formatting (DESIGN.md §8).</summary>
    public static class Text
    {
        public static bool Korean = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";

        public static string T(string ko, string en) { return Korean ? ko : en; }

        public static string Refresh { get { return T("지금 새로고침", "Refresh now"); } }
        public static string StartWithWindows { get { return T("Windows 시작 시 실행", "Start with Windows"); } }
        public static string Exit { get { return T("종료", "Exit"); } }
        public static string Loading { get { return T("불러오는 중…", "Loading…"); } }
        public static string Retry { get { return T("다시 시도", "Retry"); } }
        public static string Caution { get { return T("주의", "low"); } }
        public static string Critical { get { return T("위험", "very low"); } }
        public static string NothingShown { get { return T("표시할 항목이 없어요. 우클릭해서 켜세요.", "Nothing to show. Right-click to turn a section on."); } }

        public static string CheckForUpdates { get { return T("업데이트 확인", "Check for updates"); } }
        public static string AutoUpdate { get { return T("자동 업데이트", "Update automatically"); } }
        public static string CurrentVersion { get { return T("현재", "current"); } }
        public static string Checking { get { return T("업데이트 확인 중…", "Checking for updates…"); } }
        public static string Updating { get { return T("업데이트 중…", "Updating…"); } }
        public static string UpToDate { get { return T("최신 버전이에요", "You're up to date"); } }
        public static string UpdateFailed { get { return T("업데이트하지 못했어요", "Couldn't update"); } }
        public static string InstallUpdate(string tag) { return T("업데이트 설치 (" + tag + ")", "Install update (" + tag + ")"); }

        public static string ThemeItem(ThemeMode m)
        {
            string v = m == ThemeMode.Light ? T("라이트", "Light") : m == ThemeMode.Dark ? T("다크", "Dark") : T("시스템 설정", "System");
            return T("테마: ", "Theme: ") + v;
        }

        public static string SizeItem(WidgetSize s)
        {
            string v = s == WidgetSize.Compact ? T("작게", "Compact") : s == WidgetSize.Wide ? T("넓게", "Wide") : T("보통", "Regular");
            return T("크기: ", "Size: ") + v;
        }

        /// <summary>One unit: "12분 전" / "12 min ago".</summary>
        public static string Ago(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return T("방금", "just now");
            if (t.TotalHours < 1) return (int)t.TotalMinutes + T("분 전", " min ago");
            if (t.TotalDays < 1) return (int)t.TotalHours + T("시간 전", " h ago");
            return (int)t.TotalDays + T("일 전", " d ago");
        }

        /// <summary>"12분 전 기준" / "as of 12 min ago".</summary>
        public static string AsOf(TimeSpan t)
        {
            return Korean ? Ago(t) + " 기준" : "as of " + Ago(t);
        }

        /// <summary>Two largest units: "2시간 10분" / "2h 10m".</summary>
        public static string Duration(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return T("1분 미만", "<1 min");
            if (t.TotalHours < 1) return (int)t.TotalMinutes + T("분", " min");
            if (t.TotalDays < 1) return Korean ? (int)t.TotalHours + "시간 " + t.Minutes + "분" : (int)t.TotalHours + "h " + t.Minutes + "m";
            return Korean ? (int)t.TotalDays + "일 " + t.Hours + "시간" : (int)t.TotalDays + "d " + t.Hours + "h";
        }
    }
}
