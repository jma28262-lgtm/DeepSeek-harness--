#!/usr/bin/env node
// 扫描 node_modules 下所有包的 peerDependencies，找出无法解析的缺失包
//
// 用法：node scan-missing.mjs <globalDir>
//       不传参数时使用当前工作目录。
//
// 修复记录（三处都是会静默出错的）：
//   1. 旧版把默认目录写死为 'D:/deepseek-harness/tools/global'，而调用方
//      （bootstrap.ps1 / update.ps1）从不传参 —— 换盘后 readdirSync 抛错被
//      catch 吞掉，于是"什么都没扫"却打印"无缺失 peer 依赖"，是彻底的假成功。
//      现在：node_modules 不存在就直接报错退出（exit 2），绝不假装成功。
//   2. 旧版输出 '\\nINSTALL_LIST=...'，单引号里的 \\n 是"反斜杠+n"两个字符，
//      而 PowerShell 侧用 -like 'INSTALL_LIST=*' 匹配整行 —— 永远匹配不上，
//      整段补装 peer 的逻辑从未执行过。现在改为独立一行，不带反斜杠。
//   3. 旧版用 require.resolve(name + '/package.json') 判断存在性，带 exports
//      映射且未导出 ./package.json 的包会抛 ERR_PACKAGE_PATH_NOT_EXPORTED，
//      被误判为"缺失"。现在先试包入口，再退回到 package.json 探测。
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';

const root = path.resolve(process.argv[2] || process.cwd());
const nm = path.join(root, 'node_modules');

if (!fs.existsSync(nm)) {
  console.error(`scan-missing: 找不到目录 ${nm}`);
  console.error('scan-missing: 请把 global 目录作为第一个参数传入（bootstrap.ps1 / update.ps1 已传）。');
  process.exit(2);
}

const require = createRequire(path.join(root, 'package.json'));

function canResolve(name, fromDir) {
  const r = createRequire(path.join(fromDir, '__probe__.js'));
  // 先试包入口：带 exports 映射的包不允许解析 ./package.json，
  // 旧写法会因此抛 ERR_PACKAGE_PATH_NOT_EXPORTED 并被误判为"缺失"。
  try { r.resolve(name); return true; } catch { /* 继续尝试 package.json */ }
  try { r.resolve(name + '/package.json'); return true; } catch { return false; }
}

// 收集所有包含 package.json 的包目录（含嵌套）
const pkgDirs = [];
function walk(dir, depth) {
  if (depth > 6) return;
  let entries;
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
  for (const e of entries) {
    if (e.name === '.bin' || e.name.startsWith('.')) continue;
    const full = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (e.name.startsWith('@')) {
        // scope 目录：递归一层
        try {
          for (const sub of fs.readdirSync(full)) {
            const subFull = path.join(full, sub);
            const pj = path.join(subFull, 'package.json');
            if (fs.existsSync(pj)) pkgDirs.push(subFull);
          }
        } catch { /* 跳过无权限目录 */ }
      } else {
        const pj = path.join(full, 'package.json');
        if (fs.existsSync(pj)) pkgDirs.push(full);
        walk(full, depth + 1);
      }
    }
  }
}
walk(nm, 0);

const missing = new Map(); // name -> { ranges:Set, neededBy:Set }

for (const dir of pkgDirs) {
  let pkg;
  try { pkg = JSON.parse(fs.readFileSync(path.join(dir, 'package.json'), 'utf8')); } catch { continue; }
  const peers = pkg.peerDependencies || {};
  for (const name of Object.keys(peers)) {
    if (!canResolve(name, dir)) {
      if (!missing.has(name)) missing.set(name, { ranges: new Set(), neededBy: new Set() });
      missing.get(name).ranges.add(peers[name]);
      missing.get(name).neededBy.add(pkg.name || path.basename(dir));
    }
  }
}

if (missing.size === 0) {
  // 带上扫描数量：这样"扫了 0 个包"和"扫了 300 个包都正常"不会再被混为一谈
  console.log(`OK: 已检查 ${pkgDirs.length} 个包，无缺失 peer 依赖`);
} else {
  console.log(`发现 ${missing.size} 个缺失 peer 依赖（已检查 ${pkgDirs.length} 个包）:`);
  const install = [];
  for (const [name, info] of missing) {
    const ranges = [...info.ranges];
    console.log(`  ${name}@${ranges.join(' 或 ')}  <- ${[...info.neededBy].slice(0, 3).join(', ')}${info.neededBy.size > 3 ? ' 等' + info.neededBy.size + '个包' : ''}`);
    install.push(`${name}@${ranges[0]}`);
  }
  console.log('');                       // 空行用 console.log('') 输出，绝不在字符串里写 \n
  console.log('INSTALL_LIST=' + install.join(' '));
}
