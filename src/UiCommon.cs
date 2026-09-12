using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenTimeGuard
{
    public static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(24, 26, 32);
        public static readonly Color Card = Color.FromArgb(36, 39, 47);
        public static readonly Color CardAlt = Color.FromArgb(44, 48, 58);
        public static readonly Color Text = Color.FromArgb(238, 240, 245);
        public static readonly Color TextDim = Color.FromArgb(150, 157, 171);
        public static readonly Color Good = Color.FromArgb(64, 192, 120);
        public static readonly Color Warn = Color.FromArgb(235, 170, 60);
        public static readonly Color Bad = Color.FromArgb(226, 84, 84);
        public static readonly Color Accent = Color.FromArgb(86, 150, 240);

        public static readonly Font H1 = new Font("Segoe UI Semibold", 15f);
        public static readonly Font H2 = new Font("Segoe UI Semibold", 11f);
        public static readonly Font Body = new Font("Segoe UI", 9.5f);
        public static readonly Font BodyBold = new Font("Segoe UI Semibold", 9.5f);
        public static readonly Font Mono = new Font("Consolas", 14f, FontStyle.Bold);

        public static Color ForRatio(double remainingRatio, bool blocked)
        {
            if (blocked) return Bad;
            if (remainingRatio <= 0.10) return Bad;
            if (remainingRatio <= 0.30) return Warn;
            return Good;
        }

        public static void StyleButton(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(80, 86, 100);
            b.BackColor = primary ? Accent : CardAlt;
            b.ForeColor = primary ? Color.White : Text;
            b.Font = Body;
            b.Cursor = Cursors.Hand;
            b.Height = 30;
        }
    }

    /// <summary>
    /// Penata letak yang tahan penskalaan DPI. Tombol yang diposisikan dengan
    /// koordinat tetap akan terpotong di layar 125%/150%, jadi baris tombol
    /// selalu dibuat sebagai panel yang menempel di tepi dan ikut membesar.
    /// </summary>
    public static class UiLayout
    {
        /// <summary>Baris tombol rata kanan yang menempel di bawah jendela.</summary>
        public static FlowLayoutPanel BottomBar(params Control[] rightToLeft)
        {
            FlowLayoutPanel bar = NewBar(FlowDirection.RightToLeft);
            for (int i = 0; i < rightToLeft.Length; i++) bar.Controls.Add(Size(rightToLeft[i]));
            return bar;
        }

        /// <summary>Baris tombol rata kiri yang menempel di bawah jendela.</summary>
        public static FlowLayoutPanel BottomBarLeft(params Control[] leftToRight)
        {
            FlowLayoutPanel bar = NewBar(FlowDirection.LeftToRight);
            for (int i = 0; i < leftToRight.Length; i++) bar.Controls.Add(Size(leftToRight[i]));
            return bar;
        }

        static FlowLayoutPanel NewBar(FlowDirection direction)
        {
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.FlowDirection = direction;
            bar.AutoSize = true;
            bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bar.WrapContents = true;
            bar.Padding = new Padding(8, 6, 8, 6);
            return bar;
        }

        static Control Size(Control c)
        {
            c.AutoSize = true;
            c.Margin = new Padding(4, 3, 4, 3);
            Button b = c as Button;
            if (b != null)
            {
                b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                b.MinimumSize = new System.Drawing.Size(92, 30);
                b.Padding = new Padding(10, 0, 10, 0);
            }
            return c;
        }

        /// <summary>Wadah isi yang bisa digulir, supaya tidak ada kontrol yang tak terjangkau.</summary>
        public static Panel ScrollHost()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            p.AutoScroll = true;
            p.Padding = new Padding(14, 12, 14, 4);
            return p;
        }
    }

    public static class Native
    {
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;

        /// <summary>
        /// Menegakkan kembali status "selalu di atas" tanpa merebut fokus.
        /// Aplikasi layar penuh kadang merebut posisi teratas.
        /// </summary>
        public static void ReassertTopMost(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try { SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); }
            catch { }
        }

        public static string ForegroundProcessName()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return "";
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == 0) return "";
                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById((int)pid))
                    return p.ProcessName.ToLowerInvariant();
            }
            catch { return ""; }
        }
    }

    public static class IconFactory
    {
        static Icon _cached;

        /// <summary>Ikon jam sederhana, dibuat saat runtime agar tidak perlu file .ico.</summary>
        public static Icon TrayIcon()
        {
            if (_cached != null) return _cached;
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush face = new SolidBrush(Color.FromArgb(86, 150, 240)))
                        g.FillEllipse(face, 2, 2, 28, 28);
                    using (Pen rim = new Pen(Color.White, 2f))
                        g.DrawEllipse(rim, 3, 3, 26, 26);
                    using (Pen hand = new Pen(Color.White, 2.6f))
                    {
                        g.DrawLine(hand, 16, 16, 16, 8);
                        g.DrawLine(hand, 16, 16, 22, 19);
                    }
                }
                _cached = Icon.FromHandle(bmp.GetHicon());
            }
            return _cached;
        }
    }

    /// <summary>
    /// Notifikasi kecil di pojok kanan bawah yang tidak merebut fokus,
    /// supaya tidak mengganggu permainan yang sedang berjalan.
    /// </summary>
    public class Toast : Form
    {
        const int WS_EX_TOPMOST = 0x00000008;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;

        readonly Timer _timer = new Timer();
        readonly Color _accent;

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

        Toast(string title, string message, Color accent, int seconds)
        {
            _accent = accent;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Card;
            Width = 380;
            Height = 108;
            TopMost = true;

            Label lblTitle = new Label();
            lblTitle.Text = title;
            lblTitle.Font = Theme.H2;
            lblTitle.ForeColor = accent;
            lblTitle.AutoSize = false;
            lblTitle.SetBounds(18, 14, Width - 36, 22);

            Label lblBody = new Label();
            lblBody.Text = message;
            lblBody.Font = Theme.Body;
            lblBody.ForeColor = Theme.Text;
            lblBody.AutoSize = false;
            lblBody.SetBounds(18, 40, Width - 36, 56);

            Controls.Add(lblTitle);
            Controls.Add(lblBody);

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);

            _timer.Interval = Math.Max(2, seconds) * 1000;
            _timer.Tick += delegate { _timer.Stop(); Close(); };
            _timer.Start();

            Click += delegate { Close(); };
            lblTitle.Click += delegate { Close(); };
            lblBody.Click += delegate { Close(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (SolidBrush bar = new SolidBrush(_accent))
                e.Graphics.FillRectangle(bar, 0, 0, 5, Height);
            using (Pen border = new Pen(Color.FromArgb(70, 76, 90)))
                e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Dispose();
            base.OnFormClosed(e);
        }

        public static void Show(string title, string message, Color accent, int seconds)
        {
            try
            {
                Toast t = new Toast(title, message, accent, seconds);
                t.Show();
            }
            catch { }
        }
    }

    /// <summary>Kotak dialog input satu baris bergaya gelap (pengganti InputBox).</summary>
    public class PromptForm : Form
    {
        readonly TextBox _input = new TextBox();

        public string Value { get { return _input.Text; } }

        public PromptForm(string title, string label, string initial, bool password)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            ClientSize = new Size(400, 160);

            Panel host = UiLayout.ScrollHost();
            host.BackColor = Theme.Bg;

            Label lbl = new Label();
            lbl.Text = label;
            lbl.ForeColor = Theme.Text;
            lbl.AutoSize = false;
            lbl.SetBounds(0, 4, 350, 22);

            _input.SetBounds(0, 30, 350, 26);
            _input.BackColor = Theme.CardAlt;
            _input.ForeColor = Theme.Text;
            _input.BorderStyle = BorderStyle.FixedSingle;
            _input.Text = initial == null ? "" : initial;
            if (password) _input.UseSystemPasswordChar = true;

            host.Controls.Add(lbl);
            host.Controls.Add(_input);

            Button ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            Theme.StyleButton(ok, true);

            Button cancel = new Button();
            cancel.Text = "Batal";
            cancel.DialogResult = DialogResult.Cancel;
            Theme.StyleButton(cancel, false);

            FlowLayoutPanel bar = UiLayout.BottomBar(cancel, ok);
            bar.BackColor = Theme.Bg;

            Controls.Add(host);
            Controls.Add(bar);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public static string Ask(IWin32Window owner, string title, string label, string initial, bool password)
        {
            using (PromptForm f = new PromptForm(title, label, initial, password))
                return f.ShowDialog(owner) == DialogResult.OK ? f.Value : null;
        }
    }

    public static class Msg
    {
        public static void Info(IWin32Window owner, string text)
        {
            MessageBox.Show(owner, text, "Screen Time Guard",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static void Error(IWin32Window owner, string text)
        {
            MessageBox.Show(owner, text, "Screen Time Guard",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public static bool Confirm(IWin32Window owner, string text)
        {
            return MessageBox.Show(owner, text, "Screen Time Guard",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }
    }
}
