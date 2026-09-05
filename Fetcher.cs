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
        // Explicit build-number searches must not apply the name filters:
        // the user asked for that specific build, and it may legitimately be a
        // Feature/Preview entry (e.g. the 24H2 RTM 26100.1742).
        public static List<BuildInfo> ParseKnownBuildsUnfiltered(string html)
        {
            return ParseKnownBuildsInternal(html, false);
        }

        public static List<BuildInfo> ParseKnownBuilds(string html)
        {
            return ParseKnownBuildsInternal(html, true);
        }

        static List<BuildInfo> ParseKnownBuildsInternal(string html, bool applyFilters)
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

                if (applyFilters && (
                    name.IndexOf("Framework", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Cumulative", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Preview",    StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Feature",    StringComparison.OrdinalIgnoreCase) >= 0))
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
        // Bytes, when known - populated via WuClient.ResolveUpdateOnlyFiles
        // (real per-file Size from the SyncUpdates response). Left 0 for
        // files resolved via the uupdump.net get.php scrape fallback, which
        // doesn't carry file sizes - see Fetcher.Run's "biggest .msu" trim.
        public long Size;
    }

    // =========================================================================
    //  Main fetch orchestrator
    // =========================================================================
    static class Fetcher
    {
        // uupdump.net is no longer the primary source (see WuClient) - it is
        // kept only as an automatic fallback for whenever a direct Microsoft
        // Update query fails (e.g. a build Microsoft has aged off live
        // serving). See CLAUDE.md's "uupdump.net dependency" section.
        const string BaseUrl = "https://uupdump.net";

        // slug (legacy known.php category), display name, seed build number
        // (baseline OSVersion used only to derive the branch codename / give
        // Microsoft's server something to diff against - it returns whatever
        // is actually current for that branch, not "seed + 1").
        static readonly string[][] Categories = new string[][]
        {
            new string[] { "w11-26h2", "Windows 11 26H2", "26300.1" },
            new string[] { "w11-23h2", "Windows 11 23H2", "22631.1" },
            new string[] { "w11-26h1", "Windows 11 26H1", "28000.1" },
            // 24H2 is manual-only: it has no tick box in Settings, so
            // enabledBranches never contains it and automatic scans skip it.
            // A manually entered build number bypasses the branch filter, which
            // makes 24H2 builds such as 26100.1742 reachable on demand.
            new string[] { "w11-24h2", "Windows 11 24H2", "26100.1" },
        };

        static readonly string[] AllArches = new string[] { "amd64", "arm64" };

        public static void Run(string targetBuild, string targetArch,
            HashSet<string> enabledBranches,
            bool runProcessor, string uploadDest, bool cleanup,
            Action<string> log,
            Action<long, long, double> progressCb,
            ManualResetEvent stopEvent)
        {
            Dictionary<string, Dictionary<string, BuildRecord>> buildState = BuildState.Load();

            List<BuildInfo> allBuilds = new List<BuildInfo>();

            string[] archsToTry = !string.IsNullOrEmpty(targetArch)
                ? new string[] { targetArch } : AllArches;

            if (!string.IsNullOrEmpty(targetBuild))
            {
                // Manual build: ask Microsoft directly for this exact build number
                // ('thisonly') per architecture first. Old builds Microsoft has
                // aged off its live serving legitimately fail here - fall back to
                // searching uupdump.net's own historical database (known.php),
                // which is exactly the case that database exists for.
                HashSet<string> foundArches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string arch in archsToTry)
                {
                    if (stopEvent != null && stopEvent.WaitOne(0)) return;
                    if (log != null) Logger.LogUiOnly(log, "Asking Microsoft directly for build " + targetBuild + " (" + arch + ")...");
                    BuildInfo b = WuClient.DiscoverExact(targetBuild, arch, BranchFromName(null, targetBuild), log);
                    if (b != null)
                    {
                        allBuilds.Add(b);
                        foundArches.Add(arch);
                        if (log != null) log("Found directly from Microsoft: " + b.Name + " (" + b.Arch + ")");
                    }
                }

                if (foundArches.Count < archsToTry.Length)
                {
                    if (stopEvent != null && stopEvent.WaitOne(0)) return;
                    string url = BaseUrl + "/known.php?q=" + Uri.EscapeDataString(targetBuild);
                    if (log != null) log("Falling back to uupdump.net for build " + targetBuild +
                        " (architecture" + (archsToTry.Length - foundArches.Count == 1 ? "" : "s") + " not found directly)...");
                    string html = Net.FetchText(url, 3, log);
                    List<BuildInfo> found = HtmlParser.ParseKnownBuildsUnfiltered(html);
                    foreach (BuildInfo b in found)
                    {
                        if (foundArches.Contains(b.Arch)) continue; // already have this arch directly
                        b.Branch = BranchFromName(b.Name, b.BuildNum);
                        allBuilds.Add(b);
                        Logger.LogFile("  uupdump.net fallback hit: [" + b.Name + "] build=" +
                            b.BuildNum + " arch=" + b.Arch + " branch=" + b.Branch);
                    }
                }
            }
            else
            {
                foreach (string[] cat in Categories)
                {
                    if (stopEvent != null && stopEvent.WaitOne(0)) return;
                    // Skip categories the user disabled in Settings.
                    if (enabledBranches != null && enabledBranches.Count > 0 &&
                        !enabledBranches.Contains(cat[1]))
                    {
                        if (log != null) Logger.LogUiOnly(log, "Skipping (disabled in settings): " + cat[1]);
                        continue;
                    }

                    HashSet<string> foundArches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string arch in archsToTry)
                    {
                        if (stopEvent != null && stopEvent.WaitOne(0)) return;
                        Logger.LogUiOnly(log, "Checking " + cat[1] + " (" + arch + ") directly from Microsoft...");
                        BuildInfo b = WuClient.DiscoverLatest(cat[2], arch, cat[1], log);
                        if (b != null)
                        {
                            allBuilds.Add(b);
                            foundArches.Add(arch);
                        }
                    }

                    if (foundArches.Count < archsToTry.Length)
                    {
                        if (stopEvent != null && stopEvent.WaitOne(0)) return;
                        if (log != null) log(cat[1] + ": falling back to uupdump.net (architecture" +
                            (archsToTry.Length - foundArches.Count == 1 ? "" : "s") + " not found directly)...");
                        string url = BaseUrl + "/known.php?q=category%3A" + cat[0];
                        string html = Net.FetchText(url, 3, log);
                        List<BuildInfo> builds = HtmlParser.ParseKnownBuilds(html);
                        foreach (BuildInfo b in builds)
                        {
                            if (foundArches.Contains(b.Arch)) continue;
                            b.Branch = cat[1];
                            allBuilds.Add(b);
                        }
                    }
                }
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
                // Enablement-package trick: the advertised/category build number
                // differs from the actual underlying base OS build Microsoft
                // serves and this tool stores files under. Verified live against
                // Microsoft directly (WuClient.DiscoverExact): 26100.9278,
                // 26200.9278 and 26300.9278 are three separately-resolvable
                // update objects (distinct UpdateIDs, distinct titles - "24H2"/
                // "25H2"/"26H2" respectively) built on the exact same base OS
                // build - a manual search on any of the three must land in the
                // same folder/state-key as the others, or the same content gets
                // downloaded three times under three different build numbers.
                if (buildNum.StartsWith("26300")) baseNum = "26100" + buildNum.Substring(5);
                else if (buildNum.StartsWith("26200")) baseNum = "26100" + buildNum.Substring(5);
                else if (buildNum.StartsWith("22631")) baseNum = "22621" + buildNum.Substring(5);

                // Keyed by base build, then arch, not branch: 24H2/25H2/26H2
                // (26100/26200/26300) and 22H2/23H2 (22621/22631) already
                // normalize to the same baseNum above since Microsoft serves
                // the identical underlying payload regardless of which
                // enablement-package view resolved it - the download folder
                // and builds.json entry should reflect that same build once,
                // not once per branch label. Each arch is its own sibling key
                // under the build, filled in independently as it completes.
                string logKey = baseNum + " (" + b.Arch + ")";
                if (buildState.ContainsKey(baseNum) && buildState[baseNum].ContainsKey(b.Arch))
                {
                    if (log != null) Logger.LogUiOnly(log, "Skipping already downloaded: " + logKey);
                    continue;
                }

                string label = string.IsNullOrEmpty(targetBuild) ? "LATEST" : "FORCED";
                if (log != null) log(string.Format(
                    "=== {0} {1} build {2} → {3} ({4}) ===",
                    label, b.Branch, buildNum, baseNum, b.Arch));

                // Resolve real Microsoft CDN URLs directly first (WuClient
                // already applies get.php's own updateOnly filter chain, so its
                // result is used as-is). Only on failure do we fall back to
                // scraping uupdump.net's get.php - which needs this tool's own
                // simplified NDP481/SSU/KB5043080 filter since it doesn't return
                // pre-filtered like uupdump.net's own server-side logic.
                List<UupFile> files = WuClient.ResolveUpdateOnlyFiles(b.Id, b.BuildNum, b.Arch, log);
                List<UupFile> filtered;
                bool isManualBuild = !string.IsNullOrEmpty(targetBuild);

                if (files != null)
                {
                    filtered = files;
                    if (log != null) Logger.LogUiOnly(log, "Resolved " + filtered.Count +
                        " file(s) directly from Microsoft.");
                }
                else
                {
                    if (log != null) log("Direct Microsoft file resolution failed - falling back to uupdump.net...");
                    string getUrl = BaseUrl + string.Format(
                        "/get.php?id={0}&pack=0&edition=updateOnly&aria2=2", b.Id);
                    string getHtml = Net.FetchText(getUrl, 3, log);
                    files = HtmlParser.ParseGetPage(getHtml);

                    // Filter: skip CABs that are neither SSU nor NDP; skip specific KB MSUs.
                    //
                    // KB5043080 is the 24H2 checkpoint update. During automatic scans
                    // uupdump bundles it alongside the real cumulative update and it is
                    // unwanted - but for the 24H2 RTM build 26100.1742 KB5043080 IS the
                    // payload. So the exclusion only applies to automatic scans; an
                    // explicitly requested build number always keeps its own files.
                    filtered = new List<UupFile>();
                    foreach (UupFile f in files)
                    {
                        string fn = f.Filename;
                        if (fn.EndsWith(".cab", StringComparison.OrdinalIgnoreCase))
                        {
                            if (fn.IndexOf("NDP481", StringComparison.OrdinalIgnoreCase) < 0 &&
                                fn.IndexOf("SSU",    StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                        }
                        if (!isManualBuild &&
                            fn.EndsWith(".msu", StringComparison.OrdinalIgnoreCase) &&
                            fn.IndexOf("KB5043080", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Logger.LogFile("Skipping checkpoint update (automatic scan): " + fn);
                            continue;
                        }
                        filtered.Add(f);
                    }
                }

                // Log exactly what was offered so an empty result is diagnosable
                // rather than a silent dead end.
                Logger.LogFile("Resolved " + files.Count +
                    " file(s) for " + buildNum + " (" + b.Arch + "):");
                foreach (UupFile f in files)
                    Logger.LogFile("    " + f.Filename);

                if (filtered.Count == 0)
                {
                    if (files.Count == 0)
                    {
                        if (log != null) log("No updateOnly files found for " +
                            buildNum + " (" + b.Arch + ").");
                    }
                    else
                    {
                        if (log != null) log("None of the " + files.Count +
                            " offered files matched the filters (see uupdump.log " +
                            "for the full list).");
                    }
                    continue;
                }

                // Require a .NET Framework (NDP) update alongside the LCU.
                // Automatic scans only - an explicitly requested build number
                // always keeps its own files, same convention as the
                // KB5043080 checkpoint exclusion above.
                if (!isManualBuild)
                {
                    bool hasNdp = filtered.Exists(delegate(UupFile f) {
                        return f.Filename.IndexOf("NDP", StringComparison.OrdinalIgnoreCase) >= 0;
                    });
                    if (!hasNdp)
                    {
                        // 24H2/25H2/26H2 are separately-resolvable Microsoft
                        // update objects for the same base build - one can
                        // lack the NDP cab while a sibling has it (verified
                        // live: build .9278's 26H2 view omits NDP, its 24H2/
                        // 25H2 views don't). Retry those before giving up, so
                        // this arch/branch still gets a real download instead
                        // of being skipped outright.
                        bool recovered = false;
                        foreach (string altBuildNum in NdpFallbackBuildNums(buildNum))
                        {
                            if (log != null) log(buildNum + " (" + b.Arch +
                                "): no NDP update in this view - trying " + altBuildNum + " instead...");
                            BuildInfo alt = WuClient.DiscoverExact(altBuildNum, b.Arch, b.Branch, log);
                            if (alt == null) continue;
                            List<UupFile> altFiles = WuClient.ResolveUpdateOnlyFiles(alt.Id, alt.BuildNum, alt.Arch, log);
                            if (altFiles == null) continue;
                            bool altHasNdp = altFiles.Exists(delegate(UupFile f) {
                                return f.Filename.IndexOf("NDP", StringComparison.OrdinalIgnoreCase) >= 0;
                            });
                            if (!altHasNdp) continue;

                            if (log != null) log("Found NDP update via " + altBuildNum + " - using that view instead.");
                            files = altFiles;
                            filtered = altFiles;
                            recovered = true;
                            break;
                        }
                        if (!recovered)
                        {
                            if (log != null) log("Skipping " + buildNum + " (" + b.Arch +
                                "): no .NET Framework (NDP) update in the download list (including sibling views).");
                            continue;
                        }
                    }
                }

                filtered = TrimUnneededFiles(baseNum, filtered, log);

                string targetDir = Path.Combine(
                    Paths.DownloadDir,
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
                    if (log != null) log("Download errors in " + logKey + " – not marking complete.");
                    continue;
                }

                try { if (File.Exists(marker)) File.Delete(marker); } catch { }
                if (log != null) log("Download complete: " + b.Branch + " " + buildNum + " (" + b.Arch + ")");

                if (runProcessor)
                {
                    if (stopEvent != null && stopEvent.WaitOne(0)) return;
                    if (log != null) log("--- Starting processor for " + logKey + " ---");
                    ProcessResult result = null;
                    try
                    {
                        result = Processor.ProcessBuild(
                            targetDir, uploadDest, cleanup, log, stopEvent);
                        if (log != null)
                            log(result.Success
                                ? "Processor finished successfully for " + logKey
                                : "Processor skipped (hotpatch or no-op) for " + logKey);
                    }
                    catch (OperationCanceledException)
                    { if (log != null) log("Processor stopped by user."); return; }
                    catch (Exception ex)
                    { if (log != null) log("Processor error for " + logKey + ": " + ex.Message); }

                    // Only mark as done when the processor (and upload) succeeded.
                    // This ensures a failed upload is retried on the next scan.
                    if (result != null && result.Success)
                    {
                        if (!buildState.ContainsKey(baseNum)) buildState[baseNum] = new Dictionary<string, BuildRecord>();
                        buildState[baseNum][b.Arch] = new BuildRecord {
                            completedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                            files = result.Files,
                        };
                        BuildState.Save(buildState);
                        Logger.Log("Build marked complete: " + logKey);
                    }
                    else
                    {
                        if (log != null) log("Build NOT marked complete – will retry next scan: " + logKey);
                    }
                }
                else
                {
                    // No processor: mark complete immediately after download.
                    // No prepared/processed files exist yet, so the record is
                    // just a completion timestamp with an empty file list.
                    if (!buildState.ContainsKey(baseNum)) buildState[baseNum] = new Dictionary<string, BuildRecord>();
                    buildState[baseNum][b.Arch] = new BuildRecord {
                        completedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        files = new List<BuildFileRecord>(),
                    };
                    BuildState.Save(buildState);
                    if (log != null) log("Processor not enabled – files left in download directory.");
                }
            }
        }

        // Derive the branch label from a UUP Dump entry name, falling back to
        // the build number prefix. Used for manual searches where the branch is
        // not implied by the category page we fetched from.
        static string BranchFromName(string name, string buildNum)
        {
            if (!string.IsNullOrEmpty(name))
            {
                if (name.IndexOf("26H1", StringComparison.OrdinalIgnoreCase) >= 0) return "Windows 11 26H1";
                if (name.IndexOf("26H2", StringComparison.OrdinalIgnoreCase) >= 0) return "Windows 11 26H2";
                if (name.IndexOf("24H2", StringComparison.OrdinalIgnoreCase) >= 0) return "Windows 11 24H2";
                if (name.IndexOf("23H2", StringComparison.OrdinalIgnoreCase) >= 0) return "Windows 11 23H2";
                if (name.IndexOf("22H2", StringComparison.OrdinalIgnoreCase) >= 0) return "Windows 11 22H2";
            }
            if (!string.IsNullOrEmpty(buildNum))
            {
                if (buildNum.StartsWith("28000")) return "Windows 11 26H1";
                if (buildNum.StartsWith("26300")) return "Windows 11 26H2";
                // 26200.x is the retired 25H2 enablement-package view of the
                // same base build tracked as 26H2 (26300.x) - no live "25H2"
                // category/checkbox exists anymore, so fold a manual search
                // for that number into the branch this tool actually tracks,
                // rather than resolving successfully but filing it under a
                // dead branch name nothing else will ever match again.
                if (buildNum.StartsWith("26200")) return "Windows 11 26H2";
                if (buildNum.StartsWith("26100")) return "Windows 11 24H2";
                if (buildNum.StartsWith("22631")) return "Windows 11 23H2";
                if (buildNum.StartsWith("22621")) return "Windows 11 22H2";
            }
            return "Windows 11";
        }

        // Trims a resolved download list down to what Processor.ProcessBuild
        // actually needs, dropping everything else Microsoft's server offers
        // alongside it (extra AggregatedMetadata.cab/DesktopDeployment.cab,
        // an older/smaller redundant .msu, etc.). `internal` rather than
        // `private` so a standalone test harness compiled into the same
        // assembly can call it directly against real captured file lists
        // without needing to run the full download+process pipeline - see
        // CLAUDE.md's note on this method for why that mattered here.
        //
        // 22621/22631 (22H2/23H2, normalized to baseNum 22621.x by the
        // caller) is the one family that ships differently - no .msu wrapper
        // at all, the WIM and PSF are their own standalone downloaded files.
        // Every other branch (24H2/25H2/26H2/26H1, and any future branch
        // that ships the same way, by default rather than needing its own
        // explicit whitelist entry every time Microsoft ships a new one)
        // uses the combined SSU+LCU "checkpoint" .msu, which already bundles
        // the WIM/PSF/SSU cab Processor needs internally (see CLAUDE.md's
        // "combined SSU+LCU checkpoint" notes) - so only the single largest
        // .msu (whichever KB that turns out to be this time - no hardcoding
        // a specific KB number the way the old uupdump.net-scrape filter
        // hardcoded KB5043080) plus the separate NDP (.NET Framework) cab,
        // never bundled inside the checkpoint, are actually needed.
        internal static List<UupFile> TrimUnneededFiles(string baseNum, List<UupFile> filtered, Action<string> log)
        {
            List<UupFile> trimmed;
            string keptDescription;

            if (baseNum.StartsWith("22621"))
            {
                trimmed = new List<UupFile>();
                foreach (UupFile f in filtered)
                {
                    string fn = f.Filename;
                    if (fn.EndsWith(".wim", StringComparison.OrdinalIgnoreCase) ||
                        fn.EndsWith(".psf", StringComparison.OrdinalIgnoreCase) ||
                        fn.IndexOf("SSU", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        fn.IndexOf("NDP", StringComparison.OrdinalIgnoreCase) >= 0)
                        trimmed.Add(f);
                }
                keptDescription = "WIM + PSF + SSU + NDP only";
            }
            else
            {
                UupFile biggestMsu = null;
                foreach (UupFile f in filtered)
                {
                    if (!f.Filename.EndsWith(".msu", StringComparison.OrdinalIgnoreCase)) continue;
                    if (biggestMsu == null || f.Size > biggestMsu.Size) biggestMsu = f;
                }
                trimmed = new List<UupFile>();
                if (biggestMsu != null) trimmed.Add(biggestMsu);
                foreach (UupFile f in filtered)
                    if (f.Filename.IndexOf("NDP", StringComparison.OrdinalIgnoreCase) >= 0)
                        trimmed.Add(f);
                keptDescription = "largest .msu + NDP only";
            }

            if (trimmed.Count == 0) return filtered;
            if (log != null && trimmed.Count < filtered.Count)
                log("Trimmed to " + trimmed.Count + " needed file(s) of " +
                    filtered.Count + " offered (" + keptDescription + ").");
            return trimmed;
        }

        // Enablement-package families: each group is separately-resolvable
        // Microsoft update objects built on the same base OS build (see
        // CLAUDE.md's "Branch category note") - each is its own revision with
        // its own bundled file manifest, so one member can genuinely omit the
        // NDP (.NET Framework) cab while a sibling includes it.
        // 26100/26200/26300 = 24H2/25H2/26H2; 22621/22631 = 22H2/23H2.
        static readonly string[][] NdpSiblingFamilies = new string[][]
        {
            new string[] { "26300", "26200", "26100" },
            new string[] { "22631", "22621" },
        };
        // Returns the other members of buildNum's family, in fallback-
        // preference order, or empty if buildNum's major isn't in any family.
        static List<string> NdpFallbackBuildNums(string buildNum)
        {
            List<string> result = new List<string>();
            int dot = buildNum.IndexOf('.');
            if (dot < 0) return result;
            string major = buildNum.Substring(0, dot);
            string suffix = buildNum.Substring(dot);
            foreach (string[] family in NdpSiblingFamilies)
            {
                if (Array.IndexOf(family, major) < 0) continue;
                foreach (string alt in family)
                    if (alt != major) result.Add(alt + suffix);
                break;
            }
            return result;
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

}
