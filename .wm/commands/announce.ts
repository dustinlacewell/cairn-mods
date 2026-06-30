import { readFileSync, existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { execSync } from "node:child_process";
import { z } from "zod";
import { cmd } from "@ldlework/workmark/define";
import { ok, fail } from "@ldlework/workmark/helpers";

const ROOT = resolve(import.meta.dirname, "../..");
const MODS_DIR = join(ROOT, "mods");
const NOTES_DIR = join(ROOT, "release-notes");
const WEBHOOKS_FILE = join(ROOT, ".discord-webhooks");

const KNOWN_MODS = [
  "CairnAPI", "CairnModOptions", "CairnDevTools", "CairnCoop", "CairnRoutes",
  "CairnFreeRoam", "CairnForage", "CairnCarryWeight", "CairnAccessories",
  "CairnNoCutscenes", "CairnArchipelago", "CairnTools",
];

const EMBED_COLOR = 5763719; // green — same as CI

// Read DISCORD_WEBHOOK_<MOD> from .discord-webhooks (same parse as sync-secrets), env override wins.
function webhookFor(mod: string): string | null {
  const key = `DISCORD_WEBHOOK_${mod.toUpperCase()}`;
  if (process.env[key]) return process.env[key] as string;
  if (!existsSync(WEBHOOKS_FILE)) return null;
  for (const line of readFileSync(WEBHOOKS_FILE, "utf-8").split("\n")) {
    const l = line.trim();
    if (!l || l.startsWith("#")) continue;
    const eq = l.indexOf("=");
    if (eq === -1) continue;
    if (l.slice(0, eq).trim() === key) return l.slice(eq + 1).trim();
  }
  return null;
}

// Latest <Mod>-vX.Y.Z tag, by creation date.
function latestTag(mod: string): string | null {
  try {
    const out = execSync(`git tag --list "${mod}-v*" --sort=-creatordate`, {
      cwd: ROOT, encoding: "utf-8",
    }).trim();
    return out ? out.split("\n")[0].trim() : null;
  } catch {
    return null;
  }
}

const versionOf = (tag: string, mod: string) => tag.replace(`${mod}-v`, "");

// Release notes minus the "## " header and blank lines — identical to the CI sed pipeline.
function notesFor(mod: string): string {
  const p = join(NOTES_DIR, `${mod}.md`);
  if (!existsSync(p)) return "";
  return readFileSync(p, "utf-8")
    .split("\n")
    .filter((l) => !l.startsWith("## ") && l.trim() !== "")
    .join("\n");
}

function repoSlug(): string {
  const url = execSync("git remote get-url origin", { cwd: ROOT, encoding: "utf-8" }).trim();
  const m = url.match(/[:/]([^/]+\/[^/]+?)(?:\.git)?$/);
  return m ? m[1] : "";
}

/** Post a mod's release announcement to its Discord channel (re-send the CI embed for a given tag). */
export default cmd({
  args: {
    mod: z.enum(KNOWN_MODS as [string, ...string[]]).describe("Mod name"),
    tag: z.string().optional().describe("Release tag (default: the mod's latest <Mod>-vX.Y.Z tag)"),
  },
  handler: async ({ mod, tag }, { ok: _ok }) => {
    const webhook = webhookFor(mod);
    if (!webhook) return fail(`No webhook for ${mod} in .discord-webhooks (or env DISCORD_WEBHOOK_${mod.toUpperCase()}).`);

    const resolvedTag = tag ?? latestTag(mod);
    if (!resolvedTag) return fail(`No tag given and no ${mod}-v* tag found.`);

    const version = versionOf(resolvedTag, mod);
    const slug = mod.toLowerCase();
    const repo = repoSlug();
    const title = `${mod} v${version}`;
    const embed = {
      embeds: [{
        title,
        url: `http://cairn.ldlework.com/mods/${slug}`,
        color: EMBED_COLOR,
        description: notesFor(mod),
        fields: [{
          name: "Download",
          value: `[${title}.dll](https://github.com/${repo}/releases/download/${resolvedTag}/${mod}.dll)`,
          inline: true,
        }],
      }],
    };

    const res = await fetch(webhook, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(embed),
    });
    if (!res.ok) {
      return fail(`Discord POST failed: ${res.status} ${res.statusText} — ${await res.text()}`);
    }
    return ok(`Announced ${title} → ${mod} channel (tag ${resolvedTag}).`);
  },
});
