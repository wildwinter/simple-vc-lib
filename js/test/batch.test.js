import { assert } from 'chai';
import { mkdtempSync, writeFileSync, readFileSync, existsSync, chmodSync, statSync } from 'fs';
import { tmpdir } from 'os';
import { join, basename, resolve } from 'path';
import { spawnSync } from 'child_process';

import {
  prepareToWriteFiles, prepareToWriteFilesAsync, writeTextFiles, writeTextFilesAsync,
  setProvider, clearProvider,
  setCommandRunner, clearCommandRunner,
  FilesystemProvider, GitProvider, PerforceProvider, SvnProvider, PlasticProvider,
} from '../src/index.js';

// ---------------------------------------------------------------------------
// All-or-nothing batch checkout: prepareToWriteFiles and
// writeTextFiles({ allOrNothing }). Mirrored by BatchCheckoutTests.cs.
// ---------------------------------------------------------------------------

function makeTempDir() {
  return mkdtempSync(join(tmpdir(), 'simple-vc-lib-batch-'));
}

/** Create each named file in a fresh temp dir and return their paths. */
function seed(names, content = 'old') {
  const dir = makeTempDir();
  return names.map((name) => {
    const filePath = join(dir, name);
    writeFileSync(filePath, content);
    return filePath;
  });
}

function isReadOnly(filePath) {
  return (statSync(filePath).mode & 0o200) === 0;
}

const ok = () => ({ success: true, status: 'ok', message: '' });

/**
 * A provider that records every call by file name. `statuses` adds fields to a
 * file's status, `failPrepare` / `failUndo` / `throwUndo` name the files whose
 * prepare or undo goes wrong, and `withUndo: false` leaves the undo methods off
 * altogether, like a third-party provider written before they existed.
 */
function fakeProvider({ statuses = {}, failPrepare = [], failUndo = [], throwUndo = [], withUndo = true } = {}) {
  const calls = [];
  const provider = {
    name: 'git',
    calls,
    status: (paths) => paths.map((p) => ({ filePath: resolve(p), system: 'git', writable: true, ...(statuses[basename(p)] ?? {}) })),
    prepareToWrite: (p) => {
      calls.push(`prepare ${basename(p)}`);
      return failPrepare.includes(basename(p))
        ? { success: false, status: 'error', message: `cannot check out ${basename(p)}` }
        : ok();
    },
    finishedWrite: (p) => { calls.push(`finished ${basename(p)}`); return ok(); },
    undoPrepareToWrite: (p) => {
      calls.push(`undo ${basename(p)}`);
      if (throwUndo.includes(basename(p))) throw new Error('undo blew up');
      return failUndo.includes(basename(p))
        ? { success: false, status: 'error', message: 'revert refused' }
        : ok();
    },
  };
  provider.statusAsync = async (paths) => provider.status(paths);
  provider.prepareToWriteAsync = async (p) => provider.prepareToWrite(p);
  provider.finishedWriteAsync = async (p) => provider.finishedWrite(p);
  provider.undoPrepareToWriteAsync = async (p, before) => provider.undoPrepareToWrite(p, before);
  if (!withUndo) {
    delete provider.undoPrepareToWrite;
    delete provider.undoPrepareToWriteAsync;
  }
  return provider;
}

/** Record every command, answering from `answer(command, args)` (default: exit 0, no output). */
function recordingRunner(answer = () => undefined) {
  const calls = [];
  setCommandRunner((command, args) => {
    calls.push(`${command} ${args.join(' ')}`);
    return answer(command, args) ?? { exitCode: 0, output: '', error: '' };
  });
  return calls;
}

afterEach(() => {
  clearProvider();
  clearCommandRunner();
});

// ---------------------------------------------------------------------------
// prepareToWriteFiles - preflight
// ---------------------------------------------------------------------------

