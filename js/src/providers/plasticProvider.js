import { existsSync, unlinkSync, rmSync } from 'fs';
import { runCommand, runCommandAsync } from '../commandRunner.js';
import { okResult, errorResult } from '../vcResult.js';
import { writableBit } from '../vcStatus.js';
import { basename, resolve } from 'path';
import { FilesystemProvider } from './filesystemProvider.js';

const fs = new FilesystemProvider();

function cm(args) {
  return runCommand('cm', args);
}

function cmAsync(args) {
  return runCommandAsync('cm', args);
}

/** Tracked by Plastic SCM: controlled, whether or not it is checked out. */
function isTracked(filePath) {
  return tracked(localState(filePath));
}

async function isTrackedAsync(filePath) {
  return tracked(await localStateAsync(filePath));
}

function tracked(state) {
  return state === 'controlled' || state === 'checkedOut';
}

/** The `cm status` call that reads one path's state: the same flags as {@link PlasticProvider#status}. */
function localStateArgs(filePath) {
  return plasticStatusArgs([filePath]);
}

function localState(filePath) {
  return localStateFrom(cm(localStateArgs(filePath)), filePath);
}

async function localStateAsync(filePath) {
  return localStateFrom(await cmAsync(localStateArgs(filePath)), filePath);
}

/**
 * One path's state in the workspace, from `cm status --machinereadable --all --ignored`:
 * - 'outside': cm failed, so the path is not in a workspace (or cm is missing)
 * - 'private' / 'ignored': listed PR / IG
 * - 'checkedOut': it already has a pending controlled change here (checked out, added,
 *   copied, replaced, or moved), so it needs no checkout
 * - 'controlled': under version control and not checked out; unchanged files are not
 *   listed at all, and a file changed without a checkout is listed CH
 *
 * `cm status --short` cannot answer this: it lists only paths with changes, so an
 * unchanged controlled file reads as untracked.
 *
 * @param {{exitCode: number, output: string}} result
 * @param {string} filePath
 * @returns {'outside' | 'private' | 'ignored' | 'checkedOut' | 'controlled'}
 */
function localStateFrom(result, filePath) {
  if (result.exitCode !== 0) return 'outside';
  const info = findStatusInfo(result.output, filePath);
  if (!info) return 'controlled';
  if (!info.tracked) return info.ignored ? 'ignored' : 'private';
  return info.checkedOut ? 'checkedOut' : 'controlled';
}

/**
 * The `cm status` line for one path: matched by absolute path, else by file name when
 * exactly one listed path has it. A folder's listing can include its contents, so the
 * first line is not necessarily the path asked about.
 */
function findStatusInfo(output, filePath) {
  const key = pathKey(filePath);
  const sameName = [];
  for (const line of output.split('\n')) {
    const parsed = parseCmStatusLine(line);
    if (!parsed) continue;
    for (const p of parsed.paths) {
      if (pathKey(p) === key) return parsed.info;
      if (basename(resolve(p)) === basename(resolve(filePath))) sameName.push(parsed.info);
    }
  }
  return sameName.length === 1 ? sameName[0] : undefined;
}

/** An absolute path for comparing with what cm prints: case-insensitive on Windows. */
function pathKey(p) {
  const abs = resolve(p);
  return process.platform === 'win32' ? abs.toLowerCase() : abs;
}

/** The result of the `cm co` that checks a controlled file out. */
function checkoutResult(filePath, result) {
  if (result.exitCode === 0) return okResult();
  const combined = (result.output + ' ' + result.error).toLowerCase();
  if (combined.includes('locked') || combined.includes('exclusive')) {
    return errorResult('locked', `'${filePath}' is locked`);
  }
  if (combined.includes('out of date') || combined.includes('not latest')) {
    return errorResult('outOfDate', `'${filePath}' is out of date — update before editing`);
  }
  return errorResult('error', `Cannot check out '${filePath}': ${result.error || result.output}`);
}

/** The result of the `cm add` that puts a new private file under version control. */
function addResult(filePath, result) {
  if (result.exitCode === 0) return okResult('File added to Plastic SCM');
  return errorResult('error', `Cannot add '${filePath}' to Plastic SCM: ${result.error || result.output}`);
}

