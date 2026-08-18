/**
 * `currentUser` / `currentUserAsync`: who the VCS thinks we are.
 *
 * The point of it is SEEDING an identity box the person can then change, so the failure that matters is
 * a confident wrong answer rather than a missing one. Every provider that cannot say returns undefined,
 * and these pin which ones those are.
 */

import { strict as assert } from 'assert';
import {
  currentUser, currentUserAsync,
  setProvider, clearProvider, setCommandRunner, clearCommandRunner,
  FilesystemProvider, GitProvider, PerforceProvider, SvnProvider, PlasticProvider,
} from '../src/index.js';

/** A runner that answers one command and refuses everything else, so a test cannot pass by accident. */
function canned(expect, output, exitCode = 0) {
  return (command, args) => {
    const a = args[0] === '-C' ? args.slice(2) : args; // git prefixes -C <cwd>
    if (command === expect.command && a.join(' ').startsWith(expect.args)) return { exitCode, output, error: '' };
    return { exitCode: 1, output: '', error: `unexpected: ${command} ${a.join(' ')}` };
  };
}

afterEach(() => { clearCommandRunner(); clearProvider(); });

describe('currentUser', () => {
  it('is undefined for the filesystem provider, which has no users', () => {
    setProvider(new FilesystemProvider());
    assert.equal(currentUser(), undefined);
  });

  it('git reports user.name', () => {
    setProvider(new GitProvider());
    setCommandRunner(canned({ command: 'git', args: 'config user.name' }, 'Ada Lovelace\n'));
    assert.equal(currentUser(), 'Ada Lovelace');
  });

  it('git with no configured name is undefined, not an empty string', () => {
    // A fresh install with no global config. An empty box is better than a user called "".
    setProvider(new GitProvider());
    setCommandRunner(canned({ command: 'git', args: 'config user.name' }, '\n', 1));
    assert.equal(currentUser(), undefined);
  });

  it('perforce reports the User name p4 info prints', () => {
    setProvider(new PerforceProvider());
    setCommandRunner(canned({ command: 'p4', args: 'info' },
      'User name: alovelace\nClient name: ada_ws\nServer address: perforce:1666\n'));
    assert.equal(currentUser(), 'alovelace');
  });

  it('perforce is undefined when p4 is unconfigured', () => {
    setProvider(new PerforceProvider());
    setCommandRunner(canned({ command: 'p4', args: 'info' }, '', 1));
    assert.equal(currentUser(), undefined);
  });

  it('plastic reports cm whoami', () => {
    setProvider(new PlasticProvider());
    setCommandRunner(canned({ command: 'cm', args: 'whoami' }, 'ada@studio\n'));
    assert.equal(currentUser(), 'ada@studio');
  });

  it('svn is undefined, because it never learns a name', () => {
    // It tells our lock from someone else's by comparing lock TOKENS, so there is no username in the
    // provider to expose. Guessing one would be worse than saying nothing.
    setProvider(new SvnProvider());
    assert.equal(currentUser(), undefined);
  });
});

describe('currentUserAsync', () => {
  it('gives the same answer as the sync call', async () => {
    setProvider(new GitProvider());
    setCommandRunner(canned({ command: 'git', args: 'config user.name' }, 'Ada Lovelace\n'));
    assert.equal(await currentUserAsync(), 'Ada Lovelace');
  });

  it('is undefined for a provider that cannot say', async () => {
    setProvider(new FilesystemProvider());
    assert.equal(await currentUserAsync(), undefined);
  });
});
