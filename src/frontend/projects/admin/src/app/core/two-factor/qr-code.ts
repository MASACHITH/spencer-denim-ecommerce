/**
 * A small QR code encoder (ISO/IEC 18004) for the 2FA enrolment page (W1-10): byte mode, error-correction level
 * M, the smallest version (1–40) that fits, and the mask with the lowest penalty score. It exists so the
 * authenticator key never leaves the browser for an external QR service and no npm package is needed for one
 * picture. It follows the structure of Project Nayuki's MIT-licensed reference encoder.
 */
export interface QrMatrix {
  /** Modules per side, without the quiet zone. */
  readonly size: number;
  /** `dark[y][x]`: true for a dark module. */
  readonly dark: readonly (readonly boolean[])[];
}

/** Error-correction codewords per block for level M, indexed by version (index 0 unused). */
const eccCodewordsPerBlock = [
  -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28,
  28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28,
];

/** Error-correction blocks for level M, indexed by version (index 0 unused). */
const errorCorrectionBlocks = [
  -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31,
  33, 35, 37, 38, 40, 43, 45, 47, 49,
];

/** Level M's two format bits. */
const eccFormatBits = 0;

/** Encodes `text` as UTF-8. Throws when it is too long for any QR version (about 2,300 bytes at level M). */
export function encodeQr(text: string): QrMatrix {
  const bytes = Array.from(new TextEncoder().encode(text));
  const version = smallestVersionFor(bytes.length);
  const codewords = addErrorCorrection(dataCodewords(bytes, version), version);
  return new QrBuilder(version).build(codewords);
}

/** An SVG path (`d`) with one unit square per dark module, offset by the quiet zone. */
export function qrSvgPath(matrix: QrMatrix, quietZone = 4): string {
  const parts: string[] = [];
  matrix.dark.forEach((row, y) =>
    row.forEach((isDark, x) => {
      if (isDark) {
        parts.push(`M${x + quietZone},${y + quietZone}h1v1h-1z`);
      }
    }),
  );
  return parts.join('');
}

function characterCountBits(version: number): number {
  return version <= 9 ? 8 : 16;
}

function rawDataModules(version: number): number {
  let result = (16 * version + 128) * version + 64;
  if (version >= 2) {
    const alignmentCount = Math.floor(version / 7) + 2;
    result -= (25 * alignmentCount - 10) * alignmentCount - 55;
    if (version >= 7) {
      result -= 36;
    }
  }
  return result;
}

function dataCodewordCount(version: number): number {
  return Math.floor(rawDataModules(version) / 8) - eccCodewordsPerBlock[version] * errorCorrectionBlocks[version];
}

function smallestVersionFor(byteCount: number): number {
  for (let version = 1; version <= 40; version++) {
    const bitsNeeded = 4 + characterCountBits(version) + byteCount * 8;
    if (byteCount < 1 << characterCountBits(version) && bitsNeeded <= dataCodewordCount(version) * 8) {
      return version;
    }
  }
  throw new RangeError('The text is too long for a QR code.');
}

/** Mode indicator, character count, the bytes, terminator and padding, as codewords. */
function dataCodewords(bytes: number[], version: number): number[] {
  const bits: number[] = [];
  const append = (value: number, length: number) => {
    for (let i = length - 1; i >= 0; i--) {
      bits.push((value >>> i) & 1);
    }
  };

  append(0b0100, 4);
  append(bytes.length, characterCountBits(version));
  bytes.forEach((b) => append(b, 8));

  const capacity = dataCodewordCount(version) * 8;
  append(0, Math.min(4, capacity - bits.length));
  append(0, (8 - (bits.length % 8)) % 8);
  for (let pad = 0xec; bits.length < capacity; pad ^= 0xec ^ 0x11) {
    append(pad, 8);
  }

  const codewords: number[] = [];
  for (let i = 0; i < bits.length; i += 8) {
    codewords.push(bits.slice(i, i + 8).reduce((acc, bit) => (acc << 1) | bit, 0));
  }
  return codewords;
}

