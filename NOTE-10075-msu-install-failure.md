# Note from Triquetra: 26100.10075 MSU fails to install (2026-11-22)

Source: Triquetra install log (`C:\ProgramData\triquetra\triquetra.log`) and `C:\Windows\Logs\DISM\dism.log` on the test machine (Windows 11 26300 host, 26100 servicing).

## Symptom
`Windows11.0-KB5132057-x64.msu` (build 26100.10075, from `updates.smce.pl/26100.10075/amd64/`) downloads and passes its SHA-256 check, but will not install:
- DISM API (`DismAddPackage`): rc `0x80070057`.
- `dism.exe /Add-Package`: expands to 63%, then `Error: 87`.
- The SSU cab (`SSU-26100.10068-x64.cab`) and the NDP cab are fine. The SSU installed with rc=0. Only the MSU fails.

## Cause found in dism.log
Both paths end with the same errors:
```
MsuManager: The MSU does not have all the packages. - CDismMsuManager::ProcessWithUpdateAgent(hr:0x80070057)
Failed to install UUP package. - CDismMsuPackage::DoInstall(hr:0x80070057)
Failed to apply the MSU unattend file to the image. - CDismMsuPackage::Install(hr:0x80070057)
```
Just before that, the update agent (UpdateAgent 10.0.26100.10075) builds a download list of 19 `WindowsWorkload.*_x64.msix` files (QueryProcessor, SemanticText, SessionManager, WinMLShared, ImageSearch, TextRecognition, SettingsModel, EP.Intel.OpenVINO.Framework, and others). These are the AI payloads that the MUMX v2 rebuild strips out of the MSU, but they are still referenced by the package metadata. The update agent then treats the MSU as incomplete and aborts.

Also seen: about 8100 DPX lines like `Extraction of file: amd64_curl_..._10.0.26100.10068_... \curl.exe failed because it is not present in the container (...\Windows11.0-KB5132057-x64.psf)`. Other examples are `srchadmin.dll` and `battc.sys`. Those files are not AI payloads, so the .psf container may be missing more than the `WindowsWorkload` packages. Not confirmed: the MSU could not be opened to list its contents (`tar` and `expand` both reject the format).

CBS.log shows nothing for the MSU. It fails before servicing starts.

## Not the cause
The download is fine. The SHA-256 matches and the accelerated-download check passed. The MSU is built this way.

## Suggested next steps
- Compare this MSU against the 26100.9807 one, which did install (confirmed on another machine). Find what changed in the rebuild or stripping step.
- Make the stripped MSU self-consistent: remove the `WindowsWorkload.*` entries from the manifests, update metadata and `express.psf.cix.xml` as well as the payload files. Alternatively keep those `.msix` files in the package.
- Check whether the `.psf` really lacks the 10068-era component files (curl.exe and others), or whether DPX only logs those at Info level and they are harmless.
- After rebuilding, re-test with `dism /Online /Add-Package` on a machine that is not yet at 10075.
