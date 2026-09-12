import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { ApiError, api } from '../api/client';
import type { SubmitQuoteRequest } from '../api/types';
import { ErrorAlert } from '../components/Alert';

/** States the program is filed in. Must match the API's validator. */
const SUPPORTED_STATES = [
  { code: 'GA', name: 'Georgia' },
  { code: 'FL', name: 'Florida' },
  { code: 'TX', name: 'Texas' },
  { code: 'NC', name: 'North Carolina' },
  { code: 'SC', name: 'South Carolina' },
  { code: 'TN', name: 'Tennessee' },
  { code: 'AL', name: 'Alabama' },
  { code: 'VA', name: 'Virginia' },
  { code: 'MS', name: 'Mississippi' },
];

const CURRENT_YEAR = new Date().getFullYear();

interface FormState {
  customerName: string;
  state: string;
  vehicleYear: string;
  vehicleMake: string;
  vehicleModel: string;
  annualMileage: string;
  priorInsuranceMonths: string;
}

const INITIAL_FORM: FormState = {
  customerName: '',
  state: 'GA',
  vehicleYear: String(CURRENT_YEAR - 2),
  vehicleMake: '',
  vehicleModel: '',
  annualMileage: '12000',
  priorInsuranceMonths: '36',
};

/**
 * Client-side validation.
 *
 * A convenience layer, not a security boundary: the API validates independently,
 * and the domain enforces its own invariants regardless of which client called.
 * Its only job is to give immediate feedback instead of a network round trip.
 */
