import { splitAtFeeMarker } from './content-page';

/** The Shipping page's fee-list marker (#33): where the live list is drawn inside the owner's copy. */
describe('splitAtFeeMarker', () => {
  it('splits at the marker paragraph and drops it', () => {
    expect(splitAtFeeMarker('<h2>A</h2><p>x</p><p>{{delivery fees}}</p><h2>B</h2>')).toEqual({
      before: '<h2>A</h2><p>x</p>',
      after: '<h2>B</h2>',
    });
  });

  it('tolerates spaces and case, and removes a second marker rather than printing it', () => {
    expect(splitAtFeeMarker('<p>a</p><p> {{ Delivery Fees }} </p><p>b</p><p>{{delivery fees}}</p>')).toEqual({
      before: '<p>a</p>',
      after: '<p>b</p>',
    });
  });

  it('puts the list after the copy when the owner removed the marker', () => {
    expect(splitAtFeeMarker('<p>a</p>')).toEqual({ before: '<p>a</p>', after: null });
  });

  it('leaves the words alone when they are part of a sentence', () => {
    expect(splitAtFeeMarker('<p>See {{delivery fees}} below</p>').after).toBeNull();
  });
});
