import { Component, ElementRef, signal, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { Drawer } from './drawer';

@Component({
  imports: [Drawer],
  template: `
    @if (triggerExists()) {
      <button type="button" #trigger (click)="open.set(true)">Open menu</button>
    }
    <sd-drawer
      [open]="open()"
      heading="Menu"
      closeLabel="Close menu"
      panelId="test-drawer"
      (closed)="open.set(false)"
      (focusLost)="focusLosses = focusLosses + 1"
    >
      <a href="#first">First link</a>
      <a href="#last">Last link</a>
    </sd-drawer>
  `,
})
class DrawerHost {
  readonly open = signal(false);

  /**
   * Lets a test **destroy the control that opened the drawer while it is open**, which is the one
   * case `focusLost` exists for and the shape W2-08 found in the wild: moving a wishlist row to the
   * bag deletes that row's button, the mini bag opens over the gap, and dismissing it left a
   * keyboard shopper on `<body>`.
   */
  readonly triggerExists = signal(true);

  readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');

  focusLosses = 0;
}

describe('Drawer', () => {
  async function render() {
    TestBed.configureTestingModule({ imports: [DrawerHost] });
    const fixture = TestBed.createComponent(DrawerHost);
    fixture.detectChanges();
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      host: fixture.componentInstance,
      trigger: fixture.componentInstance.trigger()!.nativeElement,
      drawer: element.querySelector('sd-drawer') as HTMLElement,
      panel: element.querySelector('#test-drawer') as HTMLElement,
      closeButton: element.querySelector('#test-drawer button') as HTMLButtonElement,
      settle: async () => {
        fixture.detectChanges();
        await fixture.whenStable();
      },
    };
  }

  it('starts closed, with the panel out of view', async () => {
    const { drawer, panel } = await render();

    expect(drawer.classList.contains('is-open')).toBe(false);
    expect(panel.getAttribute('role')).toBe('dialog');
    expect(panel.getAttribute('aria-modal')).toBe('true');
    expect(document.body.classList.contains('sd-no-scroll')).toBe(false);
  });

  it('names the dialog with its visible heading', async () => {
    const { panel, fixture } = await render();

    const headingId = panel.getAttribute('aria-labelledby');
    const heading = (fixture.nativeElement as HTMLElement).querySelector(`#${headingId}`);

    expect(heading?.textContent?.trim()).toBe('Menu');
  });

  it('opens on a real click, moves focus inside and locks page scrolling', async () => {
    const { trigger, drawer, closeButton, settle } = await render();

    trigger.focus();
    trigger.click();
    await settle();

    expect(drawer.classList.contains('is-open')).toBe(true);
    expect(document.activeElement).toBe(closeButton);
    expect(document.body.classList.contains('sd-no-scroll')).toBe(true);
  });

  it('closes on Escape and returns focus to whatever opened it', async () => {
    const { trigger, drawer, host, settle } = await render();

    trigger.focus();
    trigger.click();
    await settle();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle();

    expect(host.open()).toBe(false);
    expect(drawer.classList.contains('is-open')).toBe(false);
    expect(document.activeElement).toBe(trigger);
    expect(document.body.classList.contains('sd-no-scroll')).toBe(false);
  });

  /**
   * **When the opener has been destroyed, the drawer says so instead of pretending.** It cannot focus a
   * detached node, and the browser's answer to focusing nothing is `<body>`: no tab position, nothing
   * announced, a keyboard user stranded. Only the host knows where focus should go instead, so the
   * drawer raises `focusLost` at the one moment that fact is knowable.
   */
  it('raises focusLost when what opened it has gone', async () => {
    const { trigger, host, settle } = await render();

    trigger.focus();
    trigger.click();
    await settle();

    // The drawer's own work removed the control that opened it.
    host.triggerExists.set(false);
    await settle();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle();

    expect(host.focusLosses).toBe(1);

    // Where focus is left is the host's problem from here, and in jsdom it is still the drawer's own
    // close button: `sd-drawer` keeps its panel in the DOM when closed, so nothing drops focus the way
    // a real browser does once the panel is hidden. What this test can prove is that the drawer did not
    // silently "restore" to something, and that it told the host — `wishlist.e2e.ts` measures the rest
    // on a real browser, where the landing is the header's bag anchor.
    expect(document.activeElement).not.toBe(trigger);
  });

  /** A surviving opener is restored to, and raises nothing: `focusLost` is the exception, not the rule. */
  it('raises no focusLost when it can give focus back', async () => {
    const { trigger, host, settle } = await render();

    trigger.focus();
    trigger.click();
    await settle();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle();

    expect(document.activeElement).toBe(trigger);
    expect(host.focusLosses).toBe(0);
  });

  /**
   * **`<body>` is the absence of a return target, not one of them.** Opening with nothing focused used
   * to record `document.body`, which is connected, so `deactivate` "restored" it — a no-op dressed up
   * as success that left the host untold. This is the assertion that made `focusLost` reachable at all
   * in W2-08's case, where Quick View had already declined to restore to a destroyed button and focus
   * was therefore on `<body>` before the mini bag even opened.
   */
  it('treats a body-focused open as having nothing to return to', async () => {
    const { host, settle } = await render();

    (document.activeElement as HTMLElement | null)?.blur();
    expect(document.activeElement).toBe(document.body);

    host.open.set(true);
    await settle();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle();

    expect(host.focusLosses).toBe(1);
  });

  it('closes on the close button and on the scrim', async () => {
    const { trigger, host, closeButton, drawer, settle } = await render();

    trigger.click();
    await settle();
    closeButton.click();
    await settle();
    expect(host.open()).toBe(false);

    trigger.click();
    await settle();
    drawer.querySelector<HTMLElement>('.sd-drawer__scrim')?.click();
    await settle();
    expect(host.open()).toBe(false);
  });

  it('keeps Tab inside the panel', async () => {
    const { trigger, panel, closeButton, settle } = await render();

    trigger.click();
    await settle();

    const links = Array.from(panel.querySelectorAll<HTMLAnchorElement>('a[href]'));
    const last = links[links.length - 1];

    last.focus();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }));
    expect(document.activeElement).toBe(closeButton);

    closeButton.focus();
    document.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true }),
    );
    expect(document.activeElement).toBe(last);
  });

  it('pulls focus back when something outside steals it', async () => {
    const { trigger, closeButton, settle } = await render();

    trigger.click();
    await settle();

    trigger.focus();
    expect(document.activeElement).toBe(closeButton);
  });
});
