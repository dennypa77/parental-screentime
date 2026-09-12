# Screen Time Guard

Pembatas waktu layar per-aplikasi untuk komputer Windows anak. Satu file `.exe`, tanpa
perlu memasang Python, .NET SDK, atau apa pun — hanya memakai .NET Framework yang sudah
ada di setiap Windows 10/11.

---

## Apa yang dilakukan

| Kebutuhan | Bagaimana dipenuhi |
|---|---|
| Otomatis jalan saat komputer menyala | Dua Scheduled Task (agent saat *boot*, ikon tray saat *login*), keduanya dicek ulang tiap 5 menit |
| Hanya orang tua yang bisa mengatur | Panel pengaturan dikunci password (PBKDF2-SHA256, 120.000 iterasi). Anak hanya bisa melihat |
| Durasi per aplikasi (.exe) | Jatah menit terpisah untuk tiap aplikasi, beda antara hari sekolah dan akhir pekan |
| Reset otomatis tiap hari | Reset pada jam yang bisa diatur (bawaan 04:00), bukan tengah malam |
| Anak bisa lihat sisa waktu | Jendela "Sisa waktu hari ini" dari ikon tray — bisa dibuka kapan saja tanpa password |

### Tambahan yang saya sertakan

- **Peringatan sebelum habis** — notifikasi pada sisa 15 / 5 / 1 menit (bisa diubah).
- **Masa tenggang** — saat waktu habis, anak diberi waktu (bawaan 60 detik) untuk menyimpan
  permainan sebelum aplikasi ditutup. Tenggang hanya diberikan sekali per aplikasi per hari,
  jadi tidak bisa diakali dengan membuka ulang aplikasi.
- **Bonus waktu** — orang tua bisa menambah (atau mengurangi) menit untuk hari ini saja,
  misalnya hadiah karena PR selesai. Tidak mengubah aturan permanen.
- **Batas total waktu layar** — selain batas per aplikasi, ada pagu total semua aplikasi.
- **Jam tidur** — blokir semua aplikasi yang diawasi pada rentang jam tertentu (mis. 21:00–06:00).
- **Jeda pengawasan** — matikan sementara semua aturan (mis. 30 menit) untuk acara keluarga.
- **Riwayat harian** — `history.csv` mencatat pemakaian tiap hari, bisa dibuka di Excel.
- **Catatan keamanan** — setiap penutupan aplikasi, perubahan aturan, dan percobaan password
  salah tercatat di `log.txt`.
- **Mode "hanya dicatat"** — aplikasi bisa dipantau tanpa dibatasi, berguna untuk melihat dulu
  kebiasaan anak sebelum menetapkan angka.

---

## Cara memasang

Di komputer anak, sebagai Administrator:

```powershell
# 1. Build (menghasilkan bin\ScreenTimeGuard.exe)
powershell -ExecutionPolicy Bypass -File build.ps1

# 2. Pasang (klik kanan -> Run with PowerShell juga bisa; akan minta elevasi sendiri)
powershell -ExecutionPolicy Bypass -File install.ps1
```

`install.ps1` akan:

1. menyalin program ke `C:\Program Files\ScreenTimeGuard\`
2. membuat folder data `C:\ProgramData\ScreenTimeGuard\` dan **mengunci izinnya** — anak
   hanya boleh membaca, tidak boleh mengubah
3. membuka jendela untuk membuat password orang tua
4. mendaftarkan kedua Scheduled Task dan langsung menjalankannya

Mencopot: jalankan `uninstall.ps1`.

### ⚠️ Satu syarat penting

**Akun Windows yang dipakai anak harus akun _Standard_, bukan Administrator.**

Kalau anak punya hak Administrator, dia bisa mematikan Scheduled Task, mengubah file
pengaturan, atau menghapus program — tidak ada tools yang bisa mencegah itu. Cek di
*Settings → Accounts → Family & other users*; kalau tertulis "Administrator", ubah menjadi
"Standard user".

---

## Cara memakai

**Orang tua:** klik kanan ikon jam di system tray → **Panel orang tua...** → masukkan password.

- **Tab Aplikasi** — tambah/ubah/hapus aplikasi yang dibatasi.
  Cara termudah menambah: buka dulu aplikasinya, lalu tekan tombol **"Dari yang berjalan..."**
  dan pilih dari daftar. Jangan menebak nama `.exe`.
- **Tab Aturan umum** — jam reset, ambang peringatan, lama tenggang, batas total, jam tidur.
- **Tab Hari ini** — pemakaian hari ini, tombol bonus waktu, reset pemakaian, jeda pengawasan.
- **Tab Keamanan** — ganti password.
- **Tab Riwayat** — rekap harian.

**Anak:** klik dua kali ikon tray → jendela **Sisa waktu hari ini**. Setiap aplikasi punya
bilah warna: hijau (masih banyak), kuning (menipis), merah (habis). Jadi kalau jatah Roblox
habis, dia langsung lihat Minecraft Education masih tersisa berapa menit.

### Arti angka jatah

| Nilai | Artinya |
|---|---|
| `60` | boleh dipakai 60 menit hari ini |
| `0` | tidak boleh dibuka sama sekali hari ini |
| centang **Tanpa batas** | boleh sepuasnya, pemakaian tetap dicatat |

### Cara menghitung waktu

- **Selama aplikasi terbuka** (bawaan) — dihitung selama proses berjalan, walaupun
  diminimalkan. Cocok untuk game: anak tidak bisa "menghemat jatah" dengan memindah jendela.
- **Hanya saat jendela sedang dipakai** — hanya dihitung saat aplikasi berada di depan.
  Cocok untuk browser atau aplikasi belajar yang sering dibiarkan terbuka.

---

## Nama proses aplikasi yang umum

Gunakan sebagai perkiraan awal — **selalu pastikan** lewat tombol "Dari yang berjalan...".

| Aplikasi | Proses |
|---|---|
| Roblox | `RobloxPlayerBeta.exe` |
| Roblox Studio | `RobloxStudioBeta.exe` |
| Minecraft Bedrock / Education | `Minecraft.Windows.exe` |
| Minecraft Java | `javaw.exe` — **hati-hati**, dipakai juga oleh aplikasi Java lain |
| Google Chrome | `chrome.exe` |
| Microsoft Edge | `msedge.exe` |
| Discord | `Discord.exe` |
| Steam (game-nya sendiri) | tiap game punya `.exe` masing-masing |

Windows tidak boleh dibatasi: `explorer.exe`, `svchost.exe`, dan proses inti lainnya ditolak
oleh program supaya sistem tidak rusak.

---

## Bagaimana ini bekerja

Dua proses, sengaja dipisah supaya sulit diakali:

```
  Agent  (SYSTEM, Scheduled Task saat boot)
    - menghitung pemakaian tiap 5 detik
    - menutup aplikasi yang jatahnya habis
    - menyimpan settings.json / usage.json / status.json
    - melayani perintah lewat named pipe (butuh password)
                 ▲                      │
       named pipe│                      │ status.json (baca saja)
                 │                      ▼
  UI Tray  (akun anak, Scheduled Task saat login)
    - ikon tray + jendela "Sisa waktu hari ini"
    - notifikasi peringatan
    - panel orang tua (setelah password diverifikasi agent)
