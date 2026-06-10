// ---------------------------------------------------------------------------
// Publish @wildwinter/simple-vc-lib to BOTH registries in one go: npmjs and
// GitHub Packages. Each registry is guarded - if it already has this version,
// it is skipped - so the two stay in lockstep, reruns are safe, and a partial
// failure (e.g. one registry down / bad token) heals on the next run.
//
//   node scripts/publish.mjs [--dry-run] [registry-url ...]
//
// Auth comes from the user's ~/.npmrc: an npmjs publish token for
// registry.npmjs.org and a GitHub token WITH write:packages for
// npm.pkg.github.com. README/LICENSE are copied in from the repo root for the
// pack and removed afterwards (as the old inline script did).
// ---------------------------------------------------------------------------

import { execFileSync } from "node:child_process";
import { readFileSync, copyFileSync, rmSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const jsDir = dirname(dirname(fileURLToPath(import.meta.url)));
const rootDir = dirname(jsDir);
const pkg = JSON.parse(readFileSync(join(jsDir, "package.json"), "utf8"));

const args = process.argv.slice(2);
const dryRun = args.includes("--dry-run");
const registries = args.filter((a) => !a.startsWith("--"));
const targets = registries.length > 0 ? registries : [
  "https://registry.npmjs.org",
  "https://npm.pkg.github.com",
];

function alreadyPublished(registry) {
  try {
    execFileSync("npm", ["view", `${pkg.name}@${pkg.version}`, "version", `--registry=${registry}`], {
      cwd: jsDir, stdio: "pipe", encoding: "utf8",
    });
    return true;
  } catch {
    return false; // not found (or registry unreachable) - let publish surface real errors
  }
}

copyFileSync(join(rootDir, "README.md"), join(jsDir, "README.md"));
copyFileSync(join(rootDir, "LICENSE"), join(jsDir, "LICENSE"));

const failures = [];
try {
  for (const registry of targets) {
    if (alreadyPublished(registry)) {
      console.log(`${pkg.name}@${pkg.version} already on ${registry} - skipping.`);
      continue;
    }
    const publishArgs = ["publish", "--access", "public", `--registry=${registry}`];
    if (dryRun) publishArgs.push("--dry-run");
    console.log(`\npublishing ${pkg.name}@${pkg.version} -> ${registry}${dryRun ? " (dry run)" : ""}`);
    try {
      execFileSync("npm", publishArgs, { cwd: jsDir, stdio: "inherit" });
    } catch {
      failures.push(registry);
      console.error(`FAILED: ${registry} (continuing - rerun after fixing; published registries skip)`);
    }
  }
} finally {
  rmSync(join(jsDir, "README.md"), { force: true });
  rmSync(join(jsDir, "LICENSE"), { force: true });
}

if (failures.length > 0) {
  console.error(`\npublish incomplete: ${failures.join(", ")}`);
  process.exit(1);
}
console.log("\nall registries up to date.");
