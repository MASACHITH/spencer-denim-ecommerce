import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterRenderEffect,
  computed,
  inject,
  input,
  linkedSignal,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { Router } from '@angular/router';
import { Icon } from '@spencer-denim/shared';

import { NavigationService } from '../../core/navigation/navigation.service';
import { UniqueId } from '../../core/platform/unique-id';
import { RecentSearches } from '../../core/search/recent-searches.service';
import {
  Suggestion,
  SuggestionGroup,
  browseSuggestions,
  categorySuggestions,
  group,
  nextSuggestionId,
  productSuggestions,
  recentSuggestions,
  suggestionAt,
} from '../../core/search/search-suggestions';
import { isSearchable, normaliseTerm, tokenise } from '../../core/search/search-term';
import { SuggestionStore } from '../../core/search/suggestion-store';

/**
 * The search box and its suggestions overlay (GLB-04, SRCH-01).
 *
 * **It is a real combobox.** `role="combobox"` on the field, `role="listbox"` on the popup,
 * `role="group"` for each labelled block and `role="option"` for each row; `aria-expanded` says
 * whether the popup is showing, `aria-controls` names it, and `aria-activedescendant` moves with the
 * highlight while **focus never leaves the field** — which is the whole point of the pattern and the
 * part most implementations get wrong by moving focus into the list. The options are `div`s with no
 * `tabindex`, so there is nothing for focus to move to by accident, and `mousedown` is prevented so
 * a click does not blur the field either.
 *
 * **Escape, and why it stops propagating.** Below 1280 px this box lives in a panel the header opens
 * and closes, and `SiteHeader` closes that panel on Escape anywhere inside it. One Escape must not
 * do both, or dismissing the suggestions would also tear the panel down and throw focus back to the
 * toggle button. So Escape closes the popup, keeps the caret where it is, and stops there; a second
 * Escape reaches the header and closes the panel. It deliberately does **not** clear the field: that
 * gesture is already spoken for.
 *
 * **The popup never opens from focus alone**, only from typing, from `ArrowDown`, or from a click in
 * the field. That is what keeps it out of the class of bug #20 records one component over, where the
 * mega menu re-opened as soon as focus came back to its trigger after Escape: here, focus returning
 * to the field is not an event this component listens to.
 *
 * **Arrow keys wrap** at both ends, `ArrowUp` with nothing highlighted goes to the last option, and
 * `Home`/`End` are deliberately left to the browser so they still move the caret in the text.
 *
 * **Enter chooses the highlighted suggestion** rather than submitting the raw text; with nothing
 * highlighted it submits, which is what a shopper who ignored the list expects.
 */
