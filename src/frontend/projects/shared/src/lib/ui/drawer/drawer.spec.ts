import { Component, ElementRef, signal, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { Drawer } from './drawer';

@Component({
  imports: [Drawer],
  template: `
    <button type="button" #trigger (click)="open.set(true)">Open menu</button>
    <sd-drawer
      [open]="open()"
      heading="Menu"
      closeLabel="Close menu"
      panelId="test-drawer"
      (closed)="open.set(false)"
    >
      <a href="#first">First link</a>
      <a href="#last">Last link</a>
    </sd-drawer>
  `,
})
class DrawerHost {
  readonly open = signal(false);
  readonly trigger = viewChild.required<ElementRef<HTMLButtonElement>>('trigger');
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
      trigger: fixture.componentInstance.trigger().nativeElement,
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
