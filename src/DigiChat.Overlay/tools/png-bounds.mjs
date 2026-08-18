// Minimal PNG reader: canvas size plus the bounding box of visible pixels.
//
// Two PNGs with the same canvas rarely hold artwork of the same size — one may
// be padded far more than the other. Sizing off the canvas therefore makes a
// heavily padded form render small and float above the ground. The importer
// measures the alpha bounds instead, so the overlay can normalise what is
// actually *visible*.
//
// Supports 8/16-bit greyscale+alpha and RGBA, and 8-bit palette with tRNS.
// Anything else (interlaced, sub-byte palettes, no alpha channel at all) is
// reported as fully opaque, which degrades to the old canvas-based behaviour.
import { readFileSync, statSync } from "node:fs";
import { inflateSync } from "node:zlib";

/** Alpha at or below this counts as transparent; ignores soft-edge fringing. */
const ALPHA_THRESHOLD = 8;
const MAX_FILE_BYTES = 64 * 1024 * 1024;
const MAX_DIMENSION = 16384;
// The decoder holds both filtered and reconstructed scanlines at once.
const MAX_SCANLINE_BYTES = 128 * 1024 * 1024;

const CHANNELS = { 0: 1, 2: 3, 3: 1, 4: 2, 6: 4 };

export function readPng(file) {
  const fileBytes = statSync(file).size;
  if (fileBytes > MAX_FILE_BYTES)
    throw new Error(`PNG exceeds ${MAX_FILE_BYTES / 1024 / 1024} MiB safety limit`);
  const buf = readFileSync(file);
  if (buf.length < 24 || buf.readUInt32BE(0) !== 0x89504e47)
    throw new Error("not a PNG (or truncated)");

  let width = 0, height = 0, bitDepth = 8, colorType = 6, interlace = 0;
  const idat = [];
  let trns = null;

  for (let p = 8; p + 8 <= buf.length; ) {
    const len = buf.readUInt32BE(p);
    if (len > buf.length - p - 12)
      throw new Error("PNG chunk length exceeds file bounds");
    const type = buf.toString("ascii", p + 4, p + 8);
    const data = buf.subarray(p + 8, p + 8 + len);
    if (type === "IHDR") {
      if (len !== 13) throw new Error("invalid PNG IHDR length");
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      bitDepth = data[8];
      colorType = data[9];
      interlace = data[12];
    } else if (type === "IDAT") idat.push(data);
    else if (type === "tRNS") trns = Buffer.from(data);
    else if (type === "IEND") break;
    p += 12 + len;
  }

  const canvas = { width, height };
  if (width < 1 || height < 1 || width > MAX_DIMENSION || height > MAX_DIMENSION)
    throw new Error(`PNG dimensions must be between 1 and ${MAX_DIMENSION} pixels`);
  const full = { visible: { x: 0, y: 0, width, height }, canvas, measured: false };

  const channels = CHANNELS[colorType];
  const hasAlpha = colorType === 4 || colorType === 6 || (colorType === 3 && trns);
  if (interlace !== 0 || !channels || !hasAlpha) return full;
  if (bitDepth !== 8 && !(bitDepth === 16 && colorType !== 3)) return full;

  const bytesPerSample = bitDepth === 16 ? 2 : 1;
  const bpp = channels * bytesPerSample;
  const stride = width * bpp;
  const expectedBytes = (stride + 1) * height;
  if (!Number.isSafeInteger(expectedBytes) || expectedBytes > MAX_SCANLINE_BYTES)
    throw new Error("decoded PNG scanlines exceed 128 MiB safety limit");
  const raw = inflateSync(Buffer.concat(idat), { maxOutputLength: expectedBytes });
  if (raw.length < expectedBytes) return full;

  // Reverse the per-scanline filters (PNG spec §9.2).
  const out = Buffer.alloc(stride * height);
  for (let y = 0, src = 0; y < height; y++) {
    const filter = raw[src++];
    const line = y * stride;
    const prev = line - stride;
    for (let i = 0; i < stride; i++) {
      const x = raw[src++];
      const a = i >= bpp ? out[line + i - bpp] : 0;
      const b = y > 0 ? out[prev + i] : 0;
      const c = y > 0 && i >= bpp ? out[prev + i - bpp] : 0;
      let value;
      switch (filter) {
        case 0: value = x; break;
        case 1: value = x + a; break;
        case 2: value = x + b; break;
        case 3: value = x + ((a + b) >> 1); break;
        case 4: {
          const pa = Math.abs(b - c), pb = Math.abs(a - c), pc = Math.abs(a + b - 2 * c);
          value = x + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
          break;
        }
        default: return full; // unknown filter: do not guess
      }
      out[line + i] = value & 0xff;
    }
  }

  const alphaAt = (line, x) => {
    if (colorType === 3) {
      const index = out[line + x];
      return index < trns.length ? trns[index] : 255;
    }
    // Alpha is the last sample of the pixel; for 16-bit take its high byte.
    return out[line + x * bpp + bpp - bytesPerSample];
  };

  let minX = width, minY = height, maxX = -1, maxY = -1;
  for (let y = 0; y < height; y++) {
    const line = y * stride;
    for (let x = 0; x < width; x++) {
      if (alphaAt(line, x) <= ALPHA_THRESHOLD) continue;
      if (x < minX) minX = x;
      if (x > maxX) maxX = x;
      if (y < minY) minY = y;
      if (y > maxY) maxY = y;
    }
  }

  if (maxX < 0) return { ...full, empty: true }; // fully transparent
  return {
    canvas,
    measured: true,
    visible: { x: minX, y: minY, width: maxX - minX + 1, height: maxY - minY + 1 },
  };
}
