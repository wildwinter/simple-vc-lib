export type VCStatus = 'ok' | 'locked' | 'outOfDate' | 'error';

export interface VCResult {
  success: boolean;
  status: VCStatus;
  message: string;
}

export interface IVCProvider {
  readonly name: string;
  prepareToWrite(filePath: string): VCResult;
  finishedWrite(filePath: string): VCResult;
  deleteFile(filePath: string): VCResult;
  deleteFolder(folderPath: string): VCResult;
  renameFile(oldPath: string, newPath: string): VCResult;
  renameFolder(oldPath: string, newPath: string): VCResult;
}

export declare class GitProvider implements IVCProvider {
  readonly name: 'git';
  prepareToWrite(filePath: string): VCResult;
  finishedWrite(filePath: string): VCResult;
  deleteFile(filePath: string): VCResult;
  deleteFolder(folderPath: string): VCResult;
  renameFile(oldPath: string, newPath: string): VCResult;
  renameFolder(oldPath: string, newPath: string): VCResult;
}

export declare class PerforceProvider implements IVCProvider {
  readonly name: 'perforce';
  prepareToWrite(filePath: string): VCResult;
  finishedWrite(filePath: string): VCResult;
  deleteFile(filePath: string): VCResult;
  deleteFolder(folderPath: string): VCResult;
  renameFile(oldPath: string, newPath: string): VCResult;
  renameFolder(oldPath: string, newPath: string): VCResult;
}

export declare class PlasticProvider implements IVCProvider {
  readonly name: 'plastic';
  prepareToWrite(filePath: string): VCResult;
  finishedWrite(filePath: string): VCResult;
  deleteFile(filePath: string): VCResult;
  deleteFolder(folderPath: string): VCResult;
  renameFile(oldPath: string, newPath: string): VCResult;
  renameFolder(oldPath: string, newPath: string): VCResult;
}

export declare class SvnProvider implements IVCProvider {
  readonly name: 'svn';
  prepareToWrite(filePath: string): VCResult;
  finishedWrite(filePath: string): VCResult;
  deleteFile(filePath: string): VCResult;
  deleteFolder(folderPath: string): VCResult;
  renameFile(oldPath: string, newPath: string): VCResult;
  renameFolder(oldPath: string, newPath: string): VCResult;
}

export declare class FilesystemProvider implements IVCProvider {
  readonly name: 'filesystem';
  prepareToWrite(filePath: string): VCResult;
  finishedWrite(filePath: string): VCResult;
  deleteFile(filePath: string): VCResult;
  deleteFolder(folderPath: string): VCResult;
  renameFile(oldPath: string, newPath: string): VCResult;
  renameFolder(oldPath: string, newPath: string): VCResult;
}

/**
 * Prepare a file path for writing.
 * Checks out or unlocks the file in VC if it is read-only.
 * No-op if the file does not yet exist.
 */
export declare function prepareToWrite(filePath: string): VCResult;

/**
 * Notify the library that a file has been written.
 * Adds the file to VC if it is not yet tracked. No-op for existing tracked files.
 */
export declare function finishedWrite(filePath: string): VCResult;

/**
 * Delete a file, marking it for deletion in VC if tracked.
 */
export declare function deleteFile(filePath: string): VCResult;

/**
 * Delete a folder and all its contents, marking tracked files for deletion in VC.
 */
export declare function deleteFolder(folderPath: string): VCResult;

/**
 * Rename a file, informing VC of the change if the file is tracked.
 * No-op if the source does not exist.
 */
export declare function renameFile(oldPath: string, newPath: string): VCResult;

/**
 * Rename a folder, informing VC of the change for all tracked contents.
 * No-op if the source does not exist.
 */
export declare function renameFolder(oldPath: string, newPath: string): VCResult;

/**
 * Write text to a file, handling VC checkout and registration automatically.
 * Calls prepareToWrite, writes the file, then calls finishedWrite.
 */
export declare function writeTextFile(filePath: string, content: string, encoding?: BufferEncoding): VCResult;

/**
 * Write binary data to a file, handling VC checkout and registration automatically.
 * Calls prepareToWrite, writes the file, then calls finishedWrite.
 */
export declare function writeBinaryFile(filePath: string, data: Buffer | Uint8Array): VCResult;

/**
 * Override the provider used for all operations.
 * Pass null to clear the override and restore auto-detection.
 */
export declare function setProvider(provider: IVCProvider | null): void;

/** Clear any previously set provider override, restoring auto-detection. */
export declare function clearProvider(): void;
