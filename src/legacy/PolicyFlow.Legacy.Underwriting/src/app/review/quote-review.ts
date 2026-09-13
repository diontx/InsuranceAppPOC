import { CurrencyPipe, DatePipe, DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, ValidatorFn, Validators } from '@angular/forms';
import { map } from 'rxjs';

import { ApiProblem } from '../api/api-problem';
import {
  NOTE_MAX_LENGTH,
  NOTE_MIN_LENGTH,
  ReviewDecision,
  UnderwritingReview,
} from '../api/underwriting.models';
import { StatusPill } from '../shared/status-pill';
import { UtcPipe } from '../shared/utc.pipe';

export interface DecisionSubmission {
  decision: ReviewDecision;
  note: string;
}

interface DecisionOption {
  value: ReviewDecision;
  label: string;
  description: string;
  disabled: boolean;
}

/** Required, ignoring whitespace: "   " is not a reason. */
export const requiredTrimmed: ValidatorFn = (control) =>
  typeof control.value === 'string' && control.value.trim().length > 0 ? null : { required: true };

export function minTrimmedLength(min: number): ValidatorFn {
  return (control) => {
    const length = typeof control.value === 'string' ? control.value.trim().length : 0;
    return length > 0 && length < min ? { minlength: { requiredLength: min, actualLength: length } } : null;
  };
}

/** The note rules depend on the decision, so the validators do too. */
export function noteValidators(decision: ReviewDecision | null): ValidatorFn[] {
  const maxLength = Validators.maxLength(NOTE_MAX_LENGTH);

  return decision === 'FollowUp' || decision === 'Decline'
    ? [requiredTrimmed, minTrimmedLength(NOTE_MIN_LENGTH), maxLength]
    : [maxLength];
}

/**
 * The review form: the application as submitted, why it was referred, and the
 * decision.
 *
 * The application fields are read-only inputs rather than text, so the page reads
 * as the familiar underwriting form, but nothing about the customer's submission
 * is editable here — an underwriter decides on what was applied for.
 *
 * Presentational: it validates and emits a {@link DecisionSubmission}; the store
 * owns the HTTP call, and the parent passes the result back in as `review` and
 * `error`.
 */
