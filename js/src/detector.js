import { existsSync, statSync } from 'fs';
import { join, dirname } from 'path';
import { runCommand } from './commandRunner.js';

/**
 * Auto-detect the active version control system for a given file path.
 * Walks up the directory tree looking for VC markers, then checks for Perforce.
 * Falls back to 'filesystem' if nothing is found.
 *
 * @param {string} absPath - Absolute path of the file (may not exist yet).
 * @returns {'git' | 'plastic' | 'svn' | 'perforce' | 'filesystem'}
 */
export function detectProvider(absPath) {
  let dir;
  try {
    dir = statSync(absPath).isDirectory() ? absPath : dirname(absPath);
  } catch {
    // Path doesn't exist yet — use its parent directory.
    dir = dirname(absPath);
  }

  let current = dir;
  while (true) {
    if (existsSync(join(current, '.git'))) return 'git';
    if (existsSync(join(current, '.plastic'))) return 'plastic';
    if (existsSync(join(current, '.svn'))) return 'svn';

    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }

  // Perforce has no marker directory; detect by running `p4 info`.
  const p4 = runCommand('p4', ['info'], { timeout: 3000 });
  if (p4.exitCode === 0 && p4.output.includes('Client name:')) return 'perforce';

  return 'filesystem';
}
