#!/usr/bin/env node
// ИГРУШЕЧНЫЙ резолвер каскада определений. Не продакшн-код, не заготовка кода:
// исполняемая иллюстрация к 12-inventory-overlay-mechanics.md. Ноль зависимостей.
//   node resolve.mjs            — печатает резолв + отчёт валидатора, exit 1 при ошибке
//   node resolve.mjs --artifact worker — печатает итоговое тело артефакта роли
import { readdirSync, readFileSync, existsSync } from "node:fs";
import { join, basename } from "node:path";

const LAYERS = ["base", "user", "project"]; // порядок = приоритет, младший первым
const FIELDS = ["tier", "requiredCapabilities", "spawn", "escalation"];
const emitted = (slug) => `petbox-${slug}`;

function readLayer(dir) {
  const meta = JSON.parse(readFileSync(join(dir, "layer.json"), "utf8"));
  const entries = new Map(); // slug -> {patch, prose, append, files}
  for (const f of readdirSync(dir).sort()) {
    if (f === "layer.json") continue;
    const m = /^petbox-([a-z0-9-]+)\.(json|md|append\.md)$/.exec(f);
    if (!m) { console.error(`  E5 ${dir}/${f}: имя файла не по схеме petbox-<slug>.{json,md,append.md}`); process.exitCode = 1; continue; }
    const [, slug, kind] = m;
    const e = entries.get(slug) ?? { files: [] };
    e.files.push(f);
    const body = readFileSync(join(dir, f), "utf8");
    if (kind === "json") {
      e.patch = JSON.parse(body);
      if (e.patch.slug !== slug) { console.error(`  E5 ${dir}/${f}: slug "${e.patch.slug}" не совпадает с именем файла`); process.exitCode = 1; }
    } else if (kind === "md") e.prose = body.trimEnd();
    else e.append = body.trimEnd();
    entries.set(slug, e);
  }
  for (const [slug, e] of entries) {
    if (e.prose !== undefined && e.append !== undefined) {
      console.error(`  E4 ${dir}: роль ${slug} одновременно замещает прозу (.md) и дописывает (.append.md) — выберите одно`);
      process.exitCode = 1;
    }
  }
  return { dir, meta, entries };
}

const errors = [], warnings = [], trace = [];
let roles = new Map(); // slug -> {role, provenance}

for (const dir of LAYERS) {
  const L = readLayer(dir);
  if (L.meta.mode === "replace") { if (roles.size) trace.push(`${L.meta.name}: mode=replace — нижние слои ОТБРОШЕНЫ (${[...roles.keys()].join(", ")})`); roles = new Map(); }
  else if (L.meta.mode !== "overlay") { errors.push(`E0 ${dir}: неизвестный mode "${L.meta.mode}"`); }
  let touched = 0;
  for (const [slug, e] of L.entries) {
    const prev = roles.get(slug);
    if (e.patch?.removed === true) {
      if (!prev) { errors.push(`E2 ${L.meta.name}: тумбстоун роли "${slug}", которой нет ниже (переименована в базисе? слой протух)`); continue; }
      roles.delete(slug); touched++; trace.push(`${L.meta.name}: − ${slug} (тумбстоун)`); continue;
    }
    if (!prev) {
      const p = e.patch;
      if (!p) { errors.push(`E3 ${L.meta.name}: у новой роли "${slug}" есть проза, но нет .json`); continue; }
      const missing = ["tier", "requiredCapabilities"].filter((k) => p[k] === undefined);
      if (missing.length) { errors.push(`E3 ${L.meta.name}: новая роль "${slug}" неполна, нет: ${missing.join(", ")}`); continue; }
      roles.set(slug, { role: { slug, ...Object.fromEntries(FIELDS.filter((k)=>p[k]!==undefined).map((k)=>[k,p[k]])), notes: e.prose ?? "", addenda: e.append ? [[L.meta.name, e.append]] : [] }, from: { def: L.meta.name } });
      touched++; trace.push(`${L.meta.name}: + ${slug} (новая роль)`); continue;
    }
    const r = { ...prev.role }; const from = { ...prev.from }; const what = [];
    for (const k of FIELDS) if (e.patch?.[k] !== undefined) {
      if (JSON.stringify(r[k]) === JSON.stringify(e.patch[k])) warnings.push(`W3 ${L.meta.name}: ${slug}.${k} задано тем же значением — слой ничего не меняет`);
      r[k] = e.patch[k]; from[k] = L.meta.name; what.push(k);
    }
    if (e.prose !== undefined) {
      if (e.prose === r.notes) warnings.push(`W3 ${L.meta.name}: проза ${slug} побайтово равна нижней — это РЕПЛИКА, а не слой`);
      r.notes = e.prose; r.addenda = []; from.def = L.meta.name; what.push("проза (замещение целиком, дополнения сброшены)");
    }
    if (e.append !== undefined) { r.addenda = [...(r.addenda ?? []), [L.meta.name, e.append]]; what.push("проза (+дополнение)"); }
    if (!what.length) warnings.push(`W3 ${L.meta.name}: файл(ы) ${e.files.join(", ")} не меняют ничего`);
    else { roles.set(slug, { role: r, from }); touched++; trace.push(`${L.meta.name}: ~ ${slug} → ${what.join(", ")}`); }
  }
  if (!touched && L.meta.mode === "overlay") warnings.push(`W3 ${L.meta.name}: слой целиком не меняет состав — реплика, а не слой`);
}