/** Splits the data into blocks, appends each block's Reed–Solomon codewords, and interleaves them. */
function addErrorCorrection(data: number[], version: number): number[] {
  const blockCount = errorCorrectionBlocks[version];
  const eccLength = eccCodewordsPerBlock[version];
  const rawCodewords = Math.floor(rawDataModules(version) / 8);
  const shortBlockCount = blockCount - (rawCodewords % blockCount);
  const shortBlockLength = Math.floor(rawCodewords / blockCount);

  const divisor = reedSolomonDivisor(eccLength);
  const blocks: number[][] = [];
  for (let i = 0, k = 0; i < blockCount; i++) {
    const blockData = data.slice(k, k + shortBlockLength - eccLength + (i < shortBlockCount ? 0 : 1));
    k += blockData.length;
    const ecc = reedSolomonRemainder(blockData, divisor);
    if (i < shortBlockCount) {
      blockData.push(0);
    }
    blocks.push([...blockData, ...ecc]);
  }

  const result: number[] = [];
  for (let i = 0; i < blocks[0].length; i++) {
    blocks.forEach((block, j) => {
      // The padding byte added to short blocks above is skipped here.
      if (i !== shortBlockLength - eccLength || j >= shortBlockCount) {
        result.push(block[i]);
      }
    });
  }
  return result;
}

function reedSolomonDivisor(degree: number): number[] {
  const result = new Array<number>(degree).fill(0);
  result[degree - 1] = 1;
  let root = 1;
  for (let i = 0; i < degree; i++) {
    for (let j = 0; j < result.length; j++) {
      result[j] = gfMultiply(result[j], root);
      if (j + 1 < result.length) {
        result[j] ^= result[j + 1];
      }
    }
    root = gfMultiply(root, 0x02);
  }
  return result;
}

function reedSolomonRemainder(data: number[], divisor: number[]): number[] {
  const result = divisor.map(() => 0);
  for (const b of data) {
    const factor = b ^ (result.shift() as number);
    result.push(0);
    divisor.forEach((coefficient, i) => (result[i] ^= gfMultiply(coefficient, factor)));
  }
  return result;
}

/** Multiplication in GF(2^8) modulo x^8 + x^4 + x^3 + x^2 + 1. */
function gfMultiply(x: number, y: number): number {
  let z = 0;
  for (let i = 7; i >= 0; i--) {
    z = (z << 1) ^ ((z >>> 7) * 0x11d);
    z ^= ((y >>> i) & 1) * x;
  }
  return z & 0xff;
}

function bit(value: number, index: number): boolean {
  return ((value >>> index) & 1) !== 0;
}

class QrBuilder {
  private readonly version: number;
  private readonly size: number;
  private readonly modules: boolean[][];
  private readonly isFunction: boolean[][];

  constructor(version: number) {
    this.version = version;
    this.size = version * 4 + 17;
    this.modules = Array.from({ length: this.size }, () => new Array<boolean>(this.size).fill(false));
    this.isFunction = Array.from({ length: this.size }, () => new Array<boolean>(this.size).fill(false));
  }

  build(codewords: number[]): QrMatrix {
    this.drawFunctionPatterns();
    this.drawCodewords(codewords);

    let bestMask = 0;
    let bestPenalty = Number.POSITIVE_INFINITY;
    for (let mask = 0; mask < 8; mask++) {
      this.applyMask(mask);
      this.drawFormatBits(mask);
      const penalty = this.penaltyScore();
      if (penalty < bestPenalty) {
        bestMask = mask;
        bestPenalty = penalty;
      }
      this.applyMask(mask); // XOR again to undo.
    }

    this.applyMask(bestMask);
    this.drawFormatBits(bestMask);
    return { size: this.size, dark: this.modules };
  }

