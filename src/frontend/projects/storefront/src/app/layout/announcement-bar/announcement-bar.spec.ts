import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ReducedMotion } from '@spencer-denim/shared';

import { ANNOUNCEMENT_ROTATION_MS, AnnouncementBar } from './announcement-bar';
import { PLACEHOLDER_ANNOUNCEMENTS } from '../../core/announcements/announcement.service';

/** Stands in for the media query so each test can choose the motion setting. */
class ReducedMotionStub {
  readonly state = signal(false);
  readonly enabled = this.state.asReadonly();
}

describe('AnnouncementBar', () => {
  let motion: ReducedMotionStub;
  let fixture: ComponentFixture<AnnouncementBar>;

  function render(reducedMotion: boolean) {
    motion = new ReducedMotionStub();
    motion.state.set(reducedMotion);

    TestBed.configureTestingModule({
      imports: [AnnouncementBar],
      providers: [{ provide: ReducedMotion, useValue: motion }],
    });

    fixture = TestBed.createComponent(AnnouncementBar);
    fixture.detectChanges();
    return fixture;
  }

  function text(): string {
    return (
      (fixture.nativeElement as HTMLElement)
        .querySelector('[data-testid="announcement"]')
        ?.textContent?.trim() ?? ''
    );
  }

  function control(label: string): HTMLButtonElement {
    const button = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      `button[aria-label="${label}"]`,
    );
    if (!button) {
      throw new Error(`No "${label}" control`);
    }
    return button;
  }

  afterEach(() => {
    vi.useRealTimers();
  });

  it('shows the first message and announces changes politely', () => {
    render(false);

    expect(text()).toBe(PLACEHOLDER_ANNOUNCEMENTS[0].text);
    expect(
      (fixture.nativeElement as HTMLElement)
        .querySelector('[data-testid="announcement"]')
        ?.getAttribute('aria-live'),
    ).toBe('polite');
  });

  it('offers previous and next controls', () => {
    render(false);

    expect(control('Previous announcement')).toBeTruthy();
    expect(control('Next announcement')).toBeTruthy();
  });

  it('moves to the next message and wraps round', () => {
    render(true);
    const last = PLACEHOLDER_ANNOUNCEMENTS.length - 1;

    control('Next announcement').click();
    fixture.detectChanges();
    expect(text()).toBe(PLACEHOLDER_ANNOUNCEMENTS[1].text);

    control('Previous announcement').click();
    control('Previous announcement').click();
    fixture.detectChanges();
    expect(text()).toBe(PLACEHOLDER_ANNOUNCEMENTS[last].text);
  });

  it('rotates on its own when motion is allowed', () => {
    vi.useFakeTimers();
    render(false);

    expect(text()).toBe(PLACEHOLDER_ANNOUNCEMENTS[0].text);

    vi.advanceTimersByTime(ANNOUNCEMENT_ROTATION_MS);
    fixture.detectChanges();

    expect(text()).toBe(PLACEHOLDER_ANNOUNCEMENTS[1].text);
  });

  it('never rotates on its own when the visitor prefers reduced motion (GLB-12)', () => {
    vi.useFakeTimers();
    render(true);

    vi.advanceTimersByTime(ANNOUNCEMENT_ROTATION_MS * 5);
    fixture.detectChanges();

    expect(text()).toBe(PLACEHOLDER_ANNOUNCEMENTS[0].text);
    expect(fixture.componentInstance.currentIndex()).toBe(0);
  });

  it('stops rotating when the setting changes to reduced motion', () => {
    vi.useFakeTimers();
    render(false);

    motion.state.set(true);
    fixture.detectChanges();

    vi.advanceTimersByTime(ANNOUNCEMENT_ROTATION_MS * 3);
    fixture.detectChanges();

    expect(fixture.componentInstance.currentIndex()).toBe(0);
  });

  it('pauses while the pointer is over the bar and resumes when it leaves', () => {
    vi.useFakeTimers();
    render(false);
    const host = fixture.nativeElement as HTMLElement;

    host.dispatchEvent(new MouseEvent('mouseenter'));
    fixture.detectChanges();
    vi.advanceTimersByTime(ANNOUNCEMENT_ROTATION_MS * 2);
    fixture.detectChanges();
    expect(fixture.componentInstance.currentIndex()).toBe(0);

    host.dispatchEvent(new MouseEvent('mouseleave'));
    fixture.detectChanges();
    vi.advanceTimersByTime(ANNOUNCEMENT_ROTATION_MS);
    fixture.detectChanges();
    expect(fixture.componentInstance.currentIndex()).toBe(1);
  });

  it('pauses while a control has keyboard focus', () => {
    vi.useFakeTimers();
    render(false);
    const host = fixture.nativeElement as HTMLElement;

    host.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    fixture.detectChanges();
    vi.advanceTimersByTime(ANNOUNCEMENT_ROTATION_MS * 2);
    fixture.detectChanges();

    expect(fixture.componentInstance.currentIndex()).toBe(0);
  });
});
