import { assert } from 'chai';
import { mkdtempSync, writeFileSync, mkdirSync, existsSync, chmodSync, statSync } from 'fs';
import { tmpdir } from 'os';
import { join } from 'path';
import { spawnSync } from 'child_process';

import {
  prepareToWrite, finishedWrite, deleteFile, deleteFolder,
  setProvider, clearProvider,
  GitProvider, FilesystemProvider,
} from '../src/index.js';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function makeTempDir() {
  return mkdtempSync(join(tmpdir(), 'simple-vc-lib-test-'));
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
