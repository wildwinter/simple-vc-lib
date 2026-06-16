import { existsSync, accessSync, chmodSync, statSync, unlinkSync, rmSync, renameSync, constants } from 'fs';
import { dirname, resolve, basename } from 'path';
import { runCommand, runCommandAsync } from '../commandRunner.js';
import { okResult, errorResult } from '../vcResult.js';
import { writableBit } from '../vcStatus.js';
import { FilesystemProvider } from './filesystemProvider.js';

const fs = new FilesystemProvider();

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

function git(args, cwd, options = {}) {
  return runCommand('git', gitArgv(args, cwd), { cwd, ...options });
}

function gitAsync(args, cwd, options = {}) {
  return runCommandAsync('git', gitArgv(args, cwd), { cwd, ...options });
}

function gitArgv(args, cwd) {
  const isWindows = process.platform === 'win32';
  const safeCwd = isWindows ? cwd.replace(/\\/g, '/') : cwd;
  const safeArgs = isWindows ? args.map(arg => typeof arg === 'string' ? arg.replace(/\\/g, '/') : arg) : args;
  return ['-C', safeCwd, ...safeArgs];
}

/**
 * Returns true if the file is tracked by git (exists in the index).
 */
function isTracked(filePath) {
  const result = git(['ls-files', '--error-unmatch', filePath], dirname(filePath));
  return result.exitCode === 0;
}

async function isTrackedAsync(filePath) {
  const result = await gitAsync(['ls-files', '--error-unmatch', filePath], dirname(filePath));
  return result.exitCode === 0;
}

/**
 * Returns true if the given directory is inside a git repository.
 */
function isInRepo(dir) {
  return git(['rev-parse', '--git-dir'], dir).exitCode === 0;
}

