// Process control via PowerShell. Two jobs: kill every running Cairn, and launch one instance with the
// co-op env vars set (host or joiner). We launch the exe directly with SteamAppId set so Steam's
// RestartAppIfNecessary stays quiet and the second instance isn't blocked — same recipe as
// tools/run-local-coop-test.ps1.

import { spawn } from "node:child_process";

const GAME_DIR = "P:\\Steam\\steamapps\\common\\Cairn";
const STEAM_APP_ID = "1588550";

/** Run a PowerShell script block and resolve with its stdout (rejects on non-zero exit). */
function pwsh(script: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const child = spawn(
      "powershell.exe",
      ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
      { windowsHide: true },
    );
    let out = "";
    let err = "";
    child.stdout.on("data", (d) => (out += d.toString()));
    child.stderr.on("data", (d) => (err += d.toString()));
    child.on("error", reject);
    child.on("close", (code) =>
      code === 0 ? resolve(out.trim()) : reject(new Error(`powershell exited ${code}: ${err.trim() || out.trim()}`)),
    );
  });
}

/** Kill every running Cairn (host + joiner). Safe to call when none are running — the try/catch +
 * explicit `exit 0` keeps PowerShell from surfacing a non-terminating "no process" error as exit 1. */
export async function killCairn(): Promise<void> {
  await pwsh("try { Get-Process Cairn -ErrorAction Stop | Stop-Process -Force } catch {}; exit 0");
}

export type Role = "host" | "joiner";

/**
 * Launch one Cairn instance with the co-op env vars set. The HOST is instance 1 (autohost); every other is
 * a local joiner with a 1-based `instanceIndex` (2, 3, …) passed through CAIRNCOOP_INSTANCE — which the mod
 * uses to bind a distinct ModLoopback port (HostPort + index-1) and name suffix, so N instances coexist
 * locally. Returns once Start-Process has been issued (the game boots on its own; the caller polls the
 * console for readiness).
 */
export async function launchCairn(role: Role, instanceIndex = 1): Promise<void> {
  const autohost = role === "host" ? "1" : "";
  const autojoin = role === "joiner" ? "local" : "";
  const parts = [
    `$env:SteamAppId = '${STEAM_APP_ID}'`,
    `$env:SteamGameId = '${STEAM_APP_ID}'`,
    `$env:CAIRNCOOP_AUTOHOST = '${autohost}'`,
    `$env:CAIRNCOOP_AUTOJOIN = '${autojoin}'`,
  ];
  // Joiners 2,3,… carry their ordinal so the mod binds a distinct loopback port. Host (1) omits it.
  if (role === "joiner") parts.push(`$env:CAIRNCOOP_INSTANCE = '${instanceIndex}'`);
  parts.push(`Start-Process '${GAME_DIR}\\Cairn.exe' -WorkingDirectory '${GAME_DIR}'`);
  await pwsh(parts.join("; "));
}
