import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  RESPONSE_INIT,
  afterNextRender,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { BRAND, brandPageTitle } from '@spencer-denim/shared';
import { ContentApi } from '../../core/content/content-api';
import { RobotsMeta } from '../../core/platform/robots-meta';

/** Where the page is: waiting for the press, sending it, done, failed, or opened without a token. */
type Phase = 'ready' | 'sending' | 'done' | 'failed' | 'missing';

/**
 * The newsletter's unsubscribe link (#262, doc 11 P15 step 3): `/unsubscribe?token=...`.
 *
 * **One press, not on arrival.** Mail providers and security scanners open the links in an email to check them, and
 * some run the page's script while they do. A page that unsubscribed on load would unsubscribe people who never
 * asked; a button that a person presses does not. The API is a POST for the same reason.
 *
 * **One answer.** The API says 204 whatever the token was, so "done" is shown for every token — a live one, a used one
 * or a wrong one. Telling the visitor "that link is not valid" would turn this page into a way to test tokens.
 *
 * **The token leaves the address bar once it is used** (`replaceUrl`), so it is not kept in the history or sent on as
 * a referrer by whatever the visitor opens next. A page that is personal is also `noindex` and `no-store`, as the bag
 * is.
 */
@Component({
  selector: 'sd-unsubscribe-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="container unsub">
      <h1 class="unsub__title" tabindex="-1" #heading>
        {{ phase() === 'done' ? 'You have been unsubscribed' : 'Unsubscribe from our newsletter' }}
      </h1>

      @switch (phase()) {
        @case ('missing') {
          <p class="unsub__lead" data-testid="unsubscribe-missing">
            This link is not complete. Open it again from the email, making sure the whole address is copied, or ask
            us to unsubscribe you through the <a class="link" routerLink="/contact">Contact page</a>.
          </p>
        }
        @case ('done') {
          <p class="unsub__lead" data-testid="unsubscribe-done">
            Your address is marked as unsubscribed and is left out of the {{ brand }} newsletter list from now on. An
            email that was already on its way may still arrive.
          </p>
          <p class="unsub__lead">Changed your mind? You can sign up again at the bottom of any page.</p>
          <p><a class="btn btn--secondary" routerLink="/">Continue shopping</a></p>
        }
        @default {
          <p class="unsub__lead">
            Press the button below to stop receiving the {{ brand }} newsletter at the address this link was sent to.
          </p>
          <button
            class="btn btn--primary unsub__button"
            type="button"
            data-testid="unsubscribe-confirm"
            [attr.aria-disabled]="phase() === 'sending' ? 'true' : null"
            (click)="confirm()"
          >
            {{ phase() === 'sending' ? 'Unsubscribing…' : 'Unsubscribe' }}
          </button>
        }
      }

      <!-- Always in the DOM so a screen reader is already watching it when the message arrives. -->
      <p class="unsub__error" role="alert" data-testid="unsubscribe-error">
        {{ phase() === 'failed' ? 'We could not reach the shop just now. Check your connection and press Unsubscribe again.' : '' }}
      </p>
    </div>
  `,
  styles: `
    :host {
      display: block;
    }
    .unsub {
      display: grid;
      justify-items: start;
      gap: var(--space-2);
      max-width: 62ch;
      padding-block: var(--space-8) var(--space-12);
    }
    .unsub__title {
      font-size: var(--text-h1);
      letter-spacing: -0.01em;
    }
    .unsub__title:focus {
      outline: none;
    }
    .unsub__lead {
      color: var(--color-text-muted);
    }
    .unsub__button {
      min-width: 12rem;
    }
    .unsub__error:empty {
      display: none;
    }
    .unsub__error {
      color: var(--color-sale);
      font-weight: var(--weight-medium);
    }
  `,
})
export class UnsubscribePage {
  protected readonly brand = BRAND.name;

  private readonly api = inject(ContentApi);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly heading = viewChild.required<ElementRef<HTMLElement>>('heading');

  /** Read once: after a success the query string is removed, and the page must stay on "done". */
  private readonly token = (inject(ActivatedRoute).snapshot.queryParamMap.get('token') ?? '').trim();

  protected readonly phase = signal<Phase>(this.token ? 'ready' : 'missing');

  constructor() {
    inject(Title).setTitle(brandPageTitle('Unsubscribe'));
    inject(Meta).updateTag({
      name: 'description',
      content: `Stop receiving the ${BRAND.name} newsletter.`,
    });

    // Personal, and nothing a crawler should index; cleared on the way out as the bag page does.
    const robots = inject(RobotsMeta);
    robots.noindex();
    inject(DestroyRef).onDestroy(() => robots.clear());

    const response = inject(RESPONSE_INIT, { optional: true });
    if (response) {
      const headers = new Headers(response.headers ?? {});
      headers.set('Cache-Control', 'no-store, no-cache, private');
      response.headers = headers;
    }
  }

  protected confirm(): void {
    if (this.phase() === 'sending' || !this.token) {
      return;
    }

    this.phase.set('sending');

    this.api.unsubscribe(this.token).subscribe({
      next: () => this.finish(),
      // A 204 has no body, which HttpClient reports as `next` with null; anything else is the network or the server.
      error: () => this.phase.set('failed'),
    });
  }

  private finish(): void {
    this.phase.set('done');

    // The token has done its job: out of the address bar and the history entry.
    void this.router.navigate([], { queryParams: {}, replaceUrl: true });

    // The heading changed to the outcome; move focus to it so a keyboard or screen-reader user is told, rather than
    // left on a button that no longer exists.
    afterNextRender(() => this.heading().nativeElement.focus({ preventScroll: true }), {
      injector: this.injector,
    });
  }
}
