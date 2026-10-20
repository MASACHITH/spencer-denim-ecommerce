import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type IconName = 'money' | 'wallet' | 'refund' | 'bag' | 'check' | 'average' | 'visitors' | 'alert';

/**
 * The small line icons in the dashboard's tinted tiles (#186). Drawn here, not taken from the client demo's
 * icon set, and always decoration (`aria-hidden`): the label beside each one says what it is.
 */
@Component({
  selector: 'sda-dash-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { 'aria-hidden': 'true' },
  template: `
    <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" focusable="false">
      @switch (name()) {
        @case ('money') { <rect x="2.5" y="6" width="19" height="12" rx="1.5" /><circle cx="12" cy="12" r="2.6" /><path d="M6 9.5v5M18 9.5v5" /> }
        @case ('wallet') { <path d="M4 7.5h14.5a1.5 1.5 0 0 1 1.5 1.5v9a1.5 1.5 0 0 1-1.5 1.5H5.5A1.5 1.5 0 0 1 4 18V6.5A1.5 1.5 0 0 1 5.5 5H17" /><circle cx="16" cy="13.5" r="1.1" /> }
        @case ('refund') { <path d="M8 5 4 9l4 4" /><path d="M4 9h10a6 6 0 0 1 0 12H9" /> }
        @case ('bag') { <path d="M5 8h14l-1 12H6L5 8Z" /><path d="M9 10V7a3 3 0 0 1 6 0v3" /> }
        @case ('check') { <circle cx="12" cy="12" r="8.5" /><path d="m8.5 12.2 2.4 2.4 4.6-5" /> }
        @case ('average') { <path d="M4 19h16" /><path d="M7 16V11M12 16V6M17 16v-3" /> }
        @case ('visitors') { <circle cx="9" cy="8.5" r="3" /><path d="M3.5 19a5.5 5.5 0 0 1 11 0" /><path d="M16 6.2a3 3 0 0 1 0 5.6M18 19a5.5 5.5 0 0 0-2.5-4.6" /> }
        @case ('alert') { <circle cx="12" cy="12" r="8.5" /><path d="M12 7.5v5.2M12 16.2v.3" /> }
      }
    </svg>
  `,
  styles: `
    :host {
      display: inline-flex; flex: 0 0 auto; align-items: center; justify-content: center;
      width: 44px; height: 44px; border-radius: 8px;
      color: var(--icon-colour, var(--chart-accent));
      background: color-mix(in srgb, var(--icon-colour, var(--chart-accent)) 10%, transparent);
    }
  `,
})
export class DashIcon {
  readonly name = input.required<IconName>();
}
