import { NOW } from '../testing/fixtures';
import { statusLabel } from './status-pill';
import { TimeInQueuePipe } from './time-in-queue.pipe';
import { parseUtc } from './utc.pipe';

describe('parseUtc', () => {
  it('reads a timestamp without an offset as UTC, because the API omits the Z', () => {
    // Without this, every age in the queue would be off by the browser's UTC offset.
    expect(parseUtc('2026-09-13T10:00:00')?.toISOString()).toBe('2026-09-13T10:00:00.000Z');
  });

  it('keeps an explicit Z', () => {
    expect(parseUtc('2026-09-13T10:00:00Z')?.toISOString()).toBe('2026-09-13T10:00:00.000Z');
  });

  it('respects an explicit offset', () => {
    expect(parseUtc('2026-09-13T10:00:00+02:00')?.toISOString()).toBe('2026-09-13T08:00:00.000Z');
  });

  it('returns null for a missing or unparseable value', () => {
    expect(parseUtc(undefined)).toBeNull();
    expect(parseUtc('')).toBeNull();
    expect(parseUtc('not a date')).toBeNull();
  });
});

describe('TimeInQueuePipe', () => {
  const pipe = new TimeInQueuePipe();

  it.each([
    ['2026-09-13T11:59:40Z', 'just now'],
    ['2026-09-13T11:48:00Z', '12m'],
    ['2026-09-13T09:00:00Z', '3h'],
    ['2026-09-13T08:55:00Z', '3h 5m'],
    ['2026-09-11T12:00:00Z', '2d'],
    ['2026-09-11T08:00:00Z', '2d 4h'],
  ])('shows a quote submitted at %s as "%s"', (submitted, expected) => {
    expect(pipe.transform(submitted, NOW)).toBe(expected);
  });

  it('treats an API timestamp with no offset as UTC', () => {
    expect(pipe.transform('2026-09-13T09:00:00', NOW)).toBe('3h');
  });

  it('never shows a negative wait when the browser clock is behind the server', () => {
    expect(pipe.transform('2026-09-13T12:05:00Z', NOW)).toBe('just now');
  });

  it('shows a dash when there is no timestamp', () => {
    expect(pipe.transform(undefined, NOW)).toBe('—');
  });
});

describe('statusLabel', () => {
  it('uses underwriting language for the statuses an underwriter sees', () => {
    expect(statusLabel('FollowUp')).toBe('Follow-up');
    expect(statusLabel('Rated')).toBe('Approved');
    expect(statusLabel('Referral')).toBe('Referral');
  });

  it('passes an unknown status through rather than hiding it', () => {
    expect(statusLabel('Suspended')).toBe('Suspended');
  });
});
