import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { API_BASE_URL } from './api-config';
import { DecisionRequest, UnderwritingQueue, UnderwritingReview } from './underwriting.models';

/**
 * HTTP access to the underwriting endpoints. Transport only: no state, no error
 * handling, no business rules. Errors propagate as `HttpErrorResponse` and are
 * normalised by the store.
 *
 * Note what this calls: the modern `/api/underwriting` endpoints, not the
 * `/legacy` compatibility seam the Web Forms pages use. A new screen in the legacy
 * shell is built against the platform it will outlive the shell on.
 */
@Injectable({ providedIn: 'root' })
export class UnderwritingApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);

  getQueue(): Observable<UnderwritingQueue> {
    return this.http.get<UnderwritingQueue>(`${this.baseUrl}/api/underwriting/queue`);
  }

  getReview(quoteId: string): Observable<UnderwritingReview> {
    return this.http.get<UnderwritingReview>(
      `${this.baseUrl}/api/underwriting/quotes/${encodeURIComponent(quoteId)}`,
    );
  }

  decide(quoteId: string, request: DecisionRequest): Observable<UnderwritingReview> {
    return this.http.post<UnderwritingReview>(
      `${this.baseUrl}/api/underwriting/quotes/${encodeURIComponent(quoteId)}/decision`,
      request,
    );
  }
}
