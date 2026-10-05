// Turns the frames capture.mjs recorded into GIFs, on the host after the Sandbox has closed. Each animation is resampled
// to an even frame rate, cropped to the area the capture said to keep and scaled down to a width a page can show.
// A recording with a 'start' and an 'end' mark becomes one GIF; the board's recording becomes one GIF per kind of move
// it caught (a ride, an arrival, a missed call's mark), cut from around the first of each.
//
//   node encode.mjs <frames folder> <output folder>
import fs from 'node:fs';
import path from 'node:path';
import jpeg from 'jpeg-js';
import gifenc from 'gifenc';
const { GIFEncoder, quantize, applyPalette } = gifenc;

const [framesRoot, outDir] = process.argv.slice(2);
fs.mkdirSync(outDir, { recursive: true });
const FPS = 15, WIDTH = 960;

function cropScale(img, area) {
  const ax = Math.max(0, Math.round(area?.x ?? 0)), ay = Math.max(0, Math.round(area?.y ?? 0));
  const aw = Math.min(img.width - ax, Math.round(area?.width ?? img.width)), ah = Math.min(img.height - ay, Math.round(area?.height ?? img.height));
  const scale = Math.min(1, WIDTH / aw), w = Math.round(aw * scale), h = Math.round(ah * scale);
  const outPx = new Uint8Array(w * h * 4);
  // Box filter: each output pixel averages the source pixels it covers, so thin lines survive the scaling.
  for (let y = 0; y < h; y++) {
    const y0 = ay + Math.floor(y / scale), y1 = Math.max(y0 + 1, ay + Math.floor((y + 1) / scale));
    for (let x = 0; x < w; x++) {
      const x0 = ax + Math.floor(x / scale), x1 = Math.max(x0 + 1, ax + Math.floor((x + 1) / scale));
      let r = 0, g = 0, b = 0, n = 0;
      for (let sy = y0; sy < y1; sy++) for (let sx = x0; sx < x1; sx++) {
        const i = (sy * img.width + sx) * 4; r += img.data[i]; g += img.data[i + 1]; b += img.data[i + 2]; n++;
      }
      const o = (y * w + x) * 4; outPx[o] = r / n; outPx[o + 1] = g / n; outPx[o + 2] = b / n; outPx[o + 3] = 255;
    }
  }
  return { data: outPx, width: w, height: h };
}

function encode(dir, index, from, to, file) {
  const frames = index.frames.filter((f) => f.t <= to);
  if (!frames.length) return;
  // The frames on screen at each tick of an even frame rate, a frame held for as many ticks as it stayed.
  const picked = [];
  for (let t = from; t <= to; t += 1000 / FPS) {
    let f = frames[0];
    for (const c of frames) { if (c.t <= t) f = c; else break; }
    if (picked.length && picked[picked.length - 1].f.file === f.file) picked[picked.length - 1].ticks++;
    else picked.push({ f, ticks: 1 });
  }
  const images = picked.map((p) => cropScale(jpeg.decode(fs.readFileSync(path.join(dir, p.f.file)), { useTArray: true }), index.area));
  // One palette for the whole animation, from a few frames across it, keeping the last index for "unchanged".
  const sample = [0, Math.floor(images.length / 2), images.length - 1].map((i) => images[i].data);
  const joined = new Uint8Array(sample.reduce((n, d) => n + d.length, 0));
  sample.reduce((at, d) => (joined.set(d, at), at + d.length), 0);
  const palette = quantize(joined, 255);
  while (palette.length < 255) palette.push([0, 0, 0]);
  palette.push([0, 0, 0]);
  const CLEAR = 255;
  // Most of the screen stands still between frames, so after the first only the pixels that changed are stored;
  // the rest are left transparent over the frame before, which is where nearly all the saving comes from.
  const gif = GIFEncoder();
  let previous = null;
  images.forEach((img, i) => {
    const indexed = applyPalette(img.data, palette.slice(0, 255));
    let out = indexed;
    if (previous) {
      out = new Uint8Array(indexed.length);
      for (let k = 0; k < indexed.length; k++) out[k] = indexed[k] === previous[k] ? CLEAR : indexed[k];
    }
    gif.writeFrame(out, img.width, img.height, { palette: i ? undefined : palette, delay: Math.round(picked[i].ticks * 1000 / FPS), transparent: !!previous, transparentIndex: CLEAR, dispose: 1 });
    previous = indexed;
  });
  gif.finish();
  fs.writeFileSync(file, gif.bytes());
  console.log(path.basename(file), images.length, 'frames', Math.round(fs.statSync(file).size / 1024), 'KB');
}

for (const name of fs.readdirSync(framesRoot)) {
  const dir = path.join(framesRoot, name);
  const index = JSON.parse(fs.readFileSync(path.join(dir, 'index.json'), 'utf8'));
  const start = index.marks.find((m) => m.kind === 'start'), end = index.marks.find((m) => m.kind === 'end');
  if (start && end) {
    encode(dir, index, start.t, end.t, path.join(outDir, name + '.gif'));
    continue;
  }
  // Around the first of each kind of move: a little before, so the eye finds it, and long enough to land.
  const windows = { ride: [1200, 3000], arrive: [600, 2200], signal: [400, 2400] };
  for (const [kind, [before, after]] of Object.entries(windows)) {
    const m = index.marks.find((x) => x.kind === kind);
    if (m) encode(dir, index, m.t - before, m.t + after, path.join(outDir, `${name}-${kind}.gif`));
    else console.log(`${name}: no ${kind} was caught`);
  }
}
