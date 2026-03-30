import { assert } from 'chai';
import { mkdtempSync, writeFileSync, mkdirSync, existsSync, chmodSync, statSync } from 'fs';
import { tmpdir } from 'os';
import { join } from 'path';
import { spawnSync } from 'child_process';

import {
  prepareToWrite, finishedWrite, deleteFile, deleteFolder, renameFile, renameFolder,
  setProvider, clearProvider,
  GitProvider, FilesystemProvider, SvnProvider,
} from '../src/index.js';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function makeTempDir() {
  return mkdtempSync(join(tmpdir(), 'simple-vc-lib-test-'));
}

function initSvnRepo() {
  const repoDir = makeTempDir();
  const wcDir = makeTempDir();
  spawnSync('svnadmin', ['create', repoDir], { encoding: 'utf8' });
  spawnSync('svn', ['checkout', `file://${repoDir}`, wcDir], { encoding: 'utf8' });
  // Commit an initial file so the repo has a revision.
  writeFileSync(join(wcDir, 'initial.txt'), 'initial');
  spawnSync('svn', ['add', 'initial.txt'], { cwd: wcDir, encoding: 'utf8' });
  spawnSync('svn', ['commit', '-m', 'initial', '--username', 'test', '--no-auth-cache'], { cwd: wcDir, encoding: 'utf8' });
  return wcDir;
}

function initGitRepo(dir) {
  const run = (args) => spawnSync('git', args, { cwd: dir, encoding: 'utf8' });
  run(['init']);
  run(['config', 'user.email', 'test@example.com']);
  run(['config', 'user.name', 'Test User']);
  // Create an initial commit so HEAD exists.
  const readme = join(dir, 'README.md');
  writeFileSync(readme, 'test');
  run(['add', 'README.md']);
  run(['commit', '-m', 'init']);
}

// ---------------------------------------------------------------------------
// FilesystemProvider tests
// ---------------------------------------------------------------------------

describe('FilesystemProvider', () => {
  let tempDir;

  beforeEach(() => {
    tempDir = makeTempDir();
    setProvider(new FilesystemProvider());
  });

  afterEach(() => {
    clearProvider();
  });

  it('prepareToWrite on a new file returns ok', () => {
    const result = prepareToWrite(join(tempDir, 'newfile.txt'));
    assert.isTrue(result.success);
    assert.equal(result.status, 'ok');
  });

  it('prepareToWrite on an existing writable file returns ok', () => {
    const filePath = join(tempDir, 'writable.txt');
    writeFileSync(filePath, 'content');
    const result = prepareToWrite(filePath);
    assert.isTrue(result.success);
    assert.equal(result.status, 'ok');
  });

  it('prepareToWrite on a read-only file makes it writable', () => {
    const filePath = join(tempDir, 'readonly.txt');
    writeFileSync(filePath, 'content');
    chmodSync(filePath, 0o444);

    const result = prepareToWrite(filePath);
    assert.isTrue(result.success, result.message);

    // File should now be writable.
    const mode = statSync(filePath).mode;
    assert.ok(mode & 0o200, 'Owner write bit should be set');
  });

  it('finishedWrite on an existing file returns ok', () => {
    const filePath = join(tempDir, 'written.txt');
    writeFileSync(filePath, 'content');
    const result = finishedWrite(filePath);
    assert.isTrue(result.success);
  });

  it('finishedWrite on a missing file returns failure', () => {
    const result = finishedWrite(join(tempDir, 'missing.txt'));
    assert.isFalse(result.success);
    assert.equal(result.status, 'error');
  });

  it('deleteFile removes an existing file', () => {
    const filePath = join(tempDir, 'todelete.txt');
    writeFileSync(filePath, 'bye');
    const result = deleteFile(filePath);
    assert.isTrue(result.success);
    assert.isFalse(existsSync(filePath));
  });

  it('deleteFile on a missing file returns ok', () => {
    const result = deleteFile(join(tempDir, 'ghost.txt'));
    assert.isTrue(result.success);
  });

  it('deleteFolder removes a folder and its contents', () => {
    const folderPath = join(tempDir, 'myfolder');
    mkdirSync(folderPath);
    writeFileSync(join(folderPath, 'a.txt'), 'a');
    writeFileSync(join(folderPath, 'b.txt'), 'b');

    const result = deleteFolder(folderPath);
    assert.isTrue(result.success);
    assert.isFalse(existsSync(folderPath));
  });

  it('deleteFolder on a missing folder returns ok', () => {
    const result = deleteFolder(join(tempDir, 'nonexistent'));
    assert.isTrue(result.success);
  });

  it('renameFile moves the file to the new path', () => {
    const oldPath = join(tempDir, 'original.txt');
    const newPath = join(tempDir, 'renamed.txt');
    writeFileSync(oldPath, 'content');

    const result = renameFile(oldPath, newPath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(oldPath));
    assert.isTrue(existsSync(newPath));
  });

  it('renameFile on a missing file returns ok', () => {
    const result = renameFile(join(tempDir, 'ghost.txt'), join(tempDir, 'other.txt'));
    assert.isTrue(result.success);
  });

  it('renameFolder moves the folder to the new path', () => {
    const oldPath = join(tempDir, 'oldfolder');
    const newPath = join(tempDir, 'newfolder');
    mkdirSync(oldPath);
    writeFileSync(join(oldPath, 'a.txt'), 'a');

    const result = renameFolder(oldPath, newPath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(oldPath));
    assert.isTrue(existsSync(join(newPath, 'a.txt')));
  });

  it('renameFolder on a missing folder returns ok', () => {
    const result = renameFolder(join(tempDir, 'nonexistent'), join(tempDir, 'other'));
    assert.isTrue(result.success);
  });
});

