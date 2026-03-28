import { existsSync, accessSync, chmodSync, statSync, unlinkSync, rmSync, constants } from 'fs';
import { dirname } from 'path';
import { runCommand } from '../commandRunner.js';
import { okResult, errorResult } from '../vcResult.js';

function isWritable(filePath) {
  try {
    accessSync(filePath, constants.W_OK);
    return true;
  } catch {
    return false;
  }
}

function makeWritable(filePath) {
  try {
    const mode = statSync(filePath).mode;
    chmodSync(filePath, mode | 0o200);
    return isWritable(filePath);
  } catch {
    return false;
  }
}

function git(args, cwd) {
  return runCommand('git', args, { cwd });
}

/**
 * Returns true if the file is tracked by git (exists in the index).
 */
function isTracked(filePath) {
  const result = git(['ls-files', '--error-unmatch', filePath], dirname(filePath));
  return result.exitCode === 0;
}

/**
 * Git provider.
 *
 * Git does not use file locking, so prepareToWrite only needs to ensure the
 * file is writable at the OS level. finishedWrite stages new files with git add.
 */
export class GitProvider {
  get name() { return 'git'; }

  prepareToWrite(filePath) {
    if (!existsSync(filePath)) return okResult();
    if (isWritable(filePath)) return okResult();
    if (makeWritable(filePath)) return okResult('File made writable');
    return errorResult('error', `Cannot make file writable: ${filePath}`);
  }

  finishedWrite(filePath) {
    if (!existsSync(filePath))
      return errorResult('error', `File does not exist after write: ${filePath}`);

    if (isTracked(filePath)) return okResult();

    const result = git(['add', filePath], dirname(filePath));
    if (result.exitCode === 0) return okResult('File added to git');
    return errorResult('error', `git add failed: ${result.error || result.output}`);
  }

  deleteFile(filePath) {
    if (!existsSync(filePath)) return okResult();

    if (isTracked(filePath)) {
      const result = git(['rm', '--force', filePath], dirname(filePath));
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `git rm failed: ${result.error || result.output}`);
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

    const listResult = git(['ls-files', folderPath], folderPath);
    if (listResult.exitCode === 0 && listResult.output.length > 0) {
      const rmResult = git(['rm', '-r', '--force', folderPath], folderPath);
      if (rmResult.exitCode !== 0) {
        return errorResult('error', `git rm -r failed: ${rmResult.error || rmResult.output}`);
      }
    }

    // Delete any remaining untracked files git rm left behind.
    if (existsSync(folderPath)) {
      try {
        rmSync(folderPath, { recursive: true, force: true });
      } catch (e) {
        return errorResult('error', `Failed to delete folder: ${e.message}`);
      }
    }

    return okResult();
  }
}
