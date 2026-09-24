import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { ContentApi, newsletterErrorMessage } from '../../../core/content/content-api';
import { HomeSectionDto } from '../../../core/content/content.model';

/** What the form is doing, which is what it renders around. */
type SignupPhase = 'idle' | 'sending' | 'done' | 'error';

/**
 * HOME-10: the newsletter signup.
 *
 * <b>It really subscribes.</b> `marketing.NewsletterSubscribers` is specified in doc 08 §3.6, so
 * the minimal write endpoint was built rather than the form being disabled with an explanation, and
 * rather than — the worst of the three — a form that accepts an address and quietly drops it. A
 * shopper who types their address here is on the list.
 *
 * The API is the judge of an address, not this component: `type="email"` and `required` give the
 * browser its own hint, and everything else is the sentence the server sent back, so the form
 * cannot accept something the database will refuse or refuse something it would have taken.
 *
 * The confirmation is deliberately the same whether the address was new or already on the list. A
 * form that said "you are already subscribed" would be a way to test whether someone shops here.
 */
@Component({
  selector: 'sd-newsletter-signup',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="signup" [attr.aria-labelledby]="headingId()">
      <div class="container signup__inner">
        <div class="signup__copy">
          <h2 class="signup__title" [id]="headingId()">{{ heading() }}</h2>
          @if (section().subtitle; as standfirst) {
            <p class="signup__lead">{{ standfirst }}</p>
          }
        </div>

        @if (phase() === 'done') {
          <p class="signup__done" role="status" data-testid="newsletter-done">
            {{ message() }}
          </p>
        } @else {
          <form class="signup__form" novalidate (submit)="onSubmit($event)">
            <div class="signup__field">
              <label class="signup__label" [attr.for]="emailId()">Email address</label>
              <input
                [id]="emailId()"
                class="signup__input"
                data-testid="newsletter-email"
                type="email"
                name="email"
                autocomplete="email"
                inputmode="email"
                spellcheck="false"
                required
                [value]="email()"
                [disabled]="phase() === 'sending'"
                [attr.aria-invalid]="phase() === 'error' ? 'true' : null"
                [attr.aria-describedby]="phase() === 'error' ? errorId() : hintId()"
                (input)="onInput($event)"
              />
            </div>

            <button
              class="btn btn--primary signup__submit"
              type="submit"
              data-testid="newsletter-submit"
              [disabled]="phase() === 'sending'"
            >
              {{ phase() === 'sending' ? 'Signing up…' : 'Sign up' }}
            </button>

            <!-- TODO(GLB-10): link "privacy policy" to the content page once it is published. -->
            <p class="signup__hint" [id]="hintId()">
              We use your address for Spencer Denim emails only, and record your IP address as
              proof of consent. Unsubscribe from any of them.
            </p>

            <!-- Always in the DOM so a screen reader is watching it before the message arrives;
                 empty until there is something to say. -->
            <p class="signup__error" [id]="errorId()" role="alert" data-testid="newsletter-error">
              {{ phase() === 'error' ? message() : '' }}
            </p>
          </form>
        }
      </div>
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    .signup {
      padding-block: var(--space-6);
      border-top: 1px solid var(--color-border);
    }
    .signup__inner {
      display: grid;
      gap: var(--space-3);
      align-items: start;
    }
    @media (min-width: 1024px) {
      .signup__inner {
        grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
        gap: var(--space-6);
      }
    }
    .signup__copy {
      display: grid;
      gap: var(--space-1);
    }
    .signup__title {
      font-size: var(--text-h2);
    }
    .signup__lead {
      color: var(--color-text-muted);
      max-width: 48ch;
    }
    .signup__form {
      display: grid;
      grid-template-columns: minmax(0, 1fr);
      gap: var(--space-1-5);
    }
    @media (min-width: 480px) {
      .signup__form {
        grid-template-columns: minmax(0, 1fr) auto;
      }
      .signup__hint,
      .signup__error {
        grid-column: 1 / -1;
      }
    }
    .signup__field {
      display: grid;
      gap: var(--space-0-5);
      min-width: 0;
    }
    .signup__label {
      font-size: var(--text-label);
      font-weight: var(--weight-medium);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
    }
    .signup__input {
      width: 100%;
      min-height: 48px;
      padding: var(--space-1) var(--space-1-5);
      border: 1px solid var(--color-border-strong);
      background: var(--color-surface);
    }
    .signup__input[aria-invalid='true'] {
      border-color: var(--color-sale);
      border-width: 2px;
    }
    .signup__submit {
      align-self: end;
    }
    .signup__hint,
    .signup__error {
      font-size: var(--text-small);
      line-height: 1.5;
      margin: 0;
    }
    .signup__hint {
      color: var(--color-text-muted);
    }
    .signup__error:not(:empty) {
      color: var(--color-sale);
      font-weight: var(--weight-medium);
    }
    .signup__done {
      display: flex;
      align-items: center;
      min-height: 48px;
      padding: var(--space-1-5) var(--space-2);
      border: 1px solid var(--color-success);
      color: var(--color-text);
      background: var(--color-surface);
    }
  `,
})
export class NewsletterSignup {
  private readonly api = inject(ContentApi);
  private readonly destroyRef = inject(DestroyRef);

  readonly section = input.required<HomeSectionDto>();

  private readonly state = signal<SignupPhase>('idle');
  private readonly value = signal('');
  private readonly answer = signal('');

  protected readonly phase = this.state.asReadonly();
  protected readonly email = this.value.asReadonly();
  protected readonly message = this.answer.asReadonly();

  protected readonly headingId = computed(() => `home-section-${this.section().id}`);

  /**
   * Scoped per section, like the hint and the error below.
   *
   * A bare `newsletter-email` collided with the footer's field, which is on every page including
   * this one: an `id` may appear once in a document, so the footer's label pointed at *this*
   * input. The footer field was left with no accessible name at all, and clicking its label
   * jumped focus up the page. axe 4.10 retired `duplicate-id`, so nothing was going to catch it.
   */
  protected readonly emailId = computed(() => `newsletter-email-${this.section().id}`);
  protected readonly hintId = computed(() => `newsletter-hint-${this.section().id}`);
  protected readonly errorId = computed(() => `newsletter-error-${this.section().id}`);

  protected readonly heading = computed(() => this.section().title?.trim() || 'Newsletter');

  protected onInput(event: Event): void {
    this.value.set((event.target as HTMLInputElement).value);

    // Clearing the error as soon as the visitor edits stops the page arguing with someone who is
    // already fixing it.
    if (this.state() === 'error') {
      this.state.set('idle');
      this.answer.set('');
    }
  }

  protected onSubmit(event: Event): void {
    event.preventDefault();

    if (this.state() === 'sending') {
      return;
    }

    const email = this.value().trim();

    // The one thing checked here rather than on the server: an empty box is not a request, and a
    // round trip to be told so is a round trip wasted.
    if (email.length === 0) {
      this.fail('Enter your email address to sign up.');
      return;
    }

    this.state.set('sending');
    this.answer.set('');

    this.api
      .subscribe(email)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          // The server's own sentence, not one invented here: it is the same whether the address
          // was new or already on the list, and only the server knows which.
          this.answer.set(response?.message?.trim() || 'Thank you. Your email address is on the list.');
          this.state.set('done');
        },
        error: (failure: unknown) => this.fail(newsletterErrorMessage(failure)),
      });
  }

  private fail(message: string): void {
    this.answer.set(message);
    this.state.set('error');
  }
}
