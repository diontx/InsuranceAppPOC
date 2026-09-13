import { ComponentFixture, TestBed } from '@angular/core/testing';

import { UnderwritingQueueItem } from '../api/underwriting.models';
import { NOW, queueItem } from '../testing/fixtures';
import { WorkQueue } from './work-queue';

describe('WorkQueue', () => {
  const items: UnderwritingQueueItem[] = [
    queueItem({ quoteId: 'q1', quoteNumber: 'Q-2026-000101', customerName: 'Riley Morgan' }),
    queueItem({
      quoteId: 'q2',
      quoteNumber: 'Q-2026-000102',
      customerName: 'Casey Alvarez',
      status: 'FollowUp',
      reason: 'Upload proof of prior insurance.',
      submittedUtc: '2026-09-12T08:00:00',
    }),
    queueItem({ quoteId: 'q3', quoteNumber: 'Q-2026-000103', customerName: 'Jordan Blake', state: 'TX' }),
  ];

  beforeEach(() => TestBed.configureTestingModule({ imports: [WorkQueue] }));

  async function render(list: UnderwritingQueueItem[] = items, selectedQuoteId: string | null = null) {
    const fixture = TestBed.createComponent(WorkQueue);
    fixture.componentRef.setInput('items', list);
    fixture.componentRef.setInput('now', NOW);
    fixture.componentRef.setInput('selectedQuoteId', selectedQuoteId);
    await fixture.whenStable();
    return fixture;
  }

  const element = (fixture: ComponentFixture<WorkQueue>) => fixture.nativeElement as HTMLElement;

  const rows = (fixture: ComponentFixture<WorkQueue>) =>
    Array.from(element(fixture).querySelectorAll<HTMLButtonElement>('.uw-queue__item'));

  const text = (node: Element | null) => (node?.textContent ?? '').replace(/\s+/g, ' ').trim();

  const filterButton = (fixture: ComponentFixture<WorkQueue>, label: string) =>
    Array.from(element(fixture).querySelectorAll<HTMLButtonElement>('.uw-segmented button')).find((b) =>
      text(b).startsWith(label),
    )!;

  it('lists each quote with its number, applicant, status and time in queue', async () => {
    const fixture = await render();

    expect(rows(fixture)).toHaveLength(3);

    const first = text(rows(fixture)[0]);
    expect(first).toContain('Q-2026-000101');
    expect(first).toContain('Riley Morgan');
    expect(first).toContain('Referral');
    expect(first).toContain('GA · 2023 Honda Civic');
    expect(first).toContain('In queue 3h');
  });

  it('counts each status on its filter', async () => {
    const fixture = await render();

    expect(text(filterButton(fixture, 'All'))).toBe('All 3');
    expect(text(filterButton(fixture, 'Referrals'))).toBe('Referrals 2');
    expect(text(filterButton(fixture, 'Follow-up'))).toBe('Follow-up 1');
  });

  it('filters to follow-ups only', async () => {
    const fixture = await render();

    filterButton(fixture, 'Follow-up').click();
    await fixture.whenStable();

    expect(rows(fixture).map((row) => row.dataset['quoteNumber'])).toEqual(['Q-2026-000102']);
    expect(filterButton(fixture, 'Follow-up').getAttribute('aria-pressed')).toBe('true');
  });

  it('searches by applicant or quote number, ignoring case', async () => {
    const fixture = await render();
    const search = element(fixture).querySelector<HTMLInputElement>('#uw-queue-search')!;

    search.value = 'ALVAREZ';
    search.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(rows(fixture).map((row) => row.dataset['quoteNumber'])).toEqual(['Q-2026-000102']);

    search.value = '000103';
    search.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(rows(fixture).map((row) => row.dataset['quoteNumber'])).toEqual(['Q-2026-000103']);
  });

  it('does not let Enter in the search box submit the surrounding Web Forms page', async () => {
    // In Underwriting.aspx the whole module sits inside <form runat="server">.
    // Implicit submission would trigger a full postback and reload the page.
    const fixture = await render();
    const search = element(fixture).querySelector<HTMLInputElement>('#uw-queue-search')!;

    const enter = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
    search.dispatchEvent(enter);

    expect(enter.defaultPrevented).toBe(true);
  });

  it('distinguishes "nothing matches" from "nothing to review"', async () => {
    const filtered = await render();
    const search = element(filtered).querySelector<HTMLInputElement>('#uw-queue-search')!;
    search.value = 'nobody';
    search.dispatchEvent(new Event('input'));
    await filtered.whenStable();
    expect(text(element(filtered).querySelector('.uw-empty'))).toBe('No quotes match this filter.');

    const empty = await render([]);
    expect(text(element(empty).querySelector('.uw-empty'))).toBe('No quotes are awaiting review.');
  });

  it('emits the quote id when a quote is clicked', async () => {
    const fixture = await render();
    const selected = vi.fn();
    fixture.componentInstance.quoteSelected.subscribe(selected);

    rows(fixture)[1].click();

    expect(selected).toHaveBeenCalledWith('q2');
  });

  it('marks the selected quote for assistive technology', async () => {
    const fixture = await render(items, 'q2');

    expect(rows(fixture).map((row) => row.getAttribute('aria-current'))).toEqual([null, 'true', null]);
  });

  it('asks the workbench to refresh, and is disabled while it does', async () => {
    const fixture = await render();
    const refresh = vi.fn();
    fixture.componentInstance.refreshRequested.subscribe(refresh);
    const button = element(fixture).querySelector<HTMLButtonElement>('.uw-panel__header button')!;

    button.click();
    expect(refresh).toHaveBeenCalledTimes(1);

    fixture.componentRef.setInput('loading', true);
    await fixture.whenStable();
    expect(button.disabled).toBe(true);
    expect(text(button)).toBe('Refreshing…');
  });
});
