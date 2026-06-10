import { assert } from 'chai';
import { mkdtempSync, writeFileSync, readFileSync, chmodSync, existsSync } from 'fs';
import { tmpdir } from 'os';
import { join } from 'path';
import { spawnSync } from 'child_process';

import {
  fileStatus, writeTextFiles,
  setProvider, clearProvider,
  setCommandRunner, clearCommandRunner,
  FilesystemProvider, PerforceProvider,
} from '../src/index.js';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function makeTempDir() {
  // Deliberately NOT realpath'd: macOS tmpdir sits behind a symlink
  // (/var -> /private/var), and status matching must survive that
  // (repo-relative keys, not absolute-path joins).
  return mkdtempSync(join(tmpdir(), 'simple-vc-lib-status-'));
}

function initGitRepo(dir) {
  const run = (args) => spawnSync('git', args, { cwd: dir, encoding: 'utf8' });
  run(['init']);
  run(['config', 'user.email', 'test@example.com']);
  run(['config', 'user.name', 'Test User']);
  writeFileSync(join(dir, 'tracked.txt'), 'tracked');
  run(['add', 'tracked.txt']);
  run(['commit', '-m', 'init']);
  writeFileSync(join(dir, 'untracked.txt'), 'untracked');
}

const ztag = (records) => records.map((r) => r.join('\n')).join('\n\n');

/** A canned runner that answers `p4 -ztag fstat` with the given output. */
function cannedP4(fstatOutput) {
  return (command, args) => {
    if (command === 'p4' && args[0] === '-ztag' && args[1] === 'fstat') {
      return { exitCode: 0, output: fstatOutput, error: '' };
    }
    return { exitCode: 1, output: '', error: `unexpected command: ${command} ${args.join(' ')}` };
  };
}

afterEach(() => {
  clearProvider();
  clearCommandRunner();
});

// ---------------------------------------------------------------------------
// fileStatus — git (real repository)
// ---------------------------------------------------------------------------

describe('fileStatus (git, real repository)', () => {
  it('distinguishes tracked from untracked, both writable', () => {
    const dir = makeTempDir();
    initGitRepo(dir);
    const [tracked, untracked] = fileStatus([join(dir, 'tracked.txt'), join(dir, 'untracked.txt')]);
    assert.equal(tracked.system, 'git');
    assert.isTrue(tracked.tracked);
    assert.isTrue(tracked.writable);
    assert.isFalse(untracked.tracked);
  });

  it('reports a read-only file as not writable', () => {
    const dir = makeTempDir();
    initGitRepo(dir);
    chmodSync(join(dir, 'tracked.txt'), 0o444);
    const [st] = fileStatus([join(dir, 'tracked.txt')]);
    assert.isFalse(st.writable);
  });
});

// ---------------------------------------------------------------------------
// fileStatus — Perforce (canned -ztag fstat transcripts; no p4 needed)
// ---------------------------------------------------------------------------

