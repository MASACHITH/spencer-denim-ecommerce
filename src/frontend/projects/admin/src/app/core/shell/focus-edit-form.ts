import { Injector, afterNextRender } from '@angular/core';

/**
 * After a staff member presses **Edit** on a row, takes them to the form that is now editing it.
 *
 * The screens that list things (couriers, promo codes, sale campaigns, announcements) put their form **above**
 * the list. Pressing Edit on a row halfway down fills that form in, but the form is off the top of the screen, so
 * Edit looks as if it did nothing. This brings the form's top into view and puts the cursor in its first box
 * (so a keyboard or screen-reader user arrives there too). The Mega menu cards screen did this already; this is
 * the same behaviour for the others.
 *
 * Runs after the next render because the form may only just have changed from "Add" to "Edit". The scroll is
 * skipped where the browser has no `scrollIntoView` (the unit-test DOM) and is instant for a visitor who has asked
 * for reduced motion.
 */
export function focusEditForm(document: Document, injector: Injector, firstBoxTestId: string): void {
  afterNextRender(
    () => {
      const firstBox = document.querySelector<HTMLElement>(`[data-testid="${firstBoxTestId}"]`);

      if (firstBox === null) {
        return;
      }

      firstBox.focus({ preventScroll: true });

      const top = firstBox.closest('form') ?? firstBox;
      if (typeof top.scrollIntoView === 'function') {
        const view = document.defaultView;
        const calm = typeof view?.matchMedia === 'function' ? view.matchMedia('(prefers-reduced-motion: reduce)').matches : true;
        top.scrollIntoView({ block: 'start', behavior: calm ? 'auto' : 'smooth' });
      }
    },
    { injector },
  );
}
