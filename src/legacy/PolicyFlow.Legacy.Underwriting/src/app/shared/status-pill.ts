import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

const LABELS: Readonly<Record<string, string>> = {
  Referral: 'Referral',
  FollowUp: 'Follow-up',
  Rated: 'Approved',
  Declined: 'Declined',
  Pending: 'Pending',
  Processing: 'Processing',
};

/** Human label for a quote status; unknown values pass through unchanged. */
export function statusLabel(status: string): string {
  return LABELS[status] ?? status;
}

/**
 * Colour-coded status badge. The word is always rendered, so colour is never the
 * only signal.
 */
@Component({
  selector: 'uw-status-pill',
  template: `<span class="uw-pill" [class]="modifier()">{{ label() }}</span>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StatusPill {
  readonly status = input.required<string>();

  protected readonly label = computed(() => statusLabel(this.status()));
  protected readonly modifier = computed(() => `uw-pill--${this.status().toLowerCase()}`);
}