/** The result of the `cm undocheckout` that undoes a batch checkout. */
function undoCheckoutResult(filePath, result) {
  if (result.exitCode === 0) return okResult('Checkout undone in Plastic SCM');
  return errorResult('error', `Cannot undo checkout of '${filePath}' in Plastic SCM: ${result.error || result.output}`);
}

/**
 * Plastic SCM / Unity Version Control provider.
 *
 * Uses the `cm` CLI (Plastic SCM command-line client).
 * Files under Plastic SCM are read-only until checked out.
 */
export class PlasticProvider {
  get name() { return 'plastic'; }

  /** `cm whoami`, the same call the lock check already makes to tell our lock from someone else's. */
  currentUser() { const u = plasticWhoami(); return u === '' ? undefined : u; }

  /** Async twin of {@link currentUser}. */
  async currentUserAsync() { const u = await plasticWhoamiAsync(); return u === '' ? undefined : u; }

  /**
   * Check a controlled file out before it is written. A file already checked out (or
   * otherwise pending) in this workspace is not checked out again: a second `cm co` on
   * it fails, and under lock rules it fails as "locked" by our own checkout. Private,
   * ignored, and out-of-workspace files only need to be writable.
   */
  prepareToWrite(filePath) {
    if (!existsSync(filePath)) return okResult();
    if (localState(filePath) !== 'controlled') return fs.prepareToWrite(filePath);
    return checkoutResult(filePath, cm(['co', filePath]));
  }

  /** Add a new private file to Plastic SCM after it is written. */
  finishedWrite(filePath) {
    if (!existsSync(filePath))
      return errorResult('error', `'${filePath}' does not exist after write`);
    if (localState(filePath) !== 'private') return fs.finishedWrite(filePath);
    return addResult(filePath, cm(['add', filePath]));
  }

  /** Async twin of {@link prepareToWrite}. */
  async prepareToWriteAsync(filePath) {
    if (!existsSync(filePath)) return okResult();
    if ((await localStateAsync(filePath)) !== 'controlled') return fs.prepareToWriteAsync(filePath);
    return checkoutResult(filePath, await cmAsync(['co', filePath]));
  }

  /** Async twin of {@link finishedWrite}. */
  async finishedWriteAsync(filePath) {
    if (!existsSync(filePath))
      return errorResult('error', `'${filePath}' does not exist after write`);
    if ((await localStateAsync(filePath)) !== 'private') return fs.finishedWriteAsync(filePath);
    return addResult(filePath, await cmAsync(['add', filePath]));
  }

  /**
   * Undo what {@link prepareToWrite} did, for an all-or-nothing batch that has to back
   * out. A controlled file this batch checked out is released with `cm undocheckout`.
   *
   * A file `before` reports as `openedByMe` (our lock) or `dirty` (already checked out
   * or changed) is left alone: that state is the user's, and `cm undocheckout` would
   * discard their changes. Private files get the filesystem undo.
   *
   * @param {string} filePath
   * @param {import('../vcStatus.js').VCFileStatus} before
   */
  undoPrepareToWrite(filePath, before) {
    if (!existsSync(filePath) || before?.openedByMe === true) return okResult();
    if (!isTracked(filePath)) return fs.undoPrepareToWrite(filePath, before);
    if (before?.dirty === true) return okResult();
    return undoCheckoutResult(filePath, cm(['undocheckout', filePath]));
  }

  /** Async twin of {@link undoPrepareToWrite}. */
  async undoPrepareToWriteAsync(filePath, before) {
    if (!existsSync(filePath) || before?.openedByMe === true) return okResult();
    if (!(await isTrackedAsync(filePath))) return fs.undoPrepareToWriteAsync(filePath, before);
    if (before?.dirty === true) return okResult();
    return undoCheckoutResult(filePath, await cmAsync(['undocheckout', filePath]));
  }

