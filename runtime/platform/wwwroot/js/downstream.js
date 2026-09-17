/**
 * 下游 TCP 输出（B4）：配置 + 状态 + 测试连接 + 模板预览 + 下发日志。
 *
 * 现场最常干的三件事：改模板看一眼长什么样、测一下连不连得通、看哪条没发出去。
 */
import { api } from "./api.js?v=4e5f5a2b";
import { $, badge, cell, clear, el, notify } from "./dom.js?v=4e5f5a2b";
export function initDownstream() {
    $("btnDsSave").addEventListener("click", () => void save());
    $("btnDsTest").addEventListener("click", () => void testConnection());
    $("btnDsPreview").addEventListener("click", () => void preview());
    $("btnDsRefresh").addEventListener("click", () => void refreshDownstream());
    $("dsProtocol").addEventListener("change", () => {
        applyProtocolLabels($("dsProtocol").value);
    });
}
export async function refreshDownstream() {
    const res = await api.downstream();
    if (res.status !== 200 || !res.data) {
        $("dsSummary").textContent = "读取失败：" + (res.message ?? "HTTP " + res.status);
        return;
    }
    const config = res.data.config;
    $("dsEnabled").checked = config.enabled;
    $("dsProtocol").value = config.protocol === "tcp-server" ? "tcp-server" : "tcp-client";
    $("dsHost").value = config.host;
    $("dsPort").value = String(config.port);
    $("dsReplay").value = String(config.replayRecentCount ?? 0);
    $("dsUrl").value = config.url ?? "";
    $("dsTimeout").value = String(config.httpTimeoutMs ?? 5000);
    $("dsIdemHeader").value = config.idempotencyHeader ?? "Idempotency-Key";
    $("dsHeaders").value = (config.headers ?? []).join("; ");
    $("dsTemplate").value = config.template;
    $("dsEncoding").value = config.encoding ?? "utf-8";
    $("dsRetry").value = String(config.retryIntervalMs);
    $("dsOnlyComplete").checked = config.sendOnlyComplete;
    $("dsFields").textContent = "可用字段：" + res.data.templateFields.map((f) => "{" + f + "}").join(" ");
    applyProtocolLabels(config.protocol);
    renderStats(res.data.stats, res.data.file);
    renderClients(res.data.stats);
    await loadLog();
}
/** 三种模式下字段含义不同：标签、可见行跟着变 */
function applyProtocolLabels(protocol) {
    const serverMode = protocol === "tcp-server";
    const httpMode = protocol === "http";
    $("dsHostLabel").textContent = serverMode ? "绑定地址：" : "下游地址：";
    $("dsPortLabel").textContent = serverMode ? "监听端口：" : "下游端口：";
    $("dsHost").setAttribute("placeholder", serverMode ? "0.0.0.0" : "127.0.0.1");
    $("dsReplayLabel").style.display = serverMode ? "" : "none";
    $("dsHttpRow").style.display = httpMode ? "" : "none";
    $("dsHeadersRow").style.display = httpMode ? "" : "none";
}
function renderClients(stats) {
    const box = $("dsClientsBox");
    box.style.display = stats.serverMode ? "" : "none";
    if (!stats.serverMode) {
        return;
    }
    const body = $("dsClientRows");
    clear(body);
    const clients = stats.clients ?? [];
    for (const client of clients) {
        const tr = el("tr");
        tr.appendChild(cell(client.id, "code"));
        tr.appendChild(cell(client.remote, "muted"));
        tr.appendChild(cell(client.connectedAt, "muted"));
        tr.appendChild(cell(client.sent));
        tr.appendChild(cell(client.bytes));
        tr.appendChild(cell(client.lastError ?? "", "muted"));
        body.appendChild(tr);
    }
    if (!clients.length) {
        const tr = el("tr");
        const td = el("td", "还没有下游接入（平台已在本机监听，等下游连上来）", "muted");
        td.colSpan = 6;
        tr.appendChild(td);
        body.appendChild(tr);
    }
}
function readOptions() {
    return {
        enabled: $("dsEnabled").checked,
        protocol: $("dsProtocol").value,
        host: $("dsHost").value.trim(),
        port: Number($("dsPort").value) || 0,
        template: $("dsTemplate").value,
        encoding: $("dsEncoding").value,
        connectTimeoutMs: 3000,
        retryIntervalMs: Number($("dsRetry").value) || 5000,
        maxAttempts: 0,
        sendOnlyComplete: $("dsOnlyComplete").checked,
        sendIntervalMs: 0,
        replayRecentCount: Number($("dsReplay").value) || 0,
        url: $("dsUrl").value.trim(),
        httpTimeoutMs: Number($("dsTimeout").value) || 5000,
        contentType: "text/plain; charset=utf-8",
        idempotencyHeader: $("dsIdemHeader").value.trim() || "Idempotency-Key",
        headers: $("dsHeaders").value
            .split(";")
            .map((s) => s.trim())
            .filter((s) => s.length > 0)
    };
}
function renderStats(stats, file) {
    const connected = stats.connected ? "已连接" : (stats.enabled ? "未连接" : "未启用");
    $("dsSummary").textContent =
        (stats.httpMode
            ? "HTTP 模式 · 目标 " + (stats.httpTarget ?? "") + " · 幂等键头 " + (stats.idempotencyHeader ?? "")
            : stats.serverMode
                ? "服务端模式 · 监听 " + (stats.listenTarget ?? stats.target) + " · " + (stats.listening ? "监听中" : "未监听") +
                    " · 在线客户端 " + stats.clientCount
                : "客户端模式 · 目标 " + stats.target + " · " + connected) +
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
