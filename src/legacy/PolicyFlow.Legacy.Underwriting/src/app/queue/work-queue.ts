import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';

import { QueueStatus, UnderwritingQueueItem } from '../api/underwriting.models';
import { StatusPill } from '../shared/status-pill';
import { TimeInQueuePipe } from '../shared/time-in-queue.pipe';

export type QueueFilter = 'All' | QueueStatus;

/**
 * The list of quotes awaiting an underwriter.
 *
 * Presentational: it receives items and emits the underwriter's intent. Filtering
 * and search are local UI state held here, because no other part of the workbench
 * cares what the underwriter typed into the search box.
 *
 * Hosted inside a Web Forms page, the search box sits within the master page's
 * single `<form runat="server">`. Enter in a text input submits its form, so
 * without the keydown handler in the template, searching would post the whole
 * page back to IIS and reload the workbench.
 */
@Component({
  selector: 'uw-work-queue',
  imports: [StatusPill, TimeInQueuePipe],
  templateUrl: './work-queue.html',
  styleUrl: './work-queue.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkQueue {
  readonly items = input.required<readonly UnderwritingQueueItem[]>();
  readonly selectedQuoteId = input<string | null>(null);
  readonly loading = input(false);
  /** Epoch milliseconds, ticked by the workbench so ages refresh. */
  readonly now = input.required<number>();

  readonly quoteSelected = output<string>();
  readonly refreshRequested = output<void>();

  protected readonly filter = signal<QueueFilter>('All');
  protected readonly search = signal('');

  protected readonly filters: readonly { value: QueueFilter; label: string }[] = [
    { value: 'All', label: 'All' },
    { value: 'Referral', label: 'Referrals' },
    { value: 'FollowUp', label: 'Follow-up' },
  ];

  protected readonly counts = computed<Record<QueueFilter, number>>(() => {
    const items = this.items();
    return {
      All: items.length,
      Referral: items.filter((item) => item.status === 'Referral').length,
      FollowUp: items.filter((item) => item.status === 'FollowUp').length,
    };
  });

  protected readonly visibleItems = computed(() => {
    const filter = this.filter();
    const term = this.search().trim().toLowerCase();

    return this.items().filter(
      (item) =>
        (filter === 'All' || item.status === filter) &&
        (term.length === 0 ||
          item.quoteNumber.toLowerCase().includes(term) ||
          item.customerName.toLowerCase().includes(term)),
    );
  });

  protected onSearch(event: Event): void {
    this.search.set((event.target as HTMLInputElement).value);
  }
}