  deleteFile(filePath) {
    if (!existsSync(filePath)) return okResult();

    if (isTracked(filePath)) {
      const result = cm(['remove', filePath]);
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot delete '${filePath}' from Plastic SCM: ${result.error || result.output}`);
    }

    try {
      unlinkSync(filePath);
      return okResult();
    } catch (e) {
      return errorResult('error', `Cannot delete '${filePath}': ${e.message}`);
    }
  }

  renameFile(oldPath, newPath) {
    if (!existsSync(oldPath)) return okResult();
    if (isTracked(oldPath)) {
      const result = cm(['mv', oldPath, newPath]);
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot rename '${oldPath}' in Plastic SCM: ${result.error || result.output}`);
    }
    return fs.renameFile(oldPath, newPath);
  }

  renameFolder(oldPath, newPath) {
    if (!existsSync(oldPath)) return okResult();
    if (isTracked(oldPath)) {
      const result = cm(['mv', oldPath, newPath]);
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot rename folder '${oldPath}' in Plastic SCM: ${result.error || result.output}`);
    }
    return fs.renameFolder(oldPath, newPath);
  }

  deleteFolder(folderPath) {
    if (!existsSync(folderPath)) return okResult();

    if (isTracked(folderPath)) {
      // cm remove is recursive for directories.
      const result = cm(['remove', folderPath]);
      if (result.exitCode !== 0) {
        return errorResult('error', `Cannot delete folder '${folderPath}' from Plastic SCM: ${result.error || result.output}`);
      }
    }

    if (existsSync(folderPath)) {
      try {
        rmSync(folderPath, { recursive: true, force: true });
      } catch (e) {
        return errorResult('error', `Cannot delete folder '${folderPath}': ${e.message}`);
      }
    }

    return okResult();
  }

  /** Async twin of {@link deleteFile}. */
  async deleteFileAsync(filePath) {
    if (!existsSync(filePath)) return okResult();
    if (await isTrackedAsync(filePath)) {
      const result = await cmAsync(['remove', filePath]);
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot delete '${filePath}' from Plastic SCM: ${result.error || result.output}`);
    }
    try {
      unlinkSync(filePath);
      return okResult();
    } catch (e) {
      return errorResult('error', `Cannot delete '${filePath}': ${e.message}`);
    }
  }

  /** Async twin of {@link renameFile}. */
  async renameFileAsync(oldPath, newPath) {
    if (!existsSync(oldPath)) return okResult();
    if (await isTrackedAsync(oldPath)) {
      const result = await cmAsync(['mv', oldPath, newPath]);
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot rename '${oldPath}' in Plastic SCM: ${result.error || result.output}`);
    }
    return fs.renameFileAsync(oldPath, newPath);
  }

  /** Async twin of {@link renameFolder}. */
  async renameFolderAsync(oldPath, newPath) {
    if (!existsSync(oldPath)) return okResult();
    if (await isTrackedAsync(oldPath)) {
      const result = await cmAsync(['mv', oldPath, newPath]);
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot rename folder '${oldPath}' in Plastic SCM: ${result.error || result.output}`);
    }
    return fs.renameFolderAsync(oldPath, newPath);
  }

  /** Async twin of {@link deleteFolder}. */
  async deleteFolderAsync(folderPath) {
    if (!existsSync(folderPath)) return okResult();

    if (await isTrackedAsync(folderPath)) {
      // cm remove is recursive for directories.
      const result = await cmAsync(['remove', folderPath]);
      if (result.exitCode !== 0) {
        return errorResult('error', `Cannot delete folder '${folderPath}' from Plastic SCM: ${result.error || result.output}`);
      }
    }

    if (existsSync(folderPath)) {
      try {
        rmSync(folderPath, { recursive: true, force: true });
      } catch (e) {
        return errorResult('error', `Cannot delete folder '${folderPath}': ${e.message}`);
      }
    }

    return okResult();
  }

  /**
   * Status for a batch of files in ONE `cm status --machinereadable --all --ignored`
   * spawn. The machine format lists one item per line as `<2-letter code> <path>`
   * (absolute paths, quoted when they contain spaces).
   *
   * Flag choice matters: `cm status` defaults to `--controlledchanged`, which omits
   * a content-modified-but-not-checked-out file (CH) and local deletes/moves. `--all`
   * adds changed + localdeleted + localmoved + private; `--ignored` adds IG. Together
   * they surface every dirty and every untracked item, so a not-listed file can be
   * read as clean-and-controlled.
   *
   * With `{ remote: true }` it follows up with ONE `cm fileinfo` over the controlled
   * files (plus one `cm whoami`) to fill `outOfDate` (loaded changeset < head) and
   * lock holder (`openedByMe` when that's us, else `lockedBy: ["user@workspace"]`).
   *
   * NOTE: status codes, flags, and the fileinfo format are validated against the Unity
   * VCS CLI docs / UEPlasticPlugin, not a live workspace - worth one real smoke test.
   *
   * @param {string[]} filePaths
   * @param {import('../vcStatus.js').VCStatusOptions} [options]
   * @returns {import('../vcStatus.js').VCFileStatus[]}
   */
  status(filePaths, options = {}) {
    const result = cm(plasticStatusArgs(filePaths));
    const statuses = buildPlasticBase(result, filePaths);
    if (options.remote) enrichPlasticRemote(statuses);
    return statuses;
  }

  /** Async twin of {@link status}: `cm status` (+ `cm fileinfo`/`whoami` when remote). */
  async statusAsync(filePaths, options = {}) {
    const result = await cmAsync(plasticStatusArgs(filePaths));
    const statuses = buildPlasticBase(result, filePaths);
    if (options.remote) await enrichPlasticRemoteAsync(statuses);
    return statuses;
  }
}

