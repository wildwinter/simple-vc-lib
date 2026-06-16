import { existsSync, accessSync, chmodSync, statSync, unlinkSync, rmSync, renameSync, constants } from 'fs';
import { okResult, errorResult } from '../vcResult.js';
import { writableBit } from '../vcStatus.js';
import { resolve } from 'path';

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

  // Filesystem operations are local and synchronous; the async twins exist only so
  // callers can treat every provider uniformly. They do no real awaiting.
  prepareToWriteAsync(filePath) { return Promise.resolve(this.prepareToWrite(filePath)); }
  finishedWriteAsync(filePath) { return Promise.resolve(this.finishedWrite(filePath)); }
  deleteFileAsync(filePath) { return Promise.resolve(this.deleteFile(filePath)); }
  deleteFolderAsync(folderPath) { return Promise.resolve(this.deleteFolder(folderPath)); }
  renameFileAsync(oldPath, newPath) { return Promise.resolve(this.renameFile(oldPath, newPath)); }
  renameFolderAsync(oldPath, newPath) { return Promise.resolve(this.renameFolder(oldPath, newPath)); }

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

  /**
   * Status for a batch of files. No VCS: just the writable bit.
   *
   * @param {string[]} filePaths
   * @returns {import('../vcStatus.js').VCFileStatus[]}
   */
  status(filePaths) {
    return filePaths.map((filePath) => {
      const abs = resolve(filePath);
      return { filePath: abs, system: 'filesystem', writable: writableBit(abs) };
    });
  }

  /** Async twin of {@link status}. No commands to spawn - resolves immediately. */
  statusAsync(filePaths) {
    return Promise.resolve(this.status(filePaths));
  }
}

