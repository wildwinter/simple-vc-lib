import { existsSync, accessSync, chmodSync, statSync, unlinkSync, rmSync, constants } from 'fs';
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
    chmodSync(filePath, mode | 0o200); // Add owner-write bit.
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
    return errorResult('error', `Cannot make file writable: ${filePath}`);
  }

  finishedWrite(filePath) {
    if (!existsSync(filePath))
      return errorResult('error', `File does not exist after write: ${filePath}`);
    return okResult();
  }

  deleteFile(filePath) {
    if (!existsSync(filePath)) return okResult();
    try {
      unlinkSync(filePath);
      return okResult();
    } catch (e) {
      return errorResult('error', `Failed to delete file: ${e.message}`);
    }
  }

  deleteFolder(folderPath) {
    if (!existsSync(folderPath)) return okResult();
    try {
      rmSync(folderPath, { recursive: true, force: true });
      return okResult();
    } catch (e) {
      return errorResult('error', `Failed to delete folder: ${e.message}`);
    }
  }
}
