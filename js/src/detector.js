import { existsSync, statSync } from 'fs';
import { join, dirname } from 'path';
import { runCommand } from './commandRunner.js';

// Maps a VCS root directory to its system name.
// Populated on first detection; avoids repeated directory walks for the same repo.
const _rootCache = new Map();

/** Clear the detection cache. Called when the provider override is cleared. */
export function clearDetectorCache() {
  _rootCache.clear();
}

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

  // Check whether any ancestor is a known VCS root before doing any I/O.
  let current = dir;
  while (true) {
    if (_rootCache.has(current)) return _rootCache.get(current);
    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }

  // Walk up looking for VC marker directories/files.
  current = dir;
  while (true) {
    if (existsSync(join(current, '.git'))) {
      _rootCache.set(current, 'git');
      return 'git';
    }
    if (existsSync(join(current, '.plastic'))) {
      _rootCache.set(current, 'plastic');
      return 'plastic';
    }
    if (existsSync(join(current, '.svn'))) {
      _rootCache.set(current, 'svn');
      return 'svn';
    }
    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }

  // Perforce has no marker directory; detect by running `p4 info`.
  const p4 = runCommand('p4', ['info'], { timeout: 3000 });
  if (p4.exitCode === 0 && p4.output.includes('Client name:')) return 'perforce';

  return 'filesystem';
}
