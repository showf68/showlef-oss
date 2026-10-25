// Genere icons/icon_{16,32,48,128,256}.png : fleche de telechargement blanche dans un rond degrade bleu.
// Sans dependance (encodeur PNG maison, comme radio-juive). Lissage par sur-echantillonnage 4x4.
const zlib = require('zlib');
const fs = require('fs');

function crc(buf) {
  const t = [];
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  let r = 0xffffffff;
  for (const b of buf) r = t[(r ^ b) & 255] ^ (r >>> 8);
  return (r ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const td = Buffer.concat([Buffer.from(type), data]);
  const c = Buffer.alloc(4);
  c.writeUInt32BE(crc(td));
  return Buffer.concat([len, td, c]);
}

const mix = (a, b, t) => a.map((v, i) => Math.round(v + (b[i] - v) * t));
const TOP = [99, 102, 241], BOTTOM = [109, 40, 217], DEEP = [49, 46, 129];   // indigo -> violet, comme le logo du programme

// Point dans le triangle (pointe de la fleche).
function inTri(x, y, ax, ay, bx, by, cx, cy) {
  const d1 = (x - bx) * (ay - by) - (ax - bx) * (y - by);
  const d2 = (x - cx) * (by - cy) - (bx - cx) * (y - cy);
  const d3 = (x - ax) * (cy - ay) - (cx - ax) * (y - ay);
  const neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
  return !(neg && pos);
}

function arrow(x, y) {
  if (x >= 0.435 && x <= 0.565 && y >= 0.2 && y <= 0.5) return true;          // tige
  if (inTri(x, y, 0.27, 0.46, 0.73, 0.46, 0.5, 0.69)) return true;             // pointe
  if (x >= 0.27 && x <= 0.73 && y >= 0.735 && y <= 0.8) return true;          // plateau
  return false;
}

function shade(x, y) {
  const dx = x - 0.5, dy = y - 0.5, d = Math.sqrt(dx * dx + dy * dy);
  if (d > 0.48) return [0, 0, 0, 0];
  if (arrow(x, y)) return [255, 255, 255, 255];
  let c = mix(TOP, BOTTOM, Math.min(1, Math.max(0, (x * 0.4 + y) / 1.2)));
  if (d > 0.44) c = mix(c, DEEP, (d - 0.44) / 0.04 * 0.6);                    // liseré
  if (y < 0.42 && d < 0.44) c = mix(c, [255, 255, 255], (0.42 - y) * 0.25);   // reflet haut
  return [c[0], c[1], c[2], 255];
}

function png(size) {
  const stride = size * 4 + 1;
  const raw = Buffer.alloc(stride * size);
  const N = 4;
  for (let y = 0; y < size; y++) {
    raw[y * stride] = 0;
    for (let x = 0; x < size; x++) {
      let r = 0, g = 0, b = 0, a = 0;
      for (let sy = 0; sy < N; sy++) {
        for (let sx = 0; sx < N; sx++) {
          const p = shade((x + (sx + 0.5) / N) / size, (y + (sy + 0.5) / N) / size);
          r += p[0] * p[3]; g += p[1] * p[3]; b += p[2] * p[3]; a += p[3];
        }
      }
      const i = y * stride + 1 + x * 4;
      if (a > 0) { raw[i] = Math.round(r / a); raw[i + 1] = Math.round(g / a); raw[i + 2] = Math.round(b / a); }
      raw[i + 3] = Math.round(a / (N * N));
    }
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0))
  ]);
}

fs.mkdirSync(__dirname + '/icons', { recursive: true });
for (const s of [16, 32, 48, 128, 256]) fs.writeFileSync(__dirname + '/icons/icon_' + s + '.png', png(s));
