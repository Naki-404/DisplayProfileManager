<p align="center">
  <img src="Assets/app.ico" width="72" alt="Display Profile Manager icon">
</p>

<h1 align="center">Display Profile Manager</h1>

<p align="center">
  <strong>Per-game display profiles for Windows</strong><br>
  Automatically switch resolution, power plan, and color when a game starts — and restore everything when it exits.
</p>

<p align="center">
  <a href="https://github.com/Naki-404/DisplayProfileManager/releases/latest"><img src="https://img.shields.io/github/v/release/Naki-404/DisplayProfileManager?style=flat-square&label=release" alt="Latest release"></a>
  <a href="https://github.com/Naki-404/DisplayProfileManager/releases/latest"><img src="https://img.shields.io/github/downloads/Naki-404/DisplayProfileManager/total?style=flat-square" alt="Downloads"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0e7a0d?style=flat-square" alt="Platform">
  <img src="https://img.shields.io/badge/.NET-Desktop%20Runtime%206%2B-512bd4?style=flat-square" alt=".NET">
  <img src="https://img.shields.io/badge/version-1.8.0-7EB8D4?style=flat-square" alt="Version">
</p>

<p align="center">
  <a href="#download">Download</a> ·
  <a href="#features">Features</a> ·
  <a href="#how-it-works">How it works</a> ·
  <a href="#build-from-source">Build</a> ·
  <a href="#sync-local-clone">Sync</a>
</p>

---

## Download

| File | What it is |
|------|------------|
| **[DisplayProfileManager-Setup.exe](https://github.com/Naki-404/DisplayProfileManager/releases/latest)** | Recommended installer (Start Menu + Apps and Features uninstall) |
| **DisplayProfileManager.exe** | Portable single-file build (see `release\` after a local build) |

**Requirement:** [.NET Desktop Runtime 6+](https://aka.ms/dotnet/6.0/windowsdesktop-runtime-win-x64.exe) (x64).  
The installer detects a missing runtime and opens the download page for you.

Default install location:

```text
%LocalAppData%\Programs\DisplayProfileManager\
```

Config and log:

```text
%AppData%\DisplayProfileManager\profiles.json
%AppData%\DisplayProfileManager\DisplayProfileManager.log
```

---

## Features

- **Play-first UI** — live color while a game is active, slate dark theme
- **Per-game profiles** — resolution, Windows power plan, brightness / contrast / gamma / vibrance / hue
- **Auto apply** — watches for the game process, applies the profile, restores on exit
- **Hotkey presets** — switch color profiles in-game; mouse X1/X2 supported
- **Pack gallery** — bundled Tarkov / Valorant / PUBG preset packs
- **Game overlay** — compact on-screen controls without leaving the game
- **Center-screen zoom** — Windows Magnifier API (no injection into games)
- **NVIDIA FPS limit** — optional driver FPS cap per profile (when available)
- **Companions** — launch helper apps with the game and stop them afterward
- **Tray mode** — close to tray, quick profile / preset switch
- **Themes** — dark, light, or a custom palette · **EN / RU**
- **Import / export** — share or back up `profiles.json`
- **Emergency restore** — one-click safe display reset

> Display changes use the Windows API (`ChangeDisplaySettingsEx`).  
> Optional `QRes.exe` is only a tiny fallback if the API call fails.  
> Nothing is injected into games — system settings only.

---

## How it works

```text
  Game starts  -->  match .exe to a profile  -->  apply resolution / power / color
                                                        |
  Game exits   <----------------------------------------+
               restore previous snapshot / global defaults
```

1. **Scan** or add games you care about.
2. For each profile: enable it, set resolution / power / color / session options.
3. Leave the app running in the tray (optional autostart).
4. Launch the game as usual — the profile applies automatically.

---

## First launch

A short wizard asks for language, theme, autostart / tray / toasts, and default monitor.  
Everything can be changed later in **Settings**.

---

## Build from source

```powershell
git clone https://github.com/Naki-404/DisplayProfileManager.git
cd DisplayProfileManager
dotnet build .\DisplayProfileManager.csproj -c Release
```

Create the installer + portable package (writes into project-root `\release`):

```powershell
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
```

Output:

```text
release\DisplayProfileManager.exe
release\DisplayProfileManager-Setup.exe
release\portable\
```

Requires the .NET 6 SDK (or newer) with the Windows Desktop workload.

**Note:** Unsigned builds may trip antivirus heuristics (process watchers, gamma APIs). A paid Authenticode certificate is the durable fix for SmartScreen.

---

## Sync local clone

Update your local copy from GitHub:

```powershell
cd C:\Users\Kreis\DisplayProfileManager
git pull origin main
```

Publish local commits to the remote:

```powershell
git push -u origin HEAD
```

---

## Project layout

```text
DisplayProfileManager/
├── App.xaml / AppServices.cs
├── Assets/                 # app.ico, Sounds, Packs
├── Models/
├── Views/
├── Controls/
├── Services/
│   ├── Display/
│   ├── Session/
│   ├── Config/
│   ├── Input/
│   ├── Ui/
│   └── System/
├── Installer/
├── release/                # build-release.ps1 output (gitignored)
├── build-release.ps1
└── DisplayProfileManager.csproj
```

---

## Logs

Open the **Log** tab in the app (auto-refreshes when selected), or open:

```text
%AppData%\DisplayProfileManager\DisplayProfileManager.log
```

Levels: `INFO`, `WARN`, `ERROR` (errors include exception type and a short stack).

---

## Privacy and safety

- Config stays on your PC under `%AppData%`
- No telemetry, no accounts, no cloud sync
- Safe for anti-cheat: system settings only — no game memory / input hooks

---

## Author

**Nakidev** · [@nakidev](https://t.me/nakidev) · [Naki-404](https://github.com/Naki-404)

---

## License

Personal / freeware use. See repository for terms if added later.
