# Live Photo Box Changelog

> Current version: **v2.1.5**
> Project started: 2026-03-27
> Tech stack: WinUI 3 + .NET 9 + C# 13

---

## v2.1.x — Multi-Brand Compatibility, Page Redesign & CLI

> **v2.1.5** (2026-08-13) **[Feature]** About page CLI manual dialog, one-click CLI PATH add/remove scripts, Scoop install-channel detection (with GUI+CLI / GUI-only marker); **[Optimize]** CLI update reliability — checks & downloads auto-retry up to 3×, HTTP range resume + live progress bar + SHA256 verification, Unicode-safe background updater, `lpb info` shows install directory, unified update-check status line, CLI aliases trimmed to 4 (removed lipbox / lpbx); **[Fix]** update check mislabeling release builds as previews (4-part vs 3-part version comparison)
>
> **v2.1.4** (2026-08-12) **[Feature]** CLI adds `info`/`--version` environment info, `--key-timestamp` for custom key photo position, and a manual update mode (install-specific guidance, WinGet installs directed to winget, pending listing); **[Optimize]** colorized CLI output; **[Fix]** temp-file collisions in parallel batch merges
>
> **v2.1.3** (2026-08-08) **[Fix]** Corrupted HUAWEI live photo video export fixed; CLI output-default & leftover-folder fixes — single-pair writes next to the photo, batch writes into a `{folder}_<protocol>` subfolder, never the terminal's cwd, `-w` overwrites in place, `--dry-run` creates no folders; **[Optimize]** Edit-page export pipeline improved — Samsung HEIC cover replacement, OPPO pure-video extraction, single-file still-frame export (testing pending); vivo X300 merging adjusted for better vivo Gallery compatibility (still testing)
>
> **v2.1.2** (2026-08-04) **[Feature]** CLI merge supports positional file args with image/video auto-detection, plus --all-variants to generate every protocol×format combo; **[Redesign]** About page restructured — Store page / changelog / check-update entries, reworked update window; smaller installer (samples no longer bundled); protocol settings simplified (HUAWEI unified on v6_f); **[Fix]** UI/features following system language after in-app switch, corrected output format option label on the Merge page
>
> **v2.1.1** (2026-08-03) **[Feature]** **Brand-new CLI mode** — `livephotobox` command-line tool: merge/protocols commands, six aliases (lpb etc.), single-pair + batch, naming templates, AI/script automation, 100% shared core with GUI; **[Feature]** HUAWEI/Honor Moving Photo playback & cover replacement, HEIC+H.265 (HEVC) output with lossless HEVC passthrough, HUAWEI HarmonyOS 4.0 V3 protocol; **[Refactor]** Project split into shared Core + GUI + CLI; **[Fix]** HUAWEI JPEG/HEIC merging, unified 3-part version display
>
> **v2.1.0** (2026-07-30) **[Redesign]** Merge page epic redesign — left settings + right queue layout, protocol picker, format filtering, custom naming, source file management; **[Feature]** New vivo/Samsung/HUAWEI live photo support, Fusion Mode works across brands + Windows Photos; Edit page supports all protocols, OPPO dual cover modes, drag-and-drop pairing setting; About page redesigned; **[Fix]** Window title, feedback button link

## v2.0.x — LivePhotoEdit Page & Toolchain Rewrite

> **v2.0.2** (2026-07-23) **[Feature]** Multi-format export (JPEG/PNG/WebP), video/GIF export; export progress overhaul — clear per-step labels, persistent checkmark, one-click open output folder, red X + error bubble on failure; LIVE badge now fixed blue; **[Optimize]** File browser redesign, live photo detection improvements, visual refresh, export all frames inline instead of popup, unified three-state progress panel (spinner/success/error); **[Fix]** Alt+Tab window title, drag overlay overlap, export menu gray-out logic, error popups replaced with inline display
>
> **v2.0.1** (2026-07-21) **[Performance]** Edit page performance significantly improved — instant click response, no crash on rapid switching, faster HEIC thumbnails, timeline VRAM drastically reduced; refresh button smart-switches to clear button
>
> **v2.0.0** (2026-07-18) **[Major Release]** New LivePhotoEdit page — timeline, export frames, replace cover, view properties; folder drag-and-drop on all pages; timeline mode setting; enhanced log viewer; fixed packaging tools missing & language switching bug

## v1.15.x — Auto Update & Pairing Enhancement