async function isInRepoAsync(dir) {
  return (await gitAsync(['rev-parse', '--git-dir'], dir)).exitCode === 0;
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
      return errorResult('error', `'${filePath}' does not exist after write`);

    if (isTracked(filePath)) return okResult();

    const cwd = dirname(filePath);

    // File is outside the git repo entirely — no git action needed.
    if (!isInRepo(cwd))
      return fs.finishedWrite(filePath);

    const result = git(['add', filePath], cwd);
    if (result.exitCode === 0) return okResult('File added to git');
    // File is ignored by .gitignore — treat as outside the repo.
    const combined = (result.output + ' ' + result.error).toLowerCase();
    if (combined.includes('ignored')) return fs.finishedWrite(filePath);
    return errorResult('error', `Cannot add '${filePath}' to git: ${result.error || result.output}`);
  }

  // git prepareToWrite is pure local fs (no spawn), so the async twin just wraps it.
  prepareToWriteAsync(filePath) { return Promise.resolve(this.prepareToWrite(filePath)); }

  /** Async twin of {@link finishedWrite}. */
  async finishedWriteAsync(filePath) {
    if (!existsSync(filePath))
      return errorResult('error', `'${filePath}' does not exist after write`);
    if (await isTrackedAsync(filePath)) return okResult();

    const cwd = dirname(filePath);
    if (!(await isInRepoAsync(cwd)))
      return fs.finishedWriteAsync(filePath);

    const result = await gitAsync(['add', filePath], cwd);
    if (result.exitCode === 0) return okResult('File added to git');
    const combined = (result.output + ' ' + result.error).toLowerCase();
    if (combined.includes('ignored')) return fs.finishedWriteAsync(filePath);
    return errorResult('error', `Cannot add '${filePath}' to git: ${result.error || result.output}`);
  }

  /** Async twin of {@link deleteFile}. */
  async deleteFileAsync(filePath) {
    if (!existsSync(filePath)) return okResult();
    if (await isTrackedAsync(filePath)) {
      const result = await gitAsync(['rm', '--force', filePath], dirname(filePath));
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot delete '${filePath}' from git: ${result.error || result.output}`);
    }
    try {
      unlinkSync(filePath);
      return okResult();
    } catch (e) {
      return errorResult('error', `Failed to delete file: ${e.message}`);
    }
  }

  /** Async twin of {@link deleteFolder}. */
  async deleteFolderAsync(folderPath) {
    if (!existsSync(folderPath)) return okResult();
    const listResult = await gitAsync(['ls-files', folderPath], folderPath);
    if (listResult.exitCode === 0 && listResult.output.length > 0) {
      const rmResult = await gitAsync(['rm', '-r', '--force', folderPath], folderPath);
      if (rmResult.exitCode !== 0)
        return errorResult('error', `Cannot delete folder '${folderPath}' from git: ${rmResult.error || rmResult.output}`);
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

  /** Async twin of {@link renameFile}. */
  async renameFileAsync(oldPath, newPath) {
    if (!existsSync(oldPath)) return okResult();
    if (await isTrackedAsync(oldPath)) {
      const result = await gitAsync(['mv', oldPath, newPath], dirname(oldPath));
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot rename '${oldPath}' in git: ${result.error || result.output}`);
    }
    try {
      renameSync(oldPath, newPath);
      return okResult();
    } catch (e) {
      return errorResult('error', `Cannot rename '${oldPath}': ${e.message}`);
    }
  }

  /** Async twin of {@link renameFolder}. */
  async renameFolderAsync(oldPath, newPath) {
    if (!existsSync(oldPath)) return okResult();
    const result = await gitAsync(['mv', oldPath, newPath], dirname(oldPath));
    if (result.exitCode === 0) return okResult();
    try {
      renameSync(oldPath, newPath);
      return okResult();
    } catch (e) {
      return errorResult('error', `Cannot rename folder '${oldPath}': ${e.message}`);
    }
  }

  deleteFile(filePath) {
    if (!existsSync(filePath)) return okResult();

    if (isTracked(filePath)) {
      const result = git(['rm', '--force', filePath], dirname(filePath));
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot delete '${filePath}' from git: ${result.error || result.output}`);
    }

    try {
      unlinkSync(filePath);
      return okResult();
    } catch (e) {
      return errorResult('error', `Failed to delete file: ${e.message}`);
    }
  }

  renameFile(oldPath, newPath) {
    if (!existsSync(oldPath)) return okResult();
    if (isTracked(oldPath)) {
      const result = git(['mv', oldPath, newPath], dirname(oldPath));
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot rename '${oldPath}' in git: ${result.error || result.output}`);
    }
    try {
      renameSync(oldPath, newPath);
      return okResult();
    } catch (e) {
      return errorResult('error', `Cannot rename '${oldPath}': ${e.message}`);
    }
  }

  renameFolder(oldPath, newPath) {
    if (!existsSync(oldPath)) return okResult();
    const result = git(['mv', oldPath, newPath], dirname(oldPath));
    if (result.exitCode === 0) return okResult();
    // Fall back to filesystem rename for untracked folders.
    try {
      renameSync(oldPath, newPath);
      return okResult();
    } catch (e) {
      return errorResult('error', `Cannot rename folder '${oldPath}': ${e.message}`);
    }
  }

  deleteFolder(folderPath) {
    if (!existsSync(folderPath)) return okResult();

    const listResult = git(['ls-files', folderPath], folderPath);
    if (listResult.exitCode === 0 && listResult.output.length > 0) {
      const rmResult = git(['rm', '-r', '--force', folderPath], folderPath);
      if (rmResult.exitCode !== 0) {
        return errorResult('error', `Cannot delete folder '${folderPath}' from git: ${rmResult.error || rmResult.output}`);
      }
    }

    // Delete any remaining untracked files git rm left behind.
    if (existsSync(folderPath)) {
      try {
        rmSync(folderPath, { recursive: true, force: true });
      } catch (e) {
        return errorResult('error', `Cannot delete folder '${folderPath}': ${e.message}`);
      }
    }

    return okResult();
  }

  /**
   * Status for a batch of files: per repository root, ONE
   * `git status --porcelain -z` (tracked-ness) plus - when git-lfs is in play -
   * ONE `git lfs locks --verify --json` (`--verify` splits ours vs theirs).
   * git itself has no locks; lock-based git workflows are git-lfs locks.
   *
   * All matching is done on REPO-RELATIVE paths (via `rev-parse --show-prefix`),
   * never by joining absolute paths - so symlinked ancestors (macOS `/var` ->
   * `/private/var`, `/tmp` -> `/private/tmp`) cannot break the lookup.
   *
   * @param {string[]} filePaths
   * @returns {import('../vcStatus.js').VCFileStatus[]}
   */
  status(filePaths) {
    // Group by repository root (one rev-parse per unique directory, cached).
    const infoByDir = new Map();
    const groups = new Map();
    for (const filePath of filePaths) {
      const abs = resolve(filePath);
      const dir = dirname(abs);
      let info = infoByDir.get(dir);
      if (info === undefined) {
        info = gitDirInfo(git(['rev-parse', '--show-toplevel', '--show-prefix'], dir));
        infoByDir.set(dir, info);
      }
      addToGitGroup(groups, info, filePath, abs);
    }

    const byInput = new Map();
    for (const [root, files] of groups) {
      if (root === '(none)') { reportWritableOnly(files, byInput); continue; }
      const st = git(['status', '--porcelain', '-z', '--', ...files.map((f) => f.relKey)], root, { trim: false });
      const locks = git(['lfs', 'locks', '--verify', '--json'], root);
      assembleGitStatuses(files, gitStates(st), gitLocks(locks), byInput);
    }
    return filePaths.map((filePath) => byInput.get(filePath));
  }

  /**
   * Async twin of {@link status}. Within each repo the porcelain status and the
   * git-lfs lock list are independent, so they run concurrently.
   */
  async statusAsync(filePaths) {
    const infoByDir = new Map();
    const groups = new Map();
    for (const filePath of filePaths) {
      const abs = resolve(filePath);
      const dir = dirname(abs);
      let info = infoByDir.get(dir);
      if (info === undefined) {
        info = gitDirInfo(await gitAsync(['rev-parse', '--show-toplevel', '--show-prefix'], dir));
        infoByDir.set(dir, info);
      }
      addToGitGroup(groups, info, filePath, abs);
    }

    const byInput = new Map();
    for (const [root, files] of groups) {
      if (root === '(none)') { reportWritableOnly(files, byInput); continue; }
      const [st, locks] = await Promise.all([
        gitAsync(['status', '--porcelain', '-z', '--', ...files.map((f) => f.relKey)], root, { trim: false }),
        gitAsync(['lfs', 'locks', '--verify', '--json'], root),
      ]);
      assembleGitStatuses(files, gitStates(st), gitLocks(locks), byInput);
    }
    return filePaths.map((filePath) => byInput.get(filePath));
  }
}

