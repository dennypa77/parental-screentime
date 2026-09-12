using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace ScreenTimeGuard
{
    public static class StatusReader
    {
        /// <summary>Membaca status terakhir yang ditulis agent. null jika belum ada / rusak.</summary>
        public static Status Read()
        {
            try
            {
                if (!File.Exists(Paths.StatusFile)) return null;
                return Json.Read<Status>(File.ReadAllText(Paths.StatusFile, Encoding.UTF8));
            }
            catch { return null; }
        }

        public static bool IsFresh(Status s)
        {
            if (s == null || string.IsNullOrEmpty(s.GeneratedAtUtc)) return false;
            DateTime t;
            if (!DateTime.TryParse(s.GeneratedAtUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out t)) return false;
            return (DateTime.UtcNow - t.ToUniversalTime()).TotalSeconds < 45;
        }
    }

    /// <summary>Daftar aplikasi bergambar sendiri, dipakai di jendela anak.</summary>
    public class StatusPanel : Panel
    {
        const int RowHeight = 72;
        Status _status;
        string _message = "Menunggu data dari agent...";

        public StatusPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            AutoScroll = true;
        }

        public void Update(Status status, string message)
        {
            _status = status;
            _message = message;
            int rows = status == null ? 0 : status.Apps.Count;
            AutoScrollMinSize = new Size(0, Math.Max(0, rows * RowHeight + 8));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);

            if (_status == null || _status.Apps.Count == 0)
            {
                using (SolidBrush b = new SolidBrush(Theme.TextDim))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(_message, Theme.Body, b, new RectangleF(20, 20, Width - 40, 120), sf);
                }
                return;
            }

            int y = 4 + AutoScrollPosition.Y;
            for (int i = 0; i < _status.Apps.Count; i++)
            {
                DrawRow(g, _status.Apps[i], 8, y, Width - 24);
                y += RowHeight;
            }
        }

        void DrawRow(Graphics g, StatusApp app, int x, int y, int w)
        {
            Rectangle card = new Rectangle(x, y, w, RowHeight - 8);
            using (SolidBrush bg = new SolidBrush(app.Running ? Theme.CardAlt : Theme.Card))
                g.FillRectangle(bg, card);

            bool unlimited = app.RemainingSeconds < 0;
            double ratio = 1.0;
            if (!unlimited && app.LimitSeconds > 0)
                ratio = (double)app.RemainingSeconds / app.LimitSeconds;
            else if (!unlimited && app.LimitSeconds == 0)
                ratio = 0;

            Color accent = !app.Enabled ? Theme.TextDim : Theme.ForRatio(ratio, app.Blocked);

            using (SolidBrush stripe = new SolidBrush(accent))
                g.FillRectangle(stripe, card.X, card.Y, 4, card.Height);

            // Nama aplikasi
            using (SolidBrush b = new SolidBrush(Theme.Text))
                g.DrawString(app.Name, Theme.BodyBold, b, card.X + 14, card.Y + 8);

            // Keterangan status
            string sub;
            if (!app.Enabled) sub = "Tidak dibatasi (hanya dicatat)";
            else if (app.Blocked) sub = app.BlockReason;
            else if (unlimited) sub = "Tanpa batas waktu";
            else sub = "Terpakai " + Util.FormatDuration(app.UsedSeconds)
                       + " dari " + Util.FormatDuration(app.LimitSeconds)
                       + (app.BonusMinutes != 0 ? "  (bonus " + app.BonusMinutes + " menit)" : "");
            if (app.Running) sub = "● Sedang berjalan  •  " + sub;

            using (SolidBrush b = new SolidBrush(Theme.TextDim))
                g.DrawString(sub, Theme.Body, b, card.X + 14, card.Y + 27);

            // Sisa waktu (kanan)
            string big = unlimited ? "∞" : Util.FormatClock(app.RemainingSeconds);
            if (app.Blocked) big = "HABIS";
            using (SolidBrush b = new SolidBrush(accent))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(big, Theme.Mono, b,
                    new RectangleF(card.Right - 130, card.Y + 6, 120, 30), sf);
            }

            // Bilah kemajuan
            int barY = card.Bottom - 12;
            Rectangle track = new Rectangle(card.X + 14, barY, card.Width - 28, 6);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 65, 78)))
                g.FillRectangle(b, track);
            if (!unlimited)
            {
                int fill = (int)(track.Width * Math.Max(0, Math.Min(1, ratio)));
                if (fill > 0)
                    using (SolidBrush b = new SolidBrush(accent))
                        g.FillRectangle(b, track.X, track.Y, fill, track.Height);
            }
            else
            {
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, track);
            }
        }
    }

    /// <summary>Jendela yang dilihat anak: sisa waktu tiap aplikasi. Tanpa password.</summary>
    public class ChildForm : Form
    {
        readonly Label _header = new Label();
        readonly Label _sub = new Label();
        readonly StatusPanel _list = new StatusPanel();
        readonly Timer _timer = new Timer();

        const int StaleGraceSeconds = 90;
        DateTime _staleSince = DateTime.MinValue;

        public ChildForm()
        {
            Text = "Sisa Waktu Hari Ini";
            Icon = IconFactory.TrayIcon();
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 520);
            MinimumSize = new Size(460, 320);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;

            _header.Text = "Sisa Waktu Hari Ini";
            _header.Font = Theme.H1;
            _header.ForeColor = Theme.Text;
            _header.AutoSize = false;
            _header.Dock = DockStyle.Top;
            _header.Height = 34;
            _header.Padding = new Padding(14, 10, 14, 0);

            _sub.Font = Theme.Body;
            _sub.ForeColor = Theme.TextDim;
            _sub.AutoSize = false;
            _sub.Dock = DockStyle.Top;
            _sub.Height = 44;
            _sub.Padding = new Padding(16, 2, 14, 0);

            _list.Dock = DockStyle.Fill;

            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = 44;
            footer.BackColor = Theme.Bg;

            Button close = new Button();
            close.Text = "Tutup";
            close.SetBounds(0, 8, 100, 30);
            Theme.StyleButton(close, false);
            close.Click += delegate { Close(); };
            footer.Controls.Add(close);
            footer.Resize += delegate { close.Left = footer.Width - close.Width - 16; };

            Controls.Add(_list);
            Controls.Add(_sub);
            Controls.Add(_header);
            Controls.Add(footer);

            _timer.Interval = 1000;
            _timer.Tick += delegate { Refresh0(); };
            _timer.Start();
            Refresh0();
        }

        void Refresh0()
        {
            Status s = StatusReader.Read();
            bool fresh = StatusReader.IsFresh(s);

            if (!fresh)
            {
                // Agent sempat mati beberapa puluh detik saat memasang pembaruan atau
                // saat komputer baru menyala; jangan langsung menakut-nakuti anak.
                if (_staleSince == DateTime.MinValue) _staleSince = DateTime.UtcNow;
                if ((DateTime.UtcNow - _staleSince).TotalSeconds < StaleGraceSeconds)
                {
                    _sub.Text = "Menyambung ke pengawas...";
                    _sub.ForeColor = Theme.TextDim;
                }
                else
                {
                    _sub.Text = "Agent pengawas sedang tidak berjalan. "
                                + "Hubungi orang tua kalau ini terus muncul.";
                    _sub.ForeColor = Theme.Warn;
                }
                _list.Update(s, "Data belum tersedia.");
                return;
            }
            _staleSince = DateTime.MinValue;

            _sub.ForeColor = Theme.TextDim;
            StringBuilder sb = new StringBuilder();
            sb.Append(s.IsWeekend ? "Akhir pekan" : "Hari sekolah");
            sb.Append("  •  jatah direset tiap pukul ").Append(s.ResetsAtText);
            if (s.TotalLimitSeconds >= 0)
                sb.Append("\nTotal waktu layar: sisa ")
                  .Append(Util.FormatDuration(s.TotalRemainingSeconds))
                  .Append(" dari ").Append(Util.FormatDuration(s.TotalLimitSeconds));
            if (s.Bedtime) sb.Append("\nSekarang jam tidur (").Append(s.BedtimeText).Append(")");
            else if (s.Paused) sb.Append("\nPengawasan sedang dijeda orang tua (")
                                 .Append(Util.FormatDuration(s.PauseLeftSeconds)).Append(" lagi)");
            _sub.Text = sb.ToString();

            _list.Update(s, "Belum ada aplikasi yang dibatasi.");
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            _timer.Dispose();
            base.OnFormClosed(e);
        }
    }

    /// <summary>Ikon tray + pengawas notifikasi, berjalan di sesi pengguna.</summary>
    public class TrayApp : ApplicationContext
    {
        readonly NotifyIcon _icon = new NotifyIcon();
        readonly Timer _poll = new Timer();
        readonly Timer _foreground = new Timer();

        ChildForm _childForm;
        ParentForm _parentForm;

        // Ambang peringatan yang sudah ditampilkan hari ini: "proses|menit".
        readonly HashSet<string> _warned = new HashSet<string>();
        readonly HashSet<string> _inGrace = new HashSet<string>();
        readonly HashSet<string> _closed = new HashSet<string>();
        string _warnedDay = "";
        bool _agentMissingNotified;

        const int StaleGraceSeconds = 90;
        DateTime _staleSince = DateTime.MinValue;

        public TrayApp()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Sisa waktu hari ini", null, delegate { ShowChild(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Panel orang tua...", null, delegate { ShowParent(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Keluar", null, delegate { ExitWithPassword(); });

            _icon.Icon = IconFactory.TrayIcon();
            _icon.Text = "Screen Time Guard";
            _icon.Visible = true;
            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += delegate { ShowChild(); };

            _poll.Interval = 2000;
            _poll.Tick += delegate { Poll(); };
            _poll.Start();

            _foreground.Interval = 10000;
            _foreground.Tick += delegate { ReportForeground(); };
            _foreground.Start();

            ReportForeground();
            Poll();
        }

        void ShowChild()
        {
            if (_childForm == null || _childForm.IsDisposed)
            {
                _childForm = new ChildForm();
                _childForm.FormClosed += delegate { _childForm = null; };
                _childForm.Show();
            }
            _childForm.WindowState = FormWindowState.Normal;
            _childForm.Activate();
        }

        void ShowParent()
        {
            if (_parentForm != null && !_parentForm.IsDisposed)
            {
                _parentForm.Activate();
                return;
            }

            if (!IpcClient.AgentAvailable())
            {
                Msg.Error(null, "Agent pengawas tidak berjalan, jadi pengaturan tidak bisa dibuka.\n\n"
                                + "Jalankan install.ps1 sebagai Administrator, atau mulai ulang komputer.");
                return;
            }

            string password = LoginForm.Ask();
            if (password == null) return;

            _parentForm = new ParentForm(password);
            _parentForm.FormClosed += delegate { _parentForm = null; };
            _parentForm.Show();
            _parentForm.Activate();
        }

        void ExitWithPassword()
        {
            string password = LoginForm.Ask();
            if (password == null) return;
            IpcResponse r = IpcClient.Send("VERIFY", password, "", "", "");
            if (r == null || !r.Ok)
            {
                Msg.Error(null, r == null ? "Agent tidak merespons." : r.Error);
                return;
            }
            Msg.Info(null, "Ikon tray ditutup. Agent pengawas tetap berjalan di latar belakang "
                           + "dan ikon ini akan muncul lagi saat login berikutnya.");
            _icon.Visible = false;
            ExitThread();
        }

        void ReportForeground()
        {
            string name = Native.ForegroundProcessName();
            if (name.Length == 0) return;
            IpcClient.Send("FOREGROUND", "", name, "", "");
        }

        void Poll()
        {
            Status s = StatusReader.Read();
            if (!StatusReader.IsFresh(s))
            {
                // Saat pembaruan dipasang atau komputer baru menyala, agent memang
                // mati sebentar. Baru dianggap bermasalah setelah lewat masa tenggang.
                if (_staleSince == DateTime.MinValue) _staleSince = DateTime.UtcNow;
                bool reallyGone = (DateTime.UtcNow - _staleSince).TotalSeconds >= StaleGraceSeconds;

                _icon.Text = reallyGone
                    ? "Screen Time Guard - agent tidak aktif"
                    : "Screen Time Guard - menyambung...";

                if (reallyGone && !_agentMissingNotified)
                {
                    _agentMissingNotified = true;
                    Toast.Show("Pengawas tidak aktif",
                        "Agent Screen Time Guard sedang tidak berjalan. Beri tahu orang tua.",
                        Theme.Warn, 8);
                }
                return;
            }
            _staleSince = DateTime.MinValue;
            _agentMissingNotified = false;

            if (_warnedDay != s.Day)
            {
                _warnedDay = s.Day;
                _warned.Clear();
                _inGrace.Clear();
                _closed.Clear();
            }

            int[] thresholds = ParseWarn(s.WarnMinutes);
            string shortest = null;
            int shortestLeft = int.MaxValue;

            for (int i = 0; i < s.Apps.Count; i++)
            {
                StatusApp a = s.Apps[i];

                if (a.Running && a.RemainingSeconds >= 0 && !a.Blocked && a.RemainingSeconds < shortestLeft)
                {
                    shortestLeft = a.RemainingSeconds;
                    shortest = a.Name;
                }

                // Pemberitahuan masa tenggang.
                if (a.Blocked && a.Running && a.GraceLeftSeconds >= 0)
                {
                    if (!_inGrace.Contains(a.Process))
                    {
                        _inGrace.Add(a.Process);
                        Toast.Show(a.Name + " akan ditutup",
                            a.BlockReason + ".\nSimpan pekerjaanmu sekarang - aplikasi ditutup dalam "
                            + Util.FormatDuration(a.GraceLeftSeconds) + ".", Theme.Bad, 12);
                    }
                    continue;
                }
                if (!a.Blocked) _inGrace.Remove(a.Process);

                // Pemberitahuan sudah ditutup.
                if (a.Blocked && a.Running && a.GraceLeftSeconds < 0 && !_closed.Contains(a.Process))
                {
                    _closed.Add(a.Process);
                    Toast.Show(a.Name + " ditutup", a.BlockReason
                        + ".\nBuka \"Sisa waktu hari ini\" untuk melihat aplikasi lain yang masih ada waktunya.",
                        Theme.Bad, 10);
                }
                if (!a.Blocked) _closed.Remove(a.Process);

                // Peringatan ambang batas.
                if (!a.Running || a.Blocked || a.RemainingSeconds < 0 || !a.Enabled) continue;
                for (int t = 0; t < thresholds.Length; t++)
                {
                    int thresholdSec = thresholds[t] * 60;
                    if (a.RemainingSeconds > thresholdSec) continue;
                    string key = a.Process + "|" + thresholds[t];
                    if (_warned.Contains(key)) continue;
                    // Tandai ambang yang lebih besar sebagai sudah lewat supaya tidak menumpuk.
                    for (int u = 0; u <= t; u++) _warned.Add(a.Process + "|" + thresholds[u]);
                    Toast.Show("Sisa waktu " + a.Name,
                        "Tinggal " + Util.FormatDuration(a.RemainingSeconds)
                        + ". Setelah habis aplikasi akan ditutup otomatis.", Theme.Warn, 8);
                    break;
                }
            }

            if (shortest != null)
                _icon.Text = Trunc("Screen Time Guard\n" + shortest + ": sisa "
                                   + Util.FormatClock(shortestLeft));
            else if (s.Paused) _icon.Text = "Screen Time Guard - dijeda";
            else if (s.Bedtime) _icon.Text = "Screen Time Guard - jam tidur";
            else _icon.Text = "Screen Time Guard - aktif";
        }

        static string Trunc(string s)
        {
            return s.Length <= 63 ? s : s.Substring(0, 60) + "...";
        }

        static int[] ParseWarn(string text)
        {
            List<int> list = new List<int>();
            if (!string.IsNullOrEmpty(text))
            {
                string[] parts = text.Split(',');
                for (int i = 0; i < parts.Length; i++)
                {
                    int v;
                    if (int.TryParse(parts[i].Trim(), out v) && v > 0 && !list.Contains(v)) list.Add(v);
                }
            }
            if (list.Count == 0) { list.Add(15); list.Add(5); list.Add(1); }
            list.Sort();
            list.Reverse();
            return list.ToArray();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _poll.Dispose();
                _foreground.Dispose();
                _icon.Visible = false;
                _icon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
