import { assert } from 'chai';
import { mkdtempSync, writeFileSync, mkdirSync, chmodSync, statSync } from 'fs';
import { tmpdir } from 'os';
import { join, resolve } from 'path';

import {
  writeTextFile, writeTextFileAsync, writeTextFiles, prepareToWrite, deleteFile,
  setProvider, clearProvider, setCommandRunner, clearCommandRunner, PlasticProvider,
} from '../src/index.js';

// ---------------------------------------------------------------------------
// Plastic SCM writes against a simulated `cm`: a workspace with read-only files
// and a lock rule, so checking out a file that is already checked out fails as
// a real exclusive checkout does. Mirrored by PlasticProviderTests.cs.
// ---------------------------------------------------------------------------

function makeWorkspace() {
  return mkdtempSync(join(tmpdir(), 'simple-vc-lib-plastic-'));
}

function isReadOnly(filePath) {
  return (statSync(filePath).mode & 0o200) === 0;
}

/** A committed, unchanged, read-only file, as a workspace with "files read-only" holds it. */
function controlledFile(dir, name) {
  const filePath = join(dir, name);
  writeFileSync(filePath, 'old');
  chmodSync(filePath, 0o444);
  return filePath;
}

/**
 * A fake `cm` over one workspace. `states` maps absolute paths to a status code
 * ('CO', 'CH', 'PR', ...); a path with no entry is controlled and unchanged, so
 * `cm status` does not list it. Paths outside `root` are outside the workspace.
 */
function fakeCm(root, states = new Map()) {
  const calls = [];
  const inWorkspace = (p) => resolve(p).startsWith(resolve(root));
  setCommandRunner((command, args) => {
    calls.push(`${command} ${args[0]}`);
    const paths = args.slice(1).filter((a) => !a.startsWith('--'));
    if (paths.some((p) => !inWorkspace(p)))
      return { exitCode: 1, output: '', error: 'The path is not in a workspace.' };
    switch (args[0]) {
      case 'status': {
        // The real machine format (as the Unreal Plastic plugin reads it): code;path;merge flags.
        if (!args.includes('--fieldseparator=;')) return { exitCode: 1, output: '', error: 'expected --fieldseparator=;' };
        const lines = paths.flatMap((p) => states.has(resolve(p)) ? [`${states.get(resolve(p))};${resolve(p)};False;NO_MERGES`] : []);
        return { exitCode: 0, output: ['STATUS;7;game;local', ...lines].join('\n'), error: '' };
      }
      case 'co': {
        const p = resolve(paths[0]);
        if (states.get(p) === 'CO')
          return { exitCode: 1, output: '', error: `The item ${p} is exclusively checked out by you in this workspace.` };
        states.set(p, 'CO');
        chmodSync(p, 0o644);
        return { exitCode: 0, output: `Checking out ${p}`, error: '' };
      }
      case 'add':
        states.set(resolve(paths[0]), 'AD');
        return { exitCode: 0, output: '', error: '' };
      case 'remove':
        return { exitCode: 0, output: '', error: '' };
      default:
        return { exitCode: 1, output: '', error: `unexpected: cm ${args.join(' ')}` };
    }
  });
  return calls;
}

