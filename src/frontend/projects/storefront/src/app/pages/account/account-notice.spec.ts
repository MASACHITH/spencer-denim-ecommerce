import { ChangeDetectionStrategy, Component, PLATFORM_ID, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AccountNotice } from './account-notice';

/**
 * A host, so the two inputs can be driven the way a page drives them: by writing a signal and
 * re-rendering, which is exactly the update that used to create the region and its text together.
 */
@Component({
  imports: [AccountNotice],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<sd-account-notice [alert]="alert()" [status]="status()" testId="host" />`,
})
class NoticeHost {
  readonly alert = signal<string | null>(null);
  readonly status = signal<string | null>(null);
}

/**
 * The two live regions every account page renders.
 *
 * **This is the defect the component exists to prevent, written as a test.** A message written into a
 * node the same update created is not announced: assistive technology announces a live region because it
 * was already watching it. That has shipped twice on this project, and the second time a blind customer
 * pressed Cancel and heard nothing. `@if (error()) { <p role="alert">…` is the exact shape of it, and
 * `checkout-page.ts` still has it.
 */
describe('AccountNotice (W3-01)', () => {
  let fixture: ComponentFixture<NoticeHost>;
  let element: HTMLElement;

  function create(platform: 'browser' | 'server' = 'browser'): void {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: platform === 'server' ? [{ provide: PLATFORM_ID, useValue: 'server' }] : [],
    });

    fixture = TestBed.createComponent(NoticeHost);
    element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  }

  function alertRegion(): HTMLElement {
    return element.querySelector('[data-testid="host-alert"]') as HTMLElement;
  }

  function statusRegion(): HTMLElement {
    return element.querySelector('[data-testid="host-status"]') as HTMLElement;
  }

  beforeEach(() => create());

  /**
   * **On the server platform**, which is the render a shopper's browser hydrates and a crawler reads.
   * The region has to be in *that* markup, not added by the browser: a region created in the browser
   * before any message is written is the same bug one frame later.
   */
  it('renders both regions on the server, before there is anything to announce', () => {
    create('server');

    expect(alertRegion()).not.toBeNull();
    expect(statusRegion()).not.toBeNull();
    expect(alertRegion().getAttribute('role')).toBe('alert');
    expect(statusRegion().getAttribute('role')).toBe('status');
    expect(alertRegion().textContent).toBe('');
    expect(statusRegion().textContent).toBe('');
  });

  /**
   * The identity of the node must not change when a message arrives — if it did, the message would be in
   * a region nothing had been watching. Holding the element from before and comparing it afterwards is
   * the only way to assert that, because a replaced node passes every test that only queries by selector.
   */
  it('writes into the same node it rendered empty, rather than a new one', () => {
    const before = alertRegion();

    fixture.componentInstance.alert.set('That did not work.');
    fixture.detectChanges();

    expect(alertRegion()).toBe(before);
    expect(before.textContent).toBe('That did not work.');
  });

  it('writes a status into the same node too', () => {
    const before = statusRegion();

    fixture.componentInstance.status.set('Saved.');
    fixture.detectChanges();

    expect(statusRegion()).toBe(before);
    expect(before.textContent).toBe('Saved.');
  });

  /**
   * **Never `display: none` and never `hidden`.** Changes inside a hidden subtree are not announced, and
   * unhiding a region in the same update as its text is the same race by another name — so the visual
   * state is a class that adds a border, and the node is in flow either way.
   */
  it('hides neither region, and marks the one with something to say', () => {
    expect(alertRegion().hasAttribute('hidden')).toBe(false);
    expect(alertRegion().classList.contains('is-on')).toBe(false);

    fixture.componentInstance.alert.set('That did not work.');
    fixture.detectChanges();

    expect(alertRegion().hasAttribute('hidden')).toBe(false);
    expect(alertRegion().classList.contains('is-on')).toBe(true);
    // The other one is untouched: one message must not style the region it does not belong to.
    expect(statusRegion().classList.contains('is-on')).toBe(false);
  });

  /** Both are focusable without being in the tab order, so a page can re-home focus into a refusal. */
  it('makes both reachable by focus and neither by Tab', () => {
    expect(alertRegion().getAttribute('tabindex')).toBe('-1');
    expect(statusRegion().getAttribute('tabindex')).toBe('-1');
  });

  /**
   * The shell already renders five live regions before a page adds its own — the announcement bar, the
   * header search, the mini bag twice and the footer newsletter — so `role="status"` is not a unique
   * address on any page of this site. `wishlist-page.ts` records the same count.
   */
  it('names its regions from the page that owns it', () => {
    expect(alertRegion().dataset['testid']).toBe('host-alert');
    expect(statusRegion().dataset['testid']).toBe('host-status');
  });
});
