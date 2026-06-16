export type VCStatus = 'ok' | 'locked' | 'outOfDate' | 'error';

export interface VCResult {
  success: boolean;
  status: VCStatus;
  message: string;
}


export type VCSystem = 'git' | 'perforce' | 'plastic' | 'svn' | 'filesystem';

/** Per-file status, as returned by fileStatus / a provider's status. */
export interface VCFileStatus {
  /** Absolute path of the file. */
  filePath: string;
  system: VCSystem;
  /** Writable on disk right now (the read-only bit - lock workflows key off this). */
  writable: boolean;
  /** Known to the VCS (tracked / in the depot). Undefined when the provider cannot say. */
  tracked?: boolean;
  /** Opened / checked out / locked by the current user. */
  openedByMe?: boolean;
  /** Who else has it open or locked (e.g. "bob@bob-ws"). */
  lockedBy?: string[];
  /** A newer revision exists on the server. */
  outOfDate?: boolean;
  /**
   * Has pending local VC changes: a tracked file that is modified / staged / opened /
   * added / deleted but not yet committed. Untracked files are not dirty (they surface
   * via `tracked: false`). The cheap, local notion - it does not detect a file edited
   * outside VC. Undefined when the provider cannot say.
   */
  dirty?: boolean;
}

/** The outcome of one file in a writeTextFiles batch. */
export interface VCWriteOutcome {
  filePath: string;
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
  /** Batched per-file status (one spawn per provider / repository, not per file). */
  status(filePaths: string[]): VCFileStatus[];
}

export declare class GitProvider implements IVCProvider {
  readonly name: 'git';
  prepareToWrite(filePath: string): VCResult;
  finishedWrite(filePath: string): VCResult;
  deleteFile(filePath: string): VCResult;
  deleteFolder(folderPath: string): VCResult;
  renameFile(oldPath: string, newPath: string): VCResult;
  renameFolder(oldPath: string, newPath: string): VCResult;
  /** Batched per-file status (one spawn per provider / repository, not per file). */
  status(filePaths: string[]): VCFileStatus[];
}

export declare class PerforceProvider implements IVCProvider {
  readonly name: 'perforce';
  prepareToWrite(filePath: string): VCResult;
  finishedWrite(filePath: string): VCResult;
  deleteFile(filePath: string): VCResult;
  deleteFolder(folderPath: string): VCResult;
  renameFile(oldPath: string, newPath: string): VCResult;
  renameFolder(oldPath: string, newPath: string): VCResult;
  /** Batched per-file status (one spawn per provider / repository, not per file). */
  status(filePaths: string[]): VCFileStatus[];
}

export declare class PlasticProvider implements IVCProvider {
  readonly name: 'plastic';
  prepareToWrite(filePath: string): VCResult;
  finishedWrite(filePath: string): VCResult;
  deleteFile(filePath: string): VCResult;
  deleteFolder(folderPath: string): VCResult;
  renameFile(oldPath: string, newPath: string): VCResult;
  renameFolder(oldPath: string, newPath: string): VCResult;
  /** Batched per-file status (one spawn per provider / repository, not per file). */
  status(filePaths: string[]): VCFileStatus[];
}

export declare class SvnProvider implements IVCProvider {
  readonly name: 'svn';
  prepareToWrite(filePath: string): VCResult;
  finishedWrite(filePath: string): VCResult;
  deleteFile(filePath: string): VCResult;
  deleteFolder(folderPath: string): VCResult;
  renameFile(oldPath: string, newPath: string): VCResult;
  renameFolder(oldPath: string, newPath: string): VCResult;
  /** Batched per-file status (one spawn per provider / repository, not per file). */
  status(filePaths: string[]): VCFileStatus[];
}

export declare class FilesystemProvider implements IVCProvider {
  readonly name: 'filesystem';
  prepareToWrite(filePath: string): VCResult;
  finishedWrite(filePath: string): VCResult;
  deleteFile(filePath: string): VCResult;
  deleteFolder(folderPath: string): VCResult;
  renameFile(oldPath: string, newPath: string): VCResult;
  renameFolder(oldPath: string, newPath: string): VCResult;
  /** Batched per-file status (one spawn per provider / repository, not per file). */
  status(filePaths: string[]): VCFileStatus[];
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
 * If the file already exists and its content matches, no VCS operations are
 * performed and the file is not written. Set forceWrite to true to always write.
 */
export declare function writeTextFile(filePath: string, content: string, encoding?: BufferEncoding, forceWrite?: boolean): VCResult;

/**
 * Write binary data to a file, handling VC checkout and registration automatically.
 * Calls prepareToWrite, writes the file, then calls finishedWrite.
 * If the file already exists and its content matches, no VCS operations are
 * performed and the file is not written. Set forceWrite to true to always write.
 */
export declare function writeBinaryFile(filePath: string, data: Buffer | Uint8Array, forceWrite?: boolean): VCResult;

/**
 * Write a batch of text files through VC, creating parent directories.
 * Every outcome is reported (a refusal carries its why); one refusal does not
 * stop the rest. Each write goes through writeTextFile.
 */
export declare function writeTextFiles(files: { filePath: string; content: string }[], encoding?: BufferEncoding): { success: boolean; results: VCWriteOutcome[] };

/**
 * Status for a batch of files: tracked / writable / locked-by / opened-by-me /
 * out-of-date / dirty. Paths are grouped by provider so a whole project costs a
 * spawn or two, not one per file.
 */
export declare function fileStatus(filePaths: string[]): VCFileStatus[];

/**
 * Override the command runner used for all VC operations - lets tests inject
 * canned CLI output (e.g. p4 -ztag fstat transcripts) so provider logic is
 * unit-testable without the VCS installed. Pass null to clear.
 */
export declare function setCommandRunner(runner: ((command: string, args: string[], options?: object) => { exitCode: number; output: string; error: string; timedOut?: boolean }) | null): void;

/** Clear any previously set command-runner override. */
export declare function clearCommandRunner(): void;

/**
 * Override the provider used for all operations.
 * Pass null to clear the override and restore auto-detection.
 */
export declare function setProvider(provider: IVCProvider | null): void;

/** Clear any previously set provider override, restoring auto-detection. */
export declare function clearProvider(): void;
