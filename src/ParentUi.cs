using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ScreenTimeGuard
{
    /// <summary>Dialog password; mengembalikan password yang sudah diverifikasi agent, atau null.</summary>
    public class LoginForm : Form
    {
        readonly TextBox _password = new TextBox();
        readonly Label _error = new Label();
        string _verified;

        public LoginForm()
        {
            Text = "Panel Orang Tua";
            Icon = IconFactory.TrayIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            ClientSize = new Size(400, 200);

            Panel host = UiLayout.ScrollHost();
            host.BackColor = Theme.Bg;

            Label title = new Label();
            title.Text = "Masukkan password orang tua";
            title.Font = Theme.H2;
            title.ForeColor = Theme.Text;
            title.AutoSize = false;
            title.SetBounds(0, 4, 350, 26);

            _password.SetBounds(0, 34, 350, 26);
            _password.BackColor = Theme.CardAlt;
            _password.ForeColor = Theme.Text;
            _password.BorderStyle = BorderStyle.FixedSingle;
            _password.UseSystemPasswordChar = true;

            _error.ForeColor = Theme.Bad;
            _error.AutoSize = false;
            _error.SetBounds(0, 66, 350, 38);

            host.Controls.Add(title);
            host.Controls.Add(_password);
            host.Controls.Add(_error);

            Button ok = new Button();
            ok.Text = "Buka";
            Theme.StyleButton(ok, true);
            ok.Click += delegate { TryLogin(ok); };

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

        void TryLogin(Button ok)
        {
            ok.Enabled = false;
            _error.Text = "Memeriksa...";
            _error.ForeColor = Theme.TextDim;
            Application.DoEvents();

            IpcResponse r = IpcClient.Send("VERIFY", _password.Text, "", "", "");
            ok.Enabled = true;

            if (r == null)
            {
                _error.ForeColor = Theme.Bad;
                _error.Text = "Agent tidak merespons. Pastikan layanan berjalan.";
                return;
            }
            if (!r.Ok)
            {
                _error.ForeColor = Theme.Bad;
                _error.Text = r.Error;
                _password.SelectAll();
                _password.Focus();
                return;
            }
            _verified = _password.Text;
            DialogResult = DialogResult.OK;
            Close();
        }

        public static string Ask()
        {
            using (LoginForm f = new LoginForm())
                return f.ShowDialog() == DialogResult.OK ? f._verified : null;
        }
    }

    /// <summary>Pemilih proses yang sedang berjalan, supaya orang tua tidak perlu mengetik nama exe.</summary>
    public class ProcessPickerForm : Form
    {
        readonly ListView _list = new ListView();
        public string Selected { get; private set; }

        public ProcessPickerForm()
        {
            Text = "Pilih aplikasi yang sedang berjalan";
            Icon = IconFactory.TrayIcon();
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 420);
            MinimizeBox = false;

            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.Dock = DockStyle.Fill;
            _list.Columns.Add("Judul jendela", 260);
            _list.Columns.Add("Proses", 160);
            _list.DoubleClick += delegate { Choose(); };

            Button refresh = new Button();
            refresh.Text = "Muat ulang";
            refresh.Click += delegate { Populate(); };

            Button ok = new Button();
            ok.Text = "Pilih";
            ok.Click += delegate { Choose(); };

            Button cancel = new Button();
            cancel.Text = "Batal";
            cancel.DialogResult = DialogResult.Cancel;

            Controls.Add(_list);
            Controls.Add(UiLayout.BottomBar(cancel, ok));
            Controls.Add(UiLayout.BottomBarLeft(refresh));
            AcceptButton = ok;
            CancelButton = cancel;

            Populate();
        }

        void Populate()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            Dictionary<string, string> seen = new Dictionary<string, string>();
            Process[] all = Process.GetProcesses();
            for (int i = 0; i < all.Length; i++)
            {
                try
                {
                    string name = all[i].ProcessName.ToLowerInvariant();
                    if (Util.IsProtectedProcess(name)) continue;
                    string title = all[i].MainWindowTitle;
                    if (string.IsNullOrEmpty(title)) continue;   // hanya yang punya jendela
                    if (seen.ContainsKey(name)) continue;
                    seen[name] = title;
                }
                catch { }
                finally { try { all[i].Dispose(); } catch { } }
            }
            foreach (KeyValuePair<string, string> kv in seen)
            {
                ListViewItem it = new ListViewItem(kv.Value);
                it.SubItems.Add(kv.Key + ".exe");
                it.Tag = kv.Key;
                _list.Items.Add(it);
            }
            _list.EndUpdate();
        }

        void Choose()
        {
            if (_list.SelectedItems.Count == 0) return;
            Selected = (string)_list.SelectedItems[0].Tag;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>Dialog tambah/ubah batasan satu aplikasi.</summary>
    public class AppEditForm : Form
    {
        readonly TextBox _name = new TextBox();
        readonly TextBox _process = new TextBox();
        readonly NumericUpDown _weekday = new NumericUpDown();
        readonly NumericUpDown _weekend = new NumericUpDown();
        readonly CheckBox _weekdayUnlimited = new CheckBox();
        readonly CheckBox _weekendUnlimited = new CheckBox();
        readonly ComboBox _mode = new ComboBox();
        readonly CheckBox _enabled = new CheckBox();
        readonly CheckBox _total = new CheckBox();

        public AppLimit Result { get; private set; }

        public AppEditForm(AppLimit existing)
        {
            Text = existing == null ? "Tambah aplikasi" : "Ubah aplikasi";
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            Icon = IconFactory.TrayIcon();
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(500, 420);
            MinimumSize = new Size(430, 300);

            // Isi diletakkan di panel yang bisa digulir dan tombol di panel yang
            // menempel di bawah, supaya tombol tidak pernah terpotong di layar
            // dengan penskalaan 125% / 150%.
            Panel host = UiLayout.ScrollHost();

            int y = 4;
            host.Controls.Add(Lbl("Nama tampilan (bebas, dilihat anak)", 0, y)); y += 20;
            _name.SetBounds(0, y, 438, 24); host.Controls.Add(_name); y += 34;

            host.Controls.Add(Lbl("Nama proses (file .exe)", 0, y)); y += 20;
            _process.SetBounds(0, y, 240, 24); host.Controls.Add(_process);

            Button pick = new Button();
            pick.Text = "Dari yang berjalan...";
            pick.SetBounds(248, y - 2, 140, 28);
            pick.Click += delegate
            {
                using (ProcessPickerForm p = new ProcessPickerForm())
                    if (p.ShowDialog(this) == DialogResult.OK) _process.Text = p.Selected + ".exe";
            };
            host.Controls.Add(pick);

            Button browse = new Button();
            browse.Text = "Cari...";
            browse.SetBounds(394, y - 2, 64, 28);
            browse.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = "Aplikasi (*.exe)|*.exe";
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        _process.Text = Path.GetFileName(d.FileName);
                        if (_name.Text.Trim().Length == 0)
                            _name.Text = Path.GetFileNameWithoutExtension(d.FileName);
                    }
                }
            };
            host.Controls.Add(browse);
            y += 38;

            host.Controls.Add(Lbl("Jatah hari sekolah (Senin-Jumat), dalam menit", 0, y)); y += 20;
            _weekday.SetBounds(0, y, 90, 24);
            _weekday.Maximum = 1440;
            host.Controls.Add(_weekday);
            _weekdayUnlimited.Text = "Tanpa batas";
            _weekdayUnlimited.SetBounds(102, y + 2, 120, 22);
            _weekdayUnlimited.CheckedChanged += delegate { _weekday.Enabled = !_weekdayUnlimited.Checked; };
            host.Controls.Add(_weekdayUnlimited);
            y += 34;

            host.Controls.Add(Lbl("Jatah akhir pekan (Sabtu-Minggu), dalam menit", 0, y)); y += 20;
            _weekend.SetBounds(0, y, 90, 24);
            _weekend.Maximum = 1440;
            host.Controls.Add(_weekend);
            _weekendUnlimited.Text = "Tanpa batas";
            _weekendUnlimited.SetBounds(102, y + 2, 120, 22);
            _weekendUnlimited.CheckedChanged += delegate { _weekend.Enabled = !_weekendUnlimited.Checked; };
            host.Controls.Add(_weekendUnlimited);
            y += 36;

            host.Controls.Add(Lbl("Cara menghitung waktu", 0, y)); y += 20;
            _mode.SetBounds(0, y, 458, 24);
            _mode.DropDownStyle = ComboBoxStyle.DropDownList;
            _mode.Items.Add("Selama aplikasi terbuka (disarankan untuk game)");
            _mode.Items.Add("Hanya saat jendela aplikasi sedang dipakai");
            host.Controls.Add(_mode);
            y += 36;

            _enabled.Text = "Berlakukan pembatasan (jika dimatikan, hanya dicatat saja)";
            _enabled.AutoSize = true;
            _enabled.Location = new Point(0, y);
            host.Controls.Add(_enabled);
            y += 28;

            _total.Text = "Hitung juga ke dalam batas total waktu layar harian";
            _total.AutoSize = true;
            _total.Location = new Point(0, y);
            host.Controls.Add(_total);

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

            if (existing == null)
            {
                _weekday.Value = 60;
                _weekend.Value = 120;
                _mode.SelectedIndex = 0;
                _enabled.Checked = true;
                _total.Checked = true;
            }
            else
            {
                _name.Text = existing.Name;
                _process.Text = existing.Process + ".exe";
                _weekdayUnlimited.Checked = existing.WeekdayMinutes < 0;
                _weekendUnlimited.Checked = existing.WeekendMinutes < 0;
                _weekday.Value = existing.WeekdayMinutes < 0 ? 60 : Math.Min(1440, existing.WeekdayMinutes);
                _weekend.Value = existing.WeekendMinutes < 0 ? 120 : Math.Min(1440, existing.WeekendMinutes);
                _mode.SelectedIndex = existing.CountMode == "active" ? 1 : 0;
                _enabled.Checked = existing.Enabled;
                _total.Checked = existing.CountsTowardTotal;
            }
        }

        static Label Lbl(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;           // ikut membesar kalau font sistem besar
            l.Location = new Point(x, y);
            return l;
        }

        void Save()
        {
            string process = Util.NormalizeProcessName(_process.Text);
            if (process.Length == 0)
            {
                Msg.Error(this, "Nama proses tidak boleh kosong.");
                return;
            }
            if (Util.IsProtectedProcess(process))
            {
                Msg.Error(this, "\"" + process + ".exe\" adalah bagian penting dari Windows "
                                + "dan tidak boleh dibatasi.");
                return;
            }

            AppLimit a = new AppLimit();
            a.Process = process;
            a.Name = _name.Text.Trim().Length == 0 ? process : _name.Text.Trim();
            a.WeekdayMinutes = _weekdayUnlimited.Checked ? -1 : (int)_weekday.Value;
            a.WeekendMinutes = _weekendUnlimited.Checked ? -1 : (int)_weekend.Value;
            a.CountMode = _mode.SelectedIndex == 1 ? "active" : "running";
            a.Enabled = _enabled.Checked;
            a.CountsTowardTotal = _total.Checked;

            Result = a;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>Jendela pengaturan untuk orang tua.</summary>
    public class ParentForm : Form
    {
        readonly string _password;
        Settings _settings;

        readonly ListView _apps = new ListView();
        readonly ListView _today = new ListView();
        readonly TextBox _history = new TextBox();
        readonly System.Windows.Forms.Timer _refresh = new System.Windows.Forms.Timer();

        NumericUpDown _resetHour, _grace, _totalWeekday, _totalWeekend, _pauseMinutes;
        CheckBox _totalWeekdayOff, _totalWeekendOff, _bedtimeEnabled, _autoCheck;
        CheckBox _overlayEnabled, _overlayLocked;
        TextBox _warnMinutes, _bedtimeStart, _bedtimeEnd, _updateNotes, _updateUrl, _updateKey;
        Label _todaySummary, _versionLabel, _updateStatus;
        Button _checkButton, _installButton;
        string _latestVersion = "";

        public ParentForm(string password)
        {
            _password = password;

            Text = "Screen Time Guard - Panel Orang Tua";
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            Icon = IconFactory.TrayIcon();
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 560);
            MinimumSize = new Size(700, 500);
            Font = new Font("Segoe UI", 9f);

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.TabPages.Add(BuildAppsTab());
            tabs.TabPages.Add(BuildRulesTab());
            tabs.TabPages.Add(BuildTodayTab());
            tabs.TabPages.Add(BuildSecurityTab());
            tabs.TabPages.Add(BuildHistoryTab());
            tabs.TabPages.Add(BuildUpdateTab());
            Controls.Add(tabs);

            if (!LoadSettings()) { BeginInvoke((MethodInvoker)Close); return; }
            BindRules();
            RefreshAppList();
            RefreshToday();

            _refresh.Interval = 3000;
            _refresh.Tick += delegate { RefreshToday(); };
            _refresh.Start();
        }

        // ------------------------------------------------------------ tab: apps

        TabPage BuildAppsTab()
        {
            TabPage page = new TabPage("Aplikasi");
            page.Padding = new Padding(10);

            _apps.View = View.Details;
            _apps.FullRowSelect = true;
            _apps.HideSelection = false;
            _apps.Dock = DockStyle.Fill;
            _apps.Columns.Add("Aplikasi", 170);
            _apps.Columns.Add("Proses", 150);
            _apps.Columns.Add("Hari sekolah", 100);
            _apps.Columns.Add("Akhir pekan", 100);
            _apps.Columns.Add("Perhitungan", 130);
            _apps.Columns.Add("Aktif", 60);
            _apps.DoubleClick += delegate { EditApp(); };

            Button add = MakeButton("Tambah", delegate { AddApp(); });
            Button edit = MakeButton("Ubah", delegate { EditApp(); });
            Button del = MakeButton("Hapus", delegate { DeleteApp(); });
            Button save = MakeButton("Simpan perubahan", delegate { SaveSettings(); });

            FlowLayoutPanel bar = UiLayout.BottomBarLeft(add, edit, del, save);

            Label hint = new Label();
            hint.Text = "Jatah 0 menit berarti aplikasi tidak boleh dibuka sama sekali. "
                        + "Klik dua kali baris untuk mengubah.";
            hint.Dock = DockStyle.Top;
            hint.Height = 34;
            hint.ForeColor = SystemColors.GrayText;

            page.Controls.Add(_apps);
            page.Controls.Add(hint);
            page.Controls.Add(bar);
            return page;
        }

        static Button MakeButton(string text, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(92, 30);
            b.Click += onClick;
            return b;
        }

        void RefreshAppList()
        {
            _apps.BeginUpdate();
            _apps.Items.Clear();
            for (int i = 0; i < _settings.Apps.Count; i++)
            {
                AppLimit a = _settings.Apps[i];
                ListViewItem it = new ListViewItem(a.Name);
                it.SubItems.Add(a.Process + ".exe");
                it.SubItems.Add(a.WeekdayMinutes < 0 ? "tanpa batas" : a.WeekdayMinutes + " menit");
                it.SubItems.Add(a.WeekendMinutes < 0 ? "tanpa batas" : a.WeekendMinutes + " menit");
                it.SubItems.Add(a.CountMode == "active" ? "saat dipakai" : "selama terbuka");
                it.SubItems.Add(a.Enabled ? "ya" : "tidak");
                it.Tag = a;
                _apps.Items.Add(it);
            }
            _apps.EndUpdate();
        }

        void AddApp()
        {
            using (AppEditForm f = new AppEditForm(null))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                if (_settings.Find(f.Result.Process) != null)
                {
                    Msg.Error(this, "Aplikasi dengan proses itu sudah ada di daftar.");
                    return;
                }
                _settings.Apps.Add(f.Result);
                RefreshAppList();
            }
        }

        void EditApp()
        {
            if (_apps.SelectedItems.Count == 0) return;
            AppLimit current = (AppLimit)_apps.SelectedItems[0].Tag;
            using (AppEditForm f = new AppEditForm(current))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                int index = _settings.Apps.IndexOf(current);
                _settings.Apps[index] = f.Result;
                RefreshAppList();
            }
        }

        void DeleteApp()
        {
            if (_apps.SelectedItems.Count == 0) return;
            AppLimit current = (AppLimit)_apps.SelectedItems[0].Tag;
            if (!Msg.Confirm(this, "Hapus batasan untuk \"" + current.Name + "\"?")) return;
            _settings.Apps.Remove(current);
            RefreshAppList();
        }

        // ----------------------------------------------------------- tab: rules

        TabPage BuildRulesTab()
        {
            TabPage page = new TabPage("Aturan umum");
            page.Padding = new Padding(14);
            page.AutoScroll = true;

            int y = 10;

            page.Controls.Add(Section("Reset harian", 0, ref y));
            page.Controls.Add(Field("Jatah direset tiap pukul", 0, y));
            _resetHour = Num(200, y, 0, 23, 4);
            page.Controls.Add(_resetHour);
            page.Controls.Add(Hint("Disarankan pukul 04:00 supaya bermain lewat tengah malam "
                                  + "tetap terhitung hari yang sama.", 260, y + 3));
            y += 38;

            page.Controls.Add(Section("Peringatan & penutupan", 0, ref y));
            page.Controls.Add(Field("Peringatan pada sisa (menit)", 0, y));
            _warnMinutes = Txt(200, y, 120, "15,5,1");
            page.Controls.Add(_warnMinutes);
            page.Controls.Add(Hint("Pisahkan dengan koma.", 330, y + 3));
            y += 32;

            page.Controls.Add(Field("Tenggang sebelum ditutup (detik)", 0, y));
            _grace = Num(200, y, 0, 900, 60);
            page.Controls.Add(_grace);
            page.Controls.Add(Hint("Waktu untuk menyimpan permainan sebelum aplikasi ditutup paksa.",
                                   260, y + 3));
            y += 38;

            page.Controls.Add(Section("Batas total waktu layar (semua aplikasi)", 0, ref y));
            page.Controls.Add(Field("Hari sekolah (menit)", 0, y));
            _totalWeekday = Num(200, y, 0, 1440, 120);
            page.Controls.Add(_totalWeekday);
            _totalWeekdayOff = Check("Tanpa batas", 270, y + 2);
            _totalWeekdayOff.CheckedChanged += delegate { _totalWeekday.Enabled = !_totalWeekdayOff.Checked; };
            page.Controls.Add(_totalWeekdayOff);
            y += 32;

            page.Controls.Add(Field("Akhir pekan (menit)", 0, y));
            _totalWeekend = Num(200, y, 0, 1440, 180);
            page.Controls.Add(_totalWeekend);
            _totalWeekendOff = Check("Tanpa batas", 270, y + 2);
            _totalWeekendOff.CheckedChanged += delegate { _totalWeekend.Enabled = !_totalWeekendOff.Checked; };
            page.Controls.Add(_totalWeekendOff);
            y += 38;

            page.Controls.Add(Section("Penghitung melayang di layar anak", 0, ref y));
            _overlayEnabled = Check("Tampilkan penghitung sisa waktu yang selalu di atas", 0, y);
            _overlayEnabled.Width = 460;
            page.Controls.Add(_overlayEnabled);
            y += 26;

            _overlayLocked = Check("Anak tidak boleh menyembunyikannya", 0, y);
            _overlayLocked.Width = 460;
            _overlayEnabled.CheckedChanged += delegate { _overlayLocked.Enabled = _overlayEnabled.Checked; };
            page.Controls.Add(_overlayLocked);
            y += 22;

            page.Controls.Add(Hint("Anak bisa menggeser posisinya, dan klik kanan untuk memperkecil "
                                   + "atau membuka jendela lengkap.", 0, y));
            y += 40;

            page.Controls.Add(Section("Jam tidur", 0, ref y));
            _bedtimeEnabled = Check("Blokir semua aplikasi yang diawasi pada jam tidur", 0, y);
            _bedtimeEnabled.Width = 400;
            page.Controls.Add(_bedtimeEnabled);
            y += 28;

            page.Controls.Add(Field("Dari pukul", 0, y));
            _bedtimeStart = Txt(200, y, 70, "21:00");
            page.Controls.Add(_bedtimeStart);
            page.Controls.Add(Field("sampai pukul", 285, y));
            _bedtimeEnd = Txt(380, y, 70, "06:00");
            page.Controls.Add(_bedtimeEnd);
            y += 44;

            Button save = new Button();
            save.Text = "Simpan perubahan";
            save.SetBounds(0, y, 160, 32);
            save.Click += delegate { SaveSettings(); };
            page.Controls.Add(save);

            return page;
        }

        static Label Section(string text, int x, ref int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Segoe UI Semibold", 10f);
            l.SetBounds(x, y, 480, 22);
            y += 28;
            return l;
        }

        static Label Field(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y + 4, 195, 20);
            return l;
        }

        static Label Hint(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = SystemColors.GrayText;
            l.SetBounds(x, y, 380, 34);
            return l;
        }

        static NumericUpDown Num(int x, int y, int min, int max, int value)
        {
            NumericUpDown n = new NumericUpDown();
            n.SetBounds(x, y, 60, 24);
            n.Minimum = min;
            n.Maximum = max;
            n.Value = value;
            return n;
        }

        static TextBox Txt(int x, int y, int w, string value)
        {
            TextBox t = new TextBox();
            t.SetBounds(x, y, w, 24);
            t.Text = value;
            return t;
        }

        static CheckBox Check(string text, int x, int y)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.SetBounds(x, y, 140, 22);
            return c;
        }

        void BindRules()
        {
            _resetHour.Value = Math.Max(0, Math.Min(23, _settings.ResetHour));
            _warnMinutes.Text = _settings.WarnMinutes;
            _grace.Value = Math.Max(0, Math.Min(900, _settings.GraceSeconds));

            _totalWeekdayOff.Checked = _settings.TotalWeekdayMinutes < 0;
            _totalWeekday.Value = _settings.TotalWeekdayMinutes < 0
                ? 120 : Math.Min(1440, _settings.TotalWeekdayMinutes);
            _totalWeekday.Enabled = !_totalWeekdayOff.Checked;

            _totalWeekendOff.Checked = _settings.TotalWeekendMinutes < 0;
            _totalWeekend.Value = _settings.TotalWeekendMinutes < 0
                ? 180 : Math.Min(1440, _settings.TotalWeekendMinutes);
            _totalWeekend.Enabled = !_totalWeekendOff.Checked;

            _overlayEnabled.Checked = _settings.OverlayEnabled;
            _overlayLocked.Checked = _settings.OverlayLocked;
            _overlayLocked.Enabled = _overlayEnabled.Checked;

            _bedtimeEnabled.Checked = _settings.BedtimeEnabled;
            _bedtimeStart.Text = _settings.BedtimeStart;
            _bedtimeEnd.Text = _settings.BedtimeEnd;

            _updateUrl.Text = string.IsNullOrEmpty(_settings.UpdateUrl)
                ? AppInfo.DefaultUpdateUrl : _settings.UpdateUrl;
            _autoCheck.Checked = _settings.AutoCheckUpdate;
            _updateKey.Text = _settings.UpdatePublicKey == null ? "" : _settings.UpdatePublicKey;
            RefreshVersionLabel();
        }

        void CollectRules()
        {
            _settings.ResetHour = (int)_resetHour.Value;
            _settings.WarnMinutes = _warnMinutes.Text.Trim();
            _settings.GraceSeconds = (int)_grace.Value;
            _settings.TotalWeekdayMinutes = _totalWeekdayOff.Checked ? -1 : (int)_totalWeekday.Value;
            _settings.TotalWeekendMinutes = _totalWeekendOff.Checked ? -1 : (int)_totalWeekend.Value;
            _settings.OverlayEnabled = _overlayEnabled.Checked;
            _settings.OverlayLocked = _overlayLocked.Checked;
            _settings.BedtimeEnabled = _bedtimeEnabled.Checked;
            _settings.BedtimeStart = _bedtimeStart.Text.Trim();
            _settings.BedtimeEnd = _bedtimeEnd.Text.Trim();
            _settings.UpdateUrl = _updateUrl.Text.Trim();
            _settings.AutoCheckUpdate = _autoCheck.Checked;
            _settings.UpdatePublicKey = _updateKey.Text.Trim();
        }

        // ----------------------------------------------------------- tab: today

        TabPage BuildTodayTab()
        {
            TabPage page = new TabPage("Hari ini");
            page.Padding = new Padding(10);

            _today.View = View.Details;
            _today.FullRowSelect = true;
            _today.HideSelection = false;
            _today.Dock = DockStyle.Fill;
            _today.Columns.Add("Aplikasi", 180);
            _today.Columns.Add("Terpakai", 110);
            _today.Columns.Add("Jatah", 110);
            _today.Columns.Add("Sisa", 110);
            _today.Columns.Add("Bonus", 80);
            _today.Columns.Add("Status", 160);

            _todaySummary = new Label();
            _todaySummary.Dock = DockStyle.Top;
            _todaySummary.Height = 40;
            _todaySummary.ForeColor = SystemColors.GrayText;

            FlowLayoutPanel bonusBar = UiLayout.BottomBarLeft(
                MakeButton("+15 menit", delegate { Bonus(15); }),
                MakeButton("+30 menit", delegate { Bonus(30); }),
                MakeButton("-15 menit", delegate { Bonus(-15); }),
                MakeButton("Bonus lain...", delegate { BonusCustom(); }),
                MakeButton("Reset pemakaian", delegate { ResetUsage(); }));

            Label pauseLbl = new Label();
            pauseLbl.Text = "Jeda seluruh pengawasan selama";
            pauseLbl.AutoSize = true;
            pauseLbl.Padding = new Padding(0, 7, 0, 0);

            _pauseMinutes = new NumericUpDown();
            _pauseMinutes.Minimum = 1;
            _pauseMinutes.Maximum = 720;
            _pauseMinutes.Value = 30;
            _pauseMinutes.Width = 66;

            FlowLayoutPanel pauseBar = UiLayout.BottomBarLeft(
                pauseLbl,
                _pauseMinutes,
                MakeButton("Jeda", delegate { Pause((int)_pauseMinutes.Value); }),
                MakeButton("Batalkan jeda", delegate { Pause(0); }));

            page.Controls.Add(_today);
            page.Controls.Add(_todaySummary);
            page.Controls.Add(bonusBar);
            page.Controls.Add(pauseBar);
            return page;
        }

        void RefreshToday()
        {
            Status s = StatusReader.Read();
            if (_versionLabel != null) RefreshVersionLabel();
            if (!StatusReader.IsFresh(s))
            {
                _todaySummary.Text = "Agent tidak merespons - data tidak diperbarui.";
                return;
            }

            _todaySummary.Text = "Hari " + s.Day + " (" + (s.IsWeekend ? "akhir pekan" : "hari sekolah")
                + ")  •  reset pukul " + s.ResetsAtText
                + (s.TotalLimitSeconds >= 0
                    ? "\nTotal terpakai " + Util.FormatDuration(s.TotalUsedSeconds)
                      + " dari " + Util.FormatDuration(s.TotalLimitSeconds)
                    : "\nTotal terpakai " + Util.FormatDuration(s.TotalUsedSeconds) + " (tanpa batas total)")
                + (s.Paused ? "  •  DIJEDA " + Util.FormatDuration(s.PauseLeftSeconds) + " lagi" : "")
                + (s.Bedtime ? "  •  JAM TIDUR" : "");

            string selected = _today.SelectedItems.Count > 0
                ? (string)_today.SelectedItems[0].Tag : null;

            _today.BeginUpdate();
            _today.Items.Clear();
            for (int i = 0; i < s.Apps.Count; i++)
            {
                StatusApp a = s.Apps[i];
                ListViewItem it = new ListViewItem(a.Name);
                it.SubItems.Add(Util.FormatDuration(a.UsedSeconds));
                it.SubItems.Add(a.LimitSeconds < 0 ? "tanpa batas" : Util.FormatDuration(a.LimitSeconds));
                it.SubItems.Add(a.RemainingSeconds < 0 ? "∞" : Util.FormatClock(a.RemainingSeconds));
                it.SubItems.Add(a.BonusMinutes == 0 ? "-" : a.BonusMinutes + " mnt");
                it.SubItems.Add(a.Blocked ? a.BlockReason : (a.Running ? "sedang berjalan" : "-"));
                it.Tag = a.Process;
                if (a.Blocked) it.ForeColor = Color.Firebrick;
                else if (a.Running) it.ForeColor = Color.DarkGreen;
                _today.Items.Add(it);
                if (selected != null && selected == a.Process) it.Selected = true;
            }
            _today.EndUpdate();
        }

        string SelectedProcess()
        {
            if (_today.SelectedItems.Count == 0)
            {
                Msg.Error(this, "Pilih dulu aplikasi di daftar.");
                return null;
            }
            return (string)_today.SelectedItems[0].Tag;
        }

        void Bonus(int minutes)
        {
            string p = SelectedProcess();
            if (p == null) return;
            Call("BONUS", p, minutes.ToString(), "");
            RefreshToday();
        }

        void BonusCustom()
        {
            string p = SelectedProcess();
            if (p == null) return;
            string value = PromptForm.Ask(this, "Bonus waktu",
                "Tambahan menit untuk hari ini (boleh negatif):", "45", false);
            if (value == null) return;
            int minutes;
            if (!int.TryParse(value.Trim(), out minutes))
            {
                Msg.Error(this, "Masukkan angka, contoh: 45 atau -20.");
                return;
            }
            Call("BONUS", p, minutes.ToString(), "");
            RefreshToday();
        }

        void ResetUsage()
        {
            if (_today.SelectedItems.Count == 0)
            {
                if (!Msg.Confirm(this, "Reset pemakaian SEMUA aplikasi hari ini menjadi nol?")) return;
                Call("RESETUSAGE", "ALL", "", "");
            }
            else
            {
                string p = (string)_today.SelectedItems[0].Tag;
                if (!Msg.Confirm(this, "Reset pemakaian \"" + p + ".exe\" hari ini menjadi nol?")) return;
                Call("RESETUSAGE", p, "", "");
            }
            RefreshToday();
        }

        void Pause(int minutes)
        {
            Call("PAUSE", minutes.ToString(), "", "");
            RefreshToday();
        }

        // -------------------------------------------------------- tab: security

        TabPage BuildSecurityTab()
        {
            TabPage page = new TabPage("Keamanan");
            page.Padding = new Padding(14);
            page.AutoScroll = true;

            Label l = new Label();
            l.Text = "Ganti password orang tua";
            l.Font = new Font("Segoe UI Semibold", 10f);
            l.SetBounds(0, 10, 400, 22);

            TextBox p1 = new TextBox();
            p1.SetBounds(0, 44, 260, 24);
            p1.UseSystemPasswordChar = true;

            TextBox p2 = new TextBox();
            p2.SetBounds(0, 100, 260, 24);
            p2.UseSystemPasswordChar = true;

            Label l1 = new Label();
            l1.Text = "Password baru (minimal 4 karakter)";
            l1.SetBounds(0, 24, 300, 18);

            Label l2 = new Label();
            l2.Text = "Ulangi password baru";
            l2.SetBounds(0, 80, 300, 18);

            Button change = new Button();
            change.Text = "Ganti password";
            change.SetBounds(0, 136, 140, 30);
            change.Click += delegate
            {
                if (p1.Text.Length < 4) { Msg.Error(this, "Password minimal 4 karakter."); return; }
                if (p1.Text != p2.Text) { Msg.Error(this, "Kedua password tidak sama."); return; }
                if (Call("SETPASSWORD", p1.Text, "", ""))
                {
                    p1.Clear();
                    p2.Clear();
                    Msg.Info(this, "Password diganti. Password baru dipakai saat membuka panel berikutnya.");
                }
            };

            Label info = new Label();
            info.Text = "Catatan keamanan:\n"
                + "• Agar anak tidak bisa mematikan pengawas, pastikan akun Windows anak adalah "
                + "akun Standard (bukan Administrator).\n"
                + "• Semua perubahan pengaturan, penutupan aplikasi, dan percobaan password salah "
                + "dicatat di log.txt.\n"
                + "• Jika password hilang, hapus baris PasswordHash/PasswordSalt di settings.json "
                + "sebagai Administrator, lalu jalankan ulang install.ps1.";
            info.SetBounds(0, 190, 700, 140);
            info.ForeColor = SystemColors.GrayText;

            Button openFolder = new Button();
            openFolder.Text = "Buka folder data";
            openFolder.SetBounds(0, 330, 140, 30);
            openFolder.Click += delegate
            {
                try { Process.Start("explorer.exe", "\"" + Paths.Dir + "\""); }
                catch (Exception ex) { Msg.Error(this, ex.Message); }
            };

            page.Controls.Add(l);
            page.Controls.Add(l1);
            page.Controls.Add(p1);
            page.Controls.Add(l2);
            page.Controls.Add(p2);
            page.Controls.Add(change);
            page.Controls.Add(info);
            page.Controls.Add(openFolder);
            return page;
        }

        // --------------------------------------------------------- tab: history

        TabPage BuildHistoryTab()
        {
            TabPage page = new TabPage("Riwayat");
            page.Padding = new Padding(10);

            _history.Multiline = true;
            _history.ReadOnly = true;
            _history.ScrollBars = ScrollBars.Both;
            _history.WordWrap = false;
            _history.Font = new Font("Consolas", 9.5f);
            _history.Dock = DockStyle.Fill;

            page.Controls.Add(_history);
            page.Controls.Add(UiLayout.BottomBarLeft(
                MakeButton("Muat ulang", delegate { LoadHistory(); })));

            LoadHistory();
            return page;
        }

        void LoadHistory()
        {
            IpcResponse r = IpcClient.Send("GETHISTORY", _password, "", "", "");
            if (r == null || !r.Ok)
            {
                _history.Text = "Tidak bisa membaca riwayat.";
                return;
            }
            _history.Text = string.IsNullOrEmpty(r.Payload)
                ? "Belum ada riwayat. Riwayat ditulis setiap kali jatah harian direset."
                : r.Payload.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
        }

        // ---------------------------------------------------------- tab: update

        TabPage BuildUpdateTab()
        {
            TabPage page = new TabPage("Pembaruan");
            page.Padding = new Padding(14);
            page.AutoScroll = true;

            Label heading = new Label();
            heading.Text = "Pembaruan aplikasi";
            heading.Font = new Font("Segoe UI Semibold", 10f);
            heading.SetBounds(0, 8, 500, 22);

            _versionLabel = new Label();
            _versionLabel.SetBounds(0, 34, 620, 20);

            _checkButton = new Button();
            _checkButton.Text = "Cek pembaruan";
            _checkButton.SetBounds(0, 62, 130, 30);
            _checkButton.Click += delegate { CheckUpdate(); };

            _installButton = new Button();
            _installButton.Text = "Pasang sekarang";
            _installButton.SetBounds(140, 62, 140, 30);
            _installButton.Enabled = false;
            _installButton.Click += delegate { InstallUpdate(); };

            _updateStatus = new Label();
            _updateStatus.SetBounds(0, 102, 660, 22);
            _updateStatus.ForeColor = SystemColors.GrayText;

            _updateNotes = new TextBox();
            _updateNotes.Multiline = true;
            _updateNotes.ReadOnly = true;
            _updateNotes.ScrollBars = ScrollBars.Vertical;
            _updateNotes.SetBounds(0, 128, 660, 90);
            _updateNotes.Text = "Tekan \"Cek pembaruan\" untuk melihat apakah ada versi baru.";

            _autoCheck = new CheckBox();
            _autoCheck.Text = "Cek pembaruan otomatis sekali sehari (hanya memberi tahu, tidak memasang sendiri)";
            _autoCheck.SetBounds(0, 230, 660, 22);

            Label urlLabel = new Label();
            urlLabel.Text = "Alamat manifest pembaruan (harus https)";
            urlLabel.SetBounds(0, 262, 400, 18);

            _updateUrl = new TextBox();
            _updateUrl.SetBounds(0, 282, 660, 24);

            Label keyLabel = new Label();
            keyLabel.Text = "Kunci publik RSA untuk verifikasi tanda tangan (opsional, base64 - "
                            + "kosongkan kalau tidak dipakai)";
            keyLabel.SetBounds(0, 314, 660, 18);

            _updateKey = new TextBox();
            _updateKey.SetBounds(0, 334, 660, 24);

            Button saveUpdate = new Button();
            saveUpdate.Text = "Simpan perubahan";
            saveUpdate.SetBounds(0, 368, 160, 30);
            saveUpdate.Click += delegate { SaveSettings(); };

            Label info = new Label();
            info.Text = "Pembaruan diunduh lewat HTTPS dan sidik jari SHA256-nya dicocokkan dengan "
                + "manifest sebelum dijalankan. Kalau tidak cocok, pembaruan dibatalkan.\n"
                + "Aplikasi mati beberapa detik saat berkas ditimpa, lalu menyala lagi sendiri. "
                + "Jatah dan riwayat hari ini tidak hilang.";
            info.SetBounds(0, 404, 660, 60);
            info.ForeColor = SystemColors.GrayText;

            page.Controls.Add(heading);
            page.Controls.Add(_versionLabel);
            page.Controls.Add(_checkButton);
            page.Controls.Add(_installButton);
            page.Controls.Add(_updateStatus);
            page.Controls.Add(_updateNotes);
            page.Controls.Add(_autoCheck);
            page.Controls.Add(urlLabel);
            page.Controls.Add(_updateUrl);
            page.Controls.Add(keyLabel);
            page.Controls.Add(_updateKey);
            page.Controls.Add(saveUpdate);
            page.Controls.Add(info);
            return page;
        }

        void RefreshVersionLabel()
        {
            Status s = StatusReader.Read();
            string running = (s != null && !string.IsNullOrEmpty(s.AgentVersion))
                ? s.AgentVersion : "tidak diketahui";
            if (s != null && !string.IsNullOrEmpty(s.UpdateAvailableVersion))
            {
                _versionLabel.Text = "Versi terpasang: " + running
                    + "   •   versi baru tersedia: " + s.UpdateAvailableVersion;
                _versionLabel.ForeColor = Color.DarkGreen;
            }
            else
            {
                _versionLabel.Text = "Versi terpasang: " + running;
                _versionLabel.ForeColor = SystemColors.ControlText;
            }
        }

        void CheckUpdate()
        {
            _checkButton.Enabled = false;
            _installButton.Enabled = false;
            _updateStatus.ForeColor = SystemColors.GrayText;
            _updateStatus.Text = "Menghubungi server pembaruan...";

            RunInBackground("CHECKUPDATE", "", delegate (IpcResponse r)
            {
                _checkButton.Enabled = true;
                if (r == null) { ShowUpdateError("Agent tidak merespons."); return; }
                if (!r.Ok) { ShowUpdateError(r.Error); return; }

                UpdateCheckResult res;
                try { res = Json.Read<UpdateCheckResult>(r.Payload); }
                catch (Exception ex) { ShowUpdateError("Manifest tidak terbaca: " + ex.Message); return; }

                _latestVersion = res.LatestVersion;
                if (res.UpdateAvailable)
                {
                    _updateStatus.ForeColor = Color.DarkGreen;
                    _updateStatus.Text = "Versi " + res.LatestVersion + " tersedia"
                        + (res.SizeText.Length > 0 ? " (" + res.SizeText + ")" : "")
                        + (res.Signed ? " - bertanda tangan" : "")
                        + ". Terpasang: " + res.CurrentVersion + ".";
                    _installButton.Enabled = true;
                }
                else
                {
                    _updateStatus.ForeColor = SystemColors.GrayText;
                    _updateStatus.Text = "Sudah memakai versi terbaru (" + res.CurrentVersion + ").";
                }
                _updateNotes.Text = string.IsNullOrEmpty(res.Notes)
                    ? "(tidak ada catatan perubahan)"
                    : res.Notes.Replace("\n", "\r\n");
            });
        }

        void InstallUpdate()
        {
            if (!Msg.Confirm(this, "Pasang versi " + _latestVersion + " sekarang?\n\n"
                    + "Pengawas akan mati beberapa detik lalu menyala lagi sendiri. "
                    + "Jatah dan riwayat hari ini tidak hilang."))
                return;

            _checkButton.Enabled = false;
            _installButton.Enabled = false;
            _updateStatus.ForeColor = SystemColors.GrayText;
            _updateStatus.Text = "Mengunduh dan memeriksa berkas...";

            RunInBackground("APPLYUPDATE", "", delegate (IpcResponse r)
            {
                _checkButton.Enabled = true;
                if (r == null) { ShowUpdateError("Agent tidak merespons."); return; }
                if (!r.Ok) { ShowUpdateError(r.Error); return; }

                _updateStatus.ForeColor = Color.DarkGreen;
                _updateStatus.Text = "Versi " + r.Payload + " sedang dipasang. "
                    + "Tunggu sekitar 30 detik, lalu buka lagi panel ini untuk memastikan.";
                Msg.Info(this, "Pembaruan sedang dipasang.\n\n"
                    + "Panel ini akan ditutup. Pengawas menyala lagi otomatis dalam "
                    + "beberapa puluh detik.");
                Close();
            });
        }

        void ShowUpdateError(string message)
        {
            _updateStatus.ForeColor = Color.Firebrick;
            _updateStatus.Text = message;
        }

        /// <summary>
        /// Menjalankan perintah pipa yang lama (unduhan) di thread lain supaya
        /// jendela tidak membeku, lalu kembali ke thread UI untuk menampilkan hasil.
        /// </summary>
        void RunInBackground(string command, string arg1, Action<IpcResponse> onDone)
        {
            string password = _password;
            Thread t = new Thread(delegate ()
            {
                IpcResponse r = IpcClient.Send(command, password, arg1, "", "");
                try
                {
                    if (IsDisposed) return;
                    BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) onDone(r); });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        // ------------------------------------------------------------- plumbing

        bool LoadSettings()
        {
            IpcResponse r = IpcClient.Send("GETSETTINGS", _password, "", "", "");
            if (r == null || !r.Ok)
            {
                Msg.Error(this, r == null ? "Agent tidak merespons." : r.Error);
                return false;
            }
            try { _settings = Json.Read<Settings>(r.Payload); }
            catch (Exception ex) { Msg.Error(this, "Data pengaturan rusak: " + ex.Message); return false; }
            if (_settings.Apps == null) _settings.Apps = new List<AppLimit>();
            return true;
        }

        void SaveSettings()
        {
            CollectRules();
            if (!Call("SETSETTINGS", "", "", Json.Write(_settings))) return;
            if (LoadSettings())
            {
                BindRules();
                RefreshAppList();
            }
            Msg.Info(this, "Pengaturan tersimpan dan langsung berlaku.");
        }

        bool Call(string command, string arg1, string arg2, string payload)
        {
            IpcResponse r = IpcClient.Send(command, _password, arg1, arg2, payload);
            if (r == null)
            {
                Msg.Error(this, "Agent tidak merespons.");
                return false;
            }
            if (!r.Ok)
            {
                Msg.Error(this, r.Error);
                return false;
            }
            return true;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _refresh.Stop();
            _refresh.Dispose();
            base.OnFormClosed(e);
        }
    }
}