// ---------------------------------------------------------------------------
// GitProvider tests (uses a temporary git repo)
// ---------------------------------------------------------------------------

describe('GitProvider', () => {
  let repoDir;

  before(() => {
    repoDir = makeTempDir();
    initGitRepo(repoDir);
    setProvider(new GitProvider());
  });

  after(() => {
    clearProvider();
  });

  it('prepareToWrite on a new file returns ok', () => {
    const result = prepareToWrite(join(repoDir, 'newfile.txt'));
    assert.isTrue(result.success);
  });

  it('prepareToWrite on an existing writable tracked file returns ok', () => {
    const filePath = join(repoDir, 'README.md');
    const result = prepareToWrite(filePath);
    assert.isTrue(result.success);
  });

  it('prepareToWrite on a read-only file makes it writable', () => {
    const filePath = join(repoDir, 'readonly.txt');
    writeFileSync(filePath, 'content');
    chmodSync(filePath, 0o444);

    const result = prepareToWrite(filePath);
    assert.isTrue(result.success, result.message);

    const mode = statSync(filePath).mode;
    assert.ok(mode & 0o200);
  });

  it('finishedWrite adds a new file to git', () => {
    const filePath = join(repoDir, 'tracked-new.txt');
    writeFileSync(filePath, 'hello');

    const result = finishedWrite(filePath);
    assert.isTrue(result.success, result.message);

    // Verify it is now staged.
    const statusResult = spawnSync('git', ['status', '--short', filePath], {
      cwd: repoDir, encoding: 'utf8',
    });
    assert.match(statusResult.stdout.trim(), /^A /);
  });

  it('finishedWrite on an already-tracked file is a no-op', () => {
    // README.md was committed in initGitRepo.
    const result = finishedWrite(join(repoDir, 'README.md'));
    assert.isTrue(result.success);
  });

  it('deleteFile removes an untracked file', () => {
    const filePath = join(repoDir, 'untracked.txt');
    writeFileSync(filePath, 'temp');

    const result = deleteFile(filePath);
    assert.isTrue(result.success);
    assert.isFalse(existsSync(filePath));
  });

  it('deleteFile removes and unstages a tracked file', () => {
    // Stage a file first.
    const filePath = join(repoDir, 'staged.txt');
    writeFileSync(filePath, 'staged content');
    spawnSync('git', ['add', filePath], { cwd: repoDir });

    const result = deleteFile(filePath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(filePath));
  });

  it('renameFile renames an untracked file on disk', () => {
    const oldPath = join(repoDir, 'torename.txt');
    const newPath = join(repoDir, 'renamed.txt');
    writeFileSync(oldPath, 'content');

    const result = renameFile(oldPath, newPath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(oldPath));
    assert.isTrue(existsSync(newPath));
  });

  it('renameFile renames a tracked file via git mv', () => {
    const oldPath = join(repoDir, 'tracked-rename.txt');
    const newPath = join(repoDir, 'tracked-renamed.txt');
    writeFileSync(oldPath, 'hello');
    spawnSync('git', ['add', oldPath], { cwd: repoDir });
    spawnSync('git', ['commit', '-m', 'add tracked-rename'], { cwd: repoDir, encoding: 'utf8' });

    const result = renameFile(oldPath, newPath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(oldPath));
    assert.isTrue(existsSync(newPath));

    // Verify the new path is tracked in git's index (git mv was used, not a plain fs rename).
    const lsResult = spawnSync('git', ['ls-files', '--error-unmatch', newPath], { cwd: repoDir, encoding: 'utf8' });
    assert.equal(lsResult.status, 0, 'renamed file should be in git index');
  });

  it('renameFolder renames a folder with mixed tracked/untracked files', () => {
    const oldPath = join(repoDir, 'folder-to-rename');
    const newPath = join(repoDir, 'folder-renamed');
    mkdirSync(oldPath);
    const tracked = join(oldPath, 'tracked.txt');
    const untracked = join(oldPath, 'untracked.txt');
    writeFileSync(tracked, 'tracked');
    writeFileSync(untracked, 'untracked');
    spawnSync('git', ['add', tracked], { cwd: repoDir });

    const result = renameFolder(oldPath, newPath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(oldPath));
    assert.isTrue(existsSync(join(newPath, 'tracked.txt')));
    assert.isTrue(existsSync(join(newPath, 'untracked.txt')));
  });

  it('deleteFolder removes a folder with mixed tracked/untracked files', () => {
    const folderPath = join(repoDir, 'mixedfolder');
    mkdirSync(folderPath);

    const tracked = join(folderPath, 'tracked.txt');
    const untracked = join(folderPath, 'untracked.txt');
    writeFileSync(tracked, 'tracked');
    writeFileSync(untracked, 'untracked');
    spawnSync('git', ['add', tracked], { cwd: repoDir });

    const result = deleteFolder(folderPath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(folderPath));
  });
});

