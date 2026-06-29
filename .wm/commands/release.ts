import { readFileSync, writeFileSync, existsSync, mkdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { z } from "zod";
import { cmd } from "@ldlework/workmark/define";
import { ok, fail } from "@ldlework/workmark/helpers";

const ROOT = resolve(import.meta.dirname, "../..");
const MODS_DIR = join(ROOT, "mods");
const RELEASE_NOTES_DIR = join(ROOT, "release-notes");

const KNOWN_MODS = [
  "CairnAPI", "CairnModOptions", "CairnDevTools", "CairnCoop", "CairnRoutes",
  "CairnFreeRoam", "CairnForage", "CairnCarryWeight", "CairnAccessories",
  "CairnNoCutscenes", "CairnArchipelago", "CairnTools",
];

function bumpVersion(version: string, part: "patch" | "minor" | "major"): string {
  const [major, minor, patch] = version.split(".").map(Number);
  if (part === "major") return `${major + 1}.0.0`;
  if (part === "minor") return `${major}.${minor + 1}.0`;
  return `${major}.${minor}.${patch + 1}`;
}

/**
 * Prepare a per-mod release: bump the version, write release notes from the changelog,
 * commit those files, and create the tag `<Mod>-v<version>` — all LOCALLY.
 *
 * It deliberately does NOT push. Pushing the tag triggers the live build/release/Discord
 * pipeline, so it stays a separate, deliberate step: the command prints the exact
 * `git push` line to run once you've reviewed the commit. Nothing this command does
 * reaches the network or is hard to undo (`git reset --hard HEAD~1 && git tag -d <tag>`).
 */
export default cmd({
  args: {
    mod: z.enum(KNOWN_MODS as [string, ...string[]]).describe("Mod name"),
    part: z.enum(["patch", "minor", "major"]).default("patch").describe("Version part to bump"),
  },
  handler: ({ mod, part }, { sh }) => {
    try {
      const modDir = join(MODS_DIR, mod);
      const csprojPath = join(modDir, `${mod}.csproj`);
      if (!existsSync(csprojPath)) return fail(`No csproj found at ${csprojPath}`);

      // Only published mods can be released — CI's parse job rejects unpublished tags.
      const publishJson = JSON.parse(readFileSync(join(ROOT, "publish.json"), "utf-8"));
      if (!publishJson.mods.includes(mod))
        return fail(`${mod} is not in publish.json — add it before releasing.`);

      const csproj = readFileSync(csprojPath, "utf-8");
      const versionMatch = csproj.match(/<Version>(.+?)<\/Version>/);
      if (!versionMatch) return fail(`No <Version> tag found in ${mod}.csproj`);

      const oldVersion = versionMatch[1];
      const newVersion = bumpVersion(oldVersion, part as "patch" | "minor" | "major");
      const tag = `${mod}-v${newVersion}`;

      const changelogPath = join(modDir, "changelog.txt");
      const changelog = existsSync(changelogPath)
        ? readFileSync(changelogPath, "utf-8").trim()
        : "";

      // 1. Bump csproj, write notes from changelog, clear changelog.
      writeFileSync(csprojPath,
        csproj.replace(`<Version>${oldVersion}</Version>`, `<Version>${newVersion}</Version>`), "utf-8");

      mkdirSync(RELEASE_NOTES_DIR, { recursive: true });
      const notesPath = join(RELEASE_NOTES_DIR, `${mod}.md`);
      writeFileSync(notesPath,
        changelog ? `## ${mod} v${newVersion}\n\n${changelog}\n` : `## ${mod} v${newVersion}\n`, "utf-8");

      if (existsSync(changelogPath)) writeFileSync(changelogPath, "", "utf-8");

      // 2. Commit exactly the release files and create the tag — LOCAL ONLY, no push.
      sh([
        `git add "mods/${mod}/${mod}.csproj" "release-notes/${mod}.md"`,
        `git commit -m "release(${mod}): v${newVersion}"`,
        `git tag ${tag}`,
      ]);

      return ok({
        mod,
        version: { from: oldVersion, to: newVersion },
        tag,
        notes: notesPath,
        committed: true,
        pushed: false,
        pushWith: `git push origin main ${tag}`,
        undoWith: `git reset --hard HEAD~1 && git tag -d ${tag}`,
      });
    } catch (e) {
      return fail(e);
    }
  },
});
