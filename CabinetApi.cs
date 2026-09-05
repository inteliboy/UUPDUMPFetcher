using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace UupDumpFetcher
{
    // =========================================================================
    //  CabinetApi  -  direct P/Invoke binding to the OS's own cabinet.dll (the
    //  FDI - File Decompression Interface - API that expand.exe and Explorer's
    //  own CAB shell extension use internally). Ships with every Windows
    //  install since Windows 2000 (confirmed via GetProcAddress against the
    //  real C:\Windows\System32\cabinet.dll on this machine - file version
    //  5.00, exports FDICreate/FDICopy/FDIDestroy/FDIIsCabinet), so unlike
    //  libwim-15.dll this needs no embedded binary and no LoadLibrary-by-
    //  full-path dance - a bare DllImport("cabinet.dll") resolves it straight
    //  off the System32 search path.
    //
    //  Replaces ToolHelper's 7z.exe subprocess fallback in Processor.cs
    //  (classic CAB-formatted .msu packages - see that file's own comment for
    //  which builds still need this instead of WimLibApi's WIM path).
    //
    //  Every signature/struct/flag/constant below was verified against real
    //  sources, not guessed:
    //   - FDICreate/FDICopy/FDIDestroy prototypes and the FNALLOC/FNFREE/
    //     FNOPEN/FNREAD/FNWRITE/FNCLOSE/FNSEEK/PFNFDINOTIFY macro expansions:
    //     mingw-w64's fdi.h (a real, compiled, cross-platform reproduction of
    //     the actual Windows SDK header - github.com/mirror/mingw-w64).
    //   - FDINOTIFICATION/ERF struct layout, FDINOTIFICATIONTYPE values, and
    //     the exact return-value contract for every notification (0/-1/handle
    //     for fdintCOPY_FILE, TRUE/FALSE/-1 for fdintCLOSE_FILE_INFO, etc.),
    //     including the fact that FDI never calls PFNCLOSE itself and the app
    //     must close hf from fdintCLOSE_FILE_INFO: Microsoft's own Learn docs
    //     (learn.microsoft.com/.../api/fdi/nf-fdi-fnfdinotify and
    //     ns-fdi-fdinotification).
    //   - _O_*/_S_I* open()/pmode flag values: mingw-w64's fcntl.h/sys/stat.h.
    //   - _A_RDONLY/_A_HIDDEN/_A_SYSTEM/_A_ARCH/_A_EXEC/_A_NAME_IS_UTF cabinet
    //     attribute bit values: the Microsoft Cabinet Format spec.
    //
    //  Calling convention: DIAMONDAPI = __cdecl for every FDI entry point and
    //  every callback. This project targets x64 only (/platform:x64 in
    //  build.cmd, where there is one uniform calling convention), but the
    //  attribute is declared correctly per the header anyway rather than
    //  relied on being harmless - see WimLibApi.cs's own note on a wrong
    //  P/Invoke signature silently corrupting an unrelated later call.
    //
    //  Long-path safety: a classic SSU/servicing-stack cab's internal files
    //  sit under WinSxS-style component names 100+ chars long, which blows
    //  past MAX_PATH (260) the moment they're combined with outDir -
    //  confirmed via a real PathTooLongException extracting a genuine
    //  SSU-*.cab in standalone testing before this code used LongIO. Every
    //  destination-file create/open/directory-create here goes through
    //  LongIO (\\?\-prefixed CreateFileW/CreateDirectoryW, defined in
    //  Processor.cs, same namespace), and destination paths are built by
    //  plain string concatenation, never Path.GetFullPath (which throws
    //  PathTooLongException on its own >260-char validation before LongIO
    //  ever gets a chance to apply the \\?\ prefix).
    //
    //  Scope: only handles the shapes this tool's inputs actually take - a
    //  single, non-spanned cabinet (a classic-format .msu, or a standalone
    //  .cab). fdintNEXT_CABINET (multi-volume spanning) is deliberately
    //  treated as a hard failure rather than implemented: a real spanned cab
    //  has never been seen from Microsoft's WU CDN for this tool's inputs,
    //  and getting spanning right would need its own dedicated verification
    //  pass against a real spanned sample, which doesn't exist here.
    // =========================================================================
    static class CabinetApi
    {
        const string Dll = "cabinet.dll";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr FnAlloc(uint cb);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void FnFree(IntPtr pv);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        delegate IntPtr FnOpen(string pszFile, int oflag, int pmode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint FnRead(IntPtr hf, IntPtr pv, uint cb);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint FnWrite(IntPtr hf, IntPtr pv, uint cb);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int FnClose(IntPtr hf);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int FnSeek(IntPtr hf, int dist, int seektype);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr FnFdiNotify(int fdint, IntPtr pfdin);

        [StructLayout(LayoutKind.Sequential)]
        struct ERF
        {
            public int erfOper;
            public int erfType;
            public int fError; // BOOL
        }

        // Matches learn.microsoft.com/.../ns-fdi-fdinotification exactly:
        // long cb; char *psz1/2/3; void *pv; INT_PTR hf; six USHORTs; FDIERROR
        // fdie. psz1/2/3 are native strings owned by FDI - read manually with
        // ReadCabString (never PtrToStringAnsi blindly - see _A_NAME_IS_UTF
        // handling below), never free them.
        [StructLayout(LayoutKind.Sequential)]
        struct FDINOTIFICATION
        {
            public int cb;      // long - always 32-bit in Win32 regardless of arch
            public IntPtr psz1;
            public IntPtr psz2;
            public IntPtr psz3;
            public IntPtr pv;
            public IntPtr hf;   // INT_PTR
            public ushort date;
            public ushort time;
            public ushort attribs;
            public ushort setID;
            public ushort iCabinet;
            public ushort iFolder;
            public int fdie;    // FDIERROR
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr FDICreate(FnAlloc pfnalloc, FnFree pfnfree, FnOpen pfnopen,
            FnRead pfnread, FnWrite pfnwrite, FnClose pfnclose, FnSeek pfnseek,
            int cpuType, ref ERF perf);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int FDIDestroy(IntPtr hfdi);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern int FDICopy(IntPtr hfdi, string pszCabinet, string pszCabPath,
            int flags, FnFdiNotify pfnfdin, IntPtr pfnfdid, IntPtr pvUser);

        // cabinet.h's cpuUNKNOWN - FDICreate's own docs recommend it explicitly.
        const int CPU_UNKNOWN = -1;

        // FDINOTIFICATIONTYPE, in declaration order (implicit int values).
        const int fdintCABINET_INFO    = 0;
        const int fdintPARTIAL_FILE    = 1;
        const int fdintCOPY_FILE       = 2;
        const int fdintCLOSE_FILE_INFO = 3;
        const int fdintNEXT_CABINET    = 4;

        // _O_*/_S_I* - only the combination this code issues itself when
        // opening a destination file for write; the read case (FDI's own
        // internal open of the cabinet for reading) needs none of these bits
        // set, so a plain oflag==0 already resolves correctly below.
        const int O_WRONLY = 0x0001;
        const int O_RDWR   = 0x0002;
        const int O_CREAT  = 0x0100;
        const int O_TRUNC  = 0x0200;
        const int O_BINARY = 0x8000;
        const int S_IREAD  = 0x0100;
        const int S_IWRITE = 0x0080;

        // Cabinet attribute bits (Microsoft Cabinet Format spec).
        const int A_RDONLY      = 0x01;
        const int A_HIDDEN      = 0x02;
        const int A_SYSTEM      = 0x04;
        const int A_NAME_IS_UTF = 0x80;

        // FDI's INT_PTR "hf" values are opaque to FDI itself (it never
        // dereferences them, only threads them back through pfnread/pfnwrite/
        // pfnclose/pfnseek) - a small incrementing integer key into a
        // FileStream table does the same job a real OS file descriptor would,
        // matching how real FDI callers (e.g. WiX's Extract.cpp) just cast
        // CRT _open/_read/_write/_close straight through.
        class ExtractState
        {
            public string OutDir;
            public string LastPath;
            public Exception Failure;
            public readonly Dictionary<IntPtr, FileStream> Handles = new Dictionary<IntPtr, FileStream>();
            public long NextHandle = 1;
        }

        // Bookend "Extracting.../...complete." logging is the caller's job
        // (ToolHelper.ExtractMsu), matching WimLibApi.ExtractWim's convention
        // - this only extracts.
        public static void ExtractCab(string cabFile, string outDir, Action<string> log,
            ManualResetEvent stopEvent)
        {
            LongIO.CreateDirectory(outDir);

            ExtractState state = new ExtractState { OutDir = outDir.TrimEnd('\\') };

            // Every delegate passed to native code must stay rooted for the
            // whole FDICopy call - see WimLibApi.cs's GC.KeepAlive note for
            // why an unreferenced local delegate can be collected mid-call
            // while native code still holds the raw function pointer.
            FnAlloc alloc = delegate(uint cb) { return Marshal.AllocHGlobal((int)cb); };
            FnFree free = delegate(IntPtr pv) { Marshal.FreeHGlobal(pv); };
            FnOpen open = delegate(string pszFile, int oflag, int pmode)
            { return CabOpen(state, pszFile, oflag); };
            FnRead read = delegate(IntPtr hf, IntPtr pv, uint cb) { return CabRead(state, hf, pv, cb); };
            FnWrite write = delegate(IntPtr hf, IntPtr pv, uint cb) { return CabWrite(state, hf, pv, cb); };
            FnClose close = delegate(IntPtr hf) { return CabClose(state, hf); };
            FnSeek seek = delegate(IntPtr hf, int dist, int seektype) { return CabSeek(state, hf, dist, seektype); };
            FnFdiNotify notify = delegate(int fdint, IntPtr pfdin)
            { return Notify(state, open, fdint, pfdin, stopEvent); };

            ERF erf = new ERF();
            IntPtr hfdi = FDICreate(alloc, free, open, read, write, close, seek, CPU_UNKNOWN, ref erf);
            if (hfdi == IntPtr.Zero)
                throw new Exception("FDICreate failed (erfOper=" + erf.erfOper + ", erfType=" + erf.erfType + ")");

            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(cabFile));
                string name = Path.GetFileName(cabFile);
                // FDICopy builds the cabinet's full path by concatenating
                // pszCabPath directly in front of pszCabinet (per its own
                // docs' example, "C:\MyCabs"), so pszCabPath must already end
                // in a separator.
                if (!dir.EndsWith("\\")) dir += "\\";

                bool ok = FDICopy(hfdi, name, dir, 0, notify, IntPtr.Zero, IntPtr.Zero) != 0;

                GC.KeepAlive(alloc); GC.KeepAlive(free); GC.KeepAlive(open); GC.KeepAlive(read);
                GC.KeepAlive(write); GC.KeepAlive(close); GC.KeepAlive(seek); GC.KeepAlive(notify);

                if (state.Failure != null)
                    throw new Exception("CabinetApi failure at path [" + state.LastPath + "]: " +
                        state.Failure, state.Failure);
                if (!ok)
                    throw new Exception("FDICopy failed on " + name +
                        " (fdi error " + erf.erfOper + ")");
            }
            finally
            {
                FDIDestroy(hfdi);
                // A handle with no matching fdintCLOSE_FILE_INFO (only
                // reachable if FDICopy aborted mid-file) is a partial file -
                // close it so nothing leaks. Cleanup only, not policy: the
                // caller above already decided success/failure of the whole
                // operation from state.Failure/ok.
                foreach (FileStream fs in state.Handles.Values)
                {
                    try { fs.Close(); } catch { }
                }
            }
        }

        static IntPtr Notify(ExtractState state, FnOpen open, int fdint, IntPtr pfdin,
            ManualResetEvent stopEvent)
        {
            try
            {
                if (stopEvent != null && stopEvent.WaitOne(0))
                {
                    state.Failure = new OperationCanceledException("Stopped by user.");
                    return new IntPtr(-1);
                }

                FDINOTIFICATION n = (FDINOTIFICATION)Marshal.PtrToStructure(pfdin, typeof(FDINOTIFICATION));

                switch (fdint)
                {
                    case fdintCOPY_FILE:
                    {
                        string name = ReadCabString(n.psz1, (n.attribs & A_NAME_IS_UTF) != 0);
                        string dest = ResolveDestPath(state, name);
                        state.LastPath = dest;
                        // Not Path.GetDirectoryName - it normalizes/validates
                        // internally (Path.LegacyNormalizePath) and throws
                        // PathTooLongException on the very same deep names
                        // LongIO exists to handle, confirmed via a real
                        // PathTooLongException from InternalGetDirectoryName
                        // in standalone testing against a genuine SSU cab.
                        int sep = dest.LastIndexOf('\\');
                        string destDir = sep > 0 ? dest.Substring(0, sep) : null;
                        if (!string.IsNullOrEmpty(destDir)) LongIO.CreateDirectory(destDir);
                        return open(dest, O_BINARY | O_CREAT | O_WRONLY | O_TRUNC, S_IREAD | S_IWRITE);
                    }
                    case fdintCLOSE_FILE_INFO:
                    {
                        // FDI never calls PFNCLOSE for this handle itself and
                        // assumes the target file is closed the moment this
                        // callback returns, regardless of the return value -
                        // per Microsoft's own docs, so this must close it here.
                        CabClose(state, n.hf);
                        try
                        {
                            string name = ReadCabString(n.psz1, (n.attribs & A_NAME_IS_UTF) != 0);
                            string dest = ResolveDestPath(state, name);
                            File.SetLastWriteTime(dest, DosDateTimeToDateTime(n.date, n.time));
                            FileAttributes fa = FileAttributes.Normal;
                            if ((n.attribs & A_RDONLY) != 0) fa |= FileAttributes.ReadOnly;
                            if ((n.attribs & A_HIDDEN) != 0) fa |= FileAttributes.Hidden;
                            if ((n.attribs & A_SYSTEM) != 0) fa |= FileAttributes.System;
                            File.SetAttributes(dest, fa);
                        }
                        // Best-effort only: File.SetLastWriteTime/SetAttributes
                        // do their own >260-char path validation (unlike the
                        // LongIO-based create/write path above), so this can
                        // legitimately throw PathTooLongException on the same
                        // deep WinSxS-style names that made LongIO necessary
                        // in the first place. The file's actual contents
                        // already landed correctly either way - only cosmetic
                        // timestamp/attribute metadata on a transient
                        // extraction directory is lost, which Processor.cs's
                        // downstream PSF-patch/capture steps never read.
                        catch { }
                        return new IntPtr(1); // TRUE
                    }
                    case fdintNEXT_CABINET:
                        state.Failure = new NotSupportedException(
                            "Multi-cabinet (spanned) archive encountered - not supported.");
                        return new IntPtr(-1);
                    default: // fdintCABINET_INFO, fdintPARTIAL_FILE, fdintENUMERATE
                        return IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                if (state.Failure == null) state.Failure = ex;
                return new IntPtr(-1);
            }
        }

        // Cab-internal names use '/' or '\' inconsistently across producers;
        // normalize before combining. A malicious/corrupt cabinet's name is
        // untrusted input (Microsoft's own docs call this out explicitly), so
        // reject anything that would land outside outDir rather than trust it.
        static string ResolveDestPath(ExtractState state, string cabRelativeName)
        {
            // Plain string concatenation, not Path.Combine/Path.GetFullPath -
            // GetFullPath throws PathTooLongException on its own >260-char
            // validation before LongIO's \\?\ prefix ever comes into play,
            // which a real WinSxS-style cab-internal name routinely exceeds.
            string rel = cabRelativeName.Replace('/', '\\').TrimStart('\\');
            string[] parts = rel.Split('\\');
            foreach (string part in parts)
                if (part == ".." || part == ".")
                    throw new Exception("Cabinet entry escapes output directory: " + cabRelativeName);
            return state.OutDir + "\\" + rel;
        }

        static string ReadCabString(IntPtr p, bool isUtf8)
        {
            if (p == IntPtr.Zero) return null;
            List<byte> bytes = new List<byte>();
            int i = 0;
            while (true)
            {
                byte b = Marshal.ReadByte(p, i);
                if (b == 0) break;
                bytes.Add(b);
                i++;
            }
            byte[] arr = bytes.ToArray();
            return isUtf8 ? Encoding.UTF8.GetString(arr) : Encoding.Default.GetString(arr);
        }

        // MS-DOS date/time bit layout (documented on FDINOTIFICATION's own
        // page): date = year-1980 (bits 9-15) | month 1-12 (bits 5-8) |
        // day 1-31 (bits 0-4); time = hour 0-23 (bits 11-15) | minute (bits
        // 5-10) | (second/2) (bits 0-4). Wrapped defensively - this is
        // untrusted data straight from the cabinet, not something FDI or this
        // code validates.
        static DateTime DosDateTimeToDateTime(ushort date, ushort time)
        {
            try
            {
                int day    = date & 0x1F;
                int month  = (date >> 5) & 0x0F;
                int year   = 1980 + ((date >> 9) & 0x7F);
                int second = (time & 0x1F) * 2;
                int minute = (time >> 5) & 0x3F;
                int hour   = (time >> 11) & 0x1F;
                return new DateTime(year, Math.Max(1, month), Math.Max(1, day),
                    hour, minute, Math.Min(59, second));
            }
            catch { return DateTime.Now; }
        }

        static IntPtr CabOpen(ExtractState state, string pszFile, int oflag)
        {
            bool wantWrite = (oflag & (O_WRONLY | O_RDWR | O_CREAT)) != 0;
            FileStream fs = wantWrite ? LongIO.OpenCreate(pszFile) : LongIO.OpenRead(pszFile);
            IntPtr h = new IntPtr(state.NextHandle++);
            state.Handles[h] = fs;
            return h;
        }

        static uint CabRead(ExtractState state, IntPtr hf, IntPtr pv, uint cb)
        {
            FileStream fs;
            if (!state.Handles.TryGetValue(hf, out fs)) return 0;
            byte[] buf = new byte[cb];
            int n = fs.Read(buf, 0, (int)cb);
            if (n > 0) Marshal.Copy(buf, 0, pv, n);
            return (uint)n;
        }

        static uint CabWrite(ExtractState state, IntPtr hf, IntPtr pv, uint cb)
        {
            FileStream fs;
            if (!state.Handles.TryGetValue(hf, out fs)) return 0;
            byte[] buf = new byte[cb];
            Marshal.Copy(pv, buf, 0, (int)cb);
            fs.Write(buf, 0, (int)cb);
            return cb;
        }

        static int CabClose(ExtractState state, IntPtr hf)
        {
            FileStream fs;
            if (state.Handles.TryGetValue(hf, out fs))
            {
                fs.Close();
                state.Handles.Remove(hf);
            }
            return 0;
        }

        // SEEK_SET/CUR/END = 0/1/2 - standard C runtime _lseek origin values.
        static int CabSeek(ExtractState state, IntPtr hf, int dist, int seektype)
        {
            FileStream fs;
            if (!state.Handles.TryGetValue(hf, out fs)) return -1;
            SeekOrigin origin = seektype == 1 ? SeekOrigin.Current :
                (seektype == 2 ? SeekOrigin.End : SeekOrigin.Begin);
            return (int)fs.Seek(dist, origin);
        }
    }
}
