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
        static int RowHeight { get { return Theme.Mono.Height + Theme.Body.Height + 30; } }
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
            _sub.Height = 76;
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
            if (s.SessionEnabled && s.SessionRemainingSeconds >= 0)
                sb.Append("\nPemakaian komputer (semua kegiatan): sisa ")
                  .Append(Util.FormatDuration(s.SessionRemainingSeconds))
                  .Append(" dari ").Append(Util.FormatDuration(s.SessionLimitSeconds));
            if (s.TotalLimitSeconds >= 0)
                sb.Append("\nTotal aplikasi yang diawasi: sisa ")
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

    /// <summary>Posisi dan bentuk penghitung melayang, disimpan per pengguna.</summary>
    [System.Runtime.Serialization.DataContract]
    public class OverlayPrefs
    {
        [System.Runtime.Serialization.DataMember(Order = 1)] public int X;
        [System.Runtime.Serialization.DataMember(Order = 2)] public int Y;
        [System.Runtime.Serialization.DataMember(Order = 3)] public bool Collapsed;
        [System.Runtime.Serialization.DataMember(Order = 4)] public bool Hidden;

        public OverlayPrefs() { X = -1; Y = -1; }

        static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ScreenTimeGuard");
                return Path.Combine(dir, "overlay.json");
            }
        }

        public static OverlayPrefs Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return Json.Read<OverlayPrefs>(File.ReadAllText(FilePath, Encoding.UTF8));
            }
            catch { }
            return new OverlayPrefs();
        }

        public void Save()
        {
            try
            {
                string path = FilePath;
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, Json.Write(this), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    /// <summary>
    /// Penghitung kecil yang selalu tampil di atas jendela lain, supaya anak
    /// bisa terus melihat sisa waktunya tanpa membuka apa pun.
    /// Bisa digeser, dan posisinya diingat.
    /// </summary>
    public class OverlayForm : Form
    {
        const int WS_EX_TOPMOST = 0x00000008;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;

        const int MaxRows = 6;

        // Ukuran diturunkan dari tinggi font supaya tetap pas saat pengguna memakai
        // penskalaan teks Windows 125% / 150%.
        static int HeaderHeight { get { return Theme.Body.Height + 6; } }
        static int RowHeight { get { return Theme.BodyBold.Height + 6; } }
        static int PanelWidth { get { return Theme.Body.Height * 13; } }
        static int ValueWidth { get { return Theme.Body.Height * 5; } }

        readonly Timer _timer = new Timer();
        readonly OverlayPrefs _prefs;
        readonly List<StatusApp> _rows = new List<StatusApp>();

        Status _status;
        bool _dragging;
        Point _dragStart;
        int _topMostTicks;
        bool _locked;

        public event EventHandler OpenRequested;
        public event EventHandler HideRequested;

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        public OverlayForm(OverlayPrefs prefs)
        {
            _prefs = prefs;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Bg;
            Opacity = 0.88;
            // Digambar sendiri dengan metrik piksel, jadi jangan diskalakan WinForms.
            AutoScaleMode = AutoScaleMode.None;
            Width = PanelWidth;
            Height = HeaderHeight + RowHeight + 8;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Buka jendela sisa waktu", null, delegate { RaiseOpen(); });
            ToolStripItem collapse = menu.Items.Add("Perkecil / perbesar", null, delegate { ToggleCollapsed(); });
            collapse.Name = "collapse";
            menu.Items.Add(new ToolStripSeparator());
            ToolStripItem hide = menu.Items.Add("Sembunyikan", null, delegate { RaiseHide(); });
            hide.Name = "hide";
            menu.Opening += delegate { hide.Enabled = !_locked; };
            ContextMenuStrip = menu;

            MouseDown += OnMouseDownHandler;
            MouseMove += OnMouseMoveHandler;
            MouseUp += OnMouseUpHandler;
            DoubleClick += delegate { RaiseOpen(); };

            _timer.Interval = 1000;
            _timer.Tick += delegate { Refresh0(); };
            _timer.Start();

            Refresh0();
            PlaceInitial();
        }

        void RaiseOpen()
        {
            EventHandler h = OpenRequested;
            if (h != null) h(this, EventArgs.Empty);
        }

        void RaiseHide()
        {
            if (_locked) return;
            _prefs.Hidden = true;
            _prefs.Save();
            EventHandler h = HideRequested;
            if (h != null) h(this, EventArgs.Empty);
        }

        void ToggleCollapsed()
        {
            _prefs.Collapsed = !_prefs.Collapsed;
            _prefs.Save();
            Refresh0();
        }

        void PlaceInitial()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int x = _prefs.X;
            int y = _prefs.Y;
            if (x < 0 || y < 0) { x = wa.Right - Width - 20; y = wa.Top + 20; }
            Location = ClampToScreens(new Point(x, y));
        }

        Point ClampToScreens(Point p)
        {
            // Pastikan tetap terlihat kalau susunan monitor berubah.
            Rectangle bounds = new Rectangle(p, Size);
            bool visible = false;
            for (int i = 0; i < Screen.AllScreens.Length; i++)
                if (Screen.AllScreens[i].WorkingArea.IntersectsWith(bounds)) visible = true;

            if (visible)
            {
                Screen s = Screen.FromPoint(p);
                int x = Math.Max(s.WorkingArea.Left, Math.Min(p.X, s.WorkingArea.Right - Width));
                int y = Math.Max(s.WorkingArea.Top, Math.Min(p.Y, s.WorkingArea.Bottom - Height));
                return new Point(x, y);
            }

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(wa.Right - Width - 20, wa.Top + 20);
        }

        void OnMouseDownHandler(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragStart = e.Location;
        }

        void OnMouseMoveHandler(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
        }

        void OnMouseUpHandler(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            Location = ClampToScreens(Location);
            _prefs.X = Location.X;
            _prefs.Y = Location.Y;
            _prefs.Save();
        }

        void Refresh0()
        {
            _status = StatusReader.Read();

            _rows.Clear();
            if (_status != null)
            {
                _locked = _status.OverlayLocked;

                List<StatusApp> candidates = new List<StatusApp>();
                for (int i = 0; i < _status.Apps.Count; i++)
                {
                    StatusApp a = _status.Apps[i];
                    if (!a.Enabled) continue;          // hanya dicatat, bukan dibatasi
                    if (a.LimitSeconds < 0) continue;  // tanpa batas, tidak perlu hitungan mundur
                    candidates.Add(a);
                }

                // Yang sedang berjalan lebih dulu, lalu yang sisanya paling sedikit.
                candidates.Sort(delegate (StatusApp a, StatusApp b)
                {
                    if (a.Running != b.Running) return a.Running ? -1 : 1;
                    return a.RemainingSeconds.CompareTo(b.RemainingSeconds);
                });

                int take = _prefs.Collapsed ? 1 : Math.Min(MaxRows, candidates.Count);
                for (int i = 0; i < take; i++) _rows.Add(candidates[i]);
            }

            int bodyRows = Math.Max(1, _rows.Count);
            int summaryRows = 0;
            if (!_prefs.Collapsed && _status != null)
            {
                if (ShowsSession(_status)) summaryRows++;
                if (_status.TotalLimitSeconds >= 0) summaryRows++;
            }
            int desired = HeaderHeight + bodyRows * RowHeight + summaryRows * RowHeight + 8;
            if (Height != desired) Height = desired;

            // Aplikasi layar penuh kadang merebut posisi teratas; tegakkan berkala.
            _topMostTicks++;
            if (_topMostTicks % 5 == 0 && IsHandleCreated) Native.ReassertTopMost(Handle);

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Card);

            using (Pen border = new Pen(Color.FromArgb(90, 96, 112)))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            // Kepala
            using (SolidBrush hb = new SolidBrush(Theme.CardAlt))
                g.FillRectangle(hb, 1, 1, Width - 2, HeaderHeight);
            using (SolidBrush tb = new SolidBrush(Theme.TextDim))
                g.DrawString("Sisa waktu", Theme.Body, tb, 7, 3);

            bool stale = !StatusReader.IsFresh(_status);
            using (SolidBrush db = new SolidBrush(stale ? Theme.Warn : Theme.Good))
                g.FillEllipse(db, Width - 16, (HeaderHeight - 7) / 2, 7, 7);

            int y = HeaderHeight + 4;

            if (_rows.Count == 0)
            {
                using (SolidBrush b = new SolidBrush(Theme.TextDim))
                    g.DrawString(stale ? "menyambung..." : "tidak ada batas hari ini",
                        Theme.Body, b, 8, y + 2);
                return;
            }

            for (int i = 0; i < _rows.Count; i++)
            {
                DrawRow(g, _rows[i], y);
                y += RowHeight;
            }

            if (_prefs.Collapsed || _status == null) return;

            if (ShowsSession(_status))
            {
                DrawSummary(g, y, "Komputer", _status.SessionRemainingSeconds);
                y += RowHeight;
            }
            if (_status.TotalLimitSeconds >= 0)
                DrawSummary(g, y, "Total aplikasi", _status.TotalRemainingSeconds);
        }

        static bool ShowsSession(Status s)
        {
            return s.SessionEnabled && s.SessionRemainingSeconds >= 0;
        }

        void DrawSummary(Graphics g, int y, string label, int remaining)
        {
            using (Pen sep = new Pen(Color.FromArgb(70, 76, 90)))
                g.DrawLine(sep, 8, y + 1, Width - 8, y + 1);
            using (SolidBrush b = new SolidBrush(Theme.TextDim))
                g.DrawString(label, Theme.Body, b, 8, y + 3);
            using (SolidBrush b = new SolidBrush(remaining <= 0 ? Theme.Bad : Theme.TextDim))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                g.DrawString(Util.FormatClock(remaining), Theme.Body, b,
                    new RectangleF(Width - ValueWidth - 8, y + 3, ValueWidth, RowHeight), sf);
            }
        }

        void DrawRow(Graphics g, StatusApp app, int y)
        {
            double ratio = app.LimitSeconds > 0
                ? (double)app.RemainingSeconds / app.LimitSeconds : 0;
            Color accent = Theme.ForRatio(ratio, app.Blocked);

            using (SolidBrush dot = new SolidBrush(accent))
                g.FillEllipse(dot, 8, y + 7, 7, 7);

            string name = app.Name;
            if (name.Length > 15) name = name.Substring(0, 14) + "…";

            using (SolidBrush b = new SolidBrush(app.Running ? Theme.Text : Theme.TextDim))
                g.DrawString(name, app.Running ? Theme.BodyBold : Theme.Body, b, 20, y + 2);

            string value = app.Blocked ? "habis" : Util.FormatClock(app.RemainingSeconds);
            using (SolidBrush b = new SolidBrush(accent))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                g.DrawString(value, Theme.BodyBold, b,
                    new RectangleF(Width - ValueWidth - 8, y + 2, ValueWidth, RowHeight), sf);
            }
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
        OverlayForm _overlay;
        OverlayPrefs _overlayPrefs;
        ToolStripMenuItem _overlayItem;

        // Ambang peringatan yang sudah ditampilkan hari ini: "proses|menit".
        readonly HashSet<string> _warned = new HashSet<string>();
        readonly HashSet<string> _inGrace = new HashSet<string>();
        readonly HashSet<string> _closed = new HashSet<string>();
        string _warnedDay = "";
        bool _agentMissingNotified;

        const int StaleGraceSeconds = 90;
        DateTime _staleSince = DateTime.MinValue;

        DateTime _lastLockAtUtc = DateTime.MinValue;
        readonly HashSet<int> _sessionWarned = new HashSet<int>();

        public TrayApp()
        {
            _overlayPrefs = OverlayPrefs.Load();

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Sisa waktu hari ini", null, delegate { ShowChild(); });
            _overlayItem = new ToolStripMenuItem("Penghitung di layar", null,
                delegate { ToggleOverlay(); });
            _overlayItem.CheckOnClick = false;
            menu.Items.Add(_overlayItem);
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

        /// <summary>
        /// Menyalakan/mematikan penghitung melayang sesuai pengaturan orang tua
        /// dan pilihan anak. Orang tua bisa mengunci supaya tidak bisa disembunyikan.
        /// </summary>
        void SyncOverlay(Status status)
        {
            bool allowed = status == null || status.OverlayEnabled;
            bool locked = status != null && status.OverlayLocked;
            bool wanted = allowed && (locked || !_overlayPrefs.Hidden);

            if (_overlayItem != null)
            {
                _overlayItem.Checked = wanted;
                _overlayItem.Enabled = allowed && !locked;
                _overlayItem.ToolTipText = locked
                    ? "Dikunci oleh orang tua" : (allowed ? "" : "Dimatikan oleh orang tua");
            }

            if (wanted)
            {
                if (_overlay == null || _overlay.IsDisposed)
                {
                    _overlay = new OverlayForm(_overlayPrefs);
                    _overlay.OpenRequested += delegate { ShowChild(); };
                    _overlay.HideRequested += delegate { CloseOverlay(); };
                    _overlay.FormClosed += delegate { _overlay = null; };
                    _overlay.Show();
                }
            }
            else
            {
                CloseOverlay();
            }
        }

        void CloseOverlay()
        {
            if (_overlay == null || _overlay.IsDisposed) { _overlay = null; return; }
            OverlayForm o = _overlay;
            _overlay = null;
            try { o.Close(); }
            catch { }
        }

        void ToggleOverlay()
        {
            _overlayPrefs.Hidden = !_overlayPrefs.Hidden;
            _overlayPrefs.Save();
            SyncOverlay(StatusReader.Read());
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
            // Laporan ini juga memberi tahu agent berapa lama tidak ada aktivitas,
            // supaya jatah pemakaian komputer tidak habis saat anak meninggalkan meja.
            string name = Native.ForegroundProcessName();
            IpcClient.Send("FOREGROUND", "", name, Native.IdleSeconds().ToString(), "");
        }

        void Poll()
        {
            Status s = StatusReader.Read();
            SyncOverlay(s);

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
                _sessionWarned.Clear();
            }

            int[] thresholds = ParseWarn(s.WarnMinutes);
            HandleSessionLimit(s, thresholds);
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

            if (s.SessionEnabled && s.SessionRemainingSeconds >= 0
                && (shortest == null || s.SessionRemainingSeconds < shortestLeft))
                _icon.Text = Trunc("Screen Time Guard\nKomputer: sisa "
                                   + Util.FormatClock(s.SessionRemainingSeconds));
            else if (shortest != null)
                _icon.Text = Trunc("Screen Time Guard\n" + shortest + ": sisa "
                                   + Util.FormatClock(shortestLeft));
            else if (s.Paused) _icon.Text = "Screen Time Guard - dijeda";
            else if (s.Bedtime) _icon.Text = "Screen Time Guard - jam tidur";
            else _icon.Text = "Screen Time Guard - aktif";
        }

        /// <summary>
        /// Peringatan dan penguncian layar untuk batas pemakaian komputer menyeluruh.
        /// </summary>
        void HandleSessionLimit(Status s, int[] thresholds)
        {
            if (!s.SessionEnabled) return;

            if (s.SessionLockRequested)
            {
                // Agent akan memutus sesi sendiri kalau proses ini tidak melakukannya,
                // jadi cukup sekali per beberapa detik supaya tidak berulang-ulang.
                if ((DateTime.UtcNow - _lastLockAtUtc).TotalSeconds < 10) return;
                _lastLockAtUtc = DateTime.UtcNow;

                Toast.Show("Waktu komputer habis",
                    "Jatah pemakaian komputer hari ini sudah habis. Layar akan dikunci sekarang.",
                    Theme.Bad, 6);
                Application.DoEvents();
                Native.LockScreen();
                return;
            }

            if (s.SessionRemainingSeconds < 0) return;

            for (int i = 0; i < thresholds.Length; i++)
            {
                int thresholdSec = thresholds[i] * 60;
                if (s.SessionRemainingSeconds > thresholdSec) continue;
                if (_sessionWarned.Contains(thresholds[i])) continue;
                for (int u = 0; u <= i; u++) _sessionWarned.Add(thresholds[u]);
                Toast.Show("Sisa waktu komputer",
                    "Tinggal " + Util.FormatDuration(s.SessionRemainingSeconds)
                    + " untuk semua kegiatan. Setelah habis layar akan dikunci.", Theme.Warn, 9);
                break;
            }
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
                CloseOverlay();
                _icon.Visible = false;
                _icon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
