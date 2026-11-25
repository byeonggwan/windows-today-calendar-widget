// BWUI — updates from GitHub Releases. See DESIGN.md §6.10.
// Checks once a day without signing in, verifies every file's SHA-256, swaps the files and restarts.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Threading;

namespace Bwui
{
    /// <summary>Where a widget's releases live, and which release assets replace which files next to the exe.</summary>
    public sealed class UpdateSource
    {
        public readonly string Owner, Repo;
        public readonly string[] Files;

        public UpdateSource(string owner, string repo, params string[] files)
        {
            Owner = owner; Repo = repo; Files = files;
        }
    }

    public enum UpdateState { Idle, Checking, UpToDate, Available, Installing, Failed }

    public sealed class Updater
    {
        const string StageFolder = "update";
        static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);

        readonly UpdateSource source;
        readonly Settings settings;
        readonly Dispatcher ui;
        readonly string appId;
        DispatcherTimer timer;

        public UpdateState State { get; private set; }
        public string LatestTag { get; private set; }
        public string Error { get; private set; }
        public event EventHandler Changed;

        Dictionary<string, string> urls = new Dictionary<string, string>();
        Dictionary<string, string> digests = new Dictionary<string, string>();

        public Updater(string appId, UpdateSource source, Settings settings, Dispatcher ui)
        {
            this.appId = appId; this.source = source; this.settings = settings; this.ui = ui;
        }

        public static Version Current { get { return ParseVersion(AppInfo.Version) ?? new Version(0, 0); } }

        /// <summary>Install without asking (opt-in). Off means "notify only".</summary>
        public bool Auto
        {
            get { return settings.GetBool("update_auto", false); }
            set
            {
                settings.SetBool("update_auto", value);
                // Turning it on checks right away; turning it off stops background checks.
                if (value && IsInstalledCopy)
                {
                    if (State == UpdateState.Available) Install();
                    else Check(false);
                }
            }
        }

