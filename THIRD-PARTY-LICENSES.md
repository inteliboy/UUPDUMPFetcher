# Third-party components

This project's own source code is MIT-licensed (see `LICENSE`). It embeds or
talks to the following third-party components, which are **not** MIT and
retain their own licenses.

## wimlib / libwim-15.dll

- **Project**: [wimlib](https://github.com/ebiggers/wimlib) by Eric Biggers
- **What's used**: `assets\libwim-15.dll` (the `libwim` library specifically,
  not the full `wimlib` project/CLI tools), embedded as a managed resource
  and extracted to a temp folder at runtime. Called directly via P/Invoke
  (`WimLibApi.cs`) - no `wimlib-imagex.exe` or other wimlib program is
  shipped.
- **License**: dual-licensed by upstream. The full wimlib project (programs,
  scripts, docs) is GPLv3-or-later. `libwim` specifically may instead be used
  under LGPLv2.1-or-later, *except* when linked against the third-party
  `libntfs-3g` (which is GPL) - per wimlib's own `COPYING` file, Windows
  builds of `libwim` (what this project uses) don't link `libntfs-3g`, so the
  LGPLv2.1+ option applies here.
  Full text: [`COPYING`](https://github.com/ebiggers/wimlib/blob/master/COPYING),
  [`COPYING.LGPL`](https://github.com/ebiggers/wimlib/blob/master/COPYING.LGPL),
  [`COPYING.GPLv3`](https://github.com/ebiggers/wimlib/blob/master/COPYING.GPLv3).
- **Source availability**: wimlib's source is public at the link above, which
  is where `libwim-15.dll` was built from.

## cabinet.dll

- **What's used**: Windows' own built-in CAB decompression library
  (`C:\Windows\System32\cabinet.dll`), called directly via P/Invoke
  (`CabinetApi.cs`) for classic CAB-formatted `.msu` extraction. This is a
  standard Windows OS component, resolved off the system DLL search path by
  name - it is not bundled, embedded, or redistributed by this project in any
  way, so no separate license terms apply here beyond the Windows license the
  user already has by running Windows.

## uupdump.net API (reference/fallback)

- **Project**: [uup-dump/api](https://git.uupdump.net/uup-dump/api)
- **What's used**: `WuClient.cs` is a from-scratch C# reimplementation of the
  discovery/file-resolution logic in this project's `fetchupd.php`/`get.php`
  (protocol details, filter rules), written by reading their public source -
  no code from that project is copied into this repository. Separately,
  `Fetcher.cs` also calls the live `uupdump.net` website directly (scraping
  `known.php`/`get.php`) as a fallback path when Microsoft's own servers
  can't resolve a build. No files from that project are redistributed here.
- **License**: Apache License 2.0 (per the header of the real source files
  linked above).

## .NET Framework (build/runtime)

Building and running this project uses only the standard .NET Framework 4.8
runtime and reference assemblies already present on Windows (including
`System.Web.Extensions.dll` for JSON serialization). Nothing from the .NET
Framework is redistributed by this project.
