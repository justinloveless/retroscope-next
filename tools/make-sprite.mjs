// Builds the committed sprites in web/assets.
//   node tools/make-sprite.mjs cards                       → cards.png + cards.json from web/assets/cards/*.png
//   node tools/make-sprite.mjs icons <lucide-static dir>   → icons.svg from an unpacked lucide-static package
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { deflateSync, inflateSync } from 'node:zlib';

const assets = join(dirname(fileURLToPath(import.meta.url)), '..', 'web', 'assets');

export const CARD_FRAMES = ['1', '2', '3', '5', '8', '13', '21', 'abstain', 'back'];
export const CARD_WIDTH = 71;
export const CARD_HEIGHT = 95;
// Transparent columns between frames, so a fractional scale cannot sample a neighbouring card.
export const CARD_GUTTER = 2;

export const LUCIDE_VERSION = '1.49.0';
export const ICONS = [
  'archive', 'arrow-left', 'arrow-right', 'arrow-up-down', 'award', 'bell', 'building-complex', 'calendar',
  'check', 'chevron-down', 'chevron-left', 'chevron-right', 'chevron-up', 'circle', 'circle-alert',
  'circle-check', 'circle-check-big', 'circle-x', 'clipboard-list', 'clock', 'copy', 'corner-up-left',
  'credit-card', 'crosshair', 'crown', 'dot', 'download', 'ellipsis', 'external-link', 'eye', 'eye-off',
  'face-slightly-smiling', 'face-slightly-smiling-plus', 'folder-kanban', 'folder-open', 'funnel',
  'gallery-horizontal-end', 'grip-vertical', 'hash', 'house', 'image', 'info', 'layers', 'layout-dashboard',
  'link', 'link-2', 'list-ordered', 'list-plus', 'loader-circle', 'lock', 'log-in', 'log-out', 'mail',
  'maximize-2', 'menu', 'message-circle', 'message-square', 'moon', 'music', 'palette', 'panel-left', 'pause',
  'pen', 'pencil', 'play', 'plus', 'power', 'power-off', 'refresh-cw', 'rotate-ccw', 'save', 'scan-search',
  'search', 'send', 'settings', 'settings-2', 'share-2', 'shield', 'shield-alert', 'spade', 'sparkles',
  'spotlight', 'square-pen', 'star', 'sun', 'tag', 'thumbs-up', 'timer', 'trash', 'triangle-alert', 'trophy',
  'undo-2', 'upload', 'user', 'user-check', 'user-minus', 'user-plus', 'users', 'volume-x', 'vote', 'x', 'zap',
];

const SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
const CRC_TABLE = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) {
    c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  }
  return c >>> 0;
});

function crc32(buf) {
  let c = 0xffffffff;
  for (const byte of buf) {
    c = CRC_TABLE[(c ^ byte) & 0xff] ^ (c >>> 8);
  }
  return (c ^ 0xffffffff) >>> 0;
}

function paeth(a, b, c) {
  const p = a + b - c;
  const pa = Math.abs(p - a);
  const pb = Math.abs(p - b);
  const pc = Math.abs(p - c);
  return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
}

// Decodes an 8-bit RGBA, non-interlaced PNG (the only kind the card art uses) into raw RGBA rows.
export function decodePng(buf) {
  if (!buf.subarray(0, 8).equals(SIGNATURE)) {
    throw new Error('not a PNG');
  }
  let width = 0;
  let height = 0;
  const idat = [];
  for (let at = 8; at < buf.length;) {
    const length = buf.readUInt32BE(at);
    const type = buf.toString('latin1', at + 4, at + 8);
    const data = buf.subarray(at + 8, at + 8 + length);
    if (type === 'IHDR') {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      const [depth, colourType, , , interlace] = data.subarray(8);
      if (depth !== 8 || colourType !== 6 || interlace !== 0) {
        throw new Error(`unsupported PNG: depth ${depth}, colour type ${colourType}, interlace ${interlace}`);
      }
    } else if (type === 'IDAT') {
      idat.push(data);
    } else if (type === 'IEND') {
      break;
    }
    at += 12 + length;
  }
  const raw = inflateSync(Buffer.concat(idat));
  const stride = width * 4;
  const pixels = Buffer.alloc(stride * height);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const line = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
    for (let x = 0; x < stride; x++) {
      const a = x >= 4 ? pixels[y * stride + x - 4] : 0;
      const b = y > 0 ? pixels[(y - 1) * stride + x] : 0;
      const c = x >= 4 && y > 0 ? pixels[(y - 1) * stride + x - 4] : 0;
      const predictor = [0, a, b, (a + b) >> 1, paeth(a, b, c)][filter];
      if (predictor === undefined) {
        throw new Error(`bad PNG filter ${filter}`);
      }
      pixels[y * stride + x] = (line[x] + predictor) & 0xff;
    }
  }
  return { width, height, pixels };
}

