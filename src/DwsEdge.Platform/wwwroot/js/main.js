/**
 * 前端入口：装配页签、实时推送、首屏数据。
 *
 * 页面结构留在 index.html（骨架 + 文案），逻辑全在这里和各个模块里 —— 没有框架，
 * 也没有全局变量：模块之间只通过 import 通信，方便以后换壳（WebView2）或加页面。
 */
import { api } from "./api.js?v=38da8d49";
import { connectStream } from "./sse.js?v=38da8d49";
import { $ } from "./dom.js?v=38da8d49";
import { applyCameraCounters, applyMonitorStats, initRealtime, loadInitial, renderParcel, renderStats, upsertCamera } from "./realtime.js?v=38da8d49";
import { initDevices, refreshDevices, scheduleDevicesRefresh } from "./devices.js?v=38da8d49";
import { initConfig, refreshConfig } from "./config.js?v=38da8d49";
import { initRules, refreshRules } from "./rules.js?v=38da8d49";
import { initDedup, refreshDedup } from "./dedup.js?v=38da8d49";
import { initHistory, refreshHistory } from "./history.js?v=38da8d49";
import { initDownstream, refreshDownstream } from "./downstream.js?v=38da8d49";
import { initMonitor, refreshMonitor, refreshMonitorConfig, renderAlert, renderMonitorSnapshot } from "./monitor.js?v=38da8d49";
import { initAuth, refreshAuth, refreshAuthPanel } from "./auth.js?v=38da8d49";
import { initStats, refreshStats } from "./stats.js?v=38da8d49";
import { initDiag, refreshDiag } from "./diag.js?v=38da8d49";
import { initStatusBar, refreshStatusBar } from "./statusbar.js?v=38da8d49";
import { initShell } from "./shell.js?v=38da8d49";
import { initIcons } from "./icons.js?v=38da8d49";
const PAGES = ["realtime", "devices", "history", "stats", "diag", "cameras", "output", "rules", "system"];
function showPage(name) {
    for (const page of PAGES) {
        $("page-" + page).classList.toggle("active", page === name);
        $("tab-" + page).classList.toggle("active", page === name);
    }
    // 切页时按需拉一次数据（数据不多，够用且简单）
    if (name === "devices") {
        void refreshDevices();
        void refreshMonitor();
    }
    if (name === "history")
        void refreshHistory();
    if (name === "stats")
        void refreshStats();
    if (name === "diag")
        void refreshDiag();
    if (name === "cameras") {
        // 相机页：清单表格 + 存图策略 + 配置模板 + 当前 SDK 配置摘要
        void refreshConfig();
    }
    if (name === "output") {
        void refreshDownstream();
        void refreshDedup();
    }
    if (name === "rules") {
        void refreshRules();
        void refreshMonitorConfig();
    }
    if (name === "system") {
        void refreshAuthPanel();
    }
}
function setConnectionState(connected) {
    $("dot").className = "dot " + (connected ? "on" : "off");
    $("conn").textContent = connected ? "已连接" : "重连中…";
    // 这一格也参与"一票否决"：连不上平台就什么都看不到，所以跟着变色
    $("connItem").className = "sbar-item " + (connected ? "ok" : "bad");
    $("connItem").title = connected ? "与平台的实时通道正常（SSE）" : "与平台的实时通道断了，正在重连";
}
function bootstrap() {
    // T0：主题 / 信息密度先贴，避免后面首屏数据回来才换色
    initShell();
    // T0.6：导航与品牌区的图标（内联 SVG，离线可用；纯装饰，失败也不影响功能）
    initIcons();
    initRealtime();
    initDevices();
    initConfig();
    initRules();
    initDedup();
    initHistory();
    initDownstream();
    initMonitor();
    initAuth();
    initStats();
    initDiag();
    initStatusBar();
    for (const page of PAGES) {
        $("tab-" + page).addEventListener("click", () => showPage(page));
    }
    void loadInitial();
    // P0：首屏就把配置（触发模式 + 相机清单）拉一次，顶部「一键应用」条才能显示基线
    void refreshConfig();
    connectStream({
        onParcel: (p) => {
            renderParcel(p, true);
            refreshStatsSoon();
        },
        onCamera: (c) => {
            upsertCamera(c);
            scheduleDevicesRefresh();
        },
        onMonitor: (snapshot) => {
            renderMonitorSnapshot(snapshot);
            // C2：同一份快照也喂给实时页的相机状态墙（在线率/心跳/告警）
            applyMonitorStats(snapshot.cameras ?? []);
        },
        onAlert: (alert) => renderAlert(alert),
        // C2：出码计数（出一包就变），单独推、单独更新数字
        onCameraCount: (counters) => applyCameraCounters(counters),
        onStats: renderStats,
        onStatus: setConnectionState,
        // B9：推送断了（多半是会话过期）→ 刷一次登录态，需要的话弹回登录框
        onError: () => {
            void refreshAuth(true);
        }
    });
    // 首屏也拉一次监控（SSE 会补发，但"页面比平台先起来"或断线期间得靠这个）
    void refreshMonitor();
    // B9：先问一次登录态，决定要不要弹登录框；
    // T1：登录态一确定就立刻刷一次页头状态条（不然要等 5 秒的轮询）
    void refreshAuth().then(() => refreshStatusBar());
}
/** 新包裹到达后刷新统计（合并 500ms 内的多次刷新） */
let statsTimer = null;
function refreshStatsSoon() {
    if (statsTimer !== null)
        return;
    statsTimer = window.setTimeout(() => {
        statsTimer = null;
        void api.stats().then((res) => {
            if (res.data)
                renderStats(res.data);
        });
    }, 500);
}
bootstrap();
