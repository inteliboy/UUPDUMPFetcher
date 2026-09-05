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
using System.Xml;
using System.Web.Script.Serialization;

[assembly: AssemblyTitle("UUP Dump Fetcher")]
// AssemblyVersion/AssemblyFileVersion come from AssemblyVersion.generated.cs,
// written fresh by build.cmd on every compile (see version.txt) - not fixed
// here, so don't add them back.

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

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        // Undocumented, ordinal-only (stable since Win10 1903 for these three -
        // AllowDarkModeForWindow=133, SetPreferredAppMode=135, FlushMenuThemes=136,
        // per the well-known uxtheme.dll ordinal table) - best-effort, wrapped in
        // try/catch by every caller here.
        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        static extern bool AllowDarkModeForWindow(IntPtr hWnd, bool allow);

        [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
        static extern int SetPreferredAppModeInternal(int preferredAppMode);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        static extern void FlushMenuThemesInternal();

        // Applies dark scrollbar theming to one native scrollable control
        // (ListView/TreeView/RichTextBox/AutoScroll Panel). Order matters:
        // AllowDarkModeForWindow before SetWindowTheme. Best-effort since this
        // is an undocumented API.
        public static void EnableDarkScrollBar(IntPtr hwnd)
        {
            try { AllowDarkModeForWindow(hwnd, true); } catch { }
            try { SetWindowTheme(hwnd, "DarkMode_Explorer", null); } catch { }
        }

        // Call once at startup, before Application.EnableVisualStyles().
        public static void SetPreferredAppMode(bool dark)
        {
            try { SetPreferredAppModeInternal(dark ? 1 : 0); } catch { }
        }

        public static void FlushMenuThemes()
        {
            try { FlushMenuThemesInternal(); } catch { }
        }
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
        // wimlib-imagex.exe is NOT embedded - WimLibApi P/Invokes libwim-15.dll
        // directly (the exe was only ever a thin CLI wrapper over the same
        // library) - WIM extraction goes through it too (WimLibApi.ExtractWim/
        // TryExtractAsWim), not just capture. 7z.exe is NOT embedded either
        // (removed once CabinetApi.cs proved every branch this tool currently
        // handles - 23H2 standalone WIM/PSF, 24H2/26H2/26H1 WIM-formatted
        // checkpoint .msu, all build 26100+/28000+, well past the WIM-
        // checkpoint threshold - resolves through WimLibApi, never classic
        // CAB; the classic-CAB fallback that used to need 7z.exe now goes
        // through CabinetApi.cs, a direct P/Invoke binding to the OS's own
        // cabinet.dll, needing no embedded binary at all).
        static readonly string[][] ResourceNames = new string[][]
        {
            new string[] { "tools.libwim-15.dll", "libwim-15.dll" },
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

                // Pixel-exact fonts assume no DPI scaling; this tool ships
                // without a manifest, so Windows treats it as DPI-unaware by
                // default and would bitmap-stretch it on a scaled display
                // unless we opt in here, before any window is created.
                try { NativeMethods.SetProcessDPIAware(); } catch { }

                NativeMethods.SetPreferredAppMode(Theme.IsDark);
                NativeMethods.FlushMenuThemes();

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
        public static string WimLibDll { get; private set; }

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
            StateFile   = Path.Combine(DownloadDir, "builds.json");
            try { Directory.CreateDirectory(DownloadDir); } catch { }
        }

        // Called from Program.Main() after EmbeddedTools.Extract() succeeds.
        public static void InitTools()
        {
            string dir = EmbeddedTools.ToolDir;
            WimLibDll = Path.Combine(dir, "libwim-15.dll");
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
        public static bool   Branch26H2         = true;
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
                        case "branch_26h2":          Branch26H2         = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
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
            sb.AppendLine("branch_26h2 = "          + Branch26H2.ToString().ToLower());
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

    // One prepared output file's checksums (the ESD, the copied SSU cab, the
    // copied NDP cab, etc. - whatever Processor.ProcessBuild actually wrote to
    // the build's output folder). The .md5/.sha256 sidecar files Processor
    // writes alongside each one are kept too - this is a second, queryable
    // copy of the same information, not a replacement for them.
    public sealed class BuildFileRecord
    {
        public string name;
        public string md5;
        public string sha256;
    }

    // One arch's completed build (e.g. builds["26100.9278"]["amd64"]).
    public sealed class BuildRecord
    {
        public string completedUtc;
        public List<BuildFileRecord> files;
    }

    // =========================================================================
    //  Download state  (builds.json - keyed by baseNum, then arch, so 24H2/
    //  25H2/26H2 and 22H2/23H2 views of the same base build share one entry
    //  and each arch fills in its own sibling key independently)
    // =========================================================================
    static class BuildState
    {
        public static Dictionary<string, Dictionary<string, BuildRecord>> Load()
        {
            if (!File.Exists(Paths.StateFile))
                return new Dictionary<string, Dictionary<string, BuildRecord>>(StringComparer.Ordinal);
            try
            {
                string json = File.ReadAllText(Paths.StateFile, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                    return new Dictionary<string, Dictionary<string, BuildRecord>>(StringComparer.Ordinal);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                var loaded = ser.Deserialize<Dictionary<string, Dictionary<string, BuildRecord>>>(json);
                return loaded ?? new Dictionary<string, Dictionary<string, BuildRecord>>(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                Logger.Log("Failed to load " + Paths.StateFile + ": " + ex.Message);
                return new Dictionary<string, Dictionary<string, BuildRecord>>(StringComparer.Ordinal);
            }
        }

        public static void Save(Dictionary<string, Dictionary<string, BuildRecord>> builds)
        {
            try
            {
                Directory.CreateDirectory(Paths.DownloadDir);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                File.WriteAllText(Paths.StateFile, ser.Serialize(builds), Encoding.UTF8);
            }
            catch (Exception ex) { Logger.Log("Failed to save state: " + ex.Message); }
        }
    }
}