// E1: ссылка на роль, которой нет ПОСЛЕ наложения
for (const [slug, { role }] of roles) {
  for (const t of role.spawn?.allowedRoles ?? []) if (!roles.has(t)) errors.push(`E1 ${slug}.spawn.allowedRoles → "${t}": роли нет в резолве`);
  for (const t of role.escalation?.targets ?? []) if (!roles.has(t)) errors.push(`E1 ${slug}.escalation.targets → "${t}": роли нет в резолве`);
}

const arg = process.argv.indexOf("--artifact");
if (arg > -1) {
  const { role, from } = roles.get(process.argv[arg + 1]) ?? {};
  if (!role) { console.error("нет такой роли в резолве"); process.exit(1); }
  const L = [`# ${emitted(role.slug)}`, "", `Tier: \`${role.tier}\``, "", role.notes];
  for (const [name, text] of role.addenda ?? []) L.push("", `## Дополнение слоя ${name}`, "", text);
  L.push("", "## Required capabilities", ...role.requiredCapabilities.map((c) => `- \`${c}\``));
  L.push("", "## Spawn", role.spawn?.allowed ? `- Allowed. Target roles: ${(role.spawn.allowedRoles ?? []).map((r)=>`\`${emitted(r)}\``).join(", ")}.` : "- Not allowed. This is a leaf role.");
  L.push("", "## Escalation", role.escalation?.available ? `- Available → ${(role.escalation.targets ?? []).map((t)=>`\`${emitted(t)}\``).join(", ")}.` : "- Not available.");
  L.push("", `<!-- provenance: ${Object.entries(from).map(([k, v]) => `${k}=${v}`).join(" ")} -->`);
  console.log(L.join("\n"));
} else {
  console.log("=== трасса резолва ===");     for (const t of trace) console.log("  " + t);
  console.log("\n=== состав после резолва ==="); for (const [s, { role, from }] of roles) console.log(`  ${emitted(s)}  tier=${role.tier}  provenance: ${Object.entries(from).map(([k,v])=>`${k}=${v}`).join(" ")}`);
  console.log("\n=== валидатор каскада ===");
  for (const w of warnings) console.log("  " + w);
  for (const e of errors) console.log("  " + e);
  if (!warnings.length && !errors.length) console.log("  чисто");
}
if (errors.length) process.exitCode = 1;