// ---------------------------------------------------------------------------
// SvnProvider tests (uses a temporary SVN repository via file:// URL)
// ---------------------------------------------------------------------------

describe('SvnProvider', function () {
  // SVN operations can be slower than git — give each test more breathing room.
  this.timeout(10000);

  let wcDir;

  before(function () {
    // Skip entire suite if svn tooling is not available.
    const check = spawnSync('svn', ['--version', '--quiet'], { encoding: 'utf8' });
    const adminCheck = spawnSync('svnadmin', ['--version', '--quiet'], { encoding: 'utf8' });
    if (check.status !== 0 || adminCheck.status !== 0) {
      this.skip();
    }
    wcDir = initSvnRepo();
    setProvider(new SvnProvider());
  });

  after(() => {
    clearProvider();
  });

  it('finishedWrite adds a new file to SVN', () => {
    const filePath = join(wcDir, 'new.txt');
    writeFileSync(filePath, 'hello');

    const result = finishedWrite(filePath);
    assert.isTrue(result.success, result.message);

    const status = spawnSync('svn', ['status', filePath], { encoding: 'utf8' });
    assert.match(status.stdout.trim(), /^A/);
  });

  it('finishedWrite on an already-tracked file is a no-op', () => {
    // initial.txt was committed in initSvnRepo.
    const result = finishedWrite(join(wcDir, 'initial.txt'));
    assert.isTrue(result.success);
  });

  it('deleteFile removes a committed file', () => {
    // Add and commit a file so it is fully tracked.
    const filePath = join(wcDir, 'todelete.txt');
    writeFileSync(filePath, 'bye');
    spawnSync('svn', ['add', filePath], { encoding: 'utf8' });
    spawnSync('svn', ['commit', '-m', 'add todelete', '--username', 'test', '--no-auth-cache'], { cwd: wcDir, encoding: 'utf8' });

    const result = deleteFile(filePath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(filePath));

    const status = spawnSync('svn', ['status', filePath], { encoding: 'utf8' });
    assert.match(status.stdout.trim(), /^D/);
  });

  it('deleteFolder removes a committed folder', () => {
    const dirPath = join(wcDir, 'mydir');
    mkdirSync(dirPath);
    writeFileSync(join(dirPath, 'f.txt'), 'x');
    spawnSync('svn', ['add', dirPath], { encoding: 'utf8' });
    spawnSync('svn', ['commit', '-m', 'add mydir', '--username', 'test', '--no-auth-cache'], { cwd: wcDir, encoding: 'utf8' });

    const result = deleteFolder(dirPath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(dirPath));
  });

  it('renameFile renames a committed file via svn move', () => {
    const oldPath = join(wcDir, 'svn-rename-src.txt');
    const newPath = join(wcDir, 'svn-rename-dst.txt');
    writeFileSync(oldPath, 'hello');
    spawnSync('svn', ['add', oldPath], { encoding: 'utf8' });
    spawnSync('svn', ['commit', '-m', 'add rename-src', '--username', 'test', '--no-auth-cache'], { cwd: wcDir, encoding: 'utf8' });

    const result = renameFile(oldPath, newPath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(oldPath));
    assert.isTrue(existsSync(newPath));

    const status = spawnSync('svn', ['status', newPath], { encoding: 'utf8' });
    assert.match(status.stdout.trim(), /^A/);
  });

  it('renameFolder renames a committed folder via svn move', () => {
    const oldPath = join(wcDir, 'svn-rename-dir');
    const newPath = join(wcDir, 'svn-rename-dir-dst');
    mkdirSync(oldPath);
    writeFileSync(join(oldPath, 'f.txt'), 'x');
    spawnSync('svn', ['add', oldPath], { encoding: 'utf8' });
    spawnSync('svn', ['commit', '-m', 'add rename-dir', '--username', 'test', '--no-auth-cache'], { cwd: wcDir, encoding: 'utf8' });

    const result = renameFolder(oldPath, newPath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(oldPath));
    assert.isTrue(existsSync(newPath));
  });

  it('auto-detects SVN from .svn directory', () => {
    clearProvider();  // Remove explicit provider — rely on auto-detection.
    const filePath = join(wcDir, 'detected.txt');
    writeFileSync(filePath, 'x');

    const result = finishedWrite(filePath);
    assert.isTrue(result.success, result.message);

    const status = spawnSync('svn', ['status', filePath], { encoding: 'utf8' });
    assert.match(status.stdout.trim(), /^A/);

    setProvider(new SvnProvider());  // Restore for remaining tests.
  });
});
