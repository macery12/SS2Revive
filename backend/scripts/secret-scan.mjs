import { readdir, readFile } from "node:fs/promises";
import path from "node:path";
import process from "node:process";

const root = path.resolve(import.meta.dirname, "..");
const ignoredDirectories = new Set(["node_modules", ".wrangler", "coverage", "dist", ".git"]);
const ignoredFiles = new Set(["pnpm-lock.yaml", "secret-scan.mjs", ".dev.vars", ".env"]);
const findings = [];
const patterns = [
  ["private key", /-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----/],
  ["AWS-style access key", /\b(?:AKIA|ASIA)[A-Z0-9]{16}\b/],
  ["Cloudflare API token assignment", /\b(?:CLOUDFLARE_API_TOKEN|CF_API_TOKEN)\s*=\s*[^\s<{][^\r\n]*/i],
  ["Access client secret assignment", /\bCF_ACCESS_CLIENT_SECRET\s*=\s*[^\s<{][^\r\n]*/i],
  ["backend secret assignment", /\b(?:LOCAL_AUTH_SECRET|AUTH_SIGNING_SECRET|STEAM_API_KEY|R2_ACCESS_KEY_ID|R2_SECRET_ACCESS_KEY)\s*=\s*[A-Za-z0-9+/_-]{32,}={0,2}(?![A-Za-z0-9+/_=-])/i],
  ["GitHub token", /\bgh(?:p|o|u|s|r)_[A-Za-z0-9]{30,}\b/],
];

async function walk(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (entry.isDirectory() && ignoredDirectories.has(entry.name)) continue;
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      await walk(fullPath);
      continue;
    }
    if (!entry.isFile() || ignoredFiles.has(entry.name)) continue;
    // Local environment files are supposed to contain secrets and are ignored by Git. Keep
    // scanning example/template variants, where only visibly delimited placeholders are allowed.
    if (entry.name.startsWith(".env.") && !entry.name.endsWith(".example")) continue;
    const relative = path.relative(root, fullPath);
    const text = await readFile(fullPath, "utf8").catch(() => null);
    if (text === null) continue;
    for (const [label, pattern] of patterns) {
      if (pattern.test(text)) findings.push(`${relative}: ${label}`);
    }
  }
}

await walk(root);
if (findings.length > 0) {
  console.error("Potential committed secrets found:\n" + findings.join("\n"));
  process.exitCode = 1;
} else {
  console.log("Secret scan passed.");
}
