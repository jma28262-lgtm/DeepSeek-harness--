const { createRequire } = require('node:module');
const path = require('node:path');
const root = 'D:/deepseek-harness/tools/global';
const require2 = createRequire(path.join(root, 'package.json'));
// ????
for (const n of ['hono','@opentelemetry/api','react']) {
  console.log(n, '????:', require('node:fs').existsSync(path.join(root,'node_modules', n)));
}
// ? @hono/node-server resolve hono
for (const fromDir of [path.join(root,'node_modules','@hono','node-server'), path.join(root,'node_modules','@mistralai','mistralai')]) {
  for (const n of ['hono','@opentelemetry/api']) {
    try { require2.resolve(n + '/package.json', { paths: [fromDir] }); console.log('OK  ', n, '<-', fromDir.split('/node_modules/')[1]); }
    catch { console.log('MISS', n, '<-', fromDir.split('/node_modules/')[1]); }
  }
}
// ?? @opentelemetry ??
console.log('@opentelemetry ??:', require('node:fs').readdirSync(path.join(root,'node_modules','@opentelemetry')).join(', '));
console.log('hono ??:', JSON.parse(require('node:fs').readFileSync(path.join(root,'node_modules','hono','package.json'),'utf8')).version);
