/**
 * P0：全局「一键应用」条。
 *
 * 背景：原来"一键应用配置"藏在配置页最底部（那一页有 50 多个输入框），现场要滚很久才找得到，
 * 而且改完触发模式/相机清单后没有任何"还有改动没应用"的提示。
 *
 * 现在它固定在页头下方，任何页签都看得见：
 *   * 改了触发模式或相机清单 → 整条变黄 + "有未保存改动"
 *   * 点「一键应用」→ 按钮禁用 + 已用时间在走，随时切页也不会中断
 *   * 结果（成功/失败 + 结论）直接显示在条上，原文收在「应用选项与输出」折叠区
 */
import { $ } from "./dom.js?v=d9347d14";
let baseline = { triggerMode: "", cameras: [] };
let readCameras = () => [];
let readTrigger = () => "";
let busy = false;
let startedAt = 0;
let ticker = null;
/** cfg 里的 triggerMode 数字 → 界面上的 hard/soft/free */
export function triggerModeToUi(mode) {
    const value = (mode ?? "").trim();
    if (value === "1")
        return "hard";
    if (value === "2")
        return "soft";
    if (value === "0")
        return "free";
    return value === "hard" || value === "soft" || value === "free" ? value : "";
}
/** 界面上的 hard/soft/free → cfg 里的 triggerMode 数字（应用时用不到，仅展示） */
export function triggerModeLabel(value) {
    if (value === "hard")
        return "硬触发（光电）";
    if (value === "soft")
        return "软触发（调试用）";
    if (value === "free")
        return "自由拉流（狂扫）";
    return value || "未设置";
}
function normalizeCameras(list) {
    return list
        .map((line) => line.trim().toLowerCase())
        .filter((line) => line.length > 0)
        .sort();
}
function sameCameras(a, b) {
    const x = normalizeCameras(a);
    const y = normalizeCameras(b);
    if (x.length !== y.length)
        return false;
    for (let i = 0; i < x.length; i++) {
        if (x[i] !== y[i])
            return false;
    }
    return true;
}
/** 服务器回来一份新配置时调用：重设基线并刷新显示 */
export function setApplyBaseline(triggerMode, cameras) {
    baseline = { triggerMode: triggerMode ?? "", cameras: cameras.slice() };
    refreshApplyBar();
}
/** 界面任何一处改动后调用：重新算"有没有未保存的改动" */
export function refreshApplyBar() {
    if (busy)
        return;
    const trigger = readTrigger();
    const cameras = readCameras();
    const triggerChanged = trigger !== "" && trigger !== baseline.triggerMode;
    const camerasChanged = !sameCameras(cameras, baseline.cameras);
    const dirty = triggerChanged || camerasChanged;
    const bar = $("applyBar");
    bar.classList.toggle("dirtyState", dirty);
    const flag = $("applyDirty");
    flag.classList.toggle("hidden", !dirty);
    const parts = [];
    parts.push("相机清单：" + (cameras.length > 0 ? cameras.length + " 台" : "（空）"));
    if (camerasChanged) {
        parts.push("（服务器上 " + baseline.cameras.length + " 台）");
    }
    parts.push("· 当前触发模式：" + triggerModeLabel(baseline.triggerMode));
    if (triggerChanged) {
        parts.push("→ " + triggerModeLabel(trigger));
    }
    $("applySummary").textContent = parts.join(" ");
}
/** 应用开始：禁用按钮 + 计时 */
export function applyBarStart() {
    busy = true;
    startedAt = Date.now();
    const button = $("btnApply");
    button.disabled = true;
    button.textContent = "应用中…";
    $("applyBadge").textContent = "";
    const status = $("applyStatus");
    status.className = "barItem muted";
    status.textContent = "正在写配置并启动 SDK 校验（最长约 4 分钟，可以切到别的页签，不会中断）…";
    if (ticker !== null)
        window.clearInterval(ticker);
    ticker = window.setInterval(() => {
        const seconds = Math.round((Date.now() - startedAt) / 1000);
        $("applyStatus").textContent = "正在写配置并启动 SDK 校验… 已用 " + seconds + " 秒（最长约 4 分钟）";
    }, 1000);
}
/** 应用结束 */
export function applyBarFinish(ok, conclusion) {
    busy = false;
    if (ticker !== null) {
        window.clearInterval(ticker);
        ticker = null;
    }
    const seconds = Math.round((Date.now() - startedAt) / 1000);
    const button = $("btnApply");
    button.disabled = false;
    button.textContent = "一键应用";
    const status = $("applyStatus");
    status.className = "barItem " + (ok ? "probe-ok" : "probe-bad");
    status.textContent = (ok ? "已应用" : "未应用") + "（用了 " + seconds + " 秒）：" + conclusion;
}
/** 应用失败/异常（没拿到结构化结果时） */
export function applyBarError(message) {
    applyBarFinish(false, message);
}
/** 组装按钮：传两个"读当前界面值"的回调，改动时条上能自己判断 */
export function initApplyBar(getCameras, getTrigger) {
    readCameras = getCameras;
    readTrigger = getTrigger;
    $("cfgTrigger").addEventListener("change", () => refreshApplyBar());
}
