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

  return {
    exitCode: result.status ?? -1,
    output: (result.stdout ?? '').trim(),
    error: (result.stderr ?? '').trim(),
    timedOut: result.signal === 'SIGTERM' || result.signal === 'SIGKILL',
  };
}
