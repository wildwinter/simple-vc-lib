/**
 * `ignored` on file status, Perforce exclusive-open refusals, and the clean-up of a
 * file a failed write created. Git runs against a real repository; Perforce, SVN and
 * Plastic use canned transcripts taken from real p4 2026.1 and svn output (Plastic
 * from the docs only).
 */

import { assert } from 'chai';
import { mkdtempSync, mkdirSync, writeFileSync, existsSync, readFileSync } from 'fs';
import { tmpdir } from 'os';
import { join } from 'path';
import { spawnSync } from 'child_process';

import {
  fileStatus, fileStatusAsync, writeTextFile, writeTextFileAsync, writeBinaryFile, writeTextFiles,
  prepareToWrite, prepareToWriteAsync, finishedWrite, deleteFile,
  setProvider, clearProvider, setCommandRunner, clearCommandRunner,
  GitProvider, PerforceProvider, SvnProvider, PlasticProvider, FilesystemProvider,
} from '../src/index.js';

afterEach(() => {
  clearProvider();
  clearCommandRunner();
});

const makeTempDir = () => mkdtempSync(join(tmpdir(), 'simple-vc-lib-ignored-'));

// ---------------------------------------------------------------------------
// git (real repository)
// ---------------------------------------------------------------------------

describe('ignored (git, real repository)', () => {
  let dir;
  beforeEach(() => {
    dir = makeTempDir();
    const run = (args) => spawnSync('git', args, { cwd: dir, encoding: 'utf8' });
    run(['init']);
    run(['config', 'user.email', 'test@example.com']);
    run(['config', 'user.name', 'Test User']);
    writeFileSync(join(dir, '.gitignore'), '*.key\nsecret/\n');
    writeFileSync(join(dir, 'tracked.txt'), 't');
    run(['add', '.gitignore', 'tracked.txt']);
    run(['commit', '-m', 'init']);
    mkdirSync(join(dir, 'secret', 'deeper'), { recursive: true });
    writeFileSync(join(dir, 'secret', 'deeper', 'service.json'), '{}');
    writeFileSync(join(dir, 'a.key'), 'k');
    writeFileSync(join(dir, 'plain.txt'), 'p');
    setProvider(new GitProvider());
  });

  it('reports ignored files (by pattern and by folder) as untracked and ignored', () => {
    const [byPattern, byFolder, untracked, tracked] = fileStatus([
      join(dir, 'a.key'), join(dir, 'secret', 'deeper', 'service.json'),
      join(dir, 'plain.txt'), join(dir, 'tracked.txt'),
    ]);
    assert.include(byPattern, { tracked: false, ignored: true, dirty: false });
    assert.include(byFolder, { tracked: false, ignored: true, dirty: false });
    assert.include(untracked, { tracked: false, ignored: false });
    assert.include(tracked, { tracked: true, ignored: false });
  });

  it('matches through the async path', async () => {
    const [byFolder, untracked] = await fileStatusAsync([
      join(dir, 'secret', 'deeper', 'service.json'), join(dir, 'plain.txt'),
    ]);
    assert.isTrue(byFolder.ignored);
    assert.isFalse(untracked.ignored);
  });

  it('leaves ignored undefined outside any repository', () => {
    const outside = join(makeTempDir(), 'x.key');
    writeFileSync(outside, 'x');
    const [st] = fileStatus([outside]);
    assert.isUndefined(st.ignored);
    assert.isUndefined(st.tracked);
  });
});

// ---------------------------------------------------------------------------
// Perforce (canned p4 2026.1 transcripts)
// ---------------------------------------------------------------------------

/**
 * A canned p4: `-ztag fstat` answers with `fstat` (stdout and stderr), `ignores` with
 * the listed ignored paths, anything else from `others` or as an unexpected command.
 */
function cannedP4({ fstatOut = '', fstatErr = '', ignored = [], others = {} } = {}, calls = []) {
  return (command, args) => {
    calls.push(args);
    if (args[0] === '-ztag' && args[1] === 'fstat') return { exitCode: 0, output: fstatOut, error: fstatErr };
    if (args[0] === 'ignores') {
      return { exitCode: 0, output: args.slice(2).filter((p) => ignored.includes(p)).map((p) => `${p} ignored`).join('\n'), error: '' };
    }
    const key = args.join(' ');
    if (others[key]) return others[key];
    return { exitCode: 1, output: '', error: `unexpected: ${command} ${key}` };
  };
}