  private setFunction(x: number, y: number, isDark: boolean): void {
    this.modules[y][x] = isDark;
    this.isFunction[y][x] = true;
  }

  private drawFunctionPatterns(): void {
    for (let i = 0; i < this.size; i++) {
      this.setFunction(6, i, i % 2 === 0);
      this.setFunction(i, 6, i % 2 === 0);
    }

    this.drawFinder(3, 3);
    this.drawFinder(this.size - 4, 3);
    this.drawFinder(3, this.size - 4);

    const positions = this.alignmentPositions();
    const last = positions.length - 1;
    positions.forEach((py, i) =>
      positions.forEach((px, j) => {
        const overlapsFinder = (i === 0 && j === 0) || (i === 0 && j === last) || (i === last && j === 0);
        if (!overlapsFinder) {
          this.drawAlignment(px, py);
        }
      }),
    );

    this.drawFormatBits(0); // Reserved now, overwritten once the mask is chosen.
    this.drawVersion();
  }

  private drawFinder(cx: number, cy: number): void {
    for (let dy = -4; dy <= 4; dy++) {
      for (let dx = -4; dx <= 4; dx++) {
        const distance = Math.max(Math.abs(dx), Math.abs(dy));
        const x = cx + dx;
        const y = cy + dy;
        if (x >= 0 && x < this.size && y >= 0 && y < this.size) {
          this.setFunction(x, y, distance !== 2 && distance !== 4);
        }
      }
    }
  }

