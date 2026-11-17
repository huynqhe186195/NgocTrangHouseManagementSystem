import { describe, expect, it } from 'vitest';
import { roundedPolyline, shouldCaption } from './planDrawing';

describe('shouldCaption', () => {
  it('leaves a bent wall unlabelled', () => {
    // Its box is the extent of the centreline, so a caption would sit in the
    // middle of the floor rather than on the wall.
    expect(
      shouldCaption(
        [
          { x: 0, y: 0 },
          { x: 5, y: 0 },
          { x: 5, y: 4 },
        ],
        12,
      ),
    ).toBe(false);
  });

  it('still captions a box-only wall and the other architecture', () => {
    expect(shouldCaption(null, 12)).toBe(true);
    expect(shouldCaption(null, 2)).toBe(true);
    expect(shouldCaption(null, 1)).toBe(true);
  });

  it('leaves furniture unlabelled whichever way it was stored', () => {
    expect(shouldCaption(null, 13)).toBe(false);
    expect(
      shouldCaption(
        [
          { x: 0, y: 0 },
          { x: 2, y: 0 },
        ],
        21,
      ),
    ).toBe(false);
  });
});

describe('roundedPolyline', () => {
  it('draws a sharp polyline when the radius is zero', () => {
    expect(
      roundedPolyline(
        [
          { x: 0, y: 0 },
          { x: 4, y: 0 },
          { x: 4, y: 3 },
        ],
        0,
      ),
    ).toBe('M 0 0 L 4 0 L 4 3');
  });

  it('treats a negative or non-finite radius as sharp', () => {
    const sharp = 'M 0 0 L 4 0 L 4 3';
    expect(roundedPolyline([{ x: 0, y: 0 }, { x: 4, y: 0 }, { x: 4, y: 3 }], -1)).toBe(sharp);
    expect(roundedPolyline([{ x: 0, y: 0 }, { x: 4, y: 0 }, { x: 4, y: 3 }], Number.NaN)).toBe(
      sharp,
    );
  });

  it('replaces a corner with an arc when given a radius', () => {
    const d = roundedPolyline(
      [
        { x: 0, y: 0 },
        { x: 4, y: 0 },
        { x: 4, y: 3 },
      ],
      1,
    );
    // The corner (4,0) becomes (3,0) -> arc -> (4,1), bulging towards (3,1).
    expect(d).toBe('M 0 0 L 3 0 A 1 1 0 0 1 4 1 L 4 3');
  });

  it('turns the arc the other way for a corner that bends up', () => {
    const d = roundedPolyline(
      [
        { x: 0, y: 0 },
        { x: 4, y: 0 },
        { x: 4, y: -3 },
      ],
      1,
    );
    expect(d).toContain('A 1 1 0 0 0 4 -1');
  });

  it('clamps the fillet to half the shorter leg so it cannot swallow the segment', () => {
    // Both legs are 2 m; a 5 m radius must shrink to 1 m.
    const d = roundedPolyline(
      [
        { x: 0, y: 0 },
        { x: 2, y: 0 },
        { x: 2, y: 2 },
      ],
      5,
    );
    expect(d).toBe('M 0 0 L 1 0 A 1 1 0 0 1 2 1 L 2 2');
  });

  it('does not arc a vertex that sits on a straight run', () => {
    const d = roundedPolyline(
      [
        { x: 0, y: 0 },
        { x: 2, y: 0 },
        { x: 4, y: 0 },
      ],
      0.5,
    );
    expect(d).not.toContain('A');
    expect(d).toBe('M 0 0 L 1.5 0 L 2.5 0 L 4 0');
  });

  it('leaves the end points unrounded', () => {
    const d = roundedPolyline(
      [
        { x: 0, y: 0 },
        { x: 4, y: 0 },
        { x: 4, y: 3 },
      ],
      1,
    );
    expect(d.startsWith('M 0 0')).toBe(true);
    expect(d.endsWith('L 4 3')).toBe(true);
  });

  it('returns an empty path for no points and a bare move for one', () => {
    expect(roundedPolyline([], 1)).toBe('');
    expect(roundedPolyline([{ x: 2, y: 3 }], 1)).toBe('M 2 3');
  });

  it('falls back to a plain segment when two vertices coincide', () => {
    const d = roundedPolyline(
      [
        { x: 1, y: 1 },
        { x: 1, y: 1 },
        { x: 3, y: 1 },
      ],
      0.5,
    );
    expect(d).toBe('M 1 1 L 1 1 L 3 1');
  });
});
