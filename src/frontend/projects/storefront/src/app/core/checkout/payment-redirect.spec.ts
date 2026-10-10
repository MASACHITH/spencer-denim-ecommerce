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

  // A provider that is holding a payment says so in words and offers nothing to press.
  it('is a notice when there is no redirect, nothing a bank transfer has, and a note', () => {
    expect(
      paymentRedirect(instructions({ notes: 'Your payment is pending. Please do not pay again.' })),
    ).toEqual({
      amountLkr: 9_184,
      kind: 'notice',
      notice: 'Your payment is pending. Please do not pay again.',
    });
  });

  it.each([
    ['a blank note', { notes: '   ' }],
    ['no note', { notes: null }],
    ['a reference', { notes: 'Pending.', reference: 'SD-2026-000123' }],
    [
      'bank fields',
      { notes: 'Pending.', fields: [{ label: 'Bank', value: 'Example Bank PLC', copyable: false }] },
    ],
    ['a proof upload', { notes: 'Pending.', acceptsProof: true }],
  ] satisfies [string, Partial<OrderPaymentInstructionsDto>][])(
    'is not a notice with %s',
    (_name, overrides) => {
      expect(paymentRedirect(instructions(overrides))).toBeNull();
    },
  );

  it('prefers a redirect to a note when both are sent', () => {
    expect(
      paymentRedirect(
        instructions({ notes: 'Pending.', redirectUrl: 'https://pay.example.test/checkout' }),
      ),
    ).toEqual({ amountLkr: 9_184, kind: 'link', href: 'https://pay.example.test/checkout' });
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
