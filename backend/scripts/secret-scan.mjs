// Last check before anything reaches GitHub. Intended to run from the pre-push hook in
// .githooks/pre-push, not from CI: by the time CI sees a commit the secret is already published,
// and a red build cannot unpublish it.
//
// Scope is every file Git tracks, resolved from the repository root rather than from this script's
// parent. An earlier version walked `backend/` only, which silently excluded src/, tools/, tests/,
// assets/ and the workflow files. Asking Git for the file list also means ignored working files -
// .dev.vars, .env.production, wrangler.production.jsonc - are never read, so the real secrets on
// this machine cannot produce a finding and train someone to skip the hook.
//
// Known gap: this looks at the working tree, not at history. A secret that was committed and then
// deleted still ships in the objects being pushed. Removing that needs a history rewrite, which is
// not something a hook should attempt.

import { readFile } from "node:fs/promises";
import { execFileSync } from "node:child_process";
import path from "node:path";
import process from "node:process";

const root = execFileSync("git", ["rev-parse", "--show-toplevel"], { encoding: "utf8" }).trim();

/** Generated, machine-written, or the scanner's own pattern list. */
const ignoredFiles = new Set(["pnpm-lock.yaml", "secret-scan.mjs"]);

/** Names whose assigned value is a credential in every form this repository writes them. */
const secretNames = [
  "LOCAL_AUTH_SECRET",
  "AUTH_SIGNING_SECRET",
  "STEAM_API_KEY",
  "R2_ACCESS_KEY_ID",
  "R2_SECRET_ACCESS_KEY",
  "CLOUDFLARE_API_TOKEN",
  "CF_API_TOKEN",
  "CF_ACCESS_CLIENT_ID",
  "CF_ACCESS_CLIENT_SECRET",
].join("|");

// Both spellings the repository can produce: `NAME=value` in .dev.vars and shell, and
// `"NAME": "value"` in the wrangler configs and any JSON that gets pasted into an example file.
const assignmentForms = [
  new RegExp(String.raw`\b(?:${secretNames})\s*=\s*"?([^\s"'\r\n]+)`, "gi"),
  new RegExp(String.raw`"(?:${secretNames})"\s*:\s*"([^"\r\n]+)"`, "gi"),
];

/** Self-identifying credentials, where the shape alone is the finding. */
const literalPatterns = [
  ["private key", /-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----/],
  ["AWS-style access key", /\b(?:AKIA|ASIA)[A-Z0-9]{16}\b/],
  ["GitHub token", /\bgh(?:p|o|u|s|r)_[A-Za-z0-9]{30,}\b/],
  ["GitHub fine-grained token", /\bgithub_pat_[A-Za-z0-9_]{60,}\b/],
  ["Slack token", /\bxox[baprs]-[A-Za-z0-9-]{10,}\b/],
  ["Cloudflare Global API key", /\b[a-f0-9]{37}\b/],
];

/**
 * Distinguishes a real credential from the placeholders the example files are supposed to contain.
 *
 * Generated secrets here are base64 or hex over random bytes, so they mix character classes.
 * Placeholders are English written in one case: `<generate-a-local-secret...>`,
 * `replace-with-your-token`. Requiring two of upper/lower/digit rejects those without needing a
 * list of placeholder spellings to keep up to date.
 */
function looksLikeSecret(value) {
  if (value.length < 24) return false;
  if (/^[<{$%]/.test(value) || value.includes("${")) return false;
  if (!/^[A-Za-z0-9+/_=-]+$/.test(value)) return false;
  if (/^[a-f0-9]{32,}$/i.test(value)) return true;
  const classes = [/[A-Z]/, /[a-z]/, /[0-9]/].filter((pattern) => pattern.test(value)).length;
  return classes >= 2;
}

function scan(relative, text, findings) {
  for (const [label, pattern] of literalPatterns) {
    if (pattern.test(text)) findings.push(`${relative}: ${label}`);
  }
  for (const pattern of assignmentForms) {
    pattern.lastIndex = 0;
    for (const match of text.matchAll(pattern)) {
      if (looksLikeSecret(match[1] ?? "")) findings.push(`${relative}: assigned secret value`);
    }
  }
}

const tracked = execFileSync("git", ["-C", root, "ls-files", "-z"], {
  encoding: "utf8",
  maxBuffer: 64 * 1024 * 1024,
})
  .split("\0")
  .filter((entry) => entry !== "");

const findings = [];
for (const relative of tracked) {
  if (ignoredFiles.has(path.basename(relative))) continue;
  const text = await readFile(path.join(root, relative), "utf8").catch(() => null);
  // Unreadable, deleted since `ls-files`, or binary - a NUL byte means the pattern list cannot say
  // anything useful about it anyway.
  if (text === null || text.includes("\0")) continue;
  scan(relative, text, findings);
}

if (findings.length > 0) {
  console.error(
    `Potential secrets found in ${findings.length} place(s). Do not push until these are resolved:\n` +
      [...new Set(findings)].join("\n"),
  );
  process.exitCode = 1;
} else {
  console.log(`Secret scan passed (${tracked.length} tracked files).`);
}
