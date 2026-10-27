# WSA - Windows Subsystem for Android

WSA is a Windows desktop app (C#, WinUI 3) that manages the **official Android Emulator** and Android SDK command-line tools through a graphical interface and an integrated terminal, **WSA Terminal**.

WSA is **not** a hypervisor, not Microsoft's discontinued Windows Subsystem for Android, and does not replace Android. It discovers your Android SDK, runs the real `adb`, `emulator`, `avdmanager` and `sdkmanager` binaries, and shows what they report.

## Status (honest summary)

This is an early **Phase 1 + 2 + 3** release (foundation, virtual-device lifecycle, in-app setup and Android Store). The Phase 1+2 code built successfully in GitHub Actions. Phase 3 (Setup, Android Store) has **not been compiled or run yet**; it was written without a .NET SDK or Windows. Nothing has been verified on a real machine, and the downloads (JDK, Google's repository index, `sdkmanager --list` parsing) have never been exercised end to end. Expect to fix issues on first run.

| Feature | State |
|---|---|
| Overview page (SDK path, tool status, emulator version, AVD count / running count) | Implemented, untested |
| SDK discovery and diagnostics (runs each tool, `emulator -accel-check`) | Implemented, untested |
| Virtual Devices page: list AVDs with real running state, plus devices reported by adb | Implemented, untested |
| Create AVD (installed system images + `avdmanager` device profiles, name validation) | Implemented (Phase 2), untested |
| Start AVD (independent `emulator -avd` process, early-exit detection, polls adb until running) | Implemented (Phase 2), untested |
| Stop AVD (`adb -s emulator-N emu kill`; never kills processes by name) | Implemented (Phase 2), untested |
| Delete AVD (confirmation dialog, refuses while running) | Implemented (Phase 2), untested |
| Progress bar, live tool output and Cancel for lifecycle operations | Implemented (Phase 2), untested |
| WSA Terminal: direct execution of SDK tools, streaming output, history (Up/Down), copy, clear, cancel (Esc), exit codes | Implemented, untested |
| Interactive `adb shell` | **Not supported.** `adb shell <command>` runs one-shot commands only |
| **Setup** page: installs a private JDK (Microsoft OpenJDK 21, SHA-256 checked), Google's command-line tools (SHA-1 from Google's index), platform-tools and emulator via `sdkmanager`; license screen with explicit Accept; first-run redirect | Implemented (Phase 3), untested |
| **Android Store** page: lists x86_64 system images (Google Play / Google APIs / AOSP) from `sdkmanager --list`, Install / Remove with progress and Cancel, free-space check, jump to *Create virtual device* | Implemented (Phase 3), untested |
| **Phone** page: real phone over USB or Wi-Fi (adb pair/connect), device info, live view with click-to-tap and drag-to-swipe, Back/Home/Recents/Power/volume keys, ASCII text input, APK install, screenshot save, adb-only quick install (~10 MB, SHA-1 checked, license shown) | Implemented (Phase 4), untested |
| Phone live view smoothness | **Limited**: polls `adb exec-out screencap -p`, so a few frames per second, not video. Smooth mirroring would need scrcpy, a separate download that is not included |
| Pause/resume of downloads | **Not implemented** (Cancel only; a cancelled download restarts from zero) |
| Per-image download sizes | **Not shown** (`sdkmanager --list` does not report them; expect 1-2 GB each) |
| Enabling virtualization / Windows Hypervisor Platform | **Cannot be done by WSA** (needs admin + restart); Setup shows the check result and opens Windows Features |
| Settings (SDK root, buffer size) persisted to `%LOCALAPPDATA%\WSA\settings.json` | Implemented, untested |
| MSIX package | **Not produced** (see Packaging) |

## Architecture

- `src/WSA.Core` - models, interfaces, command tokenizer/parser, output parsers, `TerminalService`. No Windows dependencies.
- `src/WSA.Infrastructure` - `ProcessRunner` (no shell, `ArgumentList`, async stream capture, tree-kill on cancel/timeout), `SdkLocator`, `AdbService`, `AvdService`, `SettingsService`.
- `src/WSA.App` - WinUI 3 shell, MVVM view models (CommunityToolkit.Mvvm), pages, DI (`Microsoft.Extensions.DependencyInjection`).

Pinned: .NET SDK 10.0.100 (`global.json`, roll-forward to latest feature band), `Microsoft.WindowsAppSDK` 2.5.1 (`Directory.Packages.props`), target framework `net10.0-windows10.0.19041.0`. These versions come from Microsoft's published release notes and were **not** restored or verified in the authoring sandbox. If CI fails at restore, check this first.

## Requirements

- Windows 10 1809 (build 17763) or later, x64. Windows 11 recommended. (Minimum from Windows App SDK documentation; not tested.)
- Hardware virtualization enabled in firmware, plus Windows Hypervisor Platform (Intel/AMD) for emulator acceleration. Check with `emulator -accel-check` in WSA Terminal.
- A JDK 17+ (required by `sdkmanager` / `avdmanager`).
- The Android SDK with command-line tools, platform-tools, emulator and at least one system image (WSA does not bundle them).

## Android SDK setup

**Recommended: do it inside WSA.** On first launch WSA opens **Setup**. Press *Install everything that is missing*, read and accept Google's license text, and wait. Then open **Android Store**, install an Android version, and press *Create virtual device*. Downloads: JDK from Microsoft (`aka.ms/download-jdk`), Android tools from `dl.google.com`; the JDK goes to `%LOCALAPPDATA%\WSA\jdk` and the SDK to `%LOCALAPPDATA%\Android\Sdk` unless you set another folder in Settings. Nothing is installed system-wide and no admin rights are needed. You still need virtualization enabled in BIOS/UEFI plus the Windows Hypervisor Platform feature.

Manual alternative:

1. Install a JDK (17 or newer).
2. Download "Command line tools only" from <https://developer.android.com/studio#command-line-tools-only>; extract so that `sdkmanager.bat` is at `<SDK root>\cmdline-tools\latest\bin\`. A typical root is `%LOCALAPPDATA%\Android\Sdk`.
3. In WSA Terminal: `sdkmanager --licenses` (read and accept the licenses yourself).
4. `sdkmanager "platform-tools" "emulator" "system-images;android-34;google_apis;x86_64"`
5. Create a device: `avdmanager create avd -n Pixel_34 -k "system-images;android-34;google_apis;x86_64" -d pixel_6`
6. Open *Runtime & SDK* and press **Re-detect**. If the SDK is elsewhere, set the root in *Settings*.

## WSA Terminal

Input is tokenized (quotes group, backslashes are literal) and the matching SDK tool is launched **directly**; there is no `cmd.exe`/PowerShell, so pipes and `&&` do not work. `help` lists everything.

- Built-ins: `sdk`, `versions`, `refresh`, `avds`, `devices`, `clear`, `help`
- Tools: `adb ...`, `emulator ...`, `avdmanager ...`, `sdkmanager ...`
- Advanced mode: `sdk-exec <path relative to SDK root> [args]` runs another executable inside the SDK root (paths escaping the root are rejected).
- Launch an AVD: `emulator -avd Pixel_34` (runs until you cancel it or close the emulator window; Esc cancels and stops the process WSA started). Stop one by serial: `adb -s emulator-5554 emu kill`.
- Up/Down = history, Esc = cancel, Enter = run.

## Build locally

Requires Windows, the .NET 10 SDK, and the Windows App SDK-compatible build tools (Visual Studio 2022 17.x/2026 with the ".NET desktop" workload is the simplest route).

```powershell
dotnet restore WSA.sln -r win-x64
dotnet build WSA.sln -c Release -p:Platform=x64 --no-restore
./scripts/publish.ps1        # produces artifacts/WSA-win-x64.zip
```

## CI and releases

Workflow: `.github/workflows/build.yml` (Windows runner). Every push/PR builds and uploads artifacts; pushing a tag like `v0.1.0` also creates a GitHub Release.

- Artifacts: `WSA-win-x64.zip`, `WSA-build-info.txt` (Actions run page -> Artifacts, or the Release assets)
- No signing secrets are used.

## Packaging

The ZIP is an **unpackaged, self-contained** build (.NET and the Windows App SDK runtime are included), so it runs on a clean x64 Windows machine after extraction. The Android SDK/emulator are **not** included.

An **MSIX is not produced yet.** MSIX needs a signing certificate that matches the manifest publisher; this project does not fabricate one or disable signature checks. An MSIX can be added later with a repository-secret certificate.

## Connect a real phone

Open **Phone**. If adb is missing, press *Install adb* (about 10 MB from Google; no Java, emulator or Android image). Then:

- **USB:** on the phone enable Developer options (Settings > About phone > tap Build number 7 times) and turn on *USB debugging*; connect a data-capable cable; unlock the phone and tap *Allow* (tick *Always allow*). Some brands need their USB driver on Windows.
- **Wi-Fi (Android 11+):** Developer options > Wireless debugging > on. In WSA press *Connect over Wi-Fi...*, enter the connect address, and the first time also the pairing address and 6-digit code from *Pair device with pairing code*. PC and phone must be on the same network.
- Turn on **Live view** to see the screen; click to tap, drag to swipe. *Install APK...*, *Save screenshot...*, hardware keys and text input work on the selected device. Text input supports plain ASCII only.

Notes: only what adb can do is possible. Apps that block screenshots (banking apps, some video) show a black frame. Live view is screenshot polling, not video.

## Create and launch a virtual device

1. Install a system image (see SDK setup step 4).
2. Open **Virtual Devices -> Create virtual device**, pick the image and a device profile, enter a name (letters, digits, `.`, `_`, `-`).
3. Press **Start**. The official Android Emulator opens in its own window; WSA keeps polling adb for up to 90 s and marks the device *Running* once adb reports it. First boot can take minutes.
4. **Stop** sends `adb emu kill` to that emulator only. **Delete** asks for confirmation and is disabled for running devices.

Notes: the emulator is started without redirected output so it stays independent of WSA; if it dies within 8 s WSA reports the exit code and tells you to run `emulator -avd NAME` in WSA Terminal to see the real error.

## Troubleshooting

- **"Android SDK not found"**: set the SDK root in Settings or define `ANDROID_SDK_ROOT`.
- **sdkmanager/avdmanager "Problem"**: usually no JDK; install JDK 17+ and set `JAVA_HOME`.
- **Emulator listed but acceleration check fails**: enable virtualization in BIOS/UEFI and turn on "Windows Hypervisor Platform".
- **No virtual devices**: install a system image and create an AVD (steps above).
- **Crashes**: see `%LOCALAPPDATA%\WSA\crash.log`.
- **CI restore fails**: verify the package versions in `Directory.Packages.props` exist on NuGet.

## License

MIT
