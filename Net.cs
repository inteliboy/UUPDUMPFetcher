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

}
