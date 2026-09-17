/**
 * 下游 TCP 输出（B4）：配置 + 状态 + 测试连接 + 模板预览 + 下发日志。
 *
 * 现场最常干的三件事：改模板看一眼长什么样、测一下连不连得通、看哪条没发出去。
 */
import { api } from "./api.js?v=665be265";
import { $, badge, cell, clear, el, notify } from "./dom.js?v=665be265";
export function initDownstream() {
    $("btnDsSave").addEventListener("click", () => void save());
    $("btnDsTest").addEventListener("click", () => void testConnection());
    $("btnDsPreview").addEventListener("click", () => void preview());
    $("btnDsRefresh").addEventListener("click", () => void refreshDownstream());
}
export async function refreshDownstream() {
    const res = await api.downstream();
    if (res.status !== 200 || !res.data) {
        $("dsSummary").textContent = "读取失败：" + (res.message ?? "HTTP " + res.status);
        return;
    }
    const config = res.data.config;
    $("dsEnabled").checked = config.enabled;
    $("dsHost").value = config.host;
    $("dsPort").value = String(config.port);
    $("dsTemplate").value = config.template;
    $("dsEncoding").value = config.encoding ?? "utf-8";
    $("dsRetry").value = String(config.retryIntervalMs);
    $("dsOnlyComplete").checked = config.sendOnlyComplete;
    $("dsFields").textContent = "可用字段：" + res.data.templateFields.map((f) => "{" + f + "}").join(" ");
    renderStats(res.data.stats, res.data.file);
    await loadLog();
}
function readOptions() {
    return {
        enabled: $("dsEnabled").checked,
        protocol: "tcp-client",
        host: $("dsHost").value.trim(),
        port: Number($("dsPort").value) || 0,
        template: $("dsTemplate").value,
        encoding: $("dsEncoding").value,
        connectTimeoutMs: 3000,
        retryIntervalMs: Number($("dsRetry").value) || 5000,
        maxAttempts: 0,
        sendOnlyComplete: $("dsOnlyComplete").checked,
        sendIntervalMs: 0
    };
}
function renderStats(stats, file) {
    const connected = stats.connected ? "已连接" : (stats.enabled ? "未连接" : "未启用");
    $("dsSummary").textContent =
        "目标 " + stats.target + " · " + connected +
            (stats.connectedSince ? "（自 " + stats.connectedSince + "）" : "") +
            " · 已下发 " + stats.sent + " 条" +
            " · 失败 " + stats.failed +
            " · 待发 " + stats.queueDepth +
            " · 重试 " + stats.retries +
            (stats.lastSentAt ? " · 最近发送 " + stats.lastSentAt : "") +
            (stats.lastError ? " · 最近错误：" + stats.lastError : "");
    const problems = $("dsProblems");
    if (stats.templateProblems?.length) {
        badge(problems, "模板问题：" + stats.templateProblems.join("；"), "err");
    }
    else {
        clear(problems);
    }
    $("dsFile").textContent = "配置文件：" + file;
}
async function save() {
    $("dsMsg").textContent = "保存中…";
    const res = await api.saveDownstream(readOptions());
    if (res.status === 200 && res.data?.ok) {
        badge($("dsBadge"), "已保存", "ok");
        $("dsMsg").textContent = "已保存并生效（发送服务会按新配置重连）";
        await refreshDownstream();
    }
    else {
        badge($("dsBadge"), "保存失败", "err");
        $("dsMsg").textContent = "保存失败：" + (res.message ?? "HTTP " + res.status);
    }
}
async function testConnection() {
    $("dsMsg").textContent = "测试连接中…";
    const res = await api.testDownstream(readOptions());
    const data = res.data;
    if (data?.ok) {
        $("dsMsg").textContent = "连接成功：" + (data.target ?? "") + "（" + (data.note ?? "") + "）";
        badge($("dsBadge"), "连接正常", "ok");
    }
    else {
        $("dsMsg").textContent = "连接失败：" + ((data && data.error) || res.message || "HTTP " + res.status);
        badge($("dsBadge"), "连接失败", "err");
    }
}
async function preview() {
    const res = await api.previewDownstream($("dsTemplate").value);
    if (res.status !== 200 || !res.data) {
        $("dsPreview").textContent = "预览失败：" + (res.message ?? "HTTP " + res.status);
        return;
    }
    const data = res.data;
    $("dsPreview").textContent =
        (data.usingSample ? "（用最近一条包裹渲染）" : "（还没有包裹，只是把转义还原）") + "\n" +
            data.rendered + "\n" +
            "字节数：" + data.bytes +
            (data.problems?.length ? "\n问题：" + data.problems.join("；") : "");
    if (data.problems?.length) {
        badge($("dsBadge"), "模板有问题", "warn");
    }
}
async function loadLog() {
    const res = await api.downstreamLog(50);
    const body = $("dsLogRows");
    clear(body);
    if (res.status !== 200 || !res.data) {
        return;
    }
    const list = Array.isArray(res.data) ? res.data : [res.data];
    for (const item of list) {
        const tr = el("tr");
        tr.appendChild(cell(item.time, "muted"));
        tr.appendChild(cell(item.traceId ?? "（连接）", item.traceId ? "code" : "muted"));
        tr.appendChild(cell(item.success ? "成功" : "失败", item.success ? "" : "noread"));
        tr.appendChild(cell(item.bytes));
        tr.appendChild(cell(item.message, "muted"));
        tr.appendChild(cell(item.error ?? "", "muted"));
        body.appendChild(tr);
    }
}
/** 供别处复用：确认后立刻重发失败项（当前实现=把 failed 留在队列里等自动重试） */
export function retryHint() {
    notify("失败的下发会自动重试（默认每 5 秒一次）；改好下游地址后无需手动干预");
}