describe('fileStatus (perforce, canned ztag)', () => {
  beforeEach(() => setProvider(new PerforceProvider()));

  it('reports a file exclusively locked by another user', () => {
    setCommandRunner(cannedP4(ztag([[
      '... depotFile //depot/proj/a.txt',
      '... clientFile /ws/proj/a.txt',
      '... isMapped ',
      '... headAction edit',
      '... headRev 7',
      '... haveRev 7',
      '... otherOpen0 bob@bob-ws',
      '... otherOpen 1',
      '... otherLock ',
      '... otherLock0 bob@bob-ws',
    ]])));
    const [st] = fileStatus(['/ws/proj/a.txt']);
    assert.equal(st.system, 'perforce');
    assert.isTrue(st.tracked);
    assert.deepEqual(st.lockedBy, ['bob@bob-ws']);
    assert.isUndefined(st.openedByMe);
  });

  it('reports my own checkout as openedByMe, not lockedBy', () => {
    setCommandRunner(cannedP4(ztag([[
      '... depotFile //depot/proj/b.txt',
      '... clientFile /ws/proj/b.txt',
      '... headRev 3',
      '... haveRev 3',
      '... action edit',
      '... change default',
    ]])));
    const [st] = fileStatus(['/ws/proj/b.txt']);
    assert.isTrue(st.tracked);
    assert.isTrue(st.openedByMe);
    assert.isUndefined(st.lockedBy);
  });

  it('treats deleted-at-head as untracked (unless reopened)', () => {
    setCommandRunner(cannedP4(ztag([[
      '... depotFile //depot/proj/gone.txt',
      '... clientFile /ws/proj/gone.txt',
      '... headAction delete',
      '... headRev 9',
    ]])));
    const [st] = fileStatus(['/ws/proj/gone.txt']);
    assert.isFalse(st.tracked);
  });

  it('flags out-of-date when haveRev lags headRev', () => {
    setCommandRunner(cannedP4(ztag([[
      '... depotFile //depot/proj/c.txt',
      '... clientFile /ws/proj/c.txt',
      '... headAction edit',
      '... headRev 5',
      '... haveRev 4',
    ]])));
    const [st] = fileStatus(['/ws/proj/c.txt']);
    assert.isTrue(st.tracked);
    assert.isTrue(st.outOfDate);
  });

  it('workspace-mapped but never submitted = untracked', () => {
    setCommandRunner(cannedP4(ztag([[
      '... clientFile /ws/proj/new.txt',
      '... isMapped ',
    ]])));
    const [st] = fileStatus(['/ws/proj/new.txt']);
    assert.isFalse(st.tracked);
  });

  it('a file fstat does not mention is outside the view: untracked', () => {
    setCommandRunner(cannedP4(''));
    const [st] = fileStatus(['/ws/elsewhere/x.txt']);
    assert.isFalse(st.tracked);
  });

  it('batches every path into ONE fstat call', () => {
    let fstatCalls = 0;
    setCommandRunner((command, args) => {
      fstatCalls++;
      assert.equal(args.length - 2, 3); // -ztag fstat + all three paths in one spawn
      return { exitCode: 0, output: '', error: '' };
    });
    fileStatus(['/ws/a.txt', '/ws/b.txt', '/ws/c.txt']);
    assert.equal(fstatCalls, 1);
  });
});

// ---------------------------------------------------------------------------
// fileStatus — filesystem fallback
// ---------------------------------------------------------------------------

describe('fileStatus (filesystem fallback)', () => {
  it('reports just the writable bit', () => {
    setProvider(new FilesystemProvider());
    const dir = makeTempDir();
    const file = join(dir, 'plain.txt');
    writeFileSync(file, 'x');
    const [st, missing] = fileStatus([file, join(dir, 'not-there.txt')]);
    assert.equal(st.system, 'filesystem');
    assert.isTrue(st.writable);
    assert.isUndefined(st.tracked);
    assert.isTrue(missing.writable); // not on disk yet - nothing forbids writing it
  });
});

// ---------------------------------------------------------------------------
// writeTextFiles — batch writes with per-file outcomes
// ---------------------------------------------------------------------------

describe('writeTextFiles', () => {
  it('writes a batch (creating parent directories) and reports per-file success', () => {
    setProvider(new FilesystemProvider());
    const dir = makeTempDir();
    const batch = writeTextFiles([
      { filePath: join(dir, 'sub', 'a.txt'), content: 'A' },
      { filePath: join(dir, 'deeper', 'still', 'b.txt'), content: 'B' },
    ]);
    assert.isTrue(batch.success);
    assert.lengthOf(batch.results, 2);
    assert.equal(readFileSync(join(dir, 'sub', 'a.txt'), 'utf8'), 'A');
  });

  it('reports a refusal with its why and keeps going', () => {
    const locked = {
      name: 'git',
      prepareToWrite: (p) => p.endsWith('locked.txt')
        ? { success: false, status: 'locked', message: "'locked.txt' is locked by bob@bob-ws" }
        : { success: true, status: 'ok', message: '' },
      finishedWrite: () => ({ success: true, status: 'ok', message: '' }),
    };
    setProvider(locked);
    const dir = makeTempDir();
    const batch = writeTextFiles([
      { filePath: join(dir, 'locked.txt'), content: 'x' },
      { filePath: join(dir, 'free.txt'), content: 'y' },
    ]);
    assert.isFalse(batch.success);
    const failure = batch.results.find((r) => !r.success);
    assert.equal(failure.status, 'locked');
    assert.include(failure.message, 'locked by bob@bob-ws');
    assert.isFalse(existsSync(join(dir, 'locked.txt'))); // refused write not forced
    assert.isTrue(existsSync(join(dir, 'free.txt')));    // the rest proceeded
  });
});
