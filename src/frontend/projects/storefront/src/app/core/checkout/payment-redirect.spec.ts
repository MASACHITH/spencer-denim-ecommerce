import { OrderPaymentInstructionsDto, OrderPaymentRedirectFormDto } from './checkout.model';
import { paymentRedirect, safeRedirectUrl } from './payment-redirect';

function instructions(
  overrides: Partial<OrderPaymentInstructionsDto> = {},
): OrderPaymentInstructionsDto {
  return {
    providerCode: 'OnlineProvider',
    reference: null,
    amountLkr: 9_184,
    dueAtUtc: '2026-10-07T04:30:00Z',
    fields: [],
    notes: null,
    redirectUrl: null,
    acceptsProof: false,
    ...overrides,
  };
}

function form(overrides: { method?: string; action?: string } = {}): OrderPaymentRedirectFormDto {
  return {
    method: 'POST',
    action: 'https://pay.example.test/checkout',
    fields: [{ name: 'order_ref', value: 'SD-2026-000123' }],
    ...overrides,
  } as OrderPaymentRedirectFormDto;
}

// SHARED URL LIST - the same two lists, verbatim, are in the backend's
// tests/SpencerDenim.UnitTests/Application/Sales/PaymentRedirectFormTests.cs (`SafeUrls`, `UnsafeUrls`), which
// runs them through `PaymentRedirectForm.IsSafeAbsoluteUrl`; a test there fails if an entry is missing here.
// The two are one rule written in two languages, and a rule written twice drifts. Change both together.
// Two addresses are on neither list because the parsers disagree and a shared list needs one answer:
// `https:evil.example` and `https:\\evil.example\x` (no slashes, or backslashes) are accepted by this
// browser-side WHATWG parser and refused by .NET's Uri. The backend is the stricter there, which is the safe
// direction; do not "fix" it by loosening the backend.
const SHARED_SAFE_URLS: readonly string[] = [
  'https://pay.example.test/checkout',
  'https://pay.example.test/checkout?session=abc&x=1',
  '  https://pay.example.test/checkout  ',
  'HTTPS://PAY.EXAMPLE.TEST/checkout',
  'https://pay.example.test:8443/checkout',
  'https://203.0.113.7/pay',
  'http://localhost:5100/hosted',
  'http://LOCALHOST:5100/hosted',
  'http://127.0.0.1:5100/hosted',
];

const SHARED_UNSAFE_URLS: readonly string[] = [
  '',
  '   ',
  'not a url',
  'https:',
  'javascript:alert(1)',
  'JaVaScRiPt:alert(1)',
  'data:text/html,<script>alert(1)</script>',
  'vbscript:msgbox(1)',
  'file:///etc/passwd',
  'ftp://pay.example.test/checkout',
  '/pay/checkout',
  'pay/checkout',
  '//pay.example.test/checkout',
  'http://pay.example.test/checkout',
  'http://localhost.evil.example/hosted',
  'http://127.0.0.1.evil.example/hosted',
  'http://127.0.0.2/hosted',
  'http://[::1]/hosted',
  'http://[::1]:5100/hosted',
  'https://trusted.example@evil.example/pay',
  'https://good.example@evil.example/',
  'https://user:secret@pay.example.test/pay',
  'http://localhost@evil.example/hosted',
];

describe('safeRedirectUrl (the lists shared with the backend)', () => {
  it.each(SHARED_SAFE_URLS)('allows %j', (raw) => {
    expect(safeRedirectUrl(raw)).not.toBeNull();
  });

  it.each(SHARED_UNSAFE_URLS)('refuses %j', (raw) => {
    expect(safeRedirectUrl(raw)).toBeNull();
  });
});

describe('safeRedirectUrl', () => {
  it.each([
    ['https://pay.example.test/checkout', 'https://pay.example.test/checkout'],
    ['https://pay.example.test/checkout?session=abc&x=1', 'https://pay.example.test/checkout?session=abc&x=1'],
    ['  https://pay.example.test/checkout  ', 'https://pay.example.test/checkout'],
    ['HTTPS://PAY.EXAMPLE.TEST/checkout', 'https://pay.example.test/checkout'],
    ['https://pay.example.test:8443/checkout', 'https://pay.example.test:8443/checkout'],
    // Plain http, for the two hosts that mean "this machine" and nothing else.
    ['http://localhost:5100/hosted', 'http://localhost:5100/hosted'],
    ['http://127.0.0.1:5100/hosted', 'http://127.0.0.1:5100/hosted'],
  ])('allows %s', (raw, expected) => {
    expect(safeRedirectUrl(raw)).toBe(expected);
  });

  it.each<[string, string | null | undefined]>([
    ['null', null],
    ['undefined', undefined],
    ['empty', ''],
    ['blank', '   '],
    ['javascript:', 'javascript:alert(1)'],
    ['javascript: in mixed case', 'JaVaScRiPt:alert(1)'],
    ['javascript: broken up by a tab', 'java\tscript:alert(1)'],
    ['javascript: with a leading control character', '\u0001javascript:alert(1)'],
    ['data:', 'data:text/html,<script>alert(1)</script>'],
    ['vbscript:', 'vbscript:msgbox(1)'],
    ['file:', 'file:///etc/passwd'],
    ['ftp:', 'ftp://pay.example.test/checkout'],
    ['a relative path', '/pay/checkout'],
    ['a relative path with no leading slash', 'pay/checkout'],
    ['a protocol-relative url', '//pay.example.test/checkout'],
    ['plain http on a public host', 'http://pay.example.test/checkout'],
    ['plain http on a look-alike of localhost', 'http://localhost.evil.example/hosted'],
    ['plain http on a host that merely starts with 127.0.0.1', 'http://127.0.0.1.evil.example/hosted'],
    ['a user name', 'https://trusted.example@evil.example/pay'],
    ['a user name and password', 'https://user:secret@pay.example.test/pay'],
    ['text that is not a url', 'not a url'],
    ['a scheme and nothing else', 'https:'],
  ])('refuses %s', (_label, raw) => {
    expect(safeRedirectUrl(raw)).toBeNull();
  });

  it('refuses a value that is not a string at all', () => {
    expect(safeRedirectUrl(42 as unknown as string)).toBeNull();
    expect(safeRedirectUrl({} as unknown as string)).toBeNull();
  });
});