function chunk(type, data) {
  const head = Buffer.alloc(8);
  head.writeUInt32BE(data.length, 0);
  head.write(type, 4, 'latin1');
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([head.subarray(4), data])));
  return Buffer.concat([head, data, crc]);
}

// Per row, keeps the filter with the smallest sum of absolute residuals (the libpng heuristic).
export function encodePng({ width, height, pixels }) {
  const stride = width * 4;
  const rows = [];
  for (let y = 0; y < height; y++) {
    let best;
    let bestCost = Infinity;
    for (let filter = 0; filter < 5; filter++) {
      const line = Buffer.alloc(stride + 1);
      line[0] = filter;
      let cost = 0;
      for (let x = 0; x < stride; x++) {
        const a = x >= 4 ? pixels[y * stride + x - 4] : 0;
        const b = y > 0 ? pixels[(y - 1) * stride + x] : 0;
        const c = x >= 4 && y > 0 ? pixels[(y - 1) * stride + x - 4] : 0;
        const predictor = [0, a, b, (a + b) >> 1, paeth(a, b, c)][filter];
        const value = (pixels[y * stride + x] - predictor) & 0xff;
        line[x + 1] = value;
        cost += value < 128 ? value : 256 - value;
      }
      if (cost < bestCost) {
        best = line;
        bestCost = cost;
      }
    }
    rows.push(best);
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([8, 6, 0, 0, 0], 8);
  return Buffer.concat([
    SIGNATURE,
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(Buffer.concat(rows), { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

export function packCards(frames) {
  const width = frames.length * CARD_WIDTH + (frames.length - 1) * CARD_GUTTER;
  const stride = width * 4;
  const pixels = Buffer.alloc(stride * CARD_HEIGHT);
  const layout = {};
  frames.forEach(({ name, image }, i) => {
    if (image.width !== CARD_WIDTH || image.height !== CARD_HEIGHT) {
      throw new Error(`${name}: expected ${CARD_WIDTH}×${CARD_HEIGHT}, got ${image.width}×${image.height}`);
    }
    const x = i * (CARD_WIDTH + CARD_GUTTER);
    for (let y = 0; y < CARD_HEIGHT; y++) {
      image.pixels.copy(pixels, y * stride + x * 4, y * CARD_WIDTH * 4, (y + 1) * CARD_WIDTH * 4);
    }
    layout[name] = { x, y: 0, w: CARD_WIDTH, h: CARD_HEIGHT };
  });
  return { image: { width, height: CARD_HEIGHT, pixels }, layout };
}

function makeCards() {
  const frames = CARD_FRAMES.map((name) => ({
    name,
    image: decodePng(readFileSync(join(assets, 'cards', `${name}.png`))),
  }));
  const { image, layout } = packCards(frames);
  const map = { width: image.width, height: image.height, frames: layout };
  writeFileSync(join(assets, 'cards.png'), encodePng(image));
  writeFileSync(join(assets, 'cards.json'), JSON.stringify(map, null, 2) + '\n');
  console.log(`cards.png ${image.width}×${image.height}, ${frames.length} frames`);
}

function attributes(attrs) {
  return Object.entries(attrs).map(([k, v]) => ` ${k}="${String(v).replace(/&/g, '&amp;').replace(/"/g, '&quot;')}"`).join('');
}

// On each symbol, because <use> inherits from its own ancestors, not from this file's root.
const STROKE = attributes({
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  'stroke-width': 2,
  'stroke-linecap': 'round',
  'stroke-linejoin': 'round',
});

function makeIcons(packageDir) {
  const pkg = JSON.parse(readFileSync(join(packageDir, 'package.json'), 'utf8'));
  if (pkg.name !== 'lucide-static' || pkg.version !== LUCIDE_VERSION) {
    throw new Error(`expected lucide-static@${LUCIDE_VERSION}, got ${pkg.name}@${pkg.version}`);
  }
  const nodes = JSON.parse(readFileSync(join(packageDir, 'icon-nodes.json'), 'utf8'));
  const symbols = ICONS.map((name) => {
    if (!nodes[name]) {
      throw new Error(`lucide-static@${LUCIDE_VERSION} has no icon ${name}`);
    }
    const children = nodes[name].map(([tag, attrs]) => `<${tag}${attributes(attrs)}/>`).join('');
    return `<symbol id="${name}"${STROKE}>${children}</symbol>`;
  });
  const svg = `<!-- Lucide v${LUCIDE_VERSION} subset, ISC (see THIRD_PARTY.md) -->\n`
    + '<svg xmlns="http://www.w3.org/2000/svg">\n' + symbols.join('\n') + '\n</svg>\n';
  writeFileSync(join(assets, 'icons.svg'), svg);
  console.log(`icons.svg ${ICONS.length} symbols`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [command, arg] = process.argv.slice(2);
  if (command === 'cards') {
    makeCards();
  } else if (command === 'icons' && arg) {
    makeIcons(arg);
  } else {
    console.error('usage: node tools/make-sprite.mjs cards | icons <unpacked lucide-static dir>');
    process.exit(2);
  }
}