describe('ignored and client view (perforce, canned)', () => {
  beforeEach(() => setProvider(new PerforceProvider()));

  it('reports paths outside the client view or root as ignored, without asking p4 ignores', () => {
    const calls = [];
    setCommandRunner(cannedP4({
      fstatErr: [
        '/ws/excl/e.txt - file(s) not in client view.',
        "Path '/home/me/key.json' is not under client's root '/ws'.",
      ].join('\n'),
    }, calls));
    const [excluded, outsideRoot] = fileStatus(['/ws/excl/e.txt', '/home/me/key.json']);
    assert.include(excluded, { tracked: false, ignored: true });
    assert.include(outsideRoot, { tracked: false, ignored: true });
    assert.isFalse(calls.some((a) => a[0] === 'ignores'));
  });

  it('checks in-view untracked files against P4IGNORE in one p4 ignores call', () => {
    const calls = [];
    setCommandRunner(cannedP4({
      fstatOut: ['... depotFile //depot/sanity.txt', '... clientFile /ws/sanity.txt', '... isMapped ',
        '... headAction add', '... headType text', '... headRev 1', '... haveRev 1'].join('\n'),
      fstatErr: ['/ws/a.key - no such file(s).', '/ws/plain.txt - no such file(s).'].join('\n'),
      ignored: ['/ws/a.key'],
    }, calls));
    const [key, plain, tracked] = fileStatus(['/ws/a.key', '/ws/plain.txt', '/ws/sanity.txt']);
    assert.include(key, { tracked: false, ignored: true });
    assert.include(plain, { tracked: false, ignored: false });
    assert.include(tracked, { tracked: true, ignored: false });
    const ignoresCalls = calls.filter((a) => a[0] === 'ignores');
    assert.lengthOf(ignoresCalls, 1);
    assert.deepEqual(ignoresCalls[0], ['ignores', '-i', '/ws/a.key', '/ws/plain.txt']);
  });

  it('runs no p4 ignores when every file is tracked', () => {
    const calls = [];
    setCommandRunner(cannedP4({
      fstatOut: ['... depotFile //depot/a.txt', '... clientFile /ws/a.txt', '... headRev 1', '... haveRev 1'].join('\n'),
    }, calls));
    fileStatus(['/ws/a.txt']);
    assert.deepEqual(calls.map((a) => a[0]), ['-ztag']);
  });

  it('leaves ignored undefined when p4 ignores fails', async () => {
    setCommandRunner((command, args) => args[0] === '-ztag'
      ? { exitCode: 1, output: '', error: '/ws/a.key - no such file(s).' }
      : { exitCode: 1, output: '', error: 'boom' });
    const [st] = await fileStatusAsync(['/ws/a.key']);
    assert.isFalse(st.tracked);
    assert.isUndefined(st.ignored);
  });

  it('reads the nested other-open fields real p4 prints (... ... otherOpen0)', () => {
    setCommandRunner(cannedP4({
      fstatOut: [
        '... depotFile //depot/excl.bin', '... clientFile /ws/excl.bin', '... isMapped ',
        '... headAction add', '... headType binary+l', '... headRev 1', '... haveRev 1',
        '... ... otherOpen0 bob@bob-ws', '... ... otherAction0 edit', '... ... otherChange0 default',
        '... ... otherOpen 1',
      ].join('\n'),
    }));
    const [st] = fileStatus(['/ws/excl.bin']);
    assert.deepEqual(st.lockedBy, ['bob@bob-ws']);
  });
});

