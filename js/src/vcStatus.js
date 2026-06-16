import { statSync } from 'fs';

/**
 * @typedef {object} VCFileStatus
 * @property {string} filePath - Absolute path of the file.
 * @property {'git' | 'perforce' | 'plastic' | 'svn' | 'filesystem'} system
 * @property {boolean} writable - Writable on disk right now (the read-only bit;
 *   lock-based workflows key off this - a synced, unopened Perforce file is read-only).
 * @property {boolean} [tracked] - Known to the VCS (tracked / in the depot).
 *   Undefined when the provider cannot say.
 * @property {boolean} [openedByMe] - Opened / checked out / locked by the current user.
 * @property {string[]} [lockedBy] - Who else has it open or locked (e.g. "bob@bob-ws").
 * @property {boolean} [outOfDate] - A newer revision exists on the server.
 * @property {boolean} [dirty] - Has pending local VC changes: a tracked file that is
 *   modified / staged / opened / added / deleted but not yet committed. Untracked files
 *   are NOT dirty (they surface via `tracked: false`). This is the cheap, local notion -
 *   it does not detect a file edited outside VC (e.g. a Perforce file made writable and
 *   changed without being opened). Undefined when the provider cannot say.
 */

/**
 * Options for a status read.
 *
 * @typedef {object} VCStatusOptions
 * @property {boolean} [remote] - Permit a server round-trip to fetch `lockedBy` /
 *   `outOfDate` where the provider needs one (SVN: `svn status -u`; Plastic:
 *   `cm fileinfo`). Default false keeps the read local where possible. Providers
 *   that already carry that data for free (Perforce, git-LFS) ignore this and
 *   report it either way.
 */

/**
 * The read-only bit: cheap, local, and the primary editability signal under
 * lock-based workflows. A file not on disk yet counts as writable.
 *
 * @param {string} filePath
 * @returns {boolean}
 */
export function writableBit(filePath) {
  try {
    return (statSync(filePath).mode & 0o200) !== 0;
  } catch {
    return true;
  }
}
