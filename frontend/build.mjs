/**
 * 前端构建：TypeScript → 浏览器直接可跑的原生 ES Module。
 *
 * 为什么不用 esbuild/webpack：
 *   * 这个前端只有五六个模块、零第三方运行时依赖，打包器带来的收益很小；
 *   * 现场/离线机器不想依赖 node 工具链 —— tsc 编译完的 js 直接提交进仓库，
 *     拷过去就能跑；要改界面时，开发机上再跑一次本脚本即可；
 *   * 不打包还有一个好处：出问题时在浏览器 DevTools 里看到的就是源文件名。
 *
 * 产物：
 *   ../src/DwsEdge.Platform/wwwroot/js/*.js   （tsc 输出，模块之间用相对路径 import）
 *   ../src/DwsEdge.Platform/wwwroot/app.css   （src/styles.css 的副本）
 *
 * 用法：
 *   npm run build     一次性构建
 *   npm run check     只做类型检查（tsc --noEmit，不产出文件）
 *   npm run watch     tsc --watch + 样式监听（改了自动重新编译）
 */
import { spawn } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, watch } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "..");
const wwwroot = path.join(root, "src", "DwsEdge.Platform", "wwwroot");
const jsOut = path.join(wwwroot, "js");
const cssSrc = path.join(here, "src", "styles.css");
const cssOut = path.join(wwwroot, "app.css");

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

if (process.argv.includes("--watch")) {
  copyCss();
  watch(cssSrc, { persistent: true }, () => {
    copyCss();
    console.log("styles.css 已更新 → app.css");
  });
  await runTsc(["-p", "tsconfig.build.json", "--watch", "--preserveWatchOutput"]);
} else {
  mkdirSync(jsOut, { recursive: true });
  await runTsc(["-p", "tsconfig.build.json"]);
  copyCss();
  console.log("前端构建完成：");
  console.log("  " + path.relative(root, jsOut) + "\\*.js");
  console.log("  " + path.relative(root, cssOut));
}
