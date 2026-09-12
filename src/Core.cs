using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace ScreenTimeGuard
{
    // ---------------------------------------------------------------- models

    [DataContract]
    public class AppLimit
    {
        [DataMember(Order = 1)] public string Name;
        [DataMember(Order = 2)] public string Process;          // huruf kecil, tanpa ".exe"
        [DataMember(Order = 3)] public int WeekdayMinutes;      // -1 = tanpa batas, 0 = dilarang total
        [DataMember(Order = 4)] public int WeekendMinutes;
        [DataMember(Order = 5)] public string CountMode;        // "running" | "active"
        [DataMember(Order = 6)] public bool Enabled;
        [DataMember(Order = 7)] public bool CountsTowardTotal;

        public AppLimit()
        {
            Name = "";
            Process = "";
            WeekdayMinutes = 60;
            WeekendMinutes = 120;
            CountMode = "running";
            Enabled = true;
            CountsTowardTotal = true;
        }

        public int MinutesFor(bool weekend)
        {
            return weekend ? WeekendMinutes : WeekdayMinutes;
        }
    }

    [DataContract]
    public class Settings
    {
        [DataMember(Order = 1)] public int Version;
        [DataMember(Order = 2)] public string PasswordHash;
        [DataMember(Order = 3)] public string PasswordSalt;
        [DataMember(Order = 4)] public int ResetHour;             // jam reset harian (0-23)
        [DataMember(Order = 5)] public int GraceSeconds;          // tenggang sebelum ditutup paksa
        [DataMember(Order = 6)] public string WarnMinutes;        // contoh: "15,5,1"
        [DataMember(Order = 7)] public int TotalWeekdayMinutes;   // -1 = tanpa batas total
        [DataMember(Order = 8)] public int TotalWeekendMinutes;
        [DataMember(Order = 9)] public bool BedtimeEnabled;
        [DataMember(Order = 10)] public string BedtimeStart;      // "21:00"
        [DataMember(Order = 11)] public string BedtimeEnd;        // "06:00"
        [DataMember(Order = 12)] public string UpdateUrl;         // manifest pembaruan (wajib https)
        [DataMember(Order = 13)] public bool AutoCheckUpdate;     // cek otomatis sekali sehari
        [DataMember(Order = 14)] public string UpdatePublicKey;   // opsional: kunci publik RSA (base64 XML)
        [DataMember(Order = 15)] public bool OverlayEnabled;      // penghitung melayang di layar anak
        [DataMember(Order = 16)] public bool OverlayLocked;       // anak tidak boleh menyembunyikannya
        [DataMember(Order = 20)] public List<AppLimit> Apps;

        public Settings()
        {
            Version = 1;
            PasswordHash = "";
            PasswordSalt = "";
            ResetHour = 4;
            GraceSeconds = 60;
            WarnMinutes = "15,5,1";
            TotalWeekdayMinutes = -1;
            TotalWeekendMinutes = -1;
            BedtimeEnabled = false;
            BedtimeStart = "21:00";
            BedtimeEnd = "06:00";
            UpdateUrl = AppInfo.DefaultUpdateUrl;
            AutoCheckUpdate = true;
            UpdatePublicKey = "";
            OverlayEnabled = true;
            OverlayLocked = false;
            Apps = new List<AppLimit>();
        }

        public int[] ParsedWarnMinutes()
        {
            List<int> list = new List<int>();
            if (!string.IsNullOrEmpty(WarnMinutes))
            {
                string[] parts = WarnMinutes.Split(',');
                for (int i = 0; i < parts.Length; i++)
                {
                    int v;
                    if (int.TryParse(parts[i].Trim(), out v) && v > 0 && !list.Contains(v)) list.Add(v);
                }
            }
            list.Sort();
            list.Reverse();
            return list.ToArray();
        }

        public AppLimit Find(string process)
        {
            if (process == null) return null;
            string p = Util.NormalizeProcessName(process);
            for (int i = 0; i < Apps.Count; i++)
                if (Util.NormalizeProcessName(Apps[i].Process) == p) return Apps[i];
            return null;
        }
    }

    [DataContract]
    public class UsageEntry
    {
        [DataMember(Order = 1)] public string Process;
        [DataMember(Order = 2)] public int Seconds;
        [DataMember(Order = 3)] public int BonusMinutes;
        [DataMember(Order = 4)] public bool GraceUsed;

        public UsageEntry() { Process = ""; }
    }

    [DataContract]
    public class UsageDay
    {
        [DataMember(Order = 1)] public string Day;               // hari logis, "2026-09-12"
        [DataMember(Order = 2)] public int TotalSeconds;
        [DataMember(Order = 3)] public int TotalBonusMinutes;
        [DataMember(Order = 4)] public List<UsageEntry> Entries;
        [DataMember(Order = 5)] public string PausedUntilUtc;    // "" = tidak dijeda

        public UsageDay()
        {
            Day = "";
            Entries = new List<UsageEntry>();
            PausedUntilUtc = "";
        }

        public UsageEntry Get(string process)
        {
            string p = Util.NormalizeProcessName(process);
            for (int i = 0; i < Entries.Count; i++)
                if (Entries[i].Process == p) return Entries[i];
            UsageEntry e = new UsageEntry();
            e.Process = p;
            Entries.Add(e);
            return e;
        }
    }

    [DataContract]
    public class StatusApp
    {
        [DataMember(Order = 1)] public string Name;
        [DataMember(Order = 2)] public string Process;
        [DataMember(Order = 3)] public int LimitSeconds;        // -1 = tanpa batas (sudah termasuk bonus)
        [DataMember(Order = 4)] public int UsedSeconds;
        [DataMember(Order = 5)] public int RemainingSeconds;    // -1 = tanpa batas
        [DataMember(Order = 6)] public int BonusMinutes;
        [DataMember(Order = 7)] public bool Running;
        [DataMember(Order = 8)] public bool Blocked;
        [DataMember(Order = 9)] public bool Enabled;
        [DataMember(Order = 10)] public string BlockReason;
        [DataMember(Order = 11)] public int GraceLeftSeconds;   // -1 = tidak dalam masa tenggang

        public StatusApp() { Name = ""; Process = ""; BlockReason = ""; GraceLeftSeconds = -1; }
    }

    [DataContract]
    public class Status
    {
        [DataMember(Order = 1)] public string Day;
        [DataMember(Order = 2)] public string GeneratedAtUtc;
        [DataMember(Order = 3)] public bool AgentElevated;
        [DataMember(Order = 4)] public bool Paused;
        [DataMember(Order = 5)] public int PauseLeftSeconds;
        [DataMember(Order = 6)] public bool Bedtime;
        [DataMember(Order = 7)] public string BedtimeText;
        [DataMember(Order = 8)] public int TotalLimitSeconds;
        [DataMember(Order = 9)] public int TotalUsedSeconds;
        [DataMember(Order = 10)] public int TotalRemainingSeconds;
        [DataMember(Order = 11)] public string ResetsAtText;
        [DataMember(Order = 12)] public bool IsWeekend;
        [DataMember(Order = 13)] public string WarnMinutes;
        [DataMember(Order = 14)] public string AgentVersion;
        [DataMember(Order = 15)] public string UpdateAvailableVersion;   // "" = tidak ada
        [DataMember(Order = 16)] public bool OverlayEnabled;
        [DataMember(Order = 17)] public bool OverlayLocked;
        [DataMember(Order = 20)] public List<StatusApp> Apps;

        public Status()
        {
            Day = "";
            GeneratedAtUtc = "";
            BedtimeText = "";
            ResetsAtText = "";
            WarnMinutes = "";
            AgentVersion = "";
            UpdateAvailableVersion = "";
            Apps = new List<StatusApp>();
        }
    }

    [DataContract]
    public class IpcRequest
    {
        [DataMember(Order = 1)] public string Command;
        [DataMember(Order = 2)] public string Password;
        [DataMember(Order = 3)] public string Arg1;
        [DataMember(Order = 4)] public string Arg2;
        [DataMember(Order = 5)] public string Payload;

        public IpcRequest() { Command = ""; Password = ""; Arg1 = ""; Arg2 = ""; Payload = ""; }
    }

    [DataContract]
    public class IpcResponse
    {
        [DataMember(Order = 1)] public bool Ok;
        [DataMember(Order = 2)] public string Error;
        [DataMember(Order = 3)] public string Payload;

        public IpcResponse() { Error = ""; Payload = ""; }
    }

    // ------------------------------------------------------------------ json

    public static class Json
    {
        public static string Write<T>(T obj)
        {
            DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(T));
            using (MemoryStream ms = new MemoryStream())
            {
                using (System.Xml.XmlDictionaryWriter w =
                    JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                {
                    ser.WriteObject(w, obj);
                    w.Flush();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static T Read<T>(string text)
        {
            DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(T));
            using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                return (T)ser.ReadObject(ms);
        }
    }

    // -------------------------------------------------------------- password

    public static class PasswordHash
    {
        const int Iterations = 120000;

        public static void Create(string password, out string hash, out string salt)
        {
            byte[] s = new byte[16];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider()) rng.GetBytes(s);
            salt = Convert.ToBase64String(s);
            hash = Derive(password, s);
        }

        static string Derive(string password, byte[] salt)
        {
            using (Rfc2898DeriveBytes k = new Rfc2898DeriveBytes(
                password == null ? "" : password, salt, Iterations, HashAlgorithmName.SHA256))
                return Convert.ToBase64String(k.GetBytes(32));
        }

        public static bool Verify(string password, string hash, string salt)
        {
            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt)) return false;
            byte[] s;
            try { s = Convert.FromBase64String(salt); }
            catch { return false; }
            string got = Derive(password, s);
            if (got.Length != hash.Length) return false;
            int diff = 0;
            for (int i = 0; i < got.Length; i++) diff |= got[i] ^ hash[i];
            return diff == 0;
        }
    }

    // ----------------------------------------------------------------- paths

    public static class Paths
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ScreenTimeGuard");

        public static string Settings { get { return Path.Combine(Dir, "settings.json"); } }
        public static string Usage { get { return Path.Combine(Dir, "usage.json"); } }
        public static string StatusFile { get { return Path.Combine(Dir, "status.json"); } }
        public static string History { get { return Path.Combine(Dir, "history.csv"); } }
        public static string LogFile { get { return Path.Combine(Dir, "log.txt"); } }

        public const string PipeName = "ScreenTimeGuard.Control";

        public static void EnsureDir()
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
        }
    }

    public static class Log
    {
        static readonly object Gate = new object();

        public static void Write(string message)
        {
            try
            {
                Paths.EnsureDir();
                lock (Gate)
                {
                    FileInfo fi = new FileInfo(Paths.LogFile);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        string old = Paths.LogFile + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(Paths.LogFile, old);
                    }
                    File.AppendAllText(Paths.LogFile,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        + "  " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // ----------------------------------------------------------------- utils

    public static class Util
    {
        // Proses inti Windows yang tidak boleh dijadikan target, supaya sistem tidak rusak.
        static readonly string[] ProtectedNames = new string[]
        {
            "system", "idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
            "svchost", "explorer", "dwm", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "runtimebroker",
            "searchhost", "searchui", "startmenuexperiencehost", "shellexperiencehost", "logonui",
            "userinit", "spoolsv", "conhost", "audiodg", "dllhost", "wudfhost", "memory compression",
            "screentimeguard", "msmpeng", "securityhealthservice", "securityhealthsystray", "sppsvc",
            "applicationframehost", "textinputhost", "systemsettings", "wmiprvse", "sedsvc"
        };

        public static bool IsProtectedProcess(string name)
        {
            string n = NormalizeProcessName(name);
            if (n.Length == 0) return true;
            for (int i = 0; i < ProtectedNames.Length; i++) if (ProtectedNames[i] == n) return true;
            return false;
        }

        public static string NormalizeProcessName(string name)
        {
            if (name == null) return "";
            string n = name.Trim().Trim('"').ToLowerInvariant();
            int slash = n.LastIndexOfAny(new char[] { '\\', '/' });
            if (slash >= 0) n = n.Substring(slash + 1);
            if (n.EndsWith(".exe")) n = n.Substring(0, n.Length - 4);
            return n;
        }

        public static DateTime LogicalDay(DateTime now, int resetHour)
        {
            DateTime d = now.Date;
            if (now.Hour < resetHour) d = d.AddDays(-1);
            return d;
        }

        public static string DayKey(DateTime day)
        {
            return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public static bool IsWeekend(DateTime day)
        {
            return day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday;
        }

        public static bool TryParseHm(string value, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            if (string.IsNullOrEmpty(value)) return false;
            string[] parts = value.Split(':');
            if (parts.Length != 2) return false;
            int h, m;
            if (!int.TryParse(parts[0].Trim(), out h)) return false;
            if (!int.TryParse(parts[1].Trim(), out m)) return false;
            if (h < 0 || h > 23 || m < 0 || m > 59) return false;
            result = new TimeSpan(h, m, 0);
            return true;
        }

        /// <summary>Cek apakah "sekarang" ada di jendela jam tidur (boleh melewati tengah malam).</summary>
        public static bool InBedtime(DateTime now, string start, string end)
        {
            TimeSpan s, e;
            if (!TryParseHm(start, out s)) return false;
            if (!TryParseHm(end, out e)) return false;
            if (s == e) return false;
            TimeSpan t = now.TimeOfDay;
            if (s < e) return t >= s && t < e;
            return t >= s || t < e;   // melewati tengah malam
        }

        public static string FormatDuration(int seconds)
        {
            if (seconds < 0) return "tanpa batas";
            int h = seconds / 3600;
            int m = (seconds % 3600) / 60;
            int s = seconds % 60;
            if (h > 0) return string.Format("{0} jam {1} menit", h, m);
            if (m > 0) return string.Format("{0} menit", m);
            return string.Format("{0} detik", s);
        }

        public static string FormatClock(int seconds)
        {
            if (seconds < 0) return "tanpa batas";
            int h = seconds / 3600;
            int m = (seconds % 3600) / 60;
            int s = seconds % 60;
            if (h > 0) return string.Format("{0}:{1:00}:{2:00}", h, m, s);
            return string.Format("{0:00}:{1:00}", m, s);
        }

        public static void AtomicWriteAllText(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        public static bool IsElevated()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id =
                    System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    System.Security.Principal.WindowsPrincipal p =
                        new System.Security.Principal.WindowsPrincipal(id);
                    return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }
    }
}
