import { Pipe, PipeTransform } from '@angular/core';

import { parseUtc } from './utc.pipe';

/**
 * How long a quote has waited, e.g. `3h 5m`.
 *
 * Pure: "now" is an argument rather than read inside the pipe. An impure pipe that
 * called `Date.now()` would re-run on every change detection pass and still only
 * update when something else happened to trigger one. The workbench ticks a `now`
 * signal once a minute instead, which is both cheaper and actually refreshes.
 */
@Pipe({ name: 'timeInQueue' })
export class TimeInQueuePipe implements PipeTransform {
  transform(sinceUtc: string | null | undefined, nowMs: number): string {
    const since = parseUtc(sinceUtc);
    if (!since) {
      return '—';
    }

    // Clamped at zero: a client clock slightly behind the server must not show a
    // negative wait.
    const minutes = Math.max(0, Math.floor((nowMs - since.getTime()) / 60_000));

    if (minutes < 1) {
      return 'just now';
    }

    if (minutes < 60) {
      return `${minutes}m`;
    }

    const hours = Math.floor(minutes / 60);
    if (hours < 24) {
      const remainder = minutes % 60;
      return remainder === 0 ? `${hours}h` : `${hours}h ${remainder}m`;
    }

    const days = Math.floor(hours / 24);
    const remainder = hours % 24;
    return remainder === 0 ? `${days}d` : `${days}d ${remainder}h`;
  }
}
