import { resolve, dirname } from 'path';
import { statSync, writeFileSync } from 'fs';
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
 * Write text to a file, handling VC checkout and registration automatically.
 * Calls `prepareToWrite`, writes the file, then calls `finishedWrite`.
 * Works whether or not the file already exists.
 *
 * On failure, returns the result from whichever step failed.
 *
 * @param {string} filePath
 * @param {string} content
 * @param {BufferEncoding} [encoding='utf8']
 */
export function writeTextFile(filePath, content, encoding = 'utf8') {
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
 * On failure, returns the result from whichever step failed.
 *
 * @param {string} filePath
 * @param {Buffer | Uint8Array} data
 */
export function writeBinaryFile(filePath, data) {
  const prep = resolveProvider(filePath).prepareToWrite(filePath);
  if (!prep.success) return prep;
  try {
    writeFileSync(filePath, data);
  } catch (e) {
    return { success: false, status: 'error', message: e.message };
  }
  return resolveProvider(filePath).finishedWrite(filePath);
}
