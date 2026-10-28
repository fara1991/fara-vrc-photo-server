// ワークフロー（.github/workflows/*.yml）の PowerShell（shell: pwsh）の run ブロックと、build/*.ps1 を、PowerShell の構文解析にかける（実行はしない）。
// release.yml の構文エラー（"$tag:" の ':' が変数名の一部と読まれる）で、Release が作れなかったことの再発防止。
// 使い方（Windows）: node build/check-workflow-powershell.mjs        … 全部を確かめる。エラーがあれば終了コード 1
// ワークフローや build/*.ps1 を変えたら、push の前に実行する（GitHub の式 ${{ ... }} は、ただの文字として扱う）。
import { spawnSync } from "node:child_process";
import { mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");

/** run: | のブロックを取り出す。shell は、run の前後の行の shell: から決める。 */
function runBlocks(yaml) {
  const lines = yaml.split(/\r?\n/);
  const blocks = [];
  for (let i = 0; i < lines.length; i++) {
    const m = /^(\s*)run:\s*\|\s*$/.exec(lines[i]);
    if (!m) continue;
    const indent = m[1].length;
    let shell = "";
    for (let k = Math.max(0, i - 3); k <= i + 1; k++) {
      const sm = /shell:\s*(\S+)/.exec(lines[k] ?? "");
      if (sm) shell = sm[1];
    }
    const body = [];
    for (let j = i + 1; j < lines.length; j++) {
      if (lines[j].trim() === "") {
        body.push("");
        continue;
      }
      if (lines[j].match(/^\s*/)[0].length <= indent) break;
      body.push(lines[j].slice(indent + 2));
    }
    blocks.push({ line: i + 1, shell, text: body.join("\n") });
  }
  return blocks;
}

/** PowerShell の構文エラー（無ければ空）。ファイルは UTF-8（BOM つき）で渡す。 */
function syntaxErrors(scriptText, dir) {
  const file = join(dir, "check.ps1");
  writeFileSync(file, "﻿" + scriptText);
  const command = [
    "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8",
    "$e = $null; $t = $null",
    `[void][System.Management.Automation.Language.Parser]::ParseFile('${file}', [ref]$t, [ref]$e)`,
    '$e | ForEach-Object { Write-Output ("  " + $_.Extent.StartLineNumber + " 行目: " + $_.Message) }',
  ].join("; ");
  const result = spawnSync("powershell", ["-NoProfile", "-Command", command], { encoding: "utf8" });
  if (result.error) throw result.error;
  return result.stdout.split(/\r?\n/).filter((l) => l.trim().length > 0);
}

const dir = mkdtempSync(join(tmpdir(), "fara-ps-check-"));
let failed = 0;
let checked = 0;
try {
  const workflows = join(root, ".github", "workflows");
  for (const name of readdirSync(workflows).filter((n) => n.endsWith(".yml"))) {
    for (const block of runBlocks(readFileSync(join(workflows, name), "utf8"))) {
      if (block.shell !== "pwsh" && block.shell !== "powershell") continue;
      checked++;
      // GitHub の式（${{ ... }}）は、PowerShell が読む前に GitHub が文字列として置き換えるので、ただの文字にする
      const errors = syntaxErrors(block.text.replace(/\$\{\{[^}]*\}\}/g, "GHEXPR"), dir);
      if (errors.length > 0) {
        failed++;
        console.error(`${name}（run: ${block.line} 行目）`);
        for (const e of errors) console.error(e);
      }
    }
  }
  for (const name of readdirSync(join(root, "build")).filter((n) => n.endsWith(".ps1"))) {
    checked++;
    const errors = syntaxErrors(readFileSync(join(root, "build", name), "utf8"), dir);
    if (errors.length > 0) {
      failed++;
      console.error(`build/${name}`);
      for (const e of errors) console.error(e);
    }
  }
} finally {
  rmSync(dir, { recursive: true, force: true });
}

console.log(`PowerShell の構文: ${checked} 件を確かめ、エラー ${failed} 件`);
process.exit(failed > 0 ? 1 : 0);
