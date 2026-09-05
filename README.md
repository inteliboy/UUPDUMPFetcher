# UUP Dump Fetcher

A Windows desktop tool that downloads Windows 11 Insider/servicing update
components (UUP files) directly from Microsoft's own Windows Update servers
and assembles them into a bootable `.esd` image - no manual UUP-to-ISO
conversion steps, and no dependency on a third-party site for the actual
download in the common case.

It talks to Microsoft's Windows Update SOAP endpoints the same way
[uupdump.net](https://uupdump.net)'s own backend does, and falls back to
scraping uupdump.net only if the direct path fails (e.g. a build old enough
that Microsoft has aged it off live serving).

![.NET Framework 4.8](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4)
![Windows](https://img.shields.io/badge/platform-Windows%20x64-0078D4)

## Features

- **Direct-from-Microsoft discovery and download** - no browser, no
  third-party API key, just this tool talking to `fe3.delivery.mp.microsoft.com`
  directly.
- **Automatic branch scanning** for Windows 11 26H2, 26H1 and 23H2, plus
  on-demand fetching of any exact build number (including older/manual-only
  branches like 24H2).
- **Always finds the true latest build**, not just whatever one Windows
  edition happens to still be serviced for - some branches (23H2 is a real
  example) reach end-of-servicing for Home/Pro editions well before
  Enterprise/Education/IoT Enterprise, so this tool checks multiple editions
  and keeps whichever is actually newest.
- **Downloads only what's needed to build the image** - Microsoft's update
  metadata offers several files (extra cumulative-update cabs, aggregated
  metadata, deployment cabs) that aren't actually required to produce the
  final ESD; this tool trims those out automatically per branch shape.
- **Builds the ESD entirely in-process** - WIM/MSU extraction and ESD
  capture go through direct P/Invoke bindings to `wimlib` and Windows' own
  `cabinet.dll`, with real byte-level progress reporting. No subprocesses,
  no `7z.exe`/`wimlib-imagex.exe` shipped or required.
- **PSF (patch storage file) application** to turn the express/delta payload
  into a complete file tree before capture.
- **Optional network upload** of the finished ESD (plus SSU/`.NET Framework`
  cabs) to a destination folder/share, with optional cleanup of the raw
  download afterward.
- **Monitor mode** - re-scans every 5 minutes and only downloads/builds
  builds it hasn't completed yet.
- **Downloaded Builds viewer** - a table of every completed build/arch with
  every output file's MD5 and SHA256, a button to remove an entry (forcing a
  rebuild on the next scan without deleting anything from disk), and a
  button to re-trigger just the upload step for an already-processed build.
- **Dark-mode aware UI** that follows the Windows "Apps use light/dark theme"
  setting, with no native control left in its ugly default light-mode style.

## Requirements

**To run:** Windows 10/11, x64. .NET Framework 4.8 (already present on any
up-to-date Windows 10/11 install).

**To build:** the .NET Framework 4.x developer pack (for `csc.exe` at
`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`). No other SDK,
NuGet package, or build tool is required - `build.cmd` is a single `csc.exe`
invocation compiling all the `.cs` files in this folder into `uupdump.exe`.

## Building

```
build.cmd
```

This compiles `uupdump.exe` directly into the project folder. The assembly
version auto-increments (the revision/last number) on every successful
build, starting from `2.0.0.0` - the current version is tracked in
`version.txt`.

Required files in `assets\`:

- `app.ico` - the application icon
- `libwim-15.dll` - [wimlib](https://github.com/ebiggers/wimlib), embedded as
  a resource and extracted to a temp folder at runtime; not needed as a
  separate file alongside `uupdump.exe`

## Usage

1. Run `uupdump.exe`.
2. On the **Fetch** tab, either leave the build field empty to scan every
   enabled branch for its latest build, or type an exact build number (e.g.
   `26100.7092`) to fetch that specific one - optionally restricting to one
   architecture.
3. Check **Process update to ESD** to have the tool assemble a bootable
   `.esd` after downloading (instead of leaving the raw UUP files as-is).
4. Click **Start Fetching**. Progress and a running log are shown on the
   right; **Stop** aborts cooperatively at the next safe point.
5. On the **Settings** tab: pick which branches get scanned automatically,
   set an upload destination (a local folder or UNC share) if you want
   finished builds copied somewhere, and configure cleanup/monitor-mode
   behavior.
6. **View Downloaded Builds** shows every build/arch this tool has completed,
   with per-file checksums, a way to force a rebuild, and a way to
   re-trigger just the upload for one entry.

Completed builds are tracked in `builds.json` inside the download folder;
deleting or editing that file does not touch any files already downloaded.

## How it works

- `WuClient.cs` implements the real Windows Update SOAP protocol
  (`GetCookie` → `SyncUpdates` → `GetExtendedUpdateInfo2`) from scratch,
  matching how uupdump.net's own open-source backend
  (`git.uupdump.net/uup-dump/api`) talks to Microsoft.
- `Fetcher.cs` orchestrates discovery, dedup, per-branch file-list trimming,
  and the download loop, falling back to scraping uupdump.net only when the
  direct path fails.
- `WimLibApi.cs`/`CabinetApi.cs` P/Invoke `wimlib` and `cabinet.dll` directly
  for WIM/MSU extraction and ESD capture - no bundled `7z.exe` or
  `wimlib-imagex.exe`.
- `Processor.cs` drives the extract → PSF-patch → capture → checksum →
  upload pipeline for a downloaded build.
- `MainForm.cs`/`Theme.cs`/`Dialogs.cs` are the WinForms UI layer.

See `CLAUDE.md` in this repository for detailed, dated notes on specific
protocol findings, bug fixes, and design decisions made during development.

## Credits

- [uupdump.net](https://uupdump.net) and its
  [open-source backend](https://git.uupdump.net/uup-dump/api), whose
  discovery/filtering logic this tool's direct Microsoft client is ported
  from, and which remains a fallback source when Microsoft's live servers
  can't resolve an older build.
- [wimlib](https://github.com/ebiggers/wimlib) for WIM/ESD handling.

See [THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md) for the license terms
each of these components is actually used under.

## License

This project's own source code is licensed under the [MIT License](LICENSE).

Third-party components embedded in or used by this project (notably
`wimlib`, dual-licensed GPLv3+/LGPLv2.1+) retain their own licenses - see
[THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md) for details.
