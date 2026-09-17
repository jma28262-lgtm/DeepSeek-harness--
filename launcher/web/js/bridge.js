/* ============================================================
   WebView2 <-> C# 通信桥

   两种运行环境：
     1. 在 DeepSeekHarness.exe 的 WebView2 里 —— 走真实宿主通道
     2. 普通浏览器里直接打开（开发/预览）—— 降级为「演示模式」，
        用内置假数据让界面能渲染出来，并在顶部给出明确提示。

   原实现在非 WebView2 环境会在顶层访问 window.chrome.webview 抛错，
   导致 window.Bridge 永不赋值、app.js 整个 IIFE 中断 —— 页面变成空壳且无任何提示。
   ============================================================ */
(function () {
  "use strict";

  var host = (typeof window !== "undefined" && window.chrome && window.chrome.webview) ? window.chrome.webview : null;

  // ---------------- 演示模式（仅在没有宿主时启用） ----------------
  if (!host) {
    var demoState = {
      gateway: { state: "running", port: 3080, ready: true, pid: 25887 },
      model: { llama: true, ollama: false, llamaPort: 11435, llamaDir: "D:\\llama.cpp",
               modelDir: "D:\\Ollama\\Models", ollamaPath: "D:\\Ollama\\ollama.exe",
               backend: "llama.cpp" }
    };
    var demoEnv = {
      node: "24.19.0", nodeOk: true, dsh: "0.1.0-preview.9", dshOk: true,
      os: "Microsoft Windows NT 10.0.26100.0", port: 3080, portEditable: true,
      autoStart: false, modelDir: "D:\\Ollama\\Models", llamaDir: "D:\\llama.cpp",
      llamaPort: 11435, dpi: 1,
      secret: demoSecret,
      secretMode: "Passphrase", secretsUnlocked: false,
      secretError: "", jobObject: true, envSetupRunning: false,
      startupNotes: ["检测到明文密钥（config\\user.env），凭据库尚未解锁，暂不迁移。"],
      lastSetup: {
        MachineId: "9f3c1a7e5b2d4e08", MachineChanged: true, NeedsPassphrase: true, AllGood: false,
        Steps: [
          { Name: "换机检测", Status: "fixed", Detail: "检测到这是另一台电脑（机器指纹变化），已自动触发完整自检与配置修复。" },
          { Name: "运行时", Status: "ok", Detail: "便携 Node 与 dsh 内核均已就绪。" },
          { Name: "路径自愈：DSH_LLAMA_MODEL", Status: "fixed", Detail: "原路径失效（D:\\Ollama\\Models\\gguf\\qwen2.5-7b-…gguf）→ 已重定位到 E:\\Ollama\\Models\\gguf\\qwen2.5-7b-…gguf" },
          { Name: "路径自愈：DSH_LLAMA_DIR", Status: "fixed", Detail: "原路径失效（G:\\Ollama）→ 已重定位到 E:\\Ollama" },
          { Name: "端口", Status: "ok", Detail: "端口 3080 可用。" },
          { Name: "凭据", Status: "warn", Detail: "检测到明文密钥（config\\user.env）。需要输入一次访问口令才能解锁/迁移加密凭据库。" },
          { Name: "本地模型配置", Status: "fixed", Detail: "探测到 2 项：- auto-extra1 http://127.0.0.1:11435/v1 [stub-model-7b]；Default model: auto-extra1 / stub-model-7b" }
        ]
      }
    };
    var demoSecret = { mode: "Passphrase", unlocked: false, hasKey: true, hint: "sk-de****m000", error: "", notes: [] };
    demoEnv.secret = demoSecret;   // var 提升：demoEnv 定义在前，必须在此处补赋值
    var demoConfig = {
      DSH_PORT: "3080", DSH_AUTO_START_MODEL: "0", DSH_MODEL_BACKEND: "llama.cpp",
      DSH_LLAMA_DIR: "D:\\Ollama\\Models", DSH_LLAMA_MODEL: "", DSH_LLAMA_PORT: "11435",
      DSH_LLAMA_CONTEXT: "4096", DSH_LLAMA_GPU_LAYERS: "99", DSH_LLAMA_EXTRA_ARGS: "",
      DSH_OLLAMA_EXE: "D:\\Ollama\\ollama.exe", DSH_OLLAMA_MODEL: "",
      DSH_EXTRA_ENDPOINTS: "llama-server|http://127.0.0.1:11435|/v1", DSH_LLAMA_EXE_DIR: "D:\\llama.cpp",
      startupNotes: demoEnv.startupNotes, secret: demoSecret, lastSetup: demoEnv.lastSetup, envSetupRunning: false
    };
    var handlers = {};

    function reply(cmd, args) {
      switch (cmd) {
        case "getState": return demoState;
        case "getEnv": return demoEnv;
        case "getConfig": return demoConfig;
        case "getTheme": return "";
        case "listPlugins": return [
          { Name: "@deepseek-ai/dsh-tool-web", Version: "0.1.0", Dir: "…" },
          { Name: "@deepseek-ai/dsh-tool-skill", Version: "0.1.0", Dir: "…" }
        ];
        case "scanGGUF": return [
          { Path: "D:\\Ollama\\Models\\gguf\\qwen2.5-7b-instruct-q4_k_m.gguf", Name: "qwen2.5-7b-instruct-q4_k_m.gguf", SizeBytes: 4920000000 }
        ];
        case "setConfig": return "（演示模式）已保存";
        case "envSetup": return demoEnv.lastSetup;
        case "unlockSecrets": demoSecret.unlocked = true; return demoSecret;
        default: return true;
      }
    }

    var demoBanner = function () {
      try {
        if (document.getElementById("demo-banner")) return;
        var b = document.createElement("div");
        b.id = "demo-banner";
        b.textContent = "演示模式：当前不是由 DeepSeekHarness.exe 承载，界面使用内置假数据，任何操作都不会真正生效。";
        b.style.cssText = "position:fixed;left:0;right:0;top:0;z-index:9999;padding:8px 14px;font-size:12.5px;" +
          "background:rgba(245,177,61,.16);color:#f5e3b8;border-bottom:1px solid rgba(245,177,61,.35);text-align:center;";
        document.body.appendChild(b);
      } catch (e) { }
    };
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", demoBanner);
    else demoBanner();

    window.Bridge = {
      send: function (cmd, args) {
        return new Promise(function (resolve, reject) {
          setTimeout(function () {
            try { resolve(reply(cmd, args)); }
            catch (e) { reject(new Error(String(e))); }
          }, 60);
        });
      },
      on: function (type, fn) { if (!handlers[type]) handlers[type] = []; handlers[type].push(fn); }
    };
    return;
  }

  // ---------------- 真实宿主模式 ----------------
  var idSeq = 1;
  var pending = {};
  var handlers = {};

  function send(cmd, args, timeoutMs) {
    return new Promise(function (resolve, reject) {
      var id = idSeq++;
      pending[id] = { resolve: resolve, reject: reject };
      var payload = { id: id, cmd: cmd, args: args || {} };
      try {
        host.postMessage(JSON.stringify(payload));
      } catch (e) {
        delete pending[id];
        reject(new Error("postMessage 失败: " + e.message));
        return;
      }
      setTimeout(function () {
        if (pending[id]) {
          delete pending[id];
          reject(new Error(cmd + " 超时"));
        }
      }, timeoutMs || 60000);
    });
  }

  function on(type, fn) {
    if (!handlers[type]) handlers[type] = [];
    handlers[type].push(fn);
  }

  host.addEventListener("message", function (ev) {
    var msg;
    try { msg = JSON.parse(ev.data); } catch (e) { return; }
    if (!msg || !msg.type) return;

    if (msg.type === "response") {
      var p = pending[msg.id];
      if (!p) return;
      delete pending[msg.id];
      if (msg.ok) p.resolve(msg.data);
      else p.reject(new Error(msg.data || "操作失败"));
    } else {
      var list = handlers[msg.type];
      if (list) list.forEach(function (fn) {
        try { fn(msg); }
        catch (e) { try { console.error("[bridge] 事件处理器异常", msg.type, e); } catch (e2) { } }
      });
    }
  });

  window.Bridge = { send: send, on: on };
})();
