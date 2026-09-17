/* ============================================================
   DeepSeek Harness 桌面工作台 — 前端应用逻辑
   ============================================================ */
(function () {
  "use strict";

  const B = window.Bridge;
  const $ = function (id) { return document.getElementById(id); };

  // ============ 全局状态 ============
  const State = {
    gateway: { state: "stopped", port: 3080, ready: false, pid: 0 },
    model: { llama: false, ollama: false, llamaPort: 11435, llamaDir: "", ollamaPath: "", backend: "llama.cpp" },
    env: null,
    backend: "llama.cpp",
    currentPage: "dashboard"
  };

  // ============ Toast ============
  function toast(text, kind) {
    kind = kind || "info";
    const box = $("toasts");
    const el = document.createElement("div");
    el.className = "toast " + kind;
    const ico = { success: "✓", error: "✕", info: "i" }[kind] || "i";
    el.innerHTML = '<span class="t-ico">' + ico + "</span><span></span>";
    el.lastChild.textContent = text;
    box.appendChild(el);
    setTimeout(function () { el.classList.add("out"); setTimeout(function () { el.remove(); }, 320); }, 3200);
  }
  B.on("toast", function (m) { toast(m.text, m.kind); });

  // ============ 主题系统 ============
  const PRESETS = {
    dark: {
      name: "暗色（默认）", isDark: true,
      bgType: "gradient", bg1: "#141a2e", bg2: "#0d0f1c", bgImage: "",
      blur: 14, alpha: 86, radius: 16, accent: "#4f8cff"
    },
    light: {
      name: "明亮", isDark: false,
      bgType: "gradient", bg1: "#eef1f8", bg2: "#dbe1ef", bgImage: "",
      blur: 12, alpha: 82, radius: 16, accent: "#2f6bff"
    },
    midnight: {
      name: "午夜蓝", isDark: true,
      bgType: "gradient", bg1: "#0c1428", bg2: "#060a18", bgImage: "",
      blur: 18, alpha: 90, radius: 18, accent: "#5b8cff"
    },
    aurora: {
      name: "极光", isDark: true,
      bgType: "gradient", bg1: "#1a1230", bg2: "#0b1a2e", bgImage: "",
      blur: 16, alpha: 84, radius: 18, accent: "#9b6bff"
    }
  };

  let currentTheme = null;

  function applyTheme(t) {
    const r = document.documentElement.style;
    const dark = t.isDark !== false;
    const base = dark ? [22, 26, 42] : [255, 255, 255];
    const alpha = (t.alpha == null ? 86 : t.alpha) / 100;

    r.setProperty("--panel-alpha", alpha);
    r.setProperty("--panel", "rgba(" + base.join(",") + "," + alpha + ")");
    r.setProperty("--panel-2", "rgba(" + base.join(",") + "," + (alpha * 0.78) + ")");
    r.setProperty("--border", dark ? "rgba(255,255,255,0.07)" : "rgba(0,0,0,0.10)");
    r.setProperty("--text", dark ? "#e8ecf6" : "#1d2433");
    r.setProperty("--text-sub", dark ? "#a6b0c4" : "#4d5769");
    r.setProperty("--text-faint", dark ? "#6b7590" : "#8a94a8");
    r.setProperty("--sidebg", dark ? "rgba(12,14,24,0.62)" : "rgba(255,255,255,0.62)");
    r.setProperty("--titlebar-bg", dark ? "rgba(12,14,24,0.55)" : "rgba(255,255,255,0.6)");
    r.setProperty("--accent", t.accent || "#4f8cff");
    r.setProperty("--radius", (t.radius == null ? 16 : t.radius) + "px");
    r.setProperty("--blur", (t.blur == null ? 14 : t.blur) + "px");

    const bg = $("bg-layer");
    if (t.bgType === "image" && t.bgImage) {
      bg.className = "bgimg";
      r.setProperty("--bgimg", "url(" + t.bgImage + ")");
    } else {
      bg.className = "";
      const c1 = t.bg1 || "#141a2e";
      const c2 = (t.bgType === "solid") ? (t.bgSolid || c1) : (t.bg2 || "#0d0f1c");
      r.setProperty("--bg1", c1);
      r.setProperty("--bg2", c2);
    }
  }

  function saveTheme() {
    B.send("saveTheme", { json: JSON.stringify(currentTheme) }).then(function () {
      toast("主题已保存", "success");
    }).catch(function () { toast("主题保存失败", "error"); });
  }

  function loadTheme() {
    return B.send("getTheme", {}).then(function (json) {
      if (json) {
        try { currentTheme = JSON.parse(json); } catch (e) { currentTheme = null; }
      }
      if (!currentTheme || !currentTheme.name) currentTheme = JSON.parse(JSON.stringify(PRESETS.dark));
      applyTheme(currentTheme);
      syncThemeControls();
    }).catch(function () {
      currentTheme = JSON.parse(JSON.stringify(PRESETS.dark));
      applyTheme(currentTheme);
      syncThemeControls();
    });
  }

  function syncThemeControls() {
    const t = currentTheme;
    setBgType(t.bgType || "gradient");
    $("bg-c1").value = t.bg1 || "#141a2e";
    $("bg-c2").value = t.bg2 || "#0d0f1c";
    $("bg-solid").value = t.bgSolid || "#0e1120";
    $("s-blur").value = t.blur == null ? 14 : t.blur;
    $("v-blur").textContent = t.blur == null ? 14 : t.blur;
    $("s-alpha").value = t.alpha == null ? 86 : t.alpha;
    $("v-alpha").textContent = t.alpha == null ? 86 : t.alpha;
    $("s-radius").value = t.radius == null ? 16 : t.radius;
    $("v-radius").textContent = t.radius == null ? 16 : t.radius;
    $("s-accent").value = t.accent || "#4f8cff";
    renderPresets();
  }

  function setBgType(type) {
    $("bg-type-seg").querySelectorAll("button").forEach(function (b) {
      b.classList.toggle("active", b.dataset.bgtype === type);
    });
    $("bg-grad-field").classList.toggle("hidden", type !== "gradient");
    $("bg-img-field").classList.toggle("hidden", type !== "image");
    $("bg-solid-field").classList.toggle("hidden", type !== "solid");
  }

  function renderPresets() {
    const grid = $("preset-grid");
    grid.innerHTML = "";
    Object.keys(PRESETS).forEach(function (key) {
      const p = PRESETS[key];
      const el = document.createElement("div");
      el.className = "preset-item" + (currentTheme && currentTheme.name === p.name ? " active" : "");
      const c1 = p.bg1, c2 = p.bg2;
      el.innerHTML = '<div class="preset-swatch" style="background:linear-gradient(135deg,' + c1 + ',' + c2 + ')"></div><span>' + p.name + "</span>";
      el.addEventListener("click", function () {
        currentTheme = JSON.parse(JSON.stringify(p));
        applyTheme(currentTheme);
        syncThemeControls();
        saveTheme();
      });
      grid.appendChild(el);
    });
  }

  // 背景图片上传
  $("bg-file").addEventListener("change", function () {
    const f = this.files && this.files[0];
    if (!f) return;
    const reader = new FileReader();
    reader.onload = function (ev) {
      B.send("saveUserBg", { dataUrl: ev.target.result }).then(function (path) {
        currentTheme.bgType = "image";
        currentTheme.bgImage = path;
        applyTheme(currentTheme);
        setBgType("image");
        saveTheme();
        toast("背景图片已应用", "success");
      }).catch(function () { toast("背景图片保存失败", "error"); });
    };
    reader.readAsDataURL(f);
  });

  // 主题控件实时预览
  function bindThemeControls() {
    $("bg-type-seg").querySelectorAll("button").forEach(function (b) {
      b.addEventListener("click", function () {
        currentTheme.bgType = b.dataset.bgtype;
        setBgType(currentTheme.bgType);
        applyTheme(currentTheme);
      });
    });
    ["bg-c1", "bg-c2", "bg-solid"].forEach(function (id) {
      $(id).addEventListener("input", function () {
        if (id === "bg-c1") currentTheme.bg1 = this.value;
        else if (id === "bg-c2") currentTheme.bg2 = this.value;
        else currentTheme.bgSolid = this.value;
        applyTheme(currentTheme);
      });
    });
    ["s-blur", "s-alpha", "s-radius"].forEach(function (id) {
      $(id).addEventListener("input", function () {
        const v = parseInt(this.value, 10);
        const nameMap = { "s-blur": "blur", "s-alpha": "alpha", "s-radius": "radius" };
        currentTheme[nameMap[id]] = v;
        $("v-" + id.slice(2)).textContent = v;
        applyTheme(currentTheme);
      });
    });
    $("s-accent").addEventListener("input", function () {
      currentTheme.accent = this.value;
      applyTheme(currentTheme);
    });
    $("theme-apply").addEventListener("click", saveTheme);
    $("theme-reset").addEventListener("click", function () {
      currentTheme = JSON.parse(JSON.stringify(PRESETS.dark));
      applyTheme(currentTheme);
      syncThemeControls();
      saveTheme();
    });
  }

  // ============ 日志 ============
  const logMax = 1500;
  function addLog(line) {
    const body = $("log-body");
    const d = document.createElement("div");
    d.className = "log-line";
    let text = line;
    let cls = "";
    if (/^\[dsh\]/i.test(line)) cls = "dsh";
    else if (/^\[llama/i.test(line)) cls = "llama";
    else if (/^\[ollama/i.test(line)) cls = "ollama";
    else if (/^\[plugin/i.test(line)) cls = "plugin";
    if (/error|fail|exception|错误|失败/i.test(line)) cls = "err";
    const now = new Date();
    const ts = ("0" + now.getHours()).slice(-2) + ":" + ("0" + now.getMinutes()).slice(-2) + ":" + ("0" + now.getSeconds()).slice(-2);
    d.className = "log-line " + cls;
    d.innerHTML = '<span class="t">' + ts + "</span>" + escapeHtml(text);
    body.appendChild(d);
    while (body.childNodes.length > logMax) body.removeChild(body.firstChild);
    if ($("log-follow").checked) body.scrollTop = body.scrollHeight;
  }
  function escapeHtml(s) {
    return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  }
  B.on("log", function (m) { addLog(m.line); });
  $("log-clear").addEventListener("click", function () { $("log-body").innerHTML = ""; });

  // ============ 状态渲染 ============
  function setBadge(el, text, kind) {
    el.textContent = text;
    el.className = "sc-badge" + (kind ? " " + kind : "");
  }

  function updateState(d) {
    if (!d) return;
    if (d.gateway) State.gateway = Object.assign(State.gateway, d.gateway);
    if (d.model) State.model = Object.assign(State.model, d.model);
    renderGateway();
    renderModel();
    renderPill();
    syncSidePop();
  }

  function renderGateway() {
    const g = State.gateway;
    const st = g.state; // stopped/starting/running/error
    const gwDot = $("gw-dot"), gwState = $("gw-state"), gwDetail = $("gw-detail");
    if (st === "running" && g.ready) {
      gwDot.className = "status-dot ok"; gwState.textContent = "服务运行中";
      setBadge($("gw-badge"), "运行中", "ok");
    } else if (st === "starting") {
      gwDot.className = "status-dot starting"; gwState.textContent = "启动中…";
      setBadge($("gw-badge"), "启动中", "starting");
    } else if (st === "error") {
      gwDot.className = "status-dot err"; gwState.textContent = "服务异常";
      setBadge($("gw-badge"), "异常", "err");
    } else {
      gwDot.className = "status-dot"; gwState.textContent = "未启动";
      setBadge($("gw-badge"), "未启动");
    }
    gwDetail.textContent = "http://127.0.0.1:" + g.port;

    const running = st === "running" || g.ready;
    $("gw-start").disabled = running || st === "starting";
    $("gw-stop").disabled = !running;
    $("btn-gateway-toggle").textContent = running ? "停止服务" : "启动服务";
    $("btn-gateway-toggle").classList.toggle("primary", !running);
    $("guide-gateway").classList.toggle("hidden", running || st === "starting");
    // 引导文案动态化：结合本地模型底座状态
    if (!running && st !== "starting") {
      const m = State.model;
      if (m.llama || m.ollama) {
        $("guide-gateway").querySelector("h3").textContent = "模型底座就绪，请启动 Harness 网关";
        $("guide-desc").textContent = "本地模型已在运行，启动网关后即可在此内嵌官方 Harness 界面，开启对话、工具调用与 MCP 能力。";
      } else {
        $("guide-gateway").querySelector("h3").textContent = "一切就绪，启动你的 Harness 网关";
        $("guide-desc").textContent = "网关启动后即可使用官方界面。本地模型底座为按需加载，不会在启动时占用性能，需要时再到「本地模型」页一键加载。";
      }
    }

    // 侧栏状态
    const ss = $("side-status");
    ss.className = "side-status" + (running ? " ok" : (st === "starting" ? " starting" : (st === "error" ? " err" : "")));
    $("ss-title").textContent = running ? "服务运行中" : (st === "starting" ? "服务启动中…" : (st === "error" ? "服务异常" : "服务未启动"));
    $("ss-sub").textContent = "端口 " + g.port;
  }

  function renderModel() {
    const m = State.model;
    const mdDot = $("md-dot"), mdState = $("md-state"), mdDetail = $("md-detail");
    let running = false, label = "未加载", kind = "", detail = "按需加载 · 不占用启动性能";
    if (m.llama) { running = true; label = "llama.cpp 运行中"; kind = "ok"; detail = "端口 " + m.llamaPort; }
    else if (m.ollama) { running = true; label = "Ollama 运行中"; kind = "ok"; detail = "端口 11434"; }
    mdDot.className = "status-dot" + (running ? " ok" : "");
    mdState.textContent = label;
    setBadge($("md-badge"), running ? "运行中" : "未加载", running ? "ok" : "");
    mdDetail.textContent = detail;
    $("md-start").disabled = running;
    $("md-stop").disabled = !running;
    $("m-run-badge").textContent = running ? "运行中" : "未运行";
    $("m-run-badge").className = "sc-badge" + (running ? " ok" : "");
    $("m-run-state").textContent = running ? "模型服务运行中" : "未运行";
    $("m-run").disabled = running;
    $("m-stop").disabled = !running;
  }

  function renderPill() {
    const pill = $("svc-pill");
    const g = State.gateway;
    pill.className = "pill";
    if (g.state === "running" && g.ready) { pill.classList.add("ok"); $("pill-text").textContent = "服务运行中 · " + g.port; }
    else if (g.state === "starting") { pill.classList.add("starting"); $("pill-text").textContent = "服务启动中…"; }
    else if (g.state === "error") { pill.classList.add("err"); $("pill-text").textContent = "服务异常"; }
    else $("pill-text").textContent = "服务未启动";
  }

  function renderEnv() {
    const e = State.env;
    if (!e) return;
    $("env-node").textContent = e.node || "-";
    $("env-dsh").textContent = e.dsh || "-";
    $("env-port").textContent = State.gateway.port;
    const ok = e.nodeOk && e.dshOk;
    setBadge($("env-badge"), ok ? "正常" : "待部署", ok ? "ok" : "");
    // 设置页
    $("s-port").value = State.gateway.port;
    $("s-lport").value = State.model.llamaPort || 11435;
    $("a-os").textContent = e.os || "-";
    $("a-version").textContent = "桌面工作台 · 便携版";
    renderSetup();
  }

  function refreshEnv() {
    B.send("getEnv", {}).then(function (e) {
      State.env = e;
      // DPI 补偿：流式布局（100vw/100vh）下用 CSS zoom 放大渲染，
      // 保证界面尺寸/字号与系统 DPI（150% 等）匹配，又不产生 scale 留白
      try {
        var d = parseFloat(e.dpi) || 1;
        if (d >= 1 && d <= 4) document.documentElement.style.zoom = d;
      } catch (err) { }
      renderEnv();
      // 填充模型配置：左卡=模型目录(DSH_LLAMA_DIR)，右卡=llama.cpp 目录(LlamaExeDir)
      if (e.modelDir) $("m-dir").textContent = e.modelDir;
      if (e.llamaDir) $("m-dir-input").value = e.llamaDir;
      State.model.modelDir = e.modelDir || "";
      State.model.llamaDir = e.llamaDir || "";
      $("s-autostart").classList.toggle("on", !!e.autoStart);
      return loadConfigIntoForm();
    }).catch(function () { });
  }

  // ============ 页面切换 ============
  function showPage(name) {
    State.currentPage = name;
    document.querySelectorAll("#nav .nav-item").forEach(function (it) {
      it.classList.toggle("active", it.dataset.page === name);
    });
    document.querySelectorAll(".page").forEach(function (p) {
      p.classList.remove("active");
      if (p.id === "page-" + name) p.classList.add("active");
    });
    if (name === "harness") refreshHarness();
    if (name === "plugins") refreshPlugins();
    if (name === "models") refreshModelPage();
    if (name === "settings") refreshSettings();
    if (name === "setup") refreshSetup();
  }

  // ============ 网关操作 ============
  function startGateway() {
    $("btn-gateway-toggle").disabled = true;
    toast("正在启动 Harness 网关…", "info");
    B.send("startGateway", {}).then(function () {
      toast("网关启动指令已发送", "success");
    }).catch(function (e) {
      toast("启动失败：" + e.message, "error");
    }).then(function () {
      $("btn-gateway-toggle").disabled = false;
    });
  }
  function stopGateway() {
    toast("正在停止 Harness 网关…", "info");
    B.send("stopGateway", {}).then(function () {
      toast("网关已停止", "success");
    }).catch(function (e) {
      toast("停止失败：" + e.message, "error");
    });
  }
  function gatewayToggle() {
    const g = State.gateway;
    if (g.state === "running" || g.ready) stopGateway();
    else startGateway();
  }
  $("btn-gateway-toggle").addEventListener("click", gatewayToggle);
  $("gw-start").addEventListener("click", startGateway);
  $("gw-stop").addEventListener("click", stopGateway);
  $("guide-start").addEventListener("click", startGateway);
  $("gw-log").addEventListener("click", function () { showPage("dashboard"); $("log-body").scrollTop = $("log-body").scrollHeight; });
  $("guide-log").addEventListener("click", function () { $("log-body").scrollTop = $("log-body").scrollHeight; });

  // ============ Harness 工作台 ============
  function refreshHarness() {
    const frame = $("hx-frame");
    const g = State.gateway;
    const status = $("hx-statusbar");
    if (g.state === "running" && g.ready) {
      status.className = "hx-statusbar ok";
      status.textContent = "已连接 " + "http://127.0.0.1:" + g.port;
      if (frame.getAttribute("src") !== "http://127.0.0.1:" + g.port) {
        frame.setAttribute("src", "http://127.0.0.1:" + g.port);
      }
    } else {
      status.className = "hx-statusbar";
      status.textContent = g.state === "starting" ? "服务启动中…" : "服务未启动，启动后可在此使用官方界面";
      if (g.state !== "starting") frame.setAttribute("src", "about:blank");
    }
  }
  $("hx-refresh").addEventListener("click", function () {
    const frame = $("hx-frame");
    const src = frame.getAttribute("src");
    frame.setAttribute("src", "about:blank");
    setTimeout(function () { if (src !== "about:blank") frame.setAttribute("src", src); }, 150);
    toast("已刷新 Harness 界面", "success");
  });
  $("hx-browser").addEventListener("click", function () {
    B.send("openExternal", { url: "http://127.0.0.1:" + State.gateway.port });
  });
  $("btn-open-browser").addEventListener("click", function () {
    B.send("openExternal", { url: "http://127.0.0.1:" + State.gateway.port });
  });

  // ============ 模型管理 ============
  function fmtSize(n) {
    if (n == null) return "";
    const gb = n / (1024 * 1024 * 1024);
    if (gb >= 1) return gb.toFixed(2) + " GB";
    return (n / (1024 * 1024)).toFixed(0) + " MB";
  }

  function refreshModelPage() {
    // 左卡：模型扫描目录（含 GGUF）；右卡：llama.cpp 安装目录（含 llama-server.exe）
    const dir = State.model.modelDir || State.model.llamaDir;
    if (dir) {
      $("m-dir").textContent = dir;
      scanGguf(dir);
    }
    if (State.model.llamaDir) $("m-dir-input").value = State.model.llamaDir;
    // 动态提示：LlamaExeDir() 检测到 llama-server.exe 才返回非空
    const hint = $("m-dir-hint");
    if (hint) {
      if (State.model.llamaDir) hint.textContent = "已就绪（检测到 llama-server.exe）";
      else hint.textContent = "未检测到 llama-server.exe，请选择 llama.cpp 目录";
      hint.classList.toggle("ok", !!State.model.llamaDir);
    }
    syncBackendTabs();
  }

  function syncBackendTabs() {
    const be = State.model.backend || "llama.cpp";
    State.backend = be;
    $("backend-tabs").querySelectorAll(".bt").forEach(function (b) {
      b.classList.toggle("active", b.dataset.backend === be);
    });
    document.querySelectorAll(".ollama-only").forEach(function (el) {
      el.classList.toggle("hidden", be !== "ollama");
    });
    const row = $("row-llama-dir");
    if (row) row.classList.toggle("hidden", be === "ollama");
    $("m-cfg-title").textContent = be === "ollama" ? "Ollama 启动配置" : "llama.cpp 启动配置";
  }

  $("backend-tabs").addEventListener("click", function (ev) {
    const b = ev.target.closest(".bt");
    if (!b) return;
    State.backend = b.dataset.backend;
    syncBackendTabs();
    B.send("setConfig", { key: "DSH_MODEL_BACKEND", value: State.backend }).catch(function () { });
  });

  function scanGguf(dir) {
    const list = $("m-list");
    list.innerHTML = '<div class="empty">扫描中…</div>';
    B.send("scanGGUF", { dir: dir }).then(function (files) {
      if (!files || !files.length) {
        list.innerHTML = '<div class="empty">未找到 GGUF 文件，请检查目录</div>';
        return;
      }
      list.innerHTML = "";
      files.forEach(function (f) {
        const el = document.createElement("div");
        el.className = "model-item";
        el.innerHTML = '<span class="mi-name">' + escapeHtml(f.Name) + '</span><span class="mi-size">' + fmtSize(f.SizeBytes) + "</span>";
        el.addEventListener("click", function () {
          list.querySelectorAll(".model-item").forEach(function (i) { i.classList.remove("selected"); });
          el.classList.add("selected");
          $("m-model").value = f.Path;
        });
        list.appendChild(el);
      });
    }).catch(function () {
      list.innerHTML = '<div class="empty">扫描失败</div>';
    });
  }

  $("m-scan").addEventListener("click", function () {
    const dir = $("m-dir").textContent || $("m-dir-input").value;
    if (dir) scanGguf(dir);
  });
  $("m-browse").addEventListener("click", function () {
    B.send("pickFolder", { title: "选择包含 GGUF 模型的文件夹" }).then(function (dir) {
      if (!dir) return;
      $("m-dir").textContent = dir;
      State.model.modelDir = dir;
      scanGguf(dir);
      B.send("setConfig", { key: "DSH_LLAMA_DIR", value: dir }).catch(function () { });
    });
  });
  $("m-dir-pick").addEventListener("click", function () {
    B.send("pickFolder", { title: "选择 llama.cpp 目录（含 llama-server.exe）" }).then(function (dir) {
      if (!dir) return;
      $("m-dir-input").value = dir;
      State.model.llamaDir = dir;
      B.send("setConfig", { key: "DSH_LLAMA_EXE_DIR", value: dir }).catch(function () { });
    });
  });

  function collectModelConfig() {
    if (State.backend === "ollama") {
      return {
        backend: "ollama",
        ollamaPath: $("m-ollama-exe").value || State.model.ollamaPath,
        ollamaModel: $("m-ollama-model").value
      };
    }
    return {
      backend: "llama.cpp",
      dir: $("m-dir-input").value || State.model.llamaDir,
      model: $("m-model").value,
      ctx: $("m-ctx").value || "4096",
      gpu: $("m-gpu").value || "99",
      extra: $("m-extra").value || ""
    };
  }

  $("m-run").addEventListener("click", function () {
    const cfg = collectModelConfig();
    if (cfg.backend === "llama.cpp" && (!cfg.dir || !cfg.model)) {
      toast("请先选择模型目录和 GGUF 文件", "error");
      return;
    }
    if (cfg.backend === "ollama" && !cfg.ollamaModel) {
      toast("请输入 Ollama 模型名", "error");
      return;
    }
    toast("正在启动本地模型…", "info");
    B.send("startModel", cfg).then(function (ok) {
      if (ok === "started") toast("本地模型已启动", "success");
      else toast("模型启动失败，请查看日志", "error");
    }).catch(function (e) {
      toast("启动失败：" + e.message, "error");
    });
  });
  $("m-stop").addEventListener("click", function () {
    B.send("stopModel", { backend: State.backend }).then(function () {
      toast("模型已停止", "success");
    }).catch(function (e) { toast("停止失败：" + e.message, "error"); });
  });
  $("md-start").addEventListener("click", function () {
    showPage("models");
    setTimeout(function () { $("m-run").click(); }, 60);
  });
  $("md-stop").addEventListener("click", function () {
    B.send("stopAllModels", {}).then(function () { toast("本地模型已停止", "success"); });
  });
  $("md-config").addEventListener("click", function () { showPage("models"); });
  $("env-recheck").addEventListener("click", function () { refreshEnv(); toast("已重新检测运行环境", "success"); });

  // ============ 插件 ============
  const RECOMMENDED = [
    "@deepseek-ai/dsh-openai-bridge",
    "@deepseek-ai/dsh-better-sidebar",
    "@deepseek-ai/dsh-plugin-mcp",
    "@deepseek-ai/dsh-plugin-git",
    "dsh-plugin-web-search",
    "dsh-plugin-terminal"
  ];
  function refreshPlugins() {
    B.send("listPlugins", {}).then(function (list) {
      const box = $("pl-list");
      if (!list || !list.length) {
        box.innerHTML = '<div class="empty">尚未安装插件，可在上方输入包名安装</div>';
      } else {
        box.innerHTML = "";
        list.forEach(function (p) {
          const el = document.createElement("div");
          el.className = "pl-item";
          el.innerHTML = '<span class="pi-name">' + escapeHtml(p.Name) + '</span><span class="pi-ver">' + escapeHtml(p.Version) + '</span>' +
            '<button class="btn small ghost pl-un">卸载</button>';
          el.querySelector(".pl-un").addEventListener("click", function () {
            el.querySelector(".pl-un").disabled = true;
            B.send("uninstallPlugin", { pkg: p.Name }).then(function (nl) {
              toast("插件已卸载：" + p.Name, "success");
              renderPluginList(nl);
            }).catch(function (e) { toast(e.message, "error"); });
          });
          box.appendChild(el);
        });
      }
    }).catch(function () {
      $("pl-list").innerHTML = '<div class="empty">加载失败</div>';
    });
    renderRecommended();
  }
  function renderPluginList(list) {
    const box = $("pl-list");
    box.innerHTML = "";
    if (!list || !list.length) { box.innerHTML = '<div class="empty">尚未安装插件</div>'; return; }
    list.forEach(function (p) {
      const el = document.createElement("div");
      el.className = "pl-item";
      el.innerHTML = '<span class="pi-name">' + escapeHtml(p.Name) + '</span><span class="pi-ver">' + escapeHtml(p.Version) + "</span>";
      box.appendChild(el);
    });
  }
  function renderRecommended() {
    const box = $("pl-rec");
    box.innerHTML = "";
    RECOMMENDED.forEach(function (pkg) {
      const el = document.createElement("div");
      el.className = "rec-chip";
      el.innerHTML = '<span class="rc-name">' + escapeHtml(pkg) + '</span><span class="rc-add">+</span>';
      el.addEventListener("click", function () {
        B.send("installPlugin", { pkg: pkg }).then(function (nl) {
          renderPluginList(nl);
          refreshPlugins();
        }).catch(function (e) { toast(e.message, "error"); });
      });
      box.appendChild(el);
    });
  }
  $("pl-refresh").addEventListener("click", refreshPlugins);
  $("pl-install").addEventListener("click", function () {
    const pkg = $("pl-pkg").value.trim();
    if (!pkg) { toast("请输入插件包名", "error"); return; }
    B.send("installPlugin", { pkg: pkg }).then(function (nl) {
      renderPluginList(nl);
      $("pl-pkg").value = "";
    }).catch(function (e) { toast(e.message, "error"); });
  });
  $("pl-pkg").addEventListener("keydown", function (e) {
    if (e.key === "Enter") $("pl-install").click();
  });

  // ============ 设置 ============
  function loadConfigIntoForm() {
    return B.send("getConfig", {}).then(function (cfg) {
      if (!cfg) return;
      $("s-port").value = cfg.DSH_PORT || State.gateway.port;
      $("s-lport").value = cfg.DSH_LLAMA_PORT || 11435;
      // 密钥绝不回显：只显示掩码提示与是否已配置
      const sec = cfg.secret || {};
      if ($("s-apikey")) {
        $("s-apikey").value = "";
        $("s-apikey").placeholder = sec.hasKey
          ? ("已保存 " + (sec.hint || "") + "（留空则不修改，输入新值则覆盖）")
          : "sk-…（可选，用于云端 API）";
      }
      $("s-endpoints").value = cfg.DSH_EXTRA_ENDPOINTS || "";
      $("s-autostart").classList.toggle("on", cfg.DSH_AUTO_START_MODEL === "1");
      $("m-ctx").value = cfg.DSH_LLAMA_CONTEXT || "4096";
      $("m-gpu").value = cfg.DSH_LLAMA_GPU_LAYERS || "99";
      $("m-extra").value = cfg.DSH_LLAMA_EXTRA_ARGS || "";
      $("m-ollama-exe").value = cfg.DSH_OLLAMA_EXE || State.model.ollamaPath || "";
      $("m-ollama-model").value = cfg.DSH_OLLAMA_MODEL || "";
    }).catch(function () { });
  }
  function refreshSettings() { loadConfigIntoForm(); }

  $("s-autostart").addEventListener("click", function () {
    this.classList.toggle("on");
  });
  $("s-port-save").addEventListener("click", function () {
    const v = $("s-port").value.trim();
    B.send("setConfig", { key: "DSH_PORT", value: v }).then(function (msg) {
      toast(msg || "端口已保存", "success");
      State.gateway.port = parseInt(v, 10) || State.gateway.port;
    }).catch(function (e) { toast(e.message, "error"); });
  });
  $("s-lport-save").addEventListener("click", function () {
    B.send("setConfig", { key: "DSH_LLAMA_PORT", value: $("s-lport").value.trim() }).then(function (msg) {
      toast(msg || "模型端口已保存", "success");
    }).catch(function (e) { toast(e.message, "error"); });
  });
  $("s-save").addEventListener("click", function () {
    const set = function (k, v) { return B.send("setConfig", { key: k, value: v }); };
    const p = [];
    const typedKey = $("s-apikey").value.trim();
    if (typedKey) p.push(set("DEEPSEEK_API_KEY", typedKey));   // 留空 = 不修改，避免误清空
    p.push(set("DSH_EXTRA_ENDPOINTS", $("s-endpoints").value.trim()));
    p.push(set("DSH_AUTO_START_MODEL", $("s-autostart").classList.contains("on") ? "1" : "0"));
    p.push(set("DSH_LLAMA_CONTEXT", $("m-ctx").value));
    p.push(set("DSH_LLAMA_GPU_LAYERS", $("m-gpu").value));
    p.push(set("DSH_LLAMA_EXTRA_ARGS", $("m-extra").value.trim()));
    p.push(set("DSH_OLLAMA_EXE", $("m-ollama-exe").value.trim()));
    p.push(set("DSH_OLLAMA_MODEL", $("m-ollama-model").value.trim()));
    Promise.all(p).then(function () {
      $("s-save-state").textContent = "已保存 " + new Date().toLocaleTimeString();
      toast("配置已保存", "success");
    }).catch(function (e) { toast("保存失败：" + e.message, "error"); });
  });

  // 设置页签
  $("settings-tabs").addEventListener("click", function (ev) {
    const b = ev.target.closest(".st");
    if (!b) return;
    document.querySelectorAll("#settings-tabs .st").forEach(function (x) { x.classList.remove("active"); });
    b.classList.add("active");
    document.querySelectorAll(".settings-pane").forEach(function (p) { p.classList.remove("active"); });
    $("tab-" + b.dataset.tab).classList.add("active");
  });

  // ============ 侧栏收起 ============
  $("collapseBtn").addEventListener("click", function () {
    const sb = $("sidebar");
    sb.classList.toggle("collapsed");
    const isCollapsed = sb.classList.contains("collapsed");
    // 图标/提示随状态切换：收起后显示右箭头（可再次点击展开），避免按钮消失无法展开
    this.innerHTML = isCollapsed ? "&#xE76B;" : "&#xE76C;";
    this.title = isCollapsed ? "展开侧栏" : "收起侧栏";
  });

  // ============ 左下角状态组件：点击展开快捷操作 ============
  const sp = $("side-pop");
  function syncSidePop() {
    const g = State.gateway, m = State.model;
    const gwRunning = g.state === "running" || g.ready;
    const gwEl = $("sp-gw-state");
    gwEl.textContent = gwRunning ? "运行中" : (g.state === "starting" ? "启动中…" : (g.state === "error" ? "异常" : "未启动"));
    gwEl.className = gwRunning ? "ok" : (g.state === "starting" ? "starting" : (g.state === "error" ? "err" : ""));
    const mRunning = m.llama || m.ollama;
    const mdEl = $("sp-model-state");
    mdEl.textContent = mRunning ? "运行中" : "未加载";
    mdEl.className = mRunning ? "ok" : "";
    // 快捷项动作：网关
    const spGw = $("sp-gw");
    spGw.onclick = function () {
      if (gwRunning) stopGateway(); else startGateway();
      toggleSidePop(false);
    };
    // 快捷项动作：模型（跳转本地模型页）
    $("sp-model").onclick = function () { toggleSidePop(false); showPage("models"); };
    // 快捷项动作：日志（切到仪表盘并滚动日志）
    $("sp-log").onclick = function () { toggleSidePop(false); showPage("dashboard"); $("log-body").scrollTop = $("log-body").scrollHeight; };
    // 快捷项动作：浏览器打开
    $("sp-open").onclick = function () { toggleSidePop(false); B.send("openExternal", { url: "http://127.0.0.1:" + g.port }); };
  }
  function toggleSidePop(force) {
    const willShow = force !== undefined ? force : sp.classList.contains("hidden");
    if (willShow) {
      // 用 fixed 定位相对视口，避免被侧栏 overflow:hidden 裁剪
      const sr = $("side-status").getBoundingClientRect();
      sp.style.left = sr.left + "px";
      sp.style.width = sr.width + "px";
      sp.style.display = "flex";
      sp.classList.remove("hidden");
      // 面板高度未定时先放一次，再根据真实高度微调，保证不超出视口
      sp.style.top = "auto";
      sp.style.bottom = (window.innerHeight - sr.bottom + 8) + "px";
      const rh = sp.getBoundingClientRect();
      if (rh.top < 8) {
        sp.style.top = "8px";
        sp.style.bottom = "auto";
      }
      $("ss-caret").style.transform = "rotate(180deg)";
    } else {
      sp.classList.add("hidden");
      $("ss-caret").style.transform = "";
    }
    syncSidePop();
  }
  $("side-status").addEventListener("click", function (ev) {
    ev.stopPropagation();
    toggleSidePop();
  });
  document.addEventListener("click", function (ev) {
    if (!sp.classList.contains("hidden") && !ev.target.closest("#side-pop")) toggleSidePop(false);
  });

  // ============ 导航 & 窗口 ============
  document.querySelectorAll("#nav .nav-item").forEach(function (it) {
    it.addEventListener("click", function () { showPage(it.dataset.page); });
  });

  document.querySelectorAll(".tb-btn").forEach(function (b) {
    b.addEventListener("click", function () {
      const act = b.dataset.win;
      if (act === "min") B.send("windowMin", {});
      else if (act === "max") B.send("windowMaxToggle", {});
      else if (act === "close") B.send("windowClose", {});
    });
  });
  // 窗口边缘缩放 + 标题栏拖动：pointer 事件 + 指针捕获（社区标准方案，已验证稳定）。
  // pointerdown 立即启动原生后台循环（读全局鼠标，鼠标移出窗口也持续）；setPointerCapture 让
  // pointerup 可靠到达；单击防黏住由原生“未移动超时”+ 原生“忽略过早 End”双重保证。
  (function () {
    var EDGE = 8, edgeDir = null, winActive = null, capturedEl = null, pdTime = 0;
    var CURSORS = { l: "ew-resize", r: "ew-resize", t: "ns-resize", b: "ns-resize",
      tl: "nwse-resize", br: "nwse-resize", tr: "nesw-resize", bl: "nesw-resize" };
    document.addEventListener("pointermove", function (e) {
      var vw = window.innerWidth, vh = window.innerHeight;
      var l = e.clientX < EDGE, r = e.clientX > vw - EDGE;
      var t = e.clientY < EDGE, b = e.clientY > vh - EDGE;
      var d = null;
      if (l && t) d = "tl"; else if (r && t) d = "tr"; else if (l && b) d = "bl"; else if (r && b) d = "br";
      else if (l) d = "l"; else if (r) d = "r"; else if (t) d = "t"; else if (b) d = "b";
      if (d !== edgeDir) {
        edgeDir = d;
        document.body.style.cursor = d ? CURSORS[d] : "";
      }
    });
    document.addEventListener("pointerdown", function (e) {
      if (e.button !== 0) return;
      winActive = null;
      pdTime = Date.now();
      // 直接在 pointerdown 时检测边缘坐标（快速点击/跳转也稳定识别）
      var vw = window.innerWidth, vh = window.innerHeight;
      var l = e.clientX < EDGE, r = e.clientX > vw - EDGE;
      var t = e.clientY < EDGE, b = e.clientY > vh - EDGE;
      var d = null;
      if (l && t) d = "tl"; else if (r && t) d = "tr"; else if (l && b) d = "bl"; else if (r && b) d = "br";
      else if (l) d = "l"; else if (r) d = "r"; else if (t) d = "t"; else if (b) d = "b";
      if (d) {
        e.preventDefault();
        edgeDir = null;
        document.body.style.cursor = "";
        winActive = "resize:" + d;
        try { document.body.setPointerCapture(e.pointerId); capturedEl = document.body; } catch (err) { }
        B.send("windowResize", { dir: d });
        return;
      }
      // 标题栏左侧拖动窗口
      var tb = document.getElementById("tb-drag");
      if (tb) {
        var r = tb.getBoundingClientRect();
        if (e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom) {
          e.preventDefault();
          winActive = "drag";
          try { document.body.setPointerCapture(e.pointerId); capturedEl = document.body; } catch (err) { }
          B.send("windowDrag", {});
        }
      }
    });
    document.addEventListener("pointerup", function (e) {
      var act = winActive;
      winActive = null;
      if (capturedEl) { try { capturedEl.releasePointerCapture(e.pointerId); } catch (err) { } capturedEl = null; }
      // WebView2 在顶部边缘 mousedown 后偶发 1~2ms 的 pointercancel/pointerup 竞争：若此时发 End 会让
      // 原生循环刚启动就中断（上缘缩放/拖动卡住）。故 pointerdown 后 100ms 内的 pointerup 视为竞争，
      // 不发 End（循环由原生 GetAsyncKeyState 左键松开 / 未移动超时可靠停止）；真实拖动结束（>100ms）才发 End。
      if (Date.now() - pdTime < 100) return;
      var cmd = act === "drag" ? "windowDragEnd" : (act ? "windowResizeEnd" : null);
      if (cmd) B.send(cmd, {});
    });
    document.addEventListener("pointercancel", function (e) {
      var act = winActive;
      winActive = null;
      if (capturedEl) { try { capturedEl.releasePointerCapture(e.pointerId); } catch (err) { } capturedEl = null; }
      if (Date.now() - pdTime < 100) return;
      var cmd = act === "drag" ? "windowDragEnd" : (act ? "windowResizeEnd" : null);
      if (cmd) B.send(cmd, {});
    });
  })();


  // ============ 环境配置页 ============
  // 统一请求包装：每个调用都带 catch，避免未处理的 Promise rejection 静默失败
  function req(cmd, args, ms) {
    return B.send(cmd, args, ms).catch(function (e) {
      toast((e && e.message) ? e.message : (cmd + " 失败"), "error");
      throw e;
    });
  }

  const STEP_LABEL = { ok: "正常", fixed: "已自动修复", warn: "需注意", failed: "失败", skipped: "已跳过" };
  const MODE_LABEL = { Passphrase: "口令模式（换机可用）", Dpapi: "本机模式（免口令，不可换机）", None: "未设置" };

  function renderStartupNotes(notes) {
    const card = $("setup-notes-card"), box = $("setup-notes");
    if (!card || !box) return;
    if (!notes || !notes.length) { card.style.display = "none"; box.innerHTML = ""; return; }
    card.style.display = "";
    box.innerHTML = "";
    notes.forEach(function (n) {
      const d = document.createElement("div");
      d.className = "setup-note";
      d.textContent = n;
      box.appendChild(d);
    });
  }

  function renderSetupReport(rep) {
    const box = $("setup-report");
    if (!box) return;
    if (!rep || !rep.Steps || !rep.Steps.length) {
      box.innerHTML = '<div class="hint">尚未运行。点右上角「一键配置环境」开始。</div>';
      return;
    }
    box.innerHTML = "";
    rep.Steps.forEach(function (s) {
      const row = document.createElement("div");
      row.className = "setup-step " + (s.Status || "ok");
      const dot = document.createElement("span"); dot.className = "sdot";
      const name = document.createElement("span"); name.className = "sname"; name.textContent = s.Name || "";
      const det = document.createElement("span"); det.className = "sdetail"; det.textContent = s.Detail || "";
      const tag = document.createElement("span"); tag.className = "stext";
      tag.textContent = STEP_LABEL[s.Status] || s.Status || "";
      row.appendChild(dot); row.appendChild(name); row.appendChild(det); row.appendChild(tag);
      box.appendChild(row);
    });
  }

  function renderSetup() {
    const e = State.env || {};
    const sec = e.secret || {};
    if ($("setup-mode")) $("setup-mode").textContent = MODE_LABEL[sec.mode] || sec.mode || "未设置";
    if ($("setup-unlocked")) $("setup-unlocked").textContent = sec.unlocked ? "已解锁" : "未解锁（需输入口令）";
    if ($("setup-key")) $("setup-key").textContent = sec.hasKey ? ("已保存 " + (sec.hint || "")) : "未配置";
    if ($("setup-job")) $("setup-job").textContent = e.jobObject ? "已启用（退出时子进程由内核回收）" : "未启用";
    if ($("setup-node")) $("setup-node").textContent = (e.node || "-") + (e.nodeOk ? "" : "  ← 缺失");
    if ($("setup-dsh")) $("setup-dsh").textContent = (e.dsh || "-") + (e.dshOk ? "" : "  ← 缺失");
    if ($("setup-machine")) $("setup-machine").textContent = (e.lastSetup && e.lastSetup.MachineId) || "-";

    if ($("setup-passhint")) {
      $("setup-passhint").textContent = sec.error
        ? sec.error
        : (sec.unlocked
            ? "凭据已解锁，本机后续启动无需再输入。换到新电脑时在此输入一次口令即可。"
            : "请输入访问口令以解锁加密凭据库。换机后必须输入一次 —— 这是「不明文存储」的必然代价。");
    }
    // 换机后未解锁时，把凭据卡片高亮出来，避免用户找不到该输口令的地方
    if ($("setup-passcard")) {
      const attention = !sec.unlocked && (sec.hasKey || (e.startupNotes && e.startupNotes.length));
      $("setup-passcard").classList.toggle("attention", !!attention);
    }
    renderStartupNotes(e.startupNotes);
    renderSetupReport(e.lastSetup);
  }

  function refreshSetup() {
    return B.send("getEnv", {}).then(function (e) {
      State.env = e;
      renderSetup();
    }).catch(function () { });
  }

  function bindSetup() {
    if (!$("setup-run")) return;

    $("setup-run").addEventListener("click", function () {
      const btn = this;
      btn.disabled = true; btn.textContent = "配置中…";
      toast("正在配置环境（运行时缺失时会自动下载，可能需要几分钟）…", "info");
      B.send("envSetup", {}).then(function (rep) {
        toast(rep && rep.AllGood ? "环境配置完成" : "配置完成，但有需注意的项，见下方列表",
              rep && rep.AllGood ? "success" : "info");
        return refreshSetup();
      }).catch(function (e) {
        toast("配置失败：" + ((e && e.message) || "未知错误"), "error");
      }).then(function () {
        btn.disabled = false; btn.textContent = "一键配置环境";
      });
    });

    $("setup-unlock").addEventListener("click", function () {
      const pw = $("setup-pass").value;
      if (!pw) { toast("请先输入口令", "error"); return; }
      req("unlockSecrets", { passphrase: pw }).then(function () {
        $("setup-pass").value = "";
        toast("凭据已解锁", "success");
        return refreshEnv();
      }).catch(function () { });
    });

    $("setup-mode-pass").addEventListener("click", function () {
      const pw = $("setup-pass").value;
      if (!pw || pw.length < 6) { toast("请先输入至少 6 位的口令", "error"); return; }
      req("setSecretMode", { mode: "passphrase", passphrase: pw }).then(function () {
        $("setup-pass").value = "";
        toast("已设为口令模式：凭据文件可随盘换机", "success");
        return refreshEnv();
      }).catch(function () { });
    });

    $("setup-mode-dpapi").addEventListener("click", function () {
      req("setSecretMode", { mode: "dpapi" }).then(function () {
        toast("已设为本机模式：免口令，但换机需重新填写密钥", "success");
        return refreshEnv();
      }).catch(function () { });
    });

    $("setup-savekey").addEventListener("click", function () {
      const k = $("setup-apikey").value.trim();
      if (!k) { toast("请输入密钥", "error"); return; }
      req("setConfig", { key: "DEEPSEEK_API_KEY", value: k }).then(function (msg) {
        $("setup-apikey").value = "";
        toast(msg || "密钥已保存", "success");
        return refreshEnv();
      }).catch(function () { });
    });

    $("setup-clearkey").addEventListener("click", function () {
      req("clearSecret", {}).then(function () {
        toast("密钥已清除", "success");
        return refreshEnv();
      }).catch(function () { });
    });

    $("setup-forget").addEventListener("click", function () {
      req("forgetUnlock", {}).then(function () {
        toast("已忘记本机口令，下次启动需要重新输入", "success");
        return refreshSetup();
      }).catch(function () { });
    });
  }
  bindSetup();

  // ============ 初始化 ============
  B.on("state", function (m) { updateState(m.data); });

  function init() {
    bindThemeControls();
    // 先加载主题（会触发 applyTheme 与 renderPresets），避免 currentTheme 为空时抛错
    loadTheme().then(function () {
      renderPresets();
      // 加载环境
      return refreshEnv();
    }).then(function () {
      // 获取初始状态
      return B.send("getState", {});
    }).then(function (d) {
      updateState(d);
    }).catch(function () { });
    // 欢迎日志
    addLog("[app] DeepSeek Harness 桌面工作台已启动，欢迎使用。");
  }

  // 窗口自适应：布局已改为 100vw/100vh 流式（CSS 层面自动铺满，无留白），
  // 无需 transform scale；DPI 补偿由 refreshEnv 中的 CSS zoom 完成
  document.addEventListener("DOMContentLoaded", function () { });

  document.addEventListener("DOMContentLoaded", init);
})();
