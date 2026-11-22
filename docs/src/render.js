const fs = require('fs');
const path = require('path');
const { Resvg } = require('@resvg/resvg-js');

const [, , src, out, scaleArg] = process.argv;
const scale = Number(scaleArg || 2);
const fontDir = path.join(__dirname, 'fonts');
const fontFiles = fs.readdirSync(fontDir).filter(f => /\.(ttf|otf)$/i.test(f)).map(f => path.join(fontDir, f));

const svg = fs.readFileSync(src, 'utf8');
const resvg = new Resvg(svg, {
  fitTo: { mode: 'zoom', value: scale },
  font: { fontFiles, loadSystemFonts: false, defaultFontFamily: 'Inter' },
  background: 'rgba(0,0,0,0)',
});
const png = resvg.render().asPng();
fs.writeFileSync(out, png);
console.log(out, png.length, 'bytes');
