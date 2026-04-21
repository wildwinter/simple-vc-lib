/**
 * Perforce integration tests.
 *
 * These tests require a configured Perforce workspace on the local machine.
 * They are NOT included in the default `npm test` run. Run manually with:
 *
 *   cd js
 *   P4_TEST_DIR=/path/to/your/p4/workspace npx mocha test/perforce.test.js
 *
 * On Windows:
 *   set P4_TEST_DIR=C:\path\to\your\p4\workspace && npx mocha test/perforce.test.js
 *
 * If P4_TEST_DIR is not set, the tests fall back to the workspace root reported
 * by `p4 info`. If that also fails, the suite is skipped.
 *
 * Requirements:
 *   - `p4` on PATH
 *   - P4PORT / P4USER / P4CLIENT configured (env vars, p4 config, or p4 tickets)
 *   - P4_TEST_DIR (or the auto-detected workspace root) must be a mapped depot path
 *
 * The tests create a temporary subdirectory inside that path, submit files to
 * the depot to simulate a real tracked state, exercise the API, then clean up
 * by reverting and deleting everything they submitted.
 */

import { assert } from 'chai';
import { writeFileSync, readFileSync, existsSync, mkdirSync, rmSync } from 'fs';
import { join } from 'path';
import { spawnSync } from 'child_process';

