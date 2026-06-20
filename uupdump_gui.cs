// uupdump_gui.cs  -  UUP Dump Fetcher  (WinForms .NET 4.8 / C# 5 compatible)
//
// 7z.exe, 7z.dll, wimlib-imagex.exe and libwim-15.dll are embedded inside the
// compiled exe as managed resources and extracted to a per-version temp folder
// at runtime.  No sibling files need to be distributed alongside the exe.
//
// One-liner compile (run from the folder that contains all four binaries):
//
//   %SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /platform:x64 /optimize+ /win32icon:triquetra.ico /resource:7z.exe,tools.7z.exe /resource:7z.dll,tools.7z.dll /resource:wimlib-imagex.exe,tools.wimlib-imagex.exe /resource:libwim-15.dll,tools.libwim-15.dll /out:uupdump.exe uupdump_gui.cs
//
// Drop /win32icon:triquetra.ico if you do not have the icon file.
// The logical resource names (tools.*.xxx) must match EmbeddedTools.ResourceNames.
//
// No external NuGet packages - HTML is parsed with regex, just like Triquetra.cs.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("UUP Dump Fetcher")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace UupDumpFetcher
{
    // =========================================================================
    //  P/Invoke
    // =========================================================================
    static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        public const int SW_HIDE = 0;
    }

    // =========================================================================
    //  Embedded-tool extractor
    //  Resources are embedded with logical names matching ResourceNames[].
    //  On first run they are extracted to a stable temp subfolder named after
    //  the assembly version so upgrades automatically get a fresh copy.
    //  Subsequent runs reuse the existing folder (instant, no I/O).
    // =========================================================================
    static class EmbeddedTools
    {
        // Logical resource name -> output filename (must match /resource: flags)
        static readonly string[][] ResourceNames = new string[][]
        {
            new string[] { "tools.7z.exe",            "7z.exe"            },
            new string[] { "tools.7z.dll",            "7z.dll"            },
            new string[] { "tools.wimlib-imagex.exe", "wimlib-imagex.exe" },
            new string[] { "tools.libwim-15.dll",     "libwim-15.dll"     },
        };

        // Populated by Extract(); consumed by Paths.
        public static string ToolDir { get; private set; }

        public static void Extract()
        {
            // Subfolder named after assembly version so a rebuilt exe never
            // reuses a stale extraction from an older build.
            string ver = "0";
            try
            {
                AssemblyName an = Assembly.GetExecutingAssembly().GetName();
                ver = an.Version != null ? an.Version.ToString() : "0";
            }
            catch { }

            ToolDir = Path.Combine(Path.GetTempPath(), "uupdump_tools_" + ver);

            // If every expected file exists we skip extraction entirely.
            if (AllPresent()) return;

            Directory.CreateDirectory(ToolDir);

            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string[] pair in ResourceNames)
            {
                string resourceName = pair[0];
                string fileName     = pair[1];
                string destPath     = Path.Combine(ToolDir, fileName);

                if (File.Exists(destPath)) continue;

                using (Stream src = asm.GetManifestResourceStream(resourceName))
                {
                    if (src == null)
                        throw new Exception(
                            "Embedded resource not found: " + resourceName + "\n\n" +
                            "Re-compile with the correct /resource: flags.");

                    // Write via temp name so a mid-write crash never leaves a
                    // truncated file that would pass the AllPresent() check.
                    string tmp = destPath + ".tmp";
                    using (FileStream dst = File.Create(tmp))
                    {
                        byte[] buf = new byte[65536];
                        int read;
                        while ((read = src.Read(buf, 0, buf.Length)) > 0)
                            dst.Write(buf, 0, read);
                    }
                    File.Move(tmp, destPath);
                }
            }
        }

        static bool AllPresent()
        {
            if (string.IsNullOrEmpty(ToolDir)) return false;
            foreach (string[] pair in ResourceNames)
                if (!File.Exists(Path.Combine(ToolDir, pair[1]))) return false;
            return true;
        }
    }

    // =========================================================================
    //  Program entry point
    // =========================================================================
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender,
                System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show("Unhandled error:\n\n" + e.Exception.ToString(),
                    "UUP Dump Fetcher – Fatal Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender,
                UnhandledExceptionEventArgs e)
            {
                MessageBox.Show("Fatal error:\n\n" + e.ExceptionObject.ToString(),
                    "UUP Dump Fetcher – Fatal Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            try
            {
                NativeMethods.ShowWindow(NativeMethods.GetConsoleWindow(), NativeMethods.SW_HIDE);

                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
                // Accept all SSL certificates (mirrors Triquetra behaviour)
                ServicePointManager.ServerCertificateValidationCallback =
                    delegate(object s,
                        System.Security.Cryptography.X509Certificates.X509Certificate c,
                        System.Security.Cryptography.X509Certificates.X509Chain ch,
                        System.Net.Security.SslPolicyErrors err) { return true; };

                if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                {
                    MessageBox.Show("This program runs on Windows only.",
                        "Unsupported", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Extract 7z and wimlib from embedded resources, then tell
                // Paths where they landed.  Fast no-op on subsequent runs.
                EmbeddedTools.Extract();
                Paths.InitTools();

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show("Startup error:\n\n" + ex.ToString(),
                    "UUP Dump Fetcher – Fatal Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // =========================================================================
    //  Theme  (identical palette to Triquetra.cs + dark-mode registry detection)
    // =========================================================================
    static class Theme
    {
        public static bool IsDark;

        static Theme()
        {
            IsDark = DetectDarkMode();
        }

        static bool DetectDarkMode()
        {
            try
            {
                RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key == null) return true;
                object v = key.GetValue("AppsUseLightTheme");
                key.Close();
                return v == null || (int)v == 0;
            }
            catch { return true; }
        }

        public static Color Accent    { get { return Color.FromArgb(0x00, 0x78, 0xD4); } }
        public static Color AccentHov { get { return Color.FromArgb(0x10, 0x6E, 0xBE); } }
        public static Color AccentFg  { get { return Color.White; } }
        public static Color Bg        { get { return IsDark ? Color.FromArgb(0x1A,0x1A,0x2E) : Color.FromArgb(0xF3,0xF3,0xF8); } }
        public static Color Surface   { get { return IsDark ? Color.FromArgb(0x25,0x25,0x40) : Color.White; } }
        public static Color Border    { get { return IsDark ? Color.FromArgb(0x35,0x35,0x60) : Color.FromArgb(0xD0,0xD0,0xE0); } }
        public static Color Fg        { get { return IsDark ? Color.FromArgb(0xE8,0xE8,0xF0) : Color.FromArgb(0x1A,0x1A,0x2E); } }
        public static Color FgDim     { get { return IsDark ? Color.FromArgb(0x88,0x88,0xAA) : Color.FromArgb(0x60,0x60,0x7A); } }
        public static Color Ok        { get { return IsDark ? Color.FromArgb(0x6C,0xCB,0x6C) : Color.FromArgb(0x21,0x7A,0x21); } }
        public static Color Warn      { get { return IsDark ? Color.FromArgb(0xF0,0xA0,0x50) : Color.FromArgb(0xB8,0x5C,0x00); } }
        public static Color Err       { get { return IsDark ? Color.FromArgb(0xE0,0x50,0x50) : Color.FromArgb(0xC4,0x00,0x00); } }
        public static Color SelBg     { get { return IsDark ? Color.FromArgb(0x3A,0x3A,0x5C) : Color.FromArgb(0xDD,0xE8,0xF5); } }
        public static Color LogBg     { get { return IsDark ? Color.FromArgb(0x12,0x12,0x1E) : Color.FromArgb(0xFA,0xFA,0xFA); } }
        public static Color LogFg     { get { return IsDark ? Color.FromArgb(0xAA,0xAA,0xCC) : Color.FromArgb(0x30,0x30,0x4A); } }
        public static Color EntryBg   { get { return IsDark ? Color.FromArgb(0x2E,0x2E,0x4E) : Color.White; } }
        public static Color EntryFg   { get { return IsDark ? Color.FromArgb(0xE8,0xE8,0xF0) : Color.FromArgb(0x1A,0x1A,0x2E); } }
        public static Color Danger    { get { return Color.FromArgb(0xC4, 0x00, 0x00); } }
    }

    // =========================================================================
    //  Control factory  (mirrors Triquetra.cs Ctrl class)
    // =========================================================================
    static class Ctrl
    {
        public static Font SegoUI(float size, FontStyle style)
        { return new Font("Segoe UI", size, style); }
        public static Font SegoUI(float size) { return SegoUI(size, FontStyle.Regular); }
        public static Font SegoUI()           { return SegoUI(9.75f); }

        public static Button AccentButton(string text)
        {
            Button b = new Button();
            b.Text      = text; b.FlatStyle = FlatStyle.Flat;
            b.Font      = SegoUI(9.75f, FontStyle.Bold);
            b.BackColor = Theme.Accent; b.ForeColor = Theme.AccentFg;
            b.Height    = 34; b.Cursor = Cursors.Hand;
            b.FlatAppearance.BorderSize             = 0;
            b.FlatAppearance.MouseOverBackColor     = Theme.AccentHov;
            return b;
        }

        public static Button NormalButton(string text)
        {
            Button b = new Button();
            b.Text      = text; b.FlatStyle = FlatStyle.Flat;
            b.Font      = SegoUI(); b.BackColor = Theme.Surface; b.ForeColor = Theme.Fg;
            b.Height    = 30; b.Cursor = Cursors.Hand;
            b.FlatAppearance.BorderSize         = 1;
            b.FlatAppearance.BorderColor        = Theme.Border;
            b.FlatAppearance.MouseOverBackColor = Theme.SelBg;
            return b;
        }

        public static Button DangerButton(string text)
        {
            Button b = new Button();
            b.Text      = text; b.FlatStyle = FlatStyle.Flat;
            b.Font      = SegoUI(); b.BackColor = Theme.Danger; b.ForeColor = Color.White;
            b.Height    = 30; b.Cursor = Cursors.Hand;
            b.FlatAppearance.BorderSize         = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xA0, 0x00, 0x00);
            return b;
        }

        public static Label MakeLabel(string text, bool dim, float size)
        {
            Label l = new Label();
            l.Text      = text; l.Font = SegoUI(size);
            l.ForeColor = dim ? Theme.FgDim : Theme.Fg;
            l.BackColor = Color.Transparent; l.AutoSize = true;
            return l;
        }
        public static Label MakeLabel(string text, bool dim) { return MakeLabel(text, dim, 9.75f); }
        public static Label MakeLabel(string text)           { return MakeLabel(text, false); }

        public static TextBox MakeEntry(bool password)
        {
            TextBox tb = new TextBox();
            tb.Font        = SegoUI();
            if (Theme.IsDark)
            {
                tb.BackColor   = Theme.EntryBg;
                tb.ForeColor   = Theme.EntryFg;
                tb.BorderStyle = BorderStyle.FixedSingle;
            }
            else
            {
                tb.BackColor   = SystemColors.Window;
                tb.ForeColor   = SystemColors.WindowText;
                tb.BorderStyle = BorderStyle.Fixed3D;
            }
            if (password) tb.UseSystemPasswordChar = true;
            return tb;
        }
        public static TextBox MakeEntry() { return MakeEntry(false); }

        public static CheckBox MakeCheck(string text)
        {
            CheckBox cb = new CheckBox();
            cb.Text      = text; cb.Font = SegoUI();
            cb.ForeColor = Theme.Fg; cb.BackColor = Color.Transparent; cb.AutoSize = true;
            return cb;
        }
    }

    // =========================================================================
    //  Paths & constants
    // =========================================================================
    static class Paths
    {
        public static readonly string ExeDir;
        public static readonly string DataDir;      // C:\ProgramData\uupdump
        public static readonly string ConfigFile;   // <DataDir>\uupdump.ini
        public static readonly string LogFile;      // <DataDir>\uupdump.log

        // Download directory is configurable (set from AppConfig after load).
        // StateFile lives inside it.
        public static string DownloadDir { get; private set; }
        public static string StateFile   { get; private set; }

        // Set by InitTools() after EmbeddedTools.Extract() runs.
        public static string SevenZip { get; private set; }
        public static string WimLib   { get; private set; }

        static Paths()
        {
            ExeDir = Path.GetDirectoryName(
                         Process.GetCurrentProcess().MainModule.FileName);

            // Config + log live in C:\ProgramData\uupdump (created if missing).
            string programData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            DataDir    = Path.Combine(programData, "uupdump");
            try { Directory.CreateDirectory(DataDir); } catch { }

            ConfigFile = Path.Combine(DataDir, "uupdump.ini");
            LogFile    = Path.Combine(DataDir, "uupdump.log");

            // Default download dir (overridden by config in InitDownloadDir())
            SetDownloadDir(Path.Combine(ExeDir, "downloads"));
        }

        // Called after AppConfig.Load() to apply the configured download dir.
        public static void SetDownloadDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(ExeDir, "downloads");
            DownloadDir = dir;
            StateFile   = Path.Combine(DownloadDir, "downloaded_builds.txt");
            try { Directory.CreateDirectory(DownloadDir); } catch { }
        }

        // Called from Program.Main() after EmbeddedTools.Extract() succeeds.
        public static void InitTools()
        {
            string dir = EmbeddedTools.ToolDir;
            SevenZip   = Path.Combine(dir, "7z.exe");
            WimLib     = Path.Combine(dir, "wimlib-imagex.exe");
        }
    }

    // =========================================================================
    //  Config  (INI-style, mirrors Triquetra AppConfig)
    // =========================================================================
    static class AppConfig
    {
        public static string UploadDest         = "";
        public static string DownloadDir        = "";   // empty = default (<exe>\\downloads)
        public static bool   CleanupAfterUpload = false;
        public static bool   MonitorMode        = false;
        public static bool   RunProcessor       = false;
        public static bool   SkipUpload         = false;
        // Which Windows 11 branches to scan (all enabled by default)
        public static bool   Branch25H2         = true;
        public static bool   Branch26H1         = true;
        public static bool   Branch23H2         = true;
        public static string UserAgent          =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36";

        public static void Load()
        {
            if (!File.Exists(Paths.ConfigFile)) return;
            try
            {
                string[] lines = File.ReadAllLines(Paths.ConfigFile, Encoding.UTF8);
                bool inSection = false;
                foreach (string line in lines)
                {
                    string l = line.Trim();
                    if (l.StartsWith("["))
                    { inSection = l == "[uupdump]"; continue; }
                    if (!inSection || !l.Contains("=")) continue;
                    int    idx = l.IndexOf('=');
                    string k   = l.Substring(0, idx).Trim().ToLower();
                    string v   = l.Substring(idx + 1).Trim();
                    switch (k)
                    {
                        case "upload_dest":          UploadDest         = v; break;
                        case "download_dir":         DownloadDir        = v; break;
                        case "cleanup_after_upload": CleanupAfterUpload = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "monitor_mode":         MonitorMode        = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "run_processor":        RunProcessor       = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "skip_upload":          SkipUpload         = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "branch_25h2":          Branch25H2         = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "branch_26h1":          Branch26H1         = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "branch_23h2":          Branch23H2         = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "user_agent":           if (v.Length > 0) UserAgent = v; break;
                    }
                }
            }
            catch (Exception ex) { Logger.Log("Failed to read config: " + ex.Message); }
        }

        public static void Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[uupdump]");
            sb.AppendLine("upload_dest = "         + UploadDest);
            sb.AppendLine("download_dir = "        + DownloadDir);
            sb.AppendLine("cleanup_after_upload = " + CleanupAfterUpload.ToString().ToLower());
            sb.AppendLine("monitor_mode = "         + MonitorMode.ToString().ToLower());
            sb.AppendLine("run_processor = "        + RunProcessor.ToString().ToLower());
            sb.AppendLine("skip_upload = "          + SkipUpload.ToString().ToLower());
            sb.AppendLine("branch_25h2 = "          + Branch25H2.ToString().ToLower());
            sb.AppendLine("branch_26h1 = "          + Branch26H1.ToString().ToLower());
            sb.AppendLine("branch_23h2 = "          + Branch23H2.ToString().ToLower());
            sb.AppendLine("user_agent = "           + UserAgent);
            try { File.WriteAllText(Paths.ConfigFile, sb.ToString(), Encoding.UTF8); }
            catch (Exception ex) { Logger.Log("Failed to save config: " + ex.Message); }
        }
    }

    // =========================================================================
    //  Logger  (identical pattern to Triquetra.cs)
    // =========================================================================
    static class Logger
    {
        static readonly string _logFile = Paths.LogFile;

        public static event Action<string> MessageLogged;
        public static string LogFilePath { get { return _logFile; } }

        // Log to both file and UI panel.
        public static void Log(string msg)
        {
            WriteFile(msg);
            Action<string> h = MessageLogged;
            if (h != null) h(msg);
        }

        // Log to file only (verbose tool output like 7z lines).
        public static void LogFile(string msg)
        {
            WriteFile(msg);
        }

        // Log to UI panel only — not written to disk.
        // Used for routine scan chatter that would otherwise flood the log file.
        public static void LogUiOnly(Action<string> uiLog, string msg)
        {
            Action<string> h = MessageLogged;
            if (h != null) h(msg);
        }

        static void WriteFile(string msg)
        {
            string entry = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC") + "\t" + msg;
            try { File.AppendAllText(_logFile, entry + "\r\n", Encoding.UTF8); }
            catch { /* best effort */ }
        }

        public static void ClearFile()
        {
            File.WriteAllText(_logFile, "", Encoding.UTF8);
        }
    }

    // =========================================================================
    //  Download state  (mirrors downloaded_builds.txt logic)
    // =========================================================================
    static class BuildState
    {
        public static HashSet<string> Load()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            if (!File.Exists(Paths.StateFile)) return set;
            foreach (string l in File.ReadAllLines(Paths.StateFile, Encoding.UTF8))
            {
                string t = l.Trim();
                if (t.Length > 0) set.Add(t);
            }
            return set;
        }

        public static void Save(HashSet<string> builds)
        {
            try
            {
                Directory.CreateDirectory(Paths.DownloadDir);
                List<string> sorted = new List<string>(builds);
                sorted.Sort(StringComparer.Ordinal);
                File.WriteAllLines(Paths.StateFile, sorted.ToArray(), Encoding.UTF8);
            }
            catch (Exception ex) { Logger.Log("Failed to save state: " + ex.Message); }
        }
    }

    // =========================================================================
    //  HTTP helpers
    // =========================================================================
    static class Net
    {
        static HttpWebRequest MakeRequest(string url, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout         = timeoutMs;
            req.UserAgent       = AppConfig.UserAgent;
            req.Headers["Accept-Language"] = "en-US,en;q=0.9";
            req.Referer         = "https://uupdump.net/";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return req;
        }

        // Fetch text with retry (403 back-off, mirrors Python fetch_html)
        public static string FetchText(string url, int retries, Action<string> log)
        {
            Random rnd = new Random();
            for (int attempt = 0; attempt < retries; attempt++)
            {
                try
                {
                    if (log != null) Logger.LogUiOnly(log, "Fetching: " + url);
                    HttpWebRequest req = MakeRequest(url, 30000);
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        if (resp.StatusCode == HttpStatusCode.Forbidden)
                        {
                            if (log != null) log("403 Forbidden – retrying...");
                            Thread.Sleep(rnd.Next(1000, 3000));
                            continue;
                        }
                        using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                            return sr.ReadToEnd();
                    }
                }
                catch (WebException wex)
                {
                    HttpWebResponse wr = wex.Response as HttpWebResponse;
                    if (wr != null && wr.StatusCode == HttpStatusCode.Forbidden)
                    {
                        if (log != null) log("403 – retrying...");
                        Thread.Sleep(rnd.Next(1000, 3000));
                        continue;
                    }
                    if (log != null) log("Error: " + wex.Message + " – retrying...");
                    Thread.Sleep(rnd.Next(2000, 4000));
                }
            }
            throw new Exception("Failed to fetch " + url + " after " + retries + " attempts.");
        }
        public static string FetchText(string url) { return FetchText(url, 3, null); }

        // Download a file with progress callback; returns false on checksum mismatch.
        // Supports resume of a partial .part file and hashes while downloading.
        public static bool DownloadFile(string url, string destPath,
            string expectedSha1, Action<string> log,
            Action<long, long, double> progressCb, ManualResetEvent stopEvent)
        {
            string fileName = Path.GetFileName(destPath);

            // Already complete & verified?
            if (File.Exists(destPath))
            {
                if (string.Equals(Sha1File(destPath), expectedSha1,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (log != null) log("Already downloaded and verified: " + fileName);
                    return true;
                }
                if (log != null) log("Checksum mismatch – re-downloading: " + fileName);
                try { File.Delete(destPath); } catch { }
            }

            string partPath = destPath + ".part";
            long resumeFrom  = 0;

            // Incremental SHA-1 so we never re-read the whole file at the end.
            using (SHA1 sha = SHA1.Create())
            {
                // Resume: if a .part exists, hash what we have and continue.
                if (File.Exists(partPath))
                {
                    try
                    {
                        using (FileStream pf = File.OpenRead(partPath))
                        {
                            byte[] hb = new byte[1048576];
                            int hr;
                            while ((hr = pf.Read(hb, 0, hb.Length)) > 0)
                            {
                                if (stopEvent != null && stopEvent.WaitOne(0)) return false;
                                sha.TransformBlock(hb, 0, hr, hb, 0);
                                resumeFrom += hr;
                            }
                        }
                        if (log != null && resumeFrom > 0)
                            log(string.Format("Resuming {0} from {1:F1} MB",
                                fileName, resumeFrom / 1048576.0));
                    }
                    catch
                    {
                        // Corrupt partial: start over.
                        resumeFrom = 0;
                        sha.Initialize();
                        try { File.Delete(partPath); } catch { }
                    }
                }

                HttpWebRequest req = MakeRequest(url, 60000);
                if (resumeFrom > 0) req.AddRange(resumeFrom);

                long total;
                long done = resumeFrom;
                DateTime start = DateTime.UtcNow;

                try
                {
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        // If the server ignored Range (returns 200 not 206), restart.
                        if (resumeFrom > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
                        {
                            if (log != null) log("Server ignored resume – restarting download.");
                            resumeFrom = 0; done = 0;
                            sha.Initialize();
                            try { File.Delete(partPath); } catch { }
                        }

                        long remaining = resp.ContentLength;
                        total = resumeFrom + (remaining > 0 ? remaining : 0);

                        // Disk-space pre-check on the destination drive.
                        if (!HasFreeSpace(destPath, total - resumeFrom, log))
                            return false;

                        using (Stream src = resp.GetResponseStream())
                        using (FileStream dst = new FileStream(partPath,
                                   resumeFrom > 0 ? FileMode.Append : FileMode.Create,
                                   FileAccess.Write, FileShare.None, 65536))
                        {
                            byte[] buf = new byte[65536];
                            int read;
                            while ((read = src.Read(buf, 0, buf.Length)) > 0)
                            {
                                if (stopEvent != null && stopEvent.WaitOne(0))
                                {
                                    if (log != null) log("Download aborted (partial saved).");
                                    return false;
                                }
                                dst.Write(buf, 0, read);
                                sha.TransformBlock(buf, 0, read, buf, 0);
                                done += read;
                                double elapsed = (DateTime.UtcNow - start).TotalSeconds + 1e-9;
                                double speed   = (done - resumeFrom) / 1048576.0 / elapsed;
                                if (progressCb != null) progressCb(done, total, speed);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (log != null) log("Download error: " + ex.Message);
                    return false;
                }

                // Finalise the streaming hash.
                sha.TransformFinalBlock(new byte[0], 0, 0);
                StringBuilder sb = new StringBuilder(40);
                foreach (byte b in sha.Hash) sb.Append(b.ToString("x2"));
                string actual = sb.ToString();

                if (!string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
                {
                    if (log != null) log("Checksum mismatch for " + fileName + " – discarding.");
                    try { File.Delete(partPath); } catch { }
                    return false;
                }
            }

            // Hash matched: promote .part to the final name.
            try
            {
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(partPath, destPath);
            }
            catch (Exception ex)
            {
                if (log != null) log("Could not finalise " + fileName + ": " + ex.Message);
                return false;
            }
            if (log != null) log("Verified: " + fileName);
            return true;
        }

        // Check the destination drive has room for `needed` bytes (+5% margin).
        static bool HasFreeSpace(string destPath, long needed, Action<string> log)
        {
            if (needed <= 0) return true;
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(destPath));
                DriveInfo di = new DriveInfo(root);
                long required = needed + needed / 20 + 16 * 1048576; // +5% +16MB
                if (di.AvailableFreeSpace < required)
                {
                    if (log != null) log(string.Format(
                        "Not enough disk space on {0}: need {1:F1} MB, have {2:F1} MB.",
                        root, required / 1048576.0, di.AvailableFreeSpace / 1048576.0));
                    return false;
                }
            }
            catch { /* if we can't check, proceed */ }
            return true;
        }

        public static string Sha1File(string path)
        {
            using (SHA1 sha = SHA1.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(40);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static string Md5File(string path)
        {
            using (MD5 md5 = MD5.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] hash = md5.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(32);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // SHA256Cng routes through Windows CNG (bcrypt.dll), which transparently
        // uses the CPU's SHA extensions (SHA-NI) when the processor and OS both
        // support them. SHA256.Create() / SHA256Managed do NOT get this
        // acceleration - they are a pure managed implementation.
        public static string Sha256File(string path)
        {
            using (SHA256 sha = CreateAcceleratedSha256())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(64);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        static SHA256 CreateAcceleratedSha256()
        {
            try
            {
                // SHA256Cng uses CNG/bcrypt, which picks up SHA-NI automatically
                // on supporting hardware (Intel/AMD with SHA extensions) and OS.
                return new SHA256Cng();
            }
            catch
            {
                // Fall back to the standard managed implementation if CNG is
                // unavailable for any reason (older OS, restricted environment).
                return SHA256.Create();
            }
        }
    }

    // =========================================================================
    //  HTML parsing (regex-only; no HtmlAgilityPack dependency)
    // =========================================================================
    static class HtmlParser
    {
        // Match <a href="...">...</a>
        static readonly Regex _linkRe = new Regex(
            @"<a\s[^>]*href\s*=\s*(?:""([^""]*)""|'([^']*)')[^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        static readonly Regex _idRe = new Regex(
            @"[?&]id=([0-9a-f\-]+)", RegexOptions.IgnoreCase);

        static readonly Regex _buildNumRe = new Regex(
            @"\((\d+\.\d+)\)");

        // Parse the UUP Dump known.php table:
        //   id, name, arch, build_num
        public static List<BuildInfo> ParseKnownBuilds(string html)
        {
            // Row pattern: look for <tr> blocks containing selectlang links
            Regex rowRe = new Regex(
                @"<tr\b[^>]*>(.*?)</tr>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            Regex tdRe = new Regex(
                @"<td\b[^>]*>(.*?)</td>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            Regex textRe = new Regex(@"<[^>]+>");

            List<BuildInfo> result = new List<BuildInfo>();
            foreach (Match row in rowRe.Matches(html))
            {
                string rowHtml = row.Groups[1].Value;
                if (rowHtml.IndexOf("selectlang.php", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // Find the link
                Match linkM = _linkRe.Match(rowHtml);
                if (!linkM.Success) continue;
                string href = (linkM.Groups[1].Value.Length > 0
                    ? linkM.Groups[1].Value : linkM.Groups[2].Value).Trim();
                string name = textRe.Replace(linkM.Groups[3].Value, "").Trim();

                Match idM = _idRe.Match(href);
                if (!idM.Success) continue;
                string buildId = idM.Groups[1].Value;

                if (name.IndexOf("Framework", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Cumulative", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Preview",    StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Feature",    StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                Match bnM = _buildNumRe.Match(name);
                if (!bnM.Success) continue;
                string buildNum = bnM.Groups[1].Value;

                // Architecture is in the second <td>
                string arch = "";
                MatchCollection tds = tdRe.Matches(rowHtml);
                if (tds.Count > 1)
                    arch = textRe.Replace(tds[1].Groups[1].Value, "").Trim();

                result.Add(new BuildInfo
                {
                    Id       = buildId,
                    Name     = name,
                    Arch     = arch.ToLower().Replace("x64", "amd64"),
                    BuildNum = buildNum,
                });
            }
            return result;
        }

        // Parse get.php?...&aria2=2 response for file list
        // Format: URL  out=filename  checksum=sha-1=<hash>
        static readonly Regex _fileRe = new Regex(
            @"(https?://\S+)\s+out=(.+?)\s+checksum=sha-1=([0-9a-f]+)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        public static List<UupFile> ParseGetPage(string html)
        {
            List<UupFile> files = new List<UupFile>();
            foreach (Match m in _fileRe.Matches(html))
                files.Add(new UupFile
                {
                    Url      = m.Groups[1].Value,
                    Filename = m.Groups[2].Value,
                    Checksum = m.Groups[3].Value,
                });
            return files;
        }
    }

    // =========================================================================
    //  Data types
    // =========================================================================
    sealed class BuildInfo
    {
        public string Id;
        public string Name;
        public string Arch;
        public string BuildNum;
        public string Branch;
        public int[]  VersionTuple;
    }

    sealed class UupFile
    {
        public string Url;
        public string Filename;
        public string Checksum;
    }

    // =========================================================================
    //  External-tool helpers  (7z, wimlib)
    // =========================================================================
    static class ToolHelper
    {
        // Active child process — set while a tool runs, cleared when it exits.
        // Accessed only from the single worker thread so no lock needed.
        public static Process ActiveProcess;

        // Kill the active child process if one is running (called from UI thread).
        public static void KillActive()
        {
            Process p = ActiveProcess;
            if (p == null) return;
            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch { }
        }

        static ProcessStartInfo MakeHidden(string exe, string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute        = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError  = true;
            psi.CreateNoWindow         = true;
            psi.WorkingDirectory       = Path.GetDirectoryName(exe);
            return psi;
        }

        public static int Run7z(string arguments, Action<string> log,
            ManualResetEvent stopEvent = null)
        {
            if (!File.Exists(Paths.SevenZip))
                throw new FileNotFoundException("7z.exe not found: " + Paths.SevenZip);

            ProcessStartInfo psi = MakeHidden(Paths.SevenZip, arguments);
            using (Process p = Process.Start(psi))
            {
                ActiveProcess = p;
                // 7z produces many progress lines - send to log file only;
                // the UI panel shows only start/end messages from the caller.
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                { if (e.Data != null) Logger.LogFile(e.Data); };
                p.ErrorDataReceived  += delegate(object s, DataReceivedEventArgs e)
                { if (e.Data != null) Logger.LogFile(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                // Poll every 200 ms so Stop kills 7z promptly.
                while (!p.WaitForExit(200))
                {
                    if (stopEvent != null && stopEvent.WaitOne(0))
                    {
                        try { p.Kill(); } catch { }
                        p.WaitForExit();
                        ActiveProcess = null;
                        throw new OperationCanceledException("Stopped by user.");
                    }
                }
                ActiveProcess = null;
                return p.ExitCode;
            }
        }

        public static void ExtractMsu(string msuFile, string outDir, Action<string> log,
            ManualResetEvent stopEvent = null)
        {
            Directory.CreateDirectory(outDir);
            if (log != null) log("Extracting " + Path.GetFileName(msuFile) + "...");
            int rc = Run7z(string.Format("x \"{0}\" -o\"{1}\" -y", msuFile, outDir),
                log, stopEvent);
            if (rc != 0 && rc != 1)
                throw new Exception("7z failed on " + Path.GetFileName(msuFile) +
                    " (code " + rc + ")");
            if (log != null) log("MSU extraction complete.");
        }

        public static void ExtractWim(string wimFile, string outDir, Action<string> log,
            ManualResetEvent stopEvent = null)
        {
            Directory.CreateDirectory(outDir);
            if (log != null) log("Extracting WIM " + Path.GetFileName(wimFile) + "...");
            int rc = Run7z(string.Format("x \"{0}\" -o\"{1}\" -y", wimFile, outDir),
                log, stopEvent);
            if (rc != 0 && rc != 1)
                throw new Exception("7z WIM extract failed (code " + rc + ")");
            if (log != null) log("WIM extraction complete.");
        }

        public static void WimlibCapture(string sourceDir, string destEsd, Action<string> log,
            ManualResetEvent stopEvent = null)
        {
            if (!File.Exists(Paths.WimLib))
                throw new FileNotFoundException("wimlib-imagex.exe not found: " + Paths.WimLib);
            if (log != null) log("Creating ESD: " + Path.GetFileName(destEsd) + "...");

            string args = string.Format(
                "capture \"{0}\" \"{1}\" \"ESD Update\" --compress=LZMS --solid",
                sourceDir, destEsd);

            ProcessStartInfo psi = MakeHidden(Paths.WimLib, args);
            using (Process p = Process.Start(psi))
            {
                ActiveProcess = p;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    // All wimlib output goes to the log file only.
                    Logger.LogFile(e.Data);
                };
                p.ErrorDataReceived  += delegate(object s, DataReceivedEventArgs e)
                { if (e.Data != null) Logger.LogFile(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                while (!p.WaitForExit(200))
                {
                    if (stopEvent != null && stopEvent.WaitOne(0))
                    {
                        try { p.Kill(); } catch { }
                        p.WaitForExit();
                        ActiveProcess = null;
                        throw new OperationCanceledException("Stopped by user.");
                    }
                }
                ActiveProcess = null;
                if (p.ExitCode != 0)
                    throw new Exception("wimlib capture failed (code " + p.ExitCode + ")");
            }
        }
    }

    // =========================================================================
    //  Processor  (port of process_build / CreateAndUploadUpdates.ps1 logic)
    // =========================================================================
    static class Processor
    {
        public static bool ProcessBuild(string buildDir, string uploadDest,
            bool cleanup, Action<string> log, ManualResetEvent stopEvent)
        {
            Action chk = delegate()
            {
                if (stopEvent != null && stopEvent.WaitOne(0))
                    throw new OperationCanceledException("Stopped by user.");
            };

            // Locate required files
            string wimFile = FindFirst(buildDir, "*.wim");
            string psfFile = FindFirst(buildDir, "*.psf");
            string ssuCab  = FindFirst(buildDir, "*SSU*.cab");
            string ndpCab  = FindFirst(buildDir, "*NDP*.cab");

            // Extract MSU if anything is missing
            if (wimFile == null || psfFile == null || ssuCab == null)
            {
                string[] msus = Directory.GetFiles(buildDir, "*.msu");
                if (msus.Length == 0)
                    throw new FileNotFoundException("No MSU file found and required files missing.");

                // Pick the largest MSU
                string msuFile = msus[0];
                long   biggest = 0;
                foreach (string m in msus)
                {
                    long sz = new FileInfo(m).Length;
                    if (sz > biggest) { biggest = sz; msuFile = m; }
                }

                ToolHelper.ExtractMsu(msuFile, buildDir, log, stopEvent);

                // Hotpatch detection
                if (Directory.GetFiles(buildDir, "*hotpatch*",
                        SearchOption.AllDirectories).Length > 0)
                {
                    if (log != null) log("Hotpatch update detected – skipping processing.");
                    return false;
                }

                wimFile = FindFirst(buildDir, "*.wim");
                psfFile = FindFirst(buildDir, "*.psf");
                ssuCab  = FindFirst(buildDir, "*SSU*.cab");
                ndpCab  = FindFirst(buildDir, "*NDP*.cab");
            }

            if (wimFile == null || psfFile == null || ssuCab == null)
            {
                List<string> missing = new List<string>();
                if (wimFile == null) missing.Add("WIM");
                if (psfFile == null) missing.Add("PSF");
                if (ssuCab  == null) missing.Add("SSU CAB");
                throw new FileNotFoundException("Required files missing: " +
                    string.Join(", ", missing.ToArray()));
            }

            // Derive build version and architecture from directory structure:
            //   downloads/<branch>/<build_ver>/<arch>/
            string arch     = Path.GetFileName(buildDir).ToLower();
            string buildVer = Path.GetFileName(Path.GetDirectoryName(buildDir));

            if (log != null) log("Processing build " + buildVer + " / " + arch);

            string outputFolder = Path.Combine(buildDir, buildVer, arch);
            Directory.CreateDirectory(outputFolder);

            // Extract WIM
            chk();
            string wimExtract = Path.Combine(buildDir, Path.GetFileNameWithoutExtension(wimFile));
            if (!Directory.Exists(wimExtract))
            {
                // Disk-space pre-check: WIM expands well beyond its compressed
                // size, and the PSF patch + ESD roughly triple the footprint.
                // Require ~4x the WIM size free before extracting.
                try
                {
                    long wimSize = new FileInfo(wimFile).Length;
                    long needed  = wimSize * 4;
                    string root  = Path.GetPathRoot(Path.GetFullPath(buildDir));
                    DriveInfo di = new DriveInfo(root);
                    if (di.AvailableFreeSpace < needed)
                        throw new Exception(string.Format(
                            "Not enough disk space on {0}: need ~{1:F1} GB, have {2:F1} GB.",
                            root, needed / 1073741824.0,
                            di.AvailableFreeSpace / 1073741824.0));
                }
                catch (Exception exSpace)
                {
                    // Re-throw genuine space errors; ignore inability to measure.
                    if (exSpace.Message.IndexOf("disk space", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw;
                }
                ToolHelper.ExtractWim(wimFile, wimExtract, log, stopEvent);
            }

            // Apply PSF patch - returns the work dir containing patched files
            chk();
            if (log != null) log("Applying PSF patch...");
            string psfWorkDir = PsfPatcher.Apply(psfFile, log);

            // Capture the PSF-patched tree to ESD.
            // CRITICAL: must capture psfWorkDir (patched files), NOT wimExtract
            //           (raw WIM extract). The Python does the same: apply_psf
            //           returns work_dir and wimlib captures that directory.
            chk();
            string esdDest = Path.Combine(outputFolder,
                Path.GetFileNameWithoutExtension(wimFile) + ".esd");
            ToolHelper.WimlibCapture(psfWorkDir, esdDest, log, stopEvent);

            // MD5 + SHA256 for ESD (SHA256 uses CNG/SHA-NI hardware acceleration
            // automatically where the CPU and OS support it).
            if (log != null) log("Generating MD5 / SHA256 checksums...");
            string esdMd5Path    = esdDest + ".md5";
            string esdSha256Path = esdDest + ".sha256";
            string esdMd5        = Net.Md5File(esdDest);
            string esdSha256     = Net.Sha256File(esdDest);
            // Hash file content uses filename only (no path) for portability
            File.WriteAllText(esdMd5Path,
                esdMd5 + " *" + Path.GetFileName(esdDest) + "\n", Encoding.ASCII);
            File.WriteAllText(esdSha256Path,
                esdSha256 + " *" + Path.GetFileName(esdDest) + "\n", Encoding.ASCII);

            // Copy SSU / NDP CABs + MD5 + SHA256
            string ssuDest        = Path.Combine(outputFolder, Path.GetFileName(ssuCab));
            string ssuMd5Path     = ssuDest + ".md5";
            string ssuSha256Path  = ssuDest + ".sha256";
            File.Copy(ssuCab, ssuDest, true);
            string ssuMd5    = Net.Md5File(ssuDest);
            string ssuSha256 = Net.Sha256File(ssuDest);
            File.WriteAllText(ssuMd5Path,
                ssuMd5 + " *" + Path.GetFileName(ssuDest) + "\n", Encoding.ASCII);
            File.WriteAllText(ssuSha256Path,
                ssuSha256 + " *" + Path.GetFileName(ssuDest) + "\n", Encoding.ASCII);

            string ndpDest        = null;
            string ndpMd5Path     = null;
            string ndpSha256Path  = null;
            if (ndpCab != null)
            {
                ndpDest       = Path.Combine(outputFolder, Path.GetFileName(ndpCab));
                ndpMd5Path    = ndpDest + ".md5";
                ndpSha256Path = ndpDest + ".sha256";
                File.Copy(ndpCab, ndpDest, true);
                string ndpMd5    = Net.Md5File(ndpDest);
                string ndpSha256 = Net.Sha256File(ndpDest);
                File.WriteAllText(ndpMd5Path,
                    ndpMd5 + " *" + Path.GetFileName(ndpDest) + "\n", Encoding.ASCII);
                File.WriteAllText(ndpSha256Path,
                    ndpSha256 + " *" + Path.GetFileName(ndpDest) + "\n", Encoding.ASCII);
            }

            // Clean up WIM extract
            chk();
            if (log != null) log("Cleaning up WIM extract...");
            for (int i = 0; i < 3; i++)
            {
                try { if (Directory.Exists(wimExtract)) Directory.Delete(wimExtract, true); break; }
                catch { Thread.Sleep(400); }
            }

            if (log != null) log("Processing complete. Output in: " + outputFolder);

            // Network upload
            if (string.IsNullOrEmpty(uploadDest))
            {
                if (log != null) log("No upload destination configured – skipping transfer.");
                return true;
            }

            chk();
            string buildRootZ = Path.Combine(uploadDest, buildVer);
            Directory.CreateDirectory(buildRootZ);

            string marker = Path.Combine(buildRootZ, "non_complete");
            if (!File.Exists(marker)) File.WriteAllText(marker, "");

            string destArch = Path.Combine(buildRootZ, arch);
            Directory.CreateDirectory(destArch);

            foreach (string f in Directory.GetFiles(outputFolder))
                File.Copy(f, Path.Combine(destArch, Path.GetFileName(f)), true);

            if (log != null) log("Transfer to " + destArch + " complete.");

            // Write preview_update marker for every new build.
            // Manual verification determines if it is truly a preview.
            string previewMarker = Path.Combine(buildRootZ, "preview_update");
            if (!File.Exists(previewMarker))
            {
                File.WriteAllText(previewMarker, buildVer);
                if (log != null) log("preview_update marker written: " + previewMarker);
            }

            // Remove marker if both arches are present
            bool amd = Directory.Exists(Path.Combine(buildRootZ, "amd64"));
            bool arm = Directory.Exists(Path.Combine(buildRootZ, "arm64"));
            if (amd && arm)
            {
                try { File.Delete(marker); } catch { }
                if (log != null) log("Both architectures present – marker removed.");
            }
            else
            {
                if (log != null) log("Waiting for other architecture – marker kept.");
            }

            // Optional cleanup: use LongIO so deep PSF paths are removed correctly
            if (cleanup)
            {
                if (log != null) log("Cleanup enabled – removing " + buildDir + "...");
                try
                {
                    LongIO.DeleteDirectory(buildDir);
                    if (log != null) log("Cleanup done.");
                }
                catch (Exception exClean)
                {
                    if (log != null) log("Cleanup warning: " + exClean.Message);
                    Logger.LogFile("Cleanup failed: " + exClean.ToString());
                }
            }

            return true;
        }

        static string FindFirst(string dir, string pattern)
        {
            string[] files = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
            return files.Length > 0 ? files[0] : null;
        }
    }

    // =========================================================================
    //  LongIO  -  P/Invoke wrappers for long-path file & directory ops.
    //  .NET 4.8's System.IO rejects \\?\ prefixed paths during its own
    //  validation, so for paths that may exceed MAX_PATH (260 chars) we
    //  bypass System.IO entirely and call CreateDirectoryW / CreateFileW
    //  / MoveFileW directly.
    // =========================================================================
    static class LongIO
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateDirectoryW(string path, IntPtr lpSecurityAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFileAttributesW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(
            string lpFileName, uint access, uint share, IntPtr secAttr,
            uint creationDisposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileExW(string src, string dst, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DeleteFileW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool RemoveDirectoryW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileW(string pattern, out WIN32_FIND_DATA data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool FindNextFileW(IntPtr handle, out WIN32_FIND_DATA data);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool FindClose(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]  public string cAlternateFileName;
        }

        const uint GENERIC_WRITE          = 0x40000000;
        const uint FILE_SHARE_READ        = 0x00000001;
        const uint CREATE_ALWAYS          = 2;
        const uint FILE_ATTRIBUTE_NORMAL  = 0x80;
        const uint INVALID_FILE_ATTRIBS   = 0xFFFFFFFF;
        const uint FILE_ATTR_DIRECTORY    = 0x10;
        const uint MOVEFILE_REPLACE_EXISTING = 0x1;

        // Add the \\?\ prefix if needed; safe for already-prefixed paths.
        public static string Prefix(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            path = path.Replace('/', '\\');
            if (path.StartsWith(@"\\?\")) return path;
            if (path.StartsWith(@"\\"))   // UNC
                return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }

        public static bool Exists(string path)
        {
            uint a = GetFileAttributesW(Prefix(path));
            return a != INVALID_FILE_ATTRIBS;
        }

        public static bool FileExists(string path)
        {
            uint a = GetFileAttributesW(Prefix(path));
            return a != INVALID_FILE_ATTRIBS && (a & FILE_ATTR_DIRECTORY) == 0;
        }

        public static bool DirExists(string path)
        {
            uint a = GetFileAttributesW(Prefix(path));
            return a != INVALID_FILE_ATTRIBS && (a & FILE_ATTR_DIRECTORY) != 0;
        }

        // Recursively create a directory chain, long-path safe.
        // path may already include the \\?\ prefix or not.
        public static void CreateDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string prefixed = Prefix(path);
            if (DirExists(path)) return;

            // Walk up to find the deepest existing ancestor, then create down.
            // We work with the un-prefixed path to use LastIndexOf safely.
            string raw = path;
            if (raw.StartsWith(@"\\?\UNC\"))
                raw = @"\\" + raw.Substring(8);
            else if (raw.StartsWith(@"\\?\"))
                raw = raw.Substring(4);

            List<string> toCreate = new List<string>();
            string cur = raw;
            while (!string.IsNullOrEmpty(cur))
            {
                if (DirExists(cur)) break;
                toCreate.Add(cur);
                int sep = cur.LastIndexOf('\\');
                if (sep <= 2) break;   // C:\ or root reached
                cur = cur.Substring(0, sep);
            }
            for (int i = toCreate.Count - 1; i >= 0; i--)
            {
                if (!CreateDirectoryW(Prefix(toCreate[i]), IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 183) continue;  // ERROR_ALREADY_EXISTS
                    throw new System.ComponentModel.Win32Exception(err,
                        "CreateDirectoryW failed for: " + toCreate[i]);
                }
            }
        }

        public static void WriteAllBytes(string path, byte[] data)
        {
            SafeFileHandle h = CreateFileW(Prefix(path),
                GENERIC_WRITE, FILE_SHARE_READ, IntPtr.Zero,
                CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                throw new System.ComponentModel.Win32Exception(err,
                    "CreateFileW failed for: " + path);
            }
            using (FileStream fs = new FileStream(h, FileAccess.Write))
            {
                fs.Write(data, 0, data.Length);
            }
        }

        public static void Move(string src, string dst)
        {
            if (!MoveFileExW(Prefix(src), Prefix(dst), MOVEFILE_REPLACE_EXISTING))
            {
                int err = Marshal.GetLastWin32Error();
                throw new System.ComponentModel.Win32Exception(err,
                    "MoveFileExW failed: " + src + " -> " + dst);
            }
        }

        public static void Delete(string path)
        {
            if (!DeleteFileW(Prefix(path)))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 2) return;  // ERROR_FILE_NOT_FOUND
                throw new System.ComponentModel.Win32Exception(err,
                    "DeleteFileW failed: " + path);
            }
        }

        // Recursively delete a directory, long-path safe.
        public static void DeleteDirectory(string path)
        {
            if (!DirExists(path)) return;
            foreach (string child in EnumerateAll(path))
            {
                uint a = GetFileAttributesW(Prefix(child));
                if (a != INVALID_FILE_ATTRIBS && (a & FILE_ATTR_DIRECTORY) != 0)
                    DeleteDirectory(child);
                else
                    DeleteFileW(Prefix(child));
            }
            RemoveDirectoryW(Prefix(path));
        }

        // Enumerate immediate children (files + dirs) of a directory.
        public static List<string> EnumerateAll(string dir)
        {
            List<string> result = new List<string>();
            WIN32_FIND_DATA fd;
            string pattern = Prefix(dir) + "\\*";
            IntPtr h = FindFirstFileW(pattern, out fd);
            if (h == (IntPtr)(-1) || h == IntPtr.Zero) return result;
            try
            {
                do
                {
                    if (fd.cFileName == "." || fd.cFileName == "..") continue;
                    result.Add(dir + "\\" + fd.cFileName);
                }
                while (FindNextFileW(h, out fd));
            }
            finally { FindClose(h); }
            return result;
        }
    }

    // =========================================================================
    //  PSF patcher  (P/Invoke into msdelta.dll / UpdateCompression.dll)
    //  Mirrors the Python _apply_delta_bytes + apply_psf functions exactly.
    // =========================================================================
    static class PsfPatcher
    {
        // ----- msdelta structures & imports ------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        struct DELTA_INPUT
        {
            public IntPtr lpStart;
            public UIntPtr uSize;
            [MarshalAs(UnmanagedType.Bool)] public bool Editable;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DELTA_OUTPUT
        {
            public IntPtr lpStart;
            public UIntPtr uSize;
        }

        // We load the DLL dynamically so we gracefully degrade when not present.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr hMod, string name);

        delegate int ApplyDeltaBDelegate(
            long flags, DELTA_INPUT src, DELTA_INPUT dlt, out DELTA_OUTPUT output);

        delegate int DeltaFreeDelegate(IntPtr lpMemory);

        // -----------------------------------------------------------------------
        public static string Apply(string psfFile, Action<string> log)
        {
            string psfStem  = Path.GetFileNameWithoutExtension(psfFile);
            // Use GetFullPath to canonicalise (resolves any ../, mixed slashes)
            // so absDest concatenation is deterministic.
            string workDir  = Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName(psfFile), psfStem));
            string cixXml   = Path.Combine(workDir, "express.psf.cix.xml");

            // DIAGNOSTICS: log the key paths so the .log file shows exactly
            // what we are working with even if the loop later fails.
            Logger.LogFile("PSF Apply: psfFile  = [" + psfFile + "]");
            Logger.LogFile("PSF Apply: psfStem  = [" + psfStem + "]");
            Logger.LogFile("PSF Apply: workDir  = [" + workDir + "] (len " + workDir.Length + ")");
            Logger.LogFile("PSF Apply: cixXml   = [" + cixXml + "]");

            if (!File.Exists(cixXml))
            {
                if (log != null) log("CIX XML not found – skipping PSF patch: " + cixXml);
                return workDir;
            }

            Directory.CreateDirectory(workDir);

            List<PsfEntry> entries = ParseCixXml(cixXml);
            if (log != null) log("Applying PSF patch (" + entries.Count + " files)...");

            // Log the first 5 entry names + types to the file so we can
            // see exactly what the XML looks like.
            int sample = Math.Min(5, entries.Count);
            for (int si = 0; si < sample; si++)
            {
                PsfEntry s = entries[si];
                Logger.LogFile("  sample[" + si + "]: name=[" + s.Name +
                    "] type=" + s.SType + " off=" + s.Offset +
                    " len=" + s.Length);
            }

            // Load delta DLL
            string sysRoot  = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            string ucPath   = Path.Combine(sysRoot, "System32", "UpdateCompression.dll");
            IntPtr hLib     = LoadLibraryW(File.Exists(ucPath) ? ucPath : "msdelta.dll");
            ApplyDeltaBDelegate applyDelta = null;
            DeltaFreeDelegate   deltaFree  = null;
            if (hLib != IntPtr.Zero)
            {
                IntPtr pApply = GetProcAddress(hLib, "ApplyDeltaB");
                IntPtr pFree  = GetProcAddress(hLib, "DeltaFree");
                if (pApply != IntPtr.Zero)
                    applyDelta = (ApplyDeltaBDelegate)Marshal.GetDelegateForFunctionPointer(
                        pApply, typeof(ApplyDeltaBDelegate));
                if (pFree != IntPtr.Zero)
                    deltaFree = (DeltaFreeDelegate)Marshal.GetDelegateForFunctionPointer(
                        pFree, typeof(DeltaFreeDelegate));
            }

            // All file I/O goes through LongIO (P/Invoke wide-string APIs)
            // because .NET 4.8 System.IO rejects \\?\ prefixed paths during
            // its own input validation.  See LongIO class above.

            string stageDir = workDir + "\\" + "000";
            try { LongIO.CreateDirectory(stageDir); }
            catch (Exception exDir)
            {
                Logger.LogFile("PSF stage-dir create FAILED for: " + stageDir +
                    "  | exception: " + exDir.GetType().Name + ": " + exDir.Message);
                throw;
            }

            int idx = 0;
            int failures = 0;
            using (FileStream psf = File.OpenRead(psfFile))
            {
                foreach (PsfEntry entry in entries)
                {
                    idx++;
                    string outRel = entry.Name;
                    if (string.IsNullOrEmpty(outRel)) continue;

                    int sep = outRel.LastIndexOfAny(new char[] { '\\', '/' });
                    string shortFile = sep >= 0 ? outRel.Substring(sep + 1) : outRel;
                    string shortDir  = sep >= 0 ? outRel.Substring(0, sep)  : "";

                    string absDest   = workDir + "\\" + outRel;
                    string absParent = shortDir.Length > 0
                        ? workDir + "\\" + shortDir
                        : workDir;

                    bool useTmp = absDest.Length > 255;

                    try
                    {
                        if (!useTmp && LongIO.FileExists(absDest)) continue;

                        psf.Seek(entry.Offset, SeekOrigin.Begin);
                        byte[] deltaBytes = new byte[entry.Length];
                        psf.Read(deltaBytes, 0, deltaBytes.Length);

                        string writePath;
                        if (useTmp)
                        {
                            writePath = stageDir + "\\" + shortFile;
                        }
                        else
                        {
                            LongIO.CreateDirectory(absParent);
                            writePath = absDest;
                        }

                        byte[] data = deltaBytes;
                        if ((entry.SType == "PA30" || entry.SType == "PA31") &&
                            applyDelta != null)
                        {
                            byte[] patched = ApplyDelta(applyDelta, deltaFree, deltaBytes);
                            if (patched != null) data = patched;
                        }
                        LongIO.WriteAllBytes(writePath, data);
                    }
                    catch (Exception exEntry)
                    {
                        failures++;
                        Logger.LogFile(string.Format(
                            "PSF entry #{0} FAILED: {1}: {2}\n" +
                            "  name      = [{3}]\n" +
                            "  outRel    = [{4}]\n" +
                            "  shortDir  = [{5}] (len {6})\n" +
                            "  shortFile = [{7}] (len {8})\n" +
                            "  absDest   = [{9}] (len {10})\n" +
                            "  absParent = [{11}] (len {12})\n" +
                            "  useTmp    = {13}\n" +
                            "  sType     = {14}, offset = {15}, length = {16}",
                            idx, exEntry.GetType().Name, exEntry.Message,
                            entry.Name, outRel, shortDir, shortDir.Length,
                            shortFile, shortFile.Length,
                            absDest, absDest.Length,
                            absParent, absParent.Length,
                            useTmp, entry.SType, entry.Offset, entry.Length));
                        if (failures == 1 && log != null)
                            log("PSF entry FAILED (see log file for details): " +
                                exEntry.Message + " | name=[" + entry.Name + "]");
                    }
                }
            }

            if (failures > 0 && log != null)
                log("PSF patch completed with " + failures + " failed entries " +
                    "(of " + entries.Count + "). See uupdump.log for details.");

            // Bulk-move long-path files from 000\ to their real destinations
            if (LongIO.DirExists(stageDir))
            {
                foreach (string tmpFile in LongIO.EnumerateAll(stageDir))
                {
                    int slash = tmpFile.LastIndexOf('\\');
                    string fn = slash >= 0 ? tmpFile.Substring(slash + 1) : tmpFile;
                    foreach (PsfEntry e2 in entries)
                    {
                        int sep2 = e2.Name.LastIndexOfAny(new char[] { '\\', '/' });
                        string nameOnly = sep2 >= 0 ? e2.Name.Substring(sep2 + 1) : e2.Name;
                        if (nameOnly == fn)
                        {
                            string realDest = workDir + "\\" + e2.Name;
                            string realDir  = sep2 >= 0
                                ? workDir + "\\" + e2.Name.Substring(0, sep2)
                                : workDir;
                            try
                            {
                                LongIO.CreateDirectory(realDir);
                                if (LongIO.FileExists(realDest)) LongIO.Delete(realDest);
                                LongIO.Move(tmpFile, realDest);
                            }
                            catch (Exception exMv)
                            {
                                Logger.LogFile("PSF bulk-move FAILED: " + e2.Name +
                                    "  src=[" + tmpFile + "] dest=[" + realDest +
                                    "] | " + exMv.GetType().Name + ": " + exMv.Message);
                            }
                            break;
                        }
                    }
                }
                try { LongIO.DeleteDirectory(stageDir); } catch { }
            }

            if (log != null) log("PSF patch applied.");
            return workDir;
        }





        static byte[] ApplyDelta(ApplyDeltaBDelegate applyDelta,
            DeltaFreeDelegate deltaFree, byte[] deltaBytes)
        {
            IntPtr buf = Marshal.AllocHGlobal(deltaBytes.Length);
            try
            {
                Marshal.Copy(deltaBytes, 0, buf, deltaBytes.Length);

                DELTA_INPUT src = new DELTA_INPUT
                { lpStart = IntPtr.Zero, uSize = UIntPtr.Zero, Editable = false };
                DELTA_INPUT dlt = new DELTA_INPUT
                { lpStart = buf, uSize = (UIntPtr)deltaBytes.Length, Editable = true };

                DELTA_OUTPUT output;
                int ok = applyDelta(0, src, dlt, out output);
                if (ok == 0 || output.lpStart == IntPtr.Zero) return null;

                byte[] result = new byte[(int)output.uSize];
                Marshal.Copy(output.lpStart, result, 0, result.Length);
                if (deltaFree != null) deltaFree(output.lpStart);
                return result;
            }
            catch { return null; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        struct PsfEntry { public string Name, SType; public long Offset, Length; }

        // Prepend extended-length prefix so Windows bypasses MAX_PATH.
        // Normalises forward slashes first (CIX XML names use '/').
        static string LongPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            path = path.Replace('/', '\\');
            if (path.StartsWith(@"\\?\")) return path;
            if (path.StartsWith(@"\\"))   // UNC: \\server\share
                return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }

        static List<PsfEntry> ParseCixXml(string path)
        {
            // Use XmlDocument to mirror Python's xml.etree.ElementTree.
            // Walks <File> -> <Delta> -> <Source> just like _parse_psf_index().
            // XmlDocument auto-decodes &amp; &quot; etc. so we never see entities.
            List<PsfEntry> entries = new List<PsfEntry>();

            System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
            doc.Load(path);

            // Walk the entire tree looking for the <Files> container
            System.Xml.XmlNode filesNode = null;
            System.Xml.XmlNodeList allNodes = doc.GetElementsByTagName("Files");
            if (allNodes.Count > 0) filesNode = allNodes[0];
            if (filesNode == null) return entries;

            foreach (System.Xml.XmlNode fileEl in filesNode.ChildNodes)
            {
                if (fileEl.NodeType != System.Xml.XmlNodeType.Element) continue;
                if (!fileEl.Name.EndsWith("File", StringComparison.OrdinalIgnoreCase)) continue;

                string name = fileEl.Attributes != null && fileEl.Attributes["name"] != null
                    ? fileEl.Attributes["name"].Value : null;
                if (string.IsNullOrEmpty(name)) continue;

                // Find <Delta> child
                System.Xml.XmlNode deltaEl = null;
                foreach (System.Xml.XmlNode c in fileEl.ChildNodes)
                {
                    if (c.NodeType == System.Xml.XmlNodeType.Element &&
                        c.Name.EndsWith("Delta", StringComparison.OrdinalIgnoreCase))
                    { deltaEl = c; break; }
                }
                if (deltaEl == null) continue;

                // Find <Source> child of <Delta>
                System.Xml.XmlNode srcEl = null;
                foreach (System.Xml.XmlNode c in deltaEl.ChildNodes)
                {
                    if (c.NodeType == System.Xml.XmlNodeType.Element &&
                        c.Name.EndsWith("Source", StringComparison.OrdinalIgnoreCase))
                    { srcEl = c; break; }
                }
                if (srcEl == null || srcEl.Attributes == null) continue;

                string sType  = srcEl.Attributes["type"]   != null ? srcEl.Attributes["type"].Value   : "";
                string sOff   = srcEl.Attributes["offset"] != null ? srcEl.Attributes["offset"].Value : "0";
                string sLen   = srcEl.Attributes["length"] != null ? srcEl.Attributes["length"].Value : "0";

                long offset = 0, length = 0;
                long.TryParse(sOff, out offset);
                long.TryParse(sLen, out length);

                entries.Add(new PsfEntry
                {
                    Name   = name,
                    SType  = sType,
                    Offset = offset,
                    Length = length,
                });
            }
            return entries;
        }
    }

    // =========================================================================
    //  Main fetch orchestrator
    // =========================================================================
    static class Fetcher
    {
        const string BaseUrl = "https://uupdump.net";

        static readonly string[][] Categories = new string[][]
        {
            new string[] { "w11-25h2", "Windows 11 25H2" },
            new string[] { "w11-23h2", "Windows 11 23H2" },
            new string[] { "w11-26h1", "Windows 11 26H1" },
        };

        public static void Run(string targetBuild, string targetArch,
            HashSet<string> enabledBranches,
            bool runProcessor, string uploadDest, bool cleanup,
            Action<string> log,
            Action<long, long, double> progressCb,
            ManualResetEvent stopEvent)
        {
            HashSet<string> downloaded = BuildState.Load();

            List<BuildInfo> allBuilds = new List<BuildInfo>();
            foreach (string[] cat in Categories)
            {
                if (stopEvent != null && stopEvent.WaitOne(0)) return;
                // Skip this category if the user disabled it AND no manual build
                // was specified (manual build always searches all branches).
                if (string.IsNullOrEmpty(targetBuild) &&
                    enabledBranches != null && enabledBranches.Count > 0 &&
                    !enabledBranches.Contains(cat[1]))
                {
                    if (log != null) Logger.LogUiOnly(log, "Skipping (disabled in settings): " + cat[1]);
                    continue;
                }
                string url  = BaseUrl + "/known.php?q=category%3A" + cat[0];
                if (log != null) Logger.LogUiOnly(log, "Checking " + cat[1]);
                string html = Net.FetchText(url, 3, log);
                List<BuildInfo> builds = HtmlParser.ParseKnownBuilds(html);
                foreach (BuildInfo b in builds) b.Branch = cat[1];
                allBuilds.AddRange(builds);
            }

            // Group by (branch, arch), keep latest build per group
            Dictionary<string, BuildInfo> latest = new Dictionary<string, BuildInfo>();
            foreach (BuildInfo b in allBuilds)
            {
                if (!string.IsNullOrEmpty(targetBuild) && b.BuildNum != targetBuild)
                    continue;
                try
                {
                    string[] parts = b.BuildNum.Split('.');
                    b.VersionTuple = Array.ConvertAll(parts, int.Parse);
                }
                catch { continue; }
                string key = b.Branch + "|" + b.Arch;
                if (!latest.ContainsKey(key))
                    { latest[key] = b; continue; }
                if (CompareVersions(b.VersionTuple, latest[key].VersionTuple) > 0)
                    latest[key] = b;
            }

            // Filter by architecture when one is explicitly requested
            if (!string.IsNullOrEmpty(targetArch))
            {
                List<string> toRemove = new List<string>();
                foreach (string k in latest.Keys)
                    if (!latest[k].Arch.Equals(targetArch, StringComparison.OrdinalIgnoreCase))
                        toRemove.Add(k);
                foreach (string k in toRemove) latest.Remove(k);
            }

            if (!string.IsNullOrEmpty(targetBuild) && latest.Count == 0)
            {
                if (log != null) log("No builds found matching " + targetBuild +
                    (targetArch != null ? " (" + targetArch + ")" : ""));
                return;
            }

            foreach (BuildInfo b in latest.Values)
            {
                if (stopEvent != null && stopEvent.WaitOne(0)) return;

                string buildNum = b.BuildNum;
                string baseNum  = buildNum;
                if (buildNum.StartsWith("26200")) baseNum = "26100" + buildNum.Substring(5);
                else if (buildNum.StartsWith("22631")) baseNum = "22621" + buildNum.Substring(5);

                string stateKey = b.Branch + "_" + baseNum + "_" + b.Arch;
                if (downloaded.Contains(stateKey))
                {
                    if (log != null) Logger.LogUiOnly(log, "Skipping already downloaded: " + stateKey);
                    continue;
                }

                string label = string.IsNullOrEmpty(targetBuild) ? "LATEST" : "FORCED";
                if (log != null) log(string.Format(
                    "=== {0} {1} build {2} → {3} ({4}) ===",
                    label, b.Branch, buildNum, baseNum, b.Arch));

                string getUrl = BaseUrl + string.Format(
                    "/get.php?id={0}&pack=0&edition=updateOnly&aria2=2", b.Id);
                string getHtml = Net.FetchText(getUrl, 3, log);
                List<UupFile> files = HtmlParser.ParseGetPage(getHtml);

                // Filter: skip CABs that are neither SSU nor NDP; skip specific KB MSUs
                List<UupFile> filtered = new List<UupFile>();
                foreach (UupFile f in files)
                {
                    string fn = f.Filename;
                    if (fn.EndsWith(".cab", StringComparison.OrdinalIgnoreCase))
                    {
                        if (fn.IndexOf("NDP481", StringComparison.OrdinalIgnoreCase) < 0 &&
                            fn.IndexOf("SSU",    StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                    }
                    if (fn.EndsWith(".msu", StringComparison.OrdinalIgnoreCase) &&
                        fn.IndexOf("KB5043080", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    filtered.Add(f);
                }

                if (filtered.Count == 0)
                {
                    if (log != null) log("No matching files for this build.");
                    continue;
                }

                string targetDir = Path.Combine(
                    Paths.DownloadDir,
                    b.Branch.Replace(" ", "_"),
                    baseNum,
                    b.Arch);
                Directory.CreateDirectory(targetDir);

                string marker = Path.Combine(targetDir, "non_complete");
                if (!File.Exists(marker)) File.WriteAllText(marker, "");

                bool allOk = true;
                foreach (UupFile f in filtered)
                {
                    if (stopEvent != null && stopEvent.WaitOne(0)) return;
                    string dest = Path.Combine(targetDir, f.Filename);
                    if (log != null) log("Downloading " + f.Filename + "...");
                    if (!Net.DownloadFile(f.Url, dest, f.Checksum, log, progressCb, stopEvent))
                    {
                        allOk = false;
                        break;
                    }
                }

                if (!allOk)
                {
                    if (log != null) log("Download errors in " + stateKey + " – not marking complete.");
                    continue;
                }

                try { if (File.Exists(marker)) File.Delete(marker); } catch { }
                downloaded.Add(stateKey);
                BuildState.Save(downloaded);
                if (log != null) log("Download complete: " + b.Branch + " " + buildNum + " (" + b.Arch + ")");

                if (runProcessor)
                {
                    if (stopEvent != null && stopEvent.WaitOne(0)) return;
                    if (log != null) log("--- Starting processor for " + stateKey + " ---");
                    try
                    {
                        bool ok = Processor.ProcessBuild(
                            targetDir, uploadDest, cleanup, log, stopEvent);
                        if (log != null)
                            log(ok
                                ? "Processor finished successfully for " + stateKey
                                : "Processor skipped (hotpatch or no-op) for " + stateKey);
                    }
                    catch (OperationCanceledException)
                    { if (log != null) log("Processor stopped by user."); return; }
                    catch (Exception ex)
                    { if (log != null) log("Processor error for " + stateKey + ": " + ex.Message); }
                }
                else
                {
                    if (log != null) log("Processor not enabled – files left in download directory.");
                }
            }
        }

        static int CompareVersions(int[] a, int[] b)
        {
            int len = Math.Max(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                int ai = i < a.Length ? a[i] : 0;
                int bi = i < b.Length ? b[i] : 0;
                if (ai != bi) return ai > bi ? 1 : -1;
            }
            return 0;
        }
    }

    // =========================================================================
    //  Main form
    // =========================================================================
    class MainForm : Form
    {
        const string AppVersion = "1.0.0";

        // UI controls
        RichTextBox _logBox;
        ProgressBar _dlBar, _spinBar;
        Label       _lblProgress;   // "45.2 / 312.0 MB  –  12.3 MB/s"
        TextBox     _tbBuild, _tbUploadDest, _tbUserAgent, _tbDownloadDir;
        ComboBox    _cmbArch;
        CheckBox    _cbMonitor, _cbRunProcessor, _cbCleanup, _cbSkipUpload;
        CheckBox    _cb25H2, _cb26H1, _cb23H2;
        Button      _btnStart, _btnStop;
        TabControl  _tabs;
        TabPage     _tabFetch, _tabSettings;

        // Worker state
        bool             _busy;
        Thread           _worker;
        ManualResetEvent _stopEvent = new ManualResetEvent(false);

        // Progress values written by worker thread, read by UI timer.
        // Volatile long is not atomic on x86 but close enough for display.
        volatile int  _progPct;
        long          _progDone;   // Interlocked
        long          _progTotal;  // Interlocked
        double        _progSpeed;  // written under _logLock

        // Thread-safe log queue
        readonly Queue<string> _logQueue  = new Queue<string>();
        readonly object        _logLock   = new object();
        System.Windows.Forms.Timer _logTimer;

        public MainForm()
        {
            AppConfig.Load();
            Paths.SetDownloadDir(AppConfig.DownloadDir);
            Theme.IsDark = DetectDarkMode();

            SuspendLayout();
            Text          = "UUP Dump Fetcher v" + AppVersion;
            MinimumSize   = new Size(980, 660);
            Size          = new Size(1100, 720);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor     = Theme.Bg;
            Font          = Ctrl.SegoUI();
            try
            {
                Icon = Icon.ExtractAssociatedIcon(
                    Process.GetCurrentProcess().MainModule.FileName);
            }
            catch { }

            BuildUI();
            // Apply the current theme to all entry/combo fields now that
            // controls exist (covers users whose OS theme differs from the
            // default).
            ApplyTheme();
            ResumeLayout(false);

            // Log messages are queued by the worker thread; the UI timer
            // drains the queue in batches so the message pump stays free.
            Logger.MessageLogged += delegate(string msg) { QueueLog(msg); };

            // 100 ms UI refresh timer: flushes log queue + refreshes progress.
            _logTimer          = new System.Windows.Forms.Timer();
            _logTimer.Interval = 100;
            _logTimer.Tick    += delegate(object s, EventArgs e) { OnUiTick(); };
            _logTimer.Start();
        }

        static bool DetectDarkMode()
        {
            try
            {
                RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key == null) return true;
                object v = key.GetValue("AppsUseLightTheme");
                key.Close();
                return v == null || (int)v == 0;
            }
            catch { return true; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            QueueLog("Ready.  Downloads: " + Paths.DownloadDir);
            if (string.IsNullOrEmpty(AppConfig.UploadDest) && !AppConfig.SkipUpload)
                QueueLog("WARN: No upload destination set – configure it in the Settings tab.");
            if (AppConfig.MonitorMode)
            {
                QueueLog("Monitor mode enabled in config – auto-starting fetch...");
                BeginInvoke(new Action(delegate() { OnStart(); }));
            }
        }

        // ── Build UI ──────────────────────────────────────────────────────────
        void BuildUI()
        {
            // Top bar
            Panel topBar     = new Panel();
            topBar.Dock      = DockStyle.Top;
            topBar.Height    = 48;
            topBar.BackColor = Theme.Surface;

            Label lblTitle   = Ctrl.MakeLabel("⬇  UUP Dump Fetcher", false, 14);
            lblTitle.Font    = Ctrl.SegoUI(14, FontStyle.Bold);
            lblTitle.Location = new Point(16, 10);

            Button btnTheme   = new Button();
            btnTheme.Text     = Theme.IsDark ? "\u2600" : "\uD83C\uDF19";
            btnTheme.Width    = 36; btnTheme.Height = 28;
            btnTheme.FlatStyle = FlatStyle.Flat;
            btnTheme.BackColor = Theme.Surface; btnTheme.ForeColor = Theme.Fg;
            btnTheme.Font     = Ctrl.SegoUI(11);
            btnTheme.Anchor   = AnchorStyles.Right | AnchorStyles.Top;
            btnTheme.FlatAppearance.BorderSize = 0;
            btnTheme.Click   += delegate(object s, EventArgs e)
            {
                Theme.IsDark  = !Theme.IsDark;
                btnTheme.Text = Theme.IsDark ? "\u2600" : "\uD83C\uDF19";
                ApplyTheme();
            };
            topBar.Resize    += delegate(object s, EventArgs e)
            {
                btnTheme.Location = new Point(
                    topBar.Width - btnTheme.Width - 12,
                    (topBar.Height - btnTheme.Height) / 2);
            };
            topBar.Controls.Add(lblTitle);
            topBar.Controls.Add(btnTheme);

            Panel sep = new Panel();
            sep.Dock = DockStyle.Top; sep.Height = 1; sep.BackColor = Theme.Border;

            _tabs         = new TabControl();
            _tabs.Dock    = DockStyle.Fill;
            _tabs.Padding = new Point(18, 7);
            _tabs.Font    = Ctrl.SegoUI();

            _tabFetch    = new TabPage("  Fetch  ");
            _tabSettings = new TabPage("  Settings  ");
            _tabs.TabPages.Add(_tabFetch);
            _tabs.TabPages.Add(_tabSettings);

            // Status bar at the bottom - use TableLayoutPanel to avoid
            // z-order/Dock conflicts that hid the progress bar previously.
            Panel statusBar     = new Panel();
            statusBar.Dock      = DockStyle.Bottom;
            statusBar.Height    = 42;
            statusBar.BackColor = Theme.Surface;

            // 3-column layout: label (55%) | progress bar (45%) | Stop (fixed)
            // Spinner removed - Stop button being enabled is sufficient running feedback.
            TableLayoutPanel sbLayout = new TableLayoutPanel();
            sbLayout.Dock        = DockStyle.Fill;
            sbLayout.ColumnCount = 4;
            sbLayout.RowCount    = 1;
            sbLayout.BackColor   = Color.Transparent;
            sbLayout.Padding     = new Padding(8, 0, 8, 0);
            sbLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            sbLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            sbLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            sbLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
            sbLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _lblProgress           = new Label();
            _lblProgress.Dock      = DockStyle.Fill;
            _lblProgress.Font      = Ctrl.SegoUI(8.25f);
            _lblProgress.ForeColor = Theme.FgDim;
            _lblProgress.BackColor = Color.Transparent;
            _lblProgress.TextAlign = ContentAlignment.MiddleLeft;
            _lblProgress.Text      = "Idle";

            _dlBar        = new ProgressBar();
            _dlBar.Dock   = DockStyle.Fill;
            _dlBar.Style  = ProgressBarStyle.Continuous;
            _dlBar.Margin = new Padding(0, 13, 8, 13);

            _spinBar         = new ProgressBar();
            _spinBar.Dock    = DockStyle.Fill;
            _spinBar.Style   = ProgressBarStyle.Marquee;
            _spinBar.MarqueeAnimationSpeed = 30;
            _spinBar.Margin  = new Padding(0, 14, 4, 14);
            _spinBar.Visible = false;  // shown by SetBusy(true)

            _btnStop         = Ctrl.DangerButton("■  Stop");
            _btnStop.Dock    = DockStyle.Fill;
            _btnStop.Margin  = new Padding(0, 6, 0, 6);
            _btnStop.Enabled = false;
            _btnStop.Click  += delegate(object s, EventArgs e) { OnStop(); };

            sbLayout.Controls.Add(_lblProgress, 0, 0);
            sbLayout.Controls.Add(_dlBar,       1, 0);
            sbLayout.Controls.Add(_spinBar,     2, 0);
            sbLayout.Controls.Add(_btnStop,     3, 0);
            statusBar.Controls.Add(sbLayout);

            Controls.Add(_tabs);
            Controls.Add(sep);
            Controls.Add(topBar);
            Controls.Add(statusBar);

            BuildFetchTab();
            BuildSettingsTab();
        }

        // ── Fetch tab ─────────────────────────────────────────────────────────
        void BuildFetchTab()
        {
            _tabFetch.BackColor = Theme.Bg;

            // Left panel (controls)
            Panel left  = new Panel();
            left.Dock   = DockStyle.Left;
            left.Width  = 310;
            left.Padding = new Padding(18);
            left.BackColor = Color.Transparent;

            // Right panel (log)
            Panel right = new Panel();
            right.Dock  = DockStyle.Fill;
            right.Padding = new Padding(0, 18, 18, 0);
            right.BackColor = Color.Transparent;

            _tabFetch.Controls.Add(right);
            _tabFetch.Controls.Add(left);

            // --- Left controls ---
            int y = 18;
            Action<Control> Add = null;
            Add = delegate(Control c)
            {
                c.Left  = 18; c.Top = y; c.Width = 268;
                left.Controls.Add(c);
                y += c.Height + 6;
            };
            Action Sep = delegate()
            {
                Panel p = new Panel();
                p.Height = 1; p.BackColor = Theme.Border;
                Add(p); y += 4;
            };
            Action<string> Sec = delegate(string title)
            {
                Label l = Ctrl.MakeLabel(title.ToUpper(), true, 8f);
                l.Font = Ctrl.SegoUI(8f, FontStyle.Bold);
                Add(l); y += 2;
            };

            Sec("Fetch Options");

            Add(Ctrl.MakeLabel("Specific build  (e.g. 26100.7092)", true));

            _tbBuild = Ctrl.MakeEntry();
            _tbBuild.Height = 24;
            _tbBuild.BorderStyle = Theme.IsDark
                ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
            Add(_tbBuild);

            y += 4;
            Label lblArch = Ctrl.MakeLabel("Architecture  (manual build only)", true);
            Add(lblArch);

            _cmbArch = new ComboBox();
            _cmbArch.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbArch.Height        = 24;
            _cmbArch.Font          = Ctrl.SegoUI();
            _cmbArch.BackColor     = Theme.IsDark ? Theme.EntryBg : SystemColors.Window;
            _cmbArch.ForeColor     = Theme.IsDark ? Theme.EntryFg : SystemColors.WindowText;
            _cmbArch.FlatStyle     = Theme.IsDark ? FlatStyle.Flat : FlatStyle.Standard;
            _cmbArch.Items.Add("amd64 + arm64");
            _cmbArch.Items.Add("amd64 only");
            _cmbArch.Items.Add("arm64 only");
            _cmbArch.SelectedIndex = 0;
            _cmbArch.Enabled       = false;   // enabled only when build is specified
            Add(_cmbArch);

            // Enable/disable arch selector based on whether a build is typed
            _tbBuild.TextChanged += delegate(object s, EventArgs e)
            {
                _cmbArch.Enabled = _tbBuild.Text.Trim().Length > 0;
                if (!_cmbArch.Enabled) _cmbArch.SelectedIndex = 0;
            };

            y += 10;
            _btnStart = Ctrl.AccentButton("▶  Start Fetching");
            _btnStart.Click += delegate(object s, EventArgs e) { OnStart(); };
            Add(_btnStart);

            y += 12; Sep(); y += 4;
            Sec("Download State");

            Button btnViewState = Ctrl.NormalButton("View Downloaded Builds");
            btnViewState.Click += delegate(object s, EventArgs e) { ShowState(); };
            Add(btnViewState);

            y += 2;
            Button btnClearState = Ctrl.DangerButton("Clear State File");
            btnClearState.Click += delegate(object s, EventArgs e) { ClearState(); };
            Add(btnClearState);

            y += 12; Sep(); y += 4;
            Sec("Log");
            Button btnClearLog = Ctrl.NormalButton("Clear Log");
            btnClearLog.Click += delegate(object s, EventArgs e) { ClearLog(); };
            Add(btnClearLog);

            // --- Right: log box ---
            Label lblOut = Ctrl.MakeLabel("Output", false, 11f);
            lblOut.Font   = Ctrl.SegoUI(11f, FontStyle.Bold);
            lblOut.Dock   = DockStyle.Top;
            lblOut.Height = 26;

            Panel logWrap = new Panel();
            logWrap.Dock      = DockStyle.Fill;
            logWrap.BackColor = Theme.Border;

            _logBox             = new RichTextBox();
            _logBox.Dock        = DockStyle.Fill;
            _logBox.ReadOnly    = true;
            _logBox.BackColor   = Theme.LogBg;
            _logBox.ForeColor   = Theme.LogFg;
            _logBox.Font        = new Font("Consolas", 8.25f);
            _logBox.BorderStyle = BorderStyle.None;
            _logBox.WordWrap    = true;
            _logBox.ScrollBars  = RichTextBoxScrollBars.Vertical;
            _logBox.DetectUrls  = true;
            _logBox.LinkClicked += delegate(object s2, LinkClickedEventArgs e2)
            { try { Process.Start(e2.LinkText); } catch { } };

            logWrap.Controls.Add(_logBox);
            right.Controls.Add(logWrap);
            right.Controls.Add(lblOut);
        }

        // ── Settings tab ──────────────────────────────────────────────────────
        void BuildSettingsTab()
        {
            _tabSettings.BackColor = Theme.Bg;

            TableLayoutPanel tl = new TableLayoutPanel();
            tl.Dock        = DockStyle.Fill;
            tl.ColumnCount = 2;
            tl.Padding     = new Padding(28);
            tl.BackColor   = Color.Transparent;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // Download destination (editable, with browse) - FIRST
            Panel ddRow = new Panel();
            ddRow.Dock = DockStyle.Fill; ddRow.Height = 26;
            _tbDownloadDir = Ctrl.MakeEntry();
            _tbDownloadDir.Text   = Paths.DownloadDir;
            _tbDownloadDir.Dock   = DockStyle.Fill;
            _tbDownloadDir.Height = 24;

            Button btnBrowseDl = new Button();
            btnBrowseDl.Text      = "..."; btnBrowseDl.Width = 34;
            btnBrowseDl.FlatStyle = FlatStyle.Flat;
            btnBrowseDl.BackColor = Theme.Border; btnBrowseDl.ForeColor = Theme.Fg;
            btnBrowseDl.Cursor    = Cursors.Hand; btnBrowseDl.Dock = DockStyle.Right;
            btnBrowseDl.FlatAppearance.BorderSize = 0;
            btnBrowseDl.Click += delegate(object s, EventArgs e)
            {
                FolderBrowserDialog dlg = new FolderBrowserDialog();
                dlg.Description = "Select download / working directory";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    _tbDownloadDir.Text = dlg.SelectedPath;
            };
            ddRow.Controls.Add(_tbDownloadDir);
            ddRow.Controls.Add(btnBrowseDl);
            AddSettingRow(tl, "Download destination", ddRow, 0);

            // Upload destination - SECOND
            Panel destRow = new Panel();
            destRow.Dock = DockStyle.Fill; destRow.Height = 26;
            _tbUploadDest = Ctrl.MakeEntry();
            _tbUploadDest.Text   = AppConfig.UploadDest;
            _tbUploadDest.Dock   = DockStyle.Fill;
            _tbUploadDest.Height = 24;

            Button btnBrowse = new Button();
            btnBrowse.Text      = "..."; btnBrowse.Width = 34;
            btnBrowse.FlatStyle = FlatStyle.Flat;
            btnBrowse.BackColor = Theme.Border; btnBrowse.ForeColor = Theme.Fg;
            btnBrowse.Cursor    = Cursors.Hand; btnBrowse.Dock = DockStyle.Right;
            btnBrowse.FlatAppearance.BorderSize = 0;
            btnBrowse.Click += delegate(object s, EventArgs e)
            {
                FolderBrowserDialog dlg = new FolderBrowserDialog();
                dlg.Description = "Select upload destination folder";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    _tbUploadDest.Text = dlg.SelectedPath;
            };
            destRow.Controls.Add(_tbUploadDest);
            destRow.Controls.Add(btnBrowse);

            AddSettingRow(tl, "Upload destination\n(e.g. Z:\\ or \\\\server\\share)", destRow, 1);

            _tbUserAgent      = Ctrl.MakeEntry();
            _tbUserAgent.Text = AppConfig.UserAgent;
            AddSettingRow(tl, "Browser user-agent", _tbUserAgent, 2);

            Panel sep1 = new Panel();
            sep1.Height = 1; sep1.BackColor = Theme.Border;
            sep1.Margin = new Padding(0, 10, 0, 10);
            tl.SetColumnSpan(sep1, 2); tl.Controls.Add(sep1, 0, 3);

            _cbRunProcessor = Ctrl.MakeCheck("Process update to ESD");
            _cbRunProcessor.Checked = AppConfig.RunProcessor;

            _cbCleanup = Ctrl.MakeCheck("Cleanup working dir after upload");
            _cbCleanup.Checked = AppConfig.CleanupAfterUpload;
            _cbSkipUpload = Ctrl.MakeCheck("Skip upload");
            _cbSkipUpload.Checked = AppConfig.SkipUpload && !AppConfig.CleanupAfterUpload;

            // Skip upload is meaningless when Cleanup is on — they would
            // delete the locally processed files without uploading them first.
            _cbSkipUpload.Enabled = !_cbCleanup.Checked;
            _cbCleanup.CheckedChanged += delegate(object s, EventArgs e)
            {
                _cbSkipUpload.Enabled = !_cbCleanup.Checked;
                if (_cbCleanup.Checked) _cbSkipUpload.Checked = false;
            };

            _cbMonitor = Ctrl.MakeCheck("Monitor mode  (auto-scan every 5 min)");
            _cbMonitor.Checked = AppConfig.MonitorMode;

            FlowLayoutPanel checks = new FlowLayoutPanel();
            checks.FlowDirection = FlowDirection.TopDown;
            checks.WrapContents  = false; checks.AutoSize = true;
            checks.BackColor     = Color.Transparent;
            checks.Controls.Add(_cbMonitor);
            checks.Controls.Add(_cbRunProcessor);
            checks.Controls.Add(_cbCleanup);
            checks.Controls.Add(_cbSkipUpload);
            tl.SetColumnSpan(checks, 2); tl.Controls.Add(checks, 0, 4);

            Panel sep2 = new Panel();
            sep2.Height = 1; sep2.BackColor = Theme.Border;
            sep2.Margin = new Padding(0, 10, 0, 10);
            tl.SetColumnSpan(sep2, 2); tl.Controls.Add(sep2, 0, 5);

            // Windows 11 version selection
            tl.Controls.Add(Ctrl.MakeLabel("Windows 11 versions to scan", false), 0, 6);

            _cb25H2 = Ctrl.MakeCheck("Windows 11 25H2");
            _cb25H2.Checked = AppConfig.Branch25H2;
            _cb26H1 = Ctrl.MakeCheck("Windows 11 26H1");
            _cb26H1.Checked = AppConfig.Branch26H1;
            _cb23H2 = Ctrl.MakeCheck("Windows 11 23H2");
            _cb23H2.Checked = AppConfig.Branch23H2;

            FlowLayoutPanel verChecks = new FlowLayoutPanel();
            verChecks.FlowDirection = FlowDirection.TopDown;
            verChecks.WrapContents  = false; verChecks.AutoSize = true;
            verChecks.BackColor     = Color.Transparent;
            verChecks.Controls.Add(_cb25H2);
            verChecks.Controls.Add(_cb26H1);
            verChecks.Controls.Add(_cb23H2);
            tl.Controls.Add(verChecks, 1, 6);

            Panel sep3 = new Panel();
            sep3.Height = 1; sep3.BackColor = Theme.Border;
            sep3.Margin = new Padding(0, 10, 0, 10);
            tl.SetColumnSpan(sep3, 2); tl.Controls.Add(sep3, 0, 7);

            tl.Controls.Add(Ctrl.MakeLabel("Config file",  true), 0, 8);
            tl.Controls.Add(Ctrl.MakeLabel(Paths.ConfigFile,  true), 1, 8);
            tl.Controls.Add(Ctrl.MakeLabel("State file",   true), 0, 9);
            tl.Controls.Add(Ctrl.MakeLabel(Paths.StateFile,   true), 1, 9);
            tl.Controls.Add(Ctrl.MakeLabel("Data dir",     true), 0, 11);
            tl.Controls.Add(Ctrl.MakeLabel(Paths.LogFile, true), 1, 10);
            tl.Controls.Add(Ctrl.MakeLabel("Log file",     true), 0, 10);
            tl.Controls.Add(Ctrl.MakeLabel(Paths.DataDir, true), 1, 11);

            Button btnFolder = Ctrl.NormalButton("Open download folder");
            btnFolder.Dock   = DockStyle.Fill;
            btnFolder.Margin = new Padding(18, 6, 0, 2);
            btnFolder.Click += delegate(object s, EventArgs e)
            { try { Process.Start("explorer.exe", Paths.DownloadDir); } catch { } };
            tl.Controls.Add(Ctrl.MakeLabel("", true), 0, 13);
            tl.Controls.Add(btnFolder, 1, 12);

            Button btnSave = Ctrl.AccentButton("Save Settings");
            btnSave.Dock   = DockStyle.Fill;
            btnSave.Margin = new Padding(18, 2, 0, 6);
            btnSave.Click += delegate(object s, EventArgs e) { SaveSettings(); };
            tl.Controls.Add(Ctrl.MakeLabel("", true), 0, 12);
            tl.Controls.Add(btnSave, 1, 13);

            _tabSettings.Controls.Add(tl);
        }

        void AddSettingRow(TableLayoutPanel tl, string label, Control ctl, int row)
        {
            tl.Controls.Add(Ctrl.MakeLabel(label, true), 0, row);
            ctl.Dock = DockStyle.Fill; ctl.Margin = new Padding(18, 4, 0, 4);
            tl.Controls.Add(ctl, 1, row);
        }

        // ── Theme apply ───────────────────────────────────────────────────────
        void ApplyTheme()
        {
            BackColor = Theme.Bg;
            foreach (TabPage tp in _tabs.TabPages) tp.BackColor = Theme.Bg;
            if (_logBox != null)
            {
                _logBox.BackColor = Theme.LogBg;
                _logBox.ForeColor = Theme.LogFg;
                RecolorLog(_logBox);
            }
            // Repaint every TextBox and ComboBox so they pick up the new theme.
            // Borderless controls only — we re-apply MakeEntry's rules here.
            RestyleEntries(this);
            Invalidate(true);
        }

        // Recursively walk every control and re-apply theme colours.
        void RestyleEntries(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                TextBox tb = c as TextBox;
                if (tb != null)
                {
                    if (Theme.IsDark)
                    {
                        tb.BackColor   = Theme.EntryBg;
                        tb.ForeColor   = Theme.EntryFg;
                        tb.BorderStyle = BorderStyle.FixedSingle;
                    }
                    else
                    {
                        tb.BackColor   = SystemColors.Window;
                        tb.ForeColor   = SystemColors.WindowText;
                        tb.BorderStyle = BorderStyle.Fixed3D;
                    }
                }
                ComboBox cb = c as ComboBox;
                if (cb != null && !(c is RichTextBox))
                {
                    cb.FlatStyle = Theme.IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                    cb.BackColor = Theme.IsDark ? Theme.EntryBg : SystemColors.Window;
                    cb.ForeColor = Theme.IsDark ? Theme.EntryFg : SystemColors.WindowText;
                    // A ComboBox caches its background brush on the native
                    // handle; FlatStyle/BackColor changes only take visual
                    // effect after the handle is recreated. Force it via the
                    // protected RecreateHandle() method using reflection.
                    try
                    {
                        System.Reflection.MethodInfo mi = typeof(Control).GetMethod(
                            "RecreateHandle",
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic);
                        if (mi != null) mi.Invoke(cb, null);
                    }
                    catch { }
                    cb.Refresh();
                }
                CheckBox chk = c as CheckBox;
                if (chk != null)
                {
                    chk.ForeColor = Theme.Fg;
                    chk.BackColor = Color.Transparent;
                }
                RadioButton rb = c as RadioButton;
                if (rb != null)
                    rb.ForeColor = Theme.Fg;
                Label lbl = c as Label;
                if (lbl != null && !(c is RichTextBox) &&
                    lbl.ForeColor != Theme.Accent && lbl.ForeColor != Theme.Ok &&
                    lbl.ForeColor != Theme.Err  && lbl.ForeColor != Theme.Warn)
                    lbl.ForeColor = Theme.FgDim;
                if (c.HasChildren) RestyleEntries(c);
            }
        }

        void RecolorLog(RichTextBox box)
        {
            if (box == null || box.IsDisposed || box.TextLength == 0) return;
            int savedStart  = box.SelectionStart;
            int savedLength = box.SelectionLength;
            box.SuspendLayout();
            string[] logLines = box.Text.Split('\n');
            int pos = 0;
            foreach (string line in logLines)
            {
                if (line.Length == 0) { pos++; continue; }
                int tsEnd    = line.IndexOf("] ");
                int msgStart = tsEnd >= 0 ? tsEnd + 2 : 0;
                if (tsEnd >= 0) { box.Select(pos, tsEnd + 2); box.SelectionColor = Theme.FgDim; }
                string msgBody = tsEnd >= 0 ? line.Substring(tsEnd + 2) : line;
                box.Select(pos + msgStart, line.Length - msgStart);
                box.SelectionColor = MsgColor(msgBody);
                pos += line.Length + 1;
            }
            box.Select(savedStart, savedLength);
            box.ResumeLayout();
        }

        Color MsgColor(string msg)
        {
            string low = msg.ToLower();
            if (low.Contains("error")   || low.Contains("fail")    ||
                low.Contains("mismatch")|| low.Contains("abort"))    return Theme.Err;
            if (low.Contains("verified")|| low.Contains("complete")||
                low.Contains("success") || low.Contains("done"))     return Theme.Ok;
            if (low.Contains("skipping")|| low.Contains("already") ||
                low.Contains("sleeping")|| low.Contains("warning") ||
                low.Contains("waiting") || msg.StartsWith("WARN"))   return Theme.Warn;
            if (msg.StartsWith("===")   || msg.StartsWith("---"))    return Theme.Accent;
            return Theme.LogFg;
        }

        // ── Log helpers ───────────────────────────────────────────────────────
        void QueueLog(string msg)
        {
            lock (_logLock) _logQueue.Enqueue(msg);
        }

        DateTime _lastLogClear = DateTime.UtcNow;

        // Called by the 100 ms timer on the UI thread.
        void OnUiTick()
        {
            // Auto-clear in-app log every hour to prevent memory growth.
            if ((DateTime.UtcNow - _lastLogClear).TotalHours >= 1.0)
            {
                if (_logBox != null && !_logBox.IsDisposed) _logBox.Clear();
                _lastLogClear = DateTime.UtcNow;
                QueueLog("Log cleared (auto, hourly).");
            }

            // ── Flush log queue in one batched RTB update ────────────────
            List<string> batch = null;
            lock (_logLock)
            {
                if (_logQueue.Count > 0)
                {
                    batch = new List<string>(_logQueue);
                    _logQueue.Clear();
                }
            }
            if (batch != null && _logBox != null && !_logBox.IsDisposed)
            {
                _logBox.SuspendLayout();
                foreach (string msg in batch)
                    AppendLogToBox(msg, _logBox);
                _logBox.ResumeLayout();
                _logBox.ScrollToCaret();
            }

            // ── Refresh progress bar + label ─────────────────────────────
            if (_busy)
            {
                long done  = System.Threading.Interlocked.Read(ref _progDone);
                long total = System.Threading.Interlocked.Read(ref _progTotal);
                double spd;
                lock (_logLock) spd = _progSpeed;
                if (total > 0 || done > 0)
                {
                    _dlBar.Value = _progPct;
                    _lblProgress.Text = total > 0
                        ? string.Format("{0:F1} / {1:F1} MB  -  {2:F1} MB/s",
                            done / 1048576.0, total / 1048576.0, spd)
                        : string.Format("{0:F1} MB  -  {2:F1} MB/s",
                            done / 1048576.0, spd);
                }
            }
        }

        void AppendLog(string msg)
        {
            AppendLogToBox(msg, _logBox);
        }

        void AppendLogToBox(string msg, RichTextBox box)
        {
            if (box == null || box.IsDisposed) return;
            string ts  = "[" + DateTime.Now.ToString("HH:mm:ss") + "] ";
            Color  col = MsgColor(msg);

            box.SelectionStart  = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionColor  = Theme.FgDim;
            box.AppendText(ts);
            box.SelectionStart  = box.TextLength;
            box.SelectionColor  = col;
            box.AppendText(msg + "\n");
            // ScrollToCaret called once per batch by OnUiTick, not per message.
        }

        void ClearLog()
        {
            if (_logBox != null && !_logBox.IsDisposed) _logBox.Clear();
        }

        void ClearLogFile()
        {
            if (MessageBox.Show(
                    "Permanently erase:\n" + Logger.LogFilePath + "\n\nCannot be undone.",
                    "Clear log file", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                != DialogResult.Yes) return;
            try
            {
                Logger.ClearFile(); ClearLog();
                Logger.Log("Log file cleared.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not clear log file:\n" + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ── Status / busy ─────────────────────────────────────────────────────
        void SetStatus(string msg)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatus), msg); return; }
        }

        void SetBusy(bool busy)
        {
            if (InvokeRequired) { BeginInvoke(new Action<bool>(SetBusy), busy); return; }
            _busy             = busy;
            _spinBar.Visible  = busy;
            _btnStart.Enabled = !busy;
            _btnStop.Enabled  = busy;
            if (!busy)
            {
                System.Threading.Interlocked.Exchange(ref _progDone,  0);
                System.Threading.Interlocked.Exchange(ref _progTotal, 0);
                lock (_logLock) _progSpeed = 0;
                _progPct          = 0;
                _dlBar.Value      = 0;
                _lblProgress.Text = "Idle";
            }
        }

        // Called from worker thread — only stores values; UI timer applies them.
        void UpdateProgress(long done, long total, double speed)
        {
            System.Threading.Interlocked.Exchange(ref _progDone,  done);
            System.Threading.Interlocked.Exchange(ref _progTotal, total);
            lock (_logLock) _progSpeed = speed;
            _progPct = total > 0 ? (int)Math.Min(done * 100 / total, 100) : 0;
        }

        // ── Actions ───────────────────────────────────────────────────────────
        void OnStart()
        {
            if (_busy) return;
            _stopEvent.Reset();

            // Auto-save settings
            SaveSettings();

            string targetBuild = _tbBuild.Text.Trim();
            string targetArch  = null;   // null = all architectures
            if (!string.IsNullOrEmpty(targetBuild) && _cmbArch.SelectedIndex == 1)
                targetArch = "amd64";
            else if (!string.IsNullOrEmpty(targetBuild) && _cmbArch.SelectedIndex == 2)
                targetArch = "arm64";
            bool   runProc     = _cbRunProcessor.Checked;
            bool   useMonitor  = _cbMonitor.Checked;
            // Skip upload: checkbox state (not saved to config — session only)
            bool   skipUpload  = _cbSkipUpload != null && _cbSkipUpload.Checked;
            string uploadDest  = skipUpload ? "" : AppConfig.UploadDest;
            // Build the set of enabled branches from config (ignored for manual builds)
            HashSet<string> enabledBranches = new HashSet<string>(StringComparer.Ordinal);
            if (AppConfig.Branch25H2) enabledBranches.Add("Windows 11 25H2");
            if (AppConfig.Branch26H1) enabledBranches.Add("Windows 11 26H1");
            if (AppConfig.Branch23H2) enabledBranches.Add("Windows 11 23H2");
            bool   cleanup     = AppConfig.CleanupAfterUpload;

            if (runProc && string.IsNullOrEmpty(uploadDest) && !skipUpload)
            {
                if (MessageBox.Show(
                        "Processor is enabled but no upload destination is set.\n" +
                        "Files will be processed but NOT transferred.\n\nContinue anyway?",
                        "No upload destination",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
            }

            SetBusy(true);

            _worker = new Thread(delegate()
            {
                try
                {
                    if (useMonitor)
                    {
                        Logger.Log("Monitor mode – scanning every 5 minutes. Press Stop to exit.");
                        while (!_stopEvent.WaitOne(0))
                        {
                            Logger.LogUiOnly(Logger.Log, "Starting scan...");
                            Fetcher.Run(
                                string.IsNullOrEmpty(targetBuild) ? null : targetBuild,
                                targetArch,
                                enabledBranches,
                                runProc, uploadDest, cleanup,
                                Logger.Log, UpdateProgress, _stopEvent);
                            if (_stopEvent.WaitOne(0)) break;
                            Logger.LogUiOnly(Logger.Log, "Sleeping 5 minutes...");
                            for (int i = 0; i < 300; i++)
                            {
                                if (_stopEvent.WaitOne(1000)) break;
                            }
                        }
                    }
                    else
                    {
                        Fetcher.Run(
                            string.IsNullOrEmpty(targetBuild) ? null : targetBuild,
                            targetArch,
                            enabledBranches,
                            runProc, uploadDest, cleanup,
                            Logger.Log, UpdateProgress, _stopEvent);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("Error: " + ex.Message);
                }
                finally
                {
                    SetBusy(false);
                }
            });
            _worker.IsBackground = true;
            _worker.Start();
        }

        void OnStop()
        {
            _stopEvent.Set();
            ToolHelper.KillActive();
            Logger.Log("Stop requested – terminating active process...");
        }

        void SaveSettings()
        {
            AppConfig.UploadDest         = _tbUploadDest  != null ? _tbUploadDest.Text.Trim()  : "";
            AppConfig.CleanupAfterUpload = _cbCleanup      != null && _cbCleanup.Checked;
            AppConfig.SkipUpload         = _cbSkipUpload   != null && _cbSkipUpload.Checked;
            AppConfig.MonitorMode        = _cbMonitor      != null && _cbMonitor.Checked;
            AppConfig.RunProcessor       = _cbRunProcessor != null && _cbRunProcessor.Checked;
            AppConfig.Branch25H2         = _cb25H2         != null && _cb25H2.Checked;
            AppConfig.Branch26H1         = _cb26H1         != null && _cb26H1.Checked;
            AppConfig.Branch23H2         = _cb23H2         != null && _cb23H2.Checked;
            if (_tbUserAgent != null && _tbUserAgent.Text.Trim().Length > 0)
                AppConfig.UserAgent = _tbUserAgent.Text.Trim();
            // Apply download directory change immediately
            if (_tbDownloadDir != null)
            {
                string dd = _tbDownloadDir.Text.Trim();
                AppConfig.DownloadDir = dd;
                Paths.SetDownloadDir(dd);
            }
            AppConfig.Save();
            Logger.Log("Settings saved. Download dir: " + Paths.DownloadDir);
        }

        void ShowState()
        {
            if (!File.Exists(Paths.StateFile))
            {
                MessageBox.Show("No state file found yet.", "State",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string[] entries = File.ReadAllLines(Paths.StateFile, Encoding.UTF8);

            Form win = new Form();
            win.Text          = "Downloaded Builds";
            win.Size          = new Size(580, 420);
            win.BackColor     = Theme.Bg;
            win.Font          = Ctrl.SegoUI();
            win.StartPosition = FormStartPosition.CenterParent;

            Label lbl = Ctrl.MakeLabel(entries.Length + " recorded builds", false, 11f);
            lbl.Dock = DockStyle.Top; lbl.Height = 32; lbl.Padding = new Padding(14, 6, 0, 0);

            RichTextBox box = new RichTextBox();
            box.Dock        = DockStyle.Fill;
            box.ReadOnly    = true;
            box.BackColor   = Theme.Surface;
            box.ForeColor   = Theme.Fg;
            box.Font        = new Font("Consolas", 9f);
            box.BorderStyle = BorderStyle.None;
            box.Text        = entries.Length > 0
                ? string.Join("\n", entries)
                : "(empty)";

            win.Controls.Add(box);
            win.Controls.Add(lbl);
            win.ShowDialog(this);
        }

        void ClearState()
        {
            if (!File.Exists(Paths.StateFile))
            {
                MessageBox.Show("No state file found.", "State",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(
                    "Clear the downloaded-builds state file?\n" +
                    "This will NOT delete any downloaded files.",
                    "Confirm",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            File.WriteAllText(Paths.StateFile, "", Encoding.UTF8);
            Logger.Log("State file cleared.");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _stopEvent.Set();
            _logTimer.Stop();
            base.OnFormClosing(e);
        }
    }
}