describe('prepareToWriteFiles (preflight)', () => {
  it('refuses a file locked by another user and prepares nothing', () => {
    const [a, b, c] = seed(['a.txt', 'b.txt', 'c.txt']);
    const fake = fakeProvider({ statuses: { 'b.txt': { lockedBy: ['bob@bob-ws', 'eve@eve-ws'] } } });
    setProvider(fake);

    const batch = prepareToWriteFiles([a, b, c]);
    assert.isFalse(batch.success);
    assert.deepEqual(fake.calls, []);
    assert.deepEqual(batch.results.map((r) => r.filePath), [a, b, c]);
    assert.equal(batch.results[1].status, 'locked');
    assert.include(batch.results[1].message, 'is locked by bob@bob-ws, eve@eve-ws');
    for (const r of [batch.results[0], batch.results[2]]) {
      assert.isFalse(r.success);
      assert.equal(r.status, 'error');
      assert.include(r.message, 'another file in the batch was refused');
    }
  });

  it('refuses a file that is out of date', () => {
    const [a, b] = seed(['a.txt', 'b.txt']);
    const fake = fakeProvider({ statuses: { 'a.txt': { outOfDate: true } } });
    setProvider(fake);

    const batch = prepareToWriteFiles([a, b]);
    assert.isFalse(batch.success);
    assert.deepEqual(fake.calls, []);
    assert.equal(batch.results[0].status, 'outOfDate');
    assert.include(batch.results[0].message, 'out of date');
    assert.equal(batch.results[1].status, 'error');
  });

  it('does not refuse a file that is not on disk yet', () => {
    const [a] = seed(['a.txt']);
    const missing = join(makeTempDir(), 'new.txt');
    const fake = fakeProvider({ statuses: { 'new.txt': { lockedBy: ['bob@bob-ws'] } } });
    setProvider(fake);

    const batch = prepareToWriteFiles([a, missing]);
    assert.isTrue(batch.success);
    assert.deepEqual(fake.calls, ['prepare a.txt', 'prepare new.txt']);
  });

  it('async: refuses a locked file and prepares nothing', async () => {
    const [a, b] = seed(['a.txt', 'b.txt']);
    const fake = fakeProvider({ statuses: { 'b.txt': { lockedBy: ['bob@bob-ws'] } } });
    setProvider(fake);

    const batch = await prepareToWriteFilesAsync([a, b]);
    assert.isFalse(batch.success);
    assert.deepEqual(fake.calls, []);
    assert.equal(batch.results[1].status, 'locked');
    assert.include(batch.results[1].message, 'is locked by bob@bob-ws');
  });
});

// ---------------------------------------------------------------------------
// prepareToWriteFiles - checkout and undo
// ---------------------------------------------------------------------------

describe('prepareToWriteFiles (checkout and undo)', () => {
  it('prepares every file in order when nothing fails', () => {
    const files = seed(['a.txt', 'b.txt', 'c.txt']);
    const fake = fakeProvider();
    setProvider(fake);

    const batch = prepareToWriteFiles(files);
    assert.isTrue(batch.success);
    assert.deepEqual(fake.calls, ['prepare a.txt', 'prepare b.txt', 'prepare c.txt']);
    assert.isTrue(batch.results.every((r) => r.success && r.status === 'ok'));
  });

  it('undoes the earlier checkouts in reverse order when the 3rd of 4 fails', () => {
    const [a, b, c, d] = seed(['a.txt', 'b.txt', 'c.txt', 'd.txt']);
    const fake = fakeProvider({ failPrepare: ['c.txt'] });
    setProvider(fake);

    const batch = prepareToWriteFiles([a, b, c, d]);
    assert.isFalse(batch.success);
    assert.deepEqual(fake.calls, ['prepare a.txt', 'prepare b.txt', 'prepare c.txt', 'undo b.txt', 'undo a.txt']);
    const [ra, rb, rc, rd] = batch.results;
    for (const r of [ra, rb]) {
      assert.isFalse(r.success);
      assert.equal(r.status, 'error');
      assert.include(r.message, 'was undone because another file in the batch failed');
    }
    assert.equal(rc.message, 'cannot check out c.txt');
    assert.isFalse(rd.success);
    assert.include(rd.message, 'was not prepared because another file in the batch failed');
  });

  it('reports an undo that fails, without throwing', () => {
    const [a, b, c] = seed(['a.txt', 'b.txt', 'c.txt']);
    const fake = fakeProvider({ failPrepare: ['c.txt'], failUndo: ['b.txt'], throwUndo: ['a.txt'] });
    setProvider(fake);

    const batch = prepareToWriteFiles([a, b, c]);
    assert.isFalse(batch.success);
    assert.include(batch.results[1].message, 'could not be undone');
    assert.include(batch.results[1].message, 'revert refused');
    assert.include(batch.results[0].message, 'could not be undone');
    assert.include(batch.results[0].message, 'undo blew up');
  });

  it('works with a provider that has no undoPrepareToWrite', () => {
    const [a, b] = seed(['a.txt', 'b.txt']);
    const fake = fakeProvider({ failPrepare: ['b.txt'], withUndo: false });
    setProvider(fake);

    const batch = prepareToWriteFiles([a, b]);
    assert.isFalse(batch.success);
    assert.deepEqual(fake.calls, ['prepare a.txt', 'prepare b.txt']);
    assert.include(batch.results[0].message, 'was undone');
  });

  it('prepares a repeated path once and reports it for every input', () => {
    const [a, b] = seed(['a.txt', 'b.txt']);
    const fake = fakeProvider();
    setProvider(fake);

    const batch = prepareToWriteFiles([a, b, a]);
    assert.isTrue(batch.success);
    assert.deepEqual(fake.calls, ['prepare a.txt', 'prepare b.txt']);
    assert.deepEqual(batch.results.map((r) => r.filePath), [a, b, a]);
  });

  it('async: undoes the earlier checkouts in reverse order when the 3rd of 4 fails', async () => {
    const [a, b, c, d] = seed(['a.txt', 'b.txt', 'c.txt', 'd.txt']);
    const fake = fakeProvider({ failPrepare: ['c.txt'], failUndo: ['a.txt'] });
    setProvider(fake);

    const batch = await prepareToWriteFilesAsync([a, b, c, d]);
    assert.isFalse(batch.success);
    assert.deepEqual(fake.calls, ['prepare a.txt', 'prepare b.txt', 'prepare c.txt', 'undo b.txt', 'undo a.txt']);
    assert.include(batch.results[0].message, 'revert refused');
    assert.include(batch.results[1].message, 'was undone');
    assert.include(batch.results[3].message, 'was not prepared');
  });

  it('async: works with a provider that has no undoPrepareToWrite', async () => {
    const [a, b] = seed(['a.txt', 'b.txt']);
    const fake = fakeProvider({ failPrepare: ['b.txt'], withUndo: false });
    setProvider(fake);

    const batch = await prepareToWriteFilesAsync([a, b]);
    assert.isFalse(batch.success);
    assert.include(batch.results[0].message, 'was undone');
  });
});