import {
  prepareToWrite, finishedWrite, deleteFile, deleteFolder,
  renameFile, renameFolder, writeTextFile,
  setProvider, clearProvider, PerforceProvider,
} from '../src/index.js';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function p4(args) {
  const result = spawnSync('p4', args, { encoding: 'utf8', windowsHide: true });
  return { status: result.status ?? -1, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
}

function p4Available() {
  return p4(['info']).status === 0;
}

function workspaceRoot() {
  const out = p4(['info']).stdout;
  // "Client root: C:\path\to\workspace" — trim and normalize.
  const match = out.match(/Client root:\s+(.+)/i);
  return match ? match[1].trim().replace(/\r/g, '') : null;
}

/**
 * Add a file to the default changelist and immediately submit it, so it
 * enters the depot and becomes read-only on disk (standard Perforce behaviour).
 */
function submitFile(filePath, content = 'test content') {
  writeFileSync(filePath, content);
  const addResult = p4(['add', filePath]);
  assert.equal(addResult.status, 0,
    `p4 add failed for '${filePath}' — is the path mapped in your workspace?\n` +
    `stdout: ${addResult.stdout}\nstderr: ${addResult.stderr}`);

  // Verify the file is actually open — p4 add exits 0 even when a .p4ignore rule
  // silently filters the file, leaving the default changelist empty.
  const openedResult = p4(['opened', filePath]);
  assert.equal(openedResult.status, 0,
    `p4 add returned exit 0 but '${filePath}' is not open in any changelist.\n` +
    `p4 add output: ${addResult.stdout || addResult.stderr}\n` +
    `Likely cause: a .p4ignore file in your workspace is filtering this path.\n` +
    `Fix: add an exception for the test directory in your .p4ignore, e.g.:\n` +
    `  !_simple_vc_lib_test/`);

  const submitResult = p4(['submit', '-d', `add ${filePath} (simple-vc-lib test)`]);
  assert.equal(submitResult.status, 0,
    `p4 submit failed for '${filePath}'\n${submitResult.stderr || submitResult.stdout}`);
  // After submit the file is read-only on disk — standard Perforce behaviour.
}

// ---------------------------------------------------------------------------
// PerforceProvider tests
// ---------------------------------------------------------------------------

describe('PerforceProvider', function () {
  this.timeout(30000);

  let testDir;

  before(function () {
    if (!p4Available()) {
      console.log('    Skipping: p4 not available or workspace not configured.');
      this.skip();
    }
    const root = process.env.P4_TEST_DIR ?? workspaceRoot();
    if (!root) {
      console.log('    Skipping: set P4_TEST_DIR to a mapped workspace path, or ensure p4 info reports a client root.');
      this.skip();
    }
    testDir = join(root, '_simple_vc_lib_test');
    mkdirSync(testDir, { recursive: true });
    setProvider(new PerforceProvider());
  });

  after(function () {
    clearProvider();
    if (!testDir) return;
    // Revert all open files in the test directory (ignore errors — may be nothing open).
    p4(['revert', join(testDir, '...')]);
    // Mark all submitted depot files under testDir for deletion.
    const del = p4(['delete', join(testDir, '...')]);
    // Submit the deletions only if p4 delete opened something.
    if (del.status === 0) {
      p4(['submit', '-d', 'cleanup: remove simple-vc-lib test dir']);
    }
    // Remove the local directory whether or not anything was in the depot.
    if (existsSync(testDir)) rmSync(testDir, { recursive: true, force: true });
  });

  // -------------------------------------------------------------------------

  it('prepareToWrite opens a submitted (read-only) file for edit', function () {
    const filePath = join(testDir, 'ptw-test.txt');
    submitFile(filePath);
    // After submit the file is read-only on disk.

    const result = prepareToWrite(filePath);
    assert.isTrue(result.success, result.message);

    // Verify the file is now open for edit in Perforce.
    const fstat = p4(['fstat', filePath]);
    assert.include(fstat.stdout + fstat.stderr, 'action', 'fstat should report an open action after prepareToWrite');
  });

  it('prepareToWrite on a new (untracked) file returns ok', function () {
    const result = prepareToWrite(join(testDir, 'nonexistent.txt'));
    assert.isTrue(result.success, result.message);
  });

  it('finishedWrite adds a new file to Perforce', function () {
    const filePath = join(testDir, 'fw-new.txt');
    writeFileSync(filePath, 'new content');

    const result = finishedWrite(filePath);
    assert.isTrue(result.success, result.message);

    // Verify the file is open for add in Perforce.
    const fstat = p4(['fstat', filePath]);
    assert.include(fstat.stdout + fstat.stderr, 'add', 'fstat should report open for add after finishedWrite');
  });

  it('writeTextFile checks out, writes, and leaves file open for edit', function () {
    const filePath = join(testDir, 'write-test.txt');
    submitFile(filePath, 'original');

    const result = writeTextFile(filePath, 'updated content');
    assert.isTrue(result.success, result.message);
    assert.equal(readFileSync(filePath, 'utf8'), 'updated content');
  });

  it('finishedWrite cancels a pending delete and reopens for edit', function () {
    const filePath = join(testDir, 'fw-pending-delete.txt');
    submitFile(filePath, 'original content');

    // Mark for delete — removes the local file and opens a pending delete.
    p4(['delete', filePath]);
    assert.isFalse(existsSync(filePath));

    // Re-create the file on disk (as writeBinaryFile with forceWrite would do).
    writeFileSync(filePath, 'regenerated content');

    // finishedWrite must cancel the pending delete and reopen for edit.
    const result = finishedWrite(filePath);
    assert.isTrue(result.success, result.message);

    // File must be present on disk with the new content.
    assert.isTrue(existsSync(filePath));
    assert.equal(readFileSync(filePath, 'utf8'), 'regenerated content');

    // P4 state must be "edit", not "delete".
    const fstat = p4(['fstat', filePath]);
    assert.include(fstat.stdout, 'action edit', 'fstat should report open for edit, not delete');
    assert.notInclude(fstat.stdout, 'action delete');
  });

  it('deleteFile removes a submitted file from depot', function () {
    const filePath = join(testDir, 'del-test.txt');
    submitFile(filePath);

    const result = deleteFile(filePath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(filePath));

    // Verify it is open for delete in Perforce (file is physically gone but still pending in the changelist).
    const fstat = p4(['fstat', '-Od', filePath]);
    assert.include(fstat.stdout + fstat.stderr, 'delete', 'fstat should report open for delete');
  });

  it('deleteFile removes an untracked file from disk only', function () {
    const filePath = join(testDir, 'untracked-del.txt');
    writeFileSync(filePath, 'local only');

    const result = deleteFile(filePath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(filePath));
  });

  it('renameFile moves a submitted file via p4 move', function () {
    const oldPath = join(testDir, 'ren-src.txt');
    const newPath = join(testDir, 'ren-dst.txt');
    submitFile(oldPath, 'content');

    const result = renameFile(oldPath, newPath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(oldPath));
    assert.isTrue(existsSync(newPath));

    // The destination should be open for add (as part of the p4 move operation).
    const fstat = p4(['fstat', newPath]);
    assert.include(fstat.stdout + fstat.stderr, 'add', 'destination should be open for add after p4 move');
  });

  it('renameFolder moves a submitted folder via p4 move', function () {
    const oldDir = join(testDir, 'ren-folder-src');
    const newDir = join(testDir, 'ren-folder-dst');
    mkdirSync(oldDir);
    submitFile(join(oldDir, 'a.txt'), 'a');
    submitFile(join(oldDir, 'b.txt'), 'b');

    const result = renameFolder(oldDir, newDir);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(oldDir));
    assert.isTrue(existsSync(join(newDir, 'a.txt')));
    assert.isTrue(existsSync(join(newDir, 'b.txt')));
  });

  it('deleteFolder removes a submitted folder from depot', function () {
    const dirPath = join(testDir, 'del-folder');
    mkdirSync(dirPath);
    submitFile(join(dirPath, 'x.txt'), 'x');
    submitFile(join(dirPath, 'y.txt'), 'y');

    const result = deleteFolder(dirPath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(dirPath));
  });
});
