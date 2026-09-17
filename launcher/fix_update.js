const fs = require('fs');
const path = 'G:\\deepseek-harness\\update.ps1';
let c = fs.readFileSync(path, 'utf8');

// Remove --legacy-peer-deps from main install
c = c.replace(
  "'install', '@deepseek-ai/dsh@latest', '--legacy-peer-deps', '--no-fund', '--no-audit', '--foreground-scripts'",
  "'install', '@deepseek-ai/dsh@latest', '--no-fund', '--no-audit', '--foreground-scripts'"
);

// Remove --legacy-peer-deps from peer deps install
c = c.replace(
  "@('--legacy-peer-deps', '--no-fund', '--no-audit', '--foreground-scripts')",
  "@('--no-fund', '--no-audit', '--foreground-scripts')"
);

fs.writeFileSync(path, c, 'utf8');
console.log('update.ps1: removed --legacy-peer-deps');
