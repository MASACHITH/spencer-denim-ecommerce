import { DOCUMENT } from '@angular/common';
import { Component, Injector, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { focusEditForm } from './focus-edit-form';

@Component({
  selector: 'sda-focus-host',
  template: `
    <form data-testid="the-form">
      <input data-testid="first-box" />
      <input data-testid="second-box" />
    </form>
    <button type="button" data-testid="edit-row" (click)="edit()">Edit</button>
  `,
})
class Host {
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  edit(): void {
    focusEditForm(this.document, this.injector, 'first-box');
  }
}

describe('focusEditForm', () => {
  function create() {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    document.body.appendChild(fixture.nativeElement as HTMLElement);
    return fixture;
  }

  afterEach(() => {
    document.body.innerHTML = '';
    vi.restoreAllMocks();
  });

  it('puts the cursor in the first box of the form once the page has drawn', async () => {
    const fixture = create();
    const host = fixture.nativeElement as HTMLElement;

    host.querySelector<HTMLButtonElement>('[data-testid="edit-row"]')!.click();
    await fixture.whenStable();
    TestBed.tick();

    expect(document.activeElement).toBe(host.querySelector('[data-testid="first-box"]'));
  });

  it('brings the top of the form into view, so Edit on a row far down the page is not silent', async () => {
    const fixture = create();
    const host = fixture.nativeElement as HTMLElement;
    const form = host.querySelector<HTMLElement>('[data-testid="the-form"]')!;
    const scroll = vi.fn();
    form.scrollIntoView = scroll;

    host.querySelector<HTMLButtonElement>('[data-testid="edit-row"]')!.click();
    await fixture.whenStable();
    TestBed.tick();

    expect(scroll).toHaveBeenCalledTimes(1);
    expect(scroll.mock.calls[0][0]).toMatchObject({ block: 'start' });
  });

  it('does nothing, and does not throw, when the form is not on the page', async () => {
    const fixture = create();
    const host = fixture.nativeElement as HTMLElement;
    host.querySelector('form')!.remove();

    host.querySelector<HTMLButtonElement>('[data-testid="edit-row"]')!.click();
    await fixture.whenStable();

    expect(() => TestBed.tick()).not.toThrow();
  });
});
