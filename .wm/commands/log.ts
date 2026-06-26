import { appendFileSync, existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { z } from "zod";
import { cmd } from "@ldlework/workmark/define";
import { ok, fail } from "@ldlework/workmark/helpers";

const MODS_DIR = resolve(import.meta.dirname, "../../mods");

const KNOWN_MODS = [
  "CairnAPI", "CairnDevTools", "CairnCoop", "CairnRoutes", "CairnFreeRoam",
  "CairnForage", "CairnCarryWeight", "CairnAccessories", "CairnNoCutscenes",
  "CairnArchipelago", "CairnTools", "CrossMenuLib",
];

/** Append a changelog entry for a mod. */
export default cmd({
  args: {
    mod: z.enum(KNOWN_MODS as [string, ...string[]]).describe("Mod name"),
    message: z.string().describe("Changelog entry"),
  },
  handler: ({ mod, message }) => {
    try {
      const modDir = join(MODS_DIR, mod);
      if (!existsSync(modDir)) return fail(`No mod directory found for ${mod}`);

      const changelogPath = join(modDir, "changelog.txt");
      appendFileSync(changelogPath, `- ${message}\n`, "utf-8");
      return ok(`Logged to ${mod}/changelog.txt`);
    } catch (e) {
      return fail(e);
    }
  },
});
