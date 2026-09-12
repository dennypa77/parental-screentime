using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ScreenTimeGuard
{
    /// <summary>
    /// Proses latar belakang yang menghitung pemakaian aplikasi, menegakkan batas
    /// harian, dan melayani perintah dari UI lewat named pipe.
    /// Idealnya dijalankan sebagai SYSTEM lewat Scheduled Task.
    /// </summary>
    public class Agent
    {
        const int TickMs = 5000;
        const int MaxElapsedSeconds = 30;       // jaga-jaga kalau komputer sleep
        const int ForegroundFreshSeconds = 25;
        const int AuthMaxFailures = 5;
        const int AuthLockoutSeconds = 60;

        readonly object _gate = new object();

        Settings _settings;
        UsageDay _usage;
        DateTime _settingsStamp = DateTime.MinValue;
        DateTime _lastTick = DateTime.MinValue;
        DateTime _lastUsageSave = DateTime.MinValue;

        readonly Dictionary<string, DateTime> _graceStart = new Dictionary<string, DateTime>();

        string _foregroundProcess = "";
        DateTime _foregroundAtUtc = DateTime.MinValue;

        int _authFailures;
        DateTime _authLockoutUntilUtc = DateTime.MinValue;

        DateTime _lastUpdateCheckUtc = DateTime.MinValue;
        string _updateAvailableVersion = "";
        volatile bool _updateChecking;

        IpcServer _server;
        volatile bool _stop;
        volatile bool _stopForUpdate;

        // ------------------------------------------------------------- lifecycle

        public void Run()
        {
            Paths.EnsureDir();
            LoadSettings(true);
            LoadUsage();

            Log.Write("=== Agent " + AppInfo.Version + " mulai (elevated=" + Util.IsElevated()
                      + ", user=" + Environment.UserName + ") ===");

            _server = new IpcServer(HandleRequest);
            _server.Start();

            _lastTick = DateTime.Now;
            while (!_stop)
            {
                try { Tick(); }
                catch (Exception ex) { Log.Write("Tick error: " + ex); }

                if (_stopForUpdate)
                {
                    SaveUsage();
                    Log.Write("Agent berhenti supaya berkas .exe bisa ditimpa pembaruan.");
                    break;
                }
                Thread.Sleep(TickMs);
            }
            Log.Write("=== Agent berhenti ===");
        }

        public void Stop() { _stop = true; }

        // ---------------------------------------------------------------- state

        void LoadSettings(bool createIfMissing)
        {
            try
            {
                if (File.Exists(Paths.Settings))
                {
                    _settings = Json.Read<Settings>(File.ReadAllText(Paths.Settings, Encoding.UTF8));
                    if (_settings.Apps == null) _settings.Apps = new List<AppLimit>();
                    _settingsStamp = File.GetLastWriteTimeUtc(Paths.Settings);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Gagal membaca settings.json: " + ex.Message + " (memakai bawaan)");
            }

            _settings = new Settings();
            if (createIfMissing) SaveSettings();
        }

        void SaveSettings()
        {
            try
            {
                Util.AtomicWriteAllText(Paths.Settings, Json.Write(_settings));
                _settingsStamp = File.GetLastWriteTimeUtc(Paths.Settings);
            }
            catch (Exception ex) { Log.Write("Gagal menyimpan settings.json: " + ex.Message); }
        }

        void ReloadSettingsIfChanged()
        {
            try
            {
                if (!File.Exists(Paths.Settings)) return;
                DateTime stamp = File.GetLastWriteTimeUtc(Paths.Settings);
                if (stamp == _settingsStamp) return;
                Log.Write("settings.json berubah di luar aplikasi, memuat ulang.");
                LoadSettings(false);
            }
            catch { }
        }

        void LoadUsage()
        {
            try
            {
                if (File.Exists(Paths.Usage))
                {
                    _usage = Json.Read<UsageDay>(File.ReadAllText(Paths.Usage, Encoding.UTF8));
                    if (_usage.Entries == null) _usage.Entries = new List<UsageEntry>();
                }
            }
            catch (Exception ex) { Log.Write("Gagal membaca usage.json: " + ex.Message); }

            if (_usage == null) _usage = new UsageDay();

            string today = Util.DayKey(Util.LogicalDay(DateTime.Now, _settings.ResetHour));
            if (_usage.Day != today)
            {
                ArchiveAndReset(today);
            }
            else
            {
                // Masa tenggang yang sudah terpakai hari ini tetap dianggap terpakai
                // supaya anak tidak bisa mengulang tenggang dengan restart aplikasi.
                for (int i = 0; i < _usage.Entries.Count; i++)
                    if (_usage.Entries[i].GraceUsed) _graceStart[_usage.Entries[i].Process] = DateTime.MinValue;
            }
        }

        void SaveUsage()
        {
            try
            {
                Util.AtomicWriteAllText(Paths.Usage, Json.Write(_usage));
                _lastUsageSave = DateTime.Now;
            }
            catch (Exception ex) { Log.Write("Gagal menyimpan usage.json: " + ex.Message); }
        }

        void ArchiveAndReset(string newDay)
        {
            if (!string.IsNullOrEmpty(_usage.Day) && _usage.Entries.Count > 0)
                AppendHistory(_usage);

            Log.Write("Reset harian: " + (_usage.Day == "" ? "(baru)" : _usage.Day) + " -> " + newDay);

            _usage = new UsageDay();
            _usage.Day = newDay;
            _graceStart.Clear();
            SaveUsage();
        }

        void AppendHistory(UsageDay day)
        {
            try
            {
                bool isNew = !File.Exists(Paths.History);
                StringBuilder sb = new StringBuilder();
                if (isNew) sb.AppendLine("tanggal,aplikasi,menit_terpakai,menit_bonus");
                for (int i = 0; i < day.Entries.Count; i++)
                {
                    UsageEntry e = day.Entries[i];
                    if (e.Seconds <= 0 && e.BonusMinutes <= 0) continue;
                    AppLimit app = _settings.Find(e.Process);
                    string name = app != null ? app.Name : e.Process;
                    sb.AppendLine(string.Join(",", new string[]
                    {
                        day.Day,
                        "\"" + name.Replace("\"", "'") + "\"",
                        (e.Seconds / 60).ToString(CultureInfo.InvariantCulture),
                        e.BonusMinutes.ToString(CultureInfo.InvariantCulture)
                    }));
                }
                sb.AppendLine(string.Join(",", new string[]
                {
                    day.Day, "\"(TOTAL)\"",
                    (day.TotalSeconds / 60).ToString(CultureInfo.InvariantCulture),
                    day.TotalBonusMinutes.ToString(CultureInfo.InvariantCulture)
                }));
                File.AppendAllText(Paths.History, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Log.Write("Gagal menulis history.csv: " + ex.Message); }
        }

        // ----------------------------------------------------------------- tick

        void Tick()
        {
            lock (_gate)
            {
                DateTime now = DateTime.Now;
                int elapsed = (int)Math.Round((now - _lastTick).TotalSeconds);
                if (elapsed < 0) elapsed = 0;
                if (elapsed > MaxElapsedSeconds) elapsed = MaxElapsedSeconds;
                _lastTick = now;

                ReloadSettingsIfChanged();

                string today = Util.DayKey(Util.LogicalDay(now, _settings.ResetHour));
                if (_usage.Day != today) ArchiveAndReset(today);

                bool weekend = Util.IsWeekend(Util.LogicalDay(now, _settings.ResetHour));
                bool paused = IsPaused(now);
                bool bedtime = _settings.BedtimeEnabled
                               && Util.InBedtime(now, _settings.BedtimeStart, _settings.BedtimeEnd);

                Dictionary<string, List<Process>> running = SnapshotProcesses();
                try
                {
                    CountUsage(running, elapsed, weekend, paused);

                    int totalLimitSec = TotalLimitSeconds(weekend);
                    bool totalExceeded = totalLimitSec >= 0 && _usage.TotalSeconds >= totalLimitSec;

                    Status status = new Status();
                    status.Day = _usage.Day;
                    status.GeneratedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    status.AgentElevated = Util.IsElevated();
                    status.Paused = paused;
                    status.PauseLeftSeconds = PauseLeftSeconds(now);
                    status.Bedtime = bedtime;
                    status.BedtimeText = _settings.BedtimeEnabled
                        ? _settings.BedtimeStart + " - " + _settings.BedtimeEnd : "";
                    status.IsWeekend = weekend;
                    status.TotalLimitSeconds = totalLimitSec;
                    status.TotalUsedSeconds = _usage.TotalSeconds;
                    status.TotalRemainingSeconds = totalLimitSec < 0
                        ? -1 : Math.Max(0, totalLimitSec - _usage.TotalSeconds);
                    status.ResetsAtText = string.Format("{0:00}:00", _settings.ResetHour);
                    status.WarnMinutes = _settings.WarnMinutes;
                    status.AgentVersion = AppInfo.Version;
                    status.UpdateAvailableVersion = _updateAvailableVersion;
                    status.OverlayEnabled = _settings.OverlayEnabled;
                    status.OverlayLocked = _settings.OverlayLocked;

                    for (int i = 0; i < _settings.Apps.Count; i++)
                        status.Apps.Add(EnforceApp(_settings.Apps[i], running, now, weekend,
                                                   paused, bedtime, totalExceeded));

                    WriteStatus(status);
                }
                finally
                {
                    DisposeSnapshot(running);
                }

                // Simpan pemakaian tiap 30 detik supaya tahan mati listrik mendadak.
                if ((now - _lastUsageSave).TotalSeconds >= 30) SaveUsage();

                MaybeAutoCheckUpdate();
            }
        }

        int TotalLimitSeconds(bool weekend)
        {
            int minutes = weekend ? _settings.TotalWeekendMinutes : _settings.TotalWeekdayMinutes;
            if (minutes < 0) return -1;
            return Math.Max(0, minutes + _usage.TotalBonusMinutes) * 60;
        }

        bool IsPaused(DateTime now)
        {
            return PauseLeftSeconds(now) > 0;
        }

        int PauseLeftSeconds(DateTime now)
        {
            if (string.IsNullOrEmpty(_usage.PausedUntilUtc)) return 0;
            DateTime until;
            if (!DateTime.TryParse(_usage.PausedUntilUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out until)) return 0;
            double left = (until - DateTime.UtcNow).TotalSeconds;
            return left > 0 ? (int)left : 0;
        }

        void CountUsage(Dictionary<string, List<Process>> running, int elapsed, bool weekend, bool paused)
        {
            if (elapsed <= 0 || paused) return;

            bool foregroundFresh = (DateTime.UtcNow - _foregroundAtUtc).TotalSeconds <= ForegroundFreshSeconds;
            string foreground = foregroundFresh ? _foregroundProcess : null;

            for (int i = 0; i < _settings.Apps.Count; i++)
            {
                AppLimit app = _settings.Apps[i];
                string p = Util.NormalizeProcessName(app.Process);
                if (p.Length == 0 || !running.ContainsKey(p)) continue;

                // Mode "active" hanya menghitung saat jendela aplikasi sedang di depan.
                // Kalau laporan foreground basi (UI ditutup anak), kembali ke mode "running"
                // supaya menutup UI tidak jadi celah untuk bermain gratis.
                if (app.CountMode == "active" && foreground != null && foreground != p) continue;

                UsageEntry e = _usage.Get(p);
                e.Seconds += elapsed;
                if (app.CountsTowardTotal) _usage.TotalSeconds += elapsed;
            }
        }

        StatusApp EnforceApp(AppLimit app, Dictionary<string, List<Process>> running, DateTime now,
                             bool weekend, bool paused, bool bedtime, bool totalExceeded)
        {
            string p = Util.NormalizeProcessName(app.Process);
            UsageEntry entry = _usage.Get(p);

            int limitMinutes = app.MinutesFor(weekend);
            int limitSec = limitMinutes < 0 ? -1 : Math.Max(0, limitMinutes + entry.BonusMinutes) * 60;

            StatusApp s = new StatusApp();
            s.Name = string.IsNullOrEmpty(app.Name) ? p : app.Name;
            s.Process = p;
            s.Enabled = app.Enabled;
            s.LimitSeconds = limitSec;
            s.UsedSeconds = entry.Seconds;
            s.BonusMinutes = entry.BonusMinutes;
            s.RemainingSeconds = limitSec < 0 ? -1 : Math.Max(0, limitSec - entry.Seconds);
            s.Running = running.ContainsKey(p);

            string reason = null;
            if (paused) reason = null;
            else if (!app.Enabled) reason = null;
            else if (bedtime) reason = "Jam tidur (" + _settings.BedtimeStart + " - " + _settings.BedtimeEnd + ")";
            else if (limitSec == 0) reason = "Aplikasi ini sedang tidak diizinkan";
            else if (limitSec > 0 && entry.Seconds >= limitSec) reason = "Waktu harian sudah habis";
            else if (totalExceeded && app.CountsTowardTotal) reason = "Total waktu layar hari ini sudah habis";

            s.Blocked = reason != null;
            s.BlockReason = reason == null ? "" : reason;

            if (!s.Blocked)
            {
                _graceStart.Remove(p);
                return s;
            }

            if (!s.Running) return s;

            // Sudah pernah melewati masa tenggang hari ini -> langsung ditutup.
            if (entry.GraceUsed)
            {
                CloseApp(running[p], p, reason, true);
                return s;
            }

            DateTime started;
            if (!_graceStart.TryGetValue(p, out started))
            {
                started = now;
                _graceStart[p] = started;
                Log.Write("Masa tenggang dimulai untuk " + p + " (" + reason + "), "
                          + _settings.GraceSeconds + " detik.");
            }

            int left = _settings.GraceSeconds - (int)(now - started).TotalSeconds;
            if (left > 0)
            {
                s.GraceLeftSeconds = left;
                return s;
            }

            entry.GraceUsed = true;
            s.GraceLeftSeconds = 0;
            CloseApp(running[p], p, reason, false);
            SaveUsage();
            return s;
        }

        void CloseApp(List<Process> processes, string name, string reason, bool immediate)
        {
            for (int i = 0; i < processes.Count; i++)
            {
                Process proc = processes[i];
                try
                {
                    if (proc.HasExited) continue;

                    if (!immediate)
                    {
                        try
                        {
                            if (proc.MainWindowHandle != IntPtr.Zero) proc.CloseMainWindow();
                            proc.WaitForExit(2500);
                        }
                        catch { }
                    }

                    if (!proc.HasExited)
                    {
                        proc.Kill();
                        proc.WaitForExit(2000);
                    }
                    Log.Write("Menutup " + name + " (pid " + proc.Id + ") - " + reason);
                }
                catch (Exception ex)
                {
                    Log.Write("Gagal menutup " + name + ": " + ex.Message);
                }
            }
        }

        static Dictionary<string, List<Process>> SnapshotProcesses()
        {
            Dictionary<string, List<Process>> map = new Dictionary<string, List<Process>>();
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch (Exception ex) { Log.Write("Gagal membaca daftar proses: " + ex.Message); return map; }

            for (int i = 0; i < all.Length; i++)
            {
                string n;
                try { n = all[i].ProcessName.ToLowerInvariant(); }
                catch { try { all[i].Dispose(); } catch { } continue; }

                List<Process> list;
                if (!map.TryGetValue(n, out list))
                {
                    list = new List<Process>();
                    map[n] = list;
                }
                list.Add(all[i]);
            }
            return map;
        }

        static void DisposeSnapshot(Dictionary<string, List<Process>> map)
        {
            foreach (KeyValuePair<string, List<Process>> kv in map)
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    try { kv.Value[i].Dispose(); }
                    catch { }
                }
        }

        void WriteStatus(Status status)
        {
            try { Util.AtomicWriteAllText(Paths.StatusFile, Json.Write(status)); }
            catch (Exception ex) { Log.Write("Gagal menulis status.json: " + ex.Message); }
        }

        // -------------------------------------------------------------- pembaruan

        /// <summary>
        /// Cek pembaruan sekali sehari di thread terpisah, supaya jaringan lambat
        /// tidak pernah menunda penegakan batas waktu.
        /// </summary>
        void MaybeAutoCheckUpdate()
        {
            if (!_settings.AutoCheckUpdate || _updateChecking) return;
            if ((DateTime.UtcNow - _lastUpdateCheckUtc).TotalHours < 24) return;

            _lastUpdateCheckUtc = DateTime.UtcNow;
            _updateChecking = true;

            Settings snapshot = _settings;
            Thread t = new Thread(delegate ()
            {
                try
                {
                    UpdateCheckResult r = Updater.Check(snapshot);
                    lock (_gate)
                        _updateAvailableVersion = r.UpdateAvailable ? r.LatestVersion : "";
                    if (r.UpdateAvailable)
                        Log.Write("Pembaruan tersedia: " + r.LatestVersion
                                  + " (terpasang " + AppInfo.Version + ").");
                }
                catch (Exception ex) { Log.Write("Cek pembaruan gagal: " + ex.Message); }
                finally { _updateChecking = false; }
            });
            t.IsBackground = true;
            t.Name = "update-check";
            t.Start();
        }

        // -------------------------------------------------------------- perintah

        IpcResponse Ok(string payload)
        {
            IpcResponse r = new IpcResponse();
            r.Ok = true;
            r.Payload = payload == null ? "" : payload;
            return r;
        }

        IpcResponse Fail(string error)
        {
            IpcResponse r = new IpcResponse();
            r.Ok = false;
            r.Error = error;
            return r;
        }

        bool Authorize(IpcRequest req, out IpcResponse failure)
        {
            failure = null;

            if (DateTime.UtcNow < _authLockoutUntilUtc)
            {
                int wait = (int)(_authLockoutUntilUtc - DateTime.UtcNow).TotalSeconds + 1;
                failure = Fail("Terlalu banyak percobaan. Coba lagi dalam " + wait + " detik.");
                return false;
            }

            if (string.IsNullOrEmpty(_settings.PasswordHash))
            {
                failure = Fail("Password induk belum diatur. Jalankan install.ps1 atau "
                               + "ScreenTimeGuard.exe --setup sebagai Administrator.");
                return false;
            }

            if (PasswordHash.Verify(req.Password, _settings.PasswordHash, _settings.PasswordSalt))
            {
                _authFailures = 0;
                return true;
            }

            _authFailures++;
            Log.Write("Password salah (percobaan ke-" + _authFailures + ").");
            if (_authFailures >= AuthMaxFailures)
            {
                _authFailures = 0;
                _authLockoutUntilUtc = DateTime.UtcNow.AddSeconds(AuthLockoutSeconds);
                Log.Write("Panel orang tua dikunci " + AuthLockoutSeconds + " detik.");
            }
            failure = Fail("Password salah.");
            return false;
        }

        IpcResponse HandleRequest(IpcRequest req)
        {
            string cmd = (req.Command == null ? "" : req.Command).ToUpperInvariant();

            // Perintah tanpa otorisasi.
            if (cmd == "PING") return Ok("ok");
            if (cmd == "FOREGROUND")
            {
                lock (_gate)
                {
                    _foregroundProcess = Util.NormalizeProcessName(req.Arg1);
                    _foregroundAtUtc = DateTime.UtcNow;
                }
                return Ok("");
            }
            if (cmd == "HASPASSWORD")
            {
                lock (_gate) { return Ok(string.IsNullOrEmpty(_settings.PasswordHash) ? "no" : "yes"); }
            }

            // Perintah pembaruan memakai jaringan. Otorisasinya di dalam kunci, tetapi
            // unduhannya di luar, supaya jaringan lambat tidak menunda penegakan batas waktu.
            if (cmd == "CHECKUPDATE" || cmd == "APPLYUPDATE")
            {
                Settings snapshot;
                lock (_gate)
                {
                    IpcResponse updateFailure;
                    if (!Authorize(req, out updateFailure)) return updateFailure;
                    snapshot = _settings;
                }

                try
                {
                    if (cmd == "CHECKUPDATE")
                    {
                        UpdateCheckResult result = Updater.Check(snapshot);
                        lock (_gate)
                        {
                            _updateAvailableVersion = result.UpdateAvailable ? result.LatestVersion : "";
                            _lastUpdateCheckUtc = DateTime.UtcNow;
                        }
                        return Ok(Json.Write(result));
                    }

                    string installing = Updater.DownloadAndLaunchInstaller(snapshot);
                    _stopForUpdate = true;
                    return Ok(installing);
                }
                catch (Exception ex)
                {
                    Log.Write("Pembaruan gagal: " + ex.Message);
                    return Fail(ex.Message);
                }
            }

            lock (_gate)
            {
                IpcResponse failure;
                if (!Authorize(req, out failure)) return failure;

                switch (cmd)
                {
                    case "VERIFY":
                        return Ok("ok");

                    case "GETSETTINGS":
                        {
                            Settings copy = Json.Read<Settings>(Json.Write(_settings));
                            copy.PasswordHash = "";
                            copy.PasswordSalt = "";
                            return Ok(Json.Write(copy));
                        }

                    case "SETSETTINGS":
                        return ApplySettings(req.Payload);

                    case "SETPASSWORD":
                        {
                            if (string.IsNullOrEmpty(req.Arg1) || req.Arg1.Length < 4)
                                return Fail("Password baru minimal 4 karakter.");
                            string h, s;
                            PasswordHash.Create(req.Arg1, out h, out s);
                            _settings.PasswordHash = h;
                            _settings.PasswordSalt = s;
                            SaveSettings();
                            Log.Write("Password induk diganti.");
                            return Ok("");
                        }

                    case "GETUSAGE":
                        return Ok(Json.Write(_usage));

                    case "BONUS":
                        return ApplyBonus(req.Arg1, req.Arg2);

                    case "RESETUSAGE":
                        return ApplyResetUsage(req.Arg1);

                    case "PAUSE":
                        return ApplyPause(req.Arg1);

                    case "GETHISTORY":
                        {
                            if (!File.Exists(Paths.History)) return Ok("");
                            return Ok(File.ReadAllText(Paths.History, Encoding.UTF8));
                        }

                    default:
                        return Fail("Perintah tidak dikenal: " + cmd);
                }
            }
        }

        IpcResponse ApplySettings(string payload)
        {
            Settings incoming;
            try { incoming = Json.Read<Settings>(payload); }
            catch (Exception ex) { return Fail("Data pengaturan rusak: " + ex.Message); }

            if (incoming.Apps == null) incoming.Apps = new List<AppLimit>();
            if (incoming.ResetHour < 0 || incoming.ResetHour > 23) return Fail("Jam reset harus 0-23.");
            if (incoming.GraceSeconds < 0) incoming.GraceSeconds = 0;
            if (incoming.GraceSeconds > 900) incoming.GraceSeconds = 900;

            if (incoming.BedtimeEnabled)
            {
                TimeSpan tmp;
                if (!Util.TryParseHm(incoming.BedtimeStart, out tmp)
                    || !Util.TryParseHm(incoming.BedtimeEnd, out tmp))
                    return Fail("Format jam tidur harus HH:mm, contoh 21:00.");
            }

            if (string.IsNullOrEmpty(incoming.UpdateUrl)) incoming.UpdateUrl = AppInfo.DefaultUpdateUrl;
            Uri updateUri;
            if (!Uri.TryCreate(incoming.UpdateUrl, UriKind.Absolute, out updateUri)
                || updateUri.Scheme != Uri.UriSchemeHttps)
                return Fail("Alamat pembaruan harus berupa URL https.");
            if (incoming.UpdatePublicKey == null) incoming.UpdatePublicKey = "";

            List<AppLimit> clean = new List<AppLimit>();
            for (int i = 0; i < incoming.Apps.Count; i++)
            {
                AppLimit a = incoming.Apps[i];
                a.Process = Util.NormalizeProcessName(a.Process);
                if (a.Process.Length == 0) continue;
                if (Util.IsProtectedProcess(a.Process))
                    return Fail("Proses \"" + a.Process + "\" adalah bagian dari Windows dan tidak boleh dibatasi.");
                if (string.IsNullOrEmpty(a.Name)) a.Name = a.Process;
                if (a.CountMode != "active") a.CountMode = "running";
                if (a.WeekdayMinutes < -1) a.WeekdayMinutes = -1;
                if (a.WeekendMinutes < -1) a.WeekendMinutes = -1;

                bool duplicate = false;
                for (int j = 0; j < clean.Count; j++)
                    if (clean[j].Process == a.Process) duplicate = true;
                if (!duplicate) clean.Add(a);
            }

            // Password tidak pernah dikirim lewat kabel; pertahankan yang tersimpan.
            incoming.Apps = clean;
            incoming.PasswordHash = _settings.PasswordHash;
            incoming.PasswordSalt = _settings.PasswordSalt;
            incoming.Version = 1;

            _settings = incoming;
            SaveSettings();
            Log.Write("Pengaturan diperbarui (" + clean.Count + " aplikasi).");
            return Ok("");
        }

        IpcResponse ApplyBonus(string target, string minutesText)
        {
            int minutes;
            if (!int.TryParse(minutesText, out minutes)) return Fail("Jumlah menit tidak valid.");
            if (minutes == 0) return Ok("");
            if (minutes < -600 || minutes > 600) return Fail("Bonus harus antara -600 dan 600 menit.");

            if (string.Equals(target, "TOTAL", StringComparison.OrdinalIgnoreCase))
            {
                _usage.TotalBonusMinutes += minutes;
                Log.Write("Bonus total " + minutes + " menit.");
            }
            else
            {
                string p = Util.NormalizeProcessName(target);
                if (_settings.Find(p) == null) return Fail("Aplikasi tidak ada dalam daftar.");
                UsageEntry e = _usage.Get(p);
                e.BonusMinutes += minutes;
                // Bonus baru berarti anak berhak atas masa tenggang lagi nanti.
                if (minutes > 0)
                {
                    e.GraceUsed = false;
                    _graceStart.Remove(p);
                }
                Log.Write("Bonus " + minutes + " menit untuk " + p + ".");
            }
            SaveUsage();
            return Ok("");
        }

        IpcResponse ApplyResetUsage(string target)
        {
            if (string.Equals(target, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                _usage.Entries.Clear();
                _usage.TotalSeconds = 0;
                _graceStart.Clear();
                Log.Write("Pemakaian hari ini direset (semua aplikasi).");
            }
            else
            {
                string p = Util.NormalizeProcessName(target);
                UsageEntry e = _usage.Get(p);
                if (_settings.Find(p) != null && _settings.Find(p).CountsTowardTotal)
                    _usage.TotalSeconds = Math.Max(0, _usage.TotalSeconds - e.Seconds);
                e.Seconds = 0;
                e.GraceUsed = false;
                _graceStart.Remove(p);
                Log.Write("Pemakaian " + p + " direset.");
            }
            SaveUsage();
            return Ok("");
        }

        IpcResponse ApplyPause(string minutesText)
        {
            int minutes;
            if (!int.TryParse(minutesText, out minutes)) return Fail("Jumlah menit tidak valid.");
            if (minutes <= 0)
            {
                _usage.PausedUntilUtc = "";
                Log.Write("Jeda pengawasan dibatalkan.");
            }
            else
            {
                if (minutes > 720) minutes = 720;
                _usage.PausedUntilUtc = DateTime.UtcNow.AddMinutes(minutes)
                    .ToString("o", CultureInfo.InvariantCulture);
                Log.Write("Pengawasan dijeda " + minutes + " menit.");
            }
            SaveUsage();
            return Ok("");
        }
    }
}
