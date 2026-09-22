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

  function element(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return element().querySelector('[data-testid="announcement"]')?.textContent?.trim() ?? '';
  }

  function status(): string {
    return element().querySelector('[data-testid="announcement-status"]')?.textContent?.trim() ?? '';
  }

  function control(label: string): HTMLButtonElement {
    const button = element().querySelector<HTMLButtonElement>(`button[aria-label="${label}"]`);
    if (!button) {
      throw new Error(`No "${label}" control`);
    }
    return button;
  }

  afterEach(() => {
    vi.useRealTimers();
  });

  it('shows the first message', () => {
    render(false);

    expect(text()).toBe(PLACEHOLDER_ANNOUNCEMENTS[0].text);
  });

  // The message changes on a timer; a live region would read a marketing line aloud every
  // six seconds, interrupting whatever the visitor is doing.
  it('does not make the rotating message itself a live region', () => {
    render(false);
    const message = element().querySelector('[data-testid="announcement"]');

    expect(message?.hasAttribute('aria-live')).toBe(false);
    expect(message?.getAttribute('role')).toBeNull();
  });

  it('keeps a quiet status region that only speaks after a previous/next press', () => {
    render(false);
    const region = element().querySelector('[data-testid="announcement-status"]');

    expect(region?.getAttribute('role')).toBe('status');
    expect(region?.classList.contains('sr-only')).toBe(true);
    expect(status()).toBe('');

    control('Next announcement').click();
    fixture.detectChanges();

    expect(status()).toBe(PLACEHOLDER_ANNOUNCEMENTS[1].text);
  });

  it('says nothing when the timer moves the message on', () => {
    vi.useFakeTimers();
    render(false);

    vi.advanceTimersByTime(ANNOUNCEMENT_ROTATION_MS);
    fixture.detectChanges();

    expect(text()).toBe(PLACEHOLDER_ANNOUNCEMENTS[1].text);
    expect(status()).toBe('');
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

  // WCAG 2.2.2 (Level A): hover and focus are not a pause mechanism on a phone.
  describe('pause control (WCAG 2.2.2)', () => {
    function pauseButton(): HTMLButtonElement {
      const button = element().querySelector<HTMLButtonElement>(
        '[data-testid="announcement-pause"]',
      );
      if (!button) {
        throw new Error('No pause control');
      }
      return button;
    }

    it('is visible, named and reports its state', () => {
      render(false);

      expect(pauseButton().getAttribute('aria-label')).toBe('Pause announcements');
      expect(pauseButton().getAttribute('aria-pressed')).toBe('false');
    });

    it('stops the rotation on a click, with no pointer or focus involved', () => {
      vi.useFakeTimers();
      render(false);

      pauseButton().click();
      fixture.detectChanges();

      expect(pauseButton().getAttribute('aria-pressed')).toBe('true');

      vi.advanceTimersByTime(ANNOUNCEMENT_ROTATION_MS * 4);
      fixture.detectChanges();

      expect(fixture.componentInstance.currentIndex()).toBe(0);
    });

    it('resumes the rotation on a second click', () => {
      vi.useFakeTimers();
      render(false);

      pauseButton().click();
      fixture.detectChanges();
      vi.advanceTimersByTime(ANNOUNCEMENT_ROTATION_MS * 2);
      fixture.detectChanges();
      expect(fixture.componentInstance.currentIndex()).toBe(0);

      pauseButton().click();
      fixture.detectChanges();
      vi.advanceTimersByTime(ANNOUNCEMENT_ROTATION_MS);
      fixture.detectChanges();

      expect(fixture.componentInstance.currentIndex()).toBe(1);
    });

    it('keeps previous and next working while paused', () => {
      render(false);

      pauseButton().click();
      control('Next announcement').click();
      fixture.detectChanges();

      expect(text()).toBe(PLACEHOLDER_ANNOUNCEMENTS[1].text);
      expect(fixture.componentInstance.userPaused()).toBe(true);
    });

    it('is left out when nothing rotates, so no control does nothing', () => {
      render(true);

      expect(element().querySelector('[data-testid="announcement-pause"]')).toBeNull();
      expect(control('Next announcement')).toBeTruthy();
    });
  });

  it('pauses while the pointer is over the bar and resumes when it leaves', () => {
    vi.useFakeTimers();
    render(false);
    const host = element();

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

    element().dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    fixture.detectChanges();
    vi.advanceTimersByTime(ANNOUNCEMENT_ROTATION_MS * 2);
    fixture.detectChanges();

    expect(fixture.componentInstance.currentIndex()).toBe(0);
  });
});