/** The local `cm status` argument list. */
function plasticStatusArgs(filePaths) {
  return ['status', '--machinereadable', '--all', '--ignored', ...filePaths.map((p) => resolve(p))];
}

/**
 * Assemble the local (tracked / dirty) statuses from `cm status` output. Pure - shared
 * by the sync and async paths.
 *
 * @param {{exitCode: number, output: string}} result
 * @param {string[]} filePaths
 * @returns {import('../vcStatus.js').VCFileStatus[]}
 */
function buildPlasticBase(result, filePaths) {
  const byPath = new Map();
  const byBase = new Map();
  if (result.exitCode === 0 && result.output) {
    for (const line of result.output.split('\n')) {
      const parsed = parseCmStatusLine(line);
      if (!parsed) continue;
      for (const p of parsed.paths) {
        const abs = resolve(p);
        byPath.set(pathKey(abs), parsed.info);
        const base = basename(abs);
        if (!byBase.has(base)) byBase.set(base, []);
        byBase.get(base).push(parsed.info);
      }
    }
  }

  return filePaths.map((filePath) => {
    const abs = resolve(filePath);
    /** @type {import('../vcStatus.js').VCFileStatus} */
    const status = { filePath: abs, system: 'plastic', writable: writableBit(abs) };
    let info = byPath.get(pathKey(abs));
    if (!info) {
      const sameName = byBase.get(basename(abs));
      if (sameName && sameName.length === 1) info = sameName[0];
    }
    if (info) {
      status.tracked = info.tracked;
      status.dirty = info.dirty;
    } else if (existsSync(abs)) {
      // Not listed by `cm status --all --ignored` = controlled, no pending change.
      status.tracked = true;
      status.dirty = false;
    }
    return status;
  });
}

/** fileinfo format whose fields the parser reads positionally (see UEPlasticPlugin). */
const FILEINFO_FORMAT =
  '{RevisionChangeset};{RevisionHeadChangeset};{RepSpec};{LockedBy};{LockedWhere};{ServerPath}';

/** The controlled files (the only ones `cm fileinfo` reports on). */
function controlledFiles(statuses) {
  return statuses.filter((s) => s.tracked === true);
}

/** `cm fileinfo` over the controlled files, one line per path IN ORDER. */
function fileinfoArgs(controlled) {
  return ['fileinfo', `--format=${FILEINFO_FORMAT}`, ...controlled.map((s) => s.filePath)];
}

/**
 * Apply `cm fileinfo` output to the controlled files, zipped by index: out-of-date from
 * loaded-vs-head changeset, and lock holder (`openedByMe` if that's `me`, else `lockedBy`).
 *
 * @param {import('../vcStatus.js').VCFileStatus[]} controlled
 * @param {string} output
 * @param {string} me
 */
