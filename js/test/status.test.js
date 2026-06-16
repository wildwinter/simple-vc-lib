import { assert } from 'chai';
import { mkdtempSync, writeFileSync, readFileSync, chmodSync, existsSync } from 'fs';
import { tmpdir } from 'os';
import { join } from 'path';
import { spawnSync } from 'child_process';

import {
  fileStatus, fileStatusAsync, writeTextFiles, writeTextFileAsync, writeTextFilesAsync,
  deleteFileAsync, renameFileAsync,
  setProvider, clearProvider,
  setCommandRunner, clearCommandRunner,
  FilesystemProvider, PerforceProvider, SvnProvider, PlasticProvider,
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

  it('flags a modified tracked file as dirty, clean and untracked as not', () => {
    const dir = makeTempDir();
    initGitRepo(dir);
    writeFileSync(join(dir, 'tracked.txt'), 'tracked + local edit');
    const [modified, untracked] = fileStatus([join(dir, 'tracked.txt'), join(dir, 'untracked.txt')]);
    assert.isTrue(modified.tracked);
    assert.isTrue(modified.dirty);
    assert.isFalse(untracked.dirty); // untracked is reported via tracked:false, not dirty
  });

  it('reports a committed, unmodified file as not dirty', () => {
    const dir = makeTempDir();
    initGitRepo(dir);
    const [st] = fileStatus([join(dir, 'tracked.txt')]);
    assert.isTrue(st.tracked);
    assert.isFalse(st.dirty);
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
    assert.isTrue(st.dirty); // opened in a pending changelist = pending local change
    assert.isUndefined(st.lockedBy);
  });

  it('a synced, unopened file is not dirty', () => {
    setCommandRunner(cannedP4(ztag([[
      '... depotFile //depot/proj/d.txt',
      '... clientFile /ws/proj/d.txt',
      '... headAction edit',
      '... headRev 2',
      '... haveRev 2',
    ]])));
    const [st] = fileStatus(['/ws/proj/d.txt']);
    assert.isTrue(st.tracked);
    assert.isFalse(st.dirty);
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
// fileStatus — SVN (canned `svn status --xml -v` transcripts; no svn needed)
// ---------------------------------------------------------------------------

describe('fileStatus (svn, canned status xml)', () => {
  beforeEach(() => setProvider(new SvnProvider()));

  /** A canned runner answering `svn status --xml -v` with the given XML. */
  const cannedSvn = (xml) => (command, args) =>
    command === 'svn' && args[0] === 'status' && args.includes('--xml')
      ? { exitCode: 0, output: xml, error: '' }
      : { exitCode: 1, output: '', error: `unexpected: ${command} ${args.join(' ')}` };

  const entry = (path, item, props = 'none') =>
    `<entry path="${path}"><wc-status props="${props}" item="${item}" revision="4">` +
    `<commit revision="4"><author>alice</author></commit></wc-status></entry>`;

  const statusXml = (...entries) =>
    `<?xml version="1.0"?>\n<status>\n<target path="/ws/wc">\n${entries.join('\n')}\n</target>\n</status>`;

  it('classifies normal / modified / unversioned into tracked + dirty', () => {
    setCommandRunner(cannedSvn(statusXml(
      entry('/ws/wc/clean.txt', 'normal'),
      entry('/ws/wc/edited.txt', 'modified'),
      entry('/ws/wc/new.txt', 'unversioned'),
    )));
    const [clean, edited, untracked] = fileStatus([
      '/ws/wc/clean.txt', '/ws/wc/edited.txt', '/ws/wc/new.txt',
    ]);
    assert.equal(clean.system, 'svn');
    assert.isTrue(clean.tracked);
    assert.isFalse(clean.dirty);
    assert.isTrue(edited.tracked);
    assert.isTrue(edited.dirty);
    assert.isFalse(untracked.tracked);
    assert.isFalse(untracked.dirty);
  });

  it('treats a property-only modification as dirty', () => {
    setCommandRunner(cannedSvn(statusXml(entry('/ws/wc/props.txt', 'normal', 'modified'))));
    const [st] = fileStatus(['/ws/wc/props.txt']);
    assert.isTrue(st.tracked);
    assert.isTrue(st.dirty);
  });

  it('reports writable-only for a path svn does not mention', () => {
    setCommandRunner(cannedSvn(statusXml(entry('/ws/wc/other.txt', 'normal'))));
    const [st] = fileStatus(['/ws/wc/missing-from-output.txt']);
    assert.equal(st.system, 'svn');
    assert.isUndefined(st.tracked);
    assert.isUndefined(st.dirty);
  });

  it('batches every path into ONE status call', () => {
    let calls = 0;
    setCommandRunner((command, args) => {
      calls++;
      assert.equal(command, 'svn');
      assert.deepEqual(args.slice(0, 3), ['status', '--xml', '-v']);
      return { exitCode: 0, output: '', error: '' };
    });
    fileStatus(['/ws/wc/a.txt', '/ws/wc/b.txt', '/ws/wc/c.txt']);
    assert.equal(calls, 1);
  });

  // --- remote (svn status -u): lockedBy / outOfDate. XML shapes mirror a real run. ---

  it('with { remote: true } reports lockedBy from a server lock held by someone else', () => {
    setCommandRunner(cannedSvn(statusXml(
      '<entry path="/ws/wc/locked.txt">' +
      '<wc-status item="normal" props="normal" revision="1"><commit revision="1"><author>ian</author></commit></wc-status>' +
      '<repos-status props="none" item="none"><lock><token>t</token><owner>bob</owner><comment>x</comment></lock></repos-status>' +
      '</entry>',
    )));
    const [st] = fileStatus(['/ws/wc/locked.txt'], { remote: true });
    assert.isTrue(st.tracked);
    assert.deepEqual(st.lockedBy, ['bob']);
    assert.isUndefined(st.openedByMe);
  });

  it('with { remote: true } flags out-of-date from repos-status item != none', () => {
    setCommandRunner(cannedSvn(statusXml(
      '<entry path="/ws/wc/stale.txt">' +
      '<wc-status item="normal" props="none" revision="1"><commit revision="1"><author>ian</author></commit></wc-status>' +
      '<repos-status props="none" item="modified"></repos-status>' +
      '</entry>',
    )));
    const [st] = fileStatus(['/ws/wc/stale.txt'], { remote: true });
    assert.isTrue(st.outOfDate);
    assert.isUndefined(st.lockedBy);
  });

  it('with { remote: true } a lock we hold reports openedByMe, not lockedBy', () => {
    setCommandRunner(cannedSvn(statusXml(
      '<entry path="/ws/wc/mine.txt">' +
      '<wc-status item="normal" props="none" revision="1"><commit revision="1"><author>ian</author></commit>' +
      '<lock><token>t</token><owner>ian</owner></lock></wc-status>' +
      '<repos-status props="none" item="none"><lock><token>t</token><owner>ian</owner></lock></repos-status>' +
      '</entry>',
    )));
    const [st] = fileStatus(['/ws/wc/mine.txt'], { remote: true });
    assert.isTrue(st.openedByMe);
    assert.isUndefined(st.lockedBy);
  });

  it('default (local) does not pass -u and leaves lockedBy/outOfDate undefined', () => {
    let sawU = false;
    setCommandRunner((command, args) => {
      if (args.includes('-u')) sawU = true;
      return { exitCode: 0, output: statusXml(entry('/ws/wc/clean.txt', 'normal')), error: '' };
    });
    const [st] = fileStatus(['/ws/wc/clean.txt']);
    assert.isFalse(sawU);
    assert.isUndefined(st.lockedBy);
    assert.isUndefined(st.outOfDate);
  });
});

// ---------------------------------------------------------------------------
// fileStatus — Plastic (canned `cm status --machinereadable` transcripts)
// ---------------------------------------------------------------------------

describe('fileStatus (plastic, canned machinereadable)', () => {
  beforeEach(() => setProvider(new PlasticProvider()));

  const cannedCm = (out) => (command, args) =>
    command === 'cm' && args[0] === 'status' && args.includes('--machinereadable')
      ? { exitCode: 0, output: out, error: '' }
      : { exitCode: 1, output: '', error: `unexpected: ${command} ${args.join(' ')}` };

  it('classifies changed / checked-out / private', () => {
    setCommandRunner(cannedCm([
      'STATUS 23 /main rep:default@server',
      'CH "/ws/wc/changed.txt"',
      'CO "/ws/wc/checkedout.txt"',
      'PR "/ws/wc/new.txt"',
    ].join('\n')));
    const [changed, checkedOut, priv] = fileStatus([
      '/ws/wc/changed.txt', '/ws/wc/checkedout.txt', '/ws/wc/new.txt',
    ]);
    assert.equal(changed.system, 'plastic');
    assert.isTrue(changed.tracked);
    assert.isTrue(changed.dirty);
    assert.isTrue(checkedOut.dirty); // checked out = opened = pending local change
    assert.isFalse(priv.tracked);
    assert.isFalse(priv.dirty);
  });

  it('treats a controlled file cm does not list as clean (tracked, not dirty)', () => {
    const dir = makeTempDir();
    const file = join(dir, 'clean.txt');
    writeFileSync(file, 'x');
    setCommandRunner(cannedCm('STATUS 1 /main rep:default@server'));
    const [st] = fileStatus([file]);
    assert.isTrue(st.tracked);
    assert.isFalse(st.dirty);
  });

  it('parses unquoted paths too', () => {
    setCommandRunner(cannedCm('CH /ws/wc/changed.txt'));
    const [st] = fileStatus(['/ws/wc/changed.txt']);
    assert.isTrue(st.dirty);
  });

  it('batches every path into ONE status call', () => {
    let calls = 0;
    setCommandRunner((command, args) => {
      calls++;
      assert.equal(command, 'cm');
      assert.deepEqual(args.slice(0, 4), ['status', '--machinereadable', '--all', '--ignored']);
      return { exitCode: 0, output: '', error: '' };
    });
    fileStatus(['/ws/wc/a.txt', '/ws/wc/b.txt']);
    assert.equal(calls, 1);
  });

  // --- remote (cm fileinfo + cm whoami): lockedBy / openedByMe / outOfDate ---

  // Dispatches the three cm subcommands the remote path issues.
  const cannedCmRemote = ({ status = '', fileinfo = '', whoami = '' }) => (command, args) => {
    if (command !== 'cm') return { exitCode: 1, output: '', error: 'not cm' };
    if (args[0] === 'status') return { exitCode: 0, output: status, error: '' };
    if (args[0] === 'fileinfo') return { exitCode: 0, output: fileinfo, error: '' };
    if (args[0] === 'whoami') return { exitCode: 0, output: whoami, error: '' };
    return { exitCode: 1, output: '', error: `unexpected: ${args.join(' ')}` };
  };

  it('with { remote: true } fills outOfDate + lockedBy (other) and openedByMe (self)', () => {
    setCommandRunner(cannedCmRemote({
      // Both checked out, so both are controlled and get a fileinfo line, in input order.
      status: 'CH "/ws/wc/a.txt"\nCO "/ws/wc/b.txt"',
      fileinfo: '3;7;rep@srv;bob;bob-ws;/a.txt\n7;7;rep@srv;ian;ian-ws;/b.txt',
      whoami: 'ian',
    }));
    const [a, b] = fileStatus(['/ws/wc/a.txt', '/ws/wc/b.txt'], { remote: true });
    assert.isTrue(a.tracked);
    assert.isTrue(a.outOfDate);                 // loaded cset 3 < head 7
    assert.deepEqual(a.lockedBy, ['bob@bob-ws']); // bob != me
    assert.isUndefined(a.openedByMe);
    assert.isTrue(b.openedByMe);                 // locked by ian == me
    assert.isUndefined(b.lockedBy);
    assert.isUndefined(b.outOfDate);            // cset 7 == head 7
  });

  it('default (local) does not call fileinfo', () => {
    let calledFileinfo = false;
    setCommandRunner((command, args) => {
      if (command === 'cm' && args[0] === 'fileinfo') calledFileinfo = true;
      if (command === 'cm' && args[0] === 'status') return { exitCode: 0, output: 'CH "/ws/wc/a.txt"', error: '' };
      return { exitCode: 0, output: '', error: '' };
    });
    const [st] = fileStatus(['/ws/wc/a.txt']);
    assert.isFalse(calledFileinfo);
    assert.isTrue(st.dirty);
    assert.isUndefined(st.outOfDate);
    assert.isUndefined(st.lockedBy);
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
// fileStatusAsync — async twin (same results, non-blocking, concurrent providers)
// ---------------------------------------------------------------------------

describe('fileStatusAsync', () => {
  it('matches fileStatus for a real git repo (tracked / dirty / untracked)', async () => {
    const dir = makeTempDir();
    initGitRepo(dir);
    writeFileSync(join(dir, 'tracked.txt'), 'tracked + edit');
    const paths = [join(dir, 'tracked.txt'), join(dir, 'untracked.txt')];
    const sync = fileStatus(paths);
    const async = await fileStatusAsync(paths);
    assert.deepEqual(async, sync);
    assert.isTrue(async[0].tracked);
    assert.isTrue(async[0].dirty);
    assert.isFalse(async[1].tracked);
  });

  it('drives the Perforce canned runner through the async path', async () => {
    setProvider(new PerforceProvider());
    setCommandRunner(cannedP4(ztag([[
      '... depotFile //depot/proj/b.txt',
      '... clientFile /ws/proj/b.txt',
      '... headRev 3',
      '... haveRev 3',
      '... action edit',
    ]])));
    const [st] = await fileStatusAsync(['/ws/proj/b.txt']);
    assert.isTrue(st.tracked);
    assert.isTrue(st.openedByMe);
    assert.isTrue(st.dirty);
  });

  it('supports { remote: true } for SVN (lockedBy) through the async path', async () => {
    setProvider(new SvnProvider());
    setCommandRunner((command, args) =>
      command === 'svn' && args.includes('--xml')
        ? {
          exitCode: 0,
          error: '',
          output: '<?xml version="1.0"?>\n<status>\n<target path="/ws/wc">\n' +
            '<entry path="/ws/wc/locked.txt">' +
            '<wc-status item="normal" props="normal" revision="1"><commit revision="1"><author>ian</author></commit></wc-status>' +
            '<repos-status props="none" item="none"><lock><token>t</token><owner>bob</owner></lock></repos-status>' +
            '</entry>\n</target>\n</status>',
        }
        : { exitCode: 1, output: '', error: 'no' });
    const [st] = await fileStatusAsync(['/ws/wc/locked.txt'], { remote: true });
    assert.deepEqual(st.lockedBy, ['bob']);
  });

  it('supports { remote: true } for Plastic (fileinfo) through the async path', async () => {
    setProvider(new PlasticProvider());
    setCommandRunner((command, args) => {
      if (command !== 'cm') return { exitCode: 1, output: '', error: 'no' };
      if (args[0] === 'status') return { exitCode: 0, output: 'CH "/ws/wc/a.txt"', error: '' };
      if (args[0] === 'fileinfo') return { exitCode: 0, output: '3;7;rep@srv;bob;bob-ws;/a.txt', error: '' };
      if (args[0] === 'whoami') return { exitCode: 0, output: 'ian', error: '' };
      return { exitCode: 1, output: '', error: 'unexpected' };
    });
    const [st] = await fileStatusAsync(['/ws/wc/a.txt'], { remote: true });
    assert.isTrue(st.outOfDate);
    assert.deepEqual(st.lockedBy, ['bob@bob-ws']);
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

// ---------------------------------------------------------------------------
// async write path — writeTextFileAsync / writeTextFilesAsync
// ---------------------------------------------------------------------------

describe('async write path', () => {
  it('writeTextFilesAsync writes a batch and reports per-file success', async () => {
    setProvider(new FilesystemProvider());
    const dir = makeTempDir();
    const batch = await writeTextFilesAsync([
      { filePath: join(dir, 'sub', 'a.txt'), content: 'A' },
      { filePath: join(dir, 'deeper', 'b.txt'), content: 'B' },
    ]);
    assert.isTrue(batch.success);
    assert.lengthOf(batch.results, 2);
    assert.equal(readFileSync(join(dir, 'sub', 'a.txt'), 'utf8'), 'A');
  });

  it('writeTextFilesAsync reports a refusal with its why and keeps going', async () => {
    const locked = {
      name: 'git',
      prepareToWriteAsync: async (p) => p.endsWith('locked.txt')
        ? { success: false, status: 'locked', message: "'locked.txt' is locked by bob@bob-ws" }
        : { success: true, status: 'ok', message: '' },
      finishedWriteAsync: async () => ({ success: true, status: 'ok', message: '' }),
    };
    setProvider(locked);
    const dir = makeTempDir();
    const batch = await writeTextFilesAsync([
      { filePath: join(dir, 'locked.txt'), content: 'x' },
      { filePath: join(dir, 'free.txt'), content: 'y' },
    ]);
    assert.isFalse(batch.success);
    const failure = batch.results.find((r) => !r.success);
    assert.equal(failure.status, 'locked');
    assert.include(failure.message, 'locked by bob@bob-ws');
    assert.isFalse(existsSync(join(dir, 'locked.txt')));
    assert.isTrue(existsSync(join(dir, 'free.txt')));
  });

  it('writeTextFileAsync registers a new file with real git (finishedWriteAsync)', async () => {
    const dir = makeTempDir();
    initGitRepo(dir);
    const file = join(dir, 'fresh.txt');
    const result = await writeTextFileAsync(file, 'hello async');
    assert.isTrue(result.success);
    assert.equal(readFileSync(file, 'utf8'), 'hello async');
    // finishedWriteAsync should have staged it: fileStatus now sees it tracked.
    clearProvider();
    const [st] = await fileStatusAsync([file]);
    assert.isTrue(st.tracked);
  });

  it('deleteFileAsync removes an untracked file via the filesystem provider', async () => {
    setProvider(new FilesystemProvider());
    const dir = makeTempDir();
    const file = join(dir, 'gone.txt');
    writeFileSync(file, 'x');
    const result = await deleteFileAsync(file);
    assert.isTrue(result.success);
    assert.isFalse(existsSync(file));
  });

  it('renameFileAsync renames a tracked file via real git mv', async () => {
    const dir = makeTempDir();
    initGitRepo(dir);
    const result = await renameFileAsync(join(dir, 'tracked.txt'), join(dir, 'renamed.txt'));
    assert.isTrue(result.success);
    assert.isFalse(existsSync(join(dir, 'tracked.txt')));
    assert.isTrue(existsSync(join(dir, 'renamed.txt')));
  });

  it('deleteFileAsync removes and unstages a tracked file via real git', async () => {
    const dir = makeTempDir();
    initGitRepo(dir);
    const result = await deleteFileAsync(join(dir, 'tracked.txt'));
    assert.isTrue(result.success);
    assert.isFalse(existsSync(join(dir, 'tracked.txt')));
  });
});
