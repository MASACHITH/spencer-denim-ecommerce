import { daysProblem, feeProblem, numberOrNull, refusalText } from './overseas-delivery.model';

/** The client-side mirror of `OverseasDeliveryRules`: never looser than the server. */
describe('overseas delivery rules', () => {
  it.each([
    ['0', null],
    ['9500', null],
    ['9500.5', null],
    ['9500.55', null],
    ['1000000', null],
    ['1000000.01', 'Rs. 1,000,000'],
    ['10.555', 'two decimal places'],
    ['-1', 'two decimal places'],
    ['abc', 'two decimal places'],
  ])('fee %s', (text, expected) => {
    const problem = feeProblem(text, false);
    if (expected === null) {
      expect(problem).toBeNull();
    } else {
      expect(problem).toContain(expected);
    }
  });

  it('allows a blank fee only where blank means something', () => {
    expect(feeProblem('  ', true)).toBeNull();
    expect(feeProblem('', false)).toBe('Enter a fee.');
  });

  it.each([
    ['1', '90', true, null],
    ['7', '7', true, null],
    ['0', '5', true, 'from 1 to 90'],
    ['5', '91', true, 'from 1 to 90'],
    ['9', '3', true, 'fewest no more than the most'],
    ['4', '', true, 'or neither'],
    ['', '4', true, 'or neither'],
    ['1.5', '4', true, 'whole days'],
    ['', '', true, null],
  ])('days %s–%s', (min, max, allowBlank, expected) => {
    const problem = daysProblem(min, max, allowBlank);
    if (expected === null) {
      expect(problem).toBeNull();
    } else {
      expect(problem).toContain(expected);
    }
  });

  it('requires days on a region', () => {
    expect(daysProblem('', '', false)).toContain('Enter the estimated delivery days');
  });

  it('turns a blank box into null and a number into a number', () => {
    expect(numberOrNull(' ')).toBeNull();
    expect(numberOrNull('12.5')).toBe(12.5);
  });

  it('words every refusal', () => {
    expect(refusalText('MethodDisabled')).toContain('International delivery');
    expect(refusalText('RegionDisabled')).toContain('region');
    expect(refusalText('NotPriced')).toContain('No fee');
    expect(refusalText('CountryDisabled')).toContain('country');
    expect(refusalText('Something new')).toBe('Not available');
  });
});