@Component({
  selector: 'uw-quote-review',
  imports: [ReactiveFormsModule, CurrencyPipe, DatePipe, DecimalPipe, StatusPill, UtcPipe],
  templateUrl: './quote-review.html',
  styleUrl: './quote-review.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QuoteReview {
  readonly review = input.required<UnderwritingReview>();
  readonly submitting = input(false);
  readonly error = input<ApiProblem | null>(null);

  readonly decided = output<DecisionSubmission>();

  protected readonly noteMaxLength = NOTE_MAX_LENGTH;

  /** The server's message about the note, and the note text it was about. */
  private serverNoteError: { message: string; value: string } | null = null;

  /**
   * Holds the server's 400 on the note for as long as the note is unchanged.
   *
   * A validator rather than `setErrors()`: Angular recomputes validity whenever a
   * `formControlName` directive attaches or a validator changes, which silently
   * discards errors set by hand. Tied to the text it was about, it clears itself
   * the moment the underwriter edits the note.
   */
  private readonly serverNoteValidator: ValidatorFn = (control) =>
    this.serverNoteError && control.value === this.serverNoteError.value
      ? { server: this.serverNoteError.message }
      : null;

  protected readonly form = new FormGroup({
    decision: new FormControl<ReviewDecision | null>(null, { validators: Validators.required }),
    note: new FormControl('', {
      nonNullable: true,
      validators: [...noteValidators(null), this.serverNoteValidator],
    }),
  });

  protected readonly decision = toSignal(this.form.controls.decision.valueChanges, {
    initialValue: null,
  });

  protected readonly noteLength = toSignal(
    this.form.controls.note.valueChanges.pipe(map((value) => value.length)),
    { initialValue: 0 },
  );

  protected readonly willingCarriers = computed(() =>
    this.review().carrierAppetite.filter((carrier) => carrier.willWrite),
  );

  protected readonly decisionOptions = computed<DecisionOption[]>(() => {
    const willing = this.willingCarriers().length;

    return [
      {
        value: 'Approve',
        label: 'Approve',
        description:
          willing > 0
            ? `Price with the ${willing === 1 ? '1 carrier' : `${willing} carriers`} in appetite and release the offers to the applicant.`
            : 'Unavailable: no carrier on the panel will write this risk.',
        disabled: willing === 0,
      },
      {
        value: 'FollowUp',
        label: 'Request follow-up',
        description: 'Keep it in the queue and tell the applicant what is needed.',
        disabled: false,
      },
      {
        value: 'Decline',
        label: 'Decline',
        description: 'Reject the application. This closes the referral.',
        disabled: false,
      },
    ];
  });

  protected readonly noteLabel = computed(() => {
    switch (this.decision()) {
      case 'FollowUp':
        return 'Information needed from the applicant';
      case 'Decline':
        return 'Reason for declining';
      default:
        return 'Underwriting note (optional)';
    }
  });

  protected readonly submitLabel = computed(() => {
    switch (this.decision()) {
      case 'Approve':
        return 'Approve quote';
      case 'FollowUp':
        return 'Send follow-up request';
      case 'Decline':
        return 'Decline quote';
      default:
        return 'Record decision';
    }
  });

  /**
   * A problem not tied to the note: a 409 because another underwriter decided
   * first, a 422 because no carrier would price it, or the API being unreachable.
   */
  protected readonly generalError = computed(() => {
    const problem = this.error();
    if (!problem) {
      return null;
    }

    const fields = Object.keys(problem.fieldErrors);
    return fields.length === 1 && fields[0] === 'note' ? null : problem;
  });

  private readonly changeDetector = inject(ChangeDetectorRef);

  /** The review the form was last reset for. */
  private formReview: UnderwritingReview | null = null;

  constructor() {
    // ONE effect, deliberately. Resetting the form and applying the server's
    // field error both touch the note control, and Angular does not guarantee the
    // order two effects run in; ordered here, a reset cannot wipe the message.
    effect(() => {
      const review = this.review();
      const serverNoteMessage = this.error()?.fieldErrors['note'];

      untracked(() => {
        const note = this.form.controls.note;

        // A different quote, or this quote after a decision: start clean, and lock
        // the form once there is nothing left to decide. An error arriving for the
        // SAME review must not reset it, or a 400 would erase the note being fixed.
        if (review !== this.formReview) {
          this.formReview = review;
          this.serverNoteError = null;
          this.form.reset({ decision: null, note: '' });

          if (review.isAwaitingReview) {
            this.form.enable();
          } else {
            this.form.disable();
          }
        }

        // A 400 from the server lands on the field it names, beside the text box.
        this.serverNoteError = serverNoteMessage ? { message: serverNoteMessage, value: note.value } : null;
        note.updateValueAndValidity();
        if (serverNoteMessage) {
          note.markAsTouched();
        }

        // Reactive-form state is not a signal. Zoneless and OnPush, nothing else
        // re-renders the template for validity changed here.
        this.changeDetector.markForCheck();
      });
    });

    this.form.controls.decision.valueChanges.pipe(takeUntilDestroyed()).subscribe((decision) => {
      const note = this.form.controls.note;
      note.setValidators([...noteValidators(decision), this.serverNoteValidator]);
      note.updateValueAndValidity();
    });
  }

  protected submit(): void {
    this.form.markAllAsTouched();

    const { decision, note } = this.form.getRawValue();
    if (this.form.invalid || decision === null || this.submitting()) {
      return;
    }

    this.decided.emit({ decision, note: note.trim() });
  }

  protected decisionMissing(): boolean {
    const control = this.form.controls.decision;
    return control.touched && control.hasError('required');
  }

  protected noteError(): string | null {
    const control = this.form.controls.note;
    const errors = control.errors;

    if (!control.touched || !errors) {
      return null;
    }

    if (errors['server']) {
      return errors['server'] as string;
    }

    if (errors['required']) {
      return this.decision() === 'Decline'
        ? 'State the reason for declining.'
        : 'Say what information is needed from the applicant.';
    }

    if (errors['minlength']) {
      return `Note must be at least ${NOTE_MIN_LENGTH} characters.`;
    }

    if (errors['maxlength']) {
      return `Note must be ${NOTE_MAX_LENGTH} characters or fewer.`;
    }

    return null;
  }
}
