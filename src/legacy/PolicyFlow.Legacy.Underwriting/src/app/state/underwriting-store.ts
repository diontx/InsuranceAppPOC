import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiProblem, toApiProblem } from '../api/api-problem';
import { UnderwritingApiService } from '../api/underwriting-api.service';
import { ReviewDecision, UnderwritingQueue, UnderwritingReview } from '../api/underwriting.models';

/** A plain-language confirmation of what a decision did. */
export interface DecisionOutcome {
  quoteNumber: string;
  decision: ReviewDecision;
  message: string;
}

/**
 * State for the workbench, held in signals.
 *
 * One injectable rather than state spread across components, so the queue, the
 * open quote and the decision in flight cannot disagree. Components read signals
 * and call methods; they never talk to HTTP.
 *
 * Signals rather than an NgRx store: three pieces of state and one screen do not
 * justify actions, reducers and effects, and signals give the same unidirectional
 * flow with fine-grained change detection (the module runs zoneless).
 */
@Injectable({ providedIn: 'root' })
export class UnderwritingStore {
  private readonly api = inject(UnderwritingApiService);

  private readonly _queue = signal<UnderwritingQueue | null>(null);
  private readonly _queueLoading = signal(false);
  private readonly _queueError = signal<ApiProblem | null>(null);
  private readonly _selectedQuoteId = signal<string | null>(null);
  private readonly _review = signal<UnderwritingReview | null>(null);
  private readonly _reviewLoading = signal(false);
  private readonly _reviewError = signal<ApiProblem | null>(null);
  private readonly _submitting = signal(false);
  private readonly _decisionError = signal<ApiProblem | null>(null);
  private readonly _lastOutcome = signal<DecisionOutcome | null>(null);

  readonly queue = this._queue.asReadonly();
  readonly queueLoading = this._queueLoading.asReadonly();
  readonly queueError = this._queueError.asReadonly();
  readonly selectedQuoteId = this._selectedQuoteId.asReadonly();
  readonly review = this._review.asReadonly();
  readonly reviewLoading = this._reviewLoading.asReadonly();
  readonly reviewError = this._reviewError.asReadonly();
  readonly submitting = this._submitting.asReadonly();
  readonly decisionError = this._decisionError.asReadonly();
  readonly lastOutcome = this._lastOutcome.asReadonly();

  readonly items = computed(() => this._queue()?.items ?? []);

  /**
   * Incremented on every selection. A response is applied only if it belongs to
   * the latest selection: an underwriter who clicks A then B must end up looking
   * at B, even when A's response arrives last.
   */
  private selectionVersion = 0;

  async loadQueue(): Promise<void> {
    this._queueLoading.set(true);
    this._queueError.set(null);

    try {
      this._queue.set(await firstValueFrom(this.api.getQueue()));
    } catch (error) {
      this._queueError.set(toApiProblem(error));
    } finally {
      this._queueLoading.set(false);
    }
  }

  async select(quoteId: string): Promise<void> {
    const version = ++this.selectionVersion;

    this._selectedQuoteId.set(quoteId);
    this._review.set(null);
    this._reviewError.set(null);
    this._decisionError.set(null);
    this._reviewLoading.set(true);

    try {
      const review = await firstValueFrom(this.api.getReview(quoteId));
      if (version === this.selectionVersion) {
        this._review.set(review);
      }
    } catch (error) {
      if (version === this.selectionVersion) {
        this._reviewError.set(toApiProblem(error));
      }
    } finally {
      if (version === this.selectionVersion) {
        this._reviewLoading.set(false);
      }
    }
  }

  /** Records a decision on the open quote. Resolves true when it was saved. */
  async decide(decision: ReviewDecision, note: string): Promise<boolean> {
    const current = this._review();

    // A double-click must not send two decisions. The API would answer the second
    // with a 409, but the underwriter should never see that for their own click.
    if (!current || this._submitting()) {
      return false;
    }

    const version = this.selectionVersion;
    const trimmed = note.trim();

    this._submitting.set(true);
    this._decisionError.set(null);
    this._lastOutcome.set(null);

    try {
      const updated = await firstValueFrom(
        this.api.decide(current.quoteId, { decision, note: trimmed.length > 0 ? trimmed : undefined }),
      );

      if (version === this.selectionVersion) {
        this._review.set(updated);
      }

      this._lastOutcome.set(describeOutcome(updated, decision));
      await this.loadQueue();
      return true;
    } catch (error) {
      const problem = toApiProblem(error);
      this._decisionError.set(problem);

      if (problem.status === 409) {
        // Someone else decided first. Show what the quote is now, and a queue
        // without it, rather than leaving a form that can only fail again.
        await Promise.all([this.loadQueue(), this.refreshReview(current.quoteId, version)]);
      }

      return false;
    } finally {
      this._submitting.set(false);
    }
  }

  dismissOutcome(): void {
    this._lastOutcome.set(null);
  }

  /** Re-reads the open quote without clearing the error that explains why. */
  private async refreshReview(quoteId: string, version: number): Promise<void> {
    try {
      const review = await firstValueFrom(this.api.getReview(quoteId));
      if (version === this.selectionVersion) {
        this._review.set(review);
      }
    } catch {
      // The decision error is already on screen; a failed refresh adds nothing.
    }
  }
}

const usd = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' });

export function describeOutcome(review: UnderwritingReview, decision: ReviewDecision): DecisionOutcome {
  const quoteNumber = review.quoteNumber;

  switch (decision) {
    case 'Approve': {
      const premium = review.annualPremium === undefined ? 'unavailable' : usd.format(review.annualPremium);
      const offers = review.offerCount === 1 ? '1 carrier offer' : `${review.offerCount} carrier offers`;
      return {
        quoteNumber,
        decision,
        message: `${quoteNumber} approved and rated: ${offers}, best annual premium ${premium}.`,
      };
    }
    case 'FollowUp':
      return {
        quoteNumber,
        decision,
        message: `${quoteNumber} is held for follow-up. The applicant has been asked: "${review.decisionReason ?? ''}"`,
      };
    case 'Decline':
      return { quoteNumber, decision, message: `${quoteNumber} declined.` };
  }
}
