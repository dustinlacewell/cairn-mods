import { readFileSync, existsSync } from "node:fs";
import { resolve } from "node:path";
import { cmd } from "@ldlework/workmark/define";
import { ok, fail, execRaw } from "@ldlework/workmark/helpers";

const WEBHOOKS_FILE = resolve(import.meta.dirname, "../../.discord-webhooks");

/** Sync Discord webhook secrets from .discord-webhooks to GitHub Actions. */
export default cmd({
  handler: () => {
    if (!existsSync(WEBHOOKS_FILE)) {
      return fail(`.discord-webhooks not found at ${WEBHOOKS_FILE}`);
    }

    const lines = readFileSync(WEBHOOKS_FILE, "utf-8")
      .split("\n")
      .map((l) => l.trim())
      .filter((l) => l && !l.startsWith("#"));

    const results: { name: string; status: string }[] = [];

    for (const line of lines) {
      const eq = line.indexOf("=");
      if (eq === -1) continue;
      const name = line.slice(0, eq).trim();
      const value = line.slice(eq + 1).trim();
      try {
        execRaw(`echo ${JSON.stringify(value)} | gh secret set ${name}`, {});
        results.push({ name, status: "set" });
      } catch (e) {
        results.push({ name, status: `failed: ${e}` });
      }
    }

    return ok({ synced: results.filter((r) => r.status === "set").length, results });
  },
});
