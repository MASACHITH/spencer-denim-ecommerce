import { encodeQr, qrSvgPath, type QrMatrix } from './qr-code';

const uri =
  'otpauth://totp/Spencer%20Denim%20Admin:admin%40example.com?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=Spencer%20Denim%20Admin&digits=6';

/** FNV-1a over the modules, row by row: a compact fingerprint of a whole matrix. */
function fingerprint(matrix: QrMatrix): string {
  let hash = 0x811c9dc5;
  for (const row of matrix.dark) {
    for (const isDark of row) {
      hash ^= isDark ? 1 : 0;
      hash = Math.imul(hash, 0x01000193) >>> 0;
    }
  }
  return hash.toString(16);
}

function expectFinderAt(matrix: QrMatrix, left: number, top: number): void {
  for (let dy = 0; dy < 7; dy++) {
    for (let dx = 0; dx < 7; dx++) {
      const ring = Math.max(Math.abs(dx - 3), Math.abs(dy - 3));
      expect(matrix.dark[top + dy][left + dx]).toBe(ring !== 2);
    }
  }
}

/** Reads the first copy of the 15 format bits and undoes the fixed XOR mask. */
function formatBits(matrix: QrMatrix): number {
  const positions: [number, number][] = [
    [8, 0], [8, 1], [8, 2], [8, 3], [8, 4], [8, 5], [8, 7], [8, 8], [7, 8], [5, 8], [4, 8], [3, 8], [2, 8], [1, 8], [0, 8],
  ];
  const bits = positions.reduce((value, [x, y], i) => value | ((matrix.dark[y][x] ? 1 : 0) << i), 0);
  return bits ^ 0x5412;
}

describe('encodeQr', () => {
  it('picks the smallest version that fits', () => {
    expect(encodeQr('a').size).toBe(21); // version 1
    expect(encodeQr('x'.repeat(14)).size).toBe(21); // version 1-M holds 14 bytes
    expect(encodeQr('x'.repeat(15)).size).toBe(25); // version 2
    expect(encodeQr(uri).size).toBe(49); // version 8 for a typical enrolment URI
  });

  it('draws the three finder patterns, the timing lines and the dark module', () => {
    const matrix = encodeQr(uri);
    const last = matrix.size - 7;

    expectFinderAt(matrix, 0, 0);
    expectFinderAt(matrix, last, 0);
    expectFinderAt(matrix, 0, last);
    for (let i = 8; i < matrix.size - 8; i++) {
      expect(matrix.dark[6][i]).toBe(i % 2 === 0);
      expect(matrix.dark[i][6]).toBe(i % 2 === 0);
    }
    expect(matrix.dark[matrix.size - 8][8]).toBe(true);
  });

  it('writes format information for error-correction level M with a valid mask number', () => {
    const format = formatBits(encodeQr(uri));

    expect(format >>> 13).toBe(0); // level M
    expect((format >>> 10) & 0b111).toBeLessThan(8);
  });

  it('matches the matrix an independent decoder (zxing-cpp) read back as the same URI', () => {
    // Recorded when the encoder was checked against zxing-cpp for 26 inputs from 1 to 2,331 bytes, versions
    // 1 to 40: a change to this fingerprint means the output changed and needs checking with a real scanner.
    expect(fingerprint(encodeQr(uri))).toBe('2d4bfc23');
  });

  it('encodes text as UTF-8', () => {
    expect(encodeQr('é').size).toBe(21);
    expect(fingerprint(encodeQr('é'))).not.toBe(fingerprint(encodeQr('e')));
  });

  it('refuses text too long for any QR code', () => {
    expect(() => encodeQr('x'.repeat(2400))).toThrow(RangeError);
  });
});

describe('qrSvgPath', () => {
  it('draws one unit square per dark module, offset by the quiet zone', () => {
    const matrix: QrMatrix = { size: 2, dark: [[true, false], [false, true]] };

    expect(qrSvgPath(matrix, 4)).toBe('M4,4h1v1h-1zM5,5h1v1h-1z');
  });
});