  private drawAlignment(cx: number, cy: number): void {
    for (let dy = -2; dy <= 2; dy++) {
      for (let dx = -2; dx <= 2; dx++) {
        this.setFunction(cx + dx, cy + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
      }
    }
  }

  private alignmentPositions(): number[] {
    if (this.version === 1) {
      return [];
    }
    const count = Math.floor(this.version / 7) + 2;
    const step = Math.floor((this.version * 8 + count * 3 + 5) / (count * 4 - 4)) * 2;
    const result = [6];
    for (let position = this.size - 7; result.length < count; position -= step) {
      result.splice(1, 0, position);
    }
    return result;
  }

  private drawFormatBits(mask: number): void {
    const data = (eccFormatBits << 3) | mask;
    let remainder = data;
    for (let i = 0; i < 10; i++) {
      remainder = (remainder << 1) ^ ((remainder >>> 9) * 0x537);
    }
    const bits = ((data << 10) | remainder) ^ 0x5412;

    for (let i = 0; i <= 5; i++) {
      this.setFunction(8, i, bit(bits, i));
    }
    this.setFunction(8, 7, bit(bits, 6));
    this.setFunction(8, 8, bit(bits, 7));
    this.setFunction(7, 8, bit(bits, 8));
    for (let i = 9; i < 15; i++) {
      this.setFunction(14 - i, 8, bit(bits, i));
    }

    for (let i = 0; i < 8; i++) {
      this.setFunction(this.size - 1 - i, 8, bit(bits, i));
    }
    for (let i = 8; i < 15; i++) {
      this.setFunction(8, this.size - 15 + i, bit(bits, i));
    }
    this.setFunction(8, this.size - 8, true); // The dark module.
  }

  private drawVersion(): void {
    if (this.version < 7) {
      return;
    }
    let remainder = this.version;
    for (let i = 0; i < 12; i++) {
      remainder = (remainder << 1) ^ ((remainder >>> 11) * 0x1f25);
    }
    const bits = (this.version << 12) | remainder;
    for (let i = 0; i < 18; i++) {
      const isDark = bit(bits, i);
      const a = this.size - 11 + (i % 3);
      const b = Math.floor(i / 3);
      this.setFunction(a, b, isDark);
      this.setFunction(b, a, isDark);
    }
  }

  /** Places the codewords in the zig-zag order, two columns at a time, skipping the vertical timing column. */
  private drawCodewords(codewords: number[]): void {
    let i = 0;
    for (let right = this.size - 1; right >= 1; right -= 2) {
      if (right === 6) {
        right = 5;
      }
      for (let vertical = 0; vertical < this.size; vertical++) {
        for (let j = 0; j < 2; j++) {
          const x = right - j;
          const upward = ((right + 1) & 2) === 0;
          const y = upward ? this.size - 1 - vertical : vertical;
          if (!this.isFunction[y][x] && i < codewords.length * 8) {
            this.modules[y][x] = bit(codewords[i >>> 3], 7 - (i & 7));
            i++;
          }
        }
      }
    }
  }

  private applyMask(mask: number): void {
    for (let y = 0; y < this.size; y++) {
      for (let x = 0; x < this.size; x++) {
        if (!this.isFunction[y][x] && maskApplies(mask, x, y)) {
          this.modules[y][x] = !this.modules[y][x];
        }
      }
    }
  }

  private penaltyScore(): number {
    let result = 0;

    for (let y = 0; y < this.size; y++) {
      result += this.linePenalty((i) => this.modules[y][i]);
    }
    for (let x = 0; x < this.size; x++) {
      result += this.linePenalty((i) => this.modules[i][x]);
    }

    for (let y = 0; y < this.size - 1; y++) {
      for (let x = 0; x < this.size - 1; x++) {
        const colour = this.modules[y][x];
        if (colour === this.modules[y][x + 1] && colour === this.modules[y + 1][x] && colour === this.modules[y + 1][x + 1]) {
          result += 3;
        }
      }
    }

    const dark = this.modules.reduce((sum, row) => sum + row.filter(Boolean).length, 0);
    const total = this.size * this.size;
    result += (Math.ceil(Math.abs(dark * 20 - total * 10) / total) - 1) * 10;
    return result;
  }

  /** Rule 1 (runs of five or more) and rule 3 (finder-like 1:1:3:1:1 patterns) along one row or column. */
  private linePenalty(at: (i: number) => boolean): number {
    let result = 0;
    let runColour = false;
    let runLength = 0;
    const history = [0, 0, 0, 0, 0, 0, 0];

    for (let i = 0; i < this.size; i++) {
      if (at(i) === runColour) {
        runLength++;
        if (runLength === 5) {
          result += 3;
        } else if (runLength > 5) {
          result++;
        }
      } else {
        this.addHistory(runLength, history);
        if (!runColour) {
          result += this.finderLikePatterns(history) * 40;
        }
        runColour = at(i);
        runLength = 1;
      }
    }

    if (runColour) {
      this.addHistory(runLength, history);
      runLength = 0;
    }
    runLength += this.size;
    this.addHistory(runLength, history);
    return result + this.finderLikePatterns(history) * 40;
  }

  private addHistory(runLength: number, history: number[]): void {
    if (history[0] === 0) {
      runLength += this.size; // The light border before the line.
    }
    history.pop();
    history.unshift(runLength);
  }

  private finderLikePatterns(history: number[]): number {
    const n = history[1];
    const core = n > 0 && history[2] === n && history[3] === n * 3 && history[4] === n && history[5] === n;
    return (core && history[0] >= n * 4 && history[6] >= n ? 1 : 0) + (core && history[6] >= n * 4 && history[0] >= n ? 1 : 0);
  }
}

function maskApplies(mask: number, x: number, y: number): boolean {
  switch (mask) {
    case 0:
      return (x + y) % 2 === 0;
    case 1:
      return y % 2 === 0;
    case 2:
      return x % 3 === 0;
    case 3:
      return (x + y) % 3 === 0;
    case 4:
      return (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0;
    case 5:
      return ((x * y) % 2) + ((x * y) % 3) === 0;
    case 6:
      return (((x * y) % 2) + ((x * y) % 3)) % 2 === 0;
    default:
      return (((x + y) % 2) + ((x * y) % 3)) % 2 === 0;
  }
}