@Component({
  selector: 'sd-header-search',
  imports: [Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [SuggestionStore],
  host: { '(focusout)': 'onFocusOut($event)' },
  template: `
    <!-- action="/search" method="get" are the no-script, pre-hydration behaviour (#123). This is on every
         page and server rendered, so a shopper can press Enter before the bundle has loaded; with no
         action the browser would submit to the page they are on, ?q=jeans on the homepage, and show
         them the homepage again. Pointed at the results route it lands exactly where onSubmit sends
         them: the search term in the address is the intended behaviour of a search, unlike a name or
         an email address, which is why this form, unlike the others, is allowed to navigate. -->
    <form
      class="header-search"
      role="search"
      action="/search"
      method="get"
      [attr.aria-label]="landmarkName()"
      (submit)="onSubmit($event)"
    >
      <label class="sr-only" [attr.for]="inputId">{{ formLabel() }}</label>
      <sd-icon name="search" size="sm" />
      <input
        class="header-search__input"
        [attr.id]="inputId"
        type="search"
        name="q"
        autocomplete="off"
        autocapitalize="none"
        spellcheck="false"
        role="combobox"
        aria-autocomplete="list"
        [attr.aria-expanded]="isOpen()"
        [attr.aria-controls]="listboxId"
        [attr.aria-activedescendant]="activeId()"
        [attr.placeholder]="placeholder()"
        [attr.value]="initialTerm()"
        #field
        (input)="onInput()"
        (click)="onClick()"
        (keydown)="onKeydown($event)"
      />
      <button class="header-search__submit" type="submit">
        <span class="sr-only">Search</span>
        <sd-icon name="chevron-right" size="sm" />
      </button>
    </form>

    <!-- One live region for the box. It speaks only once a list has settled: announcing "loading"
         on every keystroke would talk over the shopper's own typing. -->
    <p class="sr-only" role="status">{{ announcement() }}</p>

    <div class="suggestions" [hidden]="!isOpen()">
      <div
        class="suggestions__list"
        role="listbox"
        [attr.id]="listboxId"
        [attr.aria-label]="formLabel() + ' suggestions'"
      >
        <!-- The listbox element stays in the document whether or not the popup is showing,
             because aria-controls on the field names it and must name something real. Its
             **contents** do not: this component is in the header of every page, twice below
             1280 px, so rendering the browse block server-side would ship a few kilobytes of
             hidden rows with every page view — which is the listing's "every control twice in the
             server HTML" lesson in a smaller size. -->
        @if (isOpen()) {
          @for (block of groups(); track block.id) {
          <div class="suggestions__group" role="group" [attr.aria-labelledby]="block.headingId">
            <p class="suggestions__heading" [attr.id]="block.headingId">{{ block.label }}</p>
            @for (option of block.options; track option.id) {
              <!-- An option in an aria-activedescendant combobox must NOT be focusable and must
                   NOT carry its own key handlers: focus stays in the text field, and the field's
                   keydown is what moves the highlight, chooses with Enter and closes with Escape.
                   The two template rules disabled below are written for the other half of the
                   pattern — a widget that moves focus into its list — and following them here would
                   put eight extra stops in the tab order and take the caret out of the box the
                   shopper is typing in. tabindex="-1" is what the pattern does ask for, and it adds
                   no tab stop. The keyboard path is driven for real in e2e/search.e2e.ts. -->
              <!-- eslint-disable-next-line @angular-eslint/template/click-events-have-key-events -->
              <div
                class="suggestions__option"
                role="option"
                tabindex="-1"
                [attr.id]="option.id"
                [attr.aria-selected]="option.id === activeId()"
                [class.suggestions__option--active]="option.id === activeId()"
                (mousedown)="keepFocus($event)"
                (mousemove)="highlight(option.id)"
                (click)="choose(option)"
              >
                @if (option.imageUrl) {
                  <img
                    class="suggestions__thumb"
                    [src]="option.imageUrl"
                    [attr.srcset]="option.imageSrcSet"
                    sizes="40px"
                    width="40"
                    height="53"
                    alt=""
                  />
                } @else {
                  <span class="suggestions__glyph" aria-hidden="true">
                    <sd-icon [name]="option.kind === 'recent' ? 'search' : 'chevron-right'" size="sm" />
                  </span>
                }
                <span class="suggestions__text">
                  <span class="suggestions__label">{{ option.label }}</span>
                  @if (option.detail) {
                    <span class="suggestions__detail">{{ option.detail }}</span>
                  }
                </span>
              </div>
            }
          </div>
          }
        }
      </div>

      <!-- Outside the listbox, both of them: a listbox may hold options and groups, and nothing
           else. The live region above is what announces these to a screen reader. -->
      @if (noMatches()) {
        <p class="suggestions__note">
          Nothing matches “{{ term() }}” yet. Press Enter to search anyway.
        </p>
      }
      @if (suggestionsFailed()) {
        <p class="suggestions__note">Suggestions are not available just now. Press Enter to search.</p>
      }

      <!-- A button is not an option either. It is also the shopper's own control over their
           search history. -->
      @if (showsRecent()) {
        <div class="suggestions__footer">
          <button class="suggestions__clear" type="button" (click)="clearRecent()">
            Clear recent searches
          </button>
        </div>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
      /* The popup is positioned against this, and has to paint over the page below it. Inside the
         header that is already settled by the header's own stacking context; on the search results
         page, where this component also appears, it is not. */
      position: relative;
      z-index: 1;
    }
    .header-search {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      min-height: var(--tap);
      padding-inline: var(--space-1);
      border-bottom: 1px solid var(--color-border-strong);
      color: var(--color-text-muted);
    }
    .header-search__input {
      flex: 1 1 auto;
      width: 100%;
      min-width: 0;
      min-height: var(--tap);
      border: 0;
      background: transparent;
      color: var(--color-text);
      font-size: var(--text-small);
    }
    .header-search__input::placeholder {
      color: var(--color-text-muted);
      opacity: 1;
    }
    .header-search__submit {
      display: inline-grid;
      place-items: center;
      width: var(--tap);
      height: var(--tap);
      flex: none;
      padding: 0;
      border: 0;
      background: transparent;
      color: var(--color-text);
      cursor: pointer;
    }

    /* A dropdown anchored to the field at every width, rather than a full-screen sheet on phones.
       Measured choice: the header is 170 px tall at 320 px and sticky, so a full-screen sheet would
       need a scroll lock and a focus trap inside it to be safe, and this list is eight rows at most.
       It scrolls instead, capped so it can never run past the fold. */
    .suggestions {
      position: absolute;
      inset-inline: 0;
      top: calc(100% + var(--space-0-5));
      z-index: 60;
      background: var(--color-surface);
      border: 1px solid var(--color-border-strong);
      box-shadow: none;
    }
    .suggestions__list {
      max-height: min(60vh, 420px);
      overflow-y: auto;
      overscroll-behavior: contain;
    }
    .suggestions__heading {
      padding: var(--space-1) var(--space-1-5) var(--space-0-5);
      color: var(--color-text-muted);
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
    }
    .suggestions__group + .suggestions__group {
      border-top: 1px solid var(--color-border);
    }
    .suggestions__option {
      display: flex;
      align-items: center;
      gap: var(--space-1-5);
      /* The row is the tap target, so it carries the 44 px minimum itself. */
      min-height: var(--tap);
      padding: var(--space-0-5) var(--space-1-5);
      cursor: pointer;
    }
    .suggestions__option--active,
    .suggestions__option:hover {
      background: var(--color-bg-alt);
    }
    .suggestions__thumb {
      flex: none;
      width: 40px;
      height: 53px;
      object-fit: cover;
      background: var(--color-bg-alt);
    }
    .suggestions__glyph {
      display: inline-grid;
      place-items: center;
      flex: none;
      width: 40px;
      color: var(--color-text-muted);
    }
    .suggestions__text {
      display: grid;
      gap: 2px;
      min-width: 0;
    }
    .suggestions__label {
      font-size: var(--text-small);
      /* A product name has no spaces to break at when it is one long word, and the narrowest
         screen this has to survive is 320 px. */
      overflow-wrap: anywhere;
    }
    .suggestions__detail {
      color: var(--color-text-muted);
      font-size: var(--text-label);
      overflow-wrap: anywhere;
    }
    .suggestions__note {
      padding: var(--space-1-5);
      color: var(--color-text-muted);
      font-size: var(--text-small);
    }
    .suggestions__footer {
      border-top: 1px solid var(--color-border);
    }
    .suggestions__clear {
      display: block;
      width: 100%;
      min-height: var(--tap);
      padding: var(--space-1) var(--space-1-5);
      border: 0;
      background: transparent;
      color: var(--color-text);
      font-size: var(--text-small);
      text-align: start;
      cursor: pointer;
    }
    .suggestions__clear:hover {
      background: var(--color-bg-alt);
    }
  `,
})
export class HeaderSearch {
  readonly placeholder = input('Search');

  /**
   * Names the `role="search"` landmark and labels the field. Two search landmarks on one page — the
   * header's and the results page's — need different names, or a screen reader's landmark list reads
   * the same entry twice.
   */
  readonly formLabel = input('Search products');

  /**
   * The term the field starts with, for the box on the search results page.
   *
   * Bound as an **attribute** so the server-rendered HTML carries it: a shopper who opens a shared
   * `/search?q=...` link on a phone has to see what was searched for in the box, and a property
   * binding is not serialised. Once the shopper edits the field the browser stops following the
   * attribute, which is why {@link seedField} also sets the property when the term changes.
   */
  readonly initialTerm = input('');

  /**
   * The name of the `role="search"` landmark, when it has to differ from the field's own label.
   *
   * `SiteHeader` renders this component **twice** — the inline box from 1280 px and the one in the
   * panel below it — and the results page renders a third. Exactly one of the header's two is ever
   * visible, so in effect a screen reader never meets two landmarks with one name; but that is true
   * by stylesheet rather than by naming, and a landmark list is the wrong place to depend on a media
   * query. The field label stays "Search products" either way, because that is what the field is.
   */
  readonly landmarkLabel = input('');

  protected readonly inputId = inject(UniqueId).for('sd-search');
  protected readonly listboxId = `${this.inputId}-listbox`;

  private readonly field = viewChild.required<ElementRef<HTMLInputElement>>('field');
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly document = inject(DOCUMENT);
  private readonly router = inject(Router);
  private readonly navigation = inject(NavigationService);
  private readonly recentSearches = inject(RecentSearches);
  private readonly suggestions = inject(SuggestionStore);

  /** What is in the field, as the component knows it. Follows {@link initialTerm} when that moves. */
  private readonly raw = linkedSignal(() => this.initialTerm());

  private readonly opened = signal(false);
  private readonly highlighted = signal<string | null>(null);

  /** What was last seeded from {@link initialTerm}, so the field is not written on every render. */
  private seeded: string | null = null;

  protected readonly term = computed(() => normaliseTerm(this.raw()));

  /** The landmark's name: its own, or the field's when nothing more specific was given. */
  protected readonly landmarkName = computed(() => this.landmarkLabel() || this.formLabel());

  /**
   * The blocks in the popup.
   *
   * With a usable term: the products the API matched and the categories the cached tree matched.
   * Without one: this browser's recent searches, or — when there are none — ways into the catalogue.
   *
   * **There is no "popular searches" block, and that is deliberate.** Nothing in this system records
   * what anyone has searched for; see `browseSuggestions` for the whole reason and what was built
   * instead.
   */
  protected readonly groups = computed<readonly SuggestionGroup[]>(() => {
    const term = this.term();
    const prefix = this.inputId;

    if (isSearchable(term)) {
      return [
        ...group(
          'products',
          prefix,
          'Products',
          productSuggestions(this.suggestions.products(), prefix),
        ),
        ...group(
          'categories',
          prefix,
          'Categories',
          categorySuggestions(this.navigation.departments(), tokenise(term), prefix),
        ),
      ];
    }

    const recent = recentSuggestions(this.recentSearches.recent(), prefix);
    if (recent.length > 0) {
      return group('recent', prefix, 'Recent searches', recent);
    }

    return group('browse', prefix, 'Browse the shop', browseSuggestions(this.navigation.departments(), prefix));
  });

  /** True when the shopper is looking at their own history, which is what offers "Clear". */
  protected readonly showsRecent = computed(
    () => !isSearchable(this.term()) && this.recentSearches.recent().length > 0,
  );

  /** A term was searched for, the answer came back, and there is nothing to show. */
  protected readonly noMatches = computed(
    () =>
      isSearchable(this.term()) &&
      this.suggestions.status() === 'ready' &&
      this.suggestions.searchedTerm() === this.term() &&
      this.groups().length === 0,
  );

  protected readonly suggestionsFailed = computed(
    () => isSearchable(this.term()) && this.suggestions.status() === 'failed',
  );

  /** Whether the popup has anything in it. `aria-expanded` must not claim a popup that is empty. */
  private readonly hasContent = computed(
    () => this.groups().length > 0 || this.noMatches() || this.suggestionsFailed(),
  );

  protected readonly isOpen = computed(() => this.opened() && this.hasContent());

  /**
   * The highlighted option's id, or null.
   *
   * Derived rather than stored, so an id can never outlive the option it names: the list changes
   * under the shopper on every keystroke, and `aria-activedescendant` pointing at an element that is
   * no longer in the document is a screen reader saying nothing at all.
   */
  protected readonly activeId = computed(() =>
    this.isOpen() && suggestionAt(this.groups(), this.highlighted()) !== null
      ? this.highlighted()
      : null,
  );

  /** What a screen reader hears when a list settles (SRCH-01, keyboard navigation). */
  protected readonly announcement = computed(() => {
    if (!this.isOpen()) {
      return '';
    }

    if (this.suggestionsFailed()) {
      return 'Suggestions are not available.';
    }

    const count = this.groups().reduce((total, block) => total + block.options.length, 0);
    if (!isSearchable(this.term())) {
      return '';
    }

    if (count === 0) {
      return `No suggestions for ${this.term()}.`;
    }

    return `${count} ${count === 1 ? 'suggestion' : 'suggestions'} for ${this.term()}.`;
  });

  constructor() {
    this.seedField();
    this.keepHighlightInView();
  }

  /** Moves the caret into the field: used when the collapsed search panel opens. */
  focusField(): void {
    this.field().nativeElement.focus();
  }

  protected onInput(): void {
    const value = this.field().nativeElement.value;
    this.raw.set(value);
    this.suggestions.suggestFor(value);
    this.highlighted.set(null);
    this.opened.set(true);
  }

  /** A click in the field opens the popup; it never closes it — a caret move is not a dismissal. */
  protected onClick(): void {
    this.openPopup();
  }

  protected onKeydown(event: KeyboardEvent): void {
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.step(1);
        return;
      case 'ArrowUp':
        event.preventDefault();
        this.step(-1);
        return;
      case 'Enter': {
        const option = suggestionAt(this.groups(), this.activeId());
        if (option) {
          // Not the raw text: the shopper chose a suggestion, and submitting the half-typed word
          // they chose it instead of is the single most irritating way to get this wrong.
          event.preventDefault();
          this.choose(option);
        }
        return;
      }
      case 'Escape':
        // **Always prevented, open or not**, and that is the whole of the fix for the bug this
        // component's own docblock claimed to be the opposite of.
        //
        // Chrome gives `input[type=search]` a native Escape-clear as the keydown's default action.
        // Preventing it only while the popup was open meant the *second* Escape — the one whose only
        // job is to dismiss the panel — emptied the field, and the clear dispatches an `input` event,
        // which `onInput` answers by opening the popup again. So a shopper who dismissed the
        // suggestions and then dismissed the panel lost their typing and got the suggestions back:
        // #20's shape, here. The keystroke is ours either way, so it is prevented either way.
        event.preventDefault();

        if (this.isOpen()) {
          // Stops here, so the header's own Escape handler does not tear the search panel down in
          // the same keystroke. A second Escape finds the popup closed and is allowed through.
          event.stopPropagation();
          this.close();
        }
        return;
      case 'Tab':
        this.close();
        return;
      default:
        return;
    }
  }

  protected onSubmit(event: Event): void {
    event.preventDefault();
    this.run(this.field().nativeElement.value);
  }

  protected highlight(id: string): void {
    this.highlighted.set(id);
  }

  /** Keeps the caret in the field when an option is clicked: a `div` would otherwise blur it. */
  protected keepFocus(event: MouseEvent): void {
    event.preventDefault();
  }

  protected choose(option: Suggestion): void {
    if (option.kind === 'recent' && option.term) {
      this.run(option.term);
      return;
    }

    if (option.path) {
      this.close();
      void this.router.navigateByUrl(option.path);
    }
  }

  protected clearRecent(): void {
    this.recentSearches.clear();
    this.highlighted.set(null);
    this.focusField();
  }

  protected onFocusOut(event: FocusEvent): void {
    const next = event.relatedTarget;
    if (next instanceof Node && this.host.nativeElement.contains(next)) {
      return;
    }

    this.close();
  }

  /** Runs a search: records it, shows the normalised term, and goes to the results page. */
  private run(raw: string): void {
    const term = normaliseTerm(raw);
    if (!isSearchable(term)) {
      // Nothing to search for. The field keeps the caret so the shopper can carry on typing.
      this.focusField();
      return;
    }

    this.close();
    this.suggestions.reset();
    this.recentSearches.remember(term);

    const element = this.field().nativeElement;
    element.value = term;
    this.raw.set(term);

    void this.router.navigate(['/search'], { queryParams: { q: term } });
  }

  private openPopup(): void {
    if (!this.opened()) {
      this.suggestions.suggestFor(this.raw());
      this.opened.set(true);
    }
  }

  private step(delta: number): void {
    const wasOpen = this.isOpen();
    this.openPopup();

    this.highlighted.set(
      nextSuggestionId(this.groups(), wasOpen ? this.activeId() : null, delta),
    );
  }

  private close(): void {
    this.opened.set(false);
    this.highlighted.set(null);
  }

  /**
   * Writes {@link initialTerm} into the field when it changes.
   *
   * The attribute binding covers the server render and the first paint; this covers a client-side
   * navigation to a different search, after which the field is "dirty" and no longer follows the
   * attribute. It leaves a field the shopper is typing in alone.
   */
  private seedField(): void {
    afterRenderEffect(() => {
      const seeded = this.initialTerm();
      untracked(() => {
        if (seeded === this.seeded) {
          return;
        }

        this.seeded = seeded;
        const element = this.field().nativeElement;
        if (seeded === '' || element === this.document.activeElement) {
          return;
        }

        element.value = seeded;
        this.raw.set(seeded);
      });
    });
  }

  /** Scrolls the highlighted option into view: the list scrolls, and focus never moves to it. */
  private keepHighlightInView(): void {
    afterRenderEffect(() => {
      const id = this.activeId();
      untracked(() => {
        if (id === null) {
          return;
        }

        const element = this.host.nativeElement.querySelector(`#${id}`);
        element?.scrollIntoView({ block: 'nearest' });
      });
    });
  }
}
