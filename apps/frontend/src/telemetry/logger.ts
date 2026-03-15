/**
 * Structured logger utility.
 * In development, uses the native console with formatted output.
 * In production, emits JSON-structured log records.
 */

type LogContext = Record<string, unknown>;

function emit(level: 'error' | 'warn' | 'info', message: string, context?: LogContext): void {
  if (import.meta.env.DEV) {
    const fn = level === 'error' ? console.error : level === 'warn' ? console.warn : console.info;
    fn(message, context ?? '');
  } else {
    // JSON-structured output for production log aggregation
    const entry = { level, message, ...context, timestamp: new Date().toISOString() };
    (level === 'error' ? console.error : level === 'warn' ? console.warn : console.info)(
      JSON.stringify(entry)
    );
  }
}

export const log = {
  error: (message: string, context?: LogContext) => emit('error', message, context),
  warn: (message: string, context?: LogContext) => emit('warn', message, context),
  info: (message: string, context?: LogContext) => emit('info', message, context),
};
