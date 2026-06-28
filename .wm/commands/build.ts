import { cmd } from "@ldlework/workmark/define";
import { modArg, configFlag, runDotnet } from "../lib/mods.js";

/** Compile a mod's DLL without deploying it to the game (NoInstall). */
export default cmd({
  args: { mod: modArg("build") },
  flags: { config: configFlag },
  handler: ({ mod, config }, { sh }) => runDotnet(sh, mod, config, "build"),
});
