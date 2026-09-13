import { describe, expect, it } from 'vitest';
import { formatStatusLabel, formatTimeInQueue, parseUtc } from '../api/client';
import { validateNote } from '../components/reviewNoteRules';

const NOW = Date.parse('2026-09-13T12:00:00Z');

describe('parseUtc', () => {
  it('reads a timestamp without an offset as UTC, because the API omits the Z', () => {
    // Without this, every age in the queue is off by the browser's UTC offset.
    expect(parseUtc('2026-09-13T10:00:00')?.toISOString()).toBe('2026-09-13T10:00:00.000Z');
  });

  it('respects an explicit Z or offset', () => {
    expect(parseUtc('2026-09-13T10:00:00Z')?.toISOString()).toBe('2026-09-13T10:00:00.000Z');
    expect(parseUtc('2026-09-13T10:00:00+02:00')?.toISOString()).toBe('2026-09-13T08:00:00.000Z');
  });

  it('returns null for a missing or unparseable value', () => {
    expect(parseUtc(undefined)).toBeNull();
    expect(parseUtc('not a date')).toBeNull();
  });
});

describe('formatTimeInQueue', () => {
  it.each([
    ['2026-09-13T11:59:40Z', 'just now'],
    ['2026-09-13T11:48:00Z', '12m'],
    ['2026-09-13T09:00:00', '3h'],
    ['2026-09-13T08:55:00Z', '3h 5m'],
    ['2026-09-11T12:00:00Z', '2d'],
    ['2026-09-11T08:00:00Z', '2d 4h'],
  ])('shows a quote submitted at %s as "%s"', (submitted, expected) => {
    expect(formatTimeInQueue(submitted, NOW)).toBe(expected);
  });

  it('never shows a negative wait when the browser clock is behind the server', () => {
    expect(formatTimeInQueue('2026-09-13T12:05:00Z', NOW)).toBe('just now');
  });
});

describe('formatStatusLabel', () => {
  it('uses underwriting language and passes unknown statuses through', () => {
    expect(formatStatusLabel('FollowUp')).toBe('Follow-up');
    expect(formatStatusLabel('Rated')).toBe('Approved');
    expect(formatStatusLabel('Suspended')).toBe('Suspended');
  });
});

describe('validateNote', () => {
  it.each([
    ['Approve', '', undefined],
    ['FollowUp', '   ', 'Say what information is needed from the applicant.'],
    ['Decline', '', 'State the reason for declining.'],
    ['Decline', ' No. ', 'Note must be at least 10 characters.'],
    ['FollowUp', 'Send proof.', undefined],
  ] as const)('%s with "%s" → %s', (decision, note, expected) => {
    expect(validateNote(decision, note)).toBe(expected);
  });

  it('caps the note at the length the database stores', () => {
    expect(validateNote('Approve', 'x'.repeat(501))).toBe('Note must be 500 characters or fewer.');
  });
});
