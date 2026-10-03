namespace ScreenTimeGuard
{
    /// <summary>
    /// Halaman panel orang tua. Semuanya menyatu dalam satu berkas: tidak ada
    /// permintaan ke internet, jadi panel tetap bisa dipakai walau komputer anak
    /// sedang tidak punya sambungan internet.
    ///
    /// Perhitungan PBKDF2 dan HMAC ditulis sendiri karena crypto.subtle di peramban
    /// hanya tersedia pada halaman https, sedangkan panel ini berjalan di http
    /// dalam jaringan rumah.
    /// </summary>
    public partial class WebPanel
    {
        const string Html = @"<!doctype html>
<html lang='id'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>Panel Orang Tua - Screen Time Guard</title>
<style>
  :root {
    --bg:#f4f5f8; --card:#fff; --line:#e2e5ec; --text:#1d2129; --dim:#6b7280;
    --accent:#2f6fe0; --good:#1f8a4c; --warn:#b5730a; --bad:#c23b3b;
  }
  @media (prefers-color-scheme: dark) {
    :root { --bg:#14161b; --card:#1e2128; --line:#2e333d; --text:#e9ecf1; --dim:#9aa3b2;
            --accent:#5c92f0; --good:#45b97a; --warn:#d99a2b; --bad:#e26a6a; }
  }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--text);
         font:15px/1.5 -apple-system,Segoe UI,Roboto,sans-serif; }
  .wrap { max-width:780px; margin:0 auto; padding:16px; }
  h1 { font-size:19px; margin:0 0 2px; }
  h2 { font-size:15px; margin:0 0 10px; }
  .sub { color:var(--dim); font-size:13px; }
  .card { background:var(--card); border:1px solid var(--line); border-radius:10px;
          padding:14px; margin-bottom:12px; }
  .row { display:flex; gap:10px; align-items:center; flex-wrap:wrap; }
  .row.space { justify-content:space-between; }
  .grow { flex:1 1 auto; min-width:0; }
  button { font:inherit; padding:9px 14px; border-radius:8px; border:1px solid var(--line);
           background:var(--card); color:var(--text); cursor:pointer; }
  button:hover { border-color:var(--accent); }
  button.primary { background:var(--accent); border-color:var(--accent); color:#fff; }
  button.good { background:var(--good); border-color:var(--good); color:#fff; }
  button.bad { background:var(--bad); border-color:var(--bad); color:#fff; }
  button:disabled { opacity:.5; cursor:default; }
  input, select, textarea { font:inherit; padding:9px 10px; border-radius:8px;
           border:1px solid var(--line); background:var(--bg); color:var(--text); width:100%; }
  label { display:block; font-size:13px; color:var(--dim); margin:10px 0 4px; }
  .tabs { display:flex; gap:6px; margin-bottom:12px; flex-wrap:wrap; }
  .tabs button { flex:1 1 auto; }
  .tabs button.on { background:var(--accent); border-color:var(--accent); color:#fff; }
  .big { font:600 22px/1.2 ui-monospace,Consolas,monospace; }
  .pill { font-size:12px; padding:2px 8px; border-radius:99px; border:1px solid var(--line);
          color:var(--dim); white-space:nowrap; }
  .pill.good { color:var(--good); border-color:var(--good); }
  .pill.warn { color:var(--warn); border-color:var(--warn); }
  .pill.bad  { color:var(--bad);  border-color:var(--bad); }
  .item { border-top:1px solid var(--line); padding:12px 0; }
  .item:first-child { border-top:0; padding-top:0; }
  .muted { color:var(--dim); font-size:13px; }
  .msg { padding:10px 12px; border-radius:8px; margin-bottom:12px; display:none; }
  .msg.show { display:block; }
  .msg.err { background:rgba(194,59,59,.12); color:var(--bad); }
  .msg.ok { background:rgba(31,138,76,.12); color:var(--good); }
  .two { display:grid; grid-template-columns:1fr 1fr; gap:10px; }
  @media (max-width:480px) { .two { grid-template-columns:1fr; } }
  .hide { display:none; }
</style>
</head>
<body>
<div class='wrap'>

  <div id='msg' class='msg'></div>

  <!-- ============ MASUK ============ -->
  <div id='login'>
    <div class='card'>
      <h1>Panel Orang Tua</h1>
      <div class='sub'>Screen Time Guard &mdash; komputer anak</div>
      <label for='pw'>Password orang tua</label>
      <input id='pw' type='password' autocomplete='current-password'>
      <div style='margin-top:12px'>
        <button id='btnLogin' class='primary'>Masuk</button>
      </div>
      <div class='muted' style='margin-top:10px'>
        Password tidak dikirim lewat jaringan. Peramban menghitung bukti kriptografis,
        jadi pemeriksaan butuh waktu satu dua detik.
      </div>
    </div>
  </div>

  <!-- ============ PANEL ============ -->
  <div id='app' class='hide'>
    <div class='row space' style='margin-bottom:12px'>
      <div class='grow'>
        <h1>Panel Orang Tua</h1>
        <div class='sub' id='daySub'>&nbsp;</div>
      </div>
      <button id='btnLogout'>Keluar</button>
    </div>

    <div class='tabs'>
      <button data-tab='ringkasan' class='on'>Ringkasan</button>
      <button data-tab='misi'>Misi <span id='misiBadge' class='pill'></span></button>
      <button data-tab='aplikasi'>Aplikasi</button>
      <button data-tab='aturan'>Aturan</button>
    </div>

    <!-- Ringkasan -->
    <div id='tab-ringkasan'>
      <div class='card' id='cardSession'></div>
      <div class='card'>
        <h2>Tindakan cepat</h2>
        <div class='row'>
          <button data-bonus='15'>+15 mnt komputer</button>
          <button data-bonus='30'>+30 mnt komputer</button>
          <button data-bonus='-15'>-15 mnt komputer</button>
        </div>
        <div class='row' style='margin-top:10px'>
          <button data-pause='30'>Jeda 30 mnt</button>
          <button data-pause='60'>Jeda 60 mnt</button>
          <button data-pause='0'>Batalkan jeda</button>
        </div>
      </div>
      <div class='card'><h2>Pemakaian hari ini</h2><div id='usageList'></div></div>
    </div>

    <!-- Misi -->
    <div id='tab-misi' class='hide'>
      <div class='card'>
        <h2>Misi anak</h2>
        <div id='missionList'></div>
      </div>
      <div class='card'>
        <h2>Tambah misi</h2>
        <label for='mTitle'>Judul misi</label>
        <input id='mTitle' placeholder='Mengaji halaman 12-13'>
        <label for='mDetail'>Keterangan (boleh dikosongkan)</label>
        <textarea id='mDetail' rows='2' placeholder='Baca pelan-pelan, lalu setor ke Ibu.'></textarea>
        <div class='two'>
          <div>
            <label for='mReward'>Hadiah (menit)</label>
            <input id='mReward' type='number' value='30' min='0' max='600'>
          </div>
          <div>
            <label for='mRepeat'>Pengulangan</label>
            <select id='mRepeat'>
              <option value='daily'>Setiap hari</option>
              <option value='weekly'>Seminggu sekali</option>
              <option value='once'>Sekali saja</option>
            </select>
          </div>
        </div>
        <label for='mTarget'>Hadiah masuk ke</label>
        <select id='mTarget'></select>
        <label class='row' style='gap:8px; margin-top:12px'>
          <input id='mNote' type='checkbox' style='width:auto'>
          <span>Anak wajib menulis keterangan saat mengumpulkan</span>
        </label>
        <div style='margin-top:12px'><button id='btnAddMission' class='primary'>Tambah misi</button></div>
      </div>
    </div>

    <!-- Aplikasi -->
    <div id='tab-aplikasi' class='hide'>
      <div class='card'>
        <h2>Batas per aplikasi</h2>
        <div class='muted' style='margin-bottom:10px'>
          Menambah aplikasi baru dilakukan di panel komputer anak, supaya bisa memilih
          dari daftar aplikasi yang sedang berjalan.
        </div>
        <div id='appList'></div>
        <div style='margin-top:12px'><button id='btnSaveApps' class='primary'>Simpan</button></div>
      </div>
    </div>

    <!-- Aturan -->
    <div id='tab-aturan' class='hide'>
      <div class='card'>
        <h2>Batas pemakaian komputer</h2>
        <label class='row' style='gap:8px'>
          <input id='sEnabled' type='checkbox' style='width:auto'>
          <span>Kunci layar kalau jatah pemakaian komputer habis</span>
        </label>
        <div class='two'>
          <div><label for='sWd'>Hari sekolah (menit)</label><input id='sWd' type='number' min='0' max='1440'></div>
          <div><label for='sWe'>Akhir pekan (menit)</label><input id='sWe' type='number' min='0' max='1440'></div>
        </div>
        <div class='two'>
          <div><label for='sIdle'>Berhenti menghitung setelah diam (menit)</label>
               <input id='sIdle' type='number' min='0' max='120'></div>
          <div><label for='sAct'>Saat waktu habis</label>
               <select id='sAct'>
                 <option value='lock'>Kunci layar</option>
                 <option value='logoff'>Keluar dari akun</option>
               </select></div>
        </div>
      </div>
      <div class='card'>
        <h2>Jam tidur</h2>
        <label class='row' style='gap:8px'>
          <input id='bEnabled' type='checkbox' style='width:auto'>
          <span>Blokir aplikasi yang diawasi pada jam tidur</span>
        </label>
        <div class='two'>
          <div><label for='bStart'>Dari pukul</label><input id='bStart' placeholder='21:00'></div>
          <div><label for='bEnd'>Sampai pukul</label><input id='bEnd' placeholder='06:00'></div>
        </div>
      </div>
      <div class='card'>
        <div class='two'>
          <div><label for='rHour'>Jatah direset tiap pukul</label><input id='rHour' type='number' min='0' max='23'></div>
          <div><label for='rGrace'>Tenggang sebelum ditutup (detik)</label><input id='rGrace' type='number' min='0' max='900'></div>
        </div>
        <div style='margin-top:12px'><button id='btnSaveRules' class='primary'>Simpan aturan</button></div>
      </div>
    </div>
  </div>
</div>

<script>
'use strict';

/* ---------- SHA-256 / HMAC / PBKDF2 (tanpa pustaka luar) ---------- */
var K = [
0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2];

function rotr(x, n) { return (x >>> n) | (x << (32 - n)); }

function sha256(msg) {
  var h0=0x6a09e667,h1=0xbb67ae85,h2=0x3c6ef372,h3=0xa54ff53a,
      h4=0x510e527f,h5=0x9b05688c,h6=0x1f83d9ab,h7=0x5be0cd19;
  var l = msg.length, total = (((l + 9 + 63) / 64) | 0) * 64;
  var m = new Uint8Array(total);
  m.set(msg); m[l] = 0x80;
  var dv = new DataView(m.buffer);
  dv.setUint32(total - 8, Math.floor(l / 536870912), false);
  dv.setUint32(total - 4, (l << 3) >>> 0, false);
  var w = new Uint32Array(64), i, j;
  for (i = 0; i < total; i += 64) {
    for (j = 0; j < 16; j++) w[j] = dv.getUint32(i + j * 4, false);
    for (j = 16; j < 64; j++) {
      var a15 = w[j-15], a2 = w[j-2];
      var s0 = rotr(a15,7) ^ rotr(a15,18) ^ (a15 >>> 3);
      var s1 = rotr(a2,17) ^ rotr(a2,19) ^ (a2 >>> 10);
      w[j] = (w[j-16] + s0 + w[j-7] + s1) >>> 0;
    }
    var a=h0,b=h1,c=h2,d=h3,e=h4,f=h5,g=h6,hh=h7;
    for (j = 0; j < 64; j++) {
      var S1 = rotr(e,6) ^ rotr(e,11) ^ rotr(e,25);
      var ch = (e & f) ^ (~e & g);
      var t1 = (hh + S1 + ch + K[j] + w[j]) >>> 0;
      var S0 = rotr(a,2) ^ rotr(a,13) ^ rotr(a,22);
      var maj = (a & b) ^ (a & c) ^ (b & c);
      var t2 = (S0 + maj) >>> 0;
      hh=g; g=f; f=e; e=(d + t1) >>> 0; d=c; c=b; b=a; a=(t1 + t2) >>> 0;
    }
    h0=(h0+a)>>>0; h1=(h1+b)>>>0; h2=(h2+c)>>>0; h3=(h3+d)>>>0;
    h4=(h4+e)>>>0; h5=(h5+f)>>>0; h6=(h6+g)>>>0; h7=(h7+hh)>>>0;
  }
  var out = new Uint8Array(32), od = new DataView(out.buffer);
  od.setUint32(0,h0,false); od.setUint32(4,h1,false); od.setUint32(8,h2,false);
  od.setUint32(12,h3,false); od.setUint32(16,h4,false); od.setUint32(20,h5,false);
  od.setUint32(24,h6,false); od.setUint32(28,h7,false);
  return out;
}

function cat(a, b) {
  var r = new Uint8Array(a.length + b.length);
  r.set(a); r.set(b, a.length);
  return r;
}

function hmac(key, msg) {
  if (key.length > 64) key = sha256(key);
  var k = new Uint8Array(64); k.set(key);
  var o = new Uint8Array(64), i = new Uint8Array(64);
  for (var x = 0; x < 64; x++) { o[x] = k[x] ^ 0x5c; i[x] = k[x] ^ 0x36; }
  return sha256(cat(o, sha256(cat(i, msg))));
}

function pbkdf2(pw, salt, iters) {
  var block = cat(salt, new Uint8Array([0,0,0,1]));
  var u = hmac(pw, block), out = u.slice(), j;
  for (var i = 1; i < iters; i++) {
    u = hmac(pw, u);
    for (j = 0; j < 32; j++) out[j] ^= u[j];
  }
  return out;
}

function b64d(s) {
  var raw = atob(s), a = new Uint8Array(raw.length);
  for (var i = 0; i < raw.length; i++) a[i] = raw.charCodeAt(i);
  return a;
}
function b64e(a) {
  var s = '';
  for (var i = 0; i < a.length; i++) s += String.fromCharCode(a[i]);
  return btoa(s);
}
function utf8(s) {
  var e = encodeURIComponent(s), a = [], i;
  for (i = 0; i < e.length; i++) {
    if (e[i] === '%') { a.push(parseInt(e.substr(i+1,2),16)); i += 2; }
    else a.push(e.charCodeAt(i));
  }
  return new Uint8Array(a);
}

/* ---------- utilitas ---------- */
var $ = function (id) { return document.getElementById(id); };
var state = null, settings = null, busy = false;

function show(text, kind) {
  var m = $('msg');
  m.textContent = text;
  m.className = 'msg show ' + (kind || 'ok');
  if (kind !== 'err') setTimeout(function () { m.className = 'msg'; }, 3500);
}
function esc(s) {
  return String(s == null ? '' : s).replace(/[&<>""]/g, function (c) {
    return { '&':'&amp;', '<':'&lt;', '>':'&gt;', '""':'&quot;' }[c] || c;
  });
}
function clock(sec) {
  if (sec == null || sec < 0) return 'tanpa batas';
  var h = Math.floor(sec/3600), m = Math.floor((sec%3600)/60), s = sec%60;
  var p = function (n) { return (n<10?'0':'') + n; };
  return h > 0 ? h + ':' + p(m) + ':' + p(s) : p(m) + ':' + p(s);
}

function post(url, body) {
  return fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body || {})
  }).then(function (r) { return r.json().then(function (j) { return { code: r.status, body: j }; }); });
}

function act(command, arg1, arg2, payload) {
  return post('/api/action', {
    Command: command, Arg1: arg1 || '', Arg2: arg2 || '', Payload: payload || ''
  }).then(function (r) {
    if (r.code === 401) { showLogin(); throw new Error('Sesi berakhir, masuk lagi.'); }
    if (!r.body.ok) throw new Error(r.body.error || 'Perintah gagal.');
    return r.body.payload;
  });
}

/* ---------- masuk ---------- */
function showLogin() { $('login').className = ''; $('app').className = 'hide'; }
function showApp() { $('login').className = 'hide'; $('app').className = ''; }

$('btnLogin').onclick = function () {
  var pw = $('pw').value;
  if (!pw) { show('Masukkan password dulu.', 'err'); return; }
  var btn = this;
  btn.disabled = true; btn.textContent = 'Memeriksa...';
  post('/api/challenge', {}).then(function (r) {
    if (r.body.error) throw new Error(r.body.error);
    var ch = r.body;
    return new Promise(function (resolve) {
      setTimeout(function () {
        var key = pbkdf2(utf8(pw), b64d(ch.salt), ch.iterations);
        resolve(b64e(hmac(key, b64d(ch.nonce))));
      }, 30);
    }).then(function (proof) {
      return post('/api/login', { nonce: ch.nonce, proof: proof });
    });
  }).then(function (r) {
    if (!r.body.ok) throw new Error(r.body.error || 'Gagal masuk.');
    $('pw').value = '';
    showApp();
    return refresh();
  }).catch(function (e) {
    show(e.message, 'err');
  }).then(function () {
    btn.disabled = false; btn.textContent = 'Masuk';
  });
};
$('pw').onkeydown = function (e) { if (e.key === 'Enter') $('btnLogin').click(); };

$('btnLogout').onclick = function () {
  post('/api/logout', {}).then(function () { showLogin(); });
};

/* ---------- tab ---------- */
var tabs = ['ringkasan','misi','aplikasi','aturan'];
Array.prototype.forEach.call(document.querySelectorAll('.tabs button'), function (b) {
  b.onclick = function () {
    Array.prototype.forEach.call(document.querySelectorAll('.tabs button'), function (x) { x.className = ''; });
    b.className = 'on';
    tabs.forEach(function (t) {
      $('tab-' + t).className = (t === b.getAttribute('data-tab')) ? '' : 'hide';
    });
  };
});

/* ---------- muat data ---------- */
function refresh() {
  return fetch('/api/state').then(function (r) {
    if (r.status === 401) { showLogin(); return null; }
    return r.json();
  }).then(function (j) {
    if (!j) return;
    state = j.status; settings = j.settings;
    if (!state) { show('Agent belum mengirim data.', 'err'); return; }
    if (!j.fresh) show('Agent di komputer anak tidak merespons.', 'err');
    render();
  }).catch(function (e) { show('Gagal memuat: ' + e.message, 'err'); });
}

function render() {
  $('daySub').textContent = state.Day + ' - ' +
    (state.IsWeekend ? 'akhir pekan' : 'hari sekolah') +
    ' - reset pukul ' + state.ResetsAtText +
    (state.Paused ? ' - DIJEDA' : '') + (state.Bedtime ? ' - JAM TIDUR' : '');

  // ringkasan jatah komputer
  var c = '';
  if (state.SessionEnabled) {
    var left = state.SessionRemainingSeconds;
    var cls = left <= 0 ? 'bad' : (left < 600 ? 'warn' : 'good');
    c = ""<h2>Waktu komputer</h2><div class='big' style='color:var(--"" + cls + "")'>"" +
        clock(left) + ""</div><div class='muted'>terpakai "" +
        Math.round(state.SessionUsedSeconds/60) + "" menit dari "" +
        Math.round(state.SessionLimitSeconds/60) + "" menit"" +
        (state.SessionBonusMinutes ? "" (termasuk bonus "" + state.SessionBonusMinutes + "" menit)"" : """") +
        ""</div>"";
  } else {
    c = ""<h2>Waktu komputer</h2><div class='muted'>Batas pemakaian komputer sedang mati. "" +
        ""Aktifkan di tab Aturan kalau ingin layar terkunci setelah sekian menit.</div>"";
  }
  $('cardSession').innerHTML = c;

  // daftar pemakaian
  var u = '';
  (state.Apps || []).forEach(function (a) {
    var cls = a.Blocked ? 'bad' : (a.RemainingSeconds >= 0 && a.RemainingSeconds < 600 ? 'warn' : 'good');
    u += ""<div class='item'><div class='row space'><div class='grow'><b>"" + esc(a.Name) + ""</b>"" +
         ""<div class='muted'>"" + (a.Blocked ? esc(a.BlockReason) :
            'terpakai ' + Math.round(a.UsedSeconds/60) + ' menit' +
            (a.LimitSeconds >= 0 ? ' dari ' + Math.round(a.LimitSeconds/60) + ' menit' : ' (tanpa batas)')) +
         ""</div></div><span class='pill "" + cls + ""'>"" +
         (a.Blocked ? 'habis' : clock(a.RemainingSeconds)) + ""</span></div>"" +
         ""<div class='row' style='margin-top:8px'>"" +
         ""<button data-appbonus='"" + esc(a.Process) + ""' data-min='15'>+15 mnt</button>"" +
         ""<button data-appbonus='"" + esc(a.Process) + ""' data-min='30'>+30 mnt</button>"" +
         ""</div></div>"";
  });
  $('usageList').innerHTML = u || ""<div class='muted'>Belum ada aplikasi yang dibatasi.</div>"";

  renderMissions();
  renderApps();
  renderRules();
  wire();
}

function renderMissions() {
  var list = state.Missions || [];
  var pend = list.filter(function (m) { return m.Status === 'submitted'; }).length;
  var badge = $('misiBadge');
  badge.textContent = pend ? pend : '';
  badge.className = pend ? 'pill bad' : 'pill';

  var h = '';
  list.forEach(function (m) {
    var cls = m.Status === 'submitted' ? 'warn' : (m.Status === 'approved' ? 'good' : '');
    h += ""<div class='item'><div class='row space'><div class='grow'><b>"" + esc(m.Title) + ""</b>"" +
         (m.Detail ? ""<div class='muted'>"" + esc(m.Detail) + ""</div>"" : '') +
         ""<div class='muted'>"" + esc(m.RewardText) + ""</div>"" +
         (m.ChildNote ? ""<div class='muted'>Catatan anak: "" + esc(m.ChildNote) + ""</div>"" : '') +
         ""</div><span class='pill "" + cls + ""'>"" + esc(m.StatusText) + ""</span></div>"";
    if (m.Status === 'submitted' || m.Status === 'available' || m.Status === 'rejected') {
      h += ""<div class='row' style='margin-top:8px'>"" +
           ""<button class='good' data-decide='"" + esc(m.Id) + ""' data-ok='1'>Lulus</button>"" +
           ""<button class='bad' data-decide='"" + esc(m.Id) + ""' data-ok='0'>Belum lulus</button>"" +
           ""<button data-delmission='"" + esc(m.Id) + ""'>Hapus misi</button></div>"";
    } else {
      h += ""<div class='row' style='margin-top:8px'>"" +
           ""<button data-delmission='"" + esc(m.Id) + ""'>Hapus misi</button></div>"";
    }
    h += ""</div>"";
  });
  $('missionList').innerHTML = h || ""<div class='muted'>Belum ada misi. Tambahkan di bawah.</div>"";

  var sel = $('mTarget'), cur = sel.value;
  var opts = ""<option value='SESSION'>Waktu komputer (semua kegiatan)</option>"" +
             ""<option value='TOTAL'>Total aplikasi yang diawasi</option>"";
  ((settings && settings.Apps) || []).forEach(function (a) {
    opts += ""<option value='"" + esc(a.Process) + ""'>"" + esc(a.Name) + ""</option>"";
  });
  sel.innerHTML = opts;
  if (cur) sel.value = cur;
}

function renderApps() {
  if (!settings) return;
  var h = '';
  (settings.Apps || []).forEach(function (a, i) {
    h += ""<div class='item'><b>"" + esc(a.Name) + ""</b> <span class='muted'>"" + esc(a.Process) + "".exe</span>"" +
         ""<div class='two'>"" +
         ""<div><label>Hari sekolah (menit, -1 = bebas)</label>"" +
         ""<input type='number' data-app='"" + i + ""' data-f='WeekdayMinutes' value='"" + a.WeekdayMinutes + ""'></div>"" +
         ""<div><label>Akhir pekan (menit, -1 = bebas)</label>"" +
         ""<input type='number' data-app='"" + i + ""' data-f='WeekendMinutes' value='"" + a.WeekendMinutes + ""'></div>"" +
         ""</div><label class='row' style='gap:8px'>"" +
         ""<input type='checkbox' style='width:auto' data-app='"" + i + ""' data-f='Enabled'"" +
         (a.Enabled ? "" checked"" : """") + ""><span>Berlakukan pembatasan</span></label></div>"";
  });
  $('appList').innerHTML = h || ""<div class='muted'>Belum ada aplikasi.</div>"";
}

function renderRules() {
  if (!settings) return;
  $('sEnabled').checked = !!settings.SessionEnabled;
  $('sWd').value = settings.SessionWeekdayMinutes;
  $('sWe').value = settings.SessionWeekendMinutes;
  $('sIdle').value = settings.SessionIdleMinutes;
  $('sAct').value = settings.SessionAction || 'lock';
  $('bEnabled').checked = !!settings.BedtimeEnabled;
  $('bStart').value = settings.BedtimeStart || '';
  $('bEnd').value = settings.BedtimeEnd || '';
  $('rHour').value = settings.ResetHour;
  $('rGrace').value = settings.GraceSeconds;
}

/* ---------- aksi ---------- */
function run(promise, okText) {
  if (busy) return;
  busy = true;
  promise.then(function () {
    if (okText) show(okText, 'ok');
    return refresh();
  }).catch(function (e) {
    show(e.message, 'err');
  }).then(function () { busy = false; });
}

function wire() {
  Array.prototype.forEach.call(document.querySelectorAll('[data-bonus]'), function (b) {
    b.onclick = function () {
      run(act('BONUS', 'SESSION', b.getAttribute('data-bonus')), 'Waktu komputer diperbarui.');
    };
  });
  Array.prototype.forEach.call(document.querySelectorAll('[data-pause]'), function (b) {
    b.onclick = function () {
      run(act('PAUSE', b.getAttribute('data-pause')), 'Jeda diperbarui.');
    };
  });
  Array.prototype.forEach.call(document.querySelectorAll('[data-appbonus]'), function (b) {
    b.onclick = function () {
      run(act('BONUS', b.getAttribute('data-appbonus'), b.getAttribute('data-min')), 'Bonus diberikan.');
    };
  });
  Array.prototype.forEach.call(document.querySelectorAll('[data-decide]'), function (b) {
    b.onclick = function () {
      var ok = b.getAttribute('data-ok') === '1';
      var note = prompt(ok ? 'Pesan untuk anak (boleh kosong):'
                           : 'Kenapa belum lulus? (boleh kosong)', '');
      if (note === null) return;
      run(act('MISSIONDECIDE', b.getAttribute('data-decide'), ok ? 'approve' : 'reject', note),
          ok ? 'Misi dinyatakan lulus, hadiah diberikan.' : 'Misi ditandai belum lulus.');
    };
  });
  Array.prototype.forEach.call(document.querySelectorAll('[data-delmission]'), function (b) {
    b.onclick = function () {
      if (!confirm('Hapus misi ini?')) return;
      var id = b.getAttribute('data-delmission');
      settings.Missions = (settings.Missions || []).filter(function (m) { return m.Id !== id; });
      run(act('SETSETTINGS', '', '', JSON.stringify(settings)), 'Misi dihapus.');
    };
  });
}

$('btnAddMission').onclick = function () {
  var title = $('mTitle').value.trim();
  if (!title) { show('Judul misi tidak boleh kosong.', 'err'); return; }
  settings.Missions = settings.Missions || [];
  settings.Missions.push({
    Id: '', Title: title, Detail: $('mDetail').value.trim(),
    RewardMinutes: parseInt($('mReward').value, 10) || 0,
    RewardTarget: $('mTarget').value, Repeat: $('mRepeat').value,
    Active: true, NeedsNote: $('mNote').checked
  });
  run(act('SETSETTINGS', '', '', JSON.stringify(settings)), 'Misi ditambahkan.');
  $('mTitle').value = ''; $('mDetail').value = '';
};

$('btnSaveApps').onclick = function () {
  Array.prototype.forEach.call(document.querySelectorAll('[data-app]'), function (el) {
    var a = settings.Apps[parseInt(el.getAttribute('data-app'), 10)];
    var f = el.getAttribute('data-f');
    a[f] = (el.type === 'checkbox') ? el.checked : (parseInt(el.value, 10) || 0);
  });
  run(act('SETSETTINGS', '', '', JSON.stringify(settings)), 'Batas aplikasi disimpan.');
};

$('btnSaveRules').onclick = function () {
  settings.SessionEnabled = $('sEnabled').checked;
  settings.SessionWeekdayMinutes = parseInt($('sWd').value, 10);
  settings.SessionWeekendMinutes = parseInt($('sWe').value, 10);
  settings.SessionIdleMinutes = parseInt($('sIdle').value, 10) || 0;
  settings.SessionAction = $('sAct').value;
  settings.BedtimeEnabled = $('bEnabled').checked;
  settings.BedtimeStart = $('bStart').value.trim();
  settings.BedtimeEnd = $('bEnd').value.trim();
  settings.ResetHour = parseInt($('rHour').value, 10) || 0;
  settings.GraceSeconds = parseInt($('rGrace').value, 10) || 0;
  run(act('SETSETTINGS', '', '', JSON.stringify(settings)), 'Aturan disimpan.');
};

/* ---------- mulai ---------- */
fetch('/api/state').then(function (r) {
  if (r.status === 401) { showLogin(); return; }
  showApp();
  return refresh();
}).catch(function () { showLogin(); });

setInterval(function () {
  if ($('app').className !== 'hide' && !busy) refresh();
}, 10000);
</script>
</body>
</html>";
    }
}
