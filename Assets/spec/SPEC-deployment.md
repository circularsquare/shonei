# Shonei — Deployment Spec

How the game ships to players. Two independently-deployed pieces: the **client**
(Unity build → itch.io) and the **market server** (Go → Hetzner).

## Client → itch.io

One-command flow: bump `bundleVersion` (Project Settings > Player), **close the
editor**, run `Tools/build-and-publish.ps1` from the project root.

- Builds Windows + Mac in batchmode via `Assets/Editor/BuildScript.cs`. The
  `Tools/Build/*` menu items do a manual build-only from inside the editor.
- Output: `<projects>/builds/win/Shonei/` and `builds/mac/Shonei.app`, wrapped so
  `butler push` uploads the folder directly. `*_DoNotShip` debug folders are stripped.
- Pushes to itch `anitagarden/shonei`, channels `win-64` and `mac`, tagged with the
  bundleVersion as `--userversion`. Players on the itch app auto-patch (diff upload).
- Version single source of truth = `bundleVersion`; shown in-game by
  `Assets/Components/VersionLabel.cs` (menu corner) via `Application.version`.

Flags: `-WindowsOnly` (skip Mac), `-SkipPush` (build only), `-UnityExe <path>`.

Prereqs: Unity 2021.3.16f1 installed; editor closed; `butler login` done once
(butler ships with the itch app; the script auto-locates it under broth/, so a
butler self-update won't break the pipeline). Operational detail in the
`project_client_build_publish` memory.

Non-obvious gotchas the script already handles:
- `Unity.exe` is a GUI-subsystem app, so PowerShell `&` does NOT wait for it — the
  script uses `Start-Process -Wait -PassThru` to block and read the real exit code.
- `System.IO.Path` is shadowed by the project's A* `Path` class in the Editor asmdef;
  `BuildScript.cs` aliases it (`using IOPath = System.IO.Path`).

## Server → Hetzner

The Go market server has its own pipeline: `shonei-server/deploy/deploy.ps1`
(build linux/amd64 → scp → restart systemd `shonei`). Builds always hit prod
(`wss://market.anita.garden`); accounts/auth are live. See
`shonei-server/deploy/README.md` and the `project_market_server_deployed` memory.

## Known gaps / future work

- **Mac build is unverified + fragile.** Cross-built from Windows → unsigned, and the
  `.app` exec bit can drop (friends may need `xattr -cr` + `chmod +x`). Apple Silicon
  needs IL2CPP, which can't cross-compile from Windows. Robust fix: build on a real Mac.
- **No code signing / notarization.** Windows SmartScreen warns; macOS Gatekeeper blocks.
  Fine for friend playtesting; needed before any public release.
- **Builds are manual on one machine.** No CI. Candidate: GitHub Actions or Unity Cloud
  Build for reproducible builds off a clean checkout.
- **No world-save backups for playtesters.** Friends now generate real saves on the prod
  server — confirm/implement backups before relying on them (see todo.txt SERVER section).
- **Save-format migration.** Builds iterate fast; a format break orphans friends' saves.
  Decide a migrate-vs-wipe-with-message policy before the next breaking save change.
- **No structured playtester feedback loop.** LogError output vanishes into Player.log
  (`%USERPROFILE%\AppData\LocalLow\Shonei\Shonei\Player.log`) where friends won't look.
- **Version bump is manual.** Could auto-increment a build number on each publish.
- **Build polish (todo.txt):** "weird maximize window behavior on build" and "ui scaling"
  only reproduce in a real build — settle Player Settings > Resolution & Presentation.
