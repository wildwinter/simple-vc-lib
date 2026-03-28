import { existsSync, unlinkSync, rmSync } from 'fs';
import { runCommand } from '../commandRunner.js';
import { okResult, errorResult } from '../vcResult.js';
import { FilesystemProvider } from './filesystemProvider.js';

const fs = new FilesystemProvider();

function svn(args) {
  return runCommand('svn', args);
}

/**
 * Returns true if the file is tracked by SVN.
 */
function isTracked(filePath) {
  const result = svn(['info', filePath]);
  return result.exitCode === 0;
}

/**
 * Subversion (SVN) provider.
 *
 * SVN files are normally writable. The exception is files with the
 * svn:needs-lock property, which are read-only until locked.
 * prepareToWrite handles this by calling `svn lock`.
 */
export class SvnProvider {
  get name() { return 'svn'; }

  prepareToWrite(filePath) {
    if (!existsSync(filePath)) return okResult();

    const fsResult = fs.prepareToWrite(filePath);
    if (fsResult.success) return okResult();

    // File is read-only — only expected for files with svn:needs-lock set.
    if (!isTracked(filePath)) {
      return errorResult('error', `Cannot make '${filePath}' writable`);
    }

    const result = svn(['lock', filePath]);
    if (result.exitCode === 0) return okResult('File locked in SVN');

    const combined = (result.output + ' ' + result.error).toLowerCase();
    if (combined.includes('locked by') || combined.includes('steal lock')) {
      return errorResult('locked', `'${filePath}' is locked by another user`);
    }
    if (combined.includes('out of date')) {
      return errorResult('outOfDate', `'${filePath}' is out of date — update before locking`);
    }
    return errorResult('error', `Cannot lock '${filePath}' in SVN: ${result.error || result.output}`);
  }

  finishedWrite(filePath) {
    if (!existsSync(filePath))
      return errorResult('error', `'${filePath}' does not exist after write`);

    if (isTracked(filePath)) return okResult();

    const result = svn(['add', filePath]);
    if (result.exitCode === 0) return okResult('File added to SVN');
    return errorResult('error', `Cannot add '${filePath}' to SVN: ${result.error || result.output}`);
  }

  deleteFile(filePath) {
    if (!existsSync(filePath)) return okResult();

    if (isTracked(filePath)) {
      const result = svn(['delete', '--force', filePath]);
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot delete '${filePath}' from SVN: ${result.error || result.output}`);
    }

    try {
      unlinkSync(filePath);
      return okResult();
    } catch (e) {
      return errorResult('error', `Failed to delete file: ${e.message}`);
    }
  }

  deleteFolder(folderPath) {
    if (!existsSync(folderPath)) return okResult();

    if (isTracked(folderPath)) {
      const result = svn(['delete', '--force', folderPath]);
      if (result.exitCode !== 0) {
        return errorResult('error', `Cannot delete folder '${folderPath}' from SVN: ${result.error || result.output}`);
      }
    } else {
      try {
        rmSync(folderPath, { recursive: true, force: true });
      } catch (e) {
        return errorResult('error', `Failed to delete folder: ${e.message}`);
      }
    }

    return okResult();
  }
}
