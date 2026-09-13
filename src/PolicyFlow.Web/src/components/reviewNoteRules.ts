import type { ReviewDecision } from '../api/types';

/**
 * Note length rules, mirrored from the API's validator so the review form can say
 * so before a round trip. The server stays authoritative.
 *
 * In their own module rather than beside the component, so component files export
 * only components and React Fast Refresh keeps working.
 */
export const NOTE_MIN_LENGTH = 10;
export const NOTE_MAX_LENGTH = 500;

/** Client-side note validation. The rules depend on the decision. */
export function validateNote(decision: ReviewDecision | null, note: string): string | undefined {
  const trimmed = note.trim();

  if (trimmed.length > NOTE_MAX_LENGTH) {
    return `Note must be ${NOTE_MAX_LENGTH} characters or fewer.`;
  }

  if (decision === 'FollowUp' || decision === 'Decline') {
    if (trimmed.length === 0) {
      return decision === 'Decline'
        ? 'State the reason for declining.'
        : 'Say what information is needed from the applicant.';
    }

    if (trimmed.length < NOTE_MIN_LENGTH) {
      return `Note must be at least ${NOTE_MIN_LENGTH} characters.`;
    }
  }

  return undefined;
}
