using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ScreenTimeGuard
{
    /// <summary>Yang disediakan agent untuk panel web.</summary>
    public interface IPanelHost
    {
        bool TryGetPasswordMaterial(out string hashBase64, out string saltBase64);

        /// <summary>Menjalankan perintah yang sudah dipastikan sah oleh panel.</summary>
        IpcResponse ExecuteAuthorized(IpcRequest request);
    }

    /// <summary>
    /// Panel orang tua lewat jaringan: dibuka dari komputer atau HP sendiri
    /// di jaringan rumah yang sama.
    ///
    /// Masuknya memakai tanya-jawab kriptografis: password TIDAK pernah dikirim
    /// lewat jaringan. Peramban menghitung PBKDF2 atas password, lalu menjawab
    /// angka acak dari server dengan HMAC. Jadi penyadap di jaringan tidak
    /// mendapat password, dan jawabannya tidak bisa dipakai ulang.
    /// </summary>
    public partial class WebPanel
    {
        const int SessionHours = 6;
        const int NonceSeconds = 120;
        const int MaxBodyBytes = 512 * 1024;

        readonly IPanelHost _host;
        readonly object _gate = new object();

        HttpListener _listener;
        Thread _thread;
        volatile bool _stop;
        int _port;
        bool _lanOnly;
        string _boundPrefix = "";

        readonly Dictionary<string, DateTime> _nonces = new Dictionary<string, DateTime>();
        readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>();
        readonly Dictionary<string, Attempts> _attempts = new Dictionary<string, Attempts>();

        class Session
        {
            public DateTime ExpiresUtc;
            public string Client;
        }

        class Attempts
        {
            public int Count;
            public DateTime LockedUntilUtc;
        }

        /// <summary>Dimatikan saat mode simulasi, supaya pengujian tidak mengubah
        /// aturan Windows Firewall yang sungguhan.</summary>
        public bool ManageFirewall = true;

        public WebPanel(IPanelHost host) { _host = host; }

        public bool Running { get { return _listener != null && _listener.IsListening; } }

        public string Url
        {
            get
            {
                if (!Running) return "";
                string ip = LocalAddress();
                return "http://" + (ip.Length > 0 ? ip : "localhost") + ":" + _port + "/";
            }
        }

        // ------------------------------------------------------------ daur hidup

        public void Start(int port, bool lanOnly)
        {
            Stop();
            _port = port;
            _lanOnly = lanOnly;
            _stop = false;

            // "+" mencakup semua kartu jaringan dan butuh hak tinggi (agent = SYSTEM).
            // Saat menguji tanpa hak itu, mundur ke localhost saja.
            string[] candidates = new string[]
            {
                "http://+:" + port + "/",
                "http://localhost:" + port + "/"
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    HttpListener listener = new HttpListener();
                    listener.Prefixes.Add(candidates[i]);
                    listener.Start();
                    _listener = listener;
                    _boundPrefix = candidates[i];
                    break;
                }
                catch (Exception ex)
                {
                    Log.Write("Panel jarak jauh gagal mengikat " + candidates[i] + ": " + ex.Message);
                }
            }

            if (_listener == null)
            {
                Log.Write("Panel jarak jauh tidak bisa dijalankan.");
                return;
            }

            if (ManageFirewall) EnsureFirewallRule(true);

            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "web-panel";
            _thread.Start();
            Log.Write("Panel jarak jauh aktif di " + _boundPrefix
                      + " (alamat untuk orang tua: " + Url + ")");
        }

        const string FirewallRuleName = "ScreenTimeGuard Panel Orang Tua";

        /// <summary>
        /// Memastikan Windows Firewall mengizinkan panel masuk. Dikerjakan agent sendiri,
        /// bukan hanya oleh installer, supaya pemasangan yang diperbarui lewat tombol
        /// Pembaruan juga mendapat izinnya tanpa perlu menjalankan install.ps1 lagi.
        /// </summary>
        static void EnsureFirewallRule(bool allow)
        {
            try
            {
                string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                string q = "\"";
                Netsh("advfirewall firewall delete rule name=" + q + FirewallRuleName + q);

                if (!allow)
                {
                    Log.Write("Izin firewall panel jarak jauh dicabut.");
                    return;
                }

                // Hanya jaringan rumah / kantor (private, domain), bukan jaringan publik.
                int code = Netsh("advfirewall firewall add rule name=" + q + FirewallRuleName + q
                                 + " dir=in action=allow program=" + q + exe + q
                                 + " enable=yes profile=private,domain protocol=tcp");
                Log.Write(code == 0
                    ? "Izin Windows Firewall untuk panel jarak jauh siap."
                    : "Gagal membuat izin firewall (kode " + code + "). Panel mungkin tidak bisa "
                      + "dibuka dari komputer lain; jalankan install.ps1 sebagai Administrator.");
            }
            catch (Exception ex)
            {
                Log.Write("Gagal mengatur izin firewall: " + ex.Message);
            }
        }

        static int Netsh(string arguments)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi =
                    new System.Diagnostics.ProcessStartInfo("netsh.exe", arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        public void Stop()
        {
            _stop = true;
            HttpListener listener = _listener;
            _listener = null;
            if (listener != null)
            {
                try { listener.Stop(); }
                catch { }
                try { listener.Close(); }
                catch { }
                Log.Write("Panel jarak jauh dihentikan.");
            }
            lock (_gate) { _sessions.Clear(); _nonces.Clear(); }
        }

        void Loop()
        {
            while (!_stop && _listener != null)
            {
                try
                {
                    HttpListenerContext context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(delegate { Serve(context); });
                }
                catch (Exception ex)
                {
                    if (!_stop) Log.Write("Panel jarak jauh: " + ex.Message);
                    Thread.Sleep(300);
                }
            }
        }

        // ------------------------------------------------------------- penyajian

        void Serve(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
                if (path.Length == 0) path = "/";

                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
                ctx.Response.Headers["Cache-Control"] = "no-store";

                if (_lanOnly && !IsLocalNetwork(ctx.Request.RemoteEndPoint))
                {
                    Log.Write("Panel jarak jauh menolak alamat luar: " + ctx.Request.RemoteEndPoint);
                    Send(ctx, 403, "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("Hanya bisa diakses dari jaringan lokal."));
                    return;
                }

                switch (path)
                {
                    case "/":
                        Send(ctx, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Html));
                        return;
                    case "/api/challenge":
                        HandleChallenge(ctx);
                        return;
                    case "/api/login":
                        HandleLogin(ctx);
                        return;
                    case "/api/logout":
                        HandleLogout(ctx);
                        return;
                    case "/api/state":
                        HandleState(ctx);
                        return;
                    case "/api/action":
                        HandleAction(ctx);
                        return;
                    default:
                        Send(ctx, 404, "text/plain; charset=utf-8",
                            Encoding.UTF8.GetBytes("Tidak ditemukan."));
                        return;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Panel jarak jauh galat: " + ex.Message);
                try { Send(ctx, 500, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Galat.")); }
                catch { }
            }
        }

        static void Send(HttpListenerContext ctx, int status, string contentType, byte[] body)
        {
            try
            {
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = contentType;
                ctx.Response.ContentLength64 = body.Length;
                ctx.Response.OutputStream.Write(body, 0, body.Length);
            }
            catch { }
            finally { try { ctx.Response.Close(); } catch { } }
        }

        static void SendJson(HttpListenerContext ctx, int status, string json)
        {
            Send(ctx, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
        }

        static string ReadBody(HttpListenerContext ctx)
        {
            if (ctx.Request.ContentLength64 > MaxBodyBytes) return "";
            using (StreamReader r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                return r.ReadToEnd();
        }

        // ------------------------------------------------------------------ masuk

        void HandleChallenge(HttpListenerContext ctx)
        {
            string hash, salt;
            if (!_host.TryGetPasswordMaterial(out hash, out salt))
            {
                SendJson(ctx, 503, "{\"error\":\"Password orang tua belum diatur di komputer anak.\"}");
                return;
            }

            string client = ClientKey(ctx);
            int wait = LockoutSecondsLeft(client);
            if (wait > 0)
            {
                SendJson(ctx, 429, "{\"error\":\"Terlalu banyak percobaan. Tunggu "
                                   + wait + " detik.\"}");
                return;
            }

            byte[] nonce = new byte[16];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider()) rng.GetBytes(nonce);
            string nonce64 = Convert.ToBase64String(nonce);

            lock (_gate)
            {
                PruneNonces();
                _nonces[nonce64] = DateTime.UtcNow.AddSeconds(NonceSeconds);
            }

            SendJson(ctx, 200, "{\"salt\":" + JsonString(salt)
                               + ",\"nonce\":" + JsonString(nonce64)
                               + ",\"iterations\":120000}");
        }

        void HandleLogin(HttpListenerContext ctx)
        {
            string client = ClientKey(ctx);
            int wait = LockoutSecondsLeft(client);
            if (wait > 0)
            {
                SendJson(ctx, 429, "{\"error\":\"Terlalu banyak percobaan. Tunggu " + wait + " detik.\"}");
                return;
            }

            string body = ReadBody(ctx);
            string nonce64 = ExtractField(body, "nonce");
            string proof64 = ExtractField(body, "proof");

            if (nonce64.Length == 0 || proof64.Length == 0)
            {
                SendJson(ctx, 400, "{\"error\":\"Permintaan tidak lengkap.\"}");
                return;
            }

            // Angka acak hanya berlaku sekali, supaya jawaban tidak bisa dipakai ulang.
            lock (_gate)
            {
                DateTime expires;
                if (!_nonces.TryGetValue(nonce64, out expires) || DateTime.UtcNow > expires)
                {
                    _nonces.Remove(nonce64);
                    SendJson(ctx, 400, "{\"error\":\"Sesi masuk kedaluwarsa. Coba lagi.\"}");
                    return;
                }
                _nonces.Remove(nonce64);
            }

            string hash, salt;
            if (!_host.TryGetPasswordMaterial(out hash, out salt))
            {
                SendJson(ctx, 503, "{\"error\":\"Password orang tua belum diatur.\"}");
                return;
            }

            bool ok = false;
            try
            {
                byte[] key = Convert.FromBase64String(hash);
                byte[] nonce = Convert.FromBase64String(nonce64);
                byte[] given = Convert.FromBase64String(proof64);
                using (HMACSHA256 mac = new HMACSHA256(key))
                {
                    byte[] expect = mac.ComputeHash(nonce);
                    if (expect.Length == given.Length)
                    {
                        int diff = 0;
                        for (int i = 0; i < expect.Length; i++) diff |= expect[i] ^ given[i];
                        ok = diff == 0;
                    }
                }
            }
            catch { ok = false; }

            if (!ok)
            {
                RegisterFailure(client);
                Log.Write("Panel jarak jauh: password salah dari " + client + ".");
                SendJson(ctx, 401, "{\"error\":\"Password salah.\"}");
                return;
            }

            RegisterSuccess(client);

            byte[] tokenBytes = new byte[32];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider()) rng.GetBytes(tokenBytes);
            string token = Convert.ToBase64String(tokenBytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');

            lock (_gate)
            {
                PruneSessions();
                Session s = new Session();
                s.ExpiresUtc = DateTime.UtcNow.AddHours(SessionHours);
                s.Client = client;
                _sessions[token] = s;
            }

            Log.Write("Panel jarak jauh: orang tua masuk dari " + client + ".");
            ctx.Response.Headers["Set-Cookie"] = "stg_session=" + token
                + "; Path=/; Max-Age=" + (SessionHours * 3600) + "; HttpOnly; SameSite=Strict";
            SendJson(ctx, 200, "{\"ok\":true}");
        }

        void PruneSessions()
        {
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, Session> kv in _sessions)
                if (DateTime.UtcNow > kv.Value.ExpiresUtc) dead.Add(kv.Key);
            for (int i = 0; i < dead.Count; i++) _sessions.Remove(dead[i]);
        }

        void HandleLogout(HttpListenerContext ctx)
        {
            string token = CookieValue(ctx, "stg_session");
            if (token != null) lock (_gate) _sessions.Remove(token);
            ctx.Response.Headers["Set-Cookie"] = "stg_session=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict";
            SendJson(ctx, 200, "{\"ok\":true}");
        }

        // ------------------------------------------------------------------ data

        void HandleState(HttpListenerContext ctx)
        {
            if (!Authorized(ctx)) { SendJson(ctx, 401, "{\"error\":\"Belum masuk.\"}"); return; }

            IpcRequest req = new IpcRequest();
            req.Command = "GETSETTINGS";
            IpcResponse settings = _host.ExecuteAuthorized(req);

            Status status = StatusReader.Read();
            string statusJson = status == null ? "null" : Json.Write(status);

            SendJson(ctx, 200, "{\"status\":" + statusJson
                               + ",\"settings\":" + (settings.Ok ? settings.Payload : "null")
                               + ",\"fresh\":" + (StatusReader.IsFresh(status) ? "true" : "false") + "}");
        }

        void HandleAction(HttpListenerContext ctx)
        {
            if (!Authorized(ctx)) { SendJson(ctx, 401, "{\"error\":\"Belum masuk.\"}"); return; }

            string body = ReadBody(ctx);
            IpcRequest req;
            try { req = Json.Read<IpcRequest>(body); }
            catch (Exception ex)
            {
                SendJson(ctx, 400, "{\"error\":" + JsonString("Permintaan tidak valid: " + ex.Message) + "}");
                return;
            }

            if (req == null || string.IsNullOrEmpty(req.Command))
            {
                SendJson(ctx, 400, "{\"error\":\"Perintah kosong.\"}");
                return;
            }

            IpcResponse response = _host.ExecuteAuthorized(req);
            SendJson(ctx, 200, "{\"ok\":" + (response.Ok ? "true" : "false")
                               + ",\"error\":" + JsonString(response.Error)
                               + ",\"payload\":" + JsonString(response.Payload) + "}");
        }

        // ------------------------------------------------------------- keamanan

        bool Authorized(HttpListenerContext ctx)
        {
            string token = CookieValue(ctx, "stg_session");
            if (string.IsNullOrEmpty(token)) return false;
            lock (_gate)
            {
                Session s;
                if (!_sessions.TryGetValue(token, out s)) return false;
                if (DateTime.UtcNow > s.ExpiresUtc) { _sessions.Remove(token); return false; }
                if (s.Client != ClientKey(ctx)) return false;
                return true;
            }
        }

        static string CookieValue(HttpListenerContext ctx, string name)
        {
            try
            {
                Cookie c = ctx.Request.Cookies[name];
                return c == null ? null : c.Value;
            }
            catch { return null; }
        }

        static string ClientKey(HttpListenerContext ctx)
        {
            try { return ctx.Request.RemoteEndPoint.Address.ToString(); }
            catch { return "?"; }
        }

        int LockoutSecondsLeft(string client)
        {
            lock (_gate)
            {
                Attempts a;
                if (!_attempts.TryGetValue(client, out a)) return 0;
                if (DateTime.UtcNow >= a.LockedUntilUtc) return 0;
                return (int)(a.LockedUntilUtc - DateTime.UtcNow).TotalSeconds + 1;
            }
        }

        void RegisterFailure(string client)
        {
            lock (_gate)
            {
                Attempts a;
                if (!_attempts.TryGetValue(client, out a)) { a = new Attempts(); _attempts[client] = a; }
                a.Count++;
                if (a.Count >= 5)
                {
                    a.Count = 0;
                    a.LockedUntilUtc = DateTime.UtcNow.AddSeconds(60);
                    Log.Write("Panel jarak jauh dikunci 60 detik untuk " + client + ".");
                }
            }
        }

        void RegisterSuccess(string client)
        {
            lock (_gate) _attempts.Remove(client);
        }

        void PruneNonces()
        {
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, DateTime> kv in _nonces)
                if (DateTime.UtcNow > kv.Value) dead.Add(kv.Key);
            for (int i = 0; i < dead.Count; i++) _nonces.Remove(dead[i]);
        }

        static bool IsLocalNetwork(IPEndPoint endpoint)
        {
            if (endpoint == null) return false;
            IPAddress a = endpoint.Address;
            if (IPAddress.IsLoopback(a)) return true;

            if (a.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal) return true;
                if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
                else return false;
            }

            byte[] b = a.GetAddressBytes();
            if (b.Length != 4) return false;
            if (b[0] == 10) return true;                              // 10.0.0.0/8
            if (b[0] == 192 && b[1] == 168) return true;              // 192.168.0.0/16
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
            if (b[0] == 169 && b[1] == 254) return true;              // link-local
            return false;
        }

        /// <summary>Penanda kartu jaringan maya / VPN, yang alamatnya tidak berguna
        /// untuk diketik orang tua di peramban.</summary>
        static readonly string[] VirtualMarkers = new string[]
        {
            "warp", "cloudflare", "tailscale", "zerotier", "wireguard", "openvpn", "vpn",
            "virtual", "vmware", "virtualbox", "hyper-v", "loopback", "pseudo", "teredo",
            "bluetooth", "docker", "wsl", "npcap", "radmin", "hamachi"
        };

        static bool LooksVirtual(NetworkInterface ni)
        {
            string text = ((ni.Name == null ? "" : ni.Name) + " "
                           + (ni.Description == null ? "" : ni.Description)).ToLowerInvariant();
            for (int i = 0; i < VirtualMarkers.Length; i++)
                if (text.IndexOf(VirtualMarkers[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static bool HasDefaultGateway(IPInterfaceProperties props)
        {
            try
            {
                foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
                {
                    if (g.Address == null) continue;
                    if (g.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (g.Address.Equals(IPAddress.Any)) continue;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Semua alamat yang masuk akal diketik orang tua, diurutkan dari yang paling
        /// mungkin benar. Kartu jaringan yang punya gerbang bawaan (Wi-Fi atau Ethernet
        /// sungguhan) diutamakan; VPN seperti Cloudflare WARP atau Tailscale dipinggirkan,
        /// karena alamatnya tidak bisa dibuka dari perangkat lain di rumah.
        /// </summary>
        public static List<string> LocalAddresses()
        {
            List<string> best = new List<string>();
            List<string> rest = new List<string>();
            try
            {
                NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < all.Length; i++)
                {
                    NetworkInterface ni = all[i];
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Ppp) continue;

                    IPInterfaceProperties props = ni.GetIPProperties();
                    bool gateway = HasDefaultGateway(props);
                    bool virtualNic = LooksVirtual(ni);

                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;

                        byte[] b = ua.Address.GetAddressBytes();
                        if (b[0] == 169 && b[1] == 254) continue;   // alamat darurat: tidak terhubung
                        if (!IsLocalNetwork(new IPEndPoint(ua.Address, 0))) continue;

                        string text = ua.Address.ToString();
                        if (virtualNic) continue;
                        if (gateway) best.Add(text); else rest.Add(text);
                    }
                }
            }
            catch { }

            // 192.168.x adalah pola jaringan rumah paling umum, jadi didahulukan.
            best.Sort(delegate (string a, string c)
            {
                int ra = a.StartsWith("192.168.", StringComparison.Ordinal) ? 0 : 1;
                int rc = c.StartsWith("192.168.", StringComparison.Ordinal) ? 0 : 1;
                return ra != rc ? ra - rc : string.CompareOrdinal(a, c);
            });

            for (int i = 0; i < rest.Count; i++)
                if (!best.Contains(rest[i])) best.Add(rest[i]);
            return best;
        }

        public static string LocalAddress()
        {
            List<string> list = LocalAddresses();
            return list.Count > 0 ? list[0] : "";
        }

        /// <summary>Semua alamat yang bisa dicoba, untuk ditampilkan di panel komputer anak.</summary>
        public List<string> AllUrls()
        {
            List<string> urls = new List<string>();
            if (!Running) return urls;
            List<string> addrs = LocalAddresses();
            for (int i = 0; i < addrs.Count; i++) urls.Add("http://" + addrs[i] + ":" + _port + "/");
            return urls;
        }

        static string JsonString(string value)
        {
            if (value == null) return "\"\"";
            StringBuilder sb = new StringBuilder("\"");
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ' || c > '~') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>Pengambil nilai sederhana untuk JSON datar dari peramban.</summary>
        static string ExtractField(string json, string name)
        {
            if (string.IsNullOrEmpty(json)) return "";
            string key = "\"" + name + "\"";
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return "";
            i = json.IndexOf(':', i + key.Length);
            if (i < 0) return "";
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return "";
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < json.Length && json[i] != '"')
            {
                if (json[i] == '\\' && i + 1 < json.Length) i++;
                sb.Append(json[i]);
                i++;
            }
            return sb.ToString();
        }

    }
}
