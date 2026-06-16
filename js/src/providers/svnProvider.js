import { existsSync, unlinkSync, rmSync } from 'fs';
import { basename, dirname, resolve } from 'path';
import { runCommand, runCommandAsync } from '../commandRunner.js';
import { okResult, errorResult } from '../vcResult.js';
import { writableBit } from '../vcStatus.js';
import { FilesystemProvider } from './filesystemProvider.js';

const fs = new FilesystemProvider();

function svn(args) {
  return runCommand('svn', args);
}

function svnAsync(args) {
  return runCommandAsync('svn', args);
}

/**
 * Returns true if the file is tracked by SVN.
 */
function isTracked(filePath) {
  const result = svn(['info', filePath]);
  return result.exitCode === 0;
}

async function isTrackedAsync(filePath) {
  return (await svnAsync(['info', filePath])).exitCode === 0;
}

/**
 * Subversion (SVN) provider.
 *
 * SVN files are normally writable. The exception is files with the
 * svn:needs-lock property, which are read-only until locked.
 * prepareToWrite handles this by calling `svn lock`.
 */
export class SvnProvider {
  get name() { return 'svn'; }

  prepareToWrite(filePath) {
    if (!existsSync(filePath)) return okResult();

    const fsResult = fs.prepareToWrite(filePath);
    if (fsResult.success) return okResult();

    // File is read-only — only expected for files with svn:needs-lock set.
    if (!isTracked(filePath)) {
      return errorResult('error', `Cannot make '${filePath}' writable`);
    }

    const result = svn(['lock', filePath]);
    if (result.exitCode === 0) return okResult('File locked in SVN');

    const combined = (result.output + ' ' + result.error).toLowerCase();
    if (combined.includes('locked by') || combined.includes('steal lock')) {
      return errorResult('locked', `'${filePath}' is locked by another user`);
    }
    if (combined.includes('out of date')) {
      return errorResult('outOfDate', `'${filePath}' is out of date — update before locking`);
    }
    return errorResult('error', `Cannot lock '${filePath}' in SVN: ${result.error || result.output}`);
  }

  finishedWrite(filePath) {
    if (!existsSync(filePath))
      return errorResult('error', `'${filePath}' does not exist after write`);

    if (isTracked(filePath)) return okResult();

    // An unversioned file inside a working copy has a versioned parent directory.
    // If the parent is not tracked either, the file is outside the working copy entirely.
    if (!isTracked(dirname(filePath)))
      return fs.finishedWrite(filePath);

    const result = svn(['add', filePath]);
    if (result.exitCode === 0) return okResult('File added to SVN');
    // File is ignored — treat as outside the working copy.
    const combined = (result.output + ' ' + result.error).toLowerCase();
    if (combined.includes('ignored')) return fs.finishedWrite(filePath);
    return errorResult('error', `Cannot add '${filePath}' to SVN: ${result.error || result.output}`);
  }

  /** Async twin of {@link prepareToWrite}. */
  async prepareToWriteAsync(filePath) {
    if (!existsSync(filePath)) return okResult();

    const fsResult = await fs.prepareToWriteAsync(filePath);
    if (fsResult.success) return okResult();

    if (!(await isTrackedAsync(filePath))) {
      return errorResult('error', `Cannot make '${filePath}' writable`);
    }

    const result = await svnAsync(['lock', filePath]);
    if (result.exitCode === 0) return okResult('File locked in SVN');

    const combined = (result.output + ' ' + result.error).toLowerCase();
    if (combined.includes('locked by') || combined.includes('steal lock')) {
      return errorResult('locked', `'${filePath}' is locked by another user`);
    }
    if (combined.includes('out of date')) {
      return errorResult('outOfDate', `'${filePath}' is out of date — update before locking`);
    }
    return errorResult('error', `Cannot lock '${filePath}' in SVN: ${result.error || result.output}`);
  }

  /** Async twin of {@link finishedWrite}. */
  async finishedWriteAsync(filePath) {
    if (!existsSync(filePath))
      return errorResult('error', `'${filePath}' does not exist after write`);

    if (await isTrackedAsync(filePath)) return okResult();
    if (!(await isTrackedAsync(dirname(filePath))))
      return fs.finishedWriteAsync(filePath);

    const result = await svnAsync(['add', filePath]);
    if (result.exitCode === 0) return okResult('File added to SVN');
    const combined = (result.output + ' ' + result.error).toLowerCase();
    if (combined.includes('ignored')) return fs.finishedWriteAsync(filePath);
    return errorResult('error', `Cannot add '${filePath}' to SVN: ${result.error || result.output}`);
  }

