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

namespace UupDumpFetcher
{
    // =========================================================================
    //  External-tool helpers  (wimlib and cabinet.dll, both DLL-direct - no
    //  embedded/subprocess binaries at all)
    //
    //  WIM extraction (both the real Windows install.wim and the "combined
    //  SSU+LCU checkpoint" .msu packages that are WIM-formatted despite the
    //  extension, since Windows 11 build 25336+ - see PSFX_MSU's own README at
    //  github.com/abbodi1406/BatUtil) goes through WimLibApi (libwim-15.dll,
    //  DLL-direct). CAB extraction (the classic MSU container format, and any
    //  standalone .cab) goes through CabinetApi (the OS's own cabinet.dll).
    //
    //  7z.exe/7z.dll were removed entirely once CabinetApi.cs proved every
    //  branch this tool currently handles - 23H2 (standalone WIM/PSF, no
    //  .msu/.cab wrapper at all), 24H2/26H2/26H1 (WIM-formatted checkpoint
    //  .msu) - resolves through WimLibApi, never classic CAB; the classic-CAB
    //  path CabinetApi replaces is exercised only offline so far (verified
    //  byte-for-byte, including SHA256 per file, against 7z.exe extracting a
    //  real downloaded SSU cab - see CabinetApi.cs's own header), kept as a
    //  safety net for any future build (older Windows 10 builds, or a format
    //  regression) that reintroduces classic CAB-in-.msu - see CabinetApi.cs.
    // =========================================================================
    static class ToolHelper
    {
        // A .msu isn't one fixed format: classic packages are CAB containers,
        // but modern combined-update checkpoints are WIM-formatted despite
        // the extension. Try wimlib first (cheap to rule out - it fails fast
        // and specifically with WIMLIB_ERR_NOT_A_WIM_FILE when the signature
        // isn't there), then CabinetApi (cabinet.dll's own FDI) for classic
        // CAB - no further fallback, since neither this project's builds nor
        // 7z.exe exist anymore to fall back to.
        public static void ExtractMsu(string msuFile, string outDir, Action<string> log,
            ManualResetEvent stopEvent = null)
        {
            if (log != null) log("Extracting " + Path.GetFileName(msuFile) + "...");

            if (!WimLibApi.TryExtractAsWim(msuFile, outDir, log, stopEvent))
            {
                LongIO.CreateDirectory(outDir);
                CabinetApi.ExtractCab(msuFile, outDir, log, stopEvent);
            }
            if (log != null) log("MSU extraction complete.");
        }

        // Direct libwim-15.dll call via WimLibApi - no child process. The
        // real Windows install.wim is always genuinely WIM-formatted, so
        // this never needs a CAB fallback.
        public static void ExtractWim(string wimFile, string outDir, Action<string> log,
            ManualResetEvent stopEvent = null)
        {
            if (log != null) log("Extracting WIM " + Path.GetFileName(wimFile) + "...");
            WimLibApi.ExtractWim(wimFile, outDir, log, stopEvent);
            if (log != null) log("WIM extraction complete.");
        }

        // Direct libwim-15.dll call via WimLibApi - no child process, no
        // wimlib-imagex.exe (see WimLibApi.cs for why this is equivalent to
        // the old "wimlib-imagex capture ... --compress=LZMS --solid" call).
        public static void WimlibCapture(string sourceDir, string destEsd, Action<string> log,
            ManualResetEvent stopEvent = null)
        {
            if (log != null) log("Creating ESD: " + Path.GetFileName(destEsd) + "...");
            WimLibApi.Capture(sourceDir, destEsd, "ESD Update", log, stopEvent);
        }
    }

    // =========================================================================
    //  Processor  (port of process_build / CreateAndUploadUpdates.ps1 logic)
    // =========================================================================
    // Success=false means a cooperative skip (e.g. hotpatch update), not an
    // error - Fetcher.Run treats it the same as before (build not marked
    // complete, retried next scan). Files is only populated on success and
    // lists exactly what builds.json records for this build/arch.
    public sealed class ProcessResult
    {
        public bool Success;
        public List<BuildFileRecord> Files = new List<BuildFileRecord>();
    }