```

Yang penting: **penegakan aturan ada di proses SYSTEM**, bukan di UI. Kalau anak mematikan
ikon tray lewat Task Manager, aplikasinya tetap ditutup saat waktu habis — dia hanya
kehilangan peringatan "5 menit lagi". Task Manager juga tidak bisa mematikan proses SYSTEM
dari akun Standard, dan Scheduled Task menghidupkannya lagi maksimal 5 menit kemudian.

Password tidak pernah disimpan dalam bentuk aslinya — hanya hash PBKDF2-SHA256 dengan salt
acak. Salah password 5 kali mengunci panel selama 60 detik.

### File data (`C:\ProgramData\ScreenTimeGuard\`)

| File | Isi |
|---|---|
| `settings.json` | aturan + hash password |
| `usage.json` | pemakaian hari ini |
| `status.json` | sisa waktu terkini (dibaca UI) |
| `history.csv` | rekap harian |
| `log.txt` | catatan kejadian |

---

## Batasan yang perlu diketahui

Supaya tidak ada harapan yang keliru:

1. **Anak dengan hak Administrator bisa melumpuhkan ini.** Lihat syarat di atas.
2. **Booting dari USB / Safe Mode** melewati semua pengawasan. Kalau ini jadi masalah,
   kunci BIOS dengan password dan matikan boot dari USB.
3. **Mengganti nama `.exe`** (misal `RobloxPlayerBeta.exe` → `game.exe`) bisa menghindar,
   karena pencocokan berdasarkan nama proses. Untuk anak yang lebih besar, `log.txt` akan
   memperlihatkan polanya.
4. **Game layar penuh eksklusif** mungkin menutupi notifikasi peringatan. Penutupan
   aplikasinya tetap jalan. Kebanyakan game modern memakai *borderless fullscreen*
   sehingga notifikasi tetap terlihat.
5. **Ini bukan pemblokir situs web.** Membatasi `chrome.exe` membatasi seluruh browser,
   bukan per situs. Untuk penyaringan konten, pakai DNS keluarga atau Microsoft Family Safety
   sebagai pelengkap.

---

## Kalau ada masalah

| Gejala | Yang perlu dicek |
|---|---|
| Ikon tray tidak muncul | `Get-ScheduledTask 'ScreenTimeGuard Tray'` — jalankan manual dengan `Start-ScheduledTask` |
| "Agent tidak aktif" | `Get-Process ScreenTimeGuard`; baca `C:\ProgramData\ScreenTimeGuard\log.txt` |
| Aplikasi tidak terhitung | Nama proses salah. Buka aplikasinya, lalu pakai tombol "Dari yang berjalan..." |
| Lupa password | Sebagai Administrator jalankan `"C:\Program Files\ScreenTimeGuard\ScreenTimeGuard.exe" --setup` |

Menjalankan manual untuk uji coba:

```powershell
ScreenTimeGuard.exe --agent    # pengawas (tanpa jendela)
ScreenTimeGuard.exe --ui       # ikon tray
ScreenTimeGuard.exe --setup    # atur password (perlu Administrator)
```

---

## Saran pemakaian

- **Pantau dulu seminggu.** Tambahkan aplikasinya dengan centang *"Berlakukan pembatasan"*
  dimatikan, lihat `history.csv`, baru tentukan angkanya. Batas yang berdasar data lebih
  mudah diterima anak daripada angka yang terasa sembarangan.
- **Jam reset 04:00, bukan 00:00**, supaya main lewat tengah malam tetap terhitung hari itu.
- **Tenggang jangan kurang dari 60 detik.** Terputus di tengah permainan terasa seperti
  hukuman, dan itu yang memicu anak mencari cara mengakali.
- **Pakai bonus waktu, jangan ubah aturan permanen** untuk kelonggaran sekali-sekali.
  Aturannya tetap konsisten, kelonggarannya jelas terasa sebagai hadiah.
- **Tunjukkan jendela sisa waktu ke anak sejak awal**, jelaskan angkanya. Alat ini dirancang
  supaya anak bisa mengatur waktunya sendiri — bukan supaya dia dikejutkan aplikasi yang
  tiba-tiba tertutup.
