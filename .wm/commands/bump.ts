import { readFileSync, writeFileSync, existsSync, mkdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { z } from "zod";
import { cmd } from "@ldlework/workmark/define";
import { ok, fail } from "@ldlework/workmark/helpers";

const MODS_DIR = resolve(import.meta.dirname, "../../mods");
const RELEASE_NOTES_DIR = resolve(import.meta.dirname, "../../release-notes");

const KNOWN_MODS = [
  "CairnAPI", "CairnDevTools", "CairnCoop", "CairnRoutes", "CairnFreeRoam",
  "CairnForage", "CairnCarryWeight", "CairnAccessories", "CairnNoCutscenes",
  "CairnArchipelago", "CairnTools", "CrossMenuLib",
];

function bumpVersion(version: string, part: "patch" | "minor" | "major"): string {
  const [major, minor, patch] = version.split(".").map(Number);
  if (part === "major") return `${major + 1}.0.0`;
  if (part === "minor") return `${major}.${minor + 1}.0`;
  return `${major}.${minor}.${patch + 1}`;
}

/** Bump a mod's version, write release notes from changelog, and clear the changelog. */
export default cmd({
  args: {
    mod: z.enum(KNOWN_MODS as [string, ...string[]]).describe("Mod name"),
    part: z.enum(["patch", "minor", "major"]).default("patch").describe("Version part to bump"),
  },
  handler: ({ mod, part }) => {
    try {
      const modDir = join(MODS_DIR, mod);
      if (!existsSync(modDir)) return fail(`No mod directory found for ${mod}`);

      const csprojPath = join(modDir, `${mod}.csproj`);
      if (!existsSync(csprojPath)) return fail(`No csproj found at ${csprojPath}`);

      const csproj = readFileSync(csprojPath, "utf-8");
      const versionMatch = csproj.match(/<Version>(.+?)<\/Version>/);
      if (!versionMatch) return fail(`No <Version> tag found in ${mod}.csproj`);

      const oldVersion = versionMatch[1];
      const newVersion = bumpVersion(oldVersion, part as "patch" | "minor" | "major");

      const changelogPath = join(modDir, "changelog.txt");
      const changelog = existsSync(changelogPath)
        ? readFileSync(changelogPath, "utf-8").trim()
        : "";

      const updatedCsproj = csproj.replace(
        `<Version>${oldVersion}</Version>`,
        `<Version>${newVersion}</Version>`
      );
      writeFileSync(csprojPath, updatedCsproj, "utf-8");

      mkdirSync(RELEASE_NOTES_DIR, { recursive: true });
      const notesPath = join(RELEASE_NOTES_DIR, `${mod}.md`);
      const notes = changelog
        ? `## ${mod} v${newVersion}\n\n${changelog}\n`
        : `## ${mod} v${newVersion}\n`;
      writeFileSync(notesPath, notes, "utf-8");

      if (existsSync(changelogPath)) {
        writeFileSync(changelogPath, "", "utf-8");
      }

      return ok({
        mod,
        version: { from: oldVersion, to: newVersion },
        notes: notesPath,
        entries: changelog ? changelog.split("\n").length : 0,
      });
    } catch (e) {
      return fail(e);
    }
  },
});
