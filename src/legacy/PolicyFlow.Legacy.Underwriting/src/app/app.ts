import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';

import { WorkQueue } from './queue/work-queue';
import { DecisionSubmission, QuoteReview } from './review/quote-review';
import { UnderwritingStore } from './state/underwriting-store';

/**
 * The underwriting workbench: work queue beside the review form.
 *
 * Bootstrapped into `<uw-workbench>` inside `Underwriting.aspx`, so it lives within
 * the Web Forms master page — header, navigation and the single server-side form —
 * while owning everything inside its element.
 */
@Component({
  selector: 'uw-workbench',
  imports: [WorkQueue, QuoteReview],
  templateUrl: './app.html',
  styleUrl: './app.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  protected readonly store = inject(UnderwritingStore);

  /** Ticks once a minute so "time in queue" stays current without polling. */
  protected readonly now = signal(Date.now());

  constructor() {
    const timer = setInterval(() => this.now.set(Date.now()), 60_000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));

    void this.store.loadQueue();
  }

  protected onSelect(quoteId: string): void {
    void this.store.select(quoteId);
  }

  protected onRefresh(): void {
    void this.store.loadQueue();
  }

  protected onDecide(submission: DecisionSubmission): void {
    void this.store.decide(submission.decision, submission.note);
  }
}
