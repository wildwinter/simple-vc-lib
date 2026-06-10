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

  // -------------------------------------------------------------------------
  // Repeated artifact-generation runs without a submit in between. These walk
  // files through pending states (open for add/edit/delete) that the naive
  // p4 commands only warn about (exit 0), silently leaving stale changelist
  // entries that abort the eventual submit and leave it locked.

  it('deleteFile on a file open for add reverts the add', function () {
    const filePath = join(testDir, 'cycle-add-del.txt');
    writeFileSync(filePath, 'run1');
    assert.isTrue(finishedWrite(filePath).success);

    const result = deleteFile(filePath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(filePath));

    // Nothing may stay pending: a leftover open-for-add with no local file
    // aborts the next submit.
    const opened = p4(['opened', filePath]);
    assert.include(opened.stdout + opened.stderr, 'not opened', 'file should not remain opened after deleteFile');
  });

  it('deleteFile on a file open for edit cancels the edit and schedules the delete', function () {
    const filePath = join(testDir, 'cycle-edit-del.txt');
    submitFile(filePath, 'original');
    assert.isTrue(prepareToWrite(filePath).success);
    writeFileSync(filePath, 'rewritten');

    const result = deleteFile(filePath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(filePath));

    const fstat = p4(['fstat', '-Od', filePath]);
    assert.include(fstat.stdout, 'action delete', 'file should be open for delete, not edit');
  });

  it('deleteFile cleans up pending p4 state when the local file is already missing', function () {
    const filePath = join(testDir, 'cycle-phantom.txt');
    writeFileSync(filePath, 'run1');
    assert.isTrue(finishedWrite(filePath).success);
    // Simulate an earlier run (or the tool itself) removing the file directly.
    rmSync(filePath, { force: true });

    const result = deleteFile(filePath);
    assert.isTrue(result.success, result.message);

    const opened = p4(['opened', filePath]);
    assert.include(opened.stdout + opened.stderr, 'not opened', 'stale open-for-add entry should be reverted');
  });

  it('prepareToWrite cancels a pending delete on a re-created file', function () {
    const filePath = join(testDir, 'cycle-ptw-pending-del.txt');
    submitFile(filePath, 'original');
    p4(['delete', filePath]);
    writeFileSync(filePath, 'regenerated');

    // p4 edit on a pending-delete file only warns and leaves the delete
    // pending — the regenerated file would be deleted at next submit.
    const result = prepareToWrite(filePath);
    assert.isTrue(result.success, result.message);

    const fstat = p4(['fstat', filePath]);
    assert.include(fstat.stdout, 'action edit', 'fstat should report open for edit, not delete');
    assert.notInclude(fstat.stdout, 'action delete');
  });

  it('repeated add/delete/re-create cycles leave a submittable changelist', function () {
    const filePath = join(testDir, 'cycle-repeat.txt');
    for (let run = 1; run <= 3; run++) {
      writeFileSync(filePath, `run ${run}`);
      assert.isTrue(finishedWrite(filePath).success, `finishedWrite failed on run ${run}`);
      assert.isTrue(deleteFile(filePath).success, `deleteFile failed on run ${run}`);
    }
    writeFileSync(filePath, 'final');
    assert.isTrue(finishedWrite(filePath).success);

    const submit = p4(['submit', '-d', 'repeated cycles (simple-vc-lib test)', filePath]);
    assert.equal(submit.status, 0, `submit after repeated cycles failed:\n${submit.stdout}\n${submit.stderr}`);
  });

  it('deleteFile on the target of a pending rename schedules the source for delete', function () {
    const oldPath = join(testDir, 'cycle-move-src.txt');
    const newPath = join(testDir, 'cycle-move-dst.txt');
    submitFile(oldPath, 'content');
    assert.isTrue(renameFile(oldPath, newPath).success);

    const result = deleteFile(newPath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(newPath));

    // Net intent: source was renamed, rename target deleted — source must end
    // up scheduled for delete so the depot matches the disk.
    const srcStat = p4(['fstat', '-Od', oldPath]);
    assert.include(srcStat.stdout, 'action delete', 'rename source should be open for delete');
    const opened = p4(['opened', newPath]);
    assert.include(opened.stdout + opened.stderr, 'not opened', 'rename target should not remain opened');
  });

  it('renameFile of a re-created pending-delete file cancels the delete and moves', function () {
    const oldPath = join(testDir, 'cycle-ren-pending-src.txt');
    const newPath = join(testDir, 'cycle-ren-pending-dst.txt');
    submitFile(oldPath, 'original');
    p4(['delete', oldPath]);
    writeFileSync(oldPath, 'regenerated');

    const result = renameFile(oldPath, newPath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(oldPath));
    assert.isTrue(existsSync(newPath));
    assert.equal(readFileSync(newPath, 'utf8'), 'regenerated');

    const dstStat = p4(['fstat', newPath]);
    assert.include(dstStat.stdout, 'action', 'destination should be opened after rename');
  });

  it('renameFile onto a pending-delete destination replaces it', function () {
    const oldPath = join(testDir, 'cycle-ren-onto-src.txt');
    const newPath = join(testDir, 'cycle-ren-onto-dst.txt');
    // Seed both files first: submitFile submits the whole default changelist,
    // so the pending delete must be staged after the last submit.
    submitFile(newPath, 'old destination');
    submitFile(oldPath, 'source content');
    assert.isTrue(deleteFile(newPath).success);

    // p4 move refuses a pending-delete target ('can't move to an existing
    // file', exit 0) — the provider must emulate the rename instead of
    // silently doing nothing.
    const result = renameFile(oldPath, newPath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(oldPath));
    assert.isTrue(existsSync(newPath));
    assert.equal(readFileSync(newPath, 'utf8'), 'source content');

    const dstStat = p4(['fstat', newPath]);
    assert.include(dstStat.stdout, 'action edit', 'destination should be reopened for edit with the new content');
    const srcStat = p4(['fstat', '-Od', oldPath]);
    assert.include(srcStat.stdout, 'action delete', 'source should be scheduled for delete');
  });

  it('renameFile onto a destination open for add unwinds the stale add', function () {
    const oldPath = join(testDir, 'cycle-ren-add-src.txt');
    const newPath = join(testDir, 'cycle-ren-add-dst.txt');
    // Seed the source first: submitFile submits the whole default changelist,
    // so the destination's pending add must be staged after the last submit.
    submitFile(oldPath, 'source content');
    writeFileSync(newPath, 'stale artifact');
    assert.isTrue(finishedWrite(newPath).success);

    const result = renameFile(oldPath, newPath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(oldPath));
    assert.equal(readFileSync(newPath, 'utf8'), 'source content');

    const dstStat = p4(['fstat', newPath]);
    assert.include(dstStat.stdout, 'action', 'destination should be opened after rename');
  });

  it('renameFile of a file open for add moves the pending add', function () {
    const oldPath = join(testDir, 'cycle-ren-openadd-src.txt');
    const newPath = join(testDir, 'cycle-ren-openadd-dst.txt');
    writeFileSync(oldPath, 'fresh artifact');
    assert.isTrue(finishedWrite(oldPath).success);

    const result = renameFile(oldPath, newPath);
    assert.isTrue(result.success, result.message);
    assert.isFalse(existsSync(oldPath));
    assert.isTrue(existsSync(newPath));

    const opened = p4(['opened', oldPath]);
    assert.include(opened.stdout + opened.stderr, 'not opened', 'old path should not remain opened');
    const dstStat = p4(['fstat', newPath]);
    assert.include(dstStat.stdout, 'action add', 'new path should be open for add');
  });

  it('renameFile onto an existing tracked file reports an error, not silent success', function () {
    const oldPath = join(testDir, 'cycle-ren-clobber-src.txt');
    const newPath = join(testDir, 'cycle-ren-clobber-dst.txt');
    submitFile(oldPath, 'source');
    submitFile(newPath, 'destination');

    // p4 move onto an existing depot file fails with exit 0; the provider must
    // surface that as an error instead of reporting a rename that never happened.
    const result = renameFile(oldPath, newPath);
    assert.isFalse(result.success, 'rename onto an existing tracked file should fail');
    assert.isTrue(existsSync(oldPath), 'source must be untouched after the failed rename');
    assert.equal(readFileSync(newPath, 'utf8'), 'destination');
  });

  it('finishedWrite on a re-created rename source keeps the rename target', function () {
    const oldPath = join(testDir, 'cycle-resrc-src.txt');
    const newPath = join(testDir, 'cycle-resrc-dst.txt');
    submitFile(oldPath, 'content');
    assert.isTrue(renameFile(oldPath, newPath).success);

    // Tool regenerates an artifact at the old name in a later run.
    writeFileSync(oldPath, 'regenerated');
    const result = finishedWrite(oldPath);
    assert.isTrue(result.success, result.message);

    const srcStat = p4(['fstat', oldPath]);
    assert.include(srcStat.stdout, 'action edit', 'recreated source should be open for edit');
    const dstStat = p4(['fstat', newPath]);
    assert.include(dstStat.stdout, 'action add', 'rename target should be reopened for add');
    assert.isTrue(existsSync(newPath));
  });
});
