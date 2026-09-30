import { ComponentFixture, TestBed } from '@angular/core/testing';

import { RecoveryCodesPanel } from './recovery-codes-panel';

describe('RecoveryCodesPanel', () => {
  const codes = ['AAAAA-BBBBB', 'CCCCC-DDDDD'];

  function create(): ComponentFixture<RecoveryCodesPanel> {
    const fixture = TestBed.createComponent(RecoveryCodesPanel);
    fixture.componentRef.setInput('codes', codes);
    fixture.componentRef.setInput('account', 'admin@example.com');
    fixture.detectChanges();
    return fixture;
  }

  function button(fixture: ComponentFixture<RecoveryCodesPanel>, label: string): HTMLButtonElement {
    return [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].find((b) => b.textContent?.includes(label))!;
  }

  afterEach(() => vi.restoreAllMocks());

  it('lists every code', () => {
    const items = (create().nativeElement as HTMLElement).querySelectorAll('.codes__code');

    expect([...items].map((item) => item.textContent?.trim())).toEqual(codes);
  });

  it('copies the codes one per line and says so', async () => {
    const writeText = vi.fn(() => Promise.resolve());
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const fixture = create();

    button(fixture, 'Copy').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(writeText).toHaveBeenCalledWith('AAAAA-BBBBB\nCCCCC-DDDDD');
    expect((fixture.nativeElement as HTMLElement).querySelector('[role="status"]')?.textContent).toContain('copied');
  });

  it('downloads a text file with the account and the codes', async () => {
    const created: Blob[] = [];
    URL.createObjectURL = vi.fn((blob: Blob) => {
      created.push(blob);
      return 'blob:codes';
    });
    URL.revokeObjectURL = vi.fn();
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    const fixture = create();

    button(fixture, 'Download').click();

    expect(click).toHaveBeenCalled();
    const text = await created[0].text();
    expect(text).toContain('admin@example.com');
    expect(text).toContain('AAAAA-BBBBB\nCCCCC-DDDDD');
  });
});