// ---------------------------------------------------------------------------
// writeTextFiles({ allOrNothing })
// ---------------------------------------------------------------------------

describe('writeTextFiles (allOrNothing)', () => {
  it('writes every file and calls finishedWrite on each', () => {
    const [a] = seed(['a.txt']);
    const fresh = join(makeTempDir(), 'sub', 'fresh.txt');
    const fake = fakeProvider();
    setProvider(fake);

    const batch = writeTextFiles([
      { filePath: a, content: 'new A' },
      { filePath: fresh, content: 'fresh' },
    ], 'utf8', { allOrNothing: true });
    assert.isTrue(batch.success);
    assert.equal(readFileSync(a, 'utf8'), 'new A');
    assert.equal(readFileSync(fresh, 'utf8'), 'fresh');
    assert.deepEqual(fake.calls, ['prepare a.txt', 'prepare fresh.txt', 'finished a.txt', 'finished fresh.txt']);
  });

  it('skips files whose content is unchanged, without checking them out', () => {
    const [a, b] = seed(['a.txt', 'b.txt'], 'same');
    const fake = fakeProvider();
    setProvider(fake);

    const batch = writeTextFiles([
      { filePath: a, content: 'same' },
      { filePath: b, content: 'different' },
    ], 'utf8', { allOrNothing: true });
    assert.isTrue(batch.success);
    assert.deepEqual(fake.calls, ['prepare b.txt', 'finished b.txt']);
    assert.deepEqual(batch.results.map((r) => r.filePath), [a, b]);
  });

  it('writes nothing when one file cannot be checked out', () => {
    const [a, b] = seed(['a.txt', 'b.txt']);
    const fresh = join(makeTempDir(), 'fresh.txt');
    const fake = fakeProvider({ failPrepare: ['b.txt'] });
    setProvider(fake);

    const batch = writeTextFiles([
      { filePath: a, content: 'new A' },
      { filePath: b, content: 'new B' },
      { filePath: fresh, content: 'fresh' },
    ], 'utf8', { allOrNothing: true });
    assert.isFalse(batch.success);
    assert.equal(readFileSync(a, 'utf8'), 'old');
    assert.equal(readFileSync(b, 'utf8'), 'old');
    assert.isFalse(existsSync(fresh));
    assert.notInclude(fake.calls.join(' '), 'finished');
    assert.deepEqual(fake.calls, ['prepare a.txt', 'prepare b.txt', 'undo a.txt']);
    assert.equal(batch.results[1].message, 'cannot check out b.txt');
  });

  it('writes nothing when a file is locked by another user', () => {
    const [a, b] = seed(['a.txt', 'b.txt']);
    const fake = fakeProvider({ statuses: { 'b.txt': { lockedBy: ['bob@bob-ws'] } } });
    setProvider(fake);

    const batch = writeTextFiles([
      { filePath: a, content: 'new A' },
      { filePath: b, content: 'new B' },
    ], 'utf8', { allOrNothing: true });
    assert.isFalse(batch.success);
    assert.equal(batch.results[1].status, 'locked');
    assert.equal(readFileSync(a, 'utf8'), 'old');
    assert.deepEqual(fake.calls, []);
  });

  it('without the option, still carries on past a refusal (unchanged behaviour)', () => {
    const [a, b] = seed(['a.txt', 'b.txt']);
    setProvider(fakeProvider({ failPrepare: ['a.txt'] }));

    const batch = writeTextFiles([
      { filePath: a, content: 'new A' },
      { filePath: b, content: 'new B' },
    ]);
    assert.isFalse(batch.success);
    assert.equal(readFileSync(a, 'utf8'), 'old');
    assert.equal(readFileSync(b, 'utf8'), 'new B');
  });

  it('async: writes every file, or nothing', async () => {
    const [a, b] = seed(['a.txt', 'b.txt']);
    const good = fakeProvider();
    setProvider(good);
    const written = await writeTextFilesAsync([
      { filePath: a, content: 'new A' },
      { filePath: b, content: 'new B' },
    ], 'utf8', { allOrNothing: true });
    assert.isTrue(written.success);
    assert.deepEqual(good.calls, ['prepare a.txt', 'prepare b.txt', 'finished a.txt', 'finished b.txt']);

    const bad = fakeProvider({ failPrepare: ['b.txt'] });
    setProvider(bad);
    const refused = await writeTextFilesAsync([
      { filePath: a, content: 'newer A' },
      { filePath: b, content: 'newer B' },
    ], 'utf8', { allOrNothing: true });
    assert.isFalse(refused.success);
    assert.equal(readFileSync(a, 'utf8'), 'new A');
    assert.deepEqual(bad.calls, ['prepare a.txt', 'prepare b.txt', 'undo a.txt']);
  });
});

