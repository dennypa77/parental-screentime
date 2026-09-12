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
                if (a == "agent" || a == "ui" || a == "setup" || a == "apply-update") mode = a;
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
            Icon = IconFactory.TrayIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(440, 290);
            Font = new Font("Segoe UI", 9f);

            bool exists = File.Exists(Paths.Settings);

            Label title = new Label();
            title.Text = exists ? "Atur ulang password orang tua" : "Buat password orang tua";
            title.Font = new Font("Segoe UI Semibold", 11f);
            title.SetBounds(16, 16, 400, 24);

            Label desc = new Label();
            desc.Text = "Password ini dipakai untuk membuka panel pengaturan di komputer anak. "
                      + "Anak hanya bisa melihat sisa waktu, tidak bisa mengubah apa pun.";
            desc.SetBounds(16, 42, 408, 40);
            desc.ForeColor = SystemColors.GrayText;

            Label l1 = new Label();
            l1.Text = "Password (minimal 4 karakter)";
            l1.SetBounds(16, 92, 400, 18);
            _p1.SetBounds(16, 112, 280, 24);
            _p1.UseSystemPasswordChar = true;

            Label l2 = new Label();
            l2.Text = "Ulangi password";
            l2.SetBounds(16, 144, 400, 18);
            _p2.SetBounds(16, 164, 280, 24);
            _p2.UseSystemPasswordChar = true;

            Label l3 = new Label();
            l3.Text = "Jatah harian direset tiap pukul";
            l3.SetBounds(16, 200, 200, 20);
            _resetHour.SetBounds(220, 196, 60, 24);
            _resetHour.Minimum = 0;
            _resetHour.Maximum = 23;
            _resetHour.Value = 4;

            Button ok = new Button();
            ok.Text = "Simpan";
            ok.SetBounds(248, 242, 84, 30);
            ok.Click += delegate { Save(); };

            Button cancel = new Button();
            cancel.Text = "Batal";
            cancel.SetBounds(340, 242, 84, 30);
            cancel.DialogResult = DialogResult.Cancel;

            Controls.Add(title);
            Controls.Add(desc);
            Controls.Add(l1);
            Controls.Add(_p1);
            Controls.Add(l2);
            Controls.Add(_p2);
            Controls.Add(l3);
            Controls.Add(_resetHour);
            Controls.Add(ok);
            Controls.Add(cancel);
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
