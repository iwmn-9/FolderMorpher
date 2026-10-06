# FolderMorpher

**Portable Windows file search, storage analysis, cleanup, and file server administration.**

Choose a reference folder, then switch between **Search**, **Storage**, and **Cleanup**. The collapsible administration sidebar adds NTFS permissions, migration planning, and link repair. Local folders and UNC shares are supported.

![Platform](https://img.shields.io/badge/Platform-Windows%20x64-blue.svg)
![Framework](https://img.shields.io/badge/.NET-10%20(Self--Contained)-purple.svg)
![License](https://img.shields.io/badge/Source%20License-MIT-green.svg)

[日本語](README_JA.md) · [Downloads](https://github.com/iwmn-9/FolderMorpher/releases) · [Issues](https://github.com/iwmn-9/FolderMorpher/issues)

## Start

1. Download `FolderMorpher.exe` from Releases and run it. Installation and a separate .NET runtime are unnecessary.
2. Select a reference folder. Add folders from the expanded selector and switch between them.
3. Use Search, Storage, or Cleanup. Language and other preferences are in **Settings**.

Settings, history, and metadata caches are written to the running computer. Closing the window normally leaves Host jobs running. Enable **Close Host when this window closes** in Settings to cancel jobs and shut down Host too.

### Requirements

- Windows x64 with a desktop: supported Windows 10/11 editions or Windows Server 2016 and later with Desktop Experience. See Microsoft's [.NET 10 supported OS list](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) for edition and servicing requirements.
- Search and standard storage scans work without administrator rights. Optional local NTFS MFT scanning is used only when the process already has the required privileges. Shares use the user's access permissions.
- Office is unnecessary for Open XML files (`.docx`, `.xlsx`, `.pptx`). PDF extraction uses official PdfPig, with Windows IFilter and built-in fallbacks. Optional PP-OCRv6-small for images/scanned PDFs is included in the EXE; no separate Python installation or model download is needed. Protected files may not yield searchable text.
- Video conversion batches require separately installed FFmpeg. FFmpeg is not bundled.

## Search

- Search names by default; enable **Search contents** or use `content:` for supported text, Office, and PDF contents. Include folders when needed.
- Filters include `ext:xlsx,docx`, `size:>100MB`, `dormant:>3y`, `pathlen:>240`, and `chars:illegal`, plus logical expressions and wildcards.
- While typing, memory or the local TreeCache previews the previous scan. Search, Enter, and Refresh scan the originals, then replace the final list with verified live hits. There is **no persistent full-text search database**.
- Direct traversal streams entries without keeping a second complete file list. Text scanning uses pooled buffers, an ASCII/UTF-8 fast path when applicable, and adaptive read sizes. Office reads text entries without expanding media assets.
- The same search button becomes **Stop** during execution. Clear cancels the job; an empty query does not restart it. Cached previews are provisional. Final counts include deduplicated name and content hits verified during the live scan.
- Total matches and retained rows are separate: the list retains up to 10,000 hits, while total count/bytes continue accumulating. OCR failures or limits appear in completion status.
- Elapsed time and approximate remaining time appear with the result counts. Estimates initially say “Estimating” and report overruns. Inaccessible folders and detected content-read failures are reported at completion.

## Storage

- Browse a size tree showing each item's share of the scanned root. Expansion retrieves one level of children instead of sending the entire tree to the GUI.
- Standard local scans use batched enumeration and up to eight workers. UNC scans retain the shared enumeration limit of two. Metadata comes from enumeration instead of separate per-file queries.
- Inaccessible branches remain unknown, never empty. Partial scans are labeled and retained locally without replacing complete history/cache; exports include unavailable scope.
- Load previous metadata before a fresh scan and compare growth/reduction. Cached values are previous observations, not a guarantee that the source is unchanged.
- Open **Details** for largest files and the selected folder's breakdown. History graphs are a separate action, keeping the main tree wide by default.
- A low-space warning appears when the current user has at most 10% available at the selected location. Individual server folder quotas are not always exposed by Windows' free-space API.
- SQLite TreeCache stays at `%LocalAppData%\FolderMorpher\TreeCache\tree_cache.db`. Parent IDs and names avoid repeated full paths. Shared/custom destinations use portable JSON trees and history, not shared SQLite.
- Complete initial live search or cleanup traversal can populate an absent TreeCache. Canceled, inaccessible, incomplete, or excluded traversals do not publish that new cache.

## Cleanup

- Duplicate, dormant, old-version, extracted-ZIP, and path-risk reasons combine into one row per physical file. **Scores add without a 100-point cap**; the breakdown explains priority, not a probability of safe deletion.
- Duplicate checks narrow by size and partial fingerprints, then verify survivors with **full SHA-256**. Stored hashes do not replace full verification on the next audit.
- Large candidate groups are checked first; provisional results arrive while scanning and Stop remains available. Deletion and exports wait for the final report.
- Original candidates are protected. Permanent deletion is separate, and duplicate hashes are rechecked immediately before deleting.
- Read concurrency and UNC hash bandwidth are controlled automatically.
- **Media optimization** is inside Cleanup: protected folders, image resizing/compression, large-video discovery, and FFmpeg batch generation. Compression can be lossy; review the plan before overwriting.

## Administration

| Tool | Purpose |
| --- | --- |
| Live ACL / reverse access audit | Inspect NTFS rules, AD group paths, and effective access; preview changes, preserve SDDL, apply and verify |
| Migration studio | Design virtual trees and N:1 mappings, compare permissions, deploy skeletons, export wave/runbook/Robocopy packages |
| Link repair | Inspect/repair `.lnk` and Excel external links with backups; preserve VBA binaries and report partial repairs |

Observation starts directly. Changes use a common plan for **Check → Apply → Verify**. Access permissions and protection checks apply. Maintain backups for production changes.

## Performance and implementation

The stack is **C# 14 / .NET 10 / WPF**. One distributed `FolderMorpher.exe` runs the GUI normally and a separate Host process with `--host`. Source dependencies are `UI → Contracts ← Host → Core`; Named Pipes are limited to the current user and session.

In an ordinary-rights C-drive capacity experiment without MFT or TreeCache reuse, standard scanning's median wall time fell from **33.070 s to 13.518 s**. Database saving was measured separately, and Windows' file cache was not cleared. This describes that machine and workload, not an UNC guarantee. Search, duplicate, and storage comparisons, conditions, and adopted techniques are in the [performance research record](.agents/PERFORMANCE_RESEARCH.md).

External comparison tools are not bundled. Ideas from ripgrep-all, Czkawka, and capacity tools are independently implemented; their source code was not copied into FolderMorpher.

### Build and verify

Use the .NET 10 SDK on Windows:

```powershell
dotnet build -c Release
./tools/Test-LicenseNotices.ps1
dotnet run -c Release --no-build -- --test-regression
dotnet publish ./FolderMorpher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None
./tools/Test-LicenseNotices.ps1 -DepsPath ./bin/Release/net10.0-windows/win-x64/FolderMorpher.deps.json
./dist/FolderMorpher.exe --test-ipc
```

`dist/FolderMorpher.exe` is the distribution output. The regression suite covers eight domains; this does not imply exhaustive coverage. The published EXE's integration test starts a separate Host with isolated data. Maintenance guidance is in [AGENTS.md](AGENTS.md), and decisions are in [ADR.md](.agents/ADR.md).

## Licenses

FolderMorpher source is **[MIT](LICENSE)**, copyright © 2026 iwmn-9. Bundled libraries and .NET retain their own licenses. Full texts, copyrights, and upstream notices are in **[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)** and embedded in the EXE under **Settings → Licenses**.

The [dependency review](docs/DEPENDENCIES.md) records MIT/Apache 2.0 dependencies, exact-version review, optional external tools, and the CI check for missing notices. Software is provided as-is under its licenses.

## v1.0.1 review corrections

See [implementation and verification results](docs/REVIEW_FIXES.md). Migration packages use a resolved JSON transfer plan and expected **union** of all sources, rather than sequential source mirrors. CUTOVER checks collisions before copying, verifies source stamps and destination type/size, then removes only planned extra entries. This is not full content-hash verification. Source writes must be frozen during cutover. Generated PowerShell respects the organization's execution policy; it does not bypass it. ACL identity is SID-based across selection, IPC, planning and application. Office repair edits structured external links/formulas and verifies XML before replacing the original.
