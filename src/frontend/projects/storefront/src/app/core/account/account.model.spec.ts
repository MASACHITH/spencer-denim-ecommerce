import {
  ACCOUNT_FIELD_LENGTHS,
  EMPTY_ORDER_PAGE,
  SAVED_ADDRESS_LIMIT,
  accountDate,
  blankToNull,
  customerName,
} from './account.model';

/**
 * The account payloads' helpers.
 *
 * The interfaces themselves are compile-time only and have nothing to assert. What is worth pinning is
 * the handful of values and functions the forms and the order list depend on, and in particular the two
 * places a number crosses the HTTP boundary by necessity: the field widths and the address cap.
 */
describe('the account model (ACC-01, ACC-02)', () => {
  describe('blankToNull, which decides what an emptied box means', () => {
    /**
     * The server's optional-field validator trims and reads blank as absent, so `''` would store an
     * empty string in a nullable column: a shopper who cleared their phone number would have "no phone"
     * stored as something rather than as nothing.
     */
    it('reads an empty or whitespace-only box as nothing at all', () => {
      expect(blankToNull('')).toBeNull();
      expect(blankToNull('   ')).toBeNull();
      expect(blankToNull('\t\n ')).toBeNull();
    });

    it('trims what was typed, because the server does too', () => {
      expect(blankToNull('  Perera  ')).toBe('Perera');
      expect(blankToNull('Perera')).toBe('Perera');
    });
  });

  describe('accountDate, which is what a shopper reads on an order row', () => {
    /**
     * `en-GB` with a long month, for `content-page.ts`'s reason: "7/10/2026" is ambiguous to half the
     * world, and an order date is one a shopper checks against a bank statement.
     */
    it('writes a long unambiguous date', () => {
      expect(accountDate('2026-10-07T04:30:00Z')).toBe('7 October 2026');
    });

    /** A value the API could not produce renders nothing rather than the string "Invalid Date". */
    it('renders nothing rather than Invalid Date for something it cannot read', () => {
      expect(accountDate('not a date')).toBeNull();
      expect(accountDate('')).toBeNull();
      expect(accountDate(null)).toBeNull();
      expect(accountDate(undefined)).toBeNull();
    });
  });

  describe('customerName', () => {
    it('joins the two names when there are two', () => {
      expect(
        customerName({
          userId: 1,
          email: 'x',
          firstName: 'Nimal',
          lastName: 'Perera',
          emailConfirmed: true,
        }),
      ).toBe('Nimal Perera');
    });

    /** The server's `lastName` is optional (doc 11 P11 step A.1 asks for one name). */
    it('uses the first name alone when there is no last name', () => {
      expect(
        customerName({
          userId: 1,
          email: 'x',
          firstName: 'Nimal',
          lastName: null,
          emailConfirmed: true,
        }),
      ).toBe('Nimal');
    });
  });

  /**
   * **The field widths, which are the one place #126's defect could come back.**
   *
   * #126 shipped a `maxlength` of 120 against a 100-character column: the form, the stub and sixteen
   * browser tests all passed, and a shopper typing 110 characters was refused by the server with no
   * warning. The browser cannot read a C# constant, so these numbers are duplicated across the HTTP
   * boundary by necessity — which makes one object that one test checks strictly better than eight
   * `maxlength` attributes nobody sweeps.
   *
   * **Each expectation below names the server constant it mirrors.** A C# test cannot be run from here,
   * so this is a transcription check and is honest about being one: it fails the moment somebody edits a
   * number in `account.model.ts` without the pair, and it would not notice the server changing first.
   * `BrandNameConventionTests`-style sweeping of these across the boundary is a backend task.
   */
  describe('the field widths, against the columns they mirror', () => {
    it('matches the server constants, name by name', () => {
      // CustomerAccountValidator.NameMaxLength (identity.Users.FirstName/LastName, nvarchar(80))
      expect(ACCOUNT_FIELD_LENGTHS.name).toBe(80);
      // NewsletterEmail.MaxLength, used by ValidateSignIn and ValidateRegistration
      expect(ACCOUNT_FIELD_LENGTHS.email).toBe(256);
      // CustomerAccountValidator.PasswordMaxLength
      expect(ACCOUNT_FIELD_LENGTHS.password).toBe(256);
      // CustomerAddress.PhoneMaxLength
      expect(ACCOUNT_FIELD_LENGTHS.phone).toBe(20);
      // CustomerAddress.LabelMaxLength
      expect(ACCOUNT_FIELD_LENGTHS.label).toBe(40);
      // CustomerAddress.RecipientNameMaxLength
      expect(ACCOUNT_FIELD_LENGTHS.recipientName).toBe(120);
      // CustomerAddress.LineMaxLength, for line 1 and line 2 alike
      expect(ACCOUNT_FIELD_LENGTHS.line).toBe(150);
      // CustomerAddress.CityMaxLength
      expect(ACCOUNT_FIELD_LENGTHS.city).toBe(80);
      // CustomerAddress.PostalCodeMaxLength
      expect(ACCOUNT_FIELD_LENGTHS.postalCode).toBe(10);
      // CustomerAddress.LandmarkMaxLength
      expect(ACCOUNT_FIELD_LENGTHS.landmark).toBe(150);
    });

    /** Doc 08 §3.1's app cap, restated in doc 11 P11 step 11: `CustomerAddress.MaxPerCustomer`. */
    it('says ten saved addresses, as the server does', () => {
      expect(SAVED_ADDRESS_LIMIT).toBe(10);
    });
  });

  /**
   * **`pageSize: 0` and not 10, deliberately.** A page size written here would be a second copy of
   * `GetCustomerOrdersQueryHandler.DefaultPageSize`, which is the shape of #126. The server decides the
   * size and echoes it back, and `OrdersPage.totalPages` draws no pager until it has one — which is why
   * this has to be a value that cannot be mistaken for an answer.
   */
  it('starts the order history with no page size of its own', () => {
    expect(EMPTY_ORDER_PAGE.pageSize).toBe(0);
    expect(EMPTY_ORDER_PAGE.orders).toEqual([]);
    expect(EMPTY_ORDER_PAGE.totalCount).toBe(0);
    expect(EMPTY_ORDER_PAGE.hasMore).toBe(false);
  });
});
