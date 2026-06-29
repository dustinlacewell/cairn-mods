import { execFileSync } from "node:child_process";
import { readFileSync, readdirSync, writeFileSync, mkdirSync, rmSync, existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { homedir, tmpdir } from "node:os";
import { parse as parseYaml } from "yaml";

/** ── Generate site API reference from a mod's C# `///` comments ────────────────
 *
 * Parametric over any mod. Runs DocFX `metadata` (Roslyn under the hood) over the
 * mod's project to get a complete API model from its XML doc comments, then maps
 * that to the site's existing `DocSection[]` shape (mods.ts renders it unchanged).
 *
 * The opt-in rule: a member appears in the reference IFF it has a `///` <summary>.
 * Doc-comment presence is the single control — no allowlist — so undocumented
 * internals/patch classes stay out, and authoring a `///` is what publishes it.
 */

export const ROOT = resolve(import.meta.dirname, "..", "..");

// ── Shapes (mirror site/src/data/mods.ts) ─────────────────────────────────────

export interface ApiParam { name: string; type: string; description: string; optional?: boolean; }
export interface ApiEntry { signature: string; returns?: string; description: string; params?: ApiParam[]; }
export interface DocSection { id: string; title: string; intro?: string; entries?: ApiEntry[]; code?: { lang: string; src: string }; }

// ── DocFX YAML item shape (only the fields we read) ───────────────────────────

interface DocfxParam { id: string; type: string; description?: string; }
interface DocfxItem {
  uid: string;
  commentId: string;        // T:/M:/P:/F:/E:/... prefix encodes the kind
  id: string;
  parent?: string;
  name: string;             // csharp short name, e.g. "DefineMenu(string, CrossMenuModifier)"
  type: string;             // Class | Struct | Enum | Method | Property | Field | Event | Constructor
  summary?: string;
  syntax?: { content?: string; parameters?: DocfxParam[]; return?: { type?: string; description?: string } };
}

// ── Public entry point ────────────────────────────────────────────────────────

export interface ApiDocsResult { sections: DocSection[]; types: number; members: number; }

export function generateApiDocs(modName: string): ApiDocsResult {
  const proj = join(ROOT, "mods", modName, `${modName}.csproj`);
  if (!existsSync(proj)) throw new Error(`No csproj for ${modName} at ${proj}`);

  const work = join(tmpdir(), `cairn-apidocs-${modName}`);
  rmSync(work, { recursive: true, force: true });
  mkdirSync(work, { recursive: true });
  try {
    runDocfxMetadata(modName, work);
    const items = loadItems(join(work, "api"));
    return assembleSections(items);
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
}

/** Persist a mod's generated reference to site/src/data/api/<Mod>.json. */
export function writeApiDocs(modName: string, result: ApiDocsResult): string {
  const dir = join(ROOT, "site", "src", "data", "api");
  mkdirSync(dir, { recursive: true });
  const out = join(dir, `${modName}.json`);
  writeFileSync(out, JSON.stringify({ mod: modName, generator: "docfx", sections: result.sections }, null, 2) + "\n", "utf-8");
  return out;
}

// ── DocFX invocation ──────────────────────────────────────────────────────────

function runDocfxMetadata(modName: string, work: string): void {
  const docfx = resolveDocfx();
  const cfg = join(work, "docfx.json");
  // In CI, pass CI=true through so Directory.Build.props resolves references to the
  // committed game-refs/ rather than a local Steam install. Unset locally → local refs.
  const properties = process.env["CI"] ? { CI: process.env["CI"]! } : undefined;
  writeFileSync(cfg, JSON.stringify({
    metadata: [{
      src: [{ files: [`${modName}.csproj`], src: join(ROOT, "mods", modName).replace(/\\/g, "/") }],
      dest: "api",
      outputFormat: "mref",
      ...(properties ? { properties } : {}),
    }],
  }), "utf-8");
  execFileSync(docfx, ["metadata", cfg], { stdio: "pipe", cwd: work });
}

function resolveDocfx(): string {
  if (process.env["DOCFX"]) return process.env["DOCFX"]!;
  const ext = process.platform === "win32" ? ".exe" : "";
  const tool = join(homedir(), ".dotnet", "tools", `docfx${ext}`);
  if (existsSync(tool)) return tool;
  return "docfx"; // fall back to PATH
}

// ── YAML → items ──────────────────────────────────────────────────────────────

function loadItems(apiDir: string): DocfxItem[] {
  if (!existsSync(apiDir)) throw new Error(`DocFX produced no metadata at ${apiDir}`);
  const items: DocfxItem[] = [];
  for (const f of readdirSync(apiDir)) {
    if (!f.endsWith(".yml") || f === "toc.yml") continue;
    const doc = parseYaml(readFileSync(join(apiDir, f), "utf-8")) as { items?: DocfxItem[] };
    if (doc?.items) items.push(...doc.items);
  }
  return items;
}

// ── items → DocSection[] ──────────────────────────────────────────────────────

const TYPE_KINDS = new Set(["Class", "Struct", "Enum", "Interface"]);

// MelonLoader lifecycle callbacks — framework plumbing a mod overrides, never API a
// consumer calls. Excluded so a mod's `Core : MelonMod` doesn't pollute its reference.
const MELON_LIFECYCLE = new Set([
  "OnInitializeMelon", "OnDeinitializeMelon", "OnUpdate", "OnLateUpdate", "OnFixedUpdate",
  "OnGUI", "OnApplicationQuit", "OnApplicationStart", "OnApplicationLateStart",
  "OnSceneWasLoaded", "OnSceneWasInitialized", "OnSceneWasUnloaded",
  "OnPreferencesLoaded", "OnPreferencesSaved",
]);

/** Members that are public for mechanism but are not consumer API: constructors
 *  (construction is via factories / the system here) and MelonMod lifecycle overrides. */
function isPlumbing(i: DocfxItem): boolean {
  if (i.commentId.startsWith("M:") && i.id.startsWith("#ctor")) return true;
  return MELON_LIFECYCLE.has(i.id.replace(/\(.*$/, ""));
}

function assembleSections(items: DocfxItem[]): ApiDocsResult {
  const byUid = new Map(items.map((i) => [i.uid, i]));
  const types = items.filter((i) => TYPE_KINDS.has(i.type));

  const sections: DocSection[] = [];
  let memberCount = 0;

  for (const t of types) {
    const shortName = t.name;
    const isEnum = t.type === "Enum";
    // A member appears iff it has a `///` summary — except enum values, whose names
    // are self-documenting, so the enum's full value set is always listed.
    const members = items.filter(
      (i) =>
        i.parent === t.uid &&
        !TYPE_KINDS.has(i.type) &&
        i.id !== "value__" &&
        !isPlumbing(i) &&
        (i.summary || isEnum),
    );
    if (members.length === 0 && !t.summary) continue; // nothing documented here

    const entries = members.map((m) => toEntry(shortName, m)).filter((e): e is ApiEntry => e !== null);
    memberCount += entries.length;
    if (entries.length === 0 && !t.summary) continue;

    const { text, code } = splitSummary(t.summary);
    sections.push({
      id: shortName.toLowerCase(),
      title: shortName,
      ...(text ? { intro: text } : {}),
      ...(code ? { code: { lang: "csharp", src: code } } : {}),
      ...(entries.length ? { entries } : {}),
    });
  }
  void byUid;
  return { sections, types: sections.length, members: memberCount };
}

function toEntry(typeShort: string, m: DocfxItem): ApiEntry | null {
  const kind = m.commentId[0]; // M F P E T
  const baseName = m.id.replace(/\(.*$/, ""); // strip param list from id
  const hasParens = kind === "M"; // method/ctor take args; props/fields/events don't

  let signature: string;
  let params: ApiParam[] | undefined;
  if (hasParens) {
    const parsed = parseParams(m.syntax?.content ?? "", m.syntax?.parameters ?? []);
    params = parsed.length ? parsed : undefined;
    const shortList = parsed.map((p) => (p.optional ? `${p.name}?` : p.name)).join(", ");
    signature = `${typeShort}.${baseName}(${shortList})`;
  } else {
    signature = `${typeShort}.${baseName}`;
  }

  const returns = m.syntax?.return?.description ? clean(m.syntax.return.description) : undefined;
  const { text } = splitSummary(m.summary);
  return {
    signature,
    ...(returns ? { returns } : {}),
    description: text,
    ...(params ? { params } : {}),
  };
}

/** Parse a C# signature's parameter list into typed params, descriptions matched by name. */
function parseParams(content: string, documented: DocfxParam[]): ApiParam[] {
  const open = content.indexOf("(");
  const close = content.lastIndexOf(")");
  if (open < 0 || close < open) return [];
  const inner = content.slice(open + 1, close).trim();
  if (!inner) return [];

  const descOf = new Map(documented.map((p) => [p.id, p.description]));
  return splitTopLevel(inner).map((chunk) => {
    let s = chunk.trim().replace(/^\s*(?:ref|out|in|params|this)\s+/g, "");
    let optional = false;
    const eq = s.indexOf("=");
    if (eq >= 0) { optional = true; s = s.slice(0, eq).trim(); }
    const sp = s.lastIndexOf(" ");
    const name = sp >= 0 ? s.slice(sp + 1) : s;
    const type = sp >= 0 ? s.slice(0, sp).trim() : "";
    return { name, type, description: clean(descOf.get(name) ?? ""), ...(optional ? { optional } : {}) };
  });
}

/** Split a comma list at bracket depth 0 (so generics/tuples don't break it). */
function splitTopLevel(s: string): string[] {
  const out: string[] = [];
  let depth = 0, start = 0;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === "<" || c === "(" || c === "[") depth++;
    else if (c === ">" || c === ")" || c === "]") depth--;
    else if (c === "," && depth === 0) { out.push(s.slice(start, i)); start = i + 1; }
  }
  out.push(s.slice(start));
  return out.map((x) => x.trim()).filter(Boolean);
}

// ── Summary cleaning (DocFX emits HTML in summaries) ───────────────────────────

/** Split a summary into prose + the first fenced code example (if any). */
function splitSummary(summary?: string): { text: string; code: string | null } {
  if (!summary) return { text: "", code: null };
  let code: string | null = null;
  const stripped = summary.replace(/<pre><code[^>]*>([\s\S]*?)<\/code><\/pre>/i, (_, c) => {
    if (code === null) code = decode(c).trim();
    return "";
  });
  return { text: clean(stripped), code };
}

/** HTML/xref → plain prose. */
function clean(html: string): string {
  return decode(
    html
      .replace(/<xref[^>]*href="([^"]*)"[^>]*>(.*?)<\/xref>/gi, (_, href, inner) => inner || xrefName(href))
      .replace(/<xref[^>]*href="([^"]*)"[^>]*\/?>/gi, (_, href) => xrefName(href))
      .replace(/<\/?(p|code|pre|see|c)[^>]*>/gi, "")
      .replace(/<[^>]+>/g, ""),
  ).replace(/\s+/g, " ").trim();
}

/** A readable name from an xref href like "CairnAPI.CrossMenu.Install(HarmonyLib.Harmony)". */
function xrefName(href: string): string {
  const noParens = href.replace(/\(.*$/, "");
  const parts = noParens.split(".");
  return parts.slice(-2).join("."); // e.g. CrossMenu.Install
}

function decode(s: string): string {
  return s
    .replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'").replace(/&amp;/g, "&");
}
