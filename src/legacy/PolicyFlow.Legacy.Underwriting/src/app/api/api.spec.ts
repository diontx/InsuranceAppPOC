import {
  HttpClient,
  HttpErrorResponse,
  HttpHeaders,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { API_BASE_URL, readApiBaseUrl } from './api-config';
import { toApiProblem } from './api-problem';
import { CORRELATION_ID_HEADER, correlationIdInterceptor } from './correlation-id.interceptor';
import { UnderwritingApiService } from './underwriting-api.service';

describe('UnderwritingApiService', () => {
  function setup(baseUrl = '') {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: API_BASE_URL, useValue: baseUrl },
      ],
    });

    return {
      api: TestBed.inject(UnderwritingApiService),
      http: TestBed.inject(HttpTestingController),
    };
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('reads the queue from the modern API, not the legacy seam', () => {
    const { api, http } = setup();

    api.getQueue().subscribe();

    http.expectOne({ method: 'GET', url: '/api/underwriting/queue' }).flush({ items: [] });
  });

  it('prefixes the API base address the host page configured', () => {
    const { api, http } = setup('http://localhost:8081');

    api.getQueue().subscribe();

    http.expectOne('http://localhost:8081/api/underwriting/queue').flush({ items: [] });
  });

  it('encodes the quote id into the review URL', () => {
    const { api, http } = setup();

    api.getReview('a/b').subscribe();

    http.expectOne('/api/underwriting/quotes/a%2Fb').flush({});
  });

  it('posts the decision and note as the body, with no underwriter identity', () => {
    // Who decided comes from the token on the server. A client that sent it would
    // be sending something the server must ignore.
    const { api, http } = setup();

    api.decide('quote-1', { decision: 'FollowUp', note: 'Upload proof of prior insurance.' }).subscribe();

    const request = http.expectOne({ method: 'POST', url: '/api/underwriting/quotes/quote-1/decision' });
    expect(request.request.body).toEqual({ decision: 'FollowUp', note: 'Upload proof of prior insurance.' });
    request.flush({});
  });
});

describe('readApiBaseUrl', () => {
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('reads data-api-base from the host element and trims trailing slashes', () => {
    document.body.innerHTML = '<uw-workbench data-api-base="http://localhost:8081/"></uw-workbench>';

    expect(readApiBaseUrl(document)).toBe('http://localhost:8081');
  });

  it('falls back to same-origin when the host page does not configure one', () => {
    document.body.innerHTML = '<uw-workbench></uw-workbench>';

    expect(readApiBaseUrl(document)).toBe('');
  });
});

describe('correlationIdInterceptor', () => {
  let http: HttpClient;
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([correlationIdInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  it('gives every request its own correlation id', () => {
    http.get('/api/one').subscribe();
    http.get('/api/two').subscribe();

    const first = controller.expectOne('/api/one');
    const second = controller.expectOne('/api/two');
    const firstId = first.request.headers.get(CORRELATION_ID_HEADER);

    expect(firstId).toBeTruthy();
    expect(firstId).not.toBe(second.request.headers.get(CORRELATION_ID_HEADER));

    first.flush({});
    second.flush({});
  });

  it('keeps an id the caller already set', () => {
    http.get('/api/one', { headers: { [CORRELATION_ID_HEADER]: 'caller-id' } }).subscribe();

    const request = controller.expectOne('/api/one');
    expect(request.request.headers.get(CORRELATION_ID_HEADER)).toBe('caller-id');
    request.flush({});
  });
});

describe('toApiProblem', () => {
  it('flattens validation errors onto camelCase field names', () => {
    const problem = toApiProblem(
      new HttpErrorResponse({
        status: 400,
        error: {
          title: 'One or more validation errors occurred',
          errors: { Note: ['Say what information is needed from the applicant.'] },
          correlationId: 'corr-400',
        },
      }),
    );

    expect(problem.status).toBe(400);
    expect(problem.fieldErrors['note']).toBe('Say what information is needed from the applicant.');
    expect(problem.correlationId).toBe('corr-400');
  });

  it('keeps the business-rule detail from a 422', () => {
    const problem = toApiProblem(
      new HttpErrorResponse({
        status: 422,
        error: { title: 'Business rule violation', detail: 'No carrier on the panel will write quote Q-1.' },
      }),
    );

    expect(problem.title).toBe('Business rule violation');
    expect(problem.detail).toContain('No carrier');
    expect(problem.fieldErrors).toEqual({});
  });

  it('describes an unreachable API without pretending the server answered', () => {
    const problem = toApiProblem(new HttpErrorResponse({ status: 0, statusText: 'Unknown Error' }));

    expect(problem.status).toBe(0);
    expect(problem.title).toBe('Cannot reach the PolicyFlow API');
  });

  it('does not let a non-JSON error page hide the status code', () => {
    const problem = toApiProblem(
      new HttpErrorResponse({ status: 502, statusText: 'Bad Gateway', error: '<html>proxy error</html>' }),
    );

    expect(problem.status).toBe(502);
    expect(problem.title).toBe('Bad Gateway');
  });

  it('falls back to the response header for the correlation id', () => {
    const problem = toApiProblem(
      new HttpErrorResponse({
        status: 500,
        error: { title: 'An unexpected error occurred' },
        headers: new HttpHeaders({ 'X-Correlation-Id': 'corr-header' }),
      }),
    );

    expect(problem.correlationId).toBe('corr-header');
  });
});
