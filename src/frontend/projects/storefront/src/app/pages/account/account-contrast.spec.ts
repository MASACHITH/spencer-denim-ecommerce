import { contrastRatio } from '../../core/platform/contrast';

/**
 * The colours the account pages draw, measured rather than asserted in a comment.
 *
 * **This exists because axe did not catch a 20 px control or a wrongly-ranked heading on this project
 * this week**, and because a contrast ratio written into a CSS comment is a claim nobody re-checks: the
 * first draft of `styles.css` said `--color-sale` gives "6.4:1 on white", which is wrong in the third
 * digit. So the numbers live here, computed by the same `contrastRatio` the sold-out slash is chosen
 * with, and a token that moves takes a test with it rather than quietly lowering the guarantee.
 *
 * The tokens are transcribed from `projects/shared/src/styles/tokens.css`. That duplication is the
 * weakness of this file and is named rather than hidden: CSS custom properties are not readable from a
 * jsdom test without a layout engine, so the alternative is no check at all. A token changed in one
 * place and not the other fails here, which is the behaviour that matters.
 */
const TOKENS = {
  bg: '#ffffff',
  bgAlt: '#f7f5f1',
  surface: '#ffffff',
  text: '#1a1a1a',
  textMuted: '#6b6b6b',
  border: '#e3e0da',
  borderStrong: '#6b6b6b',
  primary: '#1a1a1a',
  onPrimary: '#ffffff',
  sale: '#b3261e',
  success: '#2e7d4f',
} as const;

/** WCAG 2.2 AA: 4.5:1 for body text, 3:1 for large text and for non-text contrast (1.4.11). */
const TEXT_MINIMUM = 4.5;
const NON_TEXT_MINIMUM = 3;

describe('the account pages’ contrast (WCAG 2.2 AA)', () => {
  /** Rounded the way a reader would quote it, so a failure message is readable. */
  function ratio(a: string, b: string): number {
    return Math.round(contrastRatio(a, b) * 100) / 100;
  }

  describe('text', () => {
    it('passes for ordinary body text on both page grounds', () => {
      expect(ratio(TOKENS.text, TOKENS.bg)).toBeGreaterThanOrEqual(TEXT_MINIMUM);
      expect(ratio(TOKENS.text, TOKENS.bgAlt)).toBeGreaterThanOrEqual(TEXT_MINIMUM);
    });

    /**
     * `.acc__muted`, `.acc-hint` and `.acc-card__lines`' muted labels are all small text, so they need
     * the full 4.5 and not the 3 that large text would allow.
     */
    it('passes for the muted hints and labels, which are small text', () => {
      expect(ratio(TOKENS.textMuted, TOKENS.bg)).toBeGreaterThanOrEqual(TEXT_MINIMUM);
      expect(ratio(TOKENS.textMuted, TOKENS.surface)).toBeGreaterThanOrEqual(TEXT_MINIMUM);
    });

    /**
     * `.acc-error` and `.acc-notice--alert` — every field refusal and every page-level alert on these
     * eight pages. The field also takes a 2 px red border and `aria-invalid="true"`, so the state does
     * not rest on colour alone (WCAG 1.4.1); this is about whether the sentence can be *read*.
     */
    it('passes for a refusal, in red on white', () => {
      expect(ratio(TOKENS.sale, TOKENS.bg)).toBeGreaterThanOrEqual(TEXT_MINIMUM);
    });

    /** `.acc-notice--ok` — the server's queued-link sentence and every save confirmation. */
    it('passes for a confirmation, in green on white', () => {
      expect(ratio(TOKENS.success, TOKENS.bg)).toBeGreaterThanOrEqual(TEXT_MINIMUM);
    });

    /** `.btn--primary`: Sign in, Create account, Save details, Set password. */
    it('passes for the label on a primary button', () => {
      expect(ratio(TOKENS.onPrimary, TOKENS.primary)).toBeGreaterThanOrEqual(TEXT_MINIMUM);
    });
  });

  describe('the edges of controls (1.4.11, non-text)', () => {
    /**
     * **`--color-border-strong` and not `--color-border` on every box and select**, which is the whole
     * reason `.acc-input` names the stronger token: the card border fails 3:1 and is allowed to, and an
     * input's edge is a user-interface component boundary that is not.
     */
    it('passes for an input or select edge', () => {
      expect(ratio(TOKENS.borderStrong, TOKENS.surface)).toBeGreaterThanOrEqual(NON_TEXT_MINIMUM);
    });

    it('passes for a refused input’s edge', () => {
      expect(ratio(TOKENS.sale, TOKENS.surface)).toBeGreaterThanOrEqual(NON_TEXT_MINIMUM);
    });

    it('passes for the Default flag’s edge and the focus ring', () => {
      expect(ratio(TOKENS.primary, TOKENS.surface)).toBeGreaterThanOrEqual(NON_TEXT_MINIMUM);
    });

    it('passes for a notice’s border, which is drawn in its own text colour', () => {
      expect(ratio(TOKENS.sale, TOKENS.bg)).toBeGreaterThanOrEqual(NON_TEXT_MINIMUM);
      expect(ratio(TOKENS.success, TOKENS.bg)).toBeGreaterThanOrEqual(NON_TEXT_MINIMUM);
    });

    /**
     * **`--color-border` on `.acc-card` and `.acc-order` is 1.32:1 and that is deliberate.** WCAG 1.4.11
     * covers user-interface components and graphical objects needed to understand the content; a hairline
     * separating two cards is neither — the card's own text carries every fact on it, and the address
     * lines are legible with no border at all. It is the same token `bag-page.ts` and `checkout-page.ts`
     * already draw their panels with, so raising it here would make the account pages the odd ones out.
     *
     * **It is asserted as a fact rather than left unmentioned**, so that nobody later moves a *control's*
     * edge onto this token on the strength of the cards using it.
     */
    it('records that the decorative card border is below 3:1, on purpose', () => {
      expect(ratio(TOKENS.border, TOKENS.surface)).toBeLessThan(NON_TEXT_MINIMUM);
      expect(ratio(TOKENS.border, TOKENS.surface)).toBeCloseTo(1.32, 1);
    });
  });

  /**
   * The measured figures, so a token change shows up as a number that moved rather than as a pass that
   * became a slightly different pass. Ran on this machine; `contrastRatio` is the project's own
   * implementation of the WCAG 2.1 formula and `contrast.spec.ts` walks the colour cube against it.
   */
  it('measures the figures this task quotes', () => {
    expect(ratio(TOKENS.sale, TOKENS.bg)).toBe(6.54);
    expect(ratio(TOKENS.success, TOKENS.bg)).toBe(5.05);
    expect(ratio(TOKENS.textMuted, TOKENS.bg)).toBe(5.33);
    expect(ratio(TOKENS.text, TOKENS.bg)).toBe(17.4);
    expect(ratio(TOKENS.border, TOKENS.surface)).toBe(1.32);
  });
});