// ---------------------------------------------------------------------------
// Real providers: the read-only bit comes back
// ---------------------------------------------------------------------------

/** A real provider that refuses to prepare any file called b.txt. */
function refusingB(Base) {
  return class extends Base {
    prepareToWrite(p) {
      return basename(p) === 'b.txt' ? { success: false, status: 'error', message: 'no b' } : super.prepareToWrite(p);
    }
    prepareToWriteAsync(p) {
      return basename(p) === 'b.txt' ? Promise.resolve({ success: false, status: 'error', message: 'no b' }) : super.prepareToWriteAsync(p);
    }
  };
}

describe('allOrNothing with real providers', () => {
  it('filesystem: a read-only file made writable is read-only again after a failure elsewhere', () => {
    const [a, b] = seed(['a.txt', 'b.txt']);
    chmodSync(a, 0o444);
    setProvider(new (refusingB(FilesystemProvider))());

    const batch = writeTextFiles([
      { filePath: a, content: 'new A' },
      { filePath: b, content: 'new B' },
    ], 'utf8', { allOrNothing: true });
    assert.isFalse(batch.success);
    assert.isTrue(isReadOnly(a));
    assert.equal(readFileSync(a, 'utf8'), 'old');
    assert.include(batch.results[0].message, 'was undone');
  });

  it('filesystem: async twin restores the read-only bit too', async () => {
    const [a, b] = seed(['a.txt', 'b.txt']);
    chmodSync(a, 0o444);
    setProvider(new (refusingB(FilesystemProvider))());

    const batch = await prepareToWriteFilesAsync([a, b]);
    assert.isFalse(batch.success);
    assert.isTrue(isReadOnly(a));
  });

  it('filesystem: a file that was already writable is left writable', () => {
    const [a, b] = seed(['a.txt', 'b.txt']);
    setProvider(new (refusingB(FilesystemProvider))());

    prepareToWriteFiles([a, b]);
    assert.isFalse(isReadOnly(a));
  });

  it('filesystem: writes a read-only file when the whole batch can be prepared', () => {
    const [a, b] = seed(['a.txt', 'b.txt']);
    chmodSync(a, 0o444);
    setProvider(new FilesystemProvider());

    const batch = writeTextFiles([
      { filePath: a, content: 'new A' },
      { filePath: b, content: 'new B' },
    ], 'utf8', { allOrNothing: true });
    assert.isTrue(batch.success);
    assert.equal(readFileSync(a, 'utf8'), 'new A');
    assert.equal(readFileSync(b, 'utf8'), 'new B');
  });

  it('git: a read-only tracked file is read-only again after a failure elsewhere', function () {
    const check = spawnSync('git', ['--version'], { encoding: 'utf8' });
    if (check.error || check.status !== 0) this.skip();
    const dir = makeTempDir();
    const run = (args) => spawnSync('git', args, { cwd: dir, encoding: 'utf8' });
    run(['init']);
    run(['config', 'user.email', 'test@example.com']);
    run(['config', 'user.name', 'Test User']);
    const a = join(dir, 'a.txt');
    const b = join(dir, 'b.txt');
    writeFileSync(a, 'old');
    writeFileSync(b, 'old');
    run(['add', '.']);
    run(['commit', '-m', 'init']);
    chmodSync(a, 0o444);
    setProvider(new (refusingB(GitProvider))());

    const batch = writeTextFiles([
      { filePath: a, content: 'new A' },
      { filePath: b, content: 'new B' },
    ], 'utf8', { allOrNothing: true });
    assert.isFalse(batch.success);
    assert.isTrue(isReadOnly(a));
    assert.equal(readFileSync(a, 'utf8'), 'old');
  });
});

