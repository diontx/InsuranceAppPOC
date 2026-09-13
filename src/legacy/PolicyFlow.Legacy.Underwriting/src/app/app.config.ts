import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';

import { correlationIdInterceptor } from './api/correlation-id.interceptor';

/**
 * Application providers.
 *
 * Zoneless is the Angular 22 default, and it matters more than usual here: the
 * module shares a page with the ASP.NET AJAX client library that ScriptManager
 * injects. zone.js patches every timer, promise and event listener on the page —
 * including Microsoft's — so running without it keeps this module from reaching
 * into code it does not own.
 */
export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withInterceptors([correlationIdInterceptor])),
  ],
};
