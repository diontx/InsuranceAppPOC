import { DOCUMENT, InjectionToken, inject } from '@angular/core';

/** The element the module bootstraps into, in Underwriting.aspx and index.html. */
export const HOST_ELEMENT_SELECTOR = 'uw-workbench';

/**
 * Where the PolicyFlow API lives.
 *
 * Read from a `data-api-base` attribute on the host element, which
 * `Underwriting.aspx` renders from the same `PolicyFlow.ApiBaseAddress` appSetting
 * the Web Forms pages use. One setting in `Web.config` therefore points both the
 * legacy code-behind and this module at the same environment, with no rebuild of
 * the JavaScript bundle per environment.
 *
 * Empty means same-origin, which is what the dev server uses: `proxy.conf.json`
 * forwards `/api` to the API container.
 */
export const API_BASE_URL = new InjectionToken<string>('API_BASE_URL', {
  providedIn: 'root',
  factory: () => readApiBaseUrl(inject(DOCUMENT)),
});

export function readApiBaseUrl(document: Document): string {
  const configured = document
    .querySelector(HOST_ELEMENT_SELECTOR)
    ?.getAttribute('data-api-base')
    ?.trim();

  return configured ? configured.replace(/\/+$/, '') : '';
}
