/**
 * B8：相机状态监控与告警。
 *
 * 界面上分三块：
 *   * 实时监控页顶部的"告警条" —— 有活动告警就变红/黄，点一下跳到设备信息页看细节；
 *   * 设备信息页的"相机状态监控" —— 在线率、心跳、掉线次数、当前状态时长 + 掉线/告警记录；
 *   * 配置页的"监控与告警阈值" —— 心跳超时、离线告警、频繁掉线、在线率下限等阈值。
 *
 * 数据来源有两个，故意都留着：
 *   * SSE 的 type=monitor 快照（平台按检查间隔推，界面上的秒数/在线率会自己走，不用轮询）；
 *   * /api/monitor/* 接口（切页、点刷新时拉一次，断线后也能补上）。
 */
import { api } from "./api.js?v=38da8d49";
import { $, badge, cell, clear, dash, el } from "./dom.js?v=38da8d49";
const EVENT_LABEL = {
    offline: "掉线",
    online: "上线",
    recovered: "恢复",
    "alert-raised": "告警",
    "alert-cleared": "告警恢复"
};
const CODE_LABEL = {
    "camera-offline": "相机离线",
    "heartbeat-timeout": "心跳超时",
    "frequent-offline": "频繁掉线",
    "low-online-rate": "在线率过低",
    "declared-missing": "清单里没发现"
};
/** 告警码 → 中文（C2 的相机墙也用同一份文案，避免两处不一致） */
export function alertLabel(code) {
    return CODE_LABEL[code] ?? code;
}
const EVENT_LIMIT = 200;
let eventRowsEl;
let camRowsEl;
let alertBarEl;
let monMsgEl;
/** 最近一次快照（渲染表格要用），以及"是否只看活动告警" */
let snapshot = null;
let events = [];
let alerts = [];
export function initMonitor() {
    eventRowsEl = $("monEventRows");
    camRowsEl = $("monRows");
    alertBarEl = $("alertBar");
    monMsgEl = $("monMsg");
    $("btnRefreshMonitor").addEventListener("click", () => {
        void refreshMonitor(true);
    });
    $("monOnlyActive").addEventListener("change", () => {
        void loadEvents();
    });
    // 告警条点一下 = 跳到设备信息页（那里有完整记录）
    alertBarEl.addEventListener("click", () => {
        $("tab-devices").click();
    });
    initMonitorConfig();
}
/** SSE 推来的快照：界面上的在线率/心跳靠它自己走 */
export function renderMonitorSnapshot(data) {
    snapshot = data;
    if (Array.isArray(data.alerts)) {
        alerts = data.alerts;
    }
    renderSummary(data.summary);
    renderCameras(data.cameras ?? []);
    renderAlertBar(data.summary, data.alerts ?? []);
}
/** SSE 推来的单条告警（产生/恢复）—— 就地更新告警条，不等下一次快照 */
export function renderAlert(alert) {
    if (!alert)
        return;
    const index = alerts.findIndex((a) => a.id === alert.id);
    if (alert.active) {
        if (index >= 0)
            alerts[index] = alert;
        else
            alerts = [alert, ...alerts];
    }
    else if (index >= 0) {
        alerts[index] = alert;
    }
    else {
        alerts = [alert, ...alerts];
    }
    if (snapshot?.summary) {
        const active = alerts.filter((a) => a.active);
        snapshot.summary = { ...snapshot.summary, activeAlerts: active.length };
        renderSummary(snapshot.summary);
        renderAlertBar(snapshot.summary, alerts);
    }
}
function renderSummary(s) {
    if (!s)
        return;
    $("monCameras").textContent = String(s.cameras ?? 0);
    $("monOnline").textContent = String(s.online ?? 0) + " / " + String(s.cameras ?? 0);
    $("monRate").textContent = fmtRate(s.averageOnlineRatePercent);
    $("monAlerts").textContent = String(s.activeAlerts ?? 0);
}
/** 在线率为 -1 表示"刚上线，样本还不够" */
export function fmtRate(rate) {
    if (rate === undefined || rate === null || rate < 0)
        return "—";
    return rate.toFixed(1) + "%";
}
/** 心跳新鲜度（秒 → "3 秒前 / 5 分钟前"） */
export function fmtAge(seconds) {
    if (seconds === undefined || seconds === null || seconds < 0)
        return "—";
    if (seconds < 60)
        return seconds + " 秒前";
    if (seconds < 3600)
        return Math.floor(seconds / 60) + " 分钟前";
    return (seconds / 3600).toFixed(1) + " 小时前";
}
function fmtDuration(seconds) {
    const value = Math.max(0, Math.floor(seconds ?? 0));
    if (value < 60)
        return value + " 秒";
    if (value < 3600)
        return Math.floor(value / 60) + " 分 " + (value % 60) + " 秒";
    return (value / 3600).toFixed(1) + " 小时";
}
function renderCameras(list) {
    clear(camRowsEl);
    const sorted = [...list].sort((a, b) => a.camera.localeCompare(b.camera));
    for (const item of sorted) {
        const tr = el("tr");
        if (!item.online)
            tr.className = "offline";
        tr.appendChild(cell(item.camera, "code"));
        tr.appendChild(cell(item.online ? "在线" : "离线", item.online ? "" : "noread"));
        tr.appendChild(cell(fmtRate(item.onlineRatePercent)));
        tr.appendChild(cell(fmtAge(item.lastHeartbeatAgeSeconds)));
        tr.appendChild(cell(dash(item.lastCodeAt), "muted"));
        tr.appendChild(cell(item.codeCount ?? 0));
        tr.appendChild(cell(item.offlineCount ?? 0));
        tr.appendChild(cell(fmtDuration(item.currentStateSeconds) + (item.online ? "（在线）" : "（离线）")));
        const alertTd = el("td");
        if ((item.alerts ?? []).length === 0) {
            alertTd.className = "muted";
            alertTd.textContent = "—";
        }
        else {
            for (const a of item.alerts) {
                alertTd.appendChild(el("span", CODE_LABEL[a.code] ?? a.code, "badge " + (a.severity === "critical" ? "err" : "warn")));
                alertTd.appendChild(document.createTextNode(" "));
            }
        }
        tr.appendChild(alertTd);
        camRowsEl.appendChild(tr);
    }
}
function renderAlertBar(s, list) {
    const active = (list ?? []).filter((a) => a.active);
    clear(alertBarEl);
    alertBarEl.className = "alertbar " + (active.length === 0 ? "ok" : active.some(isCritical) ? "err" : "warn");
    if (active.length === 0) {
        alertBarEl.appendChild(el("span", "相机状态正常：" + (s?.online ?? 0) + " / " + (s?.cameras ?? 0) + " 台在线，平均在线率 " + fmtRate(s?.averageOnlineRatePercent), ""));
        return;
    }
    alertBarEl.appendChild(el("span", "相机告警 " + active.length + " 条：", "title"));
    const show = active.slice(0, 3);
    for (const a of show) {
        alertBarEl.appendChild(el("span", "[" + (CODE_LABEL[a.code] ?? a.code) + "] " + a.camera + " " + a.message + "  ", "item"));
    }
    if (active.length > show.length) {
        alertBarEl.appendChild(el("span", "等 " + active.length + " 条（点这里看全部）", "more"));
    }
    else {
        alertBarEl.appendChild(el("span", "（点这里看全部）", "more"));
    }
}
function isCritical(a) {
    return a.severity === "critical";
}
function renderEvents() {
    clear(eventRowsEl);
    const onlyActive = $("monOnlyActive").checked;
    const filtered = onlyActive ? events.filter((e) => e.event === "alert-raised") : events;
    for (const item of filtered) {
        const tr = el("tr");
        if (item.event === "offline" || item.event === "alert-raised")
            tr.className = "offline";
        tr.appendChild(cell(item.time, "muted"));
        tr.appendChild(cell(item.camera, "code"));
        tr.appendChild(cell(EVENT_LABEL[item.event] ?? item.event));
        tr.appendChild(cell(item.detail));
        tr.appendChild(cell(item.offlineDurationMs > 0 ? (item.offlineDurationMs / 1000).toFixed(1) + " 秒" : "—", "muted"));
        eventRowsEl.appendChild(tr);
    }
}
/** 拉一次监控数据（切到设备页、点刷新、断线重连后都走这里） */
export async function refreshMonitor(showMessage = false) {
    const [summary, cameras] = await Promise.all([api.monitorSummary(), api.monitorCameras()]);
    if (summary.data)
        renderSummary(summary.data);
    if (cameras.data)
        renderCameras(cameras.data);
    const alertRes = await api.monitorAlerts(100, false);
    if (alertRes.data?.items) {
        alerts = alertRes.data.items;
        renderAlertBar(summary.data ?? undefined, alerts);
    }
    await loadEvents();
    if (showMessage) {
        badge(monMsgEl, "已刷新 " + new Date().toLocaleTimeString(), "ok");
    }
}
async function loadEvents() {
    const res = await api.monitorEvents(EVENT_LIMIT);
    events = res.data ?? [];
    renderEvents();
}
// ---------------------------------------------------------------- 配置页：阈值
export function initMonitorConfig() {
    $("btnMonReload").addEventListener("click", () => {
        void refreshMonitorConfig();
    });
    $("btnMonDefaults").addEventListener("click", () => {
        fillMonitorConfig(DEFAULT_OPTIONS);
        badge($("monConfigBadge"), "已填入默认值（点保存才落盘）", "warn");
    });
    $("btnMonSave").addEventListener("click", () => {
        void saveMonitorConfig();
    });
}
const DEFAULT_OPTIONS = {
    enabled: true,
    heartbeatTimeoutSeconds: 60,
    offlineAlertSeconds: 10,
    frequentOfflineCount: 3,
    frequentOfflineWindowMinutes: 30,
    onlineRateAlertPercent: 95,
    onlineRateWindowMinutes: 60,
    checkIntervalSeconds: 5,
    eventRetentionDays: 30
};
export async function refreshMonitorConfig() {
    const res = await api.monitorConfig();
    if (!res.data) {
        $("monConfigFile").textContent = "读取失败：" + (res.message ?? "未知错误");
        return;
    }
    fillMonitorConfig(res.data.options);
    $("monConfigFile").textContent =
        "配置文件：" + res.data.file + "　事件目录：" + res.data.dataDirectory;
}
function num(id) {
    const value = Number.parseInt($(id).value, 10);
    return Number.isFinite(value) ? value : 0;
}
function fillMonitorConfig(options) {
    const o = options ?? DEFAULT_OPTIONS;
    $("monEnabled").checked = o.enabled !== false;
    $("monHeartbeat").value = String(o.heartbeatTimeoutSeconds ?? 60);
    $("monOfflineAlert").value = String(o.offlineAlertSeconds ?? 10);
    $("monFreqCount").value = String(o.frequentOfflineCount ?? 3);
    $("monFreqWindow").value = String(o.frequentOfflineWindowMinutes ?? 30);
    $("monRatePercent").value = String(o.onlineRateAlertPercent ?? 95);
    $("monRateWindow").value = String(o.onlineRateWindowMinutes ?? 60);
    $("monCheckInterval").value = String(o.checkIntervalSeconds ?? 5);
    $("monRetention").value = String(o.eventRetentionDays ?? 30);
}
async function saveMonitorConfig() {
    const options = {
        enabled: $("monEnabled").checked,
        heartbeatTimeoutSeconds: num("monHeartbeat"),
        offlineAlertSeconds: num("monOfflineAlert"),
        frequentOfflineCount: num("monFreqCount"),
        frequentOfflineWindowMinutes: num("monFreqWindow"),
        onlineRateAlertPercent: num("monRatePercent"),
        onlineRateWindowMinutes: num("monRateWindow"),
        checkIntervalSeconds: num("monCheckInterval"),
        eventRetentionDays: num("monRetention")
    };
    const res = await api.saveMonitorConfig(options);
    if (res.status === 200 && res.data?.ok) {
        badge($("monConfigBadge"), "已保存并生效", "ok");
        $("monConfigMsg").textContent = res.data.backup
            ? "旧配置已备份：" + res.data.backup
            : "配置文件已写入：" + res.data.file;
    }
    else {
        badge($("monConfigBadge"), "保存失败", "err");
        $("monConfigMsg").textContent = res.message ?? "未知错误";
    }
}
