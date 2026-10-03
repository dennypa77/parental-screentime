using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ScreenTimeGuard
{
    /// <summary>
    /// Pengelola misi: menyiapkan kesempatan pengerjaan tiap hari, menerima
    /// pengumpulan dari anak, dan mencatat keputusan orang tua.
    ///
    /// Definisi misi disimpan di settings.json (hanya bisa diubah orang tua),
    /// sedangkan riwayat pengerjaannya di missions.json.
    /// </summary>
    public class MissionStore
    {
        const int MaxRuns = 400;

        MissionBook _book = new MissionBook();

        public void Load()
        {
            try
            {
                if (File.Exists(Paths.Missions))
                {
                    _book = Json.Read<MissionBook>(File.ReadAllText(Paths.Missions, Encoding.UTF8));
                    if (_book == null || _book.Runs == null) _book = new MissionBook();
                }
            }
            catch (Exception ex)
            {
                Log.Write("Gagal membaca missions.json: " + ex.Message);
                _book = new MissionBook();
            }
        }

        public void Save()
        {
            try { Util.AtomicWriteAllText(Paths.Missions, Json.Write(_book)); }
            catch (Exception ex) { Log.Write("Gagal menyimpan missions.json: " + ex.Message); }
        }

        public List<MissionRun> Runs { get { return _book.Runs; } }

        // ------------------------------------------------------------- bantuan

        public MissionRun RunFor(string missionId, string day)
        {
            for (int i = 0; i < _book.Runs.Count; i++)
                if (_book.Runs[i].MissionId == missionId && _book.Runs[i].Day == day)
                    return _book.Runs[i];
            return null;
        }

        MissionRun LatestApproved(string missionId)
        {
            MissionRun best = null;
            DateTime bestAt = DateTime.MinValue;
            for (int i = 0; i < _book.Runs.Count; i++)
            {
                MissionRun r = _book.Runs[i];
                if (r.MissionId != missionId || r.Status != "approved") continue;
                DateTime at;
                if (!DateTime.TryParse(r.DecidedUtc, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out at)) at = DateTime.MinValue;
                if (best == null || at > bestAt) { best = r; bestAt = at; }
            }
            return best;
        }

        /// <summary>
        /// Memastikan tiap misi aktif punya satu baris pengerjaan untuk hari ini,
        /// sesuai pola pengulangannya.
        /// </summary>
        public bool EnsureRunsForToday(List<Mission> missions, string day)
        {
            bool changed = false;
            if (missions == null) return false;

            for (int i = 0; i < missions.Count; i++)
            {
                Mission m = missions[i];
                if (m == null || string.IsNullOrEmpty(m.Id) || !m.Active) continue;
                if (RunFor(m.Id, day) != null) continue;
                if (!EligibleToday(m, day)) continue;

                MissionRun run = new MissionRun();
                run.MissionId = m.Id;
                run.Day = day;
                run.Status = "available";
                _book.Runs.Add(run);
                changed = true;
            }

            if (Prune()) changed = true;
            return changed;
        }

        bool EligibleToday(Mission m, string day)
        {
            if (m.Repeat == "daily") return true;

            MissionRun approved = LatestApproved(m.Id);
            if (m.Repeat == "once") return approved == null;

            if (m.Repeat == "weekly")
            {
                if (approved == null) return true;
                DateTime at;
                if (!DateTime.TryParse(approved.DecidedUtc, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out at)) return true;
                return (DateTime.UtcNow - at.ToUniversalTime()).TotalDays >= 7;
            }
            return true;
        }

        /// <summary>Membuang riwayat lama, tetapi menyimpan misi sekali-jalan yang sudah lulus.</summary>
        bool Prune()
        {
            if (_book.Runs.Count <= MaxRuns) return false;

            List<MissionRun> keep = new List<MissionRun>();
            for (int i = 0; i < _book.Runs.Count; i++)
            {
                MissionRun r = _book.Runs[i];
                DateTime d;
                bool old = DateTime.TryParseExact(r.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                               DateTimeStyles.None, out d)
                           && (DateTime.Now - d).TotalDays > 120;
                if (old && r.Status != "approved") continue;
                keep.Add(r);
            }
            if (keep.Count == _book.Runs.Count) return false;
            _book.Runs = keep;
            return true;
        }

        // --------------------------------------------------------- aksi anak

        /// <summary>Anak menyatakan misi sudah dikerjakan. Mengembalikan pesan galat, atau null kalau berhasil.</summary>
        public string Submit(Mission mission, string day, string note)
        {
            if (mission == null) return "Misi tidak ditemukan.";
            if (!mission.Active) return "Misi ini sedang tidak aktif.";

            MissionRun run = RunFor(mission.Id, day);
            if (run == null) return "Misi ini belum tersedia hari ini.";
            if (run.Status == "submitted") return "Misi ini sudah dikumpulkan dan menunggu penilaian.";
            if (run.Status == "approved") return "Misi ini sudah dinyatakan lulus.";

            note = (note == null ? "" : note.Trim());
            if (note.Length > 500) note = note.Substring(0, 500);
            if (mission.NeedsNote && note.Length == 0)
                return "Misi ini perlu keterangan singkat tentang yang sudah dikerjakan.";

            run.Status = "submitted";
            run.SubmittedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            run.ChildNote = note;
            run.ParentNote = "";
            Save();
            Log.Write("Misi dikumpulkan anak: " + mission.Title
                      + (note.Length > 0 ? " - catatan: " + note : ""));
            return null;
        }

        // ---------------------------------------------------- aksi orang tua

        /// <summary>
        /// Orang tua menilai misi. Kalau lulus, jumlah menit hadiah dikembalikan
        /// lewat parameter keluaran supaya pemanggil yang memberikannya.
        /// </summary>
        public string Decide(Mission mission, string day, bool approve, string parentNote,
                             out int rewardMinutes)
        {
            rewardMinutes = 0;
            if (mission == null) return "Misi tidak ditemukan.";

            MissionRun run = RunFor(mission.Id, day);
            if (run == null) return "Belum ada pengerjaan untuk misi ini hari ini.";
            if (run.Status == "approved") return "Misi ini sudah dinyatakan lulus.";

            parentNote = (parentNote == null ? "" : parentNote.Trim());
            if (parentNote.Length > 500) parentNote = parentNote.Substring(0, 500);

            run.DecidedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            run.ParentNote = parentNote;

            if (approve)
            {
                run.Status = "approved";
                run.AwardedMinutes = Math.Max(0, mission.RewardMinutes);
                rewardMinutes = run.AwardedMinutes;
                Log.Write("Misi LULUS: " + mission.Title + " (+" + rewardMinutes + " menit)");
            }
            else
            {
                // Ditolak berarti anak boleh mencoba lagi hari itu juga.
                run.Status = "rejected";
                run.AwardedMinutes = 0;
                Log.Write("Misi belum lulus: " + mission.Title
                          + (parentNote.Length > 0 ? " - " + parentNote : ""));
            }

            Save();
            return null;
        }

        // --------------------------------------------------------- tampilan

        public List<StatusMission> BuildStatus(List<Mission> missions, string day)
        {
            List<StatusMission> list = new List<StatusMission>();
            if (missions == null) return list;

            for (int i = 0; i < missions.Count; i++)
            {
                Mission m = missions[i];
                if (m == null || string.IsNullOrEmpty(m.Id) || !m.Active) continue;

                MissionRun run = RunFor(m.Id, day);
                string status = run == null ? "locked" : run.Status;

                StatusMission sm = new StatusMission();
                sm.Id = m.Id;
                sm.Title = m.Title;
                sm.Detail = m.Detail == null ? "" : m.Detail;
                sm.RewardMinutes = m.RewardMinutes;
                sm.RewardText = "+" + m.RewardMinutes + " menit " + TargetText(m.RewardTarget);
                sm.Status = status;
                sm.StatusText = StatusText(status, m);
                sm.CanSubmit = status == "available" || status == "rejected";
                sm.NeedsNote = m.NeedsNote;
                sm.ChildNote = run == null ? "" : run.ChildNote;
                sm.ParentNote = run == null ? "" : run.ParentNote;
                sm.Repeat = m.Repeat;
                list.Add(sm);
            }
            return list;
        }

        public static string TargetText(string target)
        {
            if (string.Equals(target, "SESSION", StringComparison.OrdinalIgnoreCase))
                return "waktu komputer";
            if (string.Equals(target, "TOTAL", StringComparison.OrdinalIgnoreCase))
                return "total aplikasi";
            return "untuk " + target;
        }

        static string StatusText(string status, Mission m)
        {
            switch (status)
            {
                case "available": return "Siap dikerjakan";
                case "submitted": return "Menunggu penilaian orang tua";
                case "approved": return "Lulus - hadiah sudah diberikan";
                case "rejected": return "Belum lulus, boleh dicoba lagi";
                default:
                    return m.Repeat == "once" ? "Sudah selesai" : "Belum tersedia hari ini";
            }
        }

        public int CountByStatus(List<Mission> missions, string day, string status)
        {
            int n = 0;
            if (missions == null) return 0;
            for (int i = 0; i < missions.Count; i++)
            {
                Mission m = missions[i];
                if (m == null || !m.Active || string.IsNullOrEmpty(m.Id)) continue;
                MissionRun run = RunFor(m.Id, day);
                if (run != null && run.Status == status) n++;
            }
            return n;
        }

        public static string NewId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }
    }
}
