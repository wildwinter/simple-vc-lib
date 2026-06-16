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

/**
 * The pending changelist action for a file ('add', 'edit', 'delete',
 * 'move/add', 'move/delete', ...) or null if the file is not opened.
 */
function pendingAction(info) {
  return info?.match(/^\.\.\. action (\S+)/m)?.[1] ?? null;
}

/**
 * Depot path of the other half of a pending move, or null.
 */
function movedCounterpart(info) {
  return info?.match(/^\.\.\. movedFile (\S+)/m)?.[1] ?? null;
}

/**
 * The file is scheduled for delete (or is the source of a pending rename) but
 * exists again on disk. Cancel the pending delete keeping the local content,
 * then reopen the file for edit.
 *
 * p4 refuses to revert a move/delete source on its own ('has been moved, not
 * reverted', exit 0); reverting the move/add half clears both ends of the pair.
 */
function reopenAfterPendingDelete(filePath, info, action) {
  if (action === 'move/delete') {
    const target = movedCounterpart(info);
    p4(['revert', '-k', target ?? filePath]);
    // The renamed half is now untracked but still on disk; reopen it for add
    // so the earlier rename isn't silently dropped from the changelist.
    if (target) p4(['add', target]);
  } else {
    p4(['revert', '-k', filePath]);
  }
  const editResult = p4(['edit', filePath]);
  if (editResult.exitCode === 0) return okResult('File reopened for edit after pending delete was reverted');
  return errorResult('error', `Cannot reopen '${filePath}' for edit after reverting pending delete: ${editResult.error || editResult.output}`);
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

    const info = fstat(filePath);
    if (!isInDepotFstat(info)) {
      return fs.prepareToWrite(filePath);
    }

    // Re-created on disk while scheduled for delete: cancel the delete first.
    // `p4 edit` on a pending-delete file only warns (exit 0) and leaves the
    // delete pending, so the file would still be deleted at next submit.
    const action = pendingAction(info);
    if (action === 'delete' || action === 'move/delete') {
      return reopenAfterPendingDelete(filePath, info, action);
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
    const action = pendingAction(info);
    if (action === 'delete' || action === 'move/delete') {
      return reopenAfterPendingDelete(filePath, info, action);
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
    // No early-out on a missing local file: the file may still be opened in a
    // pending changelist (e.g. added then deleted between submits), and that
    // stale entry would abort the eventual submit and leave it locked.
    const info = fstat(filePath);
    const action = pendingAction(info);

    const removeLocal = () => {
      if (!existsSync(filePath)) return null;
      try {
        chmodSync(filePath, 0o666);
        unlinkSync(filePath);
        return null;
      } catch (e) {
        return errorResult('error', `Cannot remove '${filePath}' from disk: ${e.message}`);
      }
    };

    // Open for add (never submitted): there is nothing in the depot to delete.
    // `p4 delete` would only warn (exit 0) and leave the add pending, so
    // revert it, then remove the local file.
    if (action === 'add') {
      p4(['revert', '-k', filePath]);
      return removeLocal() ?? okResult();
    }

    // Target half of a pending rename: cancel the move (reverting the move/add
    // half clears both ends, keeping disk files), schedule the original path
    // for delete (p4 delete works without a local copy), and remove this one.
    if (action === 'move/add') {
      const source = movedCounterpart(info);
      p4(['revert', '-k', filePath]);
      if (source) p4(['delete', source]);
      return removeLocal() ?? okResult();
    }

    // Already scheduled for delete: just make sure the local copy is gone.
    if (action === 'delete' || action === 'move/delete') {
      return removeLocal() ?? okResult();
    }

    // Open for edit/integrate/etc: revert before scheduling the delete, since
    // `p4 delete` refuses files that are already open (warns, exit 0).
    if (action !== null) {
      p4(['revert', '-k', filePath]);
    }

    if (isInDepotFstat(info)) {
      // Remove the local copy first: `p4 delete` refuses to clobber a writable
      // local file (e.g. right after the edit revert above), but happily
      // schedules the delete when the local copy is already gone.
      const localError = removeLocal();
      if (localError) return localError;
      const result = p4(['delete', filePath]);
      if (result.exitCode !== 0)
        return errorResult('error', `Cannot delete '${filePath}' from Perforce: ${result.error || result.output}`);
      return okResult();
    }

    return removeLocal() ?? okResult();
  }

  renameFile(oldPath, newPath) {
    if (!existsSync(oldPath)) return okResult();

    // A pending state on the destination blocks the rename: p4 move refuses
    // to target an opened or existing file ('can't move to an existing file',
    // and only warns at exit 0). Clear it first.
    const dstInfo = fstat(newPath);
    const dstAction = pendingAction(dstInfo);
    if (dstAction === 'add' || dstAction === 'move/add') {
      // The destination was created in this changelist: deleting it unwinds
      // the pending add (and any rename pair) and removes the stale local copy.
      const cleared = this.deleteFile(newPath);
      if (!cleared.success) return cleared;
    } else if (dstAction === 'delete' || dstAction === 'move/delete') {
      // The destination exists at head and is scheduled for delete; p4 move
      // cannot target it even so. Emulate the rename: cancel the delete, carry
      // the source content over, reopen the destination for edit, and delete
      // the source path. (The edit must come after the file is in place —
      // p4 edit fails trying to chmod a missing local file.)
      p4(['revert', '-k', newPath]);
      try {
        if (existsSync(newPath)) {
          chmodSync(newPath, 0o666);
          unlinkSync(newPath);
        }
      } catch (e) {
        return errorResult('error', `Cannot rename '${oldPath}' over '${newPath}': ${e.message}`);
      }
      const moved = fs.renameFile(oldPath, newPath);
      if (!moved.success) return moved;
      const editResult = p4(['edit', newPath]);
      if (editResult.exitCode !== 0)
        return errorResult('error', `Cannot rename '${oldPath}' over pending-delete '${newPath}': ${editResult.error || editResult.output}`);
      return this.deleteFile(oldPath);
    }

    const info = fstat(oldPath);
    if (!isInDepotFstat(info)) return fs.renameFile(oldPath, newPath);

    const action = pendingAction(info);
    if (action === 'delete' || action === 'move/delete') {
      // Re-created on disk while scheduled for delete: cancel the delete so
      // the file can be reopened and moved.
      const reopened = reopenAfterPendingDelete(oldPath, info, action);
      if (!reopened.success) return reopened;
    } else if (action === null) {
      p4(['edit', oldPath]);
    }
    // Files already open for add/edit/move-add can be moved directly.

    const result = p4(['move', oldPath, newPath]);
    // p4 move reports several refusals at exit 0 ('not opened for edit',
    // 'can't move to an existing file', 'is synced; use -f') — require the
    // positive 'moved from' confirmation instead of blacklisting them.
    const combined = ((result.output || '') + ' ' + (result.error || '')).toLowerCase();
    if (result.exitCode === 0 && combined.includes('moved from')) return okResult();
    return errorResult('error', `Cannot rename '${oldPath}' in Perforce: ${result.error || result.output}`);
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
        status.dirty = false;
        return status;
      }

      const has = (field) => record.fields.has(field);
      const headAction = record.fields.get('headAction') ?? '';
      const deletedAtHead = headAction === 'delete' || headAction === 'move/delete';
      const openedByMe = has('action');
      status.tracked = (has('headRev') || has('depotFile')) && (!deletedAtHead || openedByMe);
      if (openedByMe || has('ourLock')) status.openedByMe = true;
      // Opened in a pending changelist (edit/add/delete) = pending local change.
      // A file changed on disk without being opened is not detected here.
      status.dirty = openedByMe;

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
