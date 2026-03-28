import { existsSync, readFileSync } from 'fs';
import { join, dirname } from 'path';

const VALID_SYSTEMS = ['git', 'perforce', 'plastic', 'svn', 'filesystem'];

/**
 * Load explicit VC configuration, if any.
 * Checks the SIMPLE_VC environment variable first, then walks up from
 * startDir looking for a .vcconfig JSON file.
 *
 * .vcconfig format: { "system": "git" | "perforce" | "plastic" | "svn" | "filesystem" }
 *
 * @param {string} startDir - Directory to begin walking upward from.
 * @returns {{ system: string } | null}
 */
export function loadConfig(startDir) {
  const envSystem = process.env.SIMPLE_VC?.toLowerCase();
  if (envSystem && VALID_SYSTEMS.includes(envSystem)) {
    return { system: envSystem };
  }

  let dir = startDir;
  while (true) {
    const configPath = join(dir, '.vcconfig');
    if (existsSync(configPath)) {
      try {
        const parsed = JSON.parse(readFileSync(configPath, 'utf8'));
        const system = parsed?.system?.toLowerCase();
        if (system && VALID_SYSTEMS.includes(system)) {
          return { system };
        }
      } catch {
        // Malformed .vcconfig — ignore and fall through to auto-detection.
      }
    }
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }

  return null;
}
