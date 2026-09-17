/**
 * 配置页（A8）：读当前 SDK 配置 + 一键应用（写配置 → 重启校验 → 失败自动回滚）。
 *
 * 界面调的就是命令行那条路径（tools\apply-config.ps1），所以不会出现"界面能过、命令行过不了"。
 */
import { api } from "./api.js?v=2f1f6302";
import { $, badge, el, notify } from "./dom.js?v=2f1f6302";
const TRIGGER_LABEL = {
    hard: "硬触发（光电）",
    soft: "软触发",
    free: "自由拉流（狂扫）"
};
let summary = null;
export function initConfig() {
    $("btnReloadConfig").addEventListener("click", () => void refreshConfig());
    $("btnFillCameras").addEventListener("click", () => fillCamerasFromConfig());
    $("btnApply").addEventListener("click", () => void applyConfig());
}
export async function refreshConfig() {
    const res = await api.config();
    if (res.status !== 200 || !res.data)
        return;
    summary = res.data;
    render();
}
function render() {
    const c = summary;
    if (!c)
        return;
    const kv = $("cfgSummary");
    kv.textContent = "";
    const rows = [
        ["运行目录", c.runtimeRoot],
        ["SDK 配置文件", c.cfgPath + (c.cfgExists ? "" : "（不存在）")],
        ["provider", c.provider ?? "—"],
        ["相机模式", "mode=" + c.mode + " num=" + c.num + " randWorkMode=" + c.randWorkMode],
        ["启用相机", c.enabledCameras + " 台（声明 " + c.declaredCameras + " 条）"],
        ["触发模式", (TRIGGER_LABEL[c.triggerName] ?? c.triggerName ?? "?") + "（triggerMode=" + c.triggerMode + "）"],
        ["方位映射", c.positionsCount + " 条 · " + c.positionsFile],
        ["一键应用脚本", (c.toolsReady ? "可用 " : "未找到 ") + c.applyScript]
    ];
    for (const [key, value] of rows) {
        kv.appendChild(el("div", key, "k"));
        kv.appendChild(el("div", value));
    }
    if (!c.toolsReady) {
        badge(kv, "找不到 apply-config.ps1：先跑一次 build.ps1（会把 tools 拷到 runtime\\tools）", "err");
    }
    if (c.problems?.length) {
        badge(kv, "配置问题：" + c.problems.join("；"), "err");
    }
    if (c.warnings?.length) {
        badge(kv, "提醒：" + c.warnings.join("；"), "warn");
    }
    fillCamerasFromConfig();
    if (c.lastApply) {
        renderApplyResult(c.lastApply, "（上次应用：" + (c.lastApply.at ?? "") + "）\n\n");
    }
}
function fillCamerasFromConfig() {
    if (!summary?.cameras?.length)
        return;
    const textarea = $("cfgCameras");
    textarea.value = summary.cameras.map((x) => x.line).join("\r\n");
}
function renderApplyResult(result, header = "") {
    $("applyOut").textContent =
        header +
            "结论：" + result.conclusion + "\n" +
            "退出码：" + result.exitCode + " · 耗时 " + (result.durationMs / 1000).toFixed(1) + " 秒\n" +
            (result.commandLine ? "命令：" + result.commandLine + "\n" : "") +
            (result.error ? "错误：" + result.error + "\n" : "") +
            "----------------------------------------\n" +
            (result.output || "（没有输出）");
    badge($("applyBadge"), "退出码 " + result.exitCode + " · " + result.conclusion, exitKind(result.exitCode));
}
function exitKind(exitCode) {
    if (exitCode === 0)
        return "ok";
    if (exitCode === 1 || exitCode === 2)
        return "warn";
    return "err";
}
async function applyConfig() {
    const triggerMode = $("cfgTrigger").value;
    const useCameras = $("cfgUseCameras").checked;
    const skipVerify = $("cfgSkipVerify").checked;
    let cameras = null;
    if (useCameras) {
        cameras = $("cfgCameras")
            .value.split(/\r?\n/)
            .map((line) => line.trim())
            .filter((line) => line.length > 0 && !line.startsWith("#"));
        if (!cameras.length) {
            notify("相机清单是空的：请先填写，或取消勾选「应用上面的相机清单」");
            return;
        }
    }
    if (!triggerMode && !cameras) {
        notify("至少要改一项：触发模式 或 相机清单");
        return;
    }
    const body = {
        triggerMode,
        cameras,
        skipVerify,
        stopHost: $("cfgStopHost").checked,
        restartHost: $("cfgRestartHost").checked
    };
    const button = $("btnApply");
    button.disabled = true;
    button.textContent = "应用中…（启动 SDK 校验，最长 4 分钟）";
    $("applyBadge").textContent = "";
    $("applyOut").textContent =
        "正在执行：\n" + JSON.stringify(body, null, 2) + "\n\n（校验会真的启动一次 SDK，请稍等…）";
    try {
        const res = await api.applyConfig(body);
        if (res.data) {
            renderApplyResult(res.data);
        }
        else {
            $("applyOut").textContent = "调用失败：" + (res.message ?? "HTTP " + res.status);
            badge($("applyBadge"), "调用失败", "err");
        }
    }
    finally {
        button.disabled = false;
        button.textContent = "一键应用";
        await refreshConfig();
    }
}
