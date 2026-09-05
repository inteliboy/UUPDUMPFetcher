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
    //  WimLibApi  -  direct P/Invoke binding to libwim-15.dll
    //
    //  wimlib-imagex.exe (no longer shipped) was only ever a thin CLI wrapper
    //  around this same library - every symbol below was confirmed present in
    //  the actual shipped libwim-15.dll via GetProcAddress, and every
    //  signature/flag/enum value below was taken from wimlib's own public
    //  header (include/wimlib.h) and CLI source (programs/imagex.c), not
    //  guessed. wimlib_tchar is wchar_t on Windows, so every string parameter
    //  is Unicode; calling convention is cdecl (WIMLIBAPI carries no stdcall
    //  decoration).
    //
    //  Replaces the single wimlib operation this tool ever used:
    //    wimlib-imagex capture "<src>" "<dest>" "ESD Update" --compress=LZMS --solid
    // =========================================================================
    static class WimLibApi
    {
        const string Dll = "libwim-15.dll";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int wimlib_create_new_wim(int ctype, out IntPtr wimRet);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        static extern int wimlib_add_image(IntPtr wim, string source, string name,
            string configFile, int addFlags);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int wimlib_set_output_pack_compression_type(IntPtr wim, int ctype);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        static extern int wimlib_write(IntPtr wim, string path, int image,
            int writeFlags, uint numThreads);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void wimlib_free(IntPtr wim);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        static extern int wimlib_open_wim(string wimFile, int openFlags, out IntPtr wimRet);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        static extern int wimlib_extract_image(IntPtr wim, int image, string target, int extractFlags);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr wimlib_get_error_string(int code);

        // NB: returns void, not int - confirmed from wimlib.h (unlike most
        // other wimlib_* calls here, this one has no wimlib_error_code result).
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void wimlib_register_progress_function(IntPtr wim,
            WimlibProgressFunc progfunc, IntPtr progctx);

        // enum wimlib_progress_status (*)(enum wimlib_progress_msg, union wimlib_progress_info*, void*)
        delegate int WimlibProgressFunc(int msgType, IntPtr info, IntPtr progctx);

        // wimlib_progress_info_write_streams - the only progress struct this
        // code reads. Layout matches include/wimlib.h exactly (two pairs of
        // uint64 followed by a uint32); struct has more trailing fields we
        // don't need, which is fine since we only read the leading ones.
        [StructLayout(LayoutKind.Sequential)]
        struct WriteStreamsInfo
        {
            public ulong TotalBytes;
            public ulong TotalStreams;
            public ulong CompletedBytes;
            public ulong CompletedStreams;
            public uint NumThreads;
        }

        // wimlib_progress_info_extract - layout matches include/wimlib.h
        // (two uint32, four pointers, then the byte/stream counters we
        // actually read); trailing fields we don't need are fine to omit.
        [StructLayout(LayoutKind.Sequential)]
        struct ExtractProgressInfo
        {
            public uint Image;
            public uint ExtractFlags;
            public IntPtr WimfileName;
            public IntPtr ImageName;
            public IntPtr Target;
            public IntPtr Reserved;
            public ulong TotalBytes;
            public ulong CompletedBytes;
            public ulong TotalStreams;
            public ulong CompletedStreams;
        }

        // enum wimlib_compression_type
        const int WIMLIB_COMPRESSION_TYPE_LZMS = 3;

        // wimlib_error_code - the one this code branches on (fall back to
        // 7z/CAB when a ".msu" turns out not to be WIM-formatted at all).
        const int WIMLIB_ERR_NOT_A_WIM_FILE = 43;

        // WIMLIB_ADD_FLAG_* - matches imagex.c's own default capture flags
        // exactly (imagex_capture_or_append's default add_flags), so this
        // direct-API capture behaves identically to what wimlib-imagex.exe
        // capture did (WINCONFIG applies wimlib's standard Windows capture
        // exclusions, e.g. pagefile.sys/hiberfil.sys/System Volume Information).
        const int WIMLIB_ADD_FLAG_VERBOSE           = 0x00000004;
        const int WIMLIB_ADD_FLAG_EXCLUDE_VERBOSE    = 0x00000080;
        const int WIMLIB_ADD_FLAG_WINCONFIG          = 0x00000800;
        const int WIMLIB_ADD_FLAG_FILE_PATHS_UNNEEDED = 0x00010000;

        const int WIMLIB_WRITE_FLAG_SOLID = 0x00001000;
        const int WIMLIB_ALL_IMAGES = -1;

        // enum wimlib_progress_msg (the handful this code cares about)
        const int MSG_EXTRACT_STREAMS       = 4;
        const int MSG_EXTRACT_IMAGE_END     = 7;
        const int MSG_SCAN_BEGIN            = 9;
        const int MSG_SCAN_END              = 11;
        const int MSG_WRITE_STREAMS         = 12;
        const int MSG_WRITE_METADATA_BEGIN  = 13;
        const int MSG_WRITE_METADATA_END    = 14;

        const int PROGRESS_STATUS_CONTINUE = 0;
        const int PROGRESS_STATUS_ABORT    = 1;

        static bool _loaded;
        static readonly object _loadLock = new object();

        // libwim-15.dll lives in the per-version temp extraction folder, not
        // beside the exe or on PATH, so a bare DllImport("libwim-15.dll")
        // cannot resolve it on its own. Explicitly loading it once by full
        // path pins it into the process; the Win32 loader then resolves every
        // subsequent DllImport("libwim-15.dll") call against that already-
        // loaded module by name, without needing to search for it again.
        static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_loadLock)
            {
                if (_loaded) return;
                if (!File.Exists(Paths.WimLibDll))
                    throw new FileNotFoundException("libwim-15.dll not found: " + Paths.WimLibDll);
                IntPtr h = LoadLibraryW(Paths.WimLibDll);
                if (h == IntPtr.Zero)
                    throw new Exception("Could not load " + Paths.WimLibDll +
                        " (Win32 error " + Marshal.GetLastWin32Error() + ")");
                _loaded = true;
            }
        }

        public static void Capture(string sourceDir, string destEsd, string imageName,
            Action<string> log, ManualResetEvent stopEvent)
        {
            EnsureLoaded();

            IntPtr wim = IntPtr.Zero;
            try
            {
                Check(wimlib_create_new_wim(WIMLIB_COMPRESSION_TYPE_LZMS, out wim),
                    "wimlib_create_new_wim");
                Check(wimlib_set_output_pack_compression_type(wim, WIMLIB_COMPRESSION_TYPE_LZMS),
                    "wimlib_set_output_pack_compression_type");

                DateTime start = DateTime.UtcNow;
                int lastLoggedPct = -1;

                // A named local does NOT by itself keep this delegate alive:
                // once the JIT sees no further managed reference to `callback`
                // after wimlib_register_progress_function, it's eligible for
                // GC even while wimlib still holds and calls the raw native
                // function pointer from its own compression threads during
                // wimlib_write below - GC.KeepAlive(callback) after the last
                // native call that can invoke it is what actually prevents
                // this. (Symptom when missing: works fine in a low-GC-pressure
                // console test, intermittently NullReferenceExceptions under
                // real GUI load once the delegate gets collected mid-write.)
                WimlibProgressFunc callback = delegate(int msgType, IntPtr info, IntPtr ctx)
                {
                    if (stopEvent != null && stopEvent.WaitOne(0))
                        return PROGRESS_STATUS_ABORT;

                    switch (msgType)
                    {
                        case MSG_SCAN_BEGIN:
                            Logger.LogFile("wimlib: scanning " + sourceDir + "...");
                            break;
                        case MSG_SCAN_END:
                            Logger.LogFile("wimlib: scan complete.");
                            break;
                        case MSG_WRITE_STREAMS:
                            if (info != IntPtr.Zero)
                            {
                                WriteStreamsInfo ws = (WriteStreamsInfo)
                                    Marshal.PtrToStructure(info, typeof(WriteStreamsInfo));
                                if (ws.TotalBytes > 0)
                                {
                                    int pct = (int)(ws.CompletedBytes * 100 / ws.TotalBytes);
                                    if (pct != lastLoggedPct && pct % 10 == 0)
                                    {
                                        lastLoggedPct = pct;
                                        double elapsed = (DateTime.UtcNow - start).TotalSeconds + 1e-9;
                                        double speed = ws.CompletedBytes / 1048576.0 / elapsed;
                                        if (log != null)
                                            log(string.Format("Creating ESD: {0}% ({1}/{2} MB, {3:F1} MB/s)",
                                                pct, ws.CompletedBytes / 1048576, ws.TotalBytes / 1048576, speed));
                                    }
                                }
                            }
                            break;
                        case MSG_WRITE_METADATA_BEGIN:
                            Logger.LogFile("wimlib: writing metadata...");
                            break;
                        case MSG_WRITE_METADATA_END:
                            Logger.LogFile("wimlib: metadata written.");
                            break;
                    }
                    return PROGRESS_STATUS_CONTINUE;
                };
                wimlib_register_progress_function(wim, callback, IntPtr.Zero);

                if (stopEvent != null && stopEvent.WaitOne(0))
                    throw new OperationCanceledException("Stopped by user.");

                int addFlags = WIMLIB_ADD_FLAG_EXCLUDE_VERBOSE | WIMLIB_ADD_FLAG_WINCONFIG |
                    WIMLIB_ADD_FLAG_VERBOSE | WIMLIB_ADD_FLAG_FILE_PATHS_UNNEEDED;
                Check(wimlib_add_image(wim, sourceDir, imageName, null, addFlags),
                    "wimlib_add_image");

                if (stopEvent != null && stopEvent.WaitOne(0))
                    throw new OperationCanceledException("Stopped by user.");

                Check(wimlib_write(wim, destEsd, WIMLIB_ALL_IMAGES, WIMLIB_WRITE_FLAG_SOLID,
                    (uint)Math.Max(1, Environment.ProcessorCount)), "wimlib_write");

                GC.KeepAlive(callback);
            }
            finally
            {
                if (wim != IntPtr.Zero) wimlib_free(wim);
            }
        }

        // Extracts every file in a WIM's (first/only) image to outDir - the
        // read-side counterpart of Capture(). Used both for the real
        // Windows install.wim (always genuinely WIM-formatted) and, via
        // TryExtractAsWim below, for unwrapping a modern "combined SSU+LCU
        // checkpoint" .msu that turns out to be WIM-formatted despite its
        // extension (see CLAUDE.md's "7z: still subprocess-based" section -
        // this replaces what 7z.dll's IInArchive.Open() couldn't do for WIM).
        public static void ExtractWim(string archivePath, string outDir, Action<string> log, ManualResetEvent stopEvent)
        {
            int rc = ExtractInternal(archivePath, outDir, log, stopEvent);
            Check(rc, "wimlib_open_wim/wimlib_extract_image on " + Path.GetFileName(archivePath));
        }

        // Same operation, but a WIMLIB_ERR_NOT_A_WIM_FILE result (the file
        // simply isn't WIM-formatted) returns false instead of throwing, so
        // the caller can fall back to CAB extraction. Any other error still
        // throws - it's a real failure, not a "wrong format" signal.
        public static bool TryExtractAsWim(string archivePath, string outDir, Action<string> log, ManualResetEvent stopEvent)
        {
            int rc = ExtractInternal(archivePath, outDir, log, stopEvent);
            if (rc == 0) return true;
            if (rc == WIMLIB_ERR_NOT_A_WIM_FILE) return false;
            Check(rc, "wimlib_open_wim/wimlib_extract_image on " + Path.GetFileName(archivePath));
            return false; // unreachable
        }

        static int ExtractInternal(string archivePath, string outDir, Action<string> log, ManualResetEvent stopEvent)
        {
            EnsureLoaded();
            Directory.CreateDirectory(outDir);

            IntPtr wim = IntPtr.Zero;
            try
            {
                int openRc = wimlib_open_wim(archivePath, 0, out wim);
                if (openRc != 0) return openRc;

                DateTime start = DateTime.UtcNow;
                int lastLoggedPct = -1;

                // See the identical GC.KeepAlive note in Capture() - this
                // delegate must stay rooted through the native call below.
                WimlibProgressFunc callback = delegate(int msgType, IntPtr info, IntPtr ctx)
                {
                    if (stopEvent != null && stopEvent.WaitOne(0))
                        return PROGRESS_STATUS_ABORT;

                    switch (msgType)
                    {
                        case MSG_EXTRACT_STREAMS:
                            if (info != IntPtr.Zero)
                            {
                                ExtractProgressInfo ei = (ExtractProgressInfo)
                                    Marshal.PtrToStructure(info, typeof(ExtractProgressInfo));
                                if (ei.TotalBytes > 0)
                                {
                                    int pct = (int)(ei.CompletedBytes * 100 / ei.TotalBytes);
                                    if (pct != lastLoggedPct && pct % 10 == 0)
                                    {
                                        lastLoggedPct = pct;
                                        double elapsed = (DateTime.UtcNow - start).TotalSeconds + 1e-9;
                                        double speed = ei.CompletedBytes / 1048576.0 / elapsed;
                                        if (log != null)
                                            log(string.Format("Extracting WIM: {0}% ({1}/{2} MB, {3:F1} MB/s)",
                                                pct, ei.CompletedBytes / 1048576, ei.TotalBytes / 1048576, speed));
                                    }
                                }
                            }
                            break;
                        case MSG_EXTRACT_IMAGE_END:
                            Logger.LogFile("wimlib: extraction complete.");
                            break;
                    }
                    return PROGRESS_STATUS_CONTINUE;
                };
                wimlib_register_progress_function(wim, callback, IntPtr.Zero);

                if (stopEvent != null && stopEvent.WaitOne(0))
                    throw new OperationCanceledException("Stopped by user.");

                int rc = wimlib_extract_image(wim, WIMLIB_ALL_IMAGES, outDir, 0);

                GC.KeepAlive(callback);
                if (rc == 0) FlattenSingleWrapperDir(outDir);
                return rc;
            }
            finally
            {
                if (wim != IntPtr.Zero) wimlib_free(wim);
            }
        }

        // Some WIM images (confirmed on a real Windows 11 "combined SSU+LCU
        // checkpoint" .msu-as-WIM) store their whole payload under one real
        // top-level directory (named "content" in the ones seen so far) -
        // this is an actual folder baked into the image's own file tree, not
        // just image metadata, so wimlib_extract_image faithfully reproduces
        // it. Processor.cs's callers expect files directly in outDir (the
        // shape 7z.exe's flat `x` extraction always produced), so flatten
        // exactly that one case: outDir contains no files of its own and
        // exactly one subdirectory - move that subdirectory's contents up
        // and remove it. Any other shape (multiple subdirs, or files already
        // at the top level) is left untouched, since it isn't the wrapper
        // pattern this is guarding against.
        //
        // Must use LongIO throughout, not plain System.IO: a real install.wim
        // extracts to 70K+ files under WinSxS-style top-level directory names
        // (100+ chars), which combined with outDir's own depth blows past
        // MAX_PATH (260) and throws PathTooLongException from Directory.GetFiles/
        // GetDirectories - confirmed via a real GUI regression run.
        static void FlattenSingleWrapperDir(string outDir)
        {
            List<string> top = LongIO.EnumerateAll(outDir);
            List<string> topDirs = new List<string>();
            int fileCount = 0;
            foreach (string entry in top)
            {
                if (LongIO.DirExists(entry)) topDirs.Add(entry);
                else fileCount++;
            }
            if (fileCount != 0 || topDirs.Count != 1) return;

            string wrapper = topDirs[0];
            foreach (string child in LongIO.EnumerateAll(wrapper))
            {
                string name = child.Substring(child.LastIndexOf('\\') + 1);
                LongIO.Move(child, outDir + "\\" + name);
            }
            LongIO.DeleteDirectory(wrapper);
        }

        static void Check(int rc, string what)
        {
            if (rc == 0) return; // WIMLIB_ERR_SUCCESS
            string msg = null;
            try
            {
                IntPtr p = wimlib_get_error_string(rc);
                if (p != IntPtr.Zero) msg = Marshal.PtrToStringUni(p);
            }
            catch { }
            throw new Exception(what + " failed (wimlib error " + rc +
                (msg != null ? ": " + msg : "") + ")");
        }
    }
}
