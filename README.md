# 🚀 FolderMorpher — Enterprise IT Storage, Audit & Migration Studio

> **Complete Windows file server storage analysis, NTFS permission auditing, and migration restructuring in a single portable tool.**  
> An all-in-one, zero-dependency, portable Windows GUI studio built for SysAdmins and IT teams. Handles disk space monitoring with 0-second cache, virtual tree redesign (N:1 mapping), live NTFS permissions with SDDL rollback, broken shortcut/Office link fixes, image optimization with sanctuary guards, and executive Excel audit reporting.

![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20%2F%20Server-blue.svg)
![Framework](https://img.shields.io/badge/.NET-8.0%20(Self--Contained)-purple.svg)
![UI](https://img.shields.io/badge/UI-Modern%20Fluent%20Design-0ea5e9.svg)
![Architecture](https://img.shields.io/badge/Philosophy-Plan%20First%20%26%203--Stage%20Rocket-emerald.svg)

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

## 🛡️ Core Operational Philosophy: Two Worlds & 3-Stage Rocket

FolderMorpher enforces a strict operational dualism designed to keep SysAdmins fast during exploration and rigorously safe during state mutations:

| Category | Targeted Actions | UX Behavior |
| :--- | :--- | :--- |
| **Observation & Generation**<br>*(Read-Only & Script Export)* | • Storage scanning & 0s tree navigation<br>• AD reverse effective access auditing<br>• Dormant & duplicate file inventory<br>• Executive Excel / CSV ledger export<br>• Archive script (.bat) & GPO script (.ps1) export | **Instant Execution**<br>(No blocking dialogs or dry-run steps — zero friction during administrative exploration) |
| **Mutation & Intervention**<br>*(State-Altering Disk Writes)* | • **Live ACL Direct Commit**<br>• **Skeleton Tree Deployment**<br>• **Direct Shortcut In-Place Repair**<br>• **Photo Overwrite Optimization** | **Plan First (3-Stage Rocket)**<br>`[ Check ]` ➔ Modal **`⚖️ Changes`** (Before/After preview) ➔ `[ Apply ]` ➔ Automatic Semantic **Verify** |

---

## 🌟 6 Core Pillars

### 1. 📊 Storage Explorer (File Server Capacity & 0-Second Tree Cache)
![Tab 0: Storage Explorer](docs/screenshots/tab0_storage.png)
- **⚡ 0-Second Tree Cache & Background Auto-Diff**: Instantly restores previous directory hierarchies in 0 seconds from local/shared cache while running background network scans. Automatically tags changed folders with growth/reduction badges (`▲ +2.4GB` / `▼ -500MB`).
- **⚡ Ultra-Fast MFT Direct Scanning & Hybrid Fallback**: Direct binary parsing of NTFS Master File Table (`$MFT`) with non-exclusive read-only handles (`GENERIC_READ`). Indexes millions of files in seconds on local/server drives with automatic 3-tier fallback to parallel recursive scanning for network shares (UNC).
- **Root-Relative Occupancy Meter (Anti-Double Counting)**: Percentage reflects absolute share of the scanned root, separated cleanly from subfolder shares.
- **Top 10 Largest Files & Explorer Highlight**: Instantly locate massive space-hogging files. Double-click to reveal and highlight directly in Windows Explorer.
- **Multi-Tab Simultaneous Scanning**: Scan and monitor multiple local drives and UNC network shares (`\\server\share`) side-by-side.

---

### 2. 🛡️ Live ACL Studio (NTFS Control & Reverse Effective Access)
![Tab 1: Live ACL Studio](docs/screenshots/tab1_liveacl.png)
- **`AclChangePlan` Pipeline**: Unified single-instance plan from dry-run preview to commit. Eliminates discrepancies between preview and actual execution.
- **2-Row Modern List Card UI**: Clean vertical cards displaying Account Name on row 1 with full width, and Identity, Rights, AppliesTo, and 🔒Inherited badges on row 2. Drag accounts from the AD palette, or drag out-of-bounds to safely discard.
- **Inheritance Transition Warning Banner**: Prominently warns when disabling inheritance, displaying the exact count of inherited ACEs being promoted to explicit entries to prevent unintended access loss.
- **True Semantic Verification (Verify)**: Post-commit, re-reads native NTFS ACL from the OS kernel and verifies 100% semantic equivalence against expected ACE rules.
- **🔍 Reverse Effective Access Inspector (Canonical DACL & Multi-Level Nested AD Groups)**: Resolves deep AD security group nesting (via `LDAP_MATCHING_RULE_IN_CHAIN` / tokenGroups) without identity contamination. Accurately evaluates Windows Canonical DACL Ordering (Explicit Allow takes precedence over Inherited Deny) with exact grant path tracing and one-click Excel audit reporting.
- **SDDL Instant Snapshot & 1-Click Rollback**: Backs up native DACL SDDL prior to commit for instant atomic recovery.

---

### 3. 🚀 Simulation Studio (Virtual Tree Design & Skeleton Deployment)
![Tab 2: Simulation Studio](docs/screenshots/tab2_simulation.png)
- **Virtual Tree Design & N:1 Folder Consolidation**: Map legacy departmental structures (e.g. `Sales_East` + `Sales_West` ➔ `Sales_Dept`) visually via drag-and-drop with recursive ACL inheritance.
- **Skeleton Deploy (Pre-Creation)**: Pre-deploy empty folder hierarchies with designed NTFS ACLs *before* kicking off massive data copy jobs via the **`⚖️ Changes`** review modal.
- **Visual Diff Inspector**: Review New, Moved, Merged, and Permission Deltas with one-click ClosedXML export.
- **Isolated Robocopy Modes**: Choose between "Preserve Designed ACLs Mode (`/COPY:DAT`)" or "Preserve Legacy ACLs Mode (`/COPYALL`)".

---

### 4. 🔗 LinkFixer (Broken Shortcuts & Office Link Repair)
![Tab 3: LinkFixer](docs/screenshots/tab3_linkfix.png)
- **Batch Shortcut (`.lnk`) In-Place Healing**: Review targeted shortcuts and old-to-new path replacements in the **`⚖️ Changes`** modal, then batch-rewrite with automatic `.bak` safety backups.
- **Headless Office Deep Inspection (`.xlsx`, `.xlsm`)**: Scans OpenXML packages and legacy BIFF streams without launching Office COM. Fixes broken external formula workbook links, worksheet references, and detects hardcoded VBA UNC paths with `~$` lock detection.
- **Domain-Wide GPO Logon Script Generator (`.ps1`)**: Outputs ready-to-deploy PowerShell logon scripts to fix client PCs automatically at next login.

---

### 5. 🧹 Audit & Hygiene (Deduplication & Storage Governance)
![Tab 4: Audit & Hygiene](docs/screenshots/tab4_audit.png)
- **Cryptographic Duplicate Detection**: Size pre-filtering followed by SHA256 cryptographic verification.
- **Dormant Files**: Identifies stale files unmodified for 3+ years (LastWriteTime basis).
- **Path Limits (260+ Chars) & Invalid Character Audit**: Catches paths and illegal characters (`* : < > ? \ / | " # % { } ~ &`) that break Windows Explorer or Cloud migrations (SharePoint/Box).
- **Smart Selection & Original Candidate Sanctum**: Safely select duplicate copies while strictly protecting original candidate files (`IsOriginalCandidate`).
- **Safety First**: Zero automated silent deletions. Generates audit inventory sheets (Excel/CSV), safe staged archive scripts (`move /Y` to archive shares), and guarded physical deletion with original file warnings.

---

### 6. 🖼️ Media Optimizer (In-Place Image Slimmer & GPU Video Batch)
![Tab 5: Media Optimizer](docs/screenshots/tab5_media.png)
- **Sanctuary Protection (Auto-Skip)**: Automatically protects designated master folders (`_Master`, `_Original`, `RAW`, `印刷用`) and professional formats (`.psd`, `.ai`, `.raw`).
- **In-Place Image Optimization**: Resizes 10MB+ camera snapshots to 2560px max dimension at 85% JPEG quality directly in-place, and optimizes PNGs while preserving 100% alpha transparency. **Reduces photo weight by up to 90%** while preserving EXIF metadata, timestamps, and orientation.
- **Dry-Run Changes Inspection**: Review affected files, settings, and sanctuary exclusions in the **`⚖️ Changes`** modal before commit.
- **Giant Video Ranker & Nightly GPU Batch**: Extracts multi-gigabyte video files and generates GPU-accelerated (NVENC/QSV H.265) overnight encoding batch scripts.

---

### 7. 📊 Executive Excel Reporting
- Zero dependency on Microsoft Office installation (powered by ClosedXML).
- Formatted executive summary cards, KPI highlights, auto-filters, and clean borderless typography.
- **Embedded `file:///` hyperlinked paths**: Clicking any path in Excel immediately opens the target parent folder in Windows Explorer.

---

## 🚀 Download & Quick Start

### Requirements
- OS: Windows 10 / Windows 11 / Windows Server 2016+ (64-bit)
- **Zero prerequisites**: No .NET runtime installation required (100% self-contained single executable).

### Instructions
1. Download the latest single executable (`FolderMorpher.exe`) from [**Releases**](../../releases).
2. Double-click `FolderMorpher.exe` to launch. No installer, no registry pollution.

---

## 🛠️ Tech Stack & Architectural Guarantees

- **Language & Runtime**: C# 12 / .NET 8.0 Windows Desktop SDK (Self-Contained single file)
- **UI Framework**: WPF (Windows Presentation Foundation) / Fluent Light Design Architecture
- **Directory Services**: `System.DirectoryServices` (LDAP / Direct ADSI queries)
- **Security Interop**: `System.Security.AccessControl` (Native NTFS ACL / SDDL)
- **Office Engine**: `ClosedXML` (Direct OpenXML packaging) & Headless ZipArchive
- **Imaging Engine**: WIC (Windows Imaging Component / Hardware Accelerated)
- **Headless Quality Assurance**: 28 automated regression tests (`--test-regression`) gating every release.
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

FolderMorpher is an administrative utility designed for IT and storage professionals. All operations modifying the file system strictly enforce the Plan-First 3-Stage Rocket protocol (interactive `⚖️ Changes` review modals, SDDL rollback snapshots, `.bak` backups, and original file preservation guards). The software is provided "AS IS", without warranty of any kind. Always ensure verified backups exist before executing batch operations on production file servers.
