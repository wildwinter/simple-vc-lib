import { resolve, dirname } from 'path';
import { statSync, writeFileSync, readFileSync, existsSync, mkdirSync } from 'fs';
import { loadConfig } from './config.js';
import { detectProvider, clearDetectorCache } from './detector.js';
import { GitProvider } from './providers/gitProvider.js';
import { PerforceProvider } from './providers/perforceProvider.js';
import { PlasticProvider } from './providers/plasticProvider.js';
import { SvnProvider } from './providers/svnProvider.js';
import { FilesystemProvider } from './providers/filesystemProvider.js';

const PROVIDER_MAP = {
  git: () => new GitProvider(),
  perforce: () => new PerforceProvider(),
  plastic: () => new PlasticProvider(),
  svn: () => new SvnProvider(),
  filesystem: () => new FilesystemProvider(),
};

/** @type {object | null} An explicitly set provider that bypasses auto-detection. */
let _overrideProvider = null;

/**
 * Override the provider used for all operations.
 * Useful for testing or in environments where auto-detection is unreliable.
 * Pass a provider instance, or null to clear the override.
 *
 * @param {object | null} provider
 */
export function setProvider(provider) {
  _overrideProvider = provider;
}

/** Clear any previously set provider override, restoring auto-detection. */
export function clearProvider() {
  _overrideProvider = null;
  clearDetectorCache();
}

function dirOf(p) {
  const abs = resolve(p);
  try {
    return statSync(abs).isDirectory() ? abs : dirname(abs);
  } catch {
    return dirname(abs);
  }
}

function resolveProvider(filePath) {
  if (_overrideProvider) return _overrideProvider;

  const dir = dirOf(filePath);
  const config = loadConfig(dir);
  if (config) {
    const factory = PROVIDER_MAP[config.system];
    if (factory) return factory();
  }

  const detected = detectProvider(resolve(filePath));
  return (PROVIDER_MAP[detected] ?? PROVIDER_MAP.filesystem)();
}

/**
 * Prepare a file path for writing.
 * Checks out or unlocks the file in VC if it is read-only.
 * No-op if the file does not yet exist.
 *
 * On failure, `status` may be 'locked', 'outOfDate', or 'error'.
 *
 * @param {string} filePath
 */
export function prepareToWrite(filePath) {
  return resolveProvider(filePath).prepareToWrite(filePath);
}

/**
 * Notify the library that a file has been written.
 * Adds the file to VC if it is not yet tracked. No-op for existing tracked files.
 *
 * @param {string} filePath
 */
export function finishedWrite(filePath) {
  return resolveProvider(filePath).finishedWrite(filePath);
}

/**
 * Delete a file, marking it for deletion in VC if tracked.
 *
 * @param {string} filePath
 */
export function deleteFile(filePath) {
  return resolveProvider(filePath).deleteFile(filePath);
}

/**
 * Delete a folder and all its contents, marking tracked files for deletion in VC.
 *
 * @param {string} folderPath
 */
export function deleteFolder(folderPath) {
  return resolveProvider(folderPath).deleteFolder(folderPath);
}

/**
 * Rename a file, informing VC of the change if the file is tracked.
 * No-op if the source does not exist.
 *
 * @param {string} oldPath
 * @param {string} newPath
 */
export function renameFile(oldPath, newPath) {
  return resolveProvider(oldPath).renameFile(oldPath, newPath);
}

/**
 * Rename a folder, informing VC of the change for all tracked contents.
 * No-op if the source does not exist.
 *
 * @param {string} oldPath
 * @param {string} newPath
 */
export function renameFolder(oldPath, newPath) {
  return resolveProvider(oldPath).renameFolder(oldPath, newPath);
}

/**
 * Write text to a file, handling VC checkout and registration automatically.
 * Calls `prepareToWrite`, writes the file, then calls `finishedWrite`.
 * Works whether or not the file already exists.
 *
 * If the file already exists and its content matches `content`, no VCS operations
 * are performed and the file is not written. Set `forceWrite` to `true` to skip
 * this check and always write.
 *
 * On failure, returns the result from whichever step failed.
 *
 * @param {string} filePath
 * @param {string} content
 * @param {BufferEncoding} [encoding='utf8']
 * @param {boolean} [forceWrite=false]
 */