// ---------------------------------------------------------------------------
// Real providers against canned CLI output: the exact undo commands
// ---------------------------------------------------------------------------

describe('undoPrepareToWrite (canned CLI output)', () => {
  const depotFstat = (filePath) => `... depotFile //depot/${basename(filePath)}\n... clientFile ${filePath}\n... headRev 3\n... haveRev 3`;

  it('perforce: reverts with p4 revert -a a depot file this batch opened', () => {
    const [a] = seed(['a.txt']);
    const calls = recordingRunner((command, args) =>
      args[0] === 'fstat' ? { exitCode: 0, output: depotFstat(a), error: '' } : undefined);

    const result = new PerforceProvider().undoPrepareToWrite(a, { filePath: a, system: 'perforce', writable: false, tracked: true });
    assert.isTrue(result.success, result.message);
    assert.include(calls, `p4 revert -a ${a}`);
  });

  it('perforce: never reverts a file that was already opened by me', async () => {
    const [a] = seed(['a.txt']);
    const calls = recordingRunner();
    const before = { filePath: a, system: 'perforce', writable: true, tracked: true, openedByMe: true };

    assert.isTrue(new PerforceProvider().undoPrepareToWrite(a, before).success);
    assert.isTrue((await new PerforceProvider().undoPrepareToWriteAsync(a, before)).success);
    assert.deepEqual(calls, []);
  });

  it('perforce: reports a revert that fails', async () => {
    const [a] = seed(['a.txt']);
    recordingRunner((command, args) => {
      if (args[0] === 'fstat') return { exitCode: 0, output: depotFstat(a), error: '' };
      if (args[0] === 'revert') return { exitCode: 1, output: '', error: 'server unreachable' };
      return undefined;
    });

    const result = await new PerforceProvider().undoPrepareToWriteAsync(a, { filePath: a, system: 'perforce', writable: false });
    assert.isFalse(result.success);
    assert.include(result.message, 'server unreachable');
  });

  it('perforce: a failed batch reverts what it opened and leaves what I had open', () => {
    const [a, b, c] = seed(['a.txt', 'b.txt', 'c.txt']);
    const ztag = [
      `... depotFile //depot/a.txt\n... clientFile ${a}\n... headRev 1\n... haveRev 1\n... action edit`,
      `... depotFile //depot/b.txt\n... clientFile ${b}\n... headRev 1\n... haveRev 1`,
      `... depotFile //depot/c.txt\n... clientFile ${c}\n... headRev 1\n... haveRev 1`,
    ].join('\n\n');
    const calls = recordingRunner((command, args) => {
      if (args[0] === '-ztag') return { exitCode: 0, output: ztag, error: '' };
      if (args[0] === 'fstat') return { exitCode: 0, output: depotFstat(args[1]), error: '' };
      if (args[0] === 'edit' && args[1] === c) return { exitCode: 1, output: '', error: 'no permission' };
      return undefined;
    });
    setProvider(new PerforceProvider());

    const batch = prepareToWriteFiles([a, b, c]);
    assert.isFalse(batch.success);
    assert.deepEqual(calls.filter((x) => x.includes('revert')), [`p4 revert -a ${b}`]);
    assert.include(batch.results[2].message, 'no permission');
  });

  it('perforce: another user having the file open refuses the batch', () => {
    const [a, b] = seed(['a.txt', 'b.txt']);
    const ztag = [
      `... depotFile //depot/a.txt\n... clientFile ${a}\n... headRev 1\n... haveRev 1`,
      `... depotFile //depot/b.txt\n... clientFile ${b}\n... headRev 1\n... haveRev 1\n... otherOpen0 bob@bob-ws\n... otherOpen 1`,
    ].join('\n\n');
    const calls = recordingRunner((command, args) =>
      args[0] === '-ztag' ? { exitCode: 0, output: ztag, error: '' } : undefined);
    setProvider(new PerforceProvider());

    const batch = prepareToWriteFiles([a, b]);
    assert.isFalse(batch.success);
    assert.equal(batch.results[1].status, 'locked');
    assert.include(batch.results[1].message, 'bob@bob-ws');
    assert.deepEqual(calls.filter((x) => x.startsWith('p4 edit')), []);
  });

  it('svn: releases a needs-lock file with svn unlock and makes it read-only again', () => {
    const [a] = seed(['a.txt']);
    const calls = recordingRunner();

    const result = new SvnProvider().undoPrepareToWrite(a, { filePath: a, system: 'svn', writable: false, tracked: true });
    assert.isTrue(result.success, result.message);
    assert.include(calls, `svn unlock ${a}`);
    assert.isTrue(isReadOnly(a));
  });

  it('svn: a file that was only made writable (never locked) just gets the read-only bit back', async () => {
    const [a] = seed(['a.txt']);
    recordingRunner((command, args) =>
      args[0] === 'unlock' ? { exitCode: 1, output: '', error: "svn: E195013: 'a.txt' is not locked in this working copy" } : undefined);

    const result = await new SvnProvider().undoPrepareToWriteAsync(a, { filePath: a, system: 'svn', writable: false, tracked: true });
    assert.isTrue(result.success, result.message);
    assert.isTrue(isReadOnly(a));
  });

  it('svn: never unlocks a file that was already mine, or one that was writable', async () => {
    const [a] = seed(['a.txt']);
    const calls = recordingRunner();
    const svnProvider = new SvnProvider();

    assert.isTrue(svnProvider.undoPrepareToWrite(a, { filePath: a, system: 'svn', writable: false, openedByMe: true }).success);
    assert.isTrue((await svnProvider.undoPrepareToWriteAsync(a, { filePath: a, system: 'svn', writable: false, openedByMe: true })).success);
    assert.isTrue(svnProvider.undoPrepareToWrite(a, { filePath: a, system: 'svn', writable: true }).success);
    assert.deepEqual(calls, []);
    assert.isFalse(isReadOnly(a));
  });

  it('plastic: undoes a checkout this batch made with cm undocheckout', async () => {
    const [a] = seed(['a.txt']);
    const calls = recordingRunner((command, args) =>
      args[0] === 'status' ? { exitCode: 0, output: `CO ${a}`, error: '' } : undefined);
    const before = { filePath: a, system: 'plastic', writable: false, tracked: true, dirty: false };

    assert.isTrue(new PlasticProvider().undoPrepareToWrite(a, before).success);
    assert.isTrue((await new PlasticProvider().undoPrepareToWriteAsync(a, before)).success);
    assert.deepEqual(calls.filter((x) => x.startsWith('cm undocheckout')), [`cm undocheckout ${a}`, `cm undocheckout ${a}`]);
  });

  it('plastic: never undoes a checkout I already had, locked or merely checked out', () => {
    const [a] = seed(['a.txt']);
    const calls = recordingRunner((command, args) =>
      args[0] === 'status' ? { exitCode: 0, output: `CO ${a}`, error: '' } : undefined);
    const plastic = new PlasticProvider();

    assert.isTrue(plastic.undoPrepareToWrite(a, { filePath: a, system: 'plastic', writable: true, tracked: true, openedByMe: true }).success);
    assert.isTrue(plastic.undoPrepareToWrite(a, { filePath: a, system: 'plastic', writable: true, tracked: true, dirty: true }).success);
    assert.deepEqual(calls.filter((x) => x.startsWith('cm undocheckout')), []);
  });
});
