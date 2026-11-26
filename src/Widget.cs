// Today's events from every signed-in Google account, merged into one list. Click an event to open it
// in Google Calendar; the open button shows the whole day there.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Bwui;

namespace TodayCalendar
{
    /// <summary>
    /// A title cut off with "…" scrolls to its end while the pointer is on its row, then waits and starts over.
    /// Nothing moves otherwise, and nothing moves at all when Windows animations are turned off.
    /// </summary>
    static class Ticker
    {
        const double PixelsPerSecond = 40;

        public static void Start(TextBlock t, FrameworkElement box)
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double full = t.DesiredSize.Width, room = box.ActualWidth;
            double overflow = full - room;
            if (room <= 0 || overflow <= 1) return;

            t.TextTrimming = TextTrimming.None;
            t.Width = full;
            // WPF clips an element to its layout slot when it is wider than the slot, which would cut the
            // text at the old width. A negative right margin widens the slot; the box still clips the view.
            t.Margin = new Thickness(0, 0, -overflow, 0);
            var shift = new TranslateTransform();
            t.RenderTransform = shift;

            var scroll = TimeSpan.FromSeconds(overflow / PixelsPerSecond);
            var holdStart = TimeSpan.FromSeconds(0.6);
            var holdEnd = TimeSpan.FromSeconds(1.2);
            var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(holdStart)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(holdStart + scroll)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(holdStart + scroll + holdEnd)));
            shift.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        public static void Stop(TextBlock t)
        {
            var shift = t.RenderTransform as TranslateTransform;
            if (shift != null) shift.BeginAnimation(TranslateTransform.XProperty, null);
            t.RenderTransform = Transform.Identity;
            t.Width = double.NaN;
            t.Margin = new Thickness(0);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args) { Bwui.App.Run("TodayCalendar", args, () => new CalendarWidget()); }
    }

    class CalendarWidget : BwuiWindow
    {
        const string Id = "TodayCalendar";
        const string CalendarGlyph = "", OpenGlyph = "";
        const string SetupUrl = "https://github.com/byeonggwan/windows-today-calendar-widget#build-it-yourself";
        static readonly Color AccentL = Palette.Hex("#1A73E8"), AccentD = Palette.Hex("#8AB4F8");
        // One color per account, so two accounts' events can be told apart (the account is also in each row's name).
        static readonly Color[] AccountL = { Palette.Hex("#1A73E8"), Palette.Hex("#188038"), Palette.Hex("#C5221F"), Palette.Hex("#8430CE"), Palette.Hex("#B06000") };
        static readonly Color[] AccountD = { Palette.Hex("#8AB4F8"), Palette.Hex("#81C995"), Palette.Hex("#F28B82"), Palette.Hex("#D7AEFB"), Palette.Hex("#FDD663") };
        static readonly TimeSpan FetchEvery = TimeSpan.FromMinutes(5);
        const double RowHeight = 32, TimeColumn = 44;

        // Another day goes back to today once it has been left alone this long (an always-on widget
        // shouldn't keep showing tomorrow as if it were today).
        static readonly TimeSpan ReturnToToday = TimeSpan.FromMinutes(1);
        const string HomeGlyph = "";

        /// <summary>One day's events and fetch state.</summary>
        sealed class DayData
        {
            public List<CalEvent> Events;     // null until the first fetch for the day finishes
            public DateTime? FetchedAt;       // UTC of the last fetch where every account answered
            public DateTime LastAttempt = DateTime.MinValue;
            public string Problem;
            public bool Busy;
        }

        readonly string accountsPath, clientPath;
        OAuthClient client;
        List<Account> accounts = new List<Account>();
        readonly Dictionary<DateTime, DayData> days = new Dictionary<DateTime, DayData>();
        DateTime viewDay;                     // the day shown; today unless the user moved
        DateTime lastNav = DateTime.MinValue; // UTC of the last ‹ › press
        string problem;                       // sign-in and setup problems
        bool signingIn;
        DateTime? snapshotNow;

        DayData Day(DateTime d)
        {
            DayData x;
            if (!days.TryGetValue(d, out x)) days[d] = x = new DayData();
            return x;
        }

        DayData View { get { return Day(viewDay); } }
        bool ViewingToday { get { return viewDay == Now.Date; } }

        public CalendarWidget() : base(Id, "Today")
        {
            // The list grows and shrinks with the day's events: keep the header in place and let the bottom move.
            AnchorBottom = false;
            var dir = Bwui.App.DataDir(Id);
            accountsPath = System.IO.Path.Combine(dir, "accounts.json");
            clientPath = System.IO.Path.Combine(dir, "oauth-client.json");

            if (Bwui.App.IsSnapshot)
            {
                // "live": render your real accounts (for checking a setup; don't publish the image).
                if (Array.IndexOf(Bwui.App.SnapshotArgs ?? new string[0], "live") >= 0) LoadLive();
                else LoadSample();
                Render();
                return;
            }

            client = BuiltIn ?? OAuthClient.Load(clientPath);
            accounts = AccountStore.Load(accountsPath);
            viewDay = Now.Date;
            Render();

            // Relative times ("in 20 min") change every minute; fetches run every 5 minutes, doubled on battery.
            var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            tick.Tick += (s, e) =>
            {
                var today = Now.Date;
                if (viewDay != today && DateTime.UtcNow - lastNav >= ReturnToToday && !IsMouseOver) viewDay = today;
                FetchIfDue(today);
                if (viewDay != today) FetchIfDue(viewDay);
                Render();
            };
            tick.Start();
            Closed += (s, e) => tick.Stop();
            Dispatcher.BeginInvoke(new Action(Fetch), DispatcherPriority.Background);
        }

        void FetchIfDue(DateTime d)
        {
            if (DateTime.UtcNow - Day(d).LastAttempt >= Bwui.App.Interval(FetchEvery)) Fetch(d);
        }

        void Go(int delta)
        {
            viewDay = viewDay.AddDays(delta);
            lastNav = DateTime.UtcNow;
            // Keep the cache small: today, the day shown, and its neighbours.
            foreach (var d in new List<DateTime>(days.Keys))
                if (d != Now.Date && Math.Abs((d - viewDay).TotalDays) > 3) days.Remove(d);
            Render();
            FetchIfDue(viewDay);
        }

        void GoToday()
        {
            viewDay = Now.Date;
            lastNav = DateTime.MinValue;
            Render();
            FetchIfDue(viewDay);
        }

        DateTime Now { get { return snapshotNow ?? DateTime.Now; } }

        /// <summary>The client compiled into release builds; a build without one falls back to a client file.</summary>
        static OAuthClient BuiltIn
        {
            get { return string.IsNullOrEmpty(BuiltInClient.Id) ? null : new OAuthClient { Id = BuiltInClient.Id, Secret = BuiltInClient.Secret }; }
        }

        protected override WidgetSize[] SupportedSizes { get { return new[] { WidgetSize.Regular, WidgetSize.Compact, WidgetSize.Wide }; } }
        protected override UpdateSource Updates { get { return new UpdateSource("byeonggwan", "windows-today-calendar-widget", "TodayCalendar.exe"); } }
        protected override bool CanRefresh { get { return true; } }
        protected override void RefreshNow() { Fetch(); }

        // ---------- menu ----------

        protected override void AddMenuItems(ContextMenu menu)
        {
            menu.Items.Add(Item(Bwui.Text.T("구글 캘린더 열기", "Open Google Calendar"), OpenDay));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(Bwui.Text.T("구글 계정 추가", "Add Google account"), AddAccount));
            foreach (var a in accounts.ToArray())
            {
                var acc = a;
                if (acc.NeedsSignIn) menu.Items.Add(Item(Bwui.Text.T("다시 로그인: ", "Sign in again: ") + acc.Email, AddAccount));
                menu.Items.Add(Item(Bwui.Text.T("계정 제거: ", "Remove account: ") + acc.Email, () => RemoveAccount(acc)));
            }
            // Only builds without a built-in client (forks, local builds) need a client file.
            if (BuiltIn == null)
            {
                menu.Items.Add(Item(Bwui.Text.T("OAuth 클라이언트 파일 선택…", "Choose OAuth client file…"), ChooseClientFile));
                menu.Items.Add(Item(Bwui.Text.T("설정 방법 보기", "How to set up"), () => Open(SetupUrl)));
            }
        }

        void AddAccount()
        {
            if (client == null) { ChooseClientFile(); if (client == null) return; }
            if (signingIn) return;
            signingIn = true; problem = null; Render();
            var c = client;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Account a = null; string err = null;
                try { a = GoogleAuth.SignIn(c); }
                catch (Exception ex) { err = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    signingIn = false;
                    if (a != null)
                    {
                        // Signing in again with the same account replaces its token.
                        accounts.RemoveAll(x => string.Equals(x.Email, a.Email, StringComparison.OrdinalIgnoreCase));
                        accounts.Add(a);
                        SaveAccounts();
                        Fetch();
                    }
                    else problem = err;
                    Render();
                }));
            });
        }

        void RemoveAccount(Account a)
        {
            accounts.Remove(a);
            SaveAccounts();
            foreach (var d in days.Values) if (d.Events != null) d.Events.RemoveAll(e => e.Account == a.Email);
            Render();
            // Also revoke the grant at Google, best effort.
            var token = a.RefreshToken;
            if (!string.IsNullOrEmpty(token))
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var req = Http.Request("https://oauth2.googleapis.com/revoke?token=" + Uri.EscapeDataString(token), "POST", null);
                        req.ContentLength = 0;
                        Http.Read(req);
                    }
                    catch { }
                });
        }

        void SaveAccounts()
        {
            try { AccountStore.Save(accountsPath, accounts); }
            catch (Exception ex) { problem = ex.Message; }
        }

        void ChooseClientFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Bwui.Text.T("구글 OAuth 클라이언트 JSON 선택", "Choose the Google OAuth client JSON"),
                Filter = "JSON (*.json)|*.json"
            };
            if (dlg.ShowDialog() != true) return;
            var c = OAuthClient.Load(dlg.FileName);
            if (c == null)
            {
                problem = Bwui.Text.T("OAuth 클라이언트 파일이 아니에요 (데스크톱 앱 유형의 JSON이 필요해요)", "Not an OAuth client file (a Desktop app JSON is needed)");
                Render();
                return;
            }
            try { File.Copy(dlg.FileName, clientPath, true); } catch (Exception ex) { problem = ex.Message; }
            client = c; problem = null;
            Render();
        }

        // ---------- data ----------

        void Fetch() { Fetch(viewDay); }

        /// <summary>
        /// Fetches the day and the one after it in one round (all accounts and calendars in parallel),
        /// so › to tomorrow is already loaded.
        /// </summary>
        void Fetch(DateTime day)
        {
            var dd = Day(day);
            var next = Day(day.AddDays(1));
            if (dd.Busy || client == null || accounts.Count == 0) return;
            dd.Busy = next.Busy = true;
            dd.LastAttempt = next.LastAttempt = DateTime.UtcNow;
            var c = client;
            var accs = accounts.ToArray();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var perAccount = new List<CalEvent>[accs.Length];
                string err = null;
                System.Threading.Tasks.Parallel.For(0, accs.Length, i =>
                {
                    var a = accs[i];
                    try { perAccount[i] = GoogleCalendar.Range(c, a, day, day.AddDays(2)); a.NeedsSignIn = false; }
                    catch (SignInRequiredException) { a.NeedsSignIn = true; }
                    catch (Exception ex) { err = ex.Message; }
                });
                var all = new List<CalEvent>();
                foreach (var l in perAccount) if (l != null) all.AddRange(l);
                if (err != null) Diag.Log("fetch failed: " + err);
                else if (all.Count == 0)
                {
                    // Counts only: an empty result is worth a line when it isn't expected.
                    var counts = new List<string>();
                    foreach (var a in accs) counts.Add((a.Calendars == null ? "?" : a.Calendars.Count.ToString()) + (a.NeedsSignIn ? " (sign-in)" : ""));
                    Diag.Log("fetch returned no events; calendars per account: " + string.Join(", ", counts.ToArray()));
                }
                var first = Merge(all.FindAll(e => Overlaps(e, day)));
                var second = Merge(all.FindAll(e => Overlaps(e, day.AddDays(1))));
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Store(dd, first, err);
                    Store(next, second, err);
                    Render();
                }));
            });
        }

        static bool Overlaps(CalEvent e, DateTime day) { return e.Start < day.AddDays(1) && e.End > day; }

        static void Store(DayData d, List<CalEvent> events, string err)
        {
            d.Busy = false;
            if (err == null) { d.Events = events; d.FetchedAt = DateTime.UtcNow; d.Problem = null; return; }
            // Keep the last good list; the stale caption says how old it is (DESIGN.md §5).
            if (d.Events == null) d.Events = events;
            d.Problem = Bwui.Text.T("일정을 불러오지 못했어요", "Couldn't load events");
        }

        /// <summary>One entry per event even if it is on two calendars or two accounts; all-day first, then by start.</summary>
        static List<CalEvent> Merge(List<CalEvent> all)
        {
            var seen = new HashSet<string>();
            var list = new List<CalEvent>();
            foreach (var e in all)
            {
                var key = (e.Uid ?? e.Title) + "|" + e.Start.Ticks;
                if (seen.Add(key)) list.Add(e);
            }
            list.Sort((a, b) =>
            {
                if (a.AllDay != b.AllDay) return a.AllDay ? -1 : 1;
                int c = a.Start.CompareTo(b.Start);
                return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCulture);
            });
            return list;
        }

        // ---------- links ----------

        string AuthUser(string email) { return string.IsNullOrEmpty(email) ? "" : "authuser=" + Uri.EscapeDataString(email); }

        void OpenDay()
        {
            var d = viewDay == DateTime.MinValue ? Now.Date : viewDay;
            var url = "https://calendar.google.com/calendar/r/day/" + d.Year + "/" + d.Month + "/" + d.Day;
            if (accounts.Count > 0) url += "?" + AuthUser(accounts[0].Email);
            Open(url);
        }

        void OpenEvent(CalEvent e)
        {
            if (string.IsNullOrEmpty(e.Link)) { OpenDay(); return; }
            Open(e.Link + (e.Link.Contains("?") ? "&" : "?") + AuthUser(e.Account));
        }

        static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // ---------- rendering ----------

        protected override UIElement Build()
        {
            var p = Theme.Current;
            var accent = p.Accent(AccentL, AccentD);
            var sp = new StackPanel();
            var view = View;
            var events = view.Events;
            var f = Freshness.Of(view.FetchedAt, TimeSpan.FromMinutes(15), TimeSpan.FromHours(3));

            // ⌂ › [↗]: look ahead, then come back. Past days aren't offered. The home button keeps its
            // place when hidden, so › never moves under the pointer.
            var trail = new StackPanel { Orientation = Orientation.Horizontal };
            var home = Ui.IconButton(HomeGlyph, Bwui.Text.T("오늘로", "Back to today"), GoToday);
            home.Visibility = ViewingToday ? Visibility.Hidden : Visibility.Visible;
            trail.Children.Add(home);
            trail.Children.Add(Spaced(Ui.IconButton(Glyphs.ChevronRight, Bwui.Text.T("다음 날", "Next day"), () => Go(1))));
            if (CurrentSize != WidgetSize.Compact)
                trail.Children.Add(Spaced(Ui.IconButton(OpenGlyph, Bwui.Text.T("구글 캘린더에서 보기", "Open in Google Calendar"), OpenDay)));
            sp.Children.Add(Ui.Header(Ui.Icon(null, accent, CalendarGlyph), DayTitle(viewDay), trail));

            // Another day is named in words too, so it can't be mistaken for today.
            string rel = RelativeDay(viewDay - Now.Date);
            string caption = rel;
            if (f.Caption != null && events != null) caption = caption == null ? f.Caption : caption + " · " + f.Caption;
            if (caption != null)
            {
                var cap = Ui.Text(caption, TextStyle.Caption, rel != null ? accent : p.TextSecondary);
                cap.Margin = new Thickness(0, Tokens.Space1, 0, 0);
                sp.Children.Add(cap);
            }

            // Setup and sign-in states come first; they replace the list only when there is nothing to show.
            if (client == null && !Bwui.App.IsSnapshot)
            {
                AddMessage(sp, Ui.Message(Bwui.Text.T("구글 로그인 설정이 필요해요. OAuth 클라이언트 파일을 선택해 주세요.",
                    "Google sign-in isn't set up. Choose your OAuth client file."), Bwui.Text.T("파일 선택", "Choose file"), ChooseClientFile));
                return sp;
            }
            if (accounts.Count == 0)
            {
                AddMessage(sp, signingIn
                    ? Ui.Message(Bwui.Text.T("브라우저에서 구글 로그인을 마쳐 주세요…", "Finish signing in to Google in your browser…"))
                    : Ui.Message(problem ?? Bwui.Text.T("오늘 일정을 볼 구글 계정을 추가해 주세요.", "Add a Google account to see today's events."),
                        Bwui.Text.T("계정 추가", "Add account"), AddAccount));
                return sp;
            }
            if (signingIn) AddMessage(sp, Ui.Message(Bwui.Text.T("브라우저에서 구글 로그인을 마쳐 주세요…", "Finish signing in to Google in your browser…")));
            foreach (var a in accounts)
                if (a.NeedsSignIn)
                    AddMessage(sp, Ui.Message(a.Email + Bwui.Text.T(" 다시 로그인이 필요해요", " needs signing in again"), Bwui.Text.T("로그인", "Sign in"), AddAccount));

            if (events == null)
            {
                if (view.Problem != null) AddMessage(sp, Ui.Message(view.Problem, Bwui.Text.Retry, Fetch));
                else { var sk = Ui.Skeleton(ContentWidth, 3); sk.Margin = new Thickness(0, Tokens.Space3, 0, 0); sp.Children.Add(sk); }
                return sp;
            }
            if (view.Problem != null && f.HasData) AddMessage(sp, Ui.Message(view.Problem, Bwui.Text.Retry, Fetch));
            if (events.Count == 0)
            {
                AddMessage(sp, Ui.Message(ViewingToday ? Bwui.Text.T("오늘 일정이 없어요", "Nothing on your calendar today")
                                                       : Bwui.Text.T("이 날은 일정이 없어요", "Nothing on your calendar this day")));
                return sp;
            }

            var list = new StackPanel { Margin = new Thickness(-Tokens.Space2, Tokens.Space2, -Tokens.Space2, 0), Opacity = f.BodyOpacity };
            foreach (var row in Rows(p, accent, events)) list.Children.Add(row);
            sp.Children.Add(list);
            return sp;
        }

        static FrameworkElement Spaced(FrameworkElement b)
        {
            b.Margin = new Thickness(Tokens.Space1, 0, 0, 0);
            return b;
        }

        /// <summary>"내일", "모레", "3일 후"… for a later day; null for today.</summary>
        static string RelativeDay(TimeSpan diff)
        {
            int n = (int)Math.Round(diff.TotalDays);
            if (n <= 0) return null;
            if (n == 1) return Bwui.Text.T("내일", "Tomorrow");
            if (n == 2) return Bwui.Text.T("모레", "In 2 days");
            return Bwui.Text.T(n + "일 후", "In " + n + " days");
        }

        static void AddMessage(StackPanel sp, FrameworkElement m)
        {
            m.Margin = new Thickness(0, Tokens.Space3, 0, 0);
            sp.Children.Add(m);
        }

        int MaxRows { get { return CurrentSize == WidgetSize.Compact ? 4 : CurrentSize == WidgetSize.Wide ? 8 : 6; } }

        List<UIElement> Rows(Palette p, Color accent, List<CalEvent> events)
        {
            // "Finished", "now" and "next" only make sense for today; on a later day everything is ahead.
            var now = ViewingToday ? Now : DateTime.MinValue;
            var shown = new List<CalEvent>(events);
            // Too many for the size: what's still ahead matters most. Drop finished events first, then
            // all-day ones from the bottom up, then the latest timed ones; one row links to the full day.
            int hidden = 0;
            int room = MaxRows;
            while (shown.Count > room)
            {
                int i = shown.FindIndex(e => !e.AllDay && e.End <= now);
                if (i >= 0) { shown.RemoveAt(i); continue; }
                if (hidden == 0) room = MaxRows - 1;        // make room for the "N more" row
                if (shown.Count <= room) break;
                i = shown.FindLastIndex(e => e.AllDay);
                if (i < 0) i = shown.Count - 1;
                shown.RemoveAt(i);
                hidden++;
            }

            bool multi = accounts.Count > 1;
            var next = events.Find(e => !e.AllDay && e.Start > now);
            var rows = new List<UIElement>();
            foreach (var e in shown) rows.Add(EventRow(p, accent, e, now, e == next, multi));
            if (hidden > 0)
            {
                var more = Ui.Text(Bwui.Text.T("외 " + hidden + "개 · 캘린더에서 보기", hidden + " more · open calendar"), TextStyle.Caption);
                more.VerticalAlignment = VerticalAlignment.Center;
                rows.Add(Ui.ListRow(more, RowHeight - 4, Bwui.Text.T("구글 캘린더에서 오늘 일정 모두 보기", "Open today in Google Calendar"), OpenDay));
            }
            return rows;
        }

        UIElement EventRow(Palette p, Color accent, CalEvent e, DateTime now, bool isNext, bool multi)
        {
            bool past = !e.AllDay && e.End <= now;
            bool current = !e.AllDay && e.Start <= now && now < e.End;
            var main = past ? p.TextTertiary : p.TextPrimary;

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TimeColumn) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var time = Ui.Text(e.AllDay ? Bwui.Text.T("종일", "All day") : e.Start.ToString("HH:mm"), TextStyle.Caption,
                current ? accent : past ? p.TextTertiary : p.TextSecondary);
            time.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(time);

            if (multi)
            {
                int idx = Math.Max(0, AccountIndex(e.Account)) % AccountL.Length;
                var dot = new Ellipse
                {
                    Width = 6, Height = 6, Margin = new Thickness(0, 0, Tokens.Space2, 0),
                    Fill = Ui.Br(p.Accent(AccountL[idx], AccountD[idx])), VerticalAlignment = VerticalAlignment.Center,
                    Opacity = past ? 0.5 : 1, ToolTip = e.Account
                };
                Grid.SetColumn(dot, 1);
                g.Children.Add(dot);
            }

            var title = Ui.Text(e.Title, TextStyle.Body, main);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.ToolTip = e.Title + (e.Calendar != null ? "\n" + e.Calendar : "") + "\n" + e.Account;
            if (current) title.FontWeight = FontWeights.SemiBold;
            title.HorizontalAlignment = HorizontalAlignment.Left;
            // The title sits in a clipping box so a long one can scroll inside it on hover (see Ticker).
            var titleBox = new Grid { ClipToBounds = true };
            titleBox.Children.Add(title);
            Grid.SetColumn(titleBox, 2);
            g.Children.Add(titleBox);

            string status = null;
            if (current) status = "~" + e.End.ToString("HH:mm");
            else if (isNext && e.Start - now <= TimeSpan.FromHours(3)) status = Bwui.Text.T(Bwui.Text.Duration(e.Start - now) + " 후", "in " + Bwui.Text.Duration(e.Start - now));
            if (status != null)
            {
                var st = Ui.Text(status, TextStyle.Caption, current ? accent : p.TextSecondary);
                st.VerticalAlignment = VerticalAlignment.Center;
                st.Margin = new Thickness(Tokens.Space2, 0, 0, 0);
                Grid.SetColumn(st, 3);
                g.Children.Add(st);
            }

            string when = e.AllDay ? Bwui.Text.T("종일", "All day") : e.Start.ToString("HH:mm") + "–" + e.End.ToString("HH:mm");
            string name = when + ", " + e.Title + (current ? Bwui.Text.T(", 진행 중", ", now") : "") + ", " + e.Account;
            var row = Ui.ListRow(g, RowHeight, name, () => OpenEvent(e));
            row.MouseEnter += (s, a) => Ticker.Start(title, titleBox);
            row.MouseLeave += (s, a) => Ticker.Stop(title);
            return row;
        }

        int AccountIndex(string email)
        {
            for (int i = 0; i < accounts.Count; i++)
                if (string.Equals(accounts[i].Email, email, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        static string DayTitle(DateTime d)
        {
            return Bwui.Text.Korean
                ? d.Month + "월 " + d.Day + "일 (" + "일월화수목금토"[(int)d.DayOfWeek] + ")"
                : d.ToString("ddd, MMM d", CultureInfo.GetCultureInfo("en-US"));
        }

        // ---------- snapshot sample ----------

        void LoadLive()
        {
            client = BuiltIn ?? OAuthClient.Load(clientPath);
            accounts = AccountStore.Load(accountsPath);
            viewDay = DateTime.Today.AddDays(SnapshotDayOffset);
            if (client == null) return;
            var day = viewDay;
            var all = new List<CalEvent>();
            foreach (var a in accounts)
            {
                try { all.AddRange(GoogleCalendar.Range(client, a, day, day.AddDays(1))); }
                catch (SignInRequiredException) { a.NeedsSignIn = true; }
                catch (Exception ex) { problem = ex.Message; }
            }
            View.Events = Merge(all); View.FetchedAt = DateTime.UtcNow;
        }

        /// <summary>Snapshot argument "nextday" shows tomorrow, as after pressing ›.</summary>
        static int SnapshotDayOffset { get { return Array.IndexOf(Bwui.App.SnapshotArgs ?? new string[0], "nextday") >= 0 ? 1 : 0; } }

        void LoadSample()
        {
            var args = Bwui.App.SnapshotArgs ?? new string[0];
            snapshotNow = DateTime.Today.AddHours(10).AddMinutes(40);
            viewDay = DateTime.Today.AddDays(SnapshotDayOffset);
            var today = viewDay;
            if (Array.IndexOf(args, "noaccount") >= 0) { client = new OAuthClient(); return; }
            client = new OAuthClient();
            string work = "me@company.example", home = "me@gmail.example";
            accounts = new List<Account> { new Account { Email = work }, new Account { Email = home } };
            View.FetchedAt = DateTime.UtcNow;
            if (Array.IndexOf(args, "empty") >= 0) { View.Events = new List<CalEvent>(); return; }
            Func<string, int, int, int, int, string, CalEvent> ev = (t, h1, m1, h2, m2, acc) => new CalEvent
            {
                Title = t, Account = acc, Start = today.AddHours(h1).AddMinutes(m1), End = today.AddHours(h2).AddMinutes(m2)
            };
            View.Events = Merge(new List<CalEvent>
            {
                new CalEvent { Title = Bwui.Text.T("재택 근무", "Working from home"), Account = work, AllDay = true, Start = today, End = today.AddDays(1) },
                ev(Bwui.Text.T("주간 스탠드업", "Weekly stand-up"), 9, 30, 10, 0, work),
                ev(Bwui.Text.T("배포 계획 리뷰", "Release plan review"), 10, 30, 11, 30, work),
                ev(Bwui.Text.T("점심 · 팀 회식", "Team lunch"), 12, 0, 13, 0, work),
                ev(Bwui.Text.T("고객사 미팅", "Customer call"), 14, 0, 15, 0, work),
                ev(Bwui.Text.T("헬스장", "Gym"), 19, 0, 20, 0, home)
            });
        }
    }
}
