const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

function createPng(width, height, drawFn) {
  const rawData = Buffer.alloc((width * 4 + 1) * height);

  for (let y = 0; y < height; y++) {
    const rowOffset = y * (width * 4 + 1);
    rawData[rowOffset] = 0; // Filter type: None

    for (let x = 0; x < width; x++) {
      const pixelOffset = rowOffset + 1 + x * 4;
      const [r, g, b, a] = drawFn(x, y, width, height);
      rawData[pixelOffset] = r;
      rawData[pixelOffset + 1] = g;
      rawData[pixelOffset + 2] = b;
      rawData[pixelOffset + 3] = a;
    }
  }

  const compressed = zlib.deflateSync(rawData);

  function makeChunk(type, data) {
    const len = Buffer.alloc(4);
    len.writeUInt32BE(data.length, 0);

    const typeAndData = Buffer.concat([Buffer.from(type, 'ascii'), data]);

    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(typeAndData), 0);

    return Buffer.concat([len, typeAndData, crc]);
  }

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // 8 bits per channel
  ihdr[9] = 6; // RGBA
  ihdr[10] = 0;
  ihdr[11] = 0;
  ihdr[12] = 0;

  const signature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
  return Buffer.concat([
    signature,
    makeChunk('IHDR', ihdr),
    makeChunk('IDAT', compressed),
    makeChunk('IEND', Buffer.alloc(0))
  ]);
}

function crc32(buf) {
  let table = [];
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) {
      c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
    }
    table[n] = c;
  }
  let crc = 0 ^ (-1);
  for (let i = 0; i < buf.length; i++) {
    crc = (crc >>> 8) ^ table[(crc ^ buf[i]) & 0xff];
  }
  return (crc ^ (-1)) >>> 0;
}

function drawWdmIcon(x, y, w, h) {
  const cx = w / 2;
  const cy = h / 2;
  const r = Math.sqrt((x - cx) ** 2 + (y - cy) ** 2);
  const maxR = w * 0.46;

  // Background rounded squircle / circle
  if (r > maxR) {
    return [0, 0, 0, 0];
  }

  // Cyan gradient background: top (0, 210, 255) to bottom (0, 110, 220)
  const t = y / h;
  let bgR = Math.round(15 + 10 * t);
  let bgG = Math.round(20 + 30 * t);
  let bgB = Math.round(35 + 45 * t);

  // Arrow & speed lines geometry (normalized 0..1)
  const nx = (x - cx) / (w * 0.4);
  const ny = (y - cy) / (h * 0.4);

  // Download Arrow shape
  const inStem = Math.abs(nx) <= 0.22 && ny >= -0.7 && ny <= 0.15;
  const inHead = ny > 0.05 && ny <= 0.7 && (Math.abs(nx) <= (0.7 - ny) * 0.95);

  if (inStem || inHead) {
    // Glowing cyan/white arrow
    return [0, 230, 255, 255];
  }

  // Horizontal speed/bandwidth bar at bottom
  if (ny >= 0.75 && ny <= 0.88 && Math.abs(nx) <= 0.6) {
    return [0, 180, 240, 220];
  }

  // Subtle border glow
  if (r >= maxR - 1.5) {
    return [0, 210, 255, 200];
  }

  return [bgR, bgG, bgB, 255];
}

const iconsDir = path.join(__dirname, '..', 'extension', 'icons');
fs.mkdirSync(iconsDir, { recursive: true });

[16, 48, 128].forEach(size => {
  const png = createPng(size, size, drawWdmIcon);
  fs.writeFileSync(path.join(iconsDir, `icon${size}.png`), png);
  console.log(`Generated icon${size}.png (${size}x${size})`);
});