> **v1.15.3** (2026-07-03) **[Fix]** Empty queue hint, settings scroll-to-section, startup animation removal, empty queue UX
>
> **v1.15.2** (2026-07-03) **[New]** Repair options dialog, lightbox overhaul, installer size 80% smaller, skipped items detailed reasons, scroll-to-top button
>
> **v1.15.1** (2026-07-02) **[Fix]** Unpackaged-mode bugs, auto-update UX, startup time reduced from 4-5s to 1-2s
>
> **v1.15.0** (2026-07-01) **[Release]** Auto-update, metadata matching, Apple-only repair filter, external tool checker

## v1.14.x — Release Fixes

This release encountered MSIX packaging issues, external tool path errors, oversized installer, background toggle not rendering, and unpackaged mode failures — resolved over 10 iterations.

> **v1.14.10** (2026-06-29) **[Release]** Official release — all issues resolved, version finalized
>
> v1.14.9 (2026-06-28) **[Fix]** MSIX packaging fix, Release Notes finalized, build tool updates
>
> v1.14.8 (2026-06-28) **[Tooling]** CI/CD auto build & release — GitHub Actions + Inno Setup installer
>
> v1.14.7 (2026-06-28) **[Docs]** Bilingual README, GitHub SVG badges, project screenshots
>
> v1.14.6 (2026-06-27) **[New]** Unpackaged mode support — bootstrap script + sideload packaging
>
> v1.14.5 (2026-06-27) **[Stability]** CrashHandler + ViewModels update, stability improvements
>
> v1.14.4 (2026-06-27) **[Optimize]** **Installer size reduced significantly** — ffmpeg full→essentials (308→156 MB), removed jhead
>
> v1.14.3 (2026-06-27) **[Fix]** ExternalToolLocator rewrite, tool detection path fix
>
> v1.14.2 (2026-06-27) **[Fix]** Settings page background toggle Acrylic theme rendering fix
>
> v1.14.1 (2026-06-26) **[Fix]** External tool path fix, LivePhotoSplitService path unification
>
> v1.14.0 (2026-06-26) **[Release]** Release candidate: repair samples, split/merge service finalization

## v1.13.x — Feature Complete

v1.13 marks the feature-complete milestone — three Live Photo protocols, smart metadata matcher, Lightbox, and Acrylic settings page all in place.

> **v1.13.5** (2026-06-26) **[Refactor]** Combo→Merge full rename (52 files) + unified converters + development docs
>
> v1.13.4 (2026-06-25) **[New]** LightboxPreview control (fullscreen image viewer), preview service integration
>
> **v1.13.3** (2026-06-24) **[New]** Smart metadata pairing engine — LivePhotoMetadataMatcher (415 lines)
>
> v1.13.2 (2026-06-24) **[UI]** Modern settings page (Backdrop/AcrylicVisibility converters), App.xaml theme unification
>
> v1.13.1 (2026-06-23) **[New]** ImagePreviewService fullscreen preview + Acrylic settings page
>
> **v1.13.0** (2026-06-23) **[Refactor]** **Three Live Photo protocols** — MicroVideo V1 / MotionPhoto V2 / OPPO Live Photo detection & parsing (+694 lines)

## v1.12.x — Service Refactoring & Tools

> v1.12.7 (2026-06-22) **[Optimize]** RepairAnalysisResult deep analysis, LivePhotoConstants system, thumbnail/filename optimization
>
> **v1.12.6** (2026-06-21) **[New]** EncoderHelper (270 lines, unified ffmpeg params) + LivePhotoBatchRunnerService batch engine
>
> v1.12.5 (2026-06-20) **[Refactor]** HEIC decoder rewrite, Combo queue optimization, TaskListScrollHelper
>
> **v1.12.4** (2026-06-20) **[Performance]** ExifTool daemon mode — PersistentExifTool, batch repair performance breakthrough
>
> v1.12.3 (2026-06-19) **[New]** BannerPreset model, unified progress bar state across all pages
>
> v1.12.2 (2026-06-18) **[Refactor]** Log system overhaul — CrashHandler + log trio + ComboBoxHelper + SessionStateManager
>
> v1.12.1 (2026-06-17) **[UI]** Hardware acceleration settings UI (QSV/NVENC/AMF toggles), full string update
>
> v1.12.0 (2026-06-15) **[Refactor]** **Service layer overhaul** — AppLogService/CrashLogWriter/CrashLogService split (+1299 lines)

## v1.11.x — Core Services

> **v1.11.1** (2026-06-13) **[Performance]** Hardware acceleration detection — HardwareService (Intel QSV / NVIDIA NVENC / AMD AMF)
>
> **v1.11.0** (2026-06-12) **[New]** FFmpeg video transcoding engine for Split service — VideoTranscodeService (486 lines)

