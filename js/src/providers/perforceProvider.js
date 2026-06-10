import { existsSync, unlinkSync, chmodSync, rmSync, cpSync, mkdirSync, readdirSync, statSync } from 'fs';
import { resolve } from 'path';
import { runCommand } from '../commandRunner.js';
import { okResult, errorResult } from '../vcResult.js';
import { writableBit } from '../vcStatus.js';
import { FilesystemProvider } from './filesystemProvider.js';

const fs = new FilesystemProvider();

// Perforce marks synced files read-only; strip that before any recursive delete.
function clearReadOnly(dirPath) {
  for (const entry of readdirSync(dirPath, { withFileTypes: true })) {
    const full = dirPath + '/' + entry.name;
    if (entry.isDirectory()) clearReadOnly(full);
    else chmodSync(full, 0o666);
  }
}

function p4(args) {
  return runCommand('p4', args);
}

/**
 * Returns p4 fstat info for a file, or null if the file is not in the depot.
 */
function fstat(filePath) {
  const result = p4(['fstat', filePath]);
  if (result.exitCode !== 0) return null;
  return result.output;
}

function isInDepot(filePath) {
  return isInDepotFstat(fstat(filePath));
}

function isInDepotFstat(info) {
  if (info === null) return false;

  // p4 fstat exits 0 for workspace-mapped files even if never submitted to the depot,
  // returning only clientFile/isMapped. Only treat the file as depot-tracked when
  // depot metadata (headRev or depotFile) is present.
  const hasDepotFile = info.includes('headRev') || info.includes('depotFile');
  if (!hasDepotFile) return false;

  // If the file was submitted as deleted at head revision, it's effectively untracked
  // unless it is currently reopened in a changelist.
  const isDeletedAtHead = info.includes('headAction delete') || info.includes('headAction move/delete');
  const isOpened = info.includes('... action ');

  if (isDeletedAtHead && !isOpened) {
    return false;
  }

  return true;
}

/**
 * Perforce (Helix Core) provider.
 *
 * Uses the `p4` CLI. Assumes the workspace is already configured in the
 * environment (P4PORT, P4USER, P4CLIENT, or via p4 config/tickets).
 */
export class PerforceProvider {
  get name() { return 'perforce'; }

  prepareToWrite(filePath) {
    if (!existsSync(filePath)) return okResult();

    if (!isInDepot(filePath)) {
      return fs.prepareToWrite(filePath);
    }

    const result = p4(['edit', filePath]);
    if (result.exitCode === 0) return okResult();

    const combined = (result.output + ' ' + result.error).toLowerCase();
    if (combined.includes('locked by')) {
      return errorResult('locked', `'${filePath}' is locked by another user`);
    }
    if (combined.includes('out of date')) {
      return errorResult('outOfDate', `'${filePath}' is out of date — sync before editing`);
    }
    return errorResult('error', `Cannot open '${filePath}' for editing: ${result.error || result.output}`);
  }

  finishedWrite(filePath) {
    if (!existsSync(filePath))
      return errorResult('error', `'${filePath}' does not exist after write`);

    const info = fstat(filePath);

    // File is outside the workspace mapping entirely — no Perforce action needed.
    if (info === null)
      return fs.finishedWrite(filePath);

    // File has a pending delete in a changelist but was just re-written to disk.
    // Cancel the delete (keeping the new local content) then reopen for edit.
    if (info.includes('... action delete')) {
      p4(['revert', '-k', filePath]);
      const editResult = p4(['edit', filePath]);
      if (editResult.exitCode === 0) return okResult('File reopened for edit after pending delete was reverted');
      return errorResult('error', `Cannot reopen '${filePath}' for edit after reverting pending delete: ${editResult.error || editResult.output}`);
    }

    if (isInDepotFstat(info)) return okResult();

    const result = p4(['add', filePath]);
    if (result.exitCode === 0) return okResult('File opened for add in Perforce');
    // File is ignored (e.g. matches a .p4ignore pattern) — treat as outside the depot.
    const combined = (result.output + ' ' + result.error).toLowerCase();
    if (combined.includes('ignored')) return fs.finishedWrite(filePath);
    return errorResult('error', `Cannot add '${filePath}' to Perforce: ${result.error || result.output}`);
  }

