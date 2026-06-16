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

/**
 * Returns true if the file is tracked by Plastic SCM (Unity Version Control).
 */
function isTracked(filePath) {
  return trackedFromShortStatus(cm(['status', '--short', filePath]));
}

async function isTrackedAsync(filePath) {
  return trackedFromShortStatus(await cmAsync(['status', '--short', filePath]));
}

/** A `cm status --short` result is tracked when it lists the file without a '?' prefix. */
function trackedFromShortStatus(result) {
  if (result.exitCode !== 0) return false;
  // Untracked files are reported with a '?' prefix.
  const lines = result.output.split('\n').filter(l => l.trim());
  return lines.length > 0 && !lines[0].startsWith('?');
}

/**
 * Plastic SCM / Unity Version Control provider.
 *
 * Uses the `cm` CLI (Plastic SCM command-line client).
 * Files under Plastic SCM are read-only until checked out.
 */
export class PlasticProvider {
  get name() { return 'plastic'; }

  prepareToWrite(filePath) {
    if (!existsSync(filePath)) return okResult();

    if (!isTracked(filePath)) {
      return fs.prepareToWrite(filePath);
    }

    const result = cm(['co', filePath]);
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

  finishedWrite(filePath) {
    if (!existsSync(filePath))
      return errorResult('error', `'${filePath}' does not exist after write`);

    // cm status --short: exit non-zero = outside workspace, exit 0 with '?' = untracked inside workspace.
    const statusResult = cm(['status', '--short', filePath]);
    if (statusResult.exitCode !== 0)
      return fs.finishedWrite(filePath);

    const lines = statusResult.output.split('\n').filter(l => l.trim());
    const tracked = lines.length > 0 && !lines[0].startsWith('?');
    if (tracked) return okResult();

    const result = cm(['add', filePath]);
    if (result.exitCode === 0) return okResult('File added to Plastic SCM');
    // File is ignored — treat as outside the workspace.
    const combined = (result.output + ' ' + result.error).toLowerCase();
    if (combined.includes('ignored')) return fs.finishedWrite(filePath);
    return errorResult('error', `Cannot add '${filePath}' to Plastic SCM: ${result.error || result.output}`);
  }

  /** Async twin of {@link prepareToWrite}. */
  async prepareToWriteAsync(filePath) {
    if (!existsSync(filePath)) return okResult();

    if (!(await isTrackedAsync(filePath))) {
      return fs.prepareToWriteAsync(filePath);
    }

    const result = await cmAsync(['co', filePath]);
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

  /** Async twin of {@link finishedWrite}. */
  async finishedWriteAsync(filePath) {
    if (!existsSync(filePath))
      return errorResult('error', `'${filePath}' does not exist after write`);

    const statusResult = await cmAsync(['status', '--short', filePath]);
    if (statusResult.exitCode !== 0)
      return fs.finishedWriteAsync(filePath);

    if (trackedFromShortStatus(statusResult)) return okResult();

    const result = await cmAsync(['add', filePath]);
    if (result.exitCode === 0) return okResult('File added to Plastic SCM');
    const combined = (result.output + ' ' + result.error).toLowerCase();
    if (combined.includes('ignored')) return fs.finishedWriteAsync(filePath);
    return errorResult('error', `Cannot add '${filePath}' to Plastic SCM: ${result.error || result.output}`);
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
        byPath.set(abs, parsed.info);
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
    let info = byPath.get(abs);
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

/**
 * Parse one `cm status --machinereadable` line into a classification and the
 * path(s) it concerns. Returns null for the header / blank / unrecognised lines.
 * A move (`MV "src" "dst"`) carries two quoted paths; both are flagged.
 *
 * @param {string} line
 * @returns {{info: {tracked: boolean, dirty: boolean}, paths: string[]} | null}
 */
function parseCmStatusLine(line) {
  const trimmed = line.trim();
  const space = trimmed.indexOf(' ');
  if (space === -1) return null;
  const code = trimmed.slice(0, space);
  const dirty = PLASTIC_DIRTY_CODES.has(code);
  const untracked = PLASTIC_UNTRACKED_CODES.has(code);
  if (!dirty && !untracked) return null; // STATUS header, blank, or unknown code
  const rest = trimmed.slice(space + 1).trim();
  const quoted = [...rest.matchAll(/"([^"]*)"/g)].map((m) => m[1]);
  const paths = quoted.length > 0 ? quoted : [rest];
  return { info: { tracked: !untracked, dirty }, paths };
}

