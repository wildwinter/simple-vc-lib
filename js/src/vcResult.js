/**
 * @typedef {'ok' | 'locked' | 'outOfDate' | 'error'} VCStatus
 * @typedef {{ success: boolean, status: VCStatus, message: string }} VCResult
 */

/** @returns {VCResult} */
export function okResult(message = '') {
  return { success: true, status: 'ok', message };
}

/** @returns {VCResult} */
export function errorResult(status, message) {
  return { success: false, status, message };
}
