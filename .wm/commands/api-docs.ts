import { z } from "zod";
import { cmd } from "@ldlework/workmark/define";
import { ok, fail } from "@ldlework/workmark/helpers";
import { modArg } from "../lib/mods.js";
import { generateApiDocs, writeApiDocs } from "../lib/apidocs.js";

/**
 * Generate a mod's site API reference from its C# `///` doc comments.
 *
 * Runs DocFX `metadata` over the mod's project (needs the global tool:
 * `dotnet tool install -g docfx`), maps the result to the site's DocSection[]
 * shape, and writes site/src/data/api/<Mod>.json — which mods.ts consumes so the
 * reference never drifts from the source. A member appears IFF it has a `///`
 * <summary>; document a member to publish it, leave it bare to keep it out.
 *
 * `wm api-docs CairnAPI` for one mod. (Re)run on release, or whenever the public
 * surface changes, and commit the regenerated JSON.
 */
export default cmd({
  args: { mod: modArg("generate API docs for") },
  flags: { quiet: z.boolean().default(false).describe("Suppress the per-section summary") },
  handler: ({ mod, quiet }) => {
    try {
      const result = generateApiDocs(mod);
      const out = writeApiDocs(mod, result);
      return ok({
        mod,
        out,
        types: result.types,
        members: result.members,
        ...(quiet ? {} : { sections: result.sections.map((s) => `${s.title} (${s.entries?.length ?? 0})`) }),
      });
    } catch (e) {
      return fail(e);
    }
  },
});
