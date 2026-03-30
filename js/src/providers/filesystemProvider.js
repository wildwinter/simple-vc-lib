import { existsSync, accessSync, chmodSync, statSync, unlinkSync, rmSync, renameSync, constants } from 'fs';
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
    chmodSync(filePath, mode | 0o200); // Adds owner-write bit.
    return isWritable(filePath);
  } catch {
    return false;
  }
}

/**
 * Plain-filesystem provider: no VC system, just file-system operations.
 * Used as the fallback when no VC is detected.
 */
export class FilesystemProvider {
  get name() { return 'filesystem'; }

  prepareToWrite(filePath) {
    if (!existsSync(filePath)) return okResult();
    if (isWritable(filePath)) return okResult();
    if (makeWritable(filePath)) return okResult('File made writable');
    return errorResult('error', `Cannot make '${filePath}' writable`);
  }

  finishedWrite(filePath) {
    if (!existsSync(filePath))
      return errorResult('error', `'${filePath}' does not exist after write`);
    return okResult();
  }

  deleteFile(filePath) {
    if (!existsSync(filePath)) return okResult();
    try {
      unlinkSync(filePath);
      return okResult();
    } catch (e) {
      return errorResult('error', `Cannot delete '${filePath}': ${e.message}`);
    }
  }

  deleteFolder(folderPath) {
    if (!existsSync(folderPath)) return okResult();
    try {
      rmSync(folderPath, { recursive: true, force: true });
      return okResult();
    } catch (e) {
      return errorResult('error', `Cannot delete folder '${folderPath}': ${e.message}`);
    }
  }

  renameFile(oldPath, newPath) {
    if (!existsSync(oldPath)) return okResult();
    try {
      renameSync(oldPath, newPath);
      return okResult();
    } catch (e) {
      return errorResult('error', `Cannot rename '${oldPath}' to '${newPath}': ${e.message}`);
    }
  }

  renameFolder(oldPath, newPath) {
    if (!existsSync(oldPath)) return okResult();
    try {
      renameSync(oldPath, newPath);
      return okResult();
    } catch (e) {
      return errorResult('error', `Cannot rename folder '${oldPath}' to '${newPath}': ${e.message}`);
    }
  }
}
