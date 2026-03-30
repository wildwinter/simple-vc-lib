import { spawnSync } from 'child_process';

/**
 * Run a CLI command synchronously.
 * Arguments are passed as an array to avoid shell-injection and handle spaces in paths.
 */
export function runCommand(command, args, options = {}) {
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