describe('exclusive (+l) refusals (perforce, canned)', () => {
  let dir;
  beforeEach(() => {
    dir = makeTempDir();
    setProvider(new PerforceProvider());
  });

  const exclusiveFstat = (file) => [
    `... depotFile //depot/${file}`, `... clientFile ${file}`, '... isMapped ',
    '... headAction add', '... headType binary+l', '... headRev 1', '... haveRev 1',
    '... ... otherOpen0 bob@bob-ws', '... ... otherAction0 edit', '... ... otherOpen 1',
  ].join('\n');

  it('maps a refused edit (reported at exit 0) to locked, naming the holder', async () => {
    const file = join(dir, 'excl.bin');
    writeFileSync(file, 'x');
    const refusal = {
      exitCode: 0,
      output: `//depot/excl.bin - can't edit exclusive file already opened for edit\n... //depot/excl.bin - also opened by bob@bob-ws`,
      error: '',
    };
    setCommandRunner((command, args) => args[0] === 'fstat'
      ? { exitCode: 0, output: exclusiveFstat(file), error: '' }
      : args[0] === 'edit' ? refusal : { exitCode: 1, output: '', error: 'unexpected' });
    for (const result of [prepareToWrite(file), await prepareToWriteAsync(file)]) {
      assert.equal(result.status, 'locked');
      assert.include(result.message, 'bob@bob-ws');
    }
  });

  it('maps a refused add (reported at exit 0) to locked', () => {
    const file = join(dir, 'new.bin');
    writeFileSync(file, 'x');
    setCommandRunner((command, args) => {
      if (args[0] === 'fstat') return { exitCode: 0, output: '', error: `${file} - no such file(s).` }; // p4 exits 0 here
      if (args[0] === 'add') {
        return {
          exitCode: 0,
          output: `//depot/new.bin - can't add exclusive file already opened for add\n... //depot/new.bin - also opened by bob@bob-ws`,
          error: '',
        };
      }
      return { exitCode: 1, output: '', error: 'unexpected' };
    });
    const result = finishedWrite(file);
    assert.equal(result.status, 'locked');
    assert.include(result.message, 'bob@bob-ws');
  });

  it('refuses to delete an exclusive file opened elsewhere and leaves the local copy', () => {
    const file = join(dir, 'excl.bin');
    writeFileSync(file, 'x');
    const calls = [];
    setCommandRunner((command, args) => {
      calls.push(args[0]);
      return args[0] === 'fstat'
        ? { exitCode: 0, output: exclusiveFstat(file), error: '' }
        : { exitCode: 1, output: '', error: 'unexpected' };
    });
    const result = deleteFile(file);
    assert.equal(result.status, 'locked');
    assert.isTrue(existsSync(file));
    assert.deepEqual(calls, ['fstat']);
  });

  it('a refused add of a new file through writeTextFile removes the file it wrote', async () => {
    const refusingAdd = (file) => (command, args) => {
      if (args[0] === 'fstat') return { exitCode: 0, output: '', error: `${file} - no such file(s).` }; // p4 exits 0 here
      if (args[0] === 'add') return { exitCode: 0, output: "//depot/x - can't add exclusive file already opened for add\n... //depot/x - also opened by bob@bob-ws", error: '' };
      return { exitCode: 1, output: '', error: 'unexpected' };
    };
    const file = join(dir, 'take.wav');
    setCommandRunner(refusingAdd(file));
    const result = writeTextFile(file, 'audio');
    assert.equal(result.status, 'locked');
    assert.include(result.message, 'removed');
    assert.isFalse(existsSync(file));

    const fileAsync = join(dir, 'take2.wav');
    setCommandRunner(refusingAdd(fileAsync));
    const asyncResult = await writeTextFileAsync(fileAsync, 'audio');
    assert.equal(asyncResult.status, 'locked');
    assert.isFalse(existsSync(fileAsync));
  });
});

// ---------------------------------------------------------------------------
// SVN (canned svn 1.14 XML)
// ---------------------------------------------------------------------------

describe('ignored (svn, canned)', () => {
  beforeEach(() => setProvider(new SvnProvider()));

  const entry = (path, item) =>
    `<entry path="${path}"><wc-status props="none" item="${item}"></wc-status></entry>`;
  const xml = (...entries) => `<?xml version="1.0"?>\n<status>\n<target path="/ws/wc">\n${entries.join('\n')}\n</target>\n</status>`;

  it('reports ignored, unversioned and normal items', () => {
    setCommandRunner((command, args) => ({
      exitCode: 0,
      output: xml(entry('/ws/wc/a.key', 'ignored'), entry('/ws/wc/plain.txt', 'unversioned'), entry('/ws/wc/t.txt', 'normal')),
      error: '',
    }));
    const [key, plain, tracked] = fileStatus(['/ws/wc/a.key', '/ws/wc/plain.txt', '/ws/wc/t.txt']);
    assert.include(key, { tracked: false, ignored: true });
    assert.include(plain, { tracked: false, ignored: false });
    assert.include(tracked, { tracked: true, ignored: false });
  });

  /** A fake working copy on disk (a root with `.svn`), holding the given file. */
  const workingCopyWith = (...rel) => {
    const wc = makeTempDir();
    mkdirSync(join(wc, '.svn'));
    const file = join(wc, ...rel);
    mkdirSync(join(file, '..'), { recursive: true });
    writeFileSync(file, 'k');
    return { wc, file };
  };

  it('places a file inside an ignored folder by asking about its folders, up to the working-copy root', () => {
    const { wc, file } = workingCopyWith('sec', 'deep', 'k.json');
    const calls = [];
    setCommandRunner((command, args) => {
      calls.push(args);
      if (args.includes('--depth')) return { exitCode: 0, output: xml(entry(join(wc, 'sec'), 'ignored'), entry(wc, 'normal')), error: '' };
      return { exitCode: 1, output: xml(), error: 'svn: warning: W155010: The node was not found.' };
    });
    const [st] = fileStatus([file]);
    assert.include(st, { tracked: false, ignored: true });
    assert.lengthOf(calls, 2);
    assert.deepEqual(calls[1], ['status', '--xml', '-v', '--depth', 'empty', join(wc, 'sec', 'deep'), join(wc, 'sec'), wc]);
  });

  it('a file under an unversioned folder is untracked but not ignored', async () => {
    const { wc, file } = workingCopyWith('newdir', 'n.txt');
    setCommandRunner((command, args) => args.includes('--depth')
      ? { exitCode: 0, output: xml(entry(join(wc, 'newdir'), 'unversioned')), error: '' }
      : { exitCode: 1, output: xml(), error: 'W155010' });
    const [st] = await fileStatusAsync([file]);
    assert.include(st, { tracked: false, ignored: false });
  });

  it('makes no second read for a file not yet written, or outside any working copy', () => {
    const outside = join(makeTempDir(), 'x.txt');
    writeFileSync(outside, 'x');
    const calls = [];
    setCommandRunner((command, args) => { calls.push(args); return { exitCode: 1, output: xml(), error: 'W155010' }; });
    fileStatus(['/ws/wc/not-yet.txt', outside]);
    assert.lengthOf(calls, 1);
  });
});

