import { cmd } from "@ldlework/workmark/define";
import { modArg, configFlag, runDotnet } from "../lib/mods.js";

/** Compile a mod and deploy it into the game in one step (the common dev loop). */
export default cmd({
  args: { mod: modArg("build and install") },
  flags: { config: configFlag },
  handler: ({ mod, config }, { sh }) => runDotnet(sh, mod, config, "build-install"),
});
