import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, map, of, startWith, timeout } from 'rxjs';

import { PingApi } from '../api/ping-api';

export type ApiStatusState =
  | { kind: 'loading' }
  | { kind: 'online'; version: string }
  | { kind: 'offline' };

/** Maximum wait for the API, so a slow API never blocks server-side rendering. */
export const API_STATUS_TIMEOUT_MS = 3000;

/**
 * Shows whether the API answers `GET /api/ping`. Scaffold wiring check only.
 * Fails to a neutral message (lesson L1).
 */
@Component({
  selector: 'sd-api-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (state().kind) {
      @case ('loading') {
        <p class="api-status" role="status">Checking the API…</p>
      }
      @case ('online') {
        <p class="api-status api-status--online" role="status" data-testid="api-status">
          API connected<span class="api-status__version"> · v{{ version() }}</span>
        </p>
      }
      @default {
        <p class="api-status api-status--offline" role="status" data-testid="api-status">
          The API is not reachable right now.
        </p>
      }
    }
  `,
  styles: `
    :host { display: block; }
    .api-status {
      display: inline-flex;
      align-items: center;
      gap: var(--space-1);
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
    }
    .api-status::before {
      content: '';
      inline-size: var(--space-1);
      block-size: var(--space-1);
      border-radius: var(--radius-round);
      background: var(--color-border-strong);
    }
    .api-status--online::before { background: var(--color-success); }
    .api-status--offline::before { background: var(--color-warning); }
  `,
})
export class ApiStatus {
  private readonly pingApi = inject(PingApi);

  protected readonly state = toSignal(
    this.pingApi.ping().pipe(
      timeout(API_STATUS_TIMEOUT_MS),
      map((response): ApiStatusState =>
        response?.status === 'ok' ? { kind: 'online', version: response.version } : { kind: 'offline' },
      ),
      catchError(() => of<ApiStatusState>({ kind: 'offline' })),
      startWith<ApiStatusState>({ kind: 'loading' }),
    ),
    { requireSync: true },
  );

  protected version(): string {
    const current = this.state();
    return current.kind === 'online' ? current.version : '';
  }
}
