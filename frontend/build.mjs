/**
 * 前端构建：TypeScript → 浏览器直接可跑的原生 ES Module。
 *
 * 为什么不用 esbuild/webpack：
 *   * 这个前端只有几个模块、零第三方运行时依赖，打包器带来的收益很小；
 *   * 现场/离线机器不想依赖 node 工具链 —— tsc 编译完的 js 直接提交进仓库，
 *     拷过去就能跑；要改界面时，开发机上再跑一次本脚本即可；
 *   * 不打包还有一个好处：出问题时在浏览器 DevTools 里看到的就是源文件名。
 *
 * 产物：
 *   ../src/DwsEdge.Platform/wwwroot/js/*.js     tsc 输出（原生 ES Module）
 *   ../src/DwsEdge.Platform/wwwroot/app.css     styles.css 的副本
 *   ../src/DwsEdge.Platform/wwwroot/index.html  引用会被写上 ?v=<内容指纹>
 *
 * 版本号（现场升级不踩浏览器缓存）：
 *   js/css 的内容算一个 8 位指纹，写进 index.html 的引用、以及模块之间的 import：
 *       <link href="./app.css?v=ab12cd34">
 *       <script src="./js/main.js?v=ab12cd34">
 *       import { api } from "./api.js?v=ab12cd34"
 *   给子模块也加版本号是必须的：原生 ES Module 的 import 是独立 URL，
 *   只给入口加的话，api.js / dom.js 这些仍可能被浏览器缓存住。
 *   内容没变指纹就不变（重复构建不会产生无意义的改动）；内容一变所有 URL 都变。
 *
 * 用法：
 *   npm run build     一次性构建
 *   npm run check     只做类型检查（tsc --noEmit，不产出文件）
 *   npm run watch     tsc --watch + 样式监听，改了自动重新编译并重新打版本号
 */
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { copyFileSync, existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync, watch } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "..");
const wwwroot = path.join(root, "src", "DwsEdge.Platform", "wwwroot");
const jsOut = path.join(wwwroot, "js");
const cssSrc = path.join(here, "src", "styles.css");
const cssOut = path.join(wwwroot, "app.css");
const htmlOut = path.join(wwwroot, "index.html");

const VERSION_PATTERN = /\?v=[0-9a-f]{6,16}/g;

/**
 * 找 typescript 编译器：优先本工程 node_modules，其次全局 npm 安装的位置。
 * 直接用 `node tsc.js` 而不是 `tsc.cmd`，避免 Windows 下 shell 转义的各种坑。
 */
function findTsc() {
  const candidates = [
    path.join(here, "node_modules", "typescript", "lib", "tsc.js"),
    process.env.APPDATA ? path.join(process.env.APPDATA, "npm", "node_modules", "typescript", "lib", "tsc.js") : "",
    path.join(process.env.ProgramFiles ?? "", "nodejs", "node_modules", "typescript", "lib", "tsc.js")
  ].filter(Boolean);

  for (const candidate of candidates) {
    if (existsSync(candidate)) return candidate;
  }
  return null;
}

function runTsc(args) {
  const tsc = findTsc();
  if (!tsc) {
    return Promise.reject(
      new Error("找不到 typescript：请在 frontend 目录执行 npm install，或全局安装（npm i -g typescript）")
    );
  }

  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [tsc, ...args], { cwd: here, stdio: "inherit" });
    child.on("error", (err) => reject(new Error("调用 tsc 失败：" + err.message)));
    child.on("exit", (code) => (code === 0 ? resolve() : reject(new Error("tsc 退出码 " + code))));
  });
}

function copyCss() {
  mkdirSync(wwwroot, { recursive: true });
  copyFileSync(cssSrc, cssOut);
}

function jsFiles() {
  if (!existsSync(jsOut)) return [];
  return readdirSync(jsOut)
    .filter((f) => f.endsWith(".js"))
    .sort();
}

/**
 * 内容指纹。哈希前先把上一次打进去的 ?v=xxx 去掉，
 * 否则"版本号本身"会参与计算 → 每次构建指纹都变，缓存策略就白写了。
 */
function contentVersion() {
  const hash = createHash("sha256");
  hash.update(readFileSync(cssOut));
  for (const file of jsFiles()) {
    hash.update(file);
    hash.update(readFileSync(path.join(jsOut, file), "utf8").replace(VERSION_PATTERN, ""));
  }
  return hash.digest("hex").slice(0, 8);
}

/** 把版本号写进 js 模块的 import 与 index.html 的引用（幂等） */
function stampAssets(version) {
  let jsChanged = 0;

  for (const file of jsFiles()) {
    const full = path.join(jsOut, file);
    const text = readFileSync(full, "utf8");
    const stamped = text.replace(/from\s+"(\.[^"]+?\.js)(?:\?v=[0-9a-f]{6,16})?"/g, 'from "$1?v=' + version + '"');
    if (stamped !== text) {
      writeFileSync(full, stamped, "utf8");
      jsChanged++;
    }
  }

  let htmlChanged = false;
  if (existsSync(htmlOut)) {
    const html = readFileSync(htmlOut, "utf8");
    const stamped = html
      .replace(/(href=")\.\/app\.css(?:\?v=[0-9a-f]{6,16})?"/g, '$1./app.css?v=' + version + '"')
      .replace(/(src=")\.\/js\/main\.js(?:\?v=[0-9a-f]{6,16})?"/g, '$1./js/main.js?v=' + version + '"');
    if (stamped !== html) {
      writeFileSync(htmlOut, stamped, "utf8");
      htmlChanged = true;
    }
  }

  return { jsChanged, htmlChanged };
}

function buildOnce() {
  copyCss();
  const version = contentVersion();
  const result = stampAssets(version);
  return { version, ...result };
}

if (process.argv.includes("--watch")) {
  // 首次先打一遍（tsc --watch 会自己编译出 js）
  copyCss();

  let stampTimer = null;
  let stamping = false;
  const scheduleStamp = () => {
    if (stampTimer) clearTimeout(stampTimer);
    stampTimer = setTimeout(() => {
      stampTimer = null;
      if (stamping) return;
      stamping = true;
      try {
        const result = buildOnce();
        if (result.jsChanged || result.htmlChanged) {
          console.log("已更新版本号 → ?v=" + result.version);
        }
      } finally {
        stamping = false;
      }
    }, 400);
  };

  // tsc --watch 写出 js 之后自动重新打版本号；样式改动同样会触发
  if (existsSync(jsOut)) watch(jsOut, { persistent: true }, scheduleStamp);
  watch(cssSrc, { persistent: true }, () => {
    copyCss();
    scheduleStamp();
  });

  console.log("watch 模式：改 src/*.ts 或 src/styles.css 会自动重新编译并更新版本号（Ctrl+C 退出）");
  await runTsc(["-p", "tsconfig.build.json", "--watch", "--preserveWatchOutput"]);
} else {
  mkdirSync(jsOut, { recursive: true });
  await runTsc(["-p", "tsconfig.build.json"]);
  const result = buildOnce();
  console.log("前端构建完成：");
  console.log("  " + path.relative(root, jsOut) + "\\*.js（" + jsFiles().length + " 个模块）");
  console.log("  " + path.relative(root, cssOut));
  console.log("  " + path.relative(root, htmlOut) + " → ?v=" + result.version
    + (result.jsChanged + (result.htmlChanged ? 1 : 0) > 0 ? "（引用已更新）" : "（内容未变，引用保持不变）"));
}
