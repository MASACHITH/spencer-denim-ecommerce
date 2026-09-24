import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  AnnouncementDto,
  HomePageDto,
  NewsletterSignupResponse,
} from './content.model';
import { ValidationProblemDetails } from '../catalog/catalog.model';

/**
 * The storefront content API (`/api/content`). One place that knows the endpoint names, so a
 * component never builds a URL.
 *
 * The base URL is supplied by `apiBaseUrlInterceptor`: empty in the browser (same origin behind
 * the reverse proxy), the internal API address during server-side rendering.
 */
@Injectable({ providedIn: 'root' })
export class ContentApi {
  private readonly http = inject(HttpClient);

  /** The homepage, section by section (HOME-01..10). */
  home(): Observable<HomePageDto> {
    return this.http.get<HomePageDto>('/api/content/home');
  }

  /** The announcement strip above the header (GLB-03). */
  announcements(): Observable<readonly AnnouncementDto[]> {
    return this.http.get<readonly AnnouncementDto[]>('/api/content/announcements');
  }

  /** Records a newsletter consent (HOME-10). */
  subscribe(email: string): Observable<NewsletterSignupResponse> {
    return this.http.post<NewsletterSignupResponse>('/api/content/newsletter', { email });
  }
}

/**
 * Turns a failed signup into something the visitor can act on.
 *
 * A 400 carries an RFC 9457 problem keyed by field, and the API's message already says what a
 * usable address looks like, so it is shown as written rather than replaced with an apology.
 * A 429 is the rate limiter, which is a different thing to say: wait, do not retype.
 */
export function newsletterErrorMessage(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) {
    return NEWSLETTER_GENERIC_ERROR;
  }

  if (error.status === 400) {
    return firstValidationMessage(error.error) ?? 'Check the email address and try again.';
  }

  if (error.status === 429) {
    return 'That is a few tries in a short time. Please wait a minute and try again.';
  }

  if (error.status === 0) {
    return 'We could not reach the store. Check your connection and try again.';
  }

  return NEWSLETTER_GENERIC_ERROR;
}

const NEWSLETTER_GENERIC_ERROR =
  'We could not save your email address just now. Please try again in a moment.';

function firstValidationMessage(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) {
    return null;
  }

  const errors = (body as ValidationProblemDetails).errors;
  if (!errors) {
    return null;
  }

  for (const messages of Object.values(errors)) {
    const message = messages?.find((candidate) => typeof candidate === 'string' && candidate.trim());
    if (message) {
      return message;
    }
  }

  return null;
}
