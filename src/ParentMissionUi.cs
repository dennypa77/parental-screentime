using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ScreenTimeGuard
{
    /// <summary>
    /// Bagian panel orang tua yang mengurus misi dan panel jarak jauh.
    /// Dipisah ke berkas sendiri supaya ParentUi.cs tidak terlalu panjang.
    /// </summary>
    public partial class ParentForm
    {
        ListView _missionList;
        CheckBox _remoteEnabled, _remoteLanOnly;
        NumericUpDown _remotePort;
        Label _remoteInfo;

        // ------------------------------------------------------------ tab misi

        TabPage BuildMissionTab()
        {
            TabPage page = new TabPage("Misi");
            page.Padding = new Padding(10);

            _missionList = new ListView();
            _missionList.View = View.Details;
            _missionList.FullRowSelect = true;
            _missionList.HideSelection = false;
            _missionList.Dock = DockStyle.Fill;
            _missionList.Columns.Add("Misi", 200);
            _missionList.Columns.Add("Hadiah", 160);
            _missionList.Columns.Add("Ulang", 80);
            _missionList.Columns.Add("Status", 170);
            _missionList.Columns.Add("Catatan anak", 200);
            _missionList.DoubleClick += delegate { EditMission(); };

            Label hint = new Label();
            hint.Text = "Anak mengerjakan misi lalu menekan tombol \"Saya sudah selesai\". "
                        + "Begitu Anda menyatakan lulus, hadiah waktunya langsung ditambahkan.";
            hint.Dock = DockStyle.Top;
            hint.Height = 36;
            hint.ForeColor = SystemColors.GrayText;

            FlowLayoutPanel bar = UiLayout.BottomBarLeft(
                MakeButton("Lulus", delegate { DecideMission(true); }),
                MakeButton("Belum lulus", delegate { DecideMission(false); }),
                MakeButton("Tambah misi", delegate { AddMission(); }),
                MakeButton("Ubah", delegate { EditMission(); }),
                MakeButton("Hapus", delegate { DeleteMission(); }));

            page.Controls.Add(_missionList);
            page.Controls.Add(hint);
            page.Controls.Add(bar);
            return page;
        }

        void RefreshMissions(Status s)
        {
            if (_missionList == null || _settings == null) return;

            string selected = _missionList.SelectedItems.Count > 0
                ? (string)_missionList.SelectedItems[0].Tag : null;

            Dictionary<string, StatusMission> live = new Dictionary<string, StatusMission>();
            if (s != null && s.Missions != null)
                for (int i = 0; i < s.Missions.Count; i++) live[s.Missions[i].Id] = s.Missions[i];

            _missionList.BeginUpdate();
            _missionList.Items.Clear();
            for (int i = 0; i < _settings.Missions.Count; i++)
            {
                Mission m = _settings.Missions[i];
                StatusMission sm;
                bool hasLive = live.TryGetValue(m.Id, out sm);

                ListViewItem it = new ListViewItem(m.Title);
                it.SubItems.Add(m.RewardMinutes + " menit " + MissionStore.TargetText(m.RewardTarget));
                it.SubItems.Add(m.Repeat == "weekly" ? "mingguan"
                                : m.Repeat == "once" ? "sekali" : "harian");
                it.SubItems.Add(!m.Active ? "tidak aktif" : hasLive ? sm.StatusText : "-");
                it.SubItems.Add(hasLive ? sm.ChildNote : "");
                it.Tag = m.Id;
                if (hasLive && sm.Status == "submitted") it.ForeColor = Color.DarkOrange;
                else if (hasLive && sm.Status == "approved") it.ForeColor = Color.DarkGreen;
                _missionList.Items.Add(it);
                if (selected != null && selected == m.Id) it.Selected = true;
            }
            _missionList.EndUpdate();
        }

        Mission SelectedMission()
        {
            if (_missionList.SelectedItems.Count == 0)
            {
                Msg.Error(this, "Pilih dulu misi di daftar.");
                return null;
            }
            string id = (string)_missionList.SelectedItems[0].Tag;
            for (int i = 0; i < _settings.Missions.Count; i++)
                if (_settings.Missions[i].Id == id) return _settings.Missions[i];
            return null;
        }

        void AddMission()
        {
            using (MissionEditForm f = new MissionEditForm(null, _settings.Apps))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                _settings.Missions.Add(f.Result);
                SaveSettings();
            }
        }

        void EditMission()
        {
            Mission m = SelectedMission();
            if (m == null) return;
            using (MissionEditForm f = new MissionEditForm(m, _settings.Apps))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                SaveSettings();
            }
        }

        void DeleteMission()
        {
            Mission m = SelectedMission();
            if (m == null) return;
            if (!Msg.Confirm(this, "Hapus misi " + m.Title + "?")) return;
            _settings.Missions.Remove(m);
            SaveSettings();
        }

        void DecideMission(bool approve)
        {
            Mission m = SelectedMission();
            if (m == null) return;

            string note = PromptForm.Ask(this,
                approve ? "Misi lulus" : "Misi belum lulus",
                approve ? "Pesan untuk anak (boleh dikosongkan):"
                        : "Kenapa belum lulus? (boleh dikosongkan)", "", false);
            if (note == null) return;

            if (!Call("MISSIONDECIDE", m.Id, approve ? "approve" : "reject", note)) return;

            RefreshToday();
            Msg.Info(this, approve
                ? "Misi dinyatakan lulus. Hadiah " + m.RewardMinutes + " menit sudah ditambahkan."
                : "Misi ditandai belum lulus. Anak masih boleh mengumpulkan lagi.");
        }

        // ------------------------------------------------- bagian panel jarak jauh

        /// <summary>Dipanggil dari tab Aturan umum. Mengembalikan tinggi yang terpakai.</summary>
        int BuildRemoteSection(TabPage page, int y)
        {
            page.Controls.Add(Section("Panel jarak jauh (atur dari komputer / HP sendiri)", 0, ref y));

            _remoteEnabled = Check("Nyalakan panel jarak jauh", 0, y);
            _remoteEnabled.Width = 320;
            page.Controls.Add(_remoteEnabled);
            y += 26;

            page.Controls.Add(Field("Nomor port", 0, y));
            _remotePort = Num(200, y, 1024, 65535, 8777);
            page.Controls.Add(_remotePort);
            y += 30;

            _remoteLanOnly = Check("Hanya boleh dibuka dari jaringan rumah (disarankan)", 0, y);
            _remoteLanOnly.Width = 440;
            page.Controls.Add(_remoteLanOnly);
            y += 28;

            _remoteInfo = Hint("", 0, y);
            _remoteInfo.Height = 50;
            page.Controls.Add(_remoteInfo);
            return y + 56;
        }

        void RefreshRemoteInfo(Status s)
        {
            if (_remoteInfo == null) return;

            if (s != null && !string.IsNullOrEmpty(s.RemoteUrl))
            {
                _remoteInfo.ForeColor = Color.DarkGreen;
                _remoteInfo.Text = "Panel aktif. Dari komputer atau HP di jaringan rumah, buka:"
                    + Environment.NewLine + s.RemoteUrl
                    + Environment.NewLine + "Masuk dengan password orang tua yang sama.";
            }
            else
            {
                _remoteInfo.ForeColor = SystemColors.GrayText;
                _remoteInfo.Text = _remoteEnabled.Checked
                    ? "Panel belum berjalan. Simpan perubahan dulu; kalau tetap mati, periksa log.txt "
                      + "dan izin Windows Firewall."
                    : "Panel jarak jauh sedang mati.";
            }
        }

        void BindRemote()
        {
            _remoteEnabled.Checked = _settings.RemoteEnabled;
            _remotePort.Value = Math.Max(1024, Math.Min(65535, _settings.RemotePort));
            _remoteLanOnly.Checked = _settings.RemoteLanOnly;
        }

        void CollectRemote()
        {
            _settings.RemoteEnabled = _remoteEnabled.Checked;
            _settings.RemotePort = (int)_remotePort.Value;
            _settings.RemoteLanOnly = _remoteLanOnly.Checked;
        }
    }
}
