using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ScreenTimeGuard
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string mode = "ui";
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].TrimStart('-', '/').ToLowerInvariant();
                if (a == "agent" || a == "ui" || a == "setup" || a == "apply-update"
                    || a == "simulate") mode = a;
            }

            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Log.Write("FATAL (" + mode + "): " + e.ExceptionObject);
            };

            try
            {
                switch (mode)
                {
                    case "agent": return RunAgent();
                    case "setup": return RunSetup();
                    case "apply-update": return RunApplyUpdate(args);
                    case "simulate": return RunSimulation(args);
                    default: return RunUi();
                }
            }
            catch (Exception ex)
            {
                Log.Write("FATAL (" + mode + "): " + ex);
                if (mode != "agent")
                    MessageBox.Show("Screen Time Guard gagal dijalankan:\n\n" + ex.Message,
                        "Screen Time Guard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        // ----------------------------------------------------------------- agent

        static int RunAgent()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "Global\\ScreenTimeGuard.Agent", out created))
            {
                if (!created)
                {
                    Log.Write("Agent lain sudah berjalan; proses ini keluar.");
                    return 0;
                }
                new Agent().Run();
                return 0;
            }
        }

        // -------------------------------------------------------------------- ui

        static int RunUi()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\ScreenTimeGuard.Ui", out created))
            {
                if (!created) return 0;   // ikon tray sudah ada di sesi ini

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
                {
                    Log.Write("UI error: " + e.Exception);
                };
                Application.Run(new TrayApp());
                return 0;
            }
        }

        // ------------------------------------------------------------ simulasi

        /// <summary>
        /// --simulate &lt;folder&gt; : menjalankan seluruh logika agent memakai folder data
        /// terpisah, TANPA menutup aplikasi dan TANPA mengunci layar. Dipakai untuk
        /// menguji aturan tanpa mengganggu pemasangan yang sedang berjalan.
        /// </summary>
        static int RunSimulation(string[] args)
        {
            string dir = null;
            bool next = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].TrimStart('-', '/').ToLowerInvariant() == "simulate") { next = true; continue; }
                if (next) { dir = args[i].Trim('"'); break; }
            }
            if (string.IsNullOrEmpty(dir))
            {
                Log.Write("--simulate butuh folder tujuan.");
                return 2;
            }

            Paths.RedirectForSimulation(dir);
            bool created;
            using (Mutex mutex = new Mutex(true, @"Local\ScreenTimeGuard.Simulasi", out created))
            {
                if (!created) return 0;
                new Agent(true).Run();
                return 0;
            }
        }

        // --------------------------------------------------------- apply-update

        /// <summary>
        /// Dijalankan oleh salinan .exe baru dari folder update:
        ///   --apply-update "&lt;path exe terpasang&gt;" &lt;pid agent lama&gt; &lt;sha256&gt;
        /// </summary>
        static int RunApplyUpdate(string[] args)
        {
            string target = null;
            int oldPid = 0;
            string sha = "";

            // Argumen posisional setelah tanda --apply-update.
            int index = 0;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.TrimStart('-', '/').ToLowerInvariant() == "apply-update") { index = 1; continue; }
                if (index == 1) { target = a.Trim('"'); index++; }
                else if (index == 2) { int.TryParse(a, out oldPid); index++; }
                else if (index == 3) { sha = a.Trim(); index++; }
            }

            if (string.IsNullOrEmpty(target))
            {
                Log.Write("[update] Argumen --apply-update tidak lengkap.");
                return 2;
            }
            return Updater.ApplyUpdate(target, oldPid, sha);
        }

        // ----------------------------------------------------------------- setup

        static int RunSetup()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (!Util.IsElevated())
            {
                MessageBox.Show("Jalankan perintah ini sebagai Administrator.",
                    "Screen Time Guard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            using (SetupForm f = new SetupForm())
                return f.ShowDialog() == DialogResult.OK ? 0 : 1;
        }
    }

    /// <summary>Wizard sekali jalan untuk menetapkan password orang tua.</summary>
    public class SetupForm : Form
    {
        readonly TextBox _p1 = new TextBox();
        readonly TextBox _p2 = new TextBox();
        readonly NumericUpDown _resetHour = new NumericUpDown();

        public SetupForm()
        {
            Text = "Screen Time Guard - Pengaturan Awal";
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            Icon = IconFactory.TrayIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(470, 330);
            MinimumSize = new Size(420, 280);
            Font = new Font("Segoe UI", 9f);

            Panel host = UiLayout.ScrollHost();

            bool exists = File.Exists(Paths.Settings);

            Label title = new Label();
            title.Text = exists ? "Atur ulang password orang tua" : "Buat password orang tua";
            title.Font = new Font("Segoe UI Semibold", 11f);
            title.SetBounds(0, 4, 420, 26);

            Label desc = new Label();
            desc.Text = "Password ini dipakai untuk membuka panel pengaturan di komputer anak. "
                      + "Anak hanya bisa melihat sisa waktu, tidak bisa mengubah apa pun.";
            desc.SetBounds(0, 32, 424, 42);
            desc.ForeColor = SystemColors.GrayText;

            Label l1 = new Label();
            l1.Text = "Password (minimal 4 karakter)";
            l1.SetBounds(0, 82, 420, 20);
            _p1.SetBounds(0, 104, 290, 26);
            _p1.UseSystemPasswordChar = true;

            Label l2 = new Label();
            l2.Text = "Ulangi password";
            l2.SetBounds(0, 138, 420, 20);
            _p2.SetBounds(0, 160, 290, 26);
            _p2.UseSystemPasswordChar = true;

            Label l3 = new Label();
            l3.Text = "Jatah harian direset tiap pukul";
            l3.SetBounds(0, 200, 210, 22);
            _resetHour.SetBounds(214, 196, 66, 26);
            _resetHour.Minimum = 0;
            _resetHour.Maximum = 23;
            _resetHour.Value = 4;

            host.Controls.Add(title);
            host.Controls.Add(desc);
            host.Controls.Add(l1);
            host.Controls.Add(_p1);
            host.Controls.Add(l2);
            host.Controls.Add(_p2);
            host.Controls.Add(l3);
            host.Controls.Add(_resetHour);

            Button ok = new Button();
            ok.Text = "Simpan";
            ok.Click += delegate { Save(); };

            Button cancel = new Button();
            cancel.Text = "Batal";
            cancel.DialogResult = DialogResult.Cancel;

            Controls.Add(host);
            Controls.Add(UiLayout.BottomBar(cancel, ok));
            AcceptButton = ok;
            CancelButton = cancel;

            if (exists)
            {
                try
                {
                    Settings s = Json.Read<Settings>(File.ReadAllText(Paths.Settings));
                    _resetHour.Value = Math.Max(0, Math.Min(23, s.ResetHour));
                }
                catch { }
            }
        }

        void Save()
        {
            if (_p1.Text.Length < 4)
            {
                MessageBox.Show(this, "Password minimal 4 karakter.", "Screen Time Guard",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_p1.Text != _p2.Text)
            {
                MessageBox.Show(this, "Kedua password tidak sama.", "Screen Time Guard",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                Paths.EnsureDir();

                Settings s = null;
                if (File.Exists(Paths.Settings))
                {
                    try { s = Json.Read<Settings>(File.ReadAllText(Paths.Settings)); }
                    catch { }
                }
                if (s == null) s = new Settings();
                if (s.Apps == null) s.Apps = new System.Collections.Generic.List<AppLimit>();

                string hash, salt;
                PasswordHash.Create(_p1.Text, out hash, out salt);
                s.PasswordHash = hash;
                s.PasswordSalt = salt;
                s.ResetHour = (int)_resetHour.Value;

                Util.AtomicWriteAllText(Paths.Settings, Json.Write(s));
                Log.Write("Password induk ditetapkan lewat --setup.");

                MessageBox.Show(this, "Password tersimpan.", "Screen Time Guard",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Gagal menyimpan: " + ex.Message, "Screen Time Guard",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