    static class Processor
    {
        public static ProcessResult ProcessBuild(string buildDir, string uploadDest,
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

                // Hotpatch detection (long-path safe - see FindFirst's comment)
                if (FindFirst(buildDir, "*hotpatch*") != null)
                {
                    if (log != null) log("Hotpatch update detected – skipping processing.");
                    return new ProcessResult { Success = false };
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
            //   downloads/<build_ver>/<arch>/
            string arch     = Path.GetFileName(buildDir).ToLower();
            string buildVer = Path.GetFileName(Path.GetDirectoryName(buildDir));

            if (log != null) log("Processing build " + buildVer + " / " + arch);

            string outputFolder = Path.Combine(buildDir, buildVer, arch);
            Directory.CreateDirectory(outputFolder);

            // Extract WIM
            chk();
            // Sibling of wimFile's own actual directory, not buildDir directly -
            // a "combined SSU+LCU checkpoint" MSU's wimFile/psfFile can now be
            // found nested (e.g. under a "content" wrapper FindFirst had to
            // recurse into), and PsfPatcher.Apply independently computes its
            // own work dir the same way (sibling of psfFile). The two must
            // match, or PsfPatcher's "no CIX XML, use as-is" fallback returns
            // a path that was never populated by the WIM extraction, and
            // wimlib_add_image fails scanning a nonexistent/empty directory.
            string wimExtract = Path.Combine(Path.GetDirectoryName(wimFile),
                Path.GetFileNameWithoutExtension(wimFile));
            // A directory left over from an interrupted run holds a partial
            // extraction; reusing it yields an ESD with missing/stale files that
            // fails manifest verification. Always start from a clean extract.
            if (Directory.Exists(wimExtract))
            {
                if (log != null) log("Removing stale WIM extract from a previous run...");
                try { LongIO.DeleteDirectory(wimExtract); }
                catch (Exception exStale)
                {
                    Logger.LogFile("Could not remove stale WIM extract: " + exStale.Message);
                    throw new Exception("Stale WIM extract could not be removed: " +
                        wimExtract + " (" + exStale.Message + ")");
                }
            }
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

            List<BuildFileRecord> outFiles = new List<BuildFileRecord>();
            outFiles.Add(new BuildFileRecord {
                name = Path.GetFileName(esdDest), md5 = esdMd5, sha256 = esdSha256 });

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
            outFiles.Add(new BuildFileRecord {
                name = Path.GetFileName(ssuDest), md5 = ssuMd5, sha256 = ssuSha256 });

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
                outFiles.Add(new BuildFileRecord {
                    name = Path.GetFileName(ndpDest), md5 = ndpMd5, sha256 = ndpSha256 });
            }

            // Clean up WIM extract
            chk();
            if (log != null) log("Cleaning up WIM extract...");
            for (int i = 0; i < 3; i++)
            {
                try { LongIO.DeleteDirectory(wimExtract); break; }
                catch { Thread.Sleep(400); }
            }

            if (log != null) log("Processing complete. Output in: " + outputFolder);

            UploadBuild(buildDir, uploadDest, cleanup, log, stopEvent);

            return new ProcessResult { Success = true, Files = outFiles };
        }

