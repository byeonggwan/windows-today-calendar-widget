// Google sign-in for an installed app (loopback redirect + PKCE) and the two Calendar API calls the widget needs.
// Each account's refresh token is encrypted with Windows DPAPI for the current user; nothing else is stored.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace TodayCalendar
{
    /// <summary>The OAuth client the user created in Google Cloud (Desktop app type), from its downloaded JSON.</summary>
    sealed class OAuthClient
    {
        public string Id, Secret;

        public static OAuthClient Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var root = Json.Parse(File.ReadAllText(path));
                // Google's download wraps the fields in "installed"; accept a flat file too.
                var o = Json.Obj(root, "installed") ?? root;
                var c = new OAuthClient { Id = Json.Str(o, "client_id"), Secret = Json.Str(o, "client_secret") };
                return string.IsNullOrEmpty(c.Id) ? null : c;
            }
            catch { return null; }
        }
    }

    sealed class Account
    {
        public string Email;
        public string RefreshToken;
        public string AccessToken;
        public DateTime AccessExpiresUtc;
        /// <summary>Google rejected the refresh token (revoked, expired or password changed): sign in again.</summary>
        public bool NeedsSignIn;
        /// <summary>The calendars shown in Google Calendar (id, name), kept for a while so each refresh is one round of event requests.</summary>
        public List<KeyValuePair<string, string>> Calendars;
        public DateTime CalendarsAtUtc;
    }

    sealed class SignInRequiredException : Exception
    {
        public SignInRequiredException(string m) : base(m) { }
    }

    /// <summary>accounts.json: email + DPAPI-encrypted refresh token per account, in the widget's data folder.</summary>
    static class AccountStore
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TodayCalendar.refresh-token.v1");

        public static List<Account> Load(string path)
        {
            var list = new List<Account>();
            try
            {
                if (!File.Exists(path)) return list;
                var arr = new JavaScriptSerializer().Deserialize<ArrayList>(File.ReadAllText(path));
                foreach (Dictionary<string, object> o in arr)
                {
                    var a = new Account { Email = Json.Str(o, "email") };
                    try
                    {
                        var enc = Convert.FromBase64String(Json.Str(o, "refresh") ?? "");
                        a.RefreshToken = Encoding.UTF8.GetString(ProtectedData.Unprotect(enc, Entropy, DataProtectionScope.CurrentUser));
                    }
                    catch { a.NeedsSignIn = true; }
                    if (!string.IsNullOrEmpty(a.Email)) list.Add(a);
                }
            }
            catch { }
            return list;
        }

        public static void Save(string path, List<Account> accounts)
        {
            var arr = new List<Dictionary<string, object>>();
            foreach (var a in accounts)
            {
                var d = new Dictionary<string, object>();
                d["email"] = a.Email;
                if (!string.IsNullOrEmpty(a.RefreshToken))
                    d["refresh"] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(a.RefreshToken), Entropy, DataProtectionScope.CurrentUser));
                arr.Add(d);
            }
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(arr), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }

    static class GoogleAuth
    {
        const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
        const string TokenUrl = "https://oauth2.googleapis.com/token";
        // Read-only calendar access, plus the account's email address to label it.
        const string Scope = "openid email https://www.googleapis.com/auth/calendar.readonly";

        /// <summary>
        /// Opens the browser to Google's sign-in page and waits (up to 5 minutes) for the redirect to a
        /// one-shot listener on 127.0.0.1. Blocks the calling thread; run it off the UI thread.
        /// </summary>
        public static Account SignIn(OAuthClient client)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string redirect = "http://127.0.0.1:" + port + "/";
                string verifier = RandomUrlSafe(48);
                string challenge = Base64Url(SHA256.Create().ComputeHash(Encoding.ASCII.GetBytes(verifier)));
                string state = RandomUrlSafe(16);

                string url = AuthUrl
                    + "?client_id=" + Uri.EscapeDataString(client.Id)
                    + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                    + "&response_type=code"
                    + "&scope=" + Uri.EscapeDataString(Scope)
                    + "&code_challenge=" + challenge + "&code_challenge_method=S256"
                    + "&access_type=offline&prompt=" + Uri.EscapeDataString("consent select_account")
                    + "&state=" + state;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                // Browsers may open a spare connection or ask for /favicon.ico first: keep accepting until
                // the redirect with ?code= or ?error= arrives.
                var deadline = DateTime.UtcNow.AddMinutes(5);
                string code = null, error = null, gotState = null;
                while (code == null && error == null)
                {
                    var left = deadline - DateTime.UtcNow;
                    var accept = listener.AcceptTcpClientAsync();
                    if (left <= TimeSpan.Zero || !accept.Wait(left)) throw new TimeoutException(Bwui.Text.T("로그인 시간이 지났어요", "Sign-in timed out"));
                    using (var tcp = accept.Result)
                    using (var stream = tcp.GetStream())
                    {
                        Dictionary<string, string> query;
                        try { query = ReadRequestQuery(stream); } catch (IOException) { continue; }
                        code = Get(query, "code"); error = Get(query, "error"); gotState = Get(query, "state");
                        if (code == null && error == null) { NotFound(stream); continue; }
                        Respond(stream, code != null && error == null && gotState == state);
                    }
                }
                if (error != null) throw new Exception(Bwui.Text.T("구글이 로그인을 거부했어요: ", "Google refused the sign-in: ") + error);
                if (gotState != state || code == null) throw new Exception(Bwui.Text.T("로그인 응답이 올바르지 않아요", "Unexpected sign-in response"));

                var tok = PostForm(TokenUrl,
                    "code=" + Uri.EscapeDataString(code)
                    + "&client_id=" + Uri.EscapeDataString(client.Id)
                    + "&client_secret=" + Uri.EscapeDataString(client.Secret ?? "")
                    + "&code_verifier=" + verifier
                    + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                    + "&grant_type=authorization_code");
                var a = new Account
                {
                    Email = EmailFromIdToken(Json.Str(tok, "id_token")),
                    RefreshToken = Json.Str(tok, "refresh_token"),
                    AccessToken = Json.Str(tok, "access_token"),
                    AccessExpiresUtc = DateTime.UtcNow.AddSeconds(Json.Num(tok, "expires_in", 3600) - 60)
                };
                if (string.IsNullOrEmpty(a.RefreshToken)) throw new Exception(Bwui.Text.T("구글이 갱신 토큰을 주지 않았어요", "Google returned no refresh token"));
                if (string.IsNullOrEmpty(a.Email)) a.Email = "account";
                return a;
            }
            finally { listener.Stop(); }
        }

        /// <summary>A valid access token, refreshed when needed. Throws SignInRequiredException if Google revoked access.</summary>
        public static string AccessToken(OAuthClient client, Account a)
        {
            if (!string.IsNullOrEmpty(a.AccessToken) && DateTime.UtcNow < a.AccessExpiresUtc) return a.AccessToken;
            if (string.IsNullOrEmpty(a.RefreshToken)) throw new SignInRequiredException("no refresh token");
            Dictionary<string, object> tok;
            try
            {
                tok = PostForm(TokenUrl,
                    "client_id=" + Uri.EscapeDataString(client.Id)
                    + "&client_secret=" + Uri.EscapeDataString(client.Secret ?? "")
                    + "&refresh_token=" + Uri.EscapeDataString(a.RefreshToken)
                    + "&grant_type=refresh_token");
            }
            catch (WebException ex)
            {
                // invalid_grant: the token was revoked or expired. Anything else (offline, 5xx) is temporary.
                if (Http.ErrorBody(ex).Contains("invalid_grant")) throw new SignInRequiredException("invalid_grant");
                throw;
            }
            a.AccessToken = Json.Str(tok, "access_token");
            a.AccessExpiresUtc = DateTime.UtcNow.AddSeconds(Json.Num(tok, "expires_in", 3600) - 60);
            return a.AccessToken;
        }

        // ---------- loopback ----------

        static Dictionary<string, string> ReadRequestQuery(NetworkStream stream)
        {
            stream.ReadTimeout = 10000;
            var buf = new byte[8192];
            var sb = new StringBuilder();
            while (sb.ToString().IndexOf("\r\n", StringComparison.Ordinal) < 0)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            }
            // "GET /?code=...&state=... HTTP/1.1"
            var line = sb.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
            var parts = line.Split(' ');
            var q = new Dictionary<string, string>();
            if (parts.Length < 2) return q;
            int i = parts[1].IndexOf('?');
            if (i < 0) return q;
            foreach (var kv in parts[1].Substring(i + 1).Split('&'))
            {
                int e = kv.IndexOf('=');
                if (e > 0) q[Uri.UnescapeDataString(kv.Substring(0, e))] = Uri.UnescapeDataString(kv.Substring(e + 1).Replace('+', ' '));
            }
            return q;
        }

        static void Respond(NetworkStream stream, bool ok)
        {
            string msg = ok
                ? Bwui.Text.T("로그인했어요. 이 탭을 닫아도 돼요.", "Signed in. You can close this tab.")
                : Bwui.Text.T("로그인하지 못했어요. 위젯에서 다시 시도해 주세요.", "Sign-in didn't complete. Try again from the widget.");
            string html = "<!doctype html><meta charset=\"utf-8\"><title>Today Calendar</title>"
                + "<body style=\"font-family:'Segoe UI',sans-serif;display:grid;place-items:center;height:90vh;color:#1F1E1C\">"
                + "<p style=\"font-size:18px\">" + WebUtility.HtmlEncode(msg) + "</p></body>";
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        static void NotFound(NetworkStream stream)
        {
            try
            {
                var head = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                stream.Write(head, 0, head.Length);
            }
            catch { }
        }

        static string Get(Dictionary<string, string> q, string k) { string v; return q.TryGetValue(k, out v) ? v : null; }

        // ---------- helpers ----------

        static Dictionary<string, object> PostForm(string url, string form)
        {
            var req = Http.Request(url, "POST", null);
            req.ContentType = "application/x-www-form-urlencoded";
            var bytes = Encoding.ASCII.GetBytes(form);
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            return Json.Parse(Http.Read(req));
        }

        /// <summary>The email claim of the id_token we just received over TLS from Google; no signature check is needed for a label.</summary>
        static string EmailFromIdToken(string jwt)
        {
            if (string.IsNullOrEmpty(jwt)) return null;
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var p = parts[1].Replace('-', '+').Replace('_', '/');
            p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
            return Json.Str(Json.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p))), "email");
        }

        static string RandomUrlSafe(int bytes)
        {
            var b = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return Base64Url(b);
        }

        static string Base64Url(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
    }

    sealed class CalEvent
    {
        public string Title, Link, Account, Calendar, Uid;
        public DateTime Start, End;   // local time; for all-day events, midnight to midnight
        public bool AllDay;
    }

    static class GoogleCalendar
    {
        const string Api = "https://www.googleapis.com/calendar/v3/";

        static readonly TimeSpan CalendarListFor = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Every event overlapping [start, end) on the calendars this account shows in Google Calendar
        /// (the ones ticked in its list). Skips cancelled events, ones you declined, and working-location entries.
        /// The calendars are asked in parallel; the calendar list itself is reused for 30 minutes.
        /// </summary>
        public static List<CalEvent> Range(OAuthClient client, Account a, DateTime start, DateTime end)
        {
            string token = GoogleAuth.AccessToken(client, a);
            var cals = a.Calendars;
            if (cals == null || DateTime.UtcNow - a.CalendarsAtUtc > CalendarListFor)
            {
                cals = new List<KeyValuePair<string, string>>();
                var doc = Json.Parse(Get(token, "users/me/calendarList?fields=items(id,summary,summaryOverride,selected,hidden)&maxResults=250"));
                foreach (Dictionary<string, object> c in Json.Arr(doc, "items"))
                {
                    if (!Json.Bool(c, "selected") || Json.Bool(c, "hidden")) continue;
                    cals.Add(new KeyValuePair<string, string>(Json.Str(c, "id"), Json.Str(c, "summaryOverride") ?? Json.Str(c, "summary")));
                }
                a.Calendars = cals; a.CalendarsAtUtc = DateTime.UtcNow;
            }

            var results = new List<CalEvent>[cals.Count];
            Exception failure = null;
            System.Threading.Tasks.Parallel.For(0, cals.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                try { results[i] = Calendar(token, a.Email, cals[i].Key, cals[i].Value, start, end); }
                catch (Exception ex) { failure = ex; }
            });
            if (failure != null) throw failure;
            var list = new List<CalEvent>();
            foreach (var r in results) if (r != null) list.AddRange(r);
            return list;
        }

        static List<CalEvent> Calendar(string token, string account, string calId, string calName, DateTime start, DateTime end)
        {
            var list = new List<CalEvent>();
            string q = "calendars/" + Uri.EscapeDataString(calId) + "/events"
                + "?singleEvents=true&orderBy=startTime&maxResults=250"
                + "&timeMin=" + Uri.EscapeDataString(Rfc3339(start))
                + "&timeMax=" + Uri.EscapeDataString(Rfc3339(end))
                + "&fields=" + Uri.EscapeDataString("items(status,summary,start,end,htmlLink,iCalUID,eventType,attendees(self,responseStatus))");
            Dictionary<string, object> evs;
            try { evs = Json.Parse(Get(token, q)); }
            catch (WebException ex)
            {
                // A calendar we can't read (permissions changed) shouldn't hide the rest.
                var r = ex.Response as HttpWebResponse;
                if (r != null && (r.StatusCode == HttpStatusCode.NotFound || r.StatusCode == HttpStatusCode.Forbidden)) return list;
                throw;
            }
            {
                foreach (Dictionary<string, object> e in Json.Arr(evs, "items"))
                {
                    if (Json.Str(e, "status") == "cancelled") continue;
                    var type = Json.Str(e, "eventType");
                    if (type == "workingLocation") continue;
                    if (Declined(e)) continue;
                    var ev = new CalEvent
                    {
                        Title = Json.Str(e, "summary") ?? Bwui.Text.T("(제목 없음)", "(No title)"),
                        Link = Json.Str(e, "htmlLink"),
                        Uid = Json.Str(e, "iCalUID"),
                        Account = account,
                        Calendar = calName
                    };
                    var s = Json.Obj(e, "start"); var en = Json.Obj(e, "end");
                    if (s == null || en == null) continue;
                    if (Json.Str(s, "date") != null)
                    {
                        ev.AllDay = true;
                        ev.Start = DateTime.ParseExact(Json.Str(s, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                        ev.End = DateTime.ParseExact(Json.Str(en, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        ev.Start = DateTimeOffset.Parse(Json.Str(s, "dateTime"), CultureInfo.InvariantCulture).LocalDateTime;
                        ev.End = DateTimeOffset.Parse(Json.Str(en, "dateTime"), CultureInfo.InvariantCulture).LocalDateTime;
                    }
                    list.Add(ev);
                }
            }
            return list;
        }

        static bool Declined(Dictionary<string, object> e)
        {
            foreach (Dictionary<string, object> at in Json.Arr(e, "attendees"))
                if (Json.Bool(at, "self") && Json.Str(at, "responseStatus") == "declined") return true;
            return false;
        }

        static string Get(string token, string path)
        {
            var req = Http.Request(Api + path, "GET", token);
            return Http.Read(req);
        }

        static string Rfc3339(DateTime local)
        {
            return new DateTimeOffset(local).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        }
    }

    static class Http
    {
        public static HttpWebRequest Request(string url, string method, string bearer)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            // .NET Framework allows only 2 connections per host by default, which would serialize the
            // per-calendar requests again.
            if (ServicePointManager.DefaultConnectionLimit < 16) ServicePointManager.DefaultConnectionLimit = 16;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = "TodayCalendarWidget";
            req.Timeout = 20000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (bearer != null) req.Headers[HttpRequestHeader.Authorization] = "Bearer " + bearer;
            return req;
        }

        public static string Read(HttpWebRequest req)
        {
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return r.ReadToEnd();
        }

        public static string ErrorBody(WebException ex)
        {
            try
            {
                if (ex.Response == null) return "";
                using (var r = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
            }
            catch { return ""; }
        }
    }

    static class Json
    {
        public static Dictionary<string, object> Parse(string s)
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(s);
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
        }

        public static ArrayList Arr(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) && v is ArrayList ? (ArrayList)v : new ArrayList();
        }

        public static string Str(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) && v != null ? v.ToString() : null;
        }

        public static bool Bool(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) && v is bool && (bool)v;
        }

        public static double Num(Dictionary<string, object> d, string k, double def)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return def;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return def; }
        }
    }
}
