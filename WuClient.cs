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
    //  WuClient  -  direct Windows Update SOAP client
    //
    //  Talks to Microsoft's own update servers (fe3.delivery.mp.microsoft.com /
    //  fe3cr.delivery.mp.microsoft.com) the same way uupdump.net's own backend
    //  does - reimplemented here from uup-dump/api's open PHP source
    //  (git.uupdump.net/uup-dump/api: shared/requests.php, shared/auths.php,
    //  fetchupd.php, get.php) so this tool no longer has to go through
    //  uupdump.net for discovery or file-URL resolution. Verified live against
    //  the real servers during development (see CLAUDE.md).
    //
    //  uupdump.net itself (Fetcher's HtmlParser-based known.php/get.php calls)
    //  is kept as an automatic fallback for whenever this direct path fails -
    //  e.g. a build Microsoft has aged off its live serving.
    // =========================================================================
    static class WuClient
    {
        const string ClientUrl  = "https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx";
        const string SecuredUrl = "https://fe3cr.delivery.mp.microsoft.com/ClientWebService/client.asmx/secured";
        const string WuNs = "http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService";

        // Rings to probe in order, per category, since which channel currently
        // serves a given branch (GA vs Dev/Beta/Release Preview) shifts over
        // time as Microsoft ships. RETAIL (GA) first since that is the common
        // case; the others only get tried if RETAIL comes back empty.
        static readonly string[] RingProbeOrder = { "RETAIL", "WIF", "WIS", "RP" };

        static readonly Random _rnd = new Random();
        static string   _cookieEncData;
        static DateTime _cookieExpires = DateTime.MinValue;
        static readonly object _cookieLock = new object();

        public sealed class FileEntry
        {
            public string Name;
            public string Sha1;
            public string Sha256;
            public long   Size;
        }

        sealed class DiscoverResult
        {
            public string UpdateId;
            public int    Rev = 1;
            public string Title;
            public string FoundBuild;
            public string FoundArch;
            public string Ring;
            public Dictionary<string, FileEntry> Files = new Dictionary<string, FileEntry>();
        }

        // ---------------------------------------------------------------
        //  Device / UUID / timestamp helpers (port of shared/auths.php +
        //  shared/main.php's genUUID/randStr)
        // ---------------------------------------------------------------
        static string RandHex(int n)
        {
            const string chars = "0123456789abcdef";
            StringBuilder sb = new StringBuilder(n);
            for (int i = 0; i < n; i++) sb.Append(chars[_rnd.Next(16)]);
            return sb.ToString();
        }

        static byte[] HexToBytes(string hex)
        {
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        // PHP's chunk_split($data,1,"\0") is byte-identical to UTF-16LE of $data
        // (each ASCII char followed by a single NUL byte).
        static string UupDevice()
        {
            string header = "13003002c377040014d5bcac7a66de0d50beddf9bba16c87edb9e019898000";
            string random = RandHex(1054);
            string end    = "b401";
            byte[] raw    = HexToBytes(header + random + end);
            string tValue = Convert.ToBase64String(raw);
            string data   = "t=" + tValue + "&p=";
            return Convert.ToBase64String(Encoding.Unicode.GetBytes(data));
        }

        static string GenUuid() { return Guid.NewGuid().ToString(); }

        static string W3c(long unixTime)
        {
            DateTime dt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unixTime);
            return dt.ToString("yyyy-MM-ddTHH:mm:ss") + "+00:00";
        }

        static long NowUnix()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        // Port of branchFromBuild() (shared/requests.php) - only the ranges this
        // tool ever queries (Windows 11 branches); unknown builds fall through
        // to rs_prerelease exactly like the PHP does.
        public static string BranchFromBuild(string build)
        {
            string[] parts = build.Split('.');
            int b = int.Parse(parts[parts.Length >= 3 ? 2 : 0]);
            switch (b)
            {
                case 19041: case 19042: case 19043: case 19044: case 19045: case 19046: return "vb_release";
                case 20348: case 20349: return "fe_release";
                case 22000: return "co_release";
                case 22621: case 22631: case 22635: return "ni_release";
                case 25398: return "zn_release";
                case 26100: case 26120: case 26200: case 26300: return "ge_release";
                case 28000: case 28100: return "br_release";
                default: return "rs_prerelease";
            }
        }

        // ---------------------------------------------------------------
        //  SOAP envelope composers (port of shared/requests.php)
        // ---------------------------------------------------------------
        static string ComposeDeviceAttributes(string ring, string build, string arch,
            int sku, string type, List<string> flags, string branch)
        {
            if (branch == "auto") branch = BranchFromBuild(build);

            string dvcFamily = "Windows.Desktop";
            string insType   = "Client";
            string prodType  = "WinNT";

            string fltBranch = "";
            string fltRing = "External";
            int flightEnabled = 1;
            int isRetail = 0;

            if (ring == "RETAIL") { fltRing = "Retail"; flightEnabled = 0; isRetail = 1; }
            if (ring == "WIF") fltBranch = "Dev";
            if (ring == "WIS") fltBranch = "Beta";
            if (ring == "RP")  fltBranch = "ReleasePreview";
            if (ring == "MSIT") { fltBranch = "MSIT"; fltRing = "Internal"; }

            long now = NowUnix();
            List<string> a = new List<string>();
            a.Add("App=WU_OS");
            a.Add("AppVer=" + build);
            a.Add("AttrDataVer=352");
            a.Add("AllowInPlaceUpgrade=1");
            a.Add("AllowOptionalContent=1");
            a.Add("AllowUpgradesWithUnsupportedTPMOrCPU=1");
            a.Add("BlockFeatureUpdates=0");
            a.Add("BranchReadinessLevel=CB");
            a.Add("CIOptin=1");
            a.Add("CurrentBranch=" + branch);
            a.Add("DataExpDateEpoch_GE25H2=" + (now + 82800));
            a.Add("DataExpDateEpoch_GE24H2=" + (now + 82800));
            a.Add("DataExpDateEpoch_GE24H2Setup=" + (now + 82800));
            a.Add("DataExpDateEpoch_CU23H2=" + (now + 82800));
            a.Add("DataExpDateEpoch_CU23H2Setup=" + (now + 82800));
            a.Add("DataExpDateEpoch_NI22H2=" + (now + 82800));
            a.Add("DataExpDateEpoch_NI22H2Setup=" + (now + 82800));
            a.Add("DataExpDateEpoch_CO21H2=" + (now + 82800));
            a.Add("DataExpDateEpoch_CO21H2Setup=" + (now + 82800));
            a.Add("DataExpDateEpoch_23H2=" + (now + 82800));
            a.Add("DataExpDateEpoch_22H2=" + (now + 82800));
            a.Add("DataExpDateEpoch_21H2=" + (now + 82800));
            a.Add("DataExpDateEpoch_21H1=" + (now + 82800));
            a.Add("DataExpDateEpoch_20H1=" + (now + 82800));
            a.Add("DataExpDateEpoch_19H1=" + (now + 82800));
            a.Add("DataVer_RS5=2000000000");
            a.Add("DefaultUserRegion=191");
            a.Add("DeviceFamily=" + dvcFamily);
            a.Add("DeviceInfoGatherSuccessful=1");
            a.Add("EKB19H2InstallCount=1");
            a.Add("EKB19H2InstallTimeEpoch=1255000000");
            a.Add("FlightingBranchName=" + fltBranch);
            a.Add("FlightRing=" + fltRing);
            a.Add("Free=gt64");
            a.Add("GStatus_GE25H2=2");
            a.Add("GStatus_GE24H2=2");
            a.Add("GStatus_GE24H2Setup=2");
            a.Add("GStatus_CU23H2=2");
            a.Add("GStatus_CU23H2Setup=2");
            a.Add("GStatus_NI23H2=2");
            a.Add("GStatus_NI22H2=2");
            a.Add("GStatus_NI22H2Setup=2");
            a.Add("GStatus_CO21H2=2");
            a.Add("GStatus_CO21H2Setup=2");
            a.Add("GStatus_22H2=2");
            a.Add("GStatus_21H2=2");
            a.Add("GStatus_21H1=2");
            a.Add("GStatus_20H1=2");
            a.Add("GStatus_20H1Setup=2");
            a.Add("GStatus_19H1=2");
            a.Add("GStatus_19H1Setup=2");
            a.Add("GStatus_RS5=2");
            a.Add("GenTelRunTimestamp_19H1=" + (now - 3600));
            a.Add("InstallDate=1438196400");
            a.Add("InstallLanguage=en-US");
            a.Add("InstallationType=" + insType);
            a.Add("IsDeviceRetailDemo=0");
            a.Add("IsFlightingEnabled=" + flightEnabled);
            a.Add("IsRetailOS=" + isRetail);
            a.Add("LaunchUserOOBE=1");
            a.Add("LCUVer=0.0.0.0");
            a.Add("MediaBranch=");
            a.Add("MediaVersion=" + build);
            a.Add("CloudPBR=1");
            a.Add("DUScan=1");
            a.Add("OEMModel=21F6CTO1WW");
            a.Add("OEMModelBaseBoard=21F6CTO1WW");
            a.Add("OEMName_Uncleaned=LENOVO");
            a.Add("OemPartnerRing=UPSFlighting");
            a.Add("OSArchitecture=" + arch);
            a.Add("OSSkuId=" + sku);
            a.Add("OSUILocale=en-US");
            a.Add("OSVersion=" + build);
            a.Add("ProcessorIdentifier=Intel64 Family 6 Model 186 Stepping 3");
            a.Add("ProcessorManufacturer=GenuineIntel");
            a.Add("ProcessorModel=13th Gen Intel(R) Core(TM) i7-1355U");
            a.Add("ProductType=" + prodType);
            a.Add("ReleaseType=" + type);
            a.Add("SdbVer_20H1=2000000000");
            a.Add("SdbVer_19H1=2000000000");
            a.Add("SecureBootCapable=1");
            a.Add("TelemetryLevel=3");
            a.Add("TimestampEpochString_GE24H2=" + (now - 3600));
            a.Add("TimestampEpochString_GE24H2Setup=" + (now - 3600));
            a.Add("TimestampEpochString_CU23H2=" + (now - 3600));
            a.Add("TimestampEpochString_CU23H2Setup=" + (now - 3600));
            a.Add("TimestampEpochString_NI23H2=" + (now - 3600));
            a.Add("TimestampEpochString_NI22H2=" + (now - 3600));
            a.Add("TimestampEpochString_NI22H2Setup=" + (now - 3600));
            a.Add("TimestampEpochString_CO21H2=" + (now - 3600));
            a.Add("TimestampEpochString_CO21H2Setup=" + (now - 3600));
            a.Add("TimestampEpochString_22H2=" + (now - 3600));
            a.Add("TimestampEpochString_21H2=" + (now - 3600));
            a.Add("TimestampEpochString_21H1=" + (now - 3600));
            a.Add("TimestampEpochString_20H1=" + (now - 3600));
            a.Add("TimestampEpochString_19H1=" + (now - 3600));
            a.Add("TPMVersion=2");
            a.Add("UpdateManagementGroup=2");
            a.Add("UpdateOfferedDays=0");
            a.Add("UpgEx_GE25H2=Green");
            a.Add("UpgEx_GE24H2Setup=Green");
            a.Add("UpgEx_GE24H2=Green");
            a.Add("UpgEx_CU23H2=Green");
            a.Add("UpgEx_NI23H2=Green");
            a.Add("UpgEx_NI22H2=Green");
            a.Add("UpgEx_CO21H2=Green");
            a.Add("UpgEx_23H2=Green");
            a.Add("UpgEx_22H2=Green");
            a.Add("UpgEx_21H2=Green");
            a.Add("UpgEx_21H1=Green");
            a.Add("UpgEx_20H1=Green");
            a.Add("UpgEx_19H1=Green");
            a.Add("UpgEx_RS5=Green");
            a.Add("UpgradeAccepted=1");
            a.Add("UpgradeEligible=1");
            a.Add("UserInPlaceUpgrade=1");
            a.Add("VBSState=2");
            a.Add("Version_RS5=2000000000");
            a.Add("Win10CommercialAzureESUEligible=1");
            a.Add("Win10CommercialKeybasedESUEligible=1");
            a.Add("Win10CommercialW365ESUEligible=1");
            a.Add("Win10ConsumerESUStatus=3");
            a.Add("Win10ConsumerESUAY=9");
            a.Add("WuClientVer=" + build);

            if (flags.Contains("thisonly")) a.Add("MediaBranch=" + branch);

            return WebUtility.HtmlEncode("E:" + string.Join("&", a.ToArray()));
        }

        static readonly int[] InstalledNonLeafIds = new int[] {
            1,10,105939029,105995585,106017178,107825194,10809856,11,117765322,129905029,
            130040030,130040031,130040032,130040033,133399034,138372035,138372036,139536037,
            139536038,139536039,139536040,142045136,158941041,158941042,158941043,158941044,
            159776047,160733048,160733049,160733050,160733051,160733055,160733056,161870057,
            161870058,161870059,17,19,2,23110993,23110994,23110995,23110996,23110999,23111000,
            23111001,23111002,23111003,23111004,2359974,2359977,24513870,28880263,296374060,3,
            30077688,30486944,316003061,326686062,326686063,327065581,327072300,327072305,
            327100345,5143990,5169043,5169044,5169047,59830006,59830007,59830008,60484010,
            62450018,62450019,62450020,69801474,8788830,8806526,9125350,9154769,98959022,
            98959023,98959024,98959025,98959026
        };

        static string ComposeGetCookieRequest()
        {
            string device = UupDevice();
            string uuid = GenUuid();
            long created = NowUnix();
            long expires = created + 120;
            return
"<s:Envelope xmlns:a=\"http://www.w3.org/2005/08/addressing\" xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" +
"<s:Header>" +
"<a:Action s:mustUnderstand=\"1\">http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService/GetCookie</a:Action>" +
"<a:MessageID>urn:uuid:" + uuid + "</a:MessageID>" +
"<a:To s:mustUnderstand=\"1\">https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx</a:To>" +
"<o:Security s:mustUnderstand=\"1\" xmlns:o=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\">" +
"<Timestamp xmlns=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">" +
"<Created>" + W3c(created) + "</Created><Expires>" + W3c(expires) + "</Expires></Timestamp>" +
"<wuws:WindowsUpdateTicketsToken wsu:id=\"ClientMSA\" xmlns:wsu=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\" xmlns:wuws=\"http://schemas.microsoft.com/msus/2014/10/WindowsUpdateAuthorization\">" +
"<TicketType Name=\"MSA\" Version=\"1.0\" Policy=\"MBI_SSL\"><Device>" + device + "</Device></TicketType>" +
"</wuws:WindowsUpdateTicketsToken></o:Security></s:Header>" +
"<s:Body><GetCookie xmlns=\"" + WuNs + "\">" +
"<oldCookie><Expiration>" + W3c(created) + "</Expiration></oldCookie>" +
"<lastChange>" + W3c(created) + "</lastChange><currentTime>" + W3c(created) + "</currentTime>" +
"<protocolVersion>2.0</protocolVersion></GetCookie></s:Body></s:Envelope>";
        }

        static string ComposeFetchUpdRequest(string arch, string ring, string build,
            int sku, string type, List<string> flags, string branch, string encData)
        {
            string device = UupDevice();
            string uuid = GenUuid();
            long created = NowUnix();
            long expires = created + 120;
            long cookieExpires = created + 604800;

            if (branch == "auto") branch = BranchFromBuild(build);

            string mainProduct = "Client.OS.rs2";
            List<string> products = new List<string>();
            products.Add("PN=" + mainProduct + "." + arch + "&Branch=" + branch + "&PrimaryOSProduct=1&Repairable=1&V=" + build + "&ReofferUpdate=1");
            products.Add("PN=Adobe.Flash." + arch + "&Repairable=1&V=0.0.0.0");
            products.Add("PN=Microsoft.Edge.Stable." + arch + "&Repairable=1&V=0.0.0.0");
            products.Add("PN=Microsoft.NETFX." + arch + "&V=0.0.0.0");
            products.Add("PN=Windows.Autopilot." + arch + "&Repairable=1&V=0.0.0.0");
            products.Add("PN=Windows.AutopilotOOBE." + arch + "&Repairable=1&V=0.0.0.0");
            products.Add("PN=Windows.Appraiser." + arch + "&Repairable=1&V=" + build);
            products.Add("PN=Windows.AppraiserData." + arch + "&Repairable=1&V=" + build);
            products.Add("PN=Windows.EmergencyUpdate." + arch + "&V=" + build);
            products.Add("PN=Windows.FeatureExperiencePack." + arch + "&Repairable=1&V=0.0.0.0");
            products.Add("PN=Windows.ManagementOOBE." + arch + "&IsWindowsManagementOOBE=1&Repairable=1&V=" + build);
            products.Add("PN=Windows.OOBE." + arch + "&IsWindowsOOBE=1&Repairable=1&V=" + build);
            products.Add("PN=Windows.OOBE.Cumulative." + arch + "&V=0.0.0.0");
            products.Add("PN=Windows.OOBE.Standalone." + arch + "&V=0.0.0.0");
            products.Add("PN=Windows.UpdateStackPackage." + arch + "&Name=Update Stack Package&Repairable=1&V=" + build);
            products.Add("PN=Hammer." + arch + "&Source=UpdateOrchestrator&V=0.0.0.0");
            products.Add("PN=MSRT." + arch + "&Source=UpdateOrchestrator&V=0.0.0.0");
            products.Add("PN=SedimentPack." + arch + "&Source=UpdateOrchestrator&V=0.0.0.0");
            products.Add("PN=UUS." + arch + "&Source=UpdateOrchestrator&V=0.0.0.0");

            List<string> callerAttrib = new List<string>();
            callerAttrib.Add("Profile=AUv2");
            callerAttrib.Add("Acquisition=1");
            callerAttrib.Add("Interactive=1");
            callerAttrib.Add("IsSeeker=1");
            callerAttrib.Add("SheddingAware=1");
            callerAttrib.Add("Id=MoUpdateOrchestrator");

            string productsStr = WebUtility.HtmlEncode(string.Join(";", products.ToArray()));
            string callerAttribStr = WebUtility.HtmlEncode("E:" + string.Join("&", callerAttrib.ToArray()));
            string deviceAttributes = ComposeDeviceAttributes(ring, build, arch, sku, type, flags, branch);
            string syncCurrent = flags.Contains("thisonly") ? "true" : "false";

            StringBuilder ids = new StringBuilder();
            foreach (int id in InstalledNonLeafIds) ids.Append("<int>" + id + "</int>");

            return
"<s:Envelope xmlns:a=\"http://www.w3.org/2005/08/addressing\" xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" +
"<s:Header>" +
"<a:Action s:mustUnderstand=\"1\">http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService/SyncUpdates</a:Action>" +
"<a:MessageID>urn:uuid:" + uuid + "</a:MessageID>" +
"<a:To s:mustUnderstand=\"1\">https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx</a:To>" +
"<o:Security s:mustUnderstand=\"1\" xmlns:o=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\">" +
"<Timestamp xmlns=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">" +
"<Created>" + W3c(created) + "</Created><Expires>" + W3c(expires) + "</Expires></Timestamp>" +
"<wuws:WindowsUpdateTicketsToken wsu:id=\"ClientMSA\" xmlns:wsu=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\" xmlns:wuws=\"http://schemas.microsoft.com/msus/2014/10/WindowsUpdateAuthorization\">" +
"<TicketType Name=\"MSA\" Version=\"1.0\" Policy=\"MBI_SSL\"><Device>" + device + "</Device></TicketType>" +
"</wuws:WindowsUpdateTicketsToken></o:Security></s:Header>" +
"<s:Body><SyncUpdates xmlns=\"" + WuNs + "\">" +
"<cookie><Expiration>" + W3c(cookieExpires) + "</Expiration><EncryptedData>" + encData + "</EncryptedData></cookie>" +
"<parameters><ExpressQuery>false</ExpressQuery>" +
"<InstalledNonLeafUpdateIDs>" + ids + "</InstalledNonLeafUpdateIDs>" +
"<OtherCachedUpdateIDs/><SkipSoftwareSync>false</SkipSoftwareSync>" +
"<NeedTwoGroupOutOfScopeUpdates>true</NeedTwoGroupOutOfScopeUpdates>" +
"<AlsoPerformRegularSync>true</AlsoPerformRegularSync><ComputerSpec/>" +
"<ExtendedUpdateInfoParameters><XmlUpdateFragmentTypes>" +
"<XmlUpdateFragmentType>Extended</XmlUpdateFragmentType>" +
"<XmlUpdateFragmentType>LocalizedProperties</XmlUpdateFragmentType>" +
"</XmlUpdateFragmentTypes><Locales><string>en-US</string></Locales></ExtendedUpdateInfoParameters>" +
"<ClientPreferredLanguages/><ProductsParameters>" +
"<SyncCurrentVersionOnly>" + syncCurrent + "</SyncCurrentVersionOnly>" +
"<DeviceAttributes>" + deviceAttributes + "</DeviceAttributes>" +
"<CallerAttributes>" + callerAttribStr + "</CallerAttributes>" +
"<Products>" + productsStr + "</Products>" +
"</ProductsParameters></parameters></SyncUpdates></s:Body></s:Envelope>";
        }

        static string ComposeFileGetRequest(string updateId, int rev, string ring,
            string checkBuild, string arch, int sku, string type)
        {
            string device = UupDevice();
            string uuid = GenUuid();
            long created = NowUnix();
            long expires = created + 120;

            string deviceAttributes = ComposeDeviceAttributes(ring, checkBuild, arch, sku, type,
                new List<string>(), "auto");

            return
"<s:Envelope xmlns:a=\"http://www.w3.org/2005/08/addressing\" xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" +
"<s:Header>" +
"<a:Action s:mustUnderstand=\"1\">http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService/GetExtendedUpdateInfo2</a:Action>" +
"<a:MessageID>urn:uuid:" + uuid + "</a:MessageID>" +
"<a:To s:mustUnderstand=\"1\">https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx/secured</a:To>" +
"<o:Security s:mustUnderstand=\"1\" xmlns:o=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\">" +
"<Timestamp xmlns=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">" +
"<Created>" + W3c(created) + "</Created><Expires>" + W3c(expires) + "</Expires></Timestamp>" +
"<wuws:WindowsUpdateTicketsToken wsu:id=\"ClientMSA\" xmlns:wsu=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\" xmlns:wuws=\"http://schemas.microsoft.com/msus/2014/10/WindowsUpdateAuthorization\">" +
"<TicketType Name=\"MSA\" Version=\"1.0\" Policy=\"MBI_SSL\"><Device>" + device + "</Device></TicketType>" +
"</wuws:WindowsUpdateTicketsToken></o:Security></s:Header>" +
"<s:Body><GetExtendedUpdateInfo2 xmlns=\"" + WuNs + "\">" +
"<updateIDs><UpdateIdentity><UpdateID>" + updateId + "</UpdateID><RevisionNumber>" + rev + "</RevisionNumber></UpdateIdentity></updateIDs>" +
"<infoTypes>" +
"<XmlUpdateFragmentType>FileUrl</XmlUpdateFragmentType>" +
"<XmlUpdateFragmentType>FileDecryption</XmlUpdateFragmentType>" +
"<XmlUpdateFragmentType>EsrpDecryptionInformation</XmlUpdateFragmentType>" +
"<XmlUpdateFragmentType>PiecesHashUrl</XmlUpdateFragmentType>" +
"<XmlUpdateFragmentType>BlockMapUrl</XmlUpdateFragmentType>" +
"</infoTypes><deviceAttributes>" + deviceAttributes + "</deviceAttributes>" +
"</GetExtendedUpdateInfo2></s:Body></s:Envelope>";
        }

        // ---------------------------------------------------------------
        //  HTTP transport
        // ---------------------------------------------------------------
        static string PostSoap(string url, string body, Action<string> log)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.UserAgent = "Windows-Update-Agent/10.0.10011.16384 Client-Protocol/2.50";
            req.ContentType = "application/soap+xml; charset=utf-8";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            byte[] data = Encoding.UTF8.GetBytes(body);
            req.ContentLength = data.Length;
            using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        static string GetEncryptedCookie(Action<string> log)
        {
            lock (_cookieLock)
            {
                if (_cookieEncData != null && DateTime.UtcNow < _cookieExpires)
                    return _cookieEncData;

                string xml = PostSoap(ClientUrl, ComposeGetCookieRequest(), log);
                Match m = Regex.Match(xml, "<EncryptedData>(.*?)</EncryptedData>", RegexOptions.Singleline);
                if (!m.Success)
                    throw new Exception("Windows Update GetCookie: no EncryptedData in response.");

                _cookieEncData = m.Groups[1].Value;
                // Real cookie validity is much longer (~90 days per GetCookieResult
                // Expiration); re-fetching daily is cheap and avoids any edge case
                // around the server-side expiry we are not tracking exactly.
                _cookieExpires = DateTime.UtcNow.AddHours(20);
                return _cookieEncData;
            }
        }

        static XmlNamespaceManager NsMgr(XmlDocument doc)
        {
            XmlNamespaceManager nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("wu", WuNs);
            return nsmgr;
        }

        static string HexOfBase64(string b64)
        {
            byte[] bytes = Convert.FromBase64String(b64);
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // Port of uupFetchUpd2()+parseFetchUpdate(): finds the leaf UpdateInfo
        // whose ProductReleaseInstalled Name matches mainProduct.arch exactly.
        // Unlike the PHP (which just takes updateArray[0] after an incidental
        // string sort), we filter by product explicitly - with the full
        // companion-product list in the request, unrelated leaves (OOBE,
        // Appraiser, etc.) can legitimately come back in the same response.
        static DiscoverResult ParseSyncUpdates(string syncXml, string wantProduct)
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(syncXml);
            XmlNamespaceManager nsmgr = NsMgr(doc);

            XmlNodeList updateInfos = doc.SelectNodes("//wu:UpdateInfo", nsmgr);
            XmlNodeList updates = doc.SelectNodes("//wu:ExtendedUpdateInfo/wu:Updates/wu:Update", nsmgr);
            if (updateInfos == null || updates == null) return null;

            Dictionary<string, List<XmlDocument>> byId = new Dictionary<string, List<XmlDocument>>();
            foreach (XmlNode u in updates)
            {
                XmlNode idNode = u.SelectSingleNode("wu:ID", nsmgr);
                XmlNode xmlNode = u.SelectSingleNode("wu:Xml", nsmgr);
                if (idNode == null || xmlNode == null) continue;
                XmlDocument fragDoc = new XmlDocument();
                try { fragDoc.LoadXml("<r>" + xmlNode.InnerText + "</r>"); } catch { continue; }
                string id = idNode.InnerText;
                if (!byId.ContainsKey(id)) byId[id] = new List<XmlDocument>();
                byId[id].Add(fragDoc);
            }

            foreach (XmlNode ui in updateInfos)
            {
                XmlNode isLeafNode = ui.SelectSingleNode("wu:IsLeaf", nsmgr);
                if (isLeafNode == null || isLeafNode.InnerText != "true") continue;

                XmlNode localIdNode = ui.SelectSingleNode("wu:ID", nsmgr);
                XmlNode leafXmlNode = ui.SelectSingleNode("wu:Xml", nsmgr);
                if (localIdNode == null || leafXmlNode == null) continue;

                XmlDocument leafFrag = new XmlDocument();
                try { leafFrag.LoadXml("<r>" + leafXmlNode.InnerText + "</r>"); } catch { continue; }

                XmlNode pri = leafFrag.SelectSingleNode("//ProductReleaseInstalled");
                if (pri == null) continue;
                string prodName = pri.Attributes["Name"].Value;
                if (!string.Equals(prodName, wantProduct, StringComparison.OrdinalIgnoreCase)) continue;

                XmlNode updId = leafFrag.SelectSingleNode("//UpdateIdentity");
                if (updId == null) continue;

                DiscoverResult r = new DiscoverResult();
                r.UpdateId = updId.Attributes["UpdateID"].Value;
                r.Rev = updId.Attributes["RevisionNumber"] != null
                    ? int.Parse(updId.Attributes["RevisionNumber"].Value) : 1;

                string version = pri.Attributes["Version"].Value;
                string[] vp = version.Split('.');
                if (vp.Length < 4) continue;
                r.FoundBuild = vp[2] + "." + vp[3];
                string[] np = prodName.Split('.');
                r.FoundArch = np[np.Length - 1];

                string localId = localIdNode.InnerText;
                if (byId.ContainsKey(localId))
                {
                    foreach (XmlDocument comp in byId[localId])
                    {
                        XmlNode titleNode = comp.SelectSingleNode("//Title");
                        if (titleNode != null && r.Title == null) r.Title = titleNode.InnerText;

                        XmlNodeList fileNodes = comp.SelectNodes("//Files/File");
                        if (fileNodes == null) continue;
                        foreach (XmlNode fn in fileNodes)
                        {
                            if (fn.Attributes["Digest"] == null || fn.Attributes["FileName"] == null ||
                                fn.Attributes["Size"] == null) continue;

                            FileEntry fe = new FileEntry();
                            fe.Name = fn.Attributes["FileName"].Value;
                            fe.Size = long.Parse(fn.Attributes["Size"].Value);
                            fe.Sha1 = HexOfBase64(fn.Attributes["Digest"].Value);

                            XmlNode add256 = fn.SelectSingleNode("AdditionalDigest[@Algorithm='SHA256']");
                            if (add256 != null) fe.Sha256 = HexOfBase64(add256.InnerText);

                            r.Files[fe.Sha1] = fe;
                        }
                    }
                }

                if (r.Title == null) r.Title = "Windows build " + r.FoundBuild;
                return r;
            }
            return null;
        }

        static DiscoverResult DiscoverInternal(string arch, string build, List<string> flags, Action<string> log, int sku)
        {
            string wantProduct = "Client.OS.rs2." + arch;
            string cookie;
            try { cookie = GetEncryptedCookie(log); }
            catch (Exception ex)
            {
                if (log != null) Logger.LogFile("WuClient: GetCookie failed: " + ex.Message);
                return null;
            }

            foreach (string ring in RingProbeOrder)
            {
                string syncXml;
                try
                {
                    syncXml = PostSoap(ClientUrl,
                        ComposeFetchUpdRequest(arch, ring, build, sku, "Production", flags, "auto", cookie), log);
                }
                catch (Exception ex)
                {
                    Logger.LogFile("WuClient: SyncUpdates (" + ring + ") failed: " + ex.Message);
                    continue;
                }

                DiscoverResult r;
                try { r = ParseSyncUpdates(syncXml, wantProduct); }
                catch (Exception ex)
                {
                    Logger.LogFile("WuClient: SyncUpdates (" + ring + ") parse failed: " + ex.Message);
                    continue;
                }

                if (r != null)
                {
                    r.Ring = ring;
                    Logger.LogFile("WuClient: matched on ring " + ring + " (sku=" + sku + ") for " + wantProduct);
                    return r;
                }
            }
            return null;
        }

        // Windows editions can be serviced on different tracks once a branch
        // is old enough that some editions reach end-of-servicing before
        // others (e.g. 23H2: Home/Pro's last build was 22631.6199, while
        // Enterprise/Education/IoT Enterprise kept going past 22631.7517 -
        // verified live against Microsoft plus the real KB5068865/KB5120240
        // support articles). A "latest" query pinned to one SKU can silently
        // cap out at that SKU's own end-of-servicing build even though a
        // genuinely newer build exists for the same branch/arch on another
        // edition's track. Probing multiple SKUs and keeping whichever comes
        // back newest means this tool always gets the real latest build for
        // a branch, regardless of which Windows edition happens to still be
        // serviced there - not limited to whatever one SKU's track offers.
        // 48 = PRODUCT_PROFESSIONAL (the common case, and uupdump.net's own
        // fetchupd.php default - verified via the real source), 4 =
        // PRODUCT_ENTERPRISE (both real, public WinNT.h/GetProductInfo SKU
        // IDs, not guessed).
        static readonly int[] SkuProbeOrder = { 48, 4 };

        static int CompareBuildStrings(string a, string b)
        {
            string[] pa = a.Split('.'), pb = b.Split('.');
            int amaj = int.Parse(pa[0]), amin = pa.Length > 1 ? int.Parse(pa[1]) : 0;
            int bmaj = int.Parse(pb[0]), bmin = pb.Length > 1 ? int.Parse(pb[1]) : 0;
            if (amaj != bmaj) return amaj.CompareTo(bmaj);
            return amin.CompareTo(bmin);
        }

        // Discover the current latest build for one branch category + arch.
        // seedBuild is only a baseline used to derive the branch codename and
        // the query's own OSVersion - the server returns whatever is actually
        // newer for that branch, not literally "seedBuild + 1". Tries every
        // SKU in SkuProbeOrder and keeps whichever result reports the highest
        // build number - see SkuProbeOrder's own comment for why.
        public static BuildInfo DiscoverLatest(string seedBuild, string arch, string branchDisplay, Action<string> log)
        {
            DiscoverResult best = null;
            foreach (int sku in SkuProbeOrder)
            {
                DiscoverResult r = DiscoverInternal(arch, "10.0." + seedBuild, new List<string>(), log, sku);
                if (r == null) continue;
                Logger.LogFile("WuClient: sku=" + sku + " -> " + r.FoundBuild + " for " + branchDisplay + " (" + arch + ")");
                if (best == null || CompareBuildStrings(r.FoundBuild, best.FoundBuild) > 0)
                    best = r;
            }
            if (best == null) return null;
            return ToBuildInfo(best, branchDisplay);
        }

        // Discover an exact build number directly (manual search), using the
        // 'thisonly' flag so Microsoft is asked for that specific version
        // rather than "whatever is latest". Old builds Microsoft has aged off
        // its live serving legitimately come back null here. SKU doesn't
        // matter here - verified live that all 4 rings resolve the exact same
        // UpdateID for a given 'thisonly' build regardless of SKU, since this
        // pins to a known update rather than asking "what's next for me".
        public static BuildInfo DiscoverExact(string buildNumFull, string arch, string branchDisplay, Action<string> log)
        {
            List<string> thisOnly = new List<string> { "thisonly" };
            DiscoverResult r = DiscoverInternal(arch, "10.0." + buildNumFull, thisOnly, log, 48);
            if (r == null) return null;
            return ToBuildInfo(r, branchDisplay);
        }

        static BuildInfo ToBuildInfo(DiscoverResult r, string branchDisplay)
        {
            BuildInfo b = new BuildInfo();
            b.Id = r.Rev != 1 ? r.UpdateId + "_rev." + r.Rev : r.UpdateId;
            b.Name = r.Title;
            b.Arch = r.FoundArch.ToLower();
            b.BuildNum = r.FoundBuild;
            b.Branch = branchDisplay;
            return b;
        }

        // Port of get.php's edition=updateOnly filter chain: diffs/baseless/
        // express-cab/psf dedup, then the KB/SSU regex (+ AggregatedMetadata/
        // DesktopDeployment cabs for build > 21380).
        static List<string> FilterUpdateOnly(IEnumerable<string> allNames, int build)
        {
            List<string> names = new List<string>(allNames);

            names.RemoveAll(delegate(string n) {
                return Regex.IsMatch(n, @"_Diffs_|_Forward_CompDB_|\.cbsu\.cab$", RegexOptions.IgnoreCase);
            });

            names.RemoveAll(delegate(string n) {
                return Regex.IsMatch(n, "^baseless_", RegexOptions.IgnoreCase);
            });

            List<string> expressCabs = names.FindAll(delegate(string n) {
                return Regex.IsMatch(n, @"Windows(10|11)\.0-KB.*-EXPRESS|SSU-.*-EXPRESS", RegexOptions.IgnoreCase);
            });
            List<string> expressBaseNames = new List<string>();
            foreach (string ec in expressCabs)
            {
                expressBaseNames.Add(Regex.Replace(ec, "-EXPRESS.cab$", "", RegexOptions.IgnoreCase));
                names.Remove(ec);
            }
            foreach (string bn in expressBaseNames)
                if (names.Contains(bn + ".cab") && names.Contains(bn + ".psf"))
                    names.Remove(bn + ".psf");

            List<string> nonKbPsfs = names.FindAll(delegate(string n) {
                return n.EndsWith(".psf", StringComparison.OrdinalIgnoreCase) &&
                       !Regex.IsMatch(n, @"Windows(10|11)\.0-KB", RegexOptions.IgnoreCase);
            });
            foreach (string p in nonKbPsfs) names.Remove(p);

            List<string> baselessKb = names.FindAll(delegate(string n) {
                return Regex.IsMatch(n, @"Windows(10|11)\.0-KB.*-baseless", RegexOptions.IgnoreCase);
            });
            foreach (string b in baselessKb) names.Remove(b);

            List<string> msus = names.FindAll(delegate(string n) {
                return n.EndsWith(".msu", StringComparison.OrdinalIgnoreCase);
            });
            foreach (string msu in msus)
            {
                string baseName = msu.Substring(0, msu.Length - 4);
                if (names.Contains(baseName + ".cab")) names.Remove(msu);
            }

            const string updatesRegex = @"Windows(10|11)\.0-KB|SSU-.*?\....$";
            List<string> kept = names.FindAll(delegate(string n) {
                return Regex.IsMatch(n, updatesRegex, RegexOptions.IgnoreCase);
            });
            if (build > 21380)
            {
                List<string> agg = names.FindAll(delegate(string n) {
                    return Regex.IsMatch(n, @"AggregatedMetadata.*?\.cab|DesktopDeployment.*?\.cab", RegexOptions.IgnoreCase);
                });
                foreach (string a in agg) if (!kept.Contains(a)) kept.Add(a);
            }
            return kept;
        }

        // Resolve the updateOnly file list (real Microsoft CDN URLs + hashes)
        // for a build previously discovered by this class or by the legacy
        // uupdump.net scrape (id may or may not carry a "_rev.N" suffix,
        // exactly like uupdump.net's own updateId format). Returns null on
        // any failure so the caller can fall back to uupdump.net's get.php.
        public static List<UupFile> ResolveUpdateOnlyFiles(string id, string buildNum, string arch, Action<string> log)
        {
            try
            {
                string updateId = id;
                int rev = 1;
                int revIdx = id.IndexOf("_rev.", StringComparison.OrdinalIgnoreCase);
                if (revIdx >= 0)
                {
                    updateId = id.Substring(0, revIdx);
                    rev = int.Parse(id.Substring(revIdx + 5));
                }

                string checkBuild = "10.0." + buildNum;

                Dictionary<string, string> nameToUrl = null;
                Dictionary<string, FileEntry> nameToEntry = null;

                // GetExtendedUpdateInfo2 can genuinely return zero
                // FileLocations for an otherwise-real, discoverable UpdateID
                // when queried under the wrong SKU - verified live: an
                // Enterprise-only build (e.g. 23H2's 22631.7517, found via
                // DiscoverLatest's sku=4 probe) came back completely empty on
                // every ring under sku=48, the same hardcoded value this loop
                // used to always send regardless of which SKU actually
                // discovered the build. Probing every SKU here too - not
                // just reusing whatever discovered it - fixes this uniformly
                // for both automatic LATEST scans and manual exact-build
                // lookups (DiscoverExact doesn't know or care which SKU would
                // eventually be needed for file resolution).
                foreach (int sku in SkuProbeOrder)
                {
                    foreach (string ring in RingProbeOrder)
                    {
                        string fileXml;
                        try
                        {
                            fileXml = PostSoap(SecuredUrl,
                                ComposeFileGetRequest(updateId, rev, ring, checkBuild, arch, sku, "Production"), log);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogFile("WuClient: GetExtendedUpdateInfo2 (" + ring + ", sku=" + sku + ") failed: " + ex.Message);
                            continue;
                        }

                        XmlDocument fdoc = new XmlDocument();
                        try { fdoc.LoadXml(fileXml); }
                        catch (Exception ex)
                        {
                            Logger.LogFile("WuClient: GetExtendedUpdateInfo2 (" + ring + ", sku=" + sku + ") XML parse failed: " + ex.Message);
                            continue;
                        }

                        XmlNamespaceManager fnsmgr = NsMgr(fdoc);
                        XmlNodeList locs = fdoc.SelectNodes("//wu:FileLocations/wu:FileLocation", fnsmgr);
                        if (locs == null || locs.Count == 0)
                        {
                            Logger.LogFile("WuClient: GetExtendedUpdateInfo2 (" + ring + ", sku=" + sku + ") returned no FileLocations for " + updateId);
                            continue;
                        }

                        Dictionary<string, FileEntry> discFiles = FetchFileMetadata(updateId, rev, arch, checkBuild, ring, sku, log);
                        if (discFiles == null || discFiles.Count == 0)
                        {
                            Logger.LogFile("WuClient: FetchFileMetadata (" + ring + ", sku=" + sku + ") returned no files for " + updateId);
                            continue;
                        }

                        nameToUrl = new Dictionary<string, string>();
                        nameToEntry = new Dictionary<string, FileEntry>();
                        foreach (XmlNode loc in locs)
                        {
                            XmlNode digestNode = loc.SelectSingleNode("wu:FileDigest", fnsmgr);
                            XmlNode urlNode = loc.SelectSingleNode("wu:Url", fnsmgr);
                            if (digestNode == null || urlNode == null) continue;

                            string sha1 = HexOfBase64(digestNode.InnerText);
                            if (!discFiles.ContainsKey(sha1)) continue;

                            FileEntry fe = discFiles[sha1];
                            nameToUrl[fe.Name] = urlNode.InnerText;
                            nameToEntry[fe.Name] = fe;
                        }
                        if (nameToUrl.Count > 0) break;
                        nameToUrl = null;
                    }
                    if (nameToUrl != null && nameToUrl.Count > 0) break;
                }

                if (nameToUrl == null || nameToUrl.Count == 0)
                {
                    Logger.LogFile("WuClient: ResolveUpdateOnlyFiles - no sku/ring combination produced file locations for " + updateId + " (" + buildNum + ", " + arch + ")");
                    return null;
                }

                int buildMajor = int.Parse(buildNum.Split('.')[0]);
                List<string> filtered = FilterUpdateOnly(nameToUrl.Keys, buildMajor);
                if (filtered.Count == 0)
                {
                    Logger.LogFile("WuClient: ResolveUpdateOnlyFiles - FilterUpdateOnly matched none of " +
                        nameToUrl.Count + " raw name(s): " + string.Join(", ", new List<string>(nameToUrl.Keys).ToArray()));
                    return null;
                }

                List<UupFile> result = new List<UupFile>();
                foreach (string name in filtered)
                {
                    result.Add(new UupFile {
                        Url = nameToUrl[name],
                        Filename = name,
                        Checksum = nameToEntry[name].Sha1,
                        Size = nameToEntry[name].Size,
                    });
                }
                return result;
            }
            catch (Exception ex)
            {
                Logger.LogFile("WuClient: ResolveUpdateOnlyFiles failed: " + ex.Message);
                return null;
            }
        }

        // GetExtendedUpdateInfo2 gives us URLs keyed by SHA1 with no filenames -
        // names/sizes/SHA256 only ever come from a SyncUpdates response's Files
        // list for the same UpdateID. Re-running SyncUpdates here (rather than
        // threading the original DiscoverResult through) keeps ResolveUpdateOnlyFiles
        // usable standalone against a build discovered via the legacy uupdump.net
        // scrape too, where no DiscoverResult exists at all.
        static Dictionary<string, FileEntry> FetchFileMetadata(string updateId, int rev, string arch,
            string checkBuild, string ring, int sku, Action<string> log)
        {
            string cookie;
            try { cookie = GetEncryptedCookie(log); }
            catch { return null; }

            string wantProduct = "Client.OS.rs2." + arch;
            string syncXml;
            try
            {
                syncXml = PostSoap(ClientUrl,
                    ComposeFetchUpdRequest(arch, ring, checkBuild, sku, "Production",
                        new List<string> { "thisonly" }, "auto", cookie), log);
            }
            catch { return null; }

            DiscoverResult r;
            try { r = ParseSyncUpdates(syncXml, wantProduct); }
            catch { return null; }

            if (r == null || !string.Equals(r.UpdateId, updateId, StringComparison.OrdinalIgnoreCase))
                return null;

            return r.Files;
        }
    }
}
