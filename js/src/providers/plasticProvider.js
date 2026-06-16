import { existsSync, unlinkSync, rmSync } from 'fs';
import { runCommand } from '../commandRunner.js';
import { okResult, errorResult } from '../vcResult.js';
import { writableBit } from '../vcStatus.js';
import { basename, resolve } from 'path';
import { FilesystemProvider } from './filesystemProvider.js';

const fs = new FilesystemProvider();

function cm(args) {
  return runCommand('cm', args);
}

/**
 * Returns true if the file is tracked by Plastic SCM (Unity Version Control).
 */
function isTracked(filePath) {
  const result = cm(['status', '--short', filePath]);
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

  /**
   * Status for a batch of files in ONE `cm status --machinereadable --all --ignored`
   * spawn. The machine format lists one item per line as `<2-letter code> <path>`
   * (absolute paths, quoted when they contain spaces).
   *
   * Flag choice matters: `cm status` defaults to `--controlledchanged`, which omits
   * a content-modified-but-not-checked-out file (CH) and local deletes/moves. `--all`
   * adds changed + localdeleted + localmoved + private; `--ignored` adds IG. Together
   * they surface every dirty and every untracked item, so a not-listed file can be
   * read as clean-and-controlled. Lock owners and out-of-date remain TODO.
   *
   * NOTE: status codes and flag semantics are validated against the Unity VCS CLI
   * docs, not a live workspace - worth one real smoke test on a Plastic install.
   *
   * @param {string[]} filePaths
   * @returns {import('../vcStatus.js').VCFileStatus[]}
   */
  status(filePaths) {
    const targets = filePaths.map((p) => resolve(p));
    const result = cm(['status', '--machinereadable', '--all', '--ignored', ...targets]);
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