        /// <summary>Only the installed copy (in %LOCALAPPDATA%\&lt;AppId&gt;) updates itself; a build in bin\ never does.</summary>
        public bool IsInstalledCopy
        {
            get
            {
                try
                {
                    var exeDir = Path.GetFullPath(Path.GetDirectoryName(EntryExe)).TrimEnd('\\');
                    return string.Equals(exeDir, Path.GetFullPath(App.DataDir(appId)).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }

        static string EntryExe { get { return System.Reflection.Assembly.GetEntryAssembly().Location; } }

        /// <summary>
        /// Background checks run only with "Update automatically" on: a minute after start, then once a day.
        /// With it off, nothing is checked until the user picks "Check for updates".
        /// </summary>
        public void Start()
        {
            timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMinutes(1) };
            timer.Tick += (s, e) =>
            {
                timer.Interval = TimeSpan.FromHours(1);
                if (!Auto) return;
                var last = settings.GetDouble("update_checked");
                if (!last.HasValue || DateTime.UtcNow - FromEpoch(last.Value) >= CheckEvery) Check(false);
            };
            timer.Start();
        }

        public void Stop() { if (timer != null) timer.Stop(); }

        public void Check(bool manual)
        {
            if (State == UpdateState.Checking || State == UpdateState.Installing) return;
            Set(UpdateState.Checking, null);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string tag;
                    Dictionary<string, string> u, d;
                    FetchLatest(source, out tag, out u, out d);
                    var v = ParseVersion(tag);
                    ui.BeginInvoke(new Action(() =>
                    {
                        settings.SetDouble("update_checked", Math.Floor(ToEpoch(DateTime.UtcNow)));
                        LatestTag = tag; urls = u; digests = d;
                        if (v != null && v > Current)
                        {
                            Set(UpdateState.Available, null);
                            if (Auto && IsInstalledCopy) Install();
                        }
                        else Set(manual ? UpdateState.UpToDate : UpdateState.Idle, null);
                    }));
                }
                catch (Exception ex)
                {
                    // A failed background check stays quiet; a check the user asked for says why.
                    ui.BeginInvoke(new Action(() => Set(manual ? UpdateState.Failed : UpdateState.Idle, ex.Message)));
                }
            });
        }

        /// <summary>Download, verify and apply. Ends with a restart, or with the old version still in place.</summary>
        public void Install()
        {
            if (State == UpdateState.Installing) return;
            if (!IsInstalledCopy) { Set(UpdateState.Failed, Text.T("설치된 위젯에서만 업데이트할 수 있어요", "Only the installed widget can update itself")); return; }
            Set(UpdateState.Installing, null);
            var stage = Path.Combine(settings.Dir, StageFolder);
            var u = urls; var d = digests;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (Directory.Exists(stage)) Directory.Delete(stage, true);
                    Directory.CreateDirectory(stage);
                    foreach (var f in source.Files) Download(f, u, d, Path.Combine(stage, f));
                    ui.BeginInvoke(new Action(() => Apply(stage)));
                }
                catch (Exception ex)
                {
                    ui.BeginInvoke(new Action(() => Set(UpdateState.Failed, ex.Message)));
                }
            });
        }

        void Set(UpdateState s, string error)
        {
            State = s;
            Error = error;
            var h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }

        // ---------- GitHub ----------

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new WebClient();
            // A generic client name; nothing about the user goes out with the request.
            c.Headers[HttpRequestHeader.UserAgent] = "BWUI-Updater";
            c.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            c.Encoding = Encoding.UTF8;
            return c;
        }

        static void FetchLatest(UpdateSource src, out string tag, out Dictionary<string, string> urls, out Dictionary<string, string> digests)
        {
            urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            digests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string json;
            using (var c = Client())
                json = c.DownloadString("https://api.github.com/repos/" + src.Owner + "/" + src.Repo + "/releases/latest");
            var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            tag = root["tag_name"] as string;
            var assets = root.ContainsKey("assets") ? root["assets"] as ArrayList : null;
            if (assets == null) return;
            foreach (Dictionary<string, object> a in assets)
            {
                var name = a["name"] as string;
                var url = a["browser_download_url"] as string;
                if (name == null || url == null) continue;
                urls[name] = url;
                object dg;
                if (a.TryGetValue("digest", out dg) && dg is string && ((string)dg).StartsWith("sha256:"))
                    digests[name] = ((string)dg).Substring(7);
            }
        }

        static void Download(string file, Dictionary<string, string> urls, Dictionary<string, string> digests, string dest)
        {
            string url;
            if (!urls.TryGetValue(file, out url)) throw new Exception(Text.T("릴리스에 " + file + " 파일이 없어요", "The release has no " + file));
            CheckHost(url);

            // Expected hash: GitHub's own asset digest, or a "<file>.sha256" asset next to it.
            string expected;
            if (!digests.TryGetValue(file, out expected))
            {
                string shaUrl;
                if (!urls.TryGetValue(file + ".sha256", out shaUrl)) throw new Exception(Text.T(file + "의 SHA-256 값이 없어 설치하지 않았어요", "No SHA-256 for " + file + ", so it was not installed"));
                CheckHost(shaUrl);
                using (var c = Client()) expected = c.DownloadString(shaUrl).Trim().Split(' ', '\t')[0];
            }

            byte[] bytes;
            using (var c = Client()) bytes = c.DownloadData(url);
            string actual;
            using (var sha = SHA256.Create()) actual = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
            if (!string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new Exception(Text.T(file + "의 SHA-256이 맞지 않아 설치하지 않았어요", "SHA-256 mismatch for " + file + ", so it was not installed"));
            File.WriteAllBytes(dest, bytes);
        }

        static void CheckHost(string url)
        {
            var u = new Uri(url);
            if (u.Scheme != Uri.UriSchemeHttps || !string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Unexpected download host: " + u.Host);
        }

        // ---------- apply ----------

        void Apply(string stage)
        {
            var dir = Path.GetDirectoryName(EntryExe);
            var moved = new List<string>();
            try
            {
                foreach (var f in source.Files)
                {
                    var target = Path.Combine(dir, f);
                    var old = target + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    // A running exe can be renamed but not overwritten.
                    if (File.Exists(target)) { File.Move(target, old); moved.Add(target); }
                    File.Move(Path.Combine(stage, f), target);
                }
            }
            catch (Exception ex)
            {
                // Put the old files back so the widget keeps working.
                foreach (var t in moved)
                {
                    try { if (File.Exists(t)) File.Delete(t); File.Move(t + ".old", t); } catch { }
                }
                Set(UpdateState.Failed, ex.Message);
                return;
            }
            try { Directory.Delete(stage, true); } catch { }
            Process.Start(EntryExe, "--after-update " + Process.GetCurrentProcess().Id);
            Application.Current.Shutdown();
        }

        /// <summary>Called at startup: wait for the old process after an update, then remove *.old files.</summary>
        internal static void AfterUpdate(string[] args)
        {
            int i = Array.IndexOf(args, "--after-update");
            int pid;
            if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out pid))
            {
                try { Process.GetProcessById(pid).WaitForExit(15000); } catch { }
            }
            try
            {
                foreach (var f in Directory.GetFiles(Path.GetDirectoryName(EntryExe), "*.old")) { try { File.Delete(f); } catch { } }
            }
            catch { }
        }

        // ---------- helpers ----------

        public static Version ParseVersion(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            Version v;
            return Version.TryParse(tag.TrimStart('v', 'V'), out v) ? v : null;
        }

        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static DateTime FromEpoch(double s) { return Epoch.AddSeconds(s); }
        static double ToEpoch(DateTime t) { return (t - Epoch).TotalSeconds; }
    }
}