function validate(form: FormState): Partial<Record<keyof FormState, string>> {
  const errors: Partial<Record<keyof FormState, string>> = {};

  if (!form.customerName.trim()) {
    errors.customerName = 'Enter the name to be insured.';
  } else if (form.customerName.trim().length < 2) {
    errors.customerName = 'Name must be at least 2 characters.';
  } else if (!/^[\p{L}\p{M}\s'\-.]+$/u.test(form.customerName.trim())) {
    errors.customerName = 'Use letters, spaces, hyphens, apostrophes and periods only.';
  }

  if (!SUPPORTED_STATES.some((s) => s.code === form.state)) {
    errors.state = 'Choose a state from the list.';
  }

  const year = Number(form.vehicleYear);
  if (!Number.isInteger(year) || year < 1981 || year > CURRENT_YEAR + 1) {
    errors.vehicleYear = `Enter a year between 1981 and ${CURRENT_YEAR + 1}.`;
  }

  if (!form.vehicleMake.trim()) {
    errors.vehicleMake = 'Enter the vehicle make.';
  }

  if (!form.vehicleModel.trim()) {
    errors.vehicleModel = 'Enter the vehicle model.';
  }

  const mileage = Number(form.annualMileage);
  if (!Number.isInteger(mileage) || mileage < 0 || mileage > 100000) {
    errors.annualMileage = 'Enter annual mileage between 0 and 100,000.';
  }

  const priorMonths = Number(form.priorInsuranceMonths);
  if (!Number.isInteger(priorMonths) || priorMonths < 0 || priorMonths > 600) {
    errors.priorInsuranceMonths = 'Enter months of prior coverage between 0 and 600.';
  }

  return errors;
}

export function NewQuotePage() {
  const navigate = useNavigate();
  const [form, setForm] = useState<FormState>(INITIAL_FORM);
  const [errors, setErrors] = useState<Partial<Record<keyof FormState, string>>>({});
  const [submitError, setSubmitError] = useState<Error | ApiError | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);

  function update(field: keyof FormState, value: string) {
    setForm((previous) => ({ ...previous, [field]: value }));

    // Clear this field's error as the user corrects it, rather than leaving a
    // stale message under a now-valid input.
    setErrors((previous) => {
      if (!previous[field]) {
        return previous;
      }
      const next = { ...previous };
      delete next[field];
      return next;
    });
  }

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();

    const validationErrors = validate(form);
    setErrors(validationErrors);

    if (Object.keys(validationErrors).length > 0) {
      // Move focus to the first invalid field so a keyboard or screen-reader
      // user is taken to the problem rather than left to hunt for it.
      const firstField = Object.keys(validationErrors)[0];
      document.getElementById(firstField)?.focus();
      return;
    }

    setIsSubmitting(true);
    setSubmitError(null);

    const payload: SubmitQuoteRequest = {
      customerName: form.customerName.trim(),
      state: form.state,
      vehicleYear: Number(form.vehicleYear),
      vehicleMake: form.vehicleMake.trim(),
      vehicleModel: form.vehicleModel.trim(),
      annualMileage: Number(form.annualMileage),
      priorInsuranceMonths: Number(form.priorInsuranceMonths),
    };

    try {
      const result = await api.submitQuote(payload);
      // The API returned 202: the quote exists but has no price yet. The status
      // page takes over and polls.
      navigate(`/quotes/${result.quoteId}`);
    } catch (error) {
      // Server-side validation wins: map its per-field messages onto the form.
      if (error instanceof ApiError && error.status === 400) {
        const serverFieldErrors = error.fieldErrors;

        if (Object.keys(serverFieldErrors).length > 0) {
          setErrors(serverFieldErrors as Partial<Record<keyof FormState, string>>);
          // Shown inline under each field, so a summary alert would repeat the
          // same sentence twice on screen.
          document.getElementById(Object.keys(serverFieldErrors)[0])?.focus();
          return;
        }
      }

      setSubmitError(error as Error);
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <div className="stack stack--lg">
      <div>
        <h1>Get your quote</h1>
        <p className="text-muted" style={{ marginTop: 'var(--space-2)' }}>
          One form, five carriers. Nothing is charged and no policy is created until
          you choose an option.
        </p>
      </div>

      {submitError && <ErrorAlert error={submitError} />}

      <form className="card" onSubmit={handleSubmit} noValidate>
        <div className="card__body stack stack--lg">
          <fieldset className="fieldset">
            <legend className="fieldset__legend">Driver</legend>
            <div className="form-grid">
              <div className="form-field form-field--full">
                <label className="form-field__label" htmlFor="customerName">
                  Full name
                </label>
                <input
                  id="customerName"
                  name="customerName"
                  className="input"
                  type="text"
                  value={form.customerName}
                  onChange={(e) => update('customerName', e.target.value)}
                  aria-invalid={Boolean(errors.customerName)}
                  aria-describedby={
                    errors.customerName ? 'customerName-error' : 'customerName-hint'
                  }
                  autoComplete="name"
                  required
                />
                {errors.customerName ? (
                  <p className="form-field__error" id="customerName-error">
                    {errors.customerName}
                  </p>
                ) : (
                  <p className="form-field__hint" id="customerName-hint">
                    The person to be insured.
                  </p>
                )}
              </div>

              <div className="form-field">
                <label className="form-field__label" htmlFor="state">
                  Garaging state
                </label>
                <select
                  id="state"
                  name="state"
                  className="select"
                  value={form.state}
                  onChange={(e) => update('state', e.target.value)}
                  aria-invalid={Boolean(errors.state)}
                  aria-describedby={errors.state ? 'state-error' : 'state-hint'}
                >
                  {SUPPORTED_STATES.map((state) => (
                    <option key={state.code} value={state.code}>
                      {state.name}
                    </option>
                  ))}
                </select>
                {errors.state ? (
                  <p className="form-field__error" id="state-error">
                    {errors.state}
                  </p>
                ) : (
                  <p className="form-field__hint" id="state-hint">
                    Not every carrier writes every state.
                  </p>
                )}
              </div>

              <div className="form-field">
                <label className="form-field__label" htmlFor="priorInsuranceMonths">
                  Months of prior coverage
                </label>
                <input
                  id="priorInsuranceMonths"
                  name="priorInsuranceMonths"
                  className="input"
                  type="number"
                  inputMode="numeric"
                  min={0}
                  max={600}
                  value={form.priorInsuranceMonths}
                  onChange={(e) => update('priorInsuranceMonths', e.target.value)}
                  aria-invalid={Boolean(errors.priorInsuranceMonths)}
                  aria-describedby={
                    errors.priorInsuranceMonths
                      ? 'priorInsuranceMonths-error'
                      : 'priorInsuranceMonths-hint'
                  }
                />
                {errors.priorInsuranceMonths ? (
                  <p className="form-field__error" id="priorInsuranceMonths-error">
                    {errors.priorInsuranceMonths}
                  </p>
                ) : (
                  <p className="form-field__hint" id="priorInsuranceMonths-hint">
                    Enter 0 if you have no current policy — this requires review.
                  </p>
                )}
              </div>
            </div>
          </fieldset>

          <fieldset className="fieldset">
            <legend className="fieldset__legend">Vehicle</legend>
            <div className="form-grid">
              <div className="form-field">
                <label className="form-field__label" htmlFor="vehicleYear">
                  Model year
                </label>
                <input
                  id="vehicleYear"
                  name="vehicleYear"
                  className="input"
                  type="number"
                  inputMode="numeric"
                  min={1981}
                  max={CURRENT_YEAR + 1}
                  value={form.vehicleYear}
                  onChange={(e) => update('vehicleYear', e.target.value)}
                  aria-invalid={Boolean(errors.vehicleYear)}
                  aria-describedby={errors.vehicleYear ? 'vehicleYear-error' : undefined}
                />
                {errors.vehicleYear && (
                  <p className="form-field__error" id="vehicleYear-error">
                    {errors.vehicleYear}
                  </p>
                )}
              </div>

              <div className="form-field">
                <label className="form-field__label" htmlFor="annualMileage">
                  Annual mileage
                </label>
                <input
                  id="annualMileage"
                  name="annualMileage"
                  className="input"
                  type="number"
                  inputMode="numeric"
                  min={0}
                  max={100000}
                  step={500}
                  value={form.annualMileage}
                  onChange={(e) => update('annualMileage', e.target.value)}
                  aria-invalid={Boolean(errors.annualMileage)}
                  aria-describedby={
                    errors.annualMileage ? 'annualMileage-error' : 'annualMileage-hint'
                  }
                />
                {errors.annualMileage ? (
                  <p className="form-field__error" id="annualMileage-error">
                    {errors.annualMileage}
                  </p>
                ) : (
                  <p className="form-field__hint" id="annualMileage-hint">
                    Lower mileage earns discounts with some carriers.
                  </p>
                )}
              </div>

              <div className="form-field">
                <label className="form-field__label" htmlFor="vehicleMake">
                  Make
                </label>
                <input
                  id="vehicleMake"
                  name="vehicleMake"
                  className="input"
                  type="text"
                  value={form.vehicleMake}
                  onChange={(e) => update('vehicleMake', e.target.value)}
                  aria-invalid={Boolean(errors.vehicleMake)}
                  aria-describedby={errors.vehicleMake ? 'vehicleMake-error' : undefined}
                  placeholder="Toyota"
                  required
                />
                {errors.vehicleMake && (
                  <p className="form-field__error" id="vehicleMake-error">
                    {errors.vehicleMake}
                  </p>
                )}
              </div>

              <div className="form-field">
                <label className="form-field__label" htmlFor="vehicleModel">
                  Model
                </label>
                <input
                  id="vehicleModel"
                  name="vehicleModel"
                  className="input"
                  type="text"
                  value={form.vehicleModel}
                  onChange={(e) => update('vehicleModel', e.target.value)}
                  aria-invalid={Boolean(errors.vehicleModel)}
                  aria-describedby={errors.vehicleModel ? 'vehicleModel-error' : undefined}
                  placeholder="Camry"
                  required
                />
                {errors.vehicleModel && (
                  <p className="form-field__error" id="vehicleModel-error">
                    {errors.vehicleModel}
                  </p>
                )}
              </div>
            </div>
          </fieldset>
        </div>

        <div className="card__footer row row--between">
          <p className="text-xs text-muted" style={{ maxWidth: '46ch' }}>
            Submitting starts underwriting in the background. You will see prices in
            a few seconds.
          </p>
          <button
            type="submit"
            className="btn btn--accent btn--block"
            disabled={isSubmitting}
          >
            {isSubmitting ? (
              <>
                <span className="spinner" aria-hidden="true" />
                Submitting…
              </>
            ) : (
              'Compare carriers'
            )}
          </button>
        </div>
      </form>

      <details className="card">
        <summary
          className="card__header"
          style={{ cursor: 'pointer', listStyle: 'none' }}
        >
          <h2 style={{ fontSize: 'var(--text-base)' }}>Demo shortcuts</h2>
        </summary>
        <div className="card__body stack stack--sm text-sm">
          <p>
            These inputs trigger specific architectural behaviours, so the demo can
            show them on command:
          </p>
          <ul style={{ margin: 0, paddingLeft: 'var(--space-5)' }} className="stack stack--sm">
            <li>
              <strong>Prior coverage = 0</strong> — the quote is referred to a human
              underwriter and no carrier is shopped.
            </li>
            <li>
              <strong>Prior coverage = 3</strong> — most carriers decline on appetite;
              only the non-standard carrier quotes, so the panel comes back short.
            </li>
            <li>
              <strong>Florida or Texas</strong> — one carrier is not licensed there and
              drops off the panel.
            </li>
            <li>
              <strong>Name "FAIL-DEPENDENCY"</strong> — the verification service
              fails, so the message is abandoned and retried by Service Bus.
            </li>
            <li>
              <strong>Model year older than {CURRENT_YEAR - 25}</strong> — the risk is
              declined outright.
            </li>
          </ul>
        </div>
      </details>
    </div>
  );
}
