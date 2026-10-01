import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { SafeLocalStorage } from '@spencer-denim/shared';

import { HeaderSearch } from './header-search';
import { ProductCardDto, ProductSearchResponse } from '../../core/catalog/catalog.model';
import { CategoryNodeDto, DepartmentDto } from '../../core/navigation/catalog-departments.model';
import { NavigationService } from '../../core/navigation/navigation.service';
import { RECENT_SEARCHES_KEY } from '../../core/search/recent-searches.service';
import { SUGGESTION_DEBOUNCE_MS } from '../../core/search/suggestion-store';

/** Long enough for the debounce to have fired, short enough not to slow the suite down. */
const PAST_THE_DEBOUNCE_MS = SUGGESTION_DEBOUNCE_MS + 60;

class StorageStub {
  readonly entries = new Map<string, string>();

  read(key: string): string | null {
    return this.entries.get(key) ?? null;
  }

  write(key: string, value: string): boolean {
    this.entries.set(key, value);
    return true;
  }

  remove(key: string): boolean {
    return this.entries.delete(key);
  }
}

function node(id: number, name: string, slug: string): CategoryNodeDto {
  return {
    id,
    name,
    slug,
    depth: 1,
    description: null,
    imageUrl: null,
    imageSrcSet: null,
    showInMegaMenu: true,
    megaMenuColumn: null,
    ageGroupSlug: null,
    children: [],
  };
}

const DEPARTMENTS: readonly DepartmentDto[] = [
  {
    id: 1,
    name: 'Women',
    slug: 'women',
    description: null,
    heroImageUrl: null,
    heroImageSrcSet: null,
    seoTitle: null,
    seoDescription: null,
    categories: [node(11, 'Linen Shirts', 'linen-shirts'), node(12, 'Jeans', 'jeans')],
  },
  {
    id: 2,
    name: 'Men',
    slug: 'men',
    description: null,
    heroImageUrl: null,
    heroImageSrcSet: null,
    seoTitle: null,
    seoDescription: null,
    categories: [],
  },
];

function card(id: number, name: string): ProductCardDto {
  return {
    id,
    productCode: `SD-${id}`,
    name,
    slug: `product-${id}`,
    departmentName: 'Women',
    departmentSlug: 'women',
    categoryName: 'Shirts',
    categorySlug: 'shirts',
    imageUrl: null,
    imageSrcSet: null,
    hoverImageUrl: null,
    hoverImageSrcSet: null,
    imageAltText: null,
    price: { current: 5990, original: 5990, discountPercent: 0, hasDiscount: false, currency: 'LKR' },
    rating: { average: null, count: 0 },
    badge: null,
    stockStatus: 'InStock',
    isInStock: true,
    colours: [],
  };
}

function response(cards: readonly ProductCardDto[], term: string): ProductSearchResponse {
  return {
    term,
    tokens: term.toLowerCase().split(' '),
    results: { items: cards, page: 1, pageSize: 6, totalCount: cards.length },
  };
}

/**
 * A wrapper that counts Escape keystrokes reaching it, which is how the one thing a unit test can
 * check about `stopPropagation` gets checked: `SiteHeader` closes the whole search panel on Escape
 * anywhere inside it, and the first Escape must not get that far.
 */
@Component({
  imports: [HeaderSearch],
  changeDetection: ChangeDetectionStrategy.OnPush,
  // On the host rather than on a wrapping div, which is where `SiteHeader` has it too — and a div
  // with a key handler is a control that cannot be reached by keyboard, which the template rules
  // rightly refuse.
  host: { '(keydown.escape)': 'escapes.set(escapes() + 1)' },
  template: `<sd-header-search [initialTerm]="seed()" [landmarkLabel]="landmark()" />`,
})
class Host {
  readonly escapes = signal(0);
  readonly seed = signal('');
  readonly landmark = signal('');
}

