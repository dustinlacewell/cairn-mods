import { cmd } from "@ldlework/workmark/define";
import { modArg, configFlag, runDotnet } from "../lib/mods.js";

/** Deploy a mod's already-built DLL (and its deps) into the game — no recompile. */
export default cmd({
  args: { mod: modArg("install") },
  flags: { config: configFlag },
  handler: ({ mod, config }, { sh }) => runDotnet(sh, mod, config, "install"),
});
