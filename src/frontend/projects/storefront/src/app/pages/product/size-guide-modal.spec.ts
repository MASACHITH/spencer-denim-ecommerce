import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { SizeGuideContentDto, SizeGuideRefDto } from '../../core/catalog/catalog.model';
import { SizeGuideModal } from './size-guide-modal';

@Component({
  imports: [SizeGuideModal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-size-guide-modal [guide]="guide()" [open]="true" />
  `,
})
class Host {
  readonly guide = signal<SizeGuideRefDto>({ id: 1, name: "Women's apparel" });
}

const FULL: SizeGuideContentDto = {
  introHtml: '<p>Measurements are in <strong>centimetres</strong>.</p>',
  table: {
    columns: ['Size', 'Bust', 'Waist'],
    rows: [
      ['XS', '78-82', '60-64'],
      ['S', '83-87', '65-69'],
    ],
  },
  measuringTipsHtml: '<p>Keep the tape level.</p>',
  imageUrl: null,
  imageAltText: null,
};

/**
 * PDP-04's dialog body.
 *
 * The parts that need a real browser — a four-column table at 320 px, the scroller, the focus
 * trap — are in `e2e/product-page.e2e.ts` and `modal.spec.ts`. What is pinned here is what the
 * markup *is*: which fields are rendered as HTML, which are rendered as text, and which blocks
 * disappear when their field is empty.
 */
describe('SizeGuideModal', () => {
  function setUp(content: SizeGuideContentDto | null, name = "Women's apparel") {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ imports: [Host] });

    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.guide.set({ id: 1, name, content });
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;

    return {
      element,
      query: <T extends Element>(selector: string) => element.querySelector<T>(selector),
      all: (selector: string) => [...element.querySelectorAll(selector)],
      text: () => element.textContent ?? '',
    };
  }

  it('draws the whole guide the API sent', () => {
    const { query, all, element } = setUp(FULL);

    // The prose is markup, so it has to arrive as elements rather than as visible angle brackets.
    expect(query('.guide__intro strong')?.textContent).toBe('centimetres');
    expect(element.textContent).not.toContain('<strong>');

    expect(all('thead th').map((cell) => cell.textContent)).toEqual(['Size', 'Bust', 'Waist']);
    expect(all('tbody tr').length).toBe(2);

    // Cell by cell. Joining `textContent` would have read "S83-8765-69", because there is no
    // whitespace between the elements to normalise.
    expect(
      [...all('tbody tr')[1].querySelectorAll('th, td')].map((cell) => cell.textContent),
    ).toEqual(['S', '83-87', '65-69']);

    expect(query('.guide__subhead')?.textContent).toBe('How to measure');
  });

  /** Every column header is a `th[scope=col]`, and every row's size is that row's header. */
  it('builds a table a screen reader can read', () => {
    const { all, query } = setUp(FULL);

    expect(all('thead th[scope="col"]').length).toBe(3);
    expect(all('tbody th[scope="row"]').map((cell) => cell.textContent)).toEqual(['XS', 'S']);
    expect(all('tbody tr')[0].querySelectorAll('td').length).toBe(2);

    // A caption, so the table is named without repeating the dialog heading on screen.
    expect(query('caption')?.textContent).toBe("Women's apparel measurements");
    expect(query('caption')?.className).toContain('sr-only');
  });

  /**
   * **The one that matters for safety.** `SizeGuideTableDto` says cells are plain text, and nothing
   * sanitises them — the server sends whatever the JSON held. A cell bound to `[innerHTML]` would
   * turn this row into an element; interpolated, it is six characters on the screen.
   */
  it('prints markup in a cell as characters rather than parsing it', () => {
    const { query, all } = setUp({
      ...FULL,
      table: {
        columns: ['Size', 'Note'],
        rows: [['S', '<img src=x onerror="alert(1)">']],
      },
    });

    const cell = all('tbody td')[0];

    // Nothing was parsed: no element was created, and the cell holds text and only text.
    expect(query('tbody img')).toBeNull();
    expect(cell.children.length).toBe(0);
    expect(cell.textContent).toBe('<img src=x onerror="alert(1)">');

    // And it is escaped on the way out, which is the positive half of the same claim.
    //
    // `expect(element.innerHTML).not.toContain('onerror=')` was the obvious assertion here and it
    // is a **false** one: serialising a text node escapes `<` and `>` and leaves `onerror="` as
    // ordinary characters, so that string is present in the markup whether or not an attribute
    // exists. It can never pass, and if it ever had it would have been measuring the wrong thing.
    expect(cell.innerHTML).toBe('&lt;img src=x onerror="alert(1)"&gt;');
  });

  it('leaves out each block whose field is empty rather than drawing a heading over a gap', () => {
    const tableOnly = setUp({
      introHtml: null,
      table: FULL.table,
      measuringTipsHtml: '   ',
      imageUrl: null,
      imageAltText: null,
    });

    expect(tableOnly.query('.guide__intro')).toBeNull();
    expect(tableOnly.query('.guide__tips')).toBeNull();
    expect(tableOnly.query('.guide__subhead')).toBeNull();
    expect(tableOnly.query('table')).not.toBeNull();

    const proseOnly = setUp({
      introHtml: '<p>No chart for this one yet.</p>',
      table: null,
      measuringTipsHtml: null,
      imageUrl: null,
      imageAltText: null,
    });

    expect(proseOnly.query('table')).toBeNull();
    expect(proseOnly.query('.guide__scroll')).toBeNull();
    expect(proseOnly.text()).toContain('No chart for this one yet.');
  });

  /**
   * The API already answers `table: null` for JSON it could not read. These are the shapes that
   * survive that and would still draw a `<thead>` over an empty `<tbody>`.
   */
  it('draws no table for a header with no rows, or rows with no header', () => {
    expect(
      setUp({ ...FULL, table: { columns: ['Size', 'Bust'], rows: [] } }).query('table'),
    ).toBeNull();

    expect(setUp({ ...FULL, table: { columns: [], rows: [['S']] } }).query('table')).toBeNull();
  });

  describe('the measuring diagram', () => {
    it('uses the asset alt text when there is one', () => {
      const { query } = setUp({
        ...FULL,
        imageUrl: '/media/public/renditions/abc/large.webp',
        imageAltText: 'Where to put the tape for bust, waist and hip.',
      });

      const image = query<HTMLImageElement>('.guide__image')!;
      expect(image.getAttribute('src')).toBe('/media/public/renditions/abc/large.webp');
      expect(image.getAttribute('alt')).toBe('Where to put the tape for bust, waist and hip.');
    });

    /**
     * Never `alt=""`: a measurement diagram is informative, so an empty alt hides it outright. The
     * fallback names the guide instead of describing a picture this component cannot see.
     */
    it('falls back to naming the guide rather than to an empty alt', () => {
      const { query } = setUp({
        ...FULL,
        imageUrl: '/media/public/renditions/abc/large.webp',
        imageAltText: null,
      });

      expect(query<HTMLImageElement>('.guide__image')?.getAttribute('alt')).toBe(
        "Measuring diagram for the Women's apparel guide",
      );
    });

    it('draws no image when there is none', () => {
      expect(setUp(FULL).query('.guide__image')).toBeNull();
    });
  });

  it('names the dialog after the guide', () => {
    const { query } = setUp(FULL, 'Kids and baby');

    expect(query('.sd-modal__title')?.textContent).toBe('Kids and baby size guide');
  });
});
