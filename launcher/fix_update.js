// 一次性补丁脚本（历史留存）：从 update.ps1 里去掉了 --legacy-peer-deps。
// 该改动当年已经手工应用；保留此文件是为了让提交历史可追溯，正常情况下无需再运行。
//
// 用法: node fix_update.js <update.ps1 的路径>
// 历史版本把目标路径写死为某个具体盘符，属于个人环境信息，已改为参数传入。
const fs = require('fs');
const path = process.argv[2];
if (!path) {
  console.error('用法: node fix_update.js <update.ps1 的路径>');
  process.exit(1);
}
let c = fs.readFileSync(path, 'utf8');

c = c.replace(
  "'install', '@deepseek-ai/dsh@latest', '--legacy-peer-deps', '--no-fund', '--no-audit', '--foreground-scripts'",
  "'install', '@deepseek-ai/dsh@latest', '--no-fund', '--no-audit', '--foreground-scripts'"
);
c = c.replace(
  "@('--legacy-peer-deps', '--no-fund', '--no-audit', '--foreground-scripts')",
  "@('--no-fund', '--no-audit', '--foreground-scripts')"
);
fs.writeFileSync(path, c, 'utf8');
console.log('update.ps1: removed --legacy-peer-deps');
