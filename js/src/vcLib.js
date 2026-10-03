import { resolve, dirname } from 'path';
import { statSync, writeFileSync, readFileSync, existsSync, mkdirSync } from 'fs';
import { writeFile, readFile, mkdir } from 'fs/promises';
import { loadConfig } from './config.js';
import { okResult, errorResult } from './vcResult.js';
import { detectProvider, clearDetectorCache } from './detector.js';
import { GitProvider, clearTrackedCache } from './providers/gitProvider.js';
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
  clearTrackedCache();
}

/**
 * Forget everything cached about the working copy: which VCS is where, and
 * which paths git has in its index.
 *
 * A long-running tool should call this when the ground moves underneath it - a
 * project closed and another opened, a working copy re-cloned - rather than
 * relying on process lifetime. Both caches are safe to lose: the next question
 * asks the VCS again.
 */
export function clearVcCaches() {
  clearDetectorCache();
  clearTrackedCache();
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

/** Async twin of {@link deleteFile}. @param {string} filePath */
export function deleteFileAsync(filePath) {
  return resolveProvider(filePath).deleteFileAsync(filePath);
}

/** Async twin of {@link deleteFolder}. @param {string} folderPath */
export function deleteFolderAsync(folderPath) {
  return resolveProvider(folderPath).deleteFolderAsync(folderPath);
}

/** Async twin of {@link renameFile}. @param {string} oldPath @param {string} newPath */
export function renameFileAsync(oldPath, newPath) {
  return resolveProvider(oldPath).renameFileAsync(oldPath, newPath);
}

/** Async twin of {@link renameFolder}. @param {string} oldPath @param {string} newPath */
export function renameFolderAsync(oldPath, newPath) {
  return resolveProvider(oldPath).renameFolderAsync(oldPath, newPath);
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
  // One resolution for both halves of the write: it is the same question,
  // and asking twice was two directory walks (and once, two `p4 info` spawns).
  const provider = resolveProvider(filePath);
  const prep = provider.prepareToWrite(filePath);
  if (!prep.success) return prep;
  try {
    writeFileSync(filePath, content, { encoding });
  } catch (e) {
    return { success: false, status: 'error', message: e.message };
  }
  return provider.finishedWrite(filePath);
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
  // One resolution for both halves of the write: it is the same question,
  // and asking twice was two directory walks (and once, two `p4 info` spawns).
  const provider = resolveProvider(filePath);
  const prep = provider.prepareToWrite(filePath);
  if (!prep.success) return prep;
  try {
    writeFileSync(filePath, data);
  } catch (e) {
    return { success: false, status: 'error', message: e.message };
  }
  return provider.finishedWrite(filePath);
}

/**
 * Write a batch of text files through VC, creating parent directories, and
 * report EVERY outcome - a refused write comes back with its why ("locked by
 * bob@bob-ws"), never a bare EACCES, and one refusal does not stop the rest.
 * Each write goes through `writeTextFile` (prepare -> write -> finished, with
 * the unchanged-content short-circuit).
 *
 * With `options.allOrNothing`, the batch is checked out as a whole before anything
 * is written: files whose content is already right are skipped (they need no
 * checkout), parent directories are created, then {@link prepareToWriteFiles}
 * prepares the rest. If any file cannot be prepared, nothing is written and nothing
 * is left checked out. Only once every file is prepared are they written and
 * `finishedWrite` called on each. VC cannot make the disk writes themselves atomic,
 * so a write or `finishedWrite` that fails at that last stage is reported against
 * its own file and the others still go ahead.
 *
 * @param {{filePath: string, content: string}[]} files
 * @param {BufferEncoding} [encoding='utf8']
 * @param {{allOrNothing?: boolean}} [options]
 * @returns {{success: boolean, results: Array<{filePath: string, success: boolean, status: import('./vcResult.js').VCStatus, message: string}>}}
 */
export function writeTextFiles(files, encoding = 'utf8', options = {}) {
  if (options.allOrNothing) return writeTextFilesAllOrNothing(files, encoding);
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

/** Async twin of {@link prepareToWrite}. @param {string} filePath */
export function prepareToWriteAsync(filePath) {
  return resolveProvider(filePath).prepareToWriteAsync(filePath);
}

/** Async twin of {@link finishedWrite}. @param {string} filePath */
export function finishedWriteAsync(filePath) {
  return resolveProvider(filePath).finishedWriteAsync(filePath);
}

/**
 * Async twin of {@link writeTextFile}.
 *
 * @param {string} filePath
 * @param {string} content
 * @param {BufferEncoding} [encoding='utf8']
 * @param {boolean} [forceWrite=false]
 */
export async function writeTextFileAsync(filePath, content, encoding = 'utf8', forceWrite = false) {
  if (!forceWrite && existsSync(filePath)) {
    try {
      const existing = await readFile(filePath, { encoding });
      if (existing === content) return { success: true, status: 'ok', message: '' };
    } catch {
      // If the file can't be read, fall through to the normal write path.
    }
  }
  // One resolution for both halves; see the sync twin.
  const provider = resolveProvider(filePath);
  const prep = await provider.prepareToWriteAsync(filePath);
  if (!prep.success) return prep;
  try {
    await writeFile(filePath, content, { encoding });
  } catch (e) {
    return { success: false, status: 'error', message: e.message };
  }
  return provider.finishedWriteAsync(filePath);
}

/**
 * Async twin of {@link writeBinaryFile}.
 *
 * @param {string} filePath
 * @param {Buffer | Uint8Array} data
 * @param {boolean} [forceWrite=false]
 */
export async function writeBinaryFileAsync(filePath, data, forceWrite = false) {
  if (!forceWrite && existsSync(filePath)) {
    try {
      const existing = await readFile(filePath);
      const incoming = Buffer.isBuffer(data) ? data : Buffer.from(data);
      if (existing.equals(incoming)) return { success: true, status: 'ok', message: '' };
    } catch {
      // If the file can't be read, fall through to the normal write path.
    }
  }
  // One resolution for both halves; see the sync twin.
  const provider = resolveProvider(filePath);
  const prep = await provider.prepareToWriteAsync(filePath);
  if (!prep.success) return prep;
  try {
    await writeFile(filePath, data);
  } catch (e) {
    return { success: false, status: 'error', message: e.message };
  }
  return provider.finishedWriteAsync(filePath);
}

/**
 * Async twin of {@link writeTextFiles}. Files are processed sequentially - VC checkout
 * commands on one workspace are not safe to run concurrently - so behaviour matches the
 * sync version exactly.
 *
 * @param {{filePath: string, content: string}[]} files
 * @param {BufferEncoding} [encoding='utf8']
 * @param {{allOrNothing?: boolean}} [options]
 */
export async function writeTextFilesAsync(files, encoding = 'utf8', options = {}) {
  if (options.allOrNothing) return writeTextFilesAllOrNothingAsync(files, encoding);
  const results = [];
  for (const { filePath, content } of files) {
    try {
      await mkdir(dirname(resolve(filePath)), { recursive: true });
    } catch (e) {
      results.push({ filePath, success: false, status: 'error', message: e.message });
      continue;
    }
    const result = await writeTextFileAsync(filePath, content, encoding);
    results.push({ filePath, success: result.success, status: result.status, message: result.message });
  }
  return { success: results.every((r) => r.success), results };
}

// ---------------------------------------------------------------------------
// All-or-nothing batch checkout
// ---------------------------------------------------------------------------

/** One file's outcome, without its path: the shape a batch stores per unique file. */
function outcomeOf(result) {
  return { success: result.success, status: result.status, message: result.message };
}

/**
 * The batch's paths once each, keyed by absolute path so `a.txt` and `/wc/a.txt`
 * are one file. The first spelling seen is the one handed to the provider.
 */
function uniqueByPath(filePaths) {
  const unique = new Map();
  for (const filePath of filePaths) {
    const key = resolve(filePath);
    if (!unique.has(key)) unique.set(key, filePath);
  }
  return [...unique.values()];
}

/** One outcome per INPUT path, in input order, looked up by absolute path. */
function reportBatch(filePaths, byKey) {
  const results = filePaths.map((filePath) => ({ filePath, ...byKey.get(resolve(filePath)) }));
  return { success: results.every((r) => r.success), results };
}

/**
 * The preflight verdict: a refusal for every existing file someone else holds or
 * that is behind the server, and a "not prepared" for everything else. Null when
 * nothing is refused and the batch can go ahead.
 */
function preflightRefusals(unique, statuses) {
  const byKey = new Map();
  let refused = false;
  unique.forEach((filePath, i) => {
    const status = statuses[i];
    if (!existsSync(filePath) || !status) return;
    if (status.lockedBy?.length > 0) {
      byKey.set(resolve(filePath), errorResult('locked', `'${filePath}' is locked by ${status.lockedBy.join(', ')}`));
      refused = true;
    } else if (status.outOfDate) {
      byKey.set(resolve(filePath), errorResult('outOfDate', `'${filePath}' is out of date; get the latest revision before editing`));
      refused = true;
    }
  });
  if (!refused) return null;
  for (const filePath of unique) {
    const key = resolve(filePath);
    if (!byKey.has(key))
      byKey.set(key, errorResult('error', `'${filePath}' was not prepared because another file in the batch was refused`));
  }
  return byKey;
}

/** What an undone path reports: the undo, and whether the undo itself worked. */
function undoneOutcome(filePath, undo) {
  if (undo.success)
    return errorResult('error', `Checkout of '${filePath}' was undone because another file in the batch failed`);
  return errorResult('error', `Checkout of '${filePath}' could not be undone after another file in the batch failed: ${undo.message}`);
}

/** Run a provider's optional undo, turning a missing method into "nothing to undo" and a throw into a result. */
function undoOne(provider, filePath, before) {
  try {
    return provider.undoPrepareToWrite?.(filePath, before) ?? okResult();
  } catch (e) {
    return errorResult('error', e.message);
  }
}

/** Async twin of {@link undoOne}. Falls back to the sync undo when a provider has only that. */
async function undoOneAsync(provider, filePath, before) {
  try {
    if (provider.undoPrepareToWriteAsync) return await provider.undoPrepareToWriteAsync(filePath, before);
    return provider.undoPrepareToWrite?.(filePath, before) ?? okResult();
  } catch (e) {
    return errorResult('error', e.message);
  }
}

/**
 * Prepare a whole batch of files for writing, or none of them.
 *
 * 1. One batched status read over every path (`fileStatus` with `{ remote: true }`,
 *    so SVN and Plastic ask the server who holds what). Any file on disk that is
 *    `lockedBy` someone is refused as 'locked', naming the holders; any that is
 *    `outOfDate` is refused as 'outOfDate'. One refusal means nothing is checked out.
 * 2. Otherwise each file is prepared in turn with the provider's `prepareToWrite`,
 *    sequentially, since VC commands on one workspace are not safe to run concurrently.
 * 3. If one fails, every file this call already prepared is undone, newest first, with
 *    the provider's `undoPrepareToWrite`. A failed undo is reported in that file's
 *    message, never thrown.
 *
 * Under Perforce, `lockedBy` lists everyone else who has the file OPEN, not only
 * those holding an exclusive lock, so another user's plain `p4 edit` refuses the
 * batch. That is deliberate: it is what hosts already show as "locked by".
 *
 * An undo only reverses what this call did. A file the user already had open,
 * checked out or locked (`openedByMe`) is never reverted, so after a failure it is
 * left exactly as it was found.
 *
 * Each input path gets one outcome, in input order (duplicates are prepared once
 * and reported for each spelling).
 *
 * @param {string[]} filePaths
 * @returns {{success: boolean, results: Array<{filePath: string, success: boolean, status: import('./vcResult.js').VCStatus, message: string}>}}
 */
export function prepareToWriteFiles(filePaths) {
  const unique = uniqueByPath(filePaths);
  const statuses = unique.length > 0 ? fileStatus(unique, { remote: true }) : [];
  const refused = preflightRefusals(unique, statuses);
  if (refused) return reportBatch(filePaths, refused);

  const byKey = new Map();
  const prepared = [];
  for (let i = 0; i < unique.length; i++) {
    const filePath = unique[i];
    const provider = resolveProvider(filePath);
    const result = provider.prepareToWrite(filePath);
    if (result.success) {
      byKey.set(resolve(filePath), outcomeOf(result));
      prepared.push({ filePath, provider, before: statuses[i] });
      continue;
    }
    byKey.set(resolve(filePath), outcomeOf(result));
    for (const done of prepared.reverse())
      byKey.set(resolve(done.filePath), undoneOutcome(done.filePath, undoOne(done.provider, done.filePath, done.before)));
    for (const unreached of unique.slice(i + 1))
      byKey.set(resolve(unreached), errorResult('error', `'${unreached}' was not prepared because another file in the batch failed`));
    break;
  }
  return reportBatch(filePaths, byKey);
}

/**
 * Async twin of {@link prepareToWriteFiles}. The status read runs concurrently across
 * providers, as `fileStatusAsync` does; the checkouts and undos stay sequential.
 *
 * @param {string[]} filePaths
 */
export async function prepareToWriteFilesAsync(filePaths) {
  const unique = uniqueByPath(filePaths);
  const statuses = unique.length > 0 ? await fileStatusAsync(unique, { remote: true }) : [];
  const refused = preflightRefusals(unique, statuses);
  if (refused) return reportBatch(filePaths, refused);

  const byKey = new Map();
  const prepared = [];
  for (let i = 0; i < unique.length; i++) {
    const filePath = unique[i];
    const provider = resolveProvider(filePath);
    const result = await provider.prepareToWriteAsync(filePath);
    if (result.success) {
      byKey.set(resolve(filePath), outcomeOf(result));
      prepared.push({ filePath, provider, before: statuses[i] });
      continue;
    }
    byKey.set(resolve(filePath), outcomeOf(result));
    for (const done of prepared.reverse())
      byKey.set(resolve(done.filePath), undoneOutcome(done.filePath, await undoOneAsync(done.provider, done.filePath, done.before)));
    for (const unreached of unique.slice(i + 1))
      byKey.set(resolve(unreached), errorResult('error', `'${unreached}' was not prepared because another file in the batch failed`));
    break;
  }
  return reportBatch(filePaths, byKey);
}

/** True when the file is already on disk with exactly this content (the `writeTextFile` short-circuit). */
function contentUnchanged(filePath, content, encoding) {
  if (!existsSync(filePath)) return false;
  try {
    return readFileSync(filePath, { encoding }) === content;
  } catch {
    return false; // Unreadable: write it the normal way.
  }
}

/** Async twin of {@link contentUnchanged}. */
async function contentUnchangedAsync(filePath, content, encoding) {
  if (!existsSync(filePath)) return false;
  try {
    return (await readFile(filePath, { encoding })) === content;
  } catch {
    return false;
  }
}

/** A batch that stopped before anything was written: failures keep their own outcome, the rest say why they were left. */
function abandonBatch(files, results, pending) {
  for (const i of pending) {
    if (results[i]) continue;
    const filePath = files[i].filePath;
    results[i] = errorResult('error', `'${filePath}' was not written because another file in the batch could not be prepared`);
  }
  return finishBatch(files, results);
}

/** One outcome per input file, in input order. */
function finishBatch(files, results) {
  const outcomes = files.map(({ filePath }, i) => ({ filePath, ...results[i] }));
  return { success: outcomes.every((r) => r.success), results: outcomes };
}

/** `writeTextFiles` with `allOrNothing`: see its doc comment. */
function writeTextFilesAllOrNothing(files, encoding) {
  const results = new Array(files.length);
  const pending = [];
  files.forEach(({ filePath, content }, i) => {
    if (contentUnchanged(filePath, content, encoding)) results[i] = okResult();
    else pending.push(i);
  });

  let mkdirFailed = false;
  for (const i of pending) {
    try {
      mkdirSync(dirname(resolve(files[i].filePath)), { recursive: true });
    } catch (e) {
      results[i] = errorResult('error', e.message);
      mkdirFailed = true;
    }
  }
  if (mkdirFailed) return abandonBatch(files, results, pending);

  const prep = prepareToWriteFiles(pending.map((i) => files[i].filePath));
  if (!prep.success) {
    prep.results.forEach((r, j) => { results[pending[j]] = outcomeOf(r); });
    return finishBatch(files, results);
  }

  for (const i of pending) {
    const { filePath, content } = files[i];
    try {
      writeFileSync(filePath, content, { encoding });
    } catch (e) {
      results[i] = errorResult('error', e.message);
      continue;
    }
    results[i] = outcomeOf(resolveProvider(filePath).finishedWrite(filePath));
  }
  return finishBatch(files, results);
}

/** Async twin of {@link writeTextFilesAllOrNothing}. */
async function writeTextFilesAllOrNothingAsync(files, encoding) {
  const results = new Array(files.length);
  const pending = [];
  for (let i = 0; i < files.length; i++) {
    const { filePath, content } = files[i];
    if (await contentUnchangedAsync(filePath, content, encoding)) results[i] = okResult();
    else pending.push(i);
  }

  let mkdirFailed = false;
  for (const i of pending) {
    try {
      await mkdir(dirname(resolve(files[i].filePath)), { recursive: true });
    } catch (e) {
      results[i] = errorResult('error', e.message);
      mkdirFailed = true;
    }
  }
  if (mkdirFailed) return abandonBatch(files, results, pending);

  const prep = await prepareToWriteFilesAsync(pending.map((i) => files[i].filePath));
  if (!prep.success) {
    prep.results.forEach((r, j) => { results[pending[j]] = outcomeOf(r); });
    return finishBatch(files, results);
  }

  for (const i of pending) {
    const { filePath, content } = files[i];
    try {
      await writeFile(filePath, content, { encoding });
    } catch (e) {
      results[i] = errorResult('error', e.message);
      continue;
    }
    results[i] = outcomeOf(await resolveProvider(filePath).finishedWriteAsync(filePath));
  }
  return finishBatch(files, results);
}

/**
 * Status for a batch of files: tracked / writable / dirty / locked-by /
 * opened-by-me / out-of-date, per file. Paths are grouped by provider so a whole
 * project costs a spawn or two, not one per file (Perforce: ONE `p4 -ztag fstat`;
 * git: one `git status` + one `git lfs locks` per repository). The writable bit is
 * always reported - in lock-based workflows it is the cheap local signal for
 * "is this editable right now?".
 *
 * By default the read stays as local as each provider allows (git without LFS and
 * SVN/Plastic do no network). Pass `{ remote: true }` to also fetch server-side
 * `lockedBy` / `outOfDate` for SVN (`svn status -u`) and Plastic (`cm fileinfo`) -
 * an extra round-trip. Perforce and git-LFS already carry that data in the one call
 * they must make, so they report it regardless of this flag.
 *
 * @param {string[]} filePaths
 * @param {import('./vcStatus.js').VCStatusOptions} [options]
 * @returns {import('./vcStatus.js').VCFileStatus[]}
 */
/**
 * Who this VCS believes the current user is, as it would name them in `lockedBy` ("bob@bob-ws",
 * "alovelace"). Undefined when the provider cannot say: the filesystem provider always, git with no
 * configured `user.name`, svn ever (it identifies locks by token, never by name).
 *
 * `pathHint` picks WHICH working copy is asked, the same way every other call here resolves a provider
 * from a path; it defaults to the process's own directory. A host that has pinned a provider with
 * {@link setProvider} - which both desktop apps do - gets that one regardless.
 *
 * Meant for SEEDING a name the person can then change, not for using as an identity: a workspace
 * account is not always a name somebody wants on their words.
 *
 * @param {string} [pathHint]
 * @returns {string | undefined}
 */
export function currentUser(pathHint) {
  return resolveProvider(pathHint ?? process.cwd()).currentUser?.(pathHint ?? process.cwd());
}

/**
 * Async twin of {@link currentUser}.
 *
 * @param {string} [pathHint]
 * @returns {Promise<string | undefined>}
 */
export async function currentUserAsync(pathHint) {
  const where = pathHint ?? process.cwd();
  return resolveProvider(where).currentUserAsync?.(where);
}

export function fileStatus(filePaths, options = {}) {
  const groups = new Map();
  for (const filePath of filePaths) {
    const provider = resolveProvider(filePath);
    if (!groups.has(provider.name)) groups.set(provider.name, { provider, paths: [] });
    groups.get(provider.name).paths.push(filePath);
  }

  const byInput = new Map();
  for (const { provider, paths } of groups.values()) {
    const statuses = provider.status(paths, options);
    for (let i = 0; i < paths.length; i++) byInput.set(paths[i], statuses[i]);
  }
  return filePaths.map((filePath) => byInput.get(filePath));
}

/**
 * Async twin of {@link fileStatus}. Spawns without blocking the event loop, and -
 * because providers are independent - runs the per-provider reads concurrently, so a
 * project spanning several repos/working copies finishes in about the time of its
 * slowest provider rather than their sum.
 *
 * @param {string[]} filePaths
 * @param {import('./vcStatus.js').VCStatusOptions} [options]
 * @returns {Promise<import('./vcStatus.js').VCFileStatus[]>}
 */
export async function fileStatusAsync(filePaths, options = {}) {
  const groups = new Map();
  for (const filePath of filePaths) {
    const provider = resolveProvider(filePath);
    if (!groups.has(provider.name)) groups.set(provider.name, { provider, paths: [] });
    groups.get(provider.name).paths.push(filePath);
  }

  const byInput = new Map();
  await Promise.all([...groups.values()].map(async ({ provider, paths }) => {
    const statuses = provider.statusAsync
      ? await provider.statusAsync(paths, options)
      : provider.status(paths, options);
    for (let i = 0; i < paths.length; i++) byInput.set(paths[i], statuses[i]);
  }));
  return filePaths.map((filePath) => byInput.get(filePath));
}