        // Copies a build's already-processed output folder to uploadDest and,
        // if cleanup is on, removes the raw download dir afterward. Split out
        // of ProcessBuild so a "Downloaded Builds" row can re-trigger just the
        // transfer for an already-processed build without redoing the whole
        // extract/patch/capture pipeline. No-op (logged) if uploadDest is
        // empty or the output folder doesn't exist (e.g. never processed, or
        // cleaned up already).
        public static void UploadBuild(string buildDir, string uploadDest,
            bool cleanup, Action<string> log, ManualResetEvent stopEvent)
        {
            Action chk = delegate()
            {
                if (stopEvent != null && stopEvent.WaitOne(0))
                    throw new OperationCanceledException("Stopped by user.");
            };

            string arch     = Path.GetFileName(buildDir).ToLower();
            string buildVer = Path.GetFileName(Path.GetDirectoryName(buildDir));
            string outputFolder = Path.Combine(buildDir, buildVer, arch);

            if (string.IsNullOrEmpty(uploadDest))
            {
                if (log != null) log("No upload destination configured – skipping transfer.");
                return;
            }
            if (!Directory.Exists(outputFolder))
            {
                if (log != null) log("Nothing to upload – output folder not found: " + outputFolder);
                return;
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
        }

        // Recursive and long-path safe (LongIO throughout): a "combined
        // SSU+LCU checkpoint" MSU's WIM payload does not always match
        // WimLibApi's single-wrapper-directory flatten heuristic (that WIM
        // can extract with multiple top-level dirs, or the wanted files
        // nested more than one level deep), so this can no longer assume
        // wimFile/psfFile/ssuCab/ndpCab sit directly under buildDir - a
        // shallow TopDirectoryOnly search silently found nothing and
        // "Required files missing" fired even though extraction succeeded.
        // Plain Directory.GetFiles(..., AllDirectories) isn't an option
        // either: real WinSxS-style extracted trees have 100+ char folder
        // names many levels deep and .NET 4.8's recursive enumerators throw
        // PathTooLongException walking them.
        static string FindFirst(string dir, string pattern)
        {
            string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return FindFirstRecursive(dir, new Regex(regexPattern, RegexOptions.IgnoreCase));
        }

        static string FindFirstRecursive(string dir, Regex nameMatch)
        {
            List<string> subDirs = new List<string>();
            foreach (string entry in LongIO.EnumerateAll(dir))
            {
                if (LongIO.DirExists(entry))
                {
                    subDirs.Add(entry);
                    continue;
                }
                string name = entry.Substring(entry.LastIndexOf('\\') + 1);
                if (nameMatch.IsMatch(name)) return entry;
            }
            foreach (string sub in subDirs)
            {
                string found = FindFirstRecursive(sub, nameMatch);
                if (found != null) return found;
            }
            return null;
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetFileAttributesExW(string path, int infoLevel,
            out WIN32_FILE_ATTRIBUTE_DATA data);

        [StructLayout(LayoutKind.Sequential)]
        struct WIN32_FILE_ATTRIBUTE_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
        }

        // Size of a file in bytes, long-path safe. Returns -1 if not found.
        public static long FileSize(string path)
        {
            WIN32_FILE_ATTRIBUTE_DATA d;
            if (!GetFileAttributesExW(Prefix(path), 0, out d)) return -1;
            return ((long)d.nFileSizeHigh << 32) | (uint)d.nFileSizeLow;
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

        const uint GENERIC_READ    = 0x80000000;
        const uint OPEN_EXISTING   = 3;

        // Long-path-safe FileStream open, for callers (CabinetApi) that need
        // incremental/seekable read-write rather than WriteAllBytes' one-shot
        // whole-buffer write.
        public static FileStream OpenRead(string path)
        {
            SafeFileHandle h = CreateFileW(Prefix(path),
                GENERIC_READ, FILE_SHARE_READ, IntPtr.Zero,
                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                throw new System.ComponentModel.Win32Exception(err,
                    "CreateFileW (read) failed for: " + path);
            }
            return new FileStream(h, FileAccess.Read);
        }

        public static FileStream OpenCreate(string path)
        {
            SafeFileHandle h = CreateFileW(Prefix(path),
                GENERIC_WRITE, FILE_SHARE_READ, IntPtr.Zero,
                CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                throw new System.ComponentModel.Win32Exception(err,
                    "CreateFileW (write) failed for: " + path);
            }
            return new FileStream(h, FileAccess.Write);
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
            // LongIO uses CreateFileW with the \\?\ prefix, which handles paths
            // of ANY length - so every file is written straight to its final
            // location.  (The old 000\ staging directory collapsed every entry
            // to its basename, and thousands of entries share a basename, e.g.
            // ...\ar-sa\f\hh.exe.mui and ...\bg-bg\f\hh.exe.mui.  They
            // overwrote each other and the move-back matched by basename, so
            // most language variants ended up missing or holding the wrong
            // payload.  That produced both the varying file counts and the
            // manifest hash mismatches.)

            int idx      = 0;
            int failures = 0;
            int written  = 0;
            // Guards against silent data loss: two entries resolving to the same
            // destination means one would overwrite the other (the bug that the
            // 000\ staging directory used to cause).
            HashSet<string> seenDest =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (FileStream psf = File.OpenRead(psfFile))
            {
                foreach (PsfEntry entry in entries)
                {
                    idx++;
                    string outRel = entry.Name;
                    if (string.IsNullOrEmpty(outRel)) continue;

                    int sep = outRel.LastIndexOfAny(new char[] { '\\', '/' });
                    string shortDir = sep >= 0 ? outRel.Substring(0, sep) : "";

                    string absDest   = workDir + "\\" + outRel;
                    string absParent = shortDir.Length > 0
                        ? workDir + "\\" + shortDir
                        : workDir;

                    try
                    {
                        // Read the delta payload - Read() may return short, so loop.
                        psf.Seek(entry.Offset, SeekOrigin.Begin);
                        byte[] deltaBytes = new byte[entry.Length];
                        int got = 0;
                        while (got < deltaBytes.Length)
                        {
                            int n = psf.Read(deltaBytes, got, deltaBytes.Length - got);
                            if (n <= 0) break;
                            got += n;
                        }
                        if (got != deltaBytes.Length)
                            throw new IOException("Short read from PSF: expected " +
                                deltaBytes.Length + " bytes, got " + got);

                        byte[] data;
                        if (entry.SType == "PA30" || entry.SType == "PA31")
                        {
                            if (applyDelta == null)
                                throw new InvalidOperationException(
                                    "msdelta/UpdateCompression unavailable - cannot apply " +
                                    entry.SType + " delta.");
                            // NEVER fall back to writing the raw delta bytes: that
                            // silently produces a corrupt file that fails manifest
                            // verification at install time.
                            data = ApplyDelta(applyDelta, deltaFree, deltaBytes);
                            if (data == null)
                                throw new InvalidOperationException(
                                    "ApplyDeltaB failed for " + entry.SType + " entry.");
                        }
                        else
                        {
                            data = deltaBytes;   // RAW entry - stored verbatim
                        }

                        // Two entries must never resolve to the same file.
                        if (!seenDest.Add(absDest))
                            throw new InvalidOperationException(
                                "Duplicate destination - entry would overwrite an " +
                                "earlier one: " + absDest);

                        LongIO.CreateDirectory(absParent);
                        LongIO.WriteAllBytes(absDest, data);

                        // Confirm the file actually landed at the expected size.
                        long onDisk = LongIO.FileSize(absDest);
                        if (onDisk != data.Length)
                            throw new IOException("Write verification failed: expected " +
                                data.Length + " bytes on disk, found " + onDisk);
                        written++;
                    }
                    catch (Exception exEntry)
                    {
                        failures++;
                        Logger.LogFile(string.Format(
                            "PSF entry #{0} FAILED: {1}: {2}\n" +
                            "  name    = [{3}]\n" +
                            "  absDest = [{4}] (len {5})\n" +
                            "  sType   = {6}, offset = {7}, length = {8}",
                            idx, exEntry.GetType().Name, exEntry.Message,
                            entry.Name, absDest, absDest.Length,
                            entry.SType, entry.Offset, entry.Length));
                        if (failures == 1 && log != null)
                            log("PSF entry FAILED: " + exEntry.Message +
                                " | name=[" + entry.Name + "]");
                    }
                }
            }

            // A partially-applied PSF yields an ESD whose files do not match the
            // component manifests. Fail loudly instead of shipping a broken ESD.
            if (failures > 0)
            {
                string msg = "PSF patch incomplete: " + failures + " of " +
                    entries.Count + " entries failed. See uupdump.log for details.";
                if (log != null) log(msg);
                throw new Exception(msg);
            }

            // Hard invariant: one file written per named entry. Any shortfall
            // means files were silently lost and the ESD would be incomplete.
            int expected = 0;
            foreach (PsfEntry e3 in entries)
                if (!string.IsNullOrEmpty(e3.Name)) expected++;

            if (written != expected)
            {
                string msg = "PSF file count mismatch: wrote " + written +
                    " files but expected " + expected + " (parsed " +
                    entries.Count + " entries). Aborting to avoid a corrupt ESD.";
                Logger.LogFile(msg);
                if (log != null) log(msg);
                throw new Exception(msg);
            }

            if (log != null) log("PSF patch applied: " + written +
                " files written (matches " + expected + " entries).");
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

}
