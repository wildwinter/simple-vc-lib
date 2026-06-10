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
