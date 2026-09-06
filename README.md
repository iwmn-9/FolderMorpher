# 🚀 FolderMorpher — Enterprise IT Storage, Audit & Migration Studio

> **"Replace multi-thousand-dollar enterprise suites." "Never dread file server migrations or departmental restructures again."**  
> An all-in-one, zero-dependency, portable Windows GUI studio built for SysAdmins and IT teams. Handles disk space monitoring, virtual tree redesign (N:1 mapping), live NTFS permissions with SDDL rollback, broken shortcut/Office macro path fixes, 90% image lossless slimming, and executive Excel audit reporting.

![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20%2F%20Server-blue.svg)
![Framework](https://img.shields.io/badge/.NET-8.0%20(Self--Contained)-purple.svg)
![UI](https://img.shields.io/badge/UI-Fluent%20Light%20Design-0ea5e9.svg)
![Architecture](https://img.shields.io/badge/License-Free%20Preview-emerald.svg)

🌐 **Language**: **[🇺🇸 English]** | [🇯🇵 日本語 (README_JA.md)](README_JA.md)

---

## 📢 Public Preview & Community Feedback Wanted!

FolderMorpher is built from the trenches of real-world enterprise IT operations.

We actively welcome real-world test reports and edge cases from IT admins:
- *"Tested on our NetApp / Windows Server 2022 / Synology / QNAP NAS and here are the results"*
- *"Encountered a specific legacy Excel formula or VBA macro pattern that failed to parse"*
- *"Feature request that would make this an even bigger lifesaver during migrations"*

Please submit your feedback or bug reports via [GitHub Issues](../../issues)!

---

## 🌟 6 Core Pillars

### 1. 📊 Storage Explorer (Fast Disk Space Analyzer)
![Tab 0: Storage Explorer](docs/screenshots/tab0_storage.png)
- **Root-Relative Occupancy Meter (Anti-Double Counting)**: Percentage reflects absolute share of the scanned root, separated cleanly from subfolder shares.
- **Top 10 Largest Files & Explorer Highlight**: Instantly locate massive space-hogging files. Double-click to reveal and highlight directly in Windows Explorer.
- **Multi-Tab Simultaneous Scanning**: Scan and monitor multiple local drives and UNC network shares (`\\server\share`) side-by-side.

---

### 2. 🛡️ Live ACL Manager & SDDL Instant Rollback
![Tab 1: Live ACL](docs/screenshots/tab1_liveacl.png)
- **Direct NTFS Permission Control**: Drag-and-drop Active Directory accounts/groups, trash zone to delete, without opening sluggish RSAT/ADUC.
- **Full Windows Security Modal Fidelity**: Real-time two-way sync between standard basic permissions (6 items) and advanced NTFS permission bits (14 granular items: Traversal, Extended Attributes, Ownership, etc.).
- **Atomic Pre-Apply SDDL Snapshot & 1-Click Rollback**: Automatically backs up full SDDL string prior to commit. Instant one-click rollback if accidental lockouts occur.
- **Access Permission Matrix CSV Export**: Generate compliance-ready audit tables in one click.

---

### 3. 🚀 Migration Simulation Studio (Skeleton Deployment)
![Tab 2: Simulation Studio](docs/screenshots/tab2_simulation.png)
- **Virtual Tree Design & N:1 Folder Consolidation**: Map legacy departmental structures (e.g. `Sales_East` + `Sales_West` ➔ `Sales_Dept`) visually via drag-and-drop.
- **Skeleton Deploy (Gawa Pre-Creation)**: Pre-deploy the target folder hierarchy with designed NTFS ACLs *before* kicking off massive data copy jobs.
- **Visual Diff Inspector**: Color-coded badges for New, Moved, Merged, and Permission Deltas with Excel export.
- **Optimized Robocopy Batch Generation**: Automatically outputs high-throughput multi-threaded scripts (`/MT:16 /COPYALL`).

---

### 4. 🔗 LinkFixer (Broken Shortcuts & Office Macro Repair)
![Tab 3: LinkFixer](docs/screenshots/tab3_linkfix.png)
- **Mass Broken Shortcut (`.lnk`) Healing**: Repaths disconnected UNC targets to the new server across desktops, recent items, and start menus with `.bak` safety backups.
- **Headless Office Deep Inspection (`.xlsx`, `.xlsm`)**: Scans OpenXML packages and legacy BIFF streams without launching Office COM. Fixes broken external formula workbook links and hardcoded VBA UNC paths with `~$` lock detection.
- **Domain-Wide GPO Logon Script Generator (`.ps1`)**: Outputs ready-to-deploy PowerShell logon scripts to fix client PCs automatically at next login.

---

### 5. 🧹 Audit & Hygiene (Complete GDMS Alternative)
![Tab 4: Audit & Hygiene](docs/screenshots/tab4_audit.png)
- **Deduplication & Storage Cleanup**:
  - **Duplicate Detection**: Size pre-filtering followed by cryptographic SHA256 verification.
  - **Dormant Files**: Identifies stale files unaccessed/unmodified for 3+ years.
  - **Path Limits (260+ Chars) & Invalid Character Audit**: Catches paths and illegal characters (`* : < > ? \ / | " # % { } ~ &`) that break Windows Explorer or Cloud migrations (SharePoint/Box).
- **Safety First**: Zero automated deletions. Generates audit inventory sheets (Excel/CSV) and safe staged archive scripts (`move /Y` to archive shares).

---

### 6. 🖼️ Media Optimizer (Lossless Image Slimmer & Video Top)
![Tab 5: Media Optimizer](docs/screenshots/tab5_media.png)
- **Sanctuary Protection (Auto-Skip)**: Automatically protects designated master folders (`_Master`, `_Original`, `RAW`) and creative professional extensions (`.psd`, `.ai`, `.raw`).
- **Visual Lossless Recompression**: Resizes 10MB+ smartphone camera snapshots to 2560px max dimension at 85% JPEG quality directly in-place. **Reduces photo weight by ~90%** while preserving 100% of EXIF, timestamps, and orientation.
- **Giant Video Ranker & Nightly GPU Batch**: Extracts multi-gigabyte video files and generates GPU-accelerated (NVENC/QSV H.265) overnight encoding batch scripts.

---

### 7. 📊 Executive Excel Reporting
- Zero dependency on Microsoft Office installation (powered by ClosedXML).
- Formatted executive summary cards, KPI highlights, auto-filters, and clean typography.
- **Embedded `file:///` hyperlinked paths**: Clicking any path in Excel immediately opens the target parent folder in Windows Explorer.

---

## 🚀 Download & Quick Start

### Requirements
- OS: Windows 10 / Windows 11 / Windows Server 2016+ (64-bit)
- **Zero prerequisites**: No .NET runtime installation required (100% self-contained single executable).

### Instructions
1. Download the single executable [**`FolderMorpher.exe`**](FolderMorpher.exe) from the repository root (or [Releases](../../releases)).
2. Double-click `FolderMorpher.exe` to launch. No installer, no registry pollution.

---

## 🛠️ Tech Stack & Architectural Guarantees

- **Language & Runtime**: C# 12 / .NET 8.0 Windows Desktop SDK (Self-Contained single file)
- **UI Framework**: WPF (Windows Presentation Foundation) / Fluent Design Architecture
- **Directory Services**: `System.DirectoryServices` (LDAP / Direct ADSI queries)
- **Security Interop**: `System.Security.AccessControl` (Native NTFS ACL / SDDL)
- **Office Engine**: `ClosedXML` (Direct OpenXML packaging) & Headless ZipArchive
- **Imaging Engine**: WIC (Windows Imaging Component / Hardware Accelerated)
- **Engineering Guidelines**: Strictly documented in [`AGENTS.md`](AGENTS.md) preserving decoupled service layers and ADR principles.

---

## 📄 Disclaimer

FolderMorpher is an administrative utility designed for IT professionals. While all file-touching features include automatic backups (`.bak`, SDDL snapshots, staged archive moves), always ensure verified file server backups exist before executing batch operations on production volumes.