  /** Async twin of {@link deleteFile}. */
  async deleteFileAsync(filePath) {
    if (!existsSync(filePath)) return okResult();
    if (await isTrackedAsync(filePath)) {
      const result = await svnAsync(['delete', '--force', filePath]);
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot delete '${filePath}' from SVN: ${result.error || result.output}`);
    }
    try {
      unlinkSync(filePath);
      return okResult();
    } catch (e) {
      return errorResult('error', `Failed to delete file: ${e.message}`);
    }
  }

  /** Async twin of {@link deleteFolder}. */
  async deleteFolderAsync(folderPath) {
    if (!existsSync(folderPath)) return okResult();
    if (await isTrackedAsync(folderPath)) {
      const result = await svnAsync(['delete', '--force', folderPath]);
      if (result.exitCode !== 0)
        return errorResult('error', `Cannot delete folder '${folderPath}' from SVN: ${result.error || result.output}`);
    } else {
      try {
        rmSync(folderPath, { recursive: true, force: true });
      } catch (e) {
        return errorResult('error', `Failed to delete folder: ${e.message}`);
      }
    }
    return okResult();
  }

  /** Async twin of {@link renameFile}. */
  async renameFileAsync(oldPath, newPath) {
    if (!existsSync(oldPath)) return okResult();
    if (await isTrackedAsync(oldPath)) {
      const result = await svnAsync(['move', '--force', oldPath, newPath]);
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot rename '${oldPath}' in SVN: ${result.error || result.output}`);
    }
    return fs.renameFileAsync(oldPath, newPath);
  }

  /** Async twin of {@link renameFolder}. */
  async renameFolderAsync(oldPath, newPath) {
    if (!existsSync(oldPath)) return okResult();
    if (await isTrackedAsync(oldPath)) {
      const result = await svnAsync(['move', '--force', oldPath, newPath]);
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot rename folder '${oldPath}' in SVN: ${result.error || result.output}`);
    }
    return fs.renameFolderAsync(oldPath, newPath);
  }

  deleteFile(filePath) {
    if (!existsSync(filePath)) return okResult();

    if (isTracked(filePath)) {
      const result = svn(['delete', '--force', filePath]);
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot delete '${filePath}' from SVN: ${result.error || result.output}`);
    }

    try {
      unlinkSync(filePath);
      return okResult();
    } catch (e) {
      return errorResult('error', `Failed to delete file: ${e.message}`);
    }
  }

  renameFile(oldPath, newPath) {
    if (!existsSync(oldPath)) return okResult();
    if (isTracked(oldPath)) {
      const result = svn(['move', '--force', oldPath, newPath]);
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot rename '${oldPath}' in SVN: ${result.error || result.output}`);
    }
    return fs.renameFile(oldPath, newPath);
  }

  renameFolder(oldPath, newPath) {
    if (!existsSync(oldPath)) return okResult();
    if (isTracked(oldPath)) {
      const result = svn(['move', '--force', oldPath, newPath]);
      if (result.exitCode === 0) return okResult();
      return errorResult('error', `Cannot rename folder '${oldPath}' in SVN: ${result.error || result.output}`);
    }
    return fs.renameFolder(oldPath, newPath);
  }

  deleteFolder(folderPath) {
    if (!existsSync(folderPath)) return okResult();

    if (isTracked(folderPath)) {
      const result = svn(['delete', '--force', folderPath]);
      if (result.exitCode !== 0) {
        return errorResult('error', `Cannot delete folder '${folderPath}' from SVN: ${result.error || result.output}`);
      }
    } else {
      try {
        rmSync(folderPath, { recursive: true, force: true });
      } catch (e) {
        return errorResult('error', `Failed to delete folder: ${e.message}`);
      }
    }

    return okResult();
  }

  /**
   * Status for a batch of files in ONE `svn status --xml -v` spawn. `-v` lists
   * clean versioned files too (item="normal"), so tracked-clean is distinguished
   * from untracked; `--xml` is the stable machine format.
   *
   * With `{ remote: true }` it adds `-u`, a server round-trip that makes svn emit a
   * `<repos-status>` per entry: `item != "none"` means a newer revision exists
   * (`outOfDate`), and a `<lock>` there names the current holder. A `<lock>` inside
   * `<wc-status>` is our own lock token (`openedByMe`); a server lock without it is
   * someone else's (`lockedBy`).
   *
   * @param {string[]} filePaths
   * @param {import('../vcStatus.js').VCStatusOptions} [options]
   * @returns {import('../vcStatus.js').VCFileStatus[]}
   */
  status(filePaths, options = {}) {
    const result = svn(svnStatusArgs(filePaths, options));
    return buildSvnStatuses(result.output, filePaths);
  }

  /** Async twin of {@link status}: one `svn status --xml`, spawned without blocking. */
  async statusAsync(filePaths, options = {}) {
    const result = await svnAsync(svnStatusArgs(filePaths, options));
    return buildSvnStatuses(result.output, filePaths);
  }
}

/** Build the `svn status` argument list (adds `-u` for a remote read). */
function svnStatusArgs(filePaths, options) {
  const targets = filePaths.map((p) => resolve(p));
  return ['status', '--xml', '-v', ...(options.remote ? ['-u'] : []), ...targets];
}

/**
 * Assemble per-file statuses from `svn status --xml` output. Pure - shared by the
 * sync and async paths.
 *
 * @param {string} output
 * @param {string[]} filePaths
 * @returns {import('../vcStatus.js').VCFileStatus[]}
 */
function buildSvnStatuses(output, filePaths) {
  const byPath = new Map();
  const byBase = new Map();
  // A bad (non-working-copy) target makes svn exit non-zero, but it still emits
  // XML for the rest - parse whatever we get and fall back per file.
  if (output) {
    const entryRe = /<entry\b[^>]*\bpath="([^"]*)"[^>]*>([\s\S]*?)<\/entry>/g;
    let m;
    while ((m = entryRe.exec(output)) !== null) {
      const entryPath = resolve(decodeXmlAttr(m[1]));
      const info = parseSvnEntry(m[2]);
      if (!info) continue;
      byPath.set(entryPath, info);
      const base = basename(entryPath);
      if (!byBase.has(base)) byBase.set(base, []);
      byBase.get(base).push(info);
    }
  }

  return filePaths.map((filePath) => {
    const abs = resolve(filePath);
    /** @type {import('../vcStatus.js').VCFileStatus} */
    const status = { filePath: abs, system: 'svn', writable: writableBit(abs) };
    let info = byPath.get(abs);
    if (!info) {
      // svn may canonicalize the echoed path (symlinked parents); fall back to
      // a basename match when it is unambiguous.
      const sameName = byBase.get(basename(abs));
      if (sameName && sameName.length === 1) info = sameName[0];
    }
    if (info) {
      if (info.tracked !== undefined) status.tracked = info.tracked;
      if (info.dirty !== undefined) status.dirty = info.dirty;
      if (info.openedByMe) status.openedByMe = true;
      if (info.lockedBy) status.lockedBy = info.lockedBy;
      if (info.outOfDate) status.outOfDate = true;
    }
    return status;
  });
}

/** XML wc-status item values that mean a tracked file with pending local changes. */
const SVN_DIRTY_ITEMS = new Set([
  'modified', 'added', 'deleted', 'replaced', 'conflicted', 'missing', 'incomplete',
]);
/** item values that mean the path is not under version control. */
const SVN_UNTRACKED_ITEMS = new Set(['unversioned', 'ignored']);

/**
 * Parse one `<entry>` body into tracked / dirty (always) plus, when `-u` added a
 * `<repos-status>`, openedByMe / lockedBy / outOfDate.
 *
 * @param {string} body
 * @returns {{tracked?: boolean, dirty?: boolean, openedByMe?: boolean, lockedBy?: string[], outOfDate?: boolean} | null}
 */
function parseSvnEntry(body) {
  const wc = extractTag(body, 'wc-status');
  if (!wc) return null;
  const item = attr(wc.attrs, 'item');
  const props = attr(wc.attrs, 'props');
  const info = classifySvnStatus(item, props);

  const repos = extractTag(body, 'repos-status');
  if (repos) {
    // repos-status only appears with -u. item != 'none' => server has changes we lack.
    const reposItem = attr(repos.attrs, 'item');
    if (reposItem && reposItem !== 'none') info.outOfDate = true;
    // A <lock> inside wc-status is our own held token; inside repos-status it is the
    // authoritative server holder. Ours => openedByMe; theirs (no wc token) => lockedBy.
    const heldByUs = /<lock\b/.test(wc.inner);
    const owner = /<lock\b[^>]*>[\s\S]*?<owner>([^<]*)<\/owner>/.exec(repos.inner)?.[1];
    if (heldByUs) info.openedByMe = true;
    else if (owner) info.lockedBy = [decodeXmlAttr(owner)];
  }
  return info;
}

/**
 * Map an `svn status` wc-status (item + props) to tracked / dirty.
 * `props` carries property-only modifications ("modified" / "conflicted").
 *
 * @param {string} item
 * @param {string} props
 * @returns {{tracked?: boolean, dirty?: boolean}}
 */
function classifySvnStatus(item, props) {
  if (SVN_UNTRACKED_ITEMS.has(item)) return { tracked: false, dirty: false };
  if (item === 'normal' || SVN_DIRTY_ITEMS.has(item)) {
    const dirty = SVN_DIRTY_ITEMS.has(item) || props === 'modified' || props === 'conflicted';
    return { tracked: true, dirty };
  }
  // 'external', 'none', or anything unrecognised: can't say.
  return {};
}

/**
 * Extract a `<tag ...>inner</tag>` (or self-closing `<tag .../>`) block. svn spreads
 * attributes across newlines, which the `[^>]` classes tolerate.
 *
 * @param {string} body
 * @param {string} tag
 * @returns {{attrs: string, inner: string} | null}
 */
function extractTag(body, tag) {
  const container = new RegExp(`<${tag}\\b([^>]*)>([\\s\\S]*?)</${tag}>`).exec(body);
  if (container) return { attrs: container[1], inner: container[2] };
  const selfClose = new RegExp(`<${tag}\\b([^>]*?)/>`).exec(body);
  if (selfClose) return { attrs: selfClose[1], inner: '' };
  return null;
}

/** Read one XML attribute value from a (possibly multiline) attribute string. */
function attr(attrs, name) {
  return new RegExp(`\\b${name}="([^"]*)"`).exec(attrs)?.[1] ?? '';
}

/** Decode the handful of XML entities svn emits in path attributes. */
function decodeXmlAttr(value) {
  return value
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, '&'); // last, so "&amp;lt;" -> "&lt;" not "<"
}

