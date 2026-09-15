#!/usr/bin/env node
/*
 * 把 docs/icon.svg 渲染成 docs/icon.png（插件图标，512×512 满幅方形）。
 *
 *   node scripts/render-icon.mjs            # 需要能 import 到 playwright-core
 *
 * playwright-core 可以从任何地方来，任选一种：
 *   NODE_PATH=<dir with node_modules> node scripts/render-icon.mjs
 *   或者在本目录 npm i -D playwright-core
 * Chromium 缺失时用 JF_CHROMIUM=<chrome 可执行文件> 指定。
 *
 * 渲染策略：SVG 先按 1024 渲染，再用 LANCZOS 缩到 512（等价 2× 超采样，边缘更干净）。
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const SVG = path.join(ROOT, 'docs', 'icon.svg');
const PNG = path.join(ROOT, 'docs', 'icon.png');
const SIZE = 512;
const SS = 2;              // 超采样倍数

const require = createRequire(import.meta.url);
let chromium;
try {
  ({ chromium } = require('playwright-core'));
} catch (e) {
  console.error('找不到 playwright-core。试试：NODE_PATH=<有 node_modules 的目录> node scripts/render-icon.mjs');
  throw e;
}

const svg = fs.readFileSync(SVG, 'utf8');
const browser = await chromium.launch({
  headless: true,
  executablePath: process.env.JF_CHROMIUM || undefined,
  args: ['--no-sandbox', '--disable-dev-shm-usage']
});
try {
  const px = SIZE * SS;
  const page = await browser.newPage({ viewport: { width: px, height: px }, deviceScaleFactor: 1 });
  await page.setContent(
    '<html><head>'
    // 字标用的字体：联网时从 Google Fonts 取，取不到就退回系统字体（SVG 里写了 font 兜底栈）
    + '<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Manrope:wght@500;600;700;800&family=Inter:wght@500;600;700;800&display=swap">'
    + '</head><body style="margin:0;padding:0">'
    + '<style>svg{display:block;width:' + px + 'px;height:' + px + 'px}</style>'
    + svg + '</body></html>',
    { waitUntil: 'load' }
  );
  // 等字体真正可用再截图，否则会拍到兜底字体
  await page.evaluate(async () => {
    if (document.fonts && document.fonts.ready) { try { await document.fonts.ready; } catch (e) { /* ignore */ } }
  }).catch(() => {});
  await page.waitForTimeout(300);
  const big = await page.screenshot({ clip: { x: 0, y: 0, width: px, height: px } });
  fs.writeFileSync(path.join(ROOT, 'docs', '.icon-' + px + '.png'), big);
} finally {
  await browser.close();
}

// 无依赖的降采样：用 canvas 再跑一遍太绕，这里直接用 sharp/canvas 都没有时的兜底 —— 交给 Python PIL
const tmp = path.join(ROOT, 'docs', '.icon-' + (SIZE * SS) + '.png');
const { execFileSync } = await import('node:child_process');
try {
  execFileSync('python3', ['-c', `
from PIL import Image
im = Image.open(r'${tmp}').convert('RGB')
im.resize((${SIZE}, ${SIZE}), Image.LANCZOS).save(r'${PNG}', optimize=True)
print('wrote', r'${PNG}', im.size, '->', (${SIZE}, ${SIZE}))
`], { stdio: 'inherit' });
} finally {
  fs.rmSync(tmp, { force: true });
}
console.log('icon.svg ->', PNG);
