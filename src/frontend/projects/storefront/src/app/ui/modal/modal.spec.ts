import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { Modal } from './modal';

/**
 * A host that owns the open state, as every real caller does, and has a control to open it from
 * so focus has somewhere to go back to.
 */
@Component({
  imports: [Modal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button id="opener" type="button" (click)="open.set(true)">Open</button>
    <button id="outside" type="button">Outside</button>
    <sd-modal [open]="open()" heading="Size guide" [size]="size()" (closed)="open.set(false)">
      <button id="first" type="button">First</button>
      <button id="last" type="button">Last</button>
    </sd-modal>
  `,
})
class Host {
  readonly open = signal(false);
  readonly size = signal<'panel' | 'full'>('panel');
}

/**
 * The modal is the one piece of this page that a keyboard user is trapped inside, so its own
 * behaviour is pinned here rather than only through whatever happens to use it. It used to be
 * covered by the size-guide tests alone, and the size guide is hidden until issue #32.
 *
 * What jsdom cannot see — a ring clipped by an ancestor, reduced motion, a real scrim click —
 * stays in `e2e/product-page.e2e.ts` against the full-screen viewer.
 */
describe('Modal', () => {
  function setUp() {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ imports: [Host] });

    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;

    return {
      fixture,
      host: fixture.componentInstance,
      element,
      query: <T extends Element>(selector: string) => element.querySelector<T>(selector),
      opener: element.querySelector<HTMLButtonElement>('#opener')!,
      open: () => {
        fixture.componentInstance.open.set(true);
        fixture.detectChanges();
      },
      close: () => {
        fixture.componentInstance.open.set(false);
        fixture.detectChanges();
      },
    };
  }

  function press(key: string, shiftKey = false): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { key, shiftKey, bubbles: true, cancelable: true });
    document.dispatchEvent(event);
    return event;
  }

  afterEach(() => document.body.classList.remove('sd-no-scroll'));

  it('renders nothing at all while it is closed', () => {
    const { query } = setUp();

    expect(query('[role="dialog"]')).toBeNull();
    expect(query('.sd-modal__scrim')).toBeNull();
  });

  it('hides its host from the page while it is closed', () => {
    const { element } = setUp();

    expect(element.querySelector('sd-modal')?.hasAttribute('hidden')).toBe(true);
  });

  it('is a modal dialog when it opens', () => {
    const { query, open } = setUp();
    open();

    const dialog = query('[role="dialog"]');
    expect(dialog).not.toBeNull();
    expect(dialog?.getAttribute('aria-modal')).toBe('true');
    // Named by its own heading, so a screen reader announces which dialog this is.
    expect(query(`#${dialog?.getAttribute('aria-labelledby')}`)?.textContent).toContain(
      'Size guide',
    );
  });

  it('moves focus into the panel', () => {
    const { open, query } = setUp();
    open();

    expect(query('.sd-modal__panel')?.contains(document.activeElement)).toBe(true);
  });

  it('stops the page behind it scrolling, and lets go afterwards', () => {
    const { open, close } = setUp();

    open();
    expect(document.body.classList.contains('sd-no-scroll')).toBe(true);

    close();
    expect(document.body.classList.contains('sd-no-scroll')).toBe(false);
  });

  it('gives focus back to whatever opened it', () => {
    const { opener, fixture, close } = setUp();

    opener.focus();
    opener.click();
    fixture.detectChanges();

    close();
    expect(document.activeElement).toBe(opener);
  });

  it('asks to close on Escape, and swallows the key', () => {
    const { open, host } = setUp();
    open();

    const event = press('Escape');

    expect(host.open()).toBe(false);
    expect(event.defaultPrevented).toBe(true);
  });

  it('asks to close when the scrim is clicked', () => {
    const { open, query, fixture, host } = setUp();
    open();

    query<HTMLElement>('.sd-modal__scrim')?.click();
    fixture.detectChanges();

    expect(host.open()).toBe(false);
  });

  it('asks to close from its own close button', () => {
    const { open, query, fixture, host } = setUp();
    open();

    query<HTMLButtonElement>('.sd-modal__close')?.click();
    fixture.detectChanges();

    expect(host.open()).toBe(false);
  });

  describe('the focus trap', () => {
    it('wraps Tab from the last control back to the first', () => {
      const { open, element } = setUp();
      open();

      element.querySelector<HTMLButtonElement>('#last')!.focus();
      const event = press('Tab');

      expect(event.defaultPrevented).toBe(true);
      expect(document.activeElement).toBe(element.querySelector('.sd-modal__close'));
    });

    it('wraps Shift+Tab from the first control to the last', () => {
      const { open, element } = setUp();
      open();

      element.querySelector<HTMLButtonElement>('.sd-modal__close')!.focus();
      const event = press('Tab', true);

      expect(event.defaultPrevented).toBe(true);
      expect(document.activeElement).toBe(element.querySelector('#last'));
    });

    it('pulls focus back when something outside the dialog takes it', () => {
      const { open, element, query } = setUp();
      open();

      const outside = element.querySelector<HTMLButtonElement>('#outside')!;
      outside.focus();
      outside.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));

      expect(query('.sd-modal__panel')?.contains(document.activeElement)).toBe(true);
    });

    it('stops listening once it is closed, so the page keeps its own keys', () => {
      const { open, close, host } = setUp();

      open();
      close();
      host.open.set(true);
      // Closed and re-opened by the host, not by the key: the dialog must not have reacted to
      // an Escape pressed while it was shut.
      const event = press('Escape');

      expect(event.defaultPrevented).toBe(false);
    });
  });

  it('takes the whole viewport when it is asked to', () => {
    const { host, fixture, element } = setUp();

    host.size.set('full');
    host.open.set(true);
    fixture.detectChanges();

    expect(element.querySelector('sd-modal')?.classList.contains('sd-modal--full')).toBe(true);
  });
});
