import { spawnSync, spawn } from 'child_process';

/** @type {((command: string, args: string[], options?: object) => object) | null} */
let _overrideRunner = null;

/**
 * Override the command runner used for all VC operations.
 * Lets tests inject canned CLI output (e.g. `p4 -ztag fstat` transcripts) so
 * provider logic is unit-testable without the VCS installed - the same pattern
 * as `setProvider`. Pass null to clear.
 *
 * @param {((command: string, args: string[], options?: object) => {exitCode: number, output: string, error: string, timedOut?: boolean}) | null} runner
 */
export function setCommandRunner(runner) {
  _overrideRunner = runner;
}

/** Clear any previously set command-runner override. */
export function clearCommandRunner() {
  _overrideRunner = null;
}

/**
 * Run a CLI command synchronously.
 * Arguments are passed as an array to avoid shell-injection and handle spaces in paths.
 */
export function runCommand(command, args, options = {}) {
  if (_overrideRunner) return _overrideRunner(command, args, options);

  const result = spawnSync(command, args, {
    encoding: 'utf8',
    timeout: options.timeout ?? 10000,
    cwd: options.cwd,
    windowsHide: true,
  });

  let errText = (result.stderr ?? '').trim();
  // If spawnSync failed to even launch the process (e.g. ENOENT), capture the underlying error message.
  if (!errText && result.error) {
    errText = result.error.message;
  }

  const exitCode = result.status ?? -1;

  // Output is trimmed by default for convenience; pass { trim: false } when byte
  // exactness matters (e.g. `git status -z`, whose first entry can begin with a
  // significant space that trimming would strip).
  const stdout = result.stdout ?? '';

  return {
    exitCode,
    output: options.trim === false ? stdout : stdout.trim(),
    error: errText,
    timedOut: result.signal === 'SIGTERM' || result.signal === 'SIGKILL',
  };
}

/**
 * Async twin of {@link runCommand} - same return shape, but spawned without blocking
 * the event loop. Honours the same command-runner override (a canned runner may return
 * a value or a Promise; both are awaited), so the test transcripts work unchanged.
 *
 * @param {string} command
 * @param {string[]} args
 * @param {object} [options]
 * @returns {Promise<{exitCode: number, output: string, error: string, timedOut: boolean}>}
 */
export async function runCommandAsync(command, args, options = {}) {
  if (_overrideRunner) {
    const overridden = await _overrideRunner(command, args, options);
    return { timedOut: false, ...overridden };
  }

  return new Promise((resolve) => {
    const child = spawn(command, args, { cwd: options.cwd, windowsHide: true });
    let stdout = '';
    let stderr = '';
    let timedOut = false;
    let settled = false;
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });

    const timer = setTimeout(() => { timedOut = true; child.kill('SIGTERM'); }, options.timeout ?? 10000);
    const finish = (result) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(result);
    };

    child.on('error', (err) => {
      // Failed to even launch (e.g. ENOENT) - mirror the sync path's error capture.
      finish({ exitCode: -1, output: '', error: stderr.trim() || err.message, timedOut });
    });
    child.on('close', (code, signal) => {
      finish({
        exitCode: code ?? -1,
        output: options.trim === false ? stdout : stdout.trim(),
        error: stderr.trim(),
        timedOut: timedOut || signal === 'SIGTERM' || signal === 'SIGKILL',
      });
    });
  });
}
