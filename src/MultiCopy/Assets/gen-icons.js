// 生成 MultiCopy 用的 .ico 图标文件（32x32, 32-bit + AND 掩码）
// 用法: node gen-icons.js <输出目录>
const fs = require('fs');
const path = require('path');

function buildIco(argb) {
    const size = 32;
    const xorSize = size * size * 4;
    const andSize = size * size / 8;
    const dibSize = 40 + xorSize + andSize;
    const total = 6 + 16 + dibSize;
    const buf = Buffer.alloc(total);
    let o = 0;
    // ICONDIR
    buf.writeUInt16LE(0, o); o += 2;
    buf.writeUInt16LE(1, o); o += 2;
    buf.writeUInt16LE(1, o); o += 2;
    // ICONDIRENTRY
    buf.writeUInt8(size, o++);
    buf.writeUInt8(size, o++);
    buf.writeUInt8(0, o++);
    buf.writeUInt8(0, o++);
    buf.writeUInt16LE(1, o); o += 2;
    buf.writeUInt16LE(32, o); o += 2;
    buf.writeUInt32LE(dibSize, o); o += 4;
    buf.writeUInt32LE(22, o); o += 4;
    // BITMAPINFOHEADER
    buf.writeUInt32LE(40, o); o += 4;
    buf.writeInt32LE(size, o); o += 4;
    buf.writeInt32LE(size * 2, o); o += 4;
    buf.writeUInt16LE(1, o); o += 2;
    buf.writeUInt16LE(32, o); o += 2;
    buf.writeUInt32LE(0, o); o += 4;
    buf.writeUInt32LE(xorSize, o); o += 4;
    buf.writeInt32LE(0, o); o += 4;
    buf.writeInt32LE(0, o); o += 4;
    buf.writeUInt32LE(0, o); o += 4;
    buf.writeUInt32LE(0, o); o += 4;

    const b = argb & 0xFF;
    const g = (argb >> 8) & 0xFF;
    const r = (argb >> 16) & 0xFF;
    const a = (argb >> 24) & 0xFF;
    const radius = 6;

    function distSq(x1, y1, x2, y2) { return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2); }
    function insideRounded(x, y) {
        if (x < radius && y < radius) return distSq(x + 0.5, y + 0.5, radius - 0.5, radius - 0.5) <= (radius - 0.5) * (radius - 0.5);
        if (x >= size - radius && y < radius) return distSq(x + 0.5, y + 0.5, size - radius - 0.5, radius - 0.5) <= (radius - 0.5) * (radius - 0.5);
        if (x < radius && y >= size - radius) return distSq(x + 0.5, y + 0.5, radius - 0.5, size - radius - 0.5) <= (radius - 0.5) * (radius - 0.5);
        if (x >= size - radius && y >= size - radius) return distSq(x + 0.5, y + 0.5, size - radius - 0.5, size - radius - 0.5) <= (radius - 0.5) * (radius - 0.5);
        return true;
    }

    // XOR 像素（BGRA，自底向上）
    for (let row = size - 1; row >= 0; row--) {
        for (let col = 0; col < size; col++) {
            const ins = insideRounded(col, row);
            const isLine = ins && (row === 10 || row === 16 || row === 22) && col >= 8 && col <= 23;
            if (isLine) { buf.writeUInt8(255, o++); buf.writeUInt8(255, o++); buf.writeUInt8(255, o++); buf.writeUInt8(255, o++); }
            else if (ins) { buf.writeUInt8(b, o++); buf.writeUInt8(g, o++); buf.writeUInt8(r, o++); buf.writeUInt8(a, o++); }
            else { buf.writeUInt8(0, o++); buf.writeUInt8(0, o++); buf.writeUInt8(0, o++); buf.writeUInt8(0, o++); }
        }
    }
    // AND 掩码（1=透明）
    for (let row = size - 1; row >= 0; row--) {
        for (let byteCol = 0; byteCol < size / 8; byteCol++) {
            let mask = 0;
            for (let bit = 0; bit < 8; bit++) {
                const col = byteCol * 8 + bit;
                if (!insideRounded(col, row)) mask |= (0x80 >> bit);
            }
            buf.writeUInt8(mask, o++);
        }
    }
    return buf;
}

const dir = process.argv[2];
fs.mkdirSync(dir, { recursive: true });
fs.writeFileSync(path.join(dir, 'app.ico'), buildIco(0xFF1F6FEB));      // 青蓝
fs.writeFileSync(path.join(dir, 'tray-on.ico'), buildIco(0xFF2EA043));  // 绿
fs.writeFileSync(path.join(dir, 'tray-off.ico'), buildIco(0xFF6E7681)); // 灰
console.log('已生成 ico 到:', dir);
