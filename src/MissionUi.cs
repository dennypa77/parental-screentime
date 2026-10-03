using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace ScreenTimeGuard
{
    /// <summary>
    /// Daftar misi untuk anak. Tidak butuh password: anak boleh melihat misinya dan
    /// menyatakan sudah selesai, tetapi penilaiannya tetap di tangan orang tua.
    /// </summary>
    public class MissionForm : Form
    {
        readonly FlowLayoutPanel _list = new FlowLayoutPanel();
        readonly Label _header = new Label();
        readonly Label _sub = new Label();
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        string _signature = "";

        public MissionForm()
        {
            Text = "Misi Saya";
            Icon = IconFactory.TrayIcon();
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(580, 560);
            MinimumSize = new Size(440, 340);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;

            _header.Text = "Misi Saya";
            _header.Font = Theme.H1;
            _header.ForeColor = Theme.Text;
            _header.AutoSize = false;
            _header.Dock = DockStyle.Top;
            _header.Height = 38;
            _header.Padding = new Padding(16, 10, 14, 0);

            _sub.Font = Theme.Body;
            _sub.ForeColor = Theme.TextDim;
            _sub.AutoSize = false;
            _sub.Dock = DockStyle.Top;
            _sub.Height = 46;
            _sub.Padding = new Padding(18, 0, 14, 0);

            _list.Dock = DockStyle.Fill;
            _list.FlowDirection = FlowDirection.TopDown;
            _list.WrapContents = false;
            _list.AutoScroll = true;
            _list.BackColor = Theme.Bg;
            _list.Padding = new Padding(12, 4, 12, 12);

            Button close = new Button();
            close.Text = "Tutup";
            Theme.StyleButton(close, false);
            close.Click += delegate { Close(); };
            FlowLayoutPanel bar = UiLayout.BottomBar(close);
            bar.BackColor = Theme.Bg;

            Controls.Add(_list);
            Controls.Add(_sub);
            Controls.Add(_header);
            Controls.Add(bar);

            _timer.Interval = 2000;
            _timer.Tick += delegate { Reload(false); };
            _timer.Start();
            Reload(true);
        }

        void Reload(bool force)
        {
            Status st = StatusReader.Read();
            if (st == null || st.Missions == null) return;

            // Hanya digambar ulang kalau isinya berubah, supaya tombol tidak bergeser
            // saat anak sedang mengarahkan kursor ke sana.
            StringBuilder sig = new StringBuilder();
            for (int i = 0; i < st.Missions.Count; i++)
                sig.Append(st.Missions[i].Id).Append(st.Missions[i].Status)
                   .Append(st.Missions[i].ParentNote).Append('|');
            if (!force && sig.ToString() == _signature) return;
            _signature = sig.ToString();

            int ready = 0, waiting = 0, done = 0;
            for (int i = 0; i < st.Missions.Count; i++)
            {
                string status = st.Missions[i].Status;
                if (status == "available" || status == "rejected") ready++;
                else if (status == "submitted") waiting++;
                else if (status == "approved") done++;
            }

            _sub.Text = ready + " siap dikerjakan  •  " + waiting + " menunggu penilaian  •  "
                        + done + " sudah lulus"
                        + Environment.NewLine + "Selesaikan misi untuk menambah waktu bermain.";

            _list.SuspendLayout();
            _list.Controls.Clear();
            if (st.Missions.Count == 0)
            {
                Label empty = new Label();
                empty.Text = "Belum ada misi dari orang tua.";
                empty.ForeColor = Theme.TextDim;
                empty.AutoSize = true;
                _list.Controls.Add(empty);
            }
            for (int i = 0; i < st.Missions.Count; i++)
                _list.Controls.Add(BuildCard(st.Missions[i]));
            _list.ResumeLayout();
        }

        Panel BuildCard(StatusMission m)
        {
            int width = Math.Max(340, _list.ClientSize.Width - 40);

            Panel card = new Panel();
            card.Width = width;
            card.BackColor = Theme.Card;
            card.Margin = new Padding(0, 0, 0, 10);
            card.Padding = new Padding(12, 10, 12, 12);
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;

            FlowLayoutPanel inner = new FlowLayoutPanel();
            inner.FlowDirection = FlowDirection.TopDown;
            inner.WrapContents = false;
            inner.AutoSize = true;
            inner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            inner.Dock = DockStyle.Top;

            Color accent = m.Status == "approved" ? Theme.Good
                         : m.Status == "submitted" ? Theme.Warn
                         : m.Status == "rejected" ? Theme.Bad
                         : Theme.Accent;

            int textWidth = width - 44;
            inner.Controls.Add(Line(m.Title, Theme.BodyBold, Theme.Text, textWidth));
            if (m.Detail.Length > 0)
                inner.Controls.Add(Line(m.Detail, Theme.Body, Theme.TextDim, textWidth));
            inner.Controls.Add(Line(m.RewardText + "   •   " + m.StatusText,
                                    Theme.Body, accent, textWidth));
            if (m.ParentNote.Length > 0)
                inner.Controls.Add(Line("Pesan orang tua: " + m.ParentNote,
                                        Theme.Body, Theme.TextDim, textWidth));

            if (m.CanSubmit)
            {
                Button submit = new Button();
                submit.Text = "Saya sudah selesai";
                submit.AutoSize = true;
                submit.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                submit.MinimumSize = new Size(180, 32);
                submit.Margin = new Padding(0, 8, 0, 0);
                Theme.StyleButton(submit, true);
                string id = m.Id;
                bool needsNote = m.NeedsNote;
                submit.Click += delegate { Submit(id, needsNote); };
                inner.Controls.Add(submit);
            }

            card.Controls.Add(inner);
            return card;
        }

        static Label Line(string text, Font font, Color color, int width)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = font;
            l.ForeColor = color;
            l.MaximumSize = new Size(Math.Max(200, width), 0);
            l.AutoSize = true;
            l.Margin = new Padding(0, 0, 0, 4);
            return l;
        }

        void Submit(string missionId, bool needsNote)
        {
            string note = "";
            if (needsNote)
            {
                note = PromptForm.Ask(this, "Misi selesai",
                    "Tulis sedikit tentang yang sudah kamu kerjakan:", "", false);
                if (note == null) return;
                if (note.Trim().Length == 0)
                {
                    Msg.Error(this, "Keterangannya belum diisi.");
                    return;
                }
            }
            else if (!Msg.Confirm(this, "Kirim ke orang tua untuk dinilai?"))
            {
                return;
            }

            IpcResponse r = IpcClient.Send("MISSIONSUBMIT", "", missionId, "", note);
            if (r == null) { Msg.Error(this, "Agent pengawas tidak merespons."); return; }
            if (!r.Ok) { Msg.Error(this, r.Error); return; }

            Msg.Info(this, "Terkirim. Tunggu orang tua menilai, ya.");
            Reload(true);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            _timer.Dispose();
            base.OnFormClosed(e);
        }
    }

    /// <summary>Dialog tambah / ubah misi untuk orang tua.</summary>
    public class MissionEditForm : Form
    {
        readonly TextBox _title = new TextBox();
        readonly TextBox _detail = new TextBox();
        readonly NumericUpDown _reward = new NumericUpDown();
        readonly ComboBox _repeat = new ComboBox();
        readonly ComboBox _target = new ComboBox();
        readonly CheckBox _needsNote = new CheckBox();
        readonly CheckBox _active = new CheckBox();
        readonly System.Collections.Generic.List<string> _targetValues =
            new System.Collections.Generic.List<string>();

        public Mission Result { get; private set; }

        public MissionEditForm(Mission existing, System.Collections.Generic.List<AppLimit> apps)
        {
            Text = existing == null ? "Tambah misi" : "Ubah misi";
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            Icon = IconFactory.TrayIcon();
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(520, 440);
            MinimumSize = new Size(440, 340);

            Panel host = UiLayout.ScrollHost();
            int y = 4;

            host.Controls.Add(Lbl("Judul misi (dilihat anak)", y)); y += 20;
            _title.SetBounds(0, y, 460, 24); host.Controls.Add(_title); y += 34;

            host.Controls.Add(Lbl("Keterangan (boleh dikosongkan)", y)); y += 20;
            _detail.SetBounds(0, y, 460, 60);
            _detail.Multiline = true;
            _detail.ScrollBars = ScrollBars.Vertical;
            host.Controls.Add(_detail); y += 70;

            host.Controls.Add(Lbl("Hadiah waktu (menit)", y)); y += 20;
            _reward.SetBounds(0, y, 90, 24);
            _reward.Maximum = 600;
            host.Controls.Add(_reward); y += 34;

            host.Controls.Add(Lbl("Hadiah ditambahkan ke", y)); y += 20;
            _target.SetBounds(0, y, 460, 24);
            _target.DropDownStyle = ComboBoxStyle.DropDownList;
            _target.Items.Add("Waktu komputer (semua kegiatan)");
            _targetValues.Add("SESSION");
            _target.Items.Add("Total aplikasi yang diawasi");
            _targetValues.Add("TOTAL");
            if (apps != null)
                for (int i = 0; i < apps.Count; i++)
                {
                    _target.Items.Add(apps[i].Name + " (" + apps[i].Process + ".exe)");
                    _targetValues.Add(apps[i].Process);
                }
            host.Controls.Add(_target); y += 34;

            host.Controls.Add(Lbl("Pengulangan", y)); y += 20;
            _repeat.SetBounds(0, y, 460, 24);
            _repeat.DropDownStyle = ComboBoxStyle.DropDownList;
            _repeat.Items.Add("Setiap hari");
            _repeat.Items.Add("Seminggu sekali");
            _repeat.Items.Add("Sekali saja");
            host.Controls.Add(_repeat); y += 34;

            _needsNote.Text = "Anak wajib menulis keterangan saat mengumpulkan";
            _needsNote.SetBounds(0, y, 460, 22);
            host.Controls.Add(_needsNote); y += 26;

            _active.Text = "Misi aktif";
            _active.SetBounds(0, y, 460, 22);
            host.Controls.Add(_active);

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
                _reward.Value = 30;
                _repeat.SelectedIndex = 0;
                _target.SelectedIndex = 0;
                _active.Checked = true;
            }
            else
            {
                _title.Text = existing.Title;
                _detail.Text = existing.Detail;
                _reward.Value = Math.Max(0, Math.Min(600, existing.RewardMinutes));
                _repeat.SelectedIndex = existing.Repeat == "weekly" ? 1
                                      : existing.Repeat == "once" ? 2 : 0;
                int ti = _targetValues.IndexOf(existing.RewardTarget);
                _target.SelectedIndex = ti >= 0 ? ti : 0;
                _needsNote.Checked = existing.NeedsNote;
                _active.Checked = existing.Active;
                Result = existing;
            }
        }

        static Label Lbl(string text, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.SetBounds(0, y, 460, 18);
            return l;
        }

        void Save()
        {
            if (_title.Text.Trim().Length == 0)
            {
                Msg.Error(this, "Judul misi tidak boleh kosong.");
                return;
            }

            Mission m = Result == null ? new Mission() : Result;
            if (string.IsNullOrEmpty(m.Id)) m.Id = MissionStore.NewId();
            m.Title = _title.Text.Trim();
            m.Detail = _detail.Text.Trim();
            m.RewardMinutes = (int)_reward.Value;
            m.RewardTarget = _targetValues[Math.Max(0, _target.SelectedIndex)];
            m.Repeat = _repeat.SelectedIndex == 1 ? "weekly"
                     : _repeat.SelectedIndex == 2 ? "once" : "daily";
            m.NeedsNote = _needsNote.Checked;
            m.Active = _active.Checked;

            Result = m;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