function applyPlasticFileinfo(controlled, output, me) {
  const lines = output.split('\n').filter((l) => l.length > 0);
  for (let i = 0; i < controlled.length && i < lines.length; i++) {
    const f = lines[i].split(';');
    if (f.length < 5) continue;
    const rev = Number(f[0]);
    const head = Number(f[1]);
    const lockedBy = f[3];
    const lockedWhere = f[4];
    // A negative head means an unshelved/special revision - not a staleness signal.
    if (Number.isFinite(rev) && Number.isFinite(head) && head >= 0 && rev < head) {
      controlled[i].outOfDate = true;
    }
    if (lockedBy) {
      if (me && lockedBy === me) controlled[i].openedByMe = true;
      else controlled[i].lockedBy = [lockedWhere ? `${lockedBy}@${lockedWhere}` : lockedBy];
    }
  }
}

/** Fill `outOfDate` / `openedByMe` / `lockedBy` from `cm fileinfo` (sync). */
function enrichPlasticRemote(statuses) {
  const controlled = controlledFiles(statuses);
  if (controlled.length === 0) return;
  const fi = cm(fileinfoArgs(controlled));
  if (fi.exitCode !== 0 || !fi.output) return;
  applyPlasticFileinfo(controlled, fi.output, plasticWhoami());
}

/** Async twin of {@link enrichPlasticRemote}. */
async function enrichPlasticRemoteAsync(statuses) {
  const controlled = controlledFiles(statuses);
  if (controlled.length === 0) return;
  const fi = await cmAsync(fileinfoArgs(controlled));
  if (fi.exitCode !== 0 || !fi.output) return;
  applyPlasticFileinfo(controlled, fi.output, await plasticWhoamiAsync());
}

/** The current Plastic user, for telling our own lock from someone else's. */
function plasticWhoami() {
  const r = cm(['whoami']);
  return r.exitCode === 0 ? r.output.trim() : '';
}

/** Async twin of {@link plasticWhoami}. */
async function plasticWhoamiAsync() {
  const r = await cmAsync(['whoami']);
  return r.exitCode === 0 ? r.output.trim() : '';
}

/** `cm` machine-readable status codes for a controlled file with a pending change. */
const PLASTIC_DIRTY_CODES = new Set([
  'CH', 'CO', 'AD', 'CP', 'RP', 'MV', 'DE', 'LD', 'LM',
]);
/** Codes meaning the path is not under version control. */
const PLASTIC_UNTRACKED_CODES = new Set(['PR', 'IG']);
/** Codes for a pending controlled change in this workspace that needs no `cm co`. */
const PLASTIC_CHECKED_OUT_CODES = new Set(['CO', 'AD', 'CP', 'RP', 'MV']);

/**
 * Parse one `cm status --machinereadable` line into a classification and the
 * path(s) it concerns. Returns null for the header / blank / unrecognised lines.
 * A move (`MV "src" "dst"`) carries two quoted paths; both are flagged. A combined
 * code such as `CO+CH` (a checked-out file whose contents changed, printed with
 * `--iscochanged`) counts as each of its parts.
 *
 * @param {string} line
 * @returns {{info: {tracked: boolean, dirty: boolean, checkedOut: boolean, ignored: boolean}, paths: string[]} | null}
 */
function parseCmStatusLine(line) {
  const trimmed = line.trim();
  const space = trimmed.indexOf(' ');
  if (space === -1) return null;
  const codes = trimmed.slice(0, space).split('+');
  const dirty = codes.some((c) => PLASTIC_DIRTY_CODES.has(c));
  const untracked = codes.some((c) => PLASTIC_UNTRACKED_CODES.has(c));
  if (!dirty && !untracked) return null; // STATUS header, blank, or unknown code
  const rest = trimmed.slice(space + 1).trim();
  const quoted = [...rest.matchAll(/"([^"]*)"/g)].map((m) => m[1]);
  const paths = quoted.length > 0 ? quoted : [rest];
  const checkedOut = !untracked && codes.some((c) => PLASTIC_CHECKED_OUT_CODES.has(c));
  return { info: { tracked: !untracked, dirty, checkedOut, ignored: codes.includes('IG') }, paths };
}