// ---------------------------------------------------------------------------
// Plastic (canned, from the docs only)
// ---------------------------------------------------------------------------

describe('ignored (plastic, canned from the docs)', () => {
  it('reports IG as ignored and PR as not', () => {
    setProvider(new PlasticProvider());
    setCommandRunner(() => ({
      exitCode: 0,
      output: ['STATUS 23 /main rep:default@server', 'IG;/ws/wc/a.key;False;NO_MERGES', 'PR;/ws/wc/new.txt;False;NO_MERGES'].join('\n'),
      error: '',
    }));
    const [key, priv] = fileStatus(['/ws/wc/a.key', '/ws/wc/new.txt']);
    assert.include(key, { tracked: false, ignored: true });
    assert.include(priv, { tracked: false, ignored: false });
  });
});

describe('ignored (filesystem)', () => {
  it('is undefined', () => {
    setProvider(new FilesystemProvider());
    const [st] = fileStatus([join(makeTempDir(), 'x.key')]);
    assert.isUndefined(st.ignored);
  });
});

// ---------------------------------------------------------------------------
// A failed write removes a file it created, and only that
// ---------------------------------------------------------------------------

describe('failed write clean-up', () => {
  const finishing = (status) => ({
    name: 'git',
    prepareToWrite: () => ({ success: true, status: 'ok', message: '' }),
    finishedWrite: () => ({ success: false, status, message: 'add refused' }),
    prepareToWriteAsync: async () => ({ success: true, status: 'ok', message: '' }),
    finishedWriteAsync: async () => ({ success: false, status, message: 'add refused' }),
  });
  const refusingFinish = finishing('locked');

  it('keeps a new file when finishedWrite fails for any reason but locked', async () => {
    setProvider(finishing('error'));
    const dir = makeTempDir();
    assert.isFalse(writeTextFile(join(dir, 'a.txt'), 'x').success);
    assert.isFalse((await writeTextFileAsync(join(dir, 'b.txt'), 'y')).success);
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'x');
    assert.isTrue(existsSync(join(dir, 'b.txt')));
  });

  it('removes a file the write created when finishedWrite refuses it as locked', () => {
    setProvider(refusingFinish);
    const dir = makeTempDir();
    const text = writeTextFile(join(dir, 'a.txt'), 'x');
    const bin = writeBinaryFile(join(dir, 'b.bin'), Buffer.from([1, 2]));
    assert.isFalse(text.success);
    assert.isFalse(bin.success);
    assert.isFalse(existsSync(join(dir, 'a.txt')));
    assert.isFalse(existsSync(join(dir, 'b.bin')));
  });

  it('never removes a file that was there before the write', async () => {
    setProvider(refusingFinish);
    const dir = makeTempDir();
    const file = join(dir, 'existing.txt');
    writeFileSync(file, 'old');
    assert.isFalse(writeTextFile(file, 'new').success);
    assert.isTrue(existsSync(file));
    assert.isFalse((await writeTextFileAsync(file, 'newer')).success);
    assert.equal(readFileSync(file, 'utf8'), 'newer');
  });

  it('applies in an all-or-nothing batch too', () => {
    setProvider({
      ...refusingFinish,
      prepareToWriteFiles: undefined,
      status: (paths) => paths.map((p) => ({ filePath: p, system: 'git', writable: true, tracked: false })),
      undoPrepareToWrite: () => ({ success: true, status: 'ok', message: '' }),
    });
    const dir = makeTempDir();
    const batch = writeTextFiles([{ filePath: join(dir, 'n.txt'), content: 'x' }], 'utf8', { allOrNothing: true });
    assert.isFalse(batch.success);
    assert.isFalse(existsSync(join(dir, 'n.txt')));
  });
});