describe('HeaderSearch (SRCH-01)', () => {
  let fixture: ComponentFixture<Host>;
  let http: HttpTestingController;
  let storage: StorageStub;
  let navigate: ReturnType<typeof vi.spyOn>;
  let navigateByUrl: ReturnType<typeof vi.spyOn>;

  function setUp(stored: readonly string[] = []): void {
    storage = new StorageStub();
    if (stored.length > 0) {
      storage.entries.set(RECENT_SEARCHES_KEY, JSON.stringify(stored));
    }

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: SafeLocalStorage, useValue: storage },
      ],
    });

    http = TestBed.inject(HttpTestingController);
    // The menu tree is what the category suggestions are matched against, so it is answered before
    // anything is typed; no suggestion ever costs a second request for it.
    TestBed.inject(NavigationService);
    http.expectOne('/api/catalog/departments').flush(DEPARTMENTS);

    const router = TestBed.inject(Router);
    navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    navigateByUrl = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
  }

  afterEach(() => {
    http.verify();
  });

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function input(): HTMLInputElement {
    return root().querySelector('input[role="combobox"]')!;
  }

  function options(): HTMLElement[] {
    return [...root().querySelectorAll<HTMLElement>('[role="option"]')];
  }

  /**
   * The option labels only.
   *
   * Not `textContent`: a product option also renders its category path, and jsdom joins the two with
   * no whitespace between them, so `textContent` is "Linen ShirtWomen > Shirts". An
   * `expect(labels).not.toContain('Linen Shirt')` against that array would be a check that can never
   * fail, which is the one thing a test must not be.
   */
  function labels(): string[] {
    return [...root().querySelectorAll('.suggestions__label')].map(
      (element) => element.textContent?.trim() ?? '',
    );
  }

  function headings(): string[] {
    return [...root().querySelectorAll('.suggestions__heading')].map(
      (heading) => heading.textContent?.trim() ?? '',
    );
  }

  function announcement(): string {
    return root().querySelector('[role="status"]')?.textContent?.trim() ?? '';
  }

  function press(key: string): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
    input().dispatchEvent(event);
    fixture.detectChanges();
    return event;
  }

  function type(value: string): void {
    input().value = value;
    input().dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();
  }

  /** Waits past the debounce and renders, so an outstanding request is in flight afterwards. */
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, PAST_THE_DEBOUNCE_MS));
    fixture.detectChanges();
  }

  /** The one outstanding suggestion request, whatever its query string. */
  function searchRequest(): TestRequest {
    return http.expectOne((request) => request.url === '/api/catalog/search');
  }

  function searchRequests(): TestRequest[] {
    return http.match((request) => request.url === '/api/catalog/search');
  }

  // ------------------------------------------------------------------ the combobox contract

  it('is a combobox with a collapsed popup before anything is typed', () => {
    setUp();

    expect(input().getAttribute('role')).toBe('combobox');
    expect(input().getAttribute('aria-expanded')).toBe('false');
    expect(input().getAttribute('aria-autocomplete')).toBe('list');
    expect(input().hasAttribute('aria-activedescendant')).toBe(false);
  });

  it('points aria-controls at the listbox that is really in the document', () => {
    setUp();

    const controls = input().getAttribute('aria-controls');

    expect(controls).toBeTruthy();
    expect(root().querySelector(`#${controls}`)?.getAttribute('role')).toBe('listbox');
  });

  it('labels its own search landmark, and labels the field with the same words by default', () => {
    setUp();

    expect(root().querySelector('[role="search"]')?.getAttribute('aria-label')).toBe(
      'Search products',
    );
    expect(root().querySelector('label')?.textContent?.trim()).toBe('Search products');
  });

  /**
   * `SiteHeader` renders this component twice and the results page a third time. Naming the landmark
   * separately from the field is what makes three distinct landmark names possible without three
   * strange field labels.
   */
  it('names the landmark separately from the field when asked', () => {
    setUp();
    fixture.componentInstance.landmark.set('Search products, header panel');
    fixture.detectChanges();

    expect(root().querySelector('[role="search"]')?.getAttribute('aria-label')).toBe(
      'Search products, header panel',
    );
    expect(root().querySelector('label')?.textContent?.trim()).toBe('Search products');
  });

  /**
   * Generated ids are counted per application instance, not per module: a module-level counter lives
   * for the life of a Node process, so ids climbed across server renders while every browser started
   * again at 0. See `UniqueId`.
   */
  it('starts its generated ids from the beginning in each application instance', () => {
    setUp();
    const first = input().id;

    setUp();

    expect(input().id).toBe(first);
    expect(first).toMatch(/^sd-search-\d+$/);
  });

  // ------------------------------------------------------------------ debounced suggestions

  it('asks the API once for a word typed straight through', async () => {
    setUp();

    type('l');
    type('li');
    type('lin');
    type('line');
    type('linen');
    await settle();

    const requests = searchRequests();
    expect(requests).toHaveLength(1);
    expect(requests[0].request.params.get('q')).toBe('linen');
    requests[0].flush(response([card(1, 'Linen Shirt')], 'linen'));
  });

  it('asks for a small page, not a results page', async () => {
    setUp();

    type('linen');
    await settle();

    const request = searchRequest();
    expect(request.request.params.get('pageSize')).toBe('6');
    expect(request.request.params.get('sort')).toBe('recommended');
    request.flush(response([], 'linen'));
  });

  it.each(['l', 'a b', '  '])('asks for nothing when the term is %p', async (term) => {
    setUp();

    type(term);
    await settle();

    expect(searchRequests()).toEqual([]);
  });

  it('shows the products the API matched and the categories the cached tree matched', async () => {
    setUp();

    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt'), card(2, 'Linen Trousers')], 'linen'));
    fixture.detectChanges();

    expect(headings()).toEqual(['Products', 'Categories']);
    expect(labels()).toEqual(['Linen Shirt', 'Linen Trousers', 'Linen Shirts']);
  });

  it('announces how many suggestions there are', async () => {
    setUp();

    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();

    expect(announcement()).toBe('2 suggestions for linen.');
  });

  it('says so when nothing matches, rather than showing an empty list', async () => {
    setUp();

    type('zzzz');
    await settle();
    searchRequest().flush(response([], 'zzzz'));
    fixture.detectChanges();

    expect(options()).toEqual([]);
    expect(root().textContent).toContain('Nothing matches');
    expect(announcement()).toBe('No suggestions for zzzz.');
  });

  it('stays usable when the suggestion request fails', async () => {
    setUp();

    type('linen');
    await settle();
    searchRequest().flush('nope', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(root().textContent).toContain('Suggestions are not available');
    expect(input().getAttribute('aria-expanded')).toBe('true');
  });

  it('clears the list at once when the term is deleted back to nothing usable', async () => {
    setUp();

    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();
    expect(options()).toHaveLength(2);

    type('l');

    // Synchronously, without waiting for a timer: suggestions for a word that has been removed are
    // worse than none.
    expect(labels()).not.toContain('Linen Shirt');
    await settle();
  });

  // ------------------------------------------------------------------ the keyboard

  it('opens on ArrowDown and highlights the first option', async () => {
    setUp();

    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();

    press('ArrowDown');

    expect(input().getAttribute('aria-activedescendant')).toBe(options()[0].id);
    expect(options()[0].getAttribute('aria-selected')).toBe('true');
    expect(options()[1].getAttribute('aria-selected')).toBe('false');
  });

  it('keeps focus in the field while the highlight moves', async () => {
    setUp();

    input().focus();
    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();

    press('ArrowDown');
    press('ArrowDown');

    expect(document.activeElement).toBe(input());
  });

  it('wraps forwards past the last option and backwards past the first', async () => {
    setUp();

    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();

    const ids = options().map((option) => option.id);
    expect(ids).toHaveLength(2);

    press('ArrowDown');
    press('ArrowDown');
    expect(input().getAttribute('aria-activedescendant')).toBe(ids[1]);

    press('ArrowDown');
    expect(input().getAttribute('aria-activedescendant')).toBe(ids[0]);

    press('ArrowUp');
    expect(input().getAttribute('aria-activedescendant')).toBe(ids[1]);
  });

  it('goes to the last option when ArrowUp is pressed with nothing highlighted', async () => {
    setUp();

    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();

    press('ArrowUp');

    expect(input().getAttribute('aria-activedescendant')).toBe(options()[1].id);
  });

  it('drops the highlight when the list changes under it', async () => {
    setUp();

    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();
    press('ArrowDown');
    expect(input().getAttribute('aria-activedescendant')).toBeTruthy();

    type('linen s');
    await settle();
    searchRequest().flush(response([], 'linen s'));
    fixture.detectChanges();

    expect(input().hasAttribute('aria-activedescendant')).toBe(false);
  });

  it('chooses the highlighted suggestion on Enter rather than submitting the typed text', async () => {
    setUp();

    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();
    press('ArrowDown');

    const event = press('Enter');

    expect(event.defaultPrevented).toBe(true);
    expect(navigateByUrl).toHaveBeenCalledWith('/product/product-1');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('submits the typed term on Enter when nothing is highlighted', async () => {
    setUp();

    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();

    // The form's submit is what a bare Enter does in a browser; jsdom does not fire it, so the
    // submit path is exercised through the form itself. The e2e suite presses the real key.
    root().querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    fixture.detectChanges();

    expect(navigate).toHaveBeenCalledWith(['/search'], { queryParams: { q: 'linen' } });
  });

  it('closes the popup on Escape, keeps the caret, and does not let the keystroke travel', async () => {
    setUp();

    input().focus();
    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();

    press('Escape');

    expect(input().getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(input());
    expect(input().value).toBe('linen');
    // SiteHeader would have closed the whole search panel.
    expect(fixture.componentInstance.escapes()).toBe(0);
  });

  /**
   * **The second Escape must be prevented too.**
   *
   * Chrome's native `input[type=search]` clear is the keydown's default action, so the only thing
   * standing between a dismissing keystroke and an emptied field is `preventDefault()`. jsdom has no
   * such native behaviour, which is exactly why the first version of this suite could not see the
   * bug: it pressed a synthetic Escape and asserted the popup closed. What a unit test *can* assert
   * is the mechanism — that the keystroke is ours on both presses — and the browser suite then
   * measures the field itself (`search.e2e.ts`, "a second Escape leaves the field and the popup
   * alone").
   */
  it('prevents the browser default on every Escape, not only the one that closes the popup', async () => {
    setUp();

    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();

    const first = press('Escape');
    expect(first.defaultPrevented).toBe(true);

    const second = press('Escape');
    expect(second.defaultPrevented).toBe(true);
  });

  it('keeps the term and the closed popup across repeated Escapes', async () => {
    setUp();

    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();

    press('Escape');
    press('Escape');
    press('Escape');

    expect(input().value).toBe('linen');
    expect(input().getAttribute('aria-expanded')).toBe('false');
  });

  /**
   * The other half of the same bug: if a clear does get through from anywhere, `onInput` must not be
   * the thing that re-opens the popup behind it. This drives the input event a native clear would
   * dispatch, which is what jsdom cannot do on its own.
   */
  it('re-opens on a real edit, which is what tells an edit from a dismissal', async () => {
    setUp();

    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();
    press('Escape');
    expect(input().getAttribute('aria-expanded')).toBe('false');

    // A deliberate edit by the shopper, not a side effect of Escape.
    type('linen s');
    await settle();
    searchRequest().flush(response([], 'linen s'));
    fixture.detectChanges();

    expect(input().getAttribute('aria-expanded')).toBe('true');
  });

  it('lets a second Escape through, so the header can close the panel', async () => {
    setUp();

    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();

    press('Escape');
    press('Escape');

    expect(fixture.componentInstance.escapes()).toBe(1);
  });

  /**
   * The shape of #20 one component over: the mega menu re-opened as soon as focus returned to its
   * trigger after Escape. Focus is not an event this component listens to, and this is what says so.
   */
  it('does not re-open when focus comes back after Escape', async () => {
    setUp();

    input().focus();
    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();
    press('Escape');

    input().blur();
    input().dispatchEvent(new FocusEvent('focus', { bubbles: false }));
    input().focus();
    fixture.detectChanges();

    expect(input().getAttribute('aria-expanded')).toBe('false');
  });

  it('closes on Tab without choosing anything', async () => {
    setUp();

    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();
    press('ArrowDown');

    press('Tab');

    expect(input().getAttribute('aria-expanded')).toBe('false');
    expect(navigateByUrl).not.toHaveBeenCalled();
  });

  // ------------------------------------------------------------------ recent and browse

  it('offers this browser’s earlier searches when the field is empty', () => {
    setUp(['linen shirt', 'denim']);

    press('ArrowDown');

    expect(headings()).toEqual(['Recent searches']);
    expect(labels()).toEqual(['linen shirt', 'denim']);
  });

  it('runs an earlier search again when it is chosen', () => {
    setUp(['linen shirt']);

    press('ArrowDown');
    press('Enter');

    expect(navigate).toHaveBeenCalledWith(['/search'], { queryParams: { q: 'linen shirt' } });
  });

  it('lets the shopper clear their history in one action', () => {
    setUp(['linen shirt']);
    press('ArrowDown');

    root().querySelector<HTMLButtonElement>('.suggestions__clear')!.click();
    fixture.detectChanges();

    expect(storage.entries.has(RECENT_SEARCHES_KEY)).toBe(false);
    expect(headings()).toEqual(['Browse the shop']);
  });

  /**
   * SRCH-01 asks for popular searches and nothing records what anyone has searched for. What the
   * overlay offers instead is the catalogue's own entrances, and the one thing it must never do is
   * call them popular.
   */
  it('offers ways into the shop when there is no history, and never calls them popular', () => {
    setUp();

    press('ArrowDown');

    expect(headings()).toEqual(['Browse the shop']);
    expect(labels()).toEqual(['Women', 'Men', 'New Arrivals', 'Sale']);
    expect(root().textContent?.toLowerCase()).not.toContain('popular');
  });

  it('records a submitted search, and only a submitted one', async () => {
    setUp();

    type('linen');
    await settle();
    searchRequest().flush(response([card(1, 'Linen Shirt')], 'linen'));
    fixture.detectChanges();

    expect(storage.entries.has(RECENT_SEARCHES_KEY)).toBe(false);

    root().querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));

    expect(JSON.parse(storage.entries.get(RECENT_SEARCHES_KEY)!)).toEqual(['linen']);
  });

  // ------------------------------------------------------------------ hostile input

  /**
   * `encodeURIComponent` throws `URIError` on half a surrogate pair, and both `HttpParams` and the
   * router's URL serialiser call it. Before the term was normalised, one pasted character took the
   * whole navigation down with an uncaught error instead of searching for nothing.
   */
  it('does not throw on a lone surrogate, and searches the term without it', async () => {
    setUp();

    expect(() => type('linen \ud83d')).not.toThrow();
    await settle();

    const request = searchRequest();
    expect(request.request.params.get('q')).toBe('linen');
    request.flush(response([], 'linen'));

    root().querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));

    expect(navigate).toHaveBeenCalledWith(['/search'], { queryParams: { q: 'linen' } });
  });

  it('searches at most the API’s maximum length, however much is pasted', async () => {
    setUp();

    type('x'.repeat(2_000));
    await settle();

    const request = searchRequest();
    expect(request.request.params.get('q')).toHaveLength(100);
    request.flush(response([], 'x'.repeat(100)));
  });

  it('submits nothing at all for a term the API would refuse', () => {
    setUp();

    type('a');
    root().querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));

    expect(navigate).not.toHaveBeenCalled();
  });

  // ------------------------------------------------------------------ the box on the results page

  it('starts with the term it is given, as an attribute the server render carries', () => {
    setUp();
    fixture.componentInstance.seed.set('linen shirt');
    fixture.detectChanges();

    expect(input().getAttribute('value')).toBe('linen shirt');
    expect(input().value).toBe('linen shirt');
  });
});
