// 将 multicopy-icon-512.png 转换为多分辨率 .ico 文件
// 生成: app.ico（主图标）, tray-on.ico（模式开-亮）, tray-off.ico（模式关-暗）
// 用法: node convert-icon.js <assets_dir>
const fs = require('fs');
const path = require('path');
const _pngToIcoMod = require('png-to-ico');
const icoFunc = _pngToIcoMod.default || _pngToIcoMod.imagesToIco || _pngToIcoMod;
const sharp = require('sharp');

const srcPng = path.join(process.argv[2], 'multicopy-icon-512.png');
const outDir = process.argv[2];

const SIZES = [16, 32, 48, 256]; // ICO 多分辨率

async function resizeToSizes(inputPath, sizes) {
    const results = await Promise.all(
        sizes.map(s => sharp(inputPath).resize(s, s, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } }).png().toBuffer())
    );
    return results;
}

async function buildTrayVariant(inputPath, opts) {
    // opts: { brightness, saturate, opacity }
    let pipeline = sharp(inputPath);
    if (opts.brightness) pipeline = pipeline.modulate({ brightness: opts.brightness });
    if (opts.saturate !== undefined) pipeline = pipeline.modulate({ saturation: opts.saturate });
    if (opts.opacity) pipeline = pipeline.ensureAlpha().composite([{
        input: await sharp({
            create: { width: 512, height: 512, channels: 4, background: { r: 0, g: 0, b: 0, alpha: 1 } }
        }).raw().toBuffer(),
        blend: 'dest-in',
        raw: { width: 512, height: 512, channels: 4 }
    }]);
    
    const buf = await pipeline.png().toBuffer();
    return resizeToSizes(buf, SIZES);
}

async function main() {
    console.log('源图片:', srcPng);
    
    // 1. 主图标 app.ico — 原图直接转
    const mainBuffers = await resizeToSizes(srcPng, SIZES);
    const appIco = await icoFunc(mainBuffers);
    fs.writeFileSync(path.join(outDir, 'app.ico'), appIco);
    console.log('✓ app.ico 已生成（' + SIZES.join('/') + 'px）');

    // 2. 托盘图标-开 tray-on.ico — 稍亮一点，保持色彩
    const onBuffers = await buildTrayVariant(srcPng, { brightness: 1.1 });
    const onIco = await icoFunc(onBuffers);
    fs.writeFileSync(path.join(outDir, 'tray-on.ico'), onIco);
    console.log('✓ tray-on.ico 已生成（亮度 +10%）');

    // 3. 托盘图标-关 tray-off.ico — 降低饱和度+亮度（灰暗效果）
    const offBuffers = await buildTrayVariant(srcPng, { brightness: 0.75, saturate: 0.3 });
    const offIco = await icoFunc(offBuffers);
    fs.writeFileSync(path.join(outDir, 'tray-off.ico'), offIco);
    console.log('✓ tray-off.ico 已生成（降饱和度 + 暗淡）');

    console.log('\n全部完成！输出目录:', outDir);
}

main().catch(e => { console.error(e); process.exit(1); });
