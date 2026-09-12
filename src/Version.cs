namespace ScreenTimeGuard
{
    /// <summary>
    /// Sumber tunggal nomor versi. build.ps1 membaca nilai di sini untuk
    /// menstempel versi file .exe, dan make-release.ps1 memakainya untuk
    /// membuat manifest pembaruan.
    /// </summary>
    public static class AppInfo
    {
        public const string Version = "1.0.0";

        public const string Name = "Screen Time Guard";

        /// <summary>Alamat manifest pembaruan bawaan (bisa diubah lewat settings.json).</summary>
        public const string DefaultUpdateUrl =
            "https://raw.githubusercontent.com/dennypa77/parental-screentime/main/release/latest.json";

        public const string TaskAgent = "ScreenTimeGuard Agent";
        public const string TaskTray = "ScreenTimeGuard Tray";
    }
}
