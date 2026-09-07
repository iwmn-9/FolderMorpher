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

### 1. 📊 Storage Explorer (TreeSize-Inspired Disk Space Analyzer)
![Tab 0: Storage Explorer](docs/screenshots/tab0_storage.png)
- **⚡ Ultra-Fast MFT Direct Scanning & Hybrid Fallback (Experimental)**: Direct binary parsing of NTFS Master File Table (`$MFT`) with non-exclusive read-only handles (`GENERIC_READ`). Indexes millions of files in seconds on local/server drives with automatic 3-tier fallback to parallel recursive scanning for network shares (UNC) and standard permissions.
- **Root-Relative Occupancy Meter (Anti-Double Counting)**: Percentage reflects absolute share of the scanned root, separated cleanly from subfolder shares.
- **Top 10 Largest Files & Explorer Highlight**: Instantly locate massive space-hogging files. Double-click to reveal and highlight directly in Windows Explorer.
- **Multi-Tab Simultaneous Scanning**: Scan and monitor multiple local drives and UNC network shares (`\\server\share`) side-by-side.

---

### 2. 🛡️ Live ACL Manager & SDDL Instant Rollback
![Tab 1: Live ACL](docs/screenshots/tab1_liveacl.png)
- **Direct NTFS Permission Control**: Drag-and-drop Active Directory accounts/groups, trash zone to delete, without opening sluggish RSAT/ADUC.
- **🔍 Reverse Effective Access Inspector (Multi-Level Nested AD Groups)**: Select any AD user or security group to reverse-scan entire file servers. Recursively resolves deeply nested AD group memberships (via `LDAP_MATCHING_RULE_IN_CHAIN` / tokenGroups) to uncover every folder the user can reach (Full Control, Modify, Read) with exact grant path tracing and one-click Excel compliance reporting.
- **Full Windows Security Modal Fidelity**: Real-time two-way sync between standard basic permissions (6 items) and advanced NTFS permission bits (14 granular items: Traversal, Extended Attributes, Ownership, etc.) supporting both Allow and Deny rules in Canonical ACL Ordering.
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
- **Headless Office Deep Inspection (`.xlsx`, `.xlsm`)**: Scans OpenXML packages and legacy BIFF streams without launching Office COM. Fixes broken external formula workbook links, worksheet references, and detects hardcoded VBA UNC paths with `~$` lock detection.
- **Domain-Wide GPO Logon Script Generator (`.ps1`)**: Outputs ready-to-deploy PowerShell logon scripts to fix client PCs automatically at next login.

---

### 5. 🧹 Audit & Hygiene (Enterprise Storage Audit)
![Tab 4: Audit & Hygiene](docs/screenshots/tab4_audit.png)
- **Deduplication & Storage Cleanup**:
  - **Duplicate Detection**: Size pre-filtering followed by cryptographic SHA256 verification.
  - **Dormant Files**: Identifies stale files unmodified for 3+ years (LastWriteTime basis).
  - **Path Limits (260+ Chars) & Invalid Character Audit**: Catches paths and illegal characters (`* : < > ? \ / | " # % { } ~ &`) that break Windows Explorer or Cloud migrations (SharePoint/Box).
- **Safety First**: Zero automated deletions. Generates audit inventory sheets (Excel/CSV) and safe staged archive scripts (`move /Y` to archive shares, with duplicate original protection guarantees).

---

### 6. 🖼️ Media Optimizer (Visually Lossless Image Slimmer & Video Top)
![Tab 5: Media Optimizer](docs/screenshots/tab5_media.png)
- **Sanctuary Protection (Auto-Skip)**: Automatically protects designated master folders (`_Master`, `_Original`, `RAW`) and creative professional extensions (`.psd`, `.ai`, `.raw`).
- **Visually Lossless Recompression**: Resizes 10MB+ camera snapshots to 2560px max dimension at 85% JPEG quality directly in-place, and lossless PNG recompression preserving 100% alpha transparency. **Reduces photo weight by ~90%** while preserving EXIF metadata, timestamps, and orientation.
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

## 🤖 AI-Assisted Maintenance & Issues Policy

This project is maintained through an engineering workflow combining human design governance and autonomous AI agents (Google DeepMind Antigravity / Gemini).

- **Issue Resolution**: Bug reports, edge cases, and feature requests submitted to [Issues](../../issues) are actively investigated, reproduced, and patched with AI agent assistance.
- **Reporting Tips**: To facilitate fast reproduction and accurate fixes by the agent, please include:
  1. Detailed reproduction steps or error logs.
  2. UI screenshots or error dialog details.
  3. Environment context (OS version, domain-joined vs. workgroup, target storage: local NTFS / SMB share / NAS etc.).
- **Pull Requests**: Community contributions and PRs are welcome and will be validated against our automated headless test suite.

---

## 📜 License & Attribution

This project is open-source software licensed under the **[MIT License](LICENSE)**.

- **Freedom of Use**: You are free to use, modify, merge, publish, distribute, and sell copies of the software for both personal and commercial purposes.
- **Attribution Required**: In accordance with the MIT License, any redistributed, modified, or derived work must preserve the original copyright notice and permission notice:
  ```text
  Copyright (c) 2026 iwmn-9
  ```

---

## 📄 Disclaimer

FolderMorpher is an administrative utility designed for IT and storage professionals. While all operations modifying the file system include safety mechanisms (such as SDDL rollback snapshots, `.bak` file generation, and staged archive isolation), the software is provided "AS IS", without warranty of any kind. Always ensure verified backups exist before executing batch operations on production file servers.

