import { readdirSync, existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { z } from "zod";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { fail } from "@ldlework/workmark/helpers";

export const ROOT = resolve(import.meta.dirname, "..", "..");
export const MODS_DIR = join(ROOT, "mods");

/** Every mod with a buildable csproj (a `mods/<Name>/<Name>.csproj`). Discovered
 *  from disk so the build/install commands never drift from what actually exists. */
export function discoverMods(): string[] {
  return readdirSync(MODS_DIR, { withFileTypes: true })
    .filter((e) => e.isDirectory())
    .map((e) => e.name)
    .filter((name) => existsSync(csprojPath(name)))
    .sort();
}

/** The `mod` positional arg: an enum over every buildable mod. */
export function modArg(verb: string) {
  return z
    .enum(discoverMods() as [string, ...string[]])
    .describe(`Mod to ${verb}`);
}

/** The `--config` flag, shared by every build/install command. */
export const configFlag = z
  .enum(["Release", "Debug"])
  .default("Release")
  .describe("Build configuration");

export const csprojPath = (mod: string) => join(MODS_DIR, mod, `${mod}.csproj`);
export const dllPath = (mod: string, config: string) =>
  join(MODS_DIR, mod, "bin", config, `${mod}.dll`);

type Sh = (cmd: string) => Promise<CallToolResult>;
type Mode = "build" | "install" | "build-install";

/** The single seam through which all three commands drive the toolchain.
 *  - build         → compile only (`NoInstall=1` suppresses the InstallToGame copy)
 *  - build-install → compile, then the csproj's InstallToGame copy fires after Build
 *  - install       → run InstallToGame alone (pure copy of the already-built DLL/deps) */
export function runDotnet(sh: Sh, mod: string, config: string, mode: Mode) {
  const proj = csprojPath(mod);

  if (mode === "install") {
    const dll = dllPath(mod, config);
    if (!existsSync(dll))
      return fail(
        `${mod} is not built (${dll} missing). Run 'wm build ${mod}' or 'wm build-install ${mod}' first.`,
      );
    return sh(`dotnet msbuild "${proj}" -t:InstallToGame -p:Configuration=${config} -nologo -v:n`);
  }

  const noInstall = mode === "build" ? " -p:NoInstall=1" : "";
  return sh(`dotnet build "${proj}" -c ${config}${noInstall}`);
}
