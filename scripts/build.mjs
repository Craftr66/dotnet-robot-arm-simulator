import { build } from 'esbuild';
import { mkdir, copyFile, cp } from 'node:fs/promises';
const destination = new URL('../src/Host/wwwroot/', import.meta.url);
await mkdir(destination, { recursive: true });
await build({ entryPoints: ['src/Frontend/main.ts'], bundle: true, format: 'esm',
  outfile: 'src/Host/wwwroot/app.js', sourcemap: true, target: 'es2022' });
for (const name of ['index.html', 'style.css']) {
  await copyFile(new URL(`../src/Frontend/${name}`, import.meta.url), new URL(name, destination));
}
await cp(new URL('../node_modules/blockly/media/',import.meta.url),new URL('blockly-media/',destination),{recursive:true});
console.log('Frontend built → src/Host/wwwroot (all assets local)');
