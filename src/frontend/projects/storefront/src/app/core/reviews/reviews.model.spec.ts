import { draftProblems, statusMessage, submissionMessage, toRequest } from './reviews.model';

/** The form's own checks mirror `ReviewRules` where the browser can know them (PDP-08, #189). */
describe('reviews.model', () => {
  it('needs a whole rating from 1 to 5', () => {
    expect(draftProblems({ rating: null, title: '', body: '' }).get('rating')).toContain('Choose a star rating');
    expect(draftProblems({ rating: 0, title: '', body: '' }).has('rating')).toBe(true);
    expect(draftProblems({ rating: 6, title: '', body: '' }).has('rating')).toBe(true);
    expect(draftProblems({ rating: 4.5, title: '', body: '' }).has('rating')).toBe(true);
    expect(draftProblems({ rating: 1, title: '', body: '' }).size).toBe(0);
    expect(draftProblems({ rating: 5, title: '', body: '' }).size).toBe(0);
  });

  it('holds the title to one line of 120 and the body to 4,000, after trimming', () => {
    expect(draftProblems({ rating: 5, title: 't'.repeat(120), body: 'b'.repeat(4000) }).size).toBe(0);
    expect(draftProblems({ rating: 5, title: `  ${'t'.repeat(120)}  `, body: '' }).size).toBe(0);
    expect(draftProblems({ rating: 5, title: 't'.repeat(121), body: '' }).has('title')).toBe(true);
    expect(draftProblems({ rating: 5, title: 'a\nb', body: '' }).get('title')).toBe('Write the title on one line.');
    expect(draftProblems({ rating: 5, title: '', body: 'b'.repeat(4001) }).has('body')).toBe(true);
  });

  it('sends empty text as null', () => {
    expect(toRequest({ rating: 3, title: '   ', body: '' })).toEqual({ rating: 3, title: null, body: null });
    expect(toRequest({ rating: 3, title: ' A ', body: ' B ' })).toEqual({ rating: 3, title: 'A', body: 'B' });
  });

  it('tells the customer what happened in their words', () => {
    expect(submissionMessage('Pending', false)).toContain('once we have checked it');
    expect(submissionMessage('Pending', true)).toContain('Your changes will appear');
    expect(submissionMessage('Published', false)).toContain('now on this page');
    expect(submissionMessage('StillHidden', true)).toContain('not shown on the site');
    expect(statusMessage('Approved')).toContain('shown on this page');
    expect(statusMessage('Pending')).toContain('waiting');
    expect(statusMessage('Hidden')).toContain('not shown');
  });
});