/** Parse one `rev-parse --show-toplevel --show-prefix` result into {root, prefix}. */
function gitDirInfo(result) {
  if (result.exitCode === 0) {
    const lines = result.output.split('\n');
    return { root: lines[0].trim(), prefix: (lines[1] ?? '').trim() };
  }
  return { root: null, prefix: '' };
}

/** Bucket a file under its repo root (or '(none)' when outside any repo). */
function addToGitGroup(groups, info, filePath, abs) {
  const key = info.root ?? '(none)';
  if (!groups.has(key)) groups.set(key, []);
  groups.get(key).push({ filePath, abs, relKey: info.prefix + basename(abs) });
}

/** Files outside any repo: report the writable bit only. */
function reportWritableOnly(files, byInput) {
  for (const { filePath, abs } of files) {
    byInput.set(filePath, { filePath: abs, system: 'git', writable: writableBit(abs) });
  }
}

/**
 * `git status --porcelain -z` -> Map(repoRelPath -> 'XY'). `-z` entries are
 * `XY <path>\0`; the path begins at index 3 (after the two status chars + space).
 */
function gitStates(st) {
  const states = new Map();
  if (st.exitCode === 0 && st.output) {
    for (const entry of st.output.split('\0')) {
      if (entry.length < 4) continue;
      states.set(entry.slice(3), entry.slice(0, 2));
    }
  }
  return states;
}

/** `git lfs locks --verify --json` -> {ours: Set, theirs: Map(path -> owners)}. */
function gitLocks(locks) {
  const ours = new Set();
  const theirs = new Map();
  if (locks.exitCode === 0 && locks.output.startsWith('{')) {
    try {
      const parsed = JSON.parse(locks.output);
      for (const lock of parsed.ours ?? []) ours.add(lock.path);
      for (const lock of parsed.theirs ?? []) {
        theirs.set(lock.path, [...(theirs.get(lock.path) ?? []), lock.owner?.name ?? 'unknown']);
      }
    } catch {
      // Unparseable lock output - status is still useful without it.
    }
  }
  return { ours, theirs };
}

/** Build per-file statuses for one repo's files from its porcelain + lock data. */
function assembleGitStatuses(files, states, { ours, theirs }, byInput) {
  for (const { filePath, abs, relKey } of files) {
    // Absent from porcelain = clean & tracked; '??' = untracked; any other code
    // (M/A/D/R/MM/...) = a tracked file with pending changes.
    const code = states.get(relKey);
    /** @type {import('../vcStatus.js').VCFileStatus} */
    const status = {
      filePath: abs,
      system: 'git',
      writable: writableBit(abs),
      tracked: code !== '??',
      dirty: code !== undefined && code !== '??',
    };
    if (ours.has(relKey)) status.openedByMe = true;
    const owners = theirs.get(relKey);
    if (owners) status.lockedBy = owners;
    byInput.set(filePath, status);
  }
}