  deleteFile(filePath) {
    if (!existsSync(filePath)) return okResult();

    if (isInDepot(filePath)) {
      const result = p4(['delete', filePath]);
      if (result.exitCode !== 0)
        return errorResult('error', `Cannot delete '${filePath}' from Perforce: ${result.error || result.output}`);
      // p4 delete marks for deletion but leaves the file on disk as read-only.
      // Physically remove it, consistent with git rm and svn delete.
      try {
        if (existsSync(filePath)) {
          chmodSync(filePath, 0o666);
          unlinkSync(filePath);
        }
      } catch (e) {
        return errorResult('error', `Cannot remove '${filePath}' from disk: ${e.message}`);
      }
      return okResult();
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
    if (isInDepot(oldPath)) {
      p4(['edit', oldPath]);
      const result = p4(['move', oldPath, newPath]);
      const combined = ((result.output || '') + ' ' + (result.error || '')).toLowerCase();
      if (result.exitCode === 0 && !combined.includes('not opened for')) return okResult();
      return errorResult('error', `Cannot rename '${oldPath}' in Perforce: ${result.error || result.output}`);
    }
    return fs.renameFile(oldPath, newPath);
  }

  renameFolder(oldPath, newPath) {
    if (!existsSync(oldPath)) return okResult();
    // p4 move with /... wildcard handles tracked files and physically moves them.
    const isWin = process.platform === 'win32';
    const src = (isWin ? oldPath.replace(/\\/g, '/') : oldPath) + '/...';
    const dst = (isWin ? newPath.replace(/\\/g, '/') : newPath) + '/...';
    p4(['edit', src]);
    p4(['move', src, dst]);
    // Move any untracked files that p4 left behind.
    if (existsSync(oldPath)) {
      try {
        mkdirSync(newPath, { recursive: true });
        cpSync(oldPath, newPath, { recursive: true });
        clearReadOnly(oldPath);
        rmSync(oldPath, { recursive: true, force: true });
      } catch (e) {
        return errorResult('error', `Cannot rename folder '${oldPath}': ${e.message}`);
      }
    }
    return okResult();
  }

  deleteFolder(folderPath) {
    if (!existsSync(folderPath)) return okResult();

    // The /... wildcard schedules the entire depot subtree for deletion.
    const isWin = process.platform === 'win32';
    const depotPath = (isWin ? folderPath.replace(/\\/g, '/') : folderPath) + '/...';
    const result = p4(['delete', depotPath]);

    if (existsSync(folderPath)) {
      try {
        rmSync(folderPath, { recursive: true, force: true });
      } catch (e) {
        return errorResult('error', `Cannot delete folder '${folderPath}': ${e.message}`);
      }
    }

    // p4 delete of untracked path exits non-zero, but we still succeeded locally.
    if (result.exitCode !== 0 && result.error && !result.error.includes('no such file')) {
      return errorResult('error', `Cannot delete folder '${folderPath}' from Perforce: ${result.error || result.output}`);
    }

    return okResult();
  }

  /**
   * Status for a batch of files in ONE `p4 -ztag fstat` spawn.
   *
   * Tracked-ness follows the same rules as the write path (`isInDepotFstat`):
   * workspace-mapped is not depot-tracked; deleted-at-head is untracked unless
   * currently reopened. Lock / checkout info comes from otherOpen / otherLock /
   * ourLock / action; staleness from haveRev < headRev.
   *
   * @param {string[]} filePaths
   * @returns {import('../vcStatus.js').VCFileStatus[]}
   */
  status(filePaths) {
    const result = p4(['-ztag', 'fstat', ...filePaths]);
    const records = result.output ? parseZtag(result.output) : [];

    const byClientFile = new Map();
    for (const record of records) {
      const clientFile = record.fields.get('clientFile');
      if (clientFile) byClientFile.set(resolve(clientFile), record);
    }

    return filePaths.map((filePath) => {
      const abs = resolve(filePath);
      /** @type {import('../vcStatus.js').VCFileStatus} */
      const status = { filePath: abs, system: 'perforce', writable: writableBit(abs) };
      const record = byClientFile.get(abs);
      if (!record) {
        // fstat returned nothing for it: outside the client view / unknown to p4.
        status.tracked = false;
        return status;
      }

      const has = (field) => record.fields.has(field);
      const headAction = record.fields.get('headAction') ?? '';
      const deletedAtHead = headAction === 'delete' || headAction === 'move/delete';
      const openedByMe = has('action');
      status.tracked = (has('headRev') || has('depotFile')) && (!deletedAtHead || openedByMe);
      if (openedByMe || has('ourLock')) status.openedByMe = true;

      const otherOpen = record.multi.get('otherOpen') ?? [];
      const otherLock = record.multi.get('otherLock') ?? [];
      // otherLockN names the locker when present; otherwise an exclusive (+l)
      // filetype means any other opener effectively holds it.
      const holders = otherLock.length > 0 ? otherLock : has('otherLock') ? otherOpen : [];
      const lockedBy = holders.length > 0 ? holders : otherOpen;
      if (lockedBy.length > 0) status.lockedBy = lockedBy;

      const haveRev = Number(record.fields.get('haveRev') ?? '0');
      const headRev = Number(record.fields.get('headRev') ?? '0');
      if (headRev > haveRev && !deletedAtHead) status.outOfDate = true;
      return status;
    });
  }
}

/**
 * Parse `p4 -ztag` output: `... field value` lines; a blank line separates
 * records. Indexed fields (otherOpen0..N) collect into `multi` by prefix.
 *
 * @param {string} output
 * @returns {{fields: Map<string, string>, multi: Map<string, string[]>}[]}
 */
export function parseZtag(output) {
  const records = [];
  let current = null;
  for (const line of output.split('\n')) {
    if (!line.startsWith('... ')) {
      if (line.trim() === '') current = null; // record separator
      continue;
    }
    if (!current) {
      current = { fields: new Map(), multi: new Map() };
      records.push(current);
    }
    const body = line.slice(4);
    const space = body.indexOf(' ');
    const field = space === -1 ? body : body.slice(0, space);
    const value = space === -1 ? '' : body.slice(space + 1);
    const indexed = /^([A-Za-z/]+?)(\d+)$/.exec(field);
    if (indexed) {
      const list = current.multi.get(indexed[1]) ?? [];
      list.push(value);
      current.multi.set(indexed[1], list);
    } else {
      current.fields.set(field, value);
    }
  }
  return records;
}
