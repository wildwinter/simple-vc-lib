import { existsSync, unlinkSync, rmSync } from 'fs';
import { runCommand } from '../commandRunner.js';
import { okResult, errorResult } from '../vcResult.js';
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
}
