import { Route } from '@angular/router';

import { routes } from './app.routes';

/**
 * Tester pass (#215): every admin page title is what the browser tab shows. The mega-menu route shipped as
 * "Mega menu cards â€” …" — an em dash saved through the wrong code page. A title carrying the tell-tale
 * UTF-8-read-as-Windows-1252 sequences is a build failure, not a tab a client sees.
 */
function titles(list: readonly Route[]): string[] {
  return list.flatMap((route) => [
    ...(typeof route.title === 'string' ? [route.title] : []),
    ...titles(route.children ?? []),
  ]);
}

describe('admin route titles', () => {
  it('carry no mis-encoded characters', () => {
    const broken = titles(routes).filter((title) => /â€|Ã|Â/.test(title));

    expect(broken).toEqual([]);
  });

  it('name the mega menu page with a real em dash', () => {
    expect(titles(routes).some((title) => title.startsWith('Mega menu cards — '))).toBe(true);
  });
});