describe('paymentRedirect', () => {
  it('is null when neither a form nor a url is sent', () => {
    expect(paymentRedirect(instructions())).toBeNull();
    expect(paymentRedirect(instructions({ redirectForm: null }))).toBeNull();
    expect(paymentRedirect(instructions({ redirectUrl: '' }))).toBeNull();
    expect(paymentRedirect(instructions({ redirectUrl: '   ' }))).toBeNull();
  });

  it('is null for a bank transfer, whatever else is in the payload', () => {
    expect(
      paymentRedirect(
        instructions({
          providerCode: 'BankTransfer',
          acceptsProof: true,
          fields: [{ label: 'Bank', value: 'Example Bank PLC', copyable: false }],
        }),
      ),
    ).toBeNull();
  });

  it('describes a form, with its fields in order', () => {
    const result = paymentRedirect(
      instructions({
        redirectForm: {
          method: 'POST',
          action: 'https://pay.example.test/checkout',
          fields: [
            { name: 'b', value: '2' },
            { name: 'a', value: '1' },
            { name: 'b', value: '3' },
          ],
        },
      }),
    );

    expect(result).toEqual({
      amountLkr: 9_184,
      kind: 'form',
      action: 'https://pay.example.test/checkout',
      fields: [
        { name: 'b', value: '2' },
        { name: 'a', value: '1' },
        { name: 'b', value: '3' },
      ],
    });
  });

  it('accepts the method in any case, since a header-style "post" is still a POST', () => {
    expect(paymentRedirect(instructions({ redirectForm: form({ method: 'post' }) }))?.kind).toBe('form');
  });

  it('describes a link', () => {
    expect(
      paymentRedirect(instructions({ redirectUrl: ' https://pay.example.test/hosted ' })),
    ).toEqual({ amountLkr: 9_184, kind: 'link', href: 'https://pay.example.test/hosted' });
  });

  it('lets the form win over the url', () => {
    expect(
      paymentRedirect(
        instructions({ redirectUrl: 'https://pay.example.test/hosted', redirectForm: form() }),
      )?.kind,
    ).toBe('form');
  });

  it('does not fall back to the url when the form is refused', () => {
    // A GET to the bare address would open the provider's page without the fields it needs.
    expect(
      paymentRedirect(
        instructions({
          redirectUrl: 'https://pay.example.test/hosted',
          redirectForm: form({ action: 'javascript:alert(1)' }),
        }),
      )?.kind,
    ).toBe('unavailable');
  });

  it('refuses a form that is not a POST', () => {
    expect(paymentRedirect(instructions({ redirectForm: form({ method: 'GET' }) }))?.kind).toBe(
      'unavailable',
    );
  });

  it('is unavailable when the url is refused and there are no bank details to fall back on', () => {
    expect(paymentRedirect(instructions({ redirectUrl: 'http://evil.example/pay' }))).toEqual({
      amountLkr: 9_184,
      kind: 'unavailable',
    });
  });

  it('falls back to the bank rendering when the url is refused but bank details are real', () => {
    expect(
      paymentRedirect(instructions({ redirectUrl: 'javascript:alert(1)', acceptsProof: true })),
    ).toBeNull();
    expect(
      paymentRedirect(
        instructions({
          redirectUrl: 'javascript:alert(1)',
          fields: [{ label: 'Bank', value: 'Example Bank PLC', copyable: false }],
        }),
      ),
    ).toBeNull();
  });

  it('survives a form whose fields did not arrive', () => {
    const odd = { method: 'POST', action: 'https://pay.example.test/checkout' } as OrderPaymentRedirectFormDto;

    expect(paymentRedirect(instructions({ redirectForm: odd }))).toEqual({
      amountLkr: 9_184,
      kind: 'form',
      action: 'https://pay.example.test/checkout',
      fields: [],
    });
  });
});