describe('Plastic SCM writes (simulated cm)', () => {
  beforeEach(() => setProvider(new PlasticProvider()));
  afterEach(() => {
    clearProvider();
    clearCommandRunner();
  });

  it('checks out an unchanged controlled file before the first write', () => {
    const root = makeWorkspace();
    const file = controlledFile(root, 'scene.patterx');
    const calls = fakeCm(root);

    const result = writeTextFile(file, 'new');
    assert.isTrue(result.success, result.message);
    assert.include(calls, 'cm co');
  });

  it('saves a file again after checking it out, without a second cm co (issue: "Save refused: ... is locked")', async () => {
    const root = makeWorkspace();
    const file = controlledFile(root, 'scene.patterx');
    const states = new Map();
    const calls = fakeCm(root, states);

    assert.isTrue(writeTextFile(file, 'first').success);
    assert.equal(states.get(resolve(file)), 'CO');

    const second = writeTextFile(file, 'second');
    assert.isTrue(second.success, second.message);
    const third = await writeTextFileAsync(file, 'third');
    assert.isTrue(third.success, third.message);
    assert.deepEqual(calls.filter((c) => c === 'cm co'), ['cm co']);
  });

  it('saves a batch of already checked-out files, all or nothing', async () => {
    const root = makeWorkspace();
    const a = controlledFile(root, 'a.patterx');
    const b = controlledFile(root, 'b.patterflow');
    fakeCm(root);

    assert.isTrue(writeTextFiles([{ filePath: a, content: '1' }, { filePath: b, content: '1' }], 'utf8', { allOrNothing: true }).success);
    const again = writeTextFiles([{ filePath: a, content: '2' }, { filePath: b, content: '2' }], 'utf8', { allOrNothing: true });
    assert.isTrue(again.success, JSON.stringify(again.results));
  });

  it('reads a combined CO+CH code as checked out', () => {
    const root = makeWorkspace();
    const file = controlledFile(root, 'scene.patterx');
    chmodSync(file, 0o644);
    const calls = fakeCm(root, new Map([[resolve(file), 'CO+CH']]));

    assert.isTrue(prepareToWrite(file).success);
    assert.notInclude(calls, 'cm co');
  });

  it('checks out a file changed without a checkout (CH), so it takes the lock', () => {
    const root = makeWorkspace();
    const file = controlledFile(root, 'scene.patterx');
    const calls = fakeCm(root, new Map([[resolve(file), 'CH']]));

    assert.isTrue(prepareToWrite(file).success);
    assert.include(calls, 'cm co');
  });

  it('still refuses a file someone else holds', () => {
    const root = makeWorkspace();
    const file = controlledFile(root, 'scene.patterx');
    setCommandRunner((command, args) => args[0] === 'co'
      ? { exitCode: 1, output: '', error: 'The item is exclusively checked out by sam@laptop.' }
      : { exitCode: 0, output: '', error: '' });

    const result = prepareToWrite(file);
    assert.isFalse(result.success);
    assert.equal(result.status, 'locked');
  });

  it('adds a new private file after writing it, and leaves ignored ones alone', () => {
    const root = makeWorkspace();
    const fresh = join(root, 'new.patterflow');
    const ignored = join(root, 'ignored.patterc');
    // cm lists a file it has never seen as private once it is on disk
    const states = new Map([[resolve(fresh), 'PR'], [resolve(ignored), 'IG']]);
    const calls = fakeCm(root, states);

    assert.isTrue(writeTextFile(fresh, 'x').success);
    assert.equal(states.get(resolve(fresh)), 'AD');
    assert.isTrue(writeTextFile(ignored, 'x').success);
    assert.deepEqual(calls.filter((c) => c === 'cm add'), ['cm add']);
  });

  it('only makes a file outside any workspace writable', () => {
    const root = makeWorkspace();
    const outside = controlledFile(makeWorkspace(), 'loose.txt');
    const calls = fakeCm(root);

    assert.isTrue(writeTextFile(outside, 'x').success);
    assert.isFalse(isReadOnly(outside));
    assert.notInclude(calls, 'cm co');
  });

  it('deletes an unchanged controlled file with cm remove', () => {
    const root = makeWorkspace();
    const file = controlledFile(root, 'scene.patterx');
    const calls = fakeCm(root);

    assert.isTrue(deleteFile(file).success);
    assert.include(calls, 'cm remove');
  });

  it('matches a folder by its own line, not the first line of its listing', () => {
    const root = makeWorkspace();
    const folder = join(root, 'scenes');
    mkdirSync(folder);
    const file = controlledFile(folder, 'a.patterflow');
    const calls = [];
    setCommandRunner((command, args) => {
      calls.push(`${command} ${args[0]}`);
      return args[0] === 'status'
        ? { exitCode: 0, output: `PR;${join(folder, 'scratch.txt')};False;NO_MERGES\nCO;${file};False;NO_MERGES`, error: '' }
        : { exitCode: 0, output: '', error: '' };
    });

    // the folder itself is unlisted, so controlled; its private child must not make it private
    assert.isTrue(new PlasticProvider().deleteFolder(folder).success);
    assert.include(calls, 'cm remove');
  });
});

// The lines a real cm prints, from the Unreal Plastic plugin's own examples. The first version of the
// provider assumed quoted paths, so on a real workspace no line ever matched its file: every file read as
// unchanged, and was checked out again on every save.
describe('Plastic SCM status lines (the real machine format)', () => {
  beforeEach(() => setProvider(new PlasticProvider()));
  afterEach(() => {
    clearProvider();
    clearCommandRunner();
  });

  const answering = (output) => {
    const calls = [];
    setCommandRunner((command, args) => {
      calls.push(`${command} ${args[0]}`);
      return args[0] === 'status' ? { exitCode: 0, output, error: '' } : { exitCode: 0, output: '', error: '' };
    });
    return calls;
  };

  it('reads a checked-out file, path with spaces and merge flags, as checked out', () => {
    const file = controlledFile(makeWorkspace(), 'my scene.patterx');
    const calls = answering(`STATUS;41;UEPlasticPluginDev;localhost:8087\nCO;${resolve(file)};False;NO_MERGES`);
    assert.isTrue(prepareToWrite(file).success);
    assert.notInclude(calls, 'cm co');
  });

  it('reads CO+CH and a changed (CH) file apart', () => {
    const file = controlledFile(makeWorkspace(), 'scene.patterx');
    let calls = answering(`CO+CH;${resolve(file)};False;NO_MERGES`);
    assert.isTrue(prepareToWrite(file).success);
    assert.notInclude(calls, 'cm co');
    calls = answering(`CH;${resolve(file)};False;NO_MERGES`);
    assert.isTrue(prepareToWrite(file).success);
    assert.include(calls, 'cm co');
  });

  it('reads a move by its destination, past the similarity, with or without the flags', () => {
    const dir = makeWorkspace();
    const moved = controlledFile(dir, 'moved.patterx');
    for (const tail of [';False;NO_MERGES', '']) {
      const calls = answering(`MV;100%;${join(dir, 'old.patterx')};${resolve(moved)}${tail}`);
      assert.isTrue(prepareToWrite(moved).success);
      assert.notInclude(calls, 'cm co');
    }
  });

  it('keeps a path that has the separator in it whole', () => {
    const file = controlledFile(makeWorkspace(), 'a;b.patterx');
    const calls = answering(`CO;${resolve(file)};False;NO_MERGES`);
    assert.isTrue(prepareToWrite(file).success);
    assert.notInclude(calls, 'cm co');
  });

  it('still reads a line with no separator as code and path', () => {
    const file = controlledFile(makeWorkspace(), 'scene.patterx');
    const calls = answering(`CO ${resolve(file)} False NO_MERGES`);
    assert.isTrue(prepareToWrite(file).success);
    assert.notInclude(calls, 'cm co');
  });
});
