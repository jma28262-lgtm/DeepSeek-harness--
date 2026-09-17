const path = require('node:path');
const { createRequire } = require('node:module');
const root = 'D:/deepseek-harness/tools/global';
for (const [from, names] of [
  [path.join(root,'node_modules','@hono','node-server'), ['hono']],
  [path.join(root,'node_modules','@mistralai','mistralai'), ['@opentelemetry/api']],
  [path.join(root,'node_modules','@opentelemetry','core'), ['@opentelemetry/api']],
]) {
  const r = createRequire(path.join(from, '__probe__.js'));
  for (const n of names) {
    try { const res = r.resolve(n + '/package.json'); console.log('OK  ', n, '->', res); }
    catch { console.log('MISS', n, 'from', from); }
  }
}
