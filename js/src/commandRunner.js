import { spawnSync } from 'child_process';

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

  return {
    exitCode,
    output: (result.stdout ?? '').trim(),
    error: errText,
    timedOut: result.signal === 'SIGTERM' || result.signal === 'SIGKILL',
  };
}
