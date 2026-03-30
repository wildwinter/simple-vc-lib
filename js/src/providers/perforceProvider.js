import { existsSync, unlinkSync, chmodSync, rmSync, cpSync, mkdirSync } from 'fs';
import { runCommand } from '../commandRunner.js';
import { okResult, errorResult } from '../vcResult.js';
import { FilesystemProvider } from './filesystemProvider.js';

const fs = new FilesystemProvider();

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
  const info = fstat(filePath);
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

    if (isInDepot(filePath)) return okResult();

    const result = p4(['add', filePath]);
    if (result.exitCode === 0) return okResult('File opened for add in Perforce');
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
}
