using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ScreenTimeGuard
{
    [DataContract]
    public class UpdateManifest
    {
        [DataMember(Name = "version", Order = 1)] public string Version;
        [DataMember(Name = "url", Order = 2)] public string Url;
        [DataMember(Name = "sha256", Order = 3)] public string Sha256;
        [DataMember(Name = "size", Order = 4)] public long Size;
        [DataMember(Name = "notes", Order = 5)] public string Notes;
        [DataMember(Name = "signature", Order = 6)] public string Signature;

        public UpdateManifest()
        {
            Version = "";
            Url = "";
            Sha256 = "";
            Notes = "";
            Signature = "";
        }
    }

    [DataContract]
    public class UpdateCheckResult
    {
        [DataMember(Order = 1)] public string CurrentVersion;
        [DataMember(Order = 2)] public string LatestVersion;
        [DataMember(Order = 3)] public bool UpdateAvailable;
        [DataMember(Order = 4)] public string Notes;
        [DataMember(Order = 5)] public string SizeText;
        [DataMember(Order = 6)] public bool Signed;

        public UpdateCheckResult()
        {
            CurrentVersion = "";
            LatestVersion = "";
            Notes = "";
            SizeText = "";
        }
    }

    public static class Updater
    {
        const int MaxManifestBytes = 64 * 1024;
        const long MaxPayloadBytes = 40L * 1024 * 1024;
        const int TimeoutMs = 30000;

        public static string UpdateDir { get { return Path.Combine(Paths.Dir, "update"); } }

        // ------------------------------------------------------------- jaringan

        static void PrepareTls()
        {
            try
            {
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | (SecurityProtocolType)12288 /* Tls13 */;
            }
            catch
            {
                try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
                catch { }
            }
        }

        static void RequireHttps(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                throw new InvalidOperationException("Alamat pembaruan tidak valid.");
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Alamat pembaruan harus memakai https.");
        }

        static HttpWebRequest MakeRequest(string url)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = "ScreenTimeGuard/" + AppInfo.Version;
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;
            req.AllowAutoRedirect = true;
            req.MaximumAutomaticRedirections = 5;
            req.CachePolicy = new System.Net.Cache.RequestCachePolicy(
                System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            return req;
        }

        static byte[] DownloadBytes(string url, long maxBytes)
        {
            PrepareTls();
            RequireHttps(url);
            using (WebResponse resp = MakeRequest(url).GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buf = new byte[16384];
                long total = 0;
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > maxBytes)
                        throw new InvalidOperationException("Berkas pembaruan melebihi batas ukuran.");
                    ms.Write(buf, 0, n);
                }
                return ms.ToArray();
            }
        }

        static void DownloadFile(string url, string path, long maxBytes)
        {
            PrepareTls();
            RequireHttps(url);
            using (WebResponse resp = MakeRequest(url).GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buf = new byte[65536];
                long total = 0;
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > maxBytes)
                        throw new InvalidOperationException("Berkas pembaruan melebihi batas ukuran.");
                    fs.Write(buf, 0, n);
                }
            }
        }

        // -------------------------------------------------------------- manifest

        public static UpdateManifest FetchManifest(string url)
        {
            if (string.IsNullOrEmpty(url)) url = AppInfo.DefaultUpdateUrl;
            byte[] raw = DownloadBytes(url, MaxManifestBytes);
            UpdateManifest m = Json.Read<UpdateManifest>(Encoding.UTF8.GetString(raw));
            if (m == null || string.IsNullOrEmpty(m.Version) || string.IsNullOrEmpty(m.Url)
                || string.IsNullOrEmpty(m.Sha256))
                throw new InvalidOperationException("Manifest pembaruan tidak lengkap.");
            m.Sha256 = m.Sha256.Trim().ToLowerInvariant();
            if (m.Sha256.Length != 64)
                throw new InvalidOperationException("Nilai sha256 dalam manifest tidak valid.");
            RequireHttps(m.Url);
            return m;
        }

        public static bool IsNewer(string candidate, string current)
        {
            Version a, b;
            if (!Version.TryParse(Normalize(candidate), out a)) return false;
            if (!Version.TryParse(Normalize(current), out b)) return false;
            return a > b;
        }

        static string Normalize(string v)
        {
            if (string.IsNullOrEmpty(v)) return "0.0.0";
            string s = v.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            return s;
        }

        public static UpdateCheckResult Check(Settings settings)
        {
            UpdateManifest m = FetchManifest(settings.UpdateUrl);
            UpdateCheckResult r = new UpdateCheckResult();
            r.CurrentVersion = AppInfo.Version;
            r.LatestVersion = m.Version;
            r.UpdateAvailable = IsNewer(m.Version, AppInfo.Version);
            r.Notes = m.Notes == null ? "" : m.Notes;
            r.Signed = !string.IsNullOrEmpty(m.Signature);
            r.SizeText = m.Size > 0
                ? Math.Round(m.Size / 1024.0, 1).ToString(CultureInfo.InvariantCulture) + " KB"
                : "";
            return r;
        }

        // --------------------------------------------------------- verifikasi

        public static string Sha256File(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return ToHex(sha.ComputeHash(fs));
        }

        static string ToHex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// Verifikasi tanda tangan RSA atas teks "versi|sha256".
        /// Hanya dipakai jika orang tua mengisi UpdatePublicKey di settings.json.
        /// </summary>
        static void VerifySignature(string publicKeyBase64Xml, UpdateManifest m)
        {
            if (string.IsNullOrEmpty(publicKeyBase64Xml)) return;   // fitur tidak diaktifkan

            if (string.IsNullOrEmpty(m.Signature))
                throw new InvalidOperationException(
                    "Pembaruan ini tidak bertanda tangan, padahal verifikasi tanda tangan diaktifkan.");

            string xml;
            try { xml = Encoding.UTF8.GetString(Convert.FromBase64String(publicKeyBase64Xml)); }
            catch { throw new InvalidOperationException("UpdatePublicKey di settings.json tidak valid."); }

            byte[] signature;
            try { signature = Convert.FromBase64String(m.Signature); }
            catch { throw new InvalidOperationException("Tanda tangan pembaruan tidak valid."); }

            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.FromXmlString(xml);
                byte[] data = Encoding.UTF8.GetBytes(m.Version + "|" + m.Sha256);
                // Overload dengan HashAlgorithmName dipakai supaya SHA256 tetap berjalan
                // apa pun jenis penyedia kriptografi bawaan sistem.
                if (!rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    throw new InvalidOperationException(
                        "Tanda tangan pembaruan tidak cocok. Pembaruan dibatalkan.");
            }
        }

        // ------------------------------------------------------------- pasang

        /// <summary>
        /// Mengunduh dan memverifikasi versi baru, lalu menjalankan penolong
        /// (salinan .exe baru) yang akan menimpa berkas terpasang.
        /// Dipanggil oleh agent, yang berjalan sebagai SYSTEM.
        /// </summary>
        public static string DownloadAndLaunchInstaller(Settings settings)
        {
            UpdateManifest m = FetchManifest(settings.UpdateUrl);
            if (!IsNewer(m.Version, AppInfo.Version))
                throw new InvalidOperationException(
                    "Sudah memakai versi terbaru (" + AppInfo.Version + ").");

            Directory.CreateDirectory(UpdateDir);
            CleanOldStaging();

            string staged = Path.Combine(UpdateDir, "ScreenTimeGuard-" + m.Version + ".exe");
            string part = staged + ".part";
            if (File.Exists(part)) File.Delete(part);

            Log.Write("Mengunduh pembaruan " + m.Version + " dari " + m.Url);
            DownloadFile(m.Url, part, MaxPayloadBytes);

            FileInfo fi = new FileInfo(part);
            if (m.Size > 0 && fi.Length != m.Size)
            {
                File.Delete(part);
                throw new InvalidOperationException("Ukuran berkas tidak sesuai manifest.");
            }

            string actual = Sha256File(part);
            if (actual != m.Sha256)
            {
                File.Delete(part);
                throw new InvalidOperationException(
                    "Sidik jari SHA256 tidak cocok. Berkas ditolak demi keamanan.");
            }

            try { VerifySignature(settings.UpdatePublicKey, m); }
            catch { File.Delete(part); throw; }

            if (File.Exists(staged)) File.Delete(staged);
            File.Move(part, staged);

            // Berkas sudah diverifikasi dan berada di folder yang hanya bisa ditulis
            // Administrator/SYSTEM, jadi aman untuk dijalankan.
            string target = Process.GetCurrentProcess().MainModule.FileName;
            ProcessStartInfo psi = new ProcessStartInfo(staged);
            psi.Arguments = "--apply-update \"" + target + "\" "
                            + Process.GetCurrentProcess().Id + " " + m.Sha256;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = UpdateDir;
            Process.Start(psi);

            Log.Write("Penolong pembaruan " + m.Version + " dijalankan; agent akan berhenti.");
            return m.Version;
        }

        static void CleanOldStaging()
        {
            try
            {
                string[] files = Directory.GetFiles(UpdateDir);
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(files[i]) < DateTime.UtcNow.AddDays(-7))
                            File.Delete(files[i]);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // --------------------------------------------- mode penolong (--apply-update)

        /// <summary>
        /// Dijalankan oleh salinan .exe BARU dari folder update.
        /// Menunggu agent lama berhenti, menimpa berkas terpasang, lalu menyalakan ulang.
        /// </summary>
        public static int ApplyUpdate(string targetPath, int oldPid, string expectedSha256)
        {
            Log.Write("[update] Mulai memasang " + AppInfo.Version + " ke " + targetPath);

            string self = Process.GetCurrentProcess().MainModule.FileName;
            if (string.Equals(self, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                Log.Write("[update] Dibatalkan: penolong berjalan dari lokasi tujuan.");
                return 1;
            }

            WaitForExit(oldPid, 30000);
            StopTask(AppInfo.TaskAgent);
            StopTask(AppInfo.TaskTray);
            KillOthers(self);

            string backup = targetPath + ".bak";
            try
            {
                if (File.Exists(targetPath))
                {
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Copy(targetPath, backup, true);
                }
            }
            catch (Exception ex) { Log.Write("[update] Gagal membuat cadangan: " + ex.Message); }

            bool copied = false;
            for (int attempt = 1; attempt <= 12 && !copied; attempt++)
            {
                try
                {
                    File.Copy(self, targetPath, true);
                    copied = true;
                }
                catch (Exception ex)
                {
                    Log.Write("[update] Percobaan salin " + attempt + " gagal: " + ex.Message);
                    Thread.Sleep(1000);
                }
            }

            if (copied)
            {
                try
                {
                    string got = Sha256File(targetPath);
                    if (!string.IsNullOrEmpty(expectedSha256)
                        && !string.Equals(got, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Write("[update] SHA256 setelah penyalinan tidak cocok; mengembalikan cadangan.");
                        copied = false;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("[update] Gagal memeriksa hasil salinan: " + ex.Message);
                    copied = false;
                }
            }

            if (!copied)
            {
                try
                {
                    if (File.Exists(backup))
                    {
                        File.Copy(backup, targetPath, true);
                        Log.Write("[update] Versi lama dikembalikan.");
                    }
                }
                catch (Exception ex) { Log.Write("[update] Gagal mengembalikan cadangan: " + ex.Message); }
            }
            else
            {
                Log.Write("[update] Berhasil dipasang: versi " + AppInfo.Version);
            }

            StartTask(AppInfo.TaskAgent);
            StartTask(AppInfo.TaskTray);

            // Kalau Scheduled Task belum terdaftar (pemakaian manual), nyalakan langsung.
            Thread.Sleep(2500);
            if (!AnyRunning(self))
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(targetPath, "--agent");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    Process.Start(psi);
                    Log.Write("[update] Agent dijalankan langsung (tanpa Scheduled Task).");
                }
                catch (Exception ex) { Log.Write("[update] Gagal menjalankan agent: " + ex.Message); }
            }

            return copied ? 0 : 1;
        }

        static void WaitForExit(int pid, int timeoutMs)
        {
            if (pid <= 0) return;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (!p.WaitForExit(timeoutMs))
                    {
                        Log.Write("[update] Agent lama belum berhenti; dihentikan paksa.");
                        try { p.Kill(); p.WaitForExit(5000); }
                        catch { }
                    }
                }
            }
            catch (ArgumentException) { /* sudah berhenti */ }
            catch (Exception ex) { Log.Write("[update] WaitForExit: " + ex.Message); }
        }

        static void KillOthers(string selfPath)
        {
            int selfPid = Process.GetCurrentProcess().Id;
            Process[] all;
            try { all = Process.GetProcessesByName("ScreenTimeGuard"); }
            catch { return; }

            for (int i = 0; i < all.Length; i++)
            {
                try
                {
                    if (all[i].Id == selfPid) continue;
                    all[i].Kill();
                    all[i].WaitForExit(5000);
                    Log.Write("[update] Menghentikan proses lama pid " + all[i].Id);
                }
                catch { }
                finally { try { all[i].Dispose(); } catch { } }
            }
        }

        static bool AnyRunning(string selfPath)
        {
            int selfPid = Process.GetCurrentProcess().Id;
            try
            {
                Process[] all = Process.GetProcessesByName("ScreenTimeGuard");
                for (int i = 0; i < all.Length; i++)
                {
                    bool other = all[i].Id != selfPid;
                    try { all[i].Dispose(); }
                    catch { }
                    if (other) return true;
                }
            }
            catch { }
            return false;
        }

        static void StopTask(string name) { RunSchtasks("/end /tn \"" + name + "\""); }

        static void StartTask(string name) { RunSchtasks("/run /tn \"" + name + "\""); }

        static void RunSchtasks(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                }
            }
            catch (Exception ex) { Log.Write("[update] schtasks " + args + ": " + ex.Message); }
        }
    }
}