export function writeTextFile(filePath, content, encoding = 'utf8', forceWrite = false) {
  if (!forceWrite && existsSync(filePath)) {
    try {
      const existing = readFileSync(filePath, { encoding });
      if (existing === content) return { success: true, status: 'ok', message: '' };
    } catch {
      // If the file can't be read, fall through to the normal write path.
    }
  }
  const prep = resolveProvider(filePath).prepareToWrite(filePath);
  if (!prep.success) return prep;
  try {
    writeFileSync(filePath, content, { encoding });
  } catch (e) {
    return { success: false, status: 'error', message: e.message };
  }
  return resolveProvider(filePath).finishedWrite(filePath);
}

/**
 * Write binary data to a file, handling VC checkout and registration automatically.
 * Calls `prepareToWrite`, writes the file, then calls `finishedWrite`.
 * Works whether or not the file already exists.
 *
 * If the file already exists and its content matches `data`, no VCS operations
 * are performed and the file is not written. Set `forceWrite` to `true` to skip
 * this check and always write.
 *
 * On failure, returns the result from whichever step failed.
 *
 * @param {string} filePath
 * @param {Buffer | Uint8Array} data
 * @param {boolean} [forceWrite=false]
 */
export function writeBinaryFile(filePath, data, forceWrite = false) {
  if (!forceWrite && existsSync(filePath)) {
    try {
      const existing = readFileSync(filePath);
      const incoming = Buffer.isBuffer(data) ? data : Buffer.from(data);
      if (existing.equals(incoming)) return { success: true, status: 'ok', message: '' };
    } catch {
      // If the file can't be read, fall through to the normal write path.
    }
  }
  const prep = resolveProvider(filePath).prepareToWrite(filePath);
  if (!prep.success) return prep;
  try {
    writeFileSync(filePath, data);
  } catch (e) {
    return { success: false, status: 'error', message: e.message };
  }
  return resolveProvider(filePath).finishedWrite(filePath);
}

/**
 * Write a batch of text files through VC, creating parent directories, and
 * report EVERY outcome - a refused write comes back with its why ("locked by
 * bob@bob-ws"), never a bare EACCES, and one refusal does not stop the rest.
 * Each write goes through `writeTextFile` (prepare -> write -> finished, with
 * the unchanged-content short-circuit).
 *
 * @param {{filePath: string, content: string}[]} files
 * @param {BufferEncoding} [encoding='utf8']
 * @returns {{success: boolean, results: Array<{filePath: string, success: boolean, status: import('./vcResult.js').VCStatus, message: string}>}}
 */
export function writeTextFiles(files, encoding = 'utf8') {
  const results = files.map(({ filePath, content }) => {
    try {
      mkdirSync(dirname(resolve(filePath)), { recursive: true });
    } catch (e) {
      return { filePath, success: false, status: 'error', message: e.message };
    }
    const result = writeTextFile(filePath, content, encoding);
    return { filePath, success: result.success, status: result.status, message: result.message };
  });
  return { success: results.every((r) => r.success), results };
}

/**
 * Status for a batch of files: tracked / writable / locked-by / opened-by-me /
 * out-of-date, per file. Paths are grouped by provider so a whole project
 * costs a spawn or two, not one per file (Perforce: ONE `p4 -ztag fstat`; git:
 * one `git status` + one `git lfs locks` per repository). The writable bit is
 * always reported - in lock-based workflows it is the cheap local signal for
 * "is this editable right now?".
 *
 * @param {string[]} filePaths
 * @returns {import('./vcStatus.js').VCFileStatus[]}
 */
export function fileStatus(filePaths) {
  const groups = new Map();
  for (const filePath of filePaths) {
    const provider = resolveProvider(filePath);
    if (!groups.has(provider.name)) groups.set(provider.name, { provider, paths: [] });
    groups.get(provider.name).paths.push(filePath);
  }

  const byInput = new Map();
  for (const { provider, paths } of groups.values()) {
    const statuses = provider.status(paths);
    for (let i = 0; i < paths.length; i++) byInput.set(paths[i], statuses[i]);
  }
  return filePaths.map((filePath) => byInput.get(filePath));
}
