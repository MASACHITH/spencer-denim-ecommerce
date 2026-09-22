import { isPlatformBrowser } from '@angular/common';
import { Injectable, PLATFORM_ID, inject } from '@angular/core';

/**
 * `localStorage` that can never throw (lesson L1).
 *
 * It is missing on the server, and a browser can refuse it outright: private windows, blocked
 * site data, and a full quota all raise. Every call is guarded, and a failure degrades to
 * "no stored value" rather than breaking the page.
 */
@Injectable({ providedIn: 'root' })
export class SafeLocalStorage {
  private readonly isBrowser = isPlatformBrowser(inject(PLATFORM_ID));

  /** The stored string, or `null` when it is missing, unreadable or we are on the server. */
  read(key: string): string | null {
    if (!this.isBrowser) {
      return null;
    }

    try {
      return window.localStorage.getItem(key);
    } catch {
      return null;
    }
  }

  /** `true` when the value was stored. Storage being unavailable is not an error here. */
  write(key: string, value: string): boolean {
    if (!this.isBrowser) {
      return false;
    }

    try {
      window.localStorage.setItem(key, value);
      return true;
    } catch {
      return false;
    }
  }

  /** `true` when the key is gone (or was never there). */
  remove(key: string): boolean {
    if (!this.isBrowser) {
      return false;
    }

    try {
      window.localStorage.removeItem(key);
      return true;
    } catch {
      return false;
    }
  }
}