## v1.10.x — HEIC Transcoding

> v1.10.1 (2026-06-11) **[Docs]** Merge/split tutorial images, hover preview, sample assets
>
> **v1.10.0** (2026-06-10) **[New]** HEIC transcoding service — HeicConverterService HEIC→JPEG decoding

## v1.9.x — Model Refactoring

> v1.9.2 (2026-06-08) **[UI]** ViewModel localization strings overhaul, cross-page layout adaptation
>
> v1.9.1 (2026-06-07) **[New]** LivePhotoBatchRunnerService batch processing prototype, MergeTask batch handling
>
> v1.9.0 (2026-06-05) **[Refactor]** **Model layer refactoring** — RepairAnalysisResult, MergeTask/SplitTask model unification, AppLogEntry system

## v1.8.x — Status Bar Control

> v1.8.1 (2026-05-25) **[Optimize]** BulkObservableCollection scan performance, ScanDirectoryButton removed
>
> **v1.8.0** (2026-05-23) **[New]** PageStatusBar control + ScanDirectoryButton directory picker

## v1.7.x — UI Polish

> v1.7.2 (2026-05-22) **[New]** Merge/split sample assets, scan progress model
>
> v1.7.1 (2026-05-20) **[UI]** Full page XAML layout/code-behind/string refinement
>
> v1.7.0 (2026-05-13) **[Docs]** README rewrite (Logo, badges, screenshots), solution restructure

## v1.6.x — Repair Engine

> v1.6.1 (2026-04-10) **[UI]** Home/About page layout & strings finalized, README rebrand
>
> **v1.6.0** (2026-04-10) **[New]** Repair engine — LivePhotoRepairService full repair analysis logic

## v1.5.x — First Beta

> **v1.5.0** (2026-04-10) **[Release]** **First Microsoft Store beta** — exiftool/jpegtran tool integration, Home page finalized

## v1.4.x — Split Service

> v1.4.2 (2026-04-09) **[Optimize]** RepairService iteration, ComboPage UI layout refresh, AppViewModel strings
>
> **v1.4.1** (2026-04-09) **[New]** LivePhotoRepairTask model, LivePhotoRepairService (+531 lines)
>
> **v1.4.0** (2026-04-08) **[New]** Split service — LivePhotoSplitService (201 lines), SplitPage layout restructuring

## v1.3.x — Structure Standardization

> v1.3.1 (2026-04-07) **[UI]** ConsolePage → **KeyPhotoPage** (cover frame replacement), navigation update
>
> **v1.3.0** (2026-04-07) **[Refactor]** Structure standardization — locale folder rename en-US→en / zh-CN→zh-Hans

## v1.2.x — Architecture Refactoring

> v1.2.5 (2026-04-06) **[UI]** Full app icon set (all sizes), 3D icon, About icon update
>
> v1.2.4 (2026-04-05) **[New]** CrashLogService, version detection in About page, FeedbackService
>
> v1.2.3 (2026-04-05) **[New]** LivePhotoSplitTask model, BulkObservableCollection performance, scan + thumbnail preview
>
> **v1.2.2** (2026-04-03) **[Refactor]** **Project rename** — LivePhotoStudio → **LivePhotoBox**, 45 files renamed
>
> v1.2.1 (2026-04-03) **[Refactor]** Architecture deepening: FilePicker/Thumbnail/Scan services, old SharedViewModel removed
>
> **v1.2.0** (2026-04-03) **[Refactor]** **Major architecture refactoring** — Services layer (AppSettings/Language/Composition) + AppViewModel MVVM

## v1.1.x — Localization & Settings

> v1.1.1 (2026-03-30) **[New]** Complete settings page, SharedViewModel config management, settings persistence
>
> **v1.1.0** (2026-03-29) **[New]** Multilingual resource files, replaced hardcoded strings with LanguageService architecture

## v1.0.x — First Complete Version

> **v1.0.0** (2026-03-29) **[Release]** **First complete version** — Merge + Split + Repair features fully functional

## v0.x — Initial Build

> v0.4.0 (2026-03-29) **[New]** Combo/Repair/Split pages fully connected, merge task model refactoring
>
> v0.3.0 (2026-03-28) **[New]** ComboPage merge logic, SharedViewModel 406 lines, LivePhotoTask model
>
> v0.2.0 (2026-03-27) **[New]** MainWindow layout, About page, Home page, Console debug page, GitHub CI workflow
>
> **v0.1.0** (2026-03-27) **[Init]** WinUI 3 + .NET 9 project scaffold, Split/Combo/Repair page skeletons, 30 initial files
