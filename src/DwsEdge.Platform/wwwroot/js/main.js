/**
 * 前端入口：装配页签、实时推送、首屏数据。
 *
 * 页面结构留在 index.html（骨架 + 文案），逻辑全在这里和各个模块里 —— 没有框架，
 * 也没有全局变量：模块之间只通过 import 通信，方便以后换壳（WebView2）或加页面。
 */
import { api } from "./api.js?v=2f1f6302";
import { connectStream } from "./sse.js?v=2f1f6302";
import { $ } from "./dom.js?v=2f1f6302";
import { initRealtime, loadInitial, renderParcel, renderStats, upsertCamera } from "./realtime.js?v=2f1f6302";
import { initDevices, refreshDevices, scheduleDevicesRefresh } from "./devices.js?v=2f1f6302";
import { initConfig, refreshConfig } from "./config.js?v=2f1f6302";
const PAGES = ["realtime", "devices", "config"];
function showPage(name) {
    for (const page of PAGES) {
        $("page-" + page).classList.toggle("active", page === name);
        $("tab-" + page).classList.toggle("active", page === name);
    }
    // 切页时按需拉一次数据（数据不多，够用且简单）
    if (name === "devices")
        void refreshDevices();
    if (name === "config")
        void refreshConfig();
}
function setConnectionState(connected) {
    $("dot").className = "dot " + (connected ? "on" : "off");
    $("conn").textContent = connected ? "已连接" : "重连中…";
}
function bootstrap() {
    initRealtime();
    initDevices();
    initConfig();
    for (const page of PAGES) {
        $("tab-" + page).addEventListener("click", () => showPage(page));
    }
    void loadInitial();
    connectStream({
        onParcel: (p) => {
            renderParcel(p, true);
            refreshStatsSoon();
        },
        onCamera: (c) => {
            upsertCamera(c);
            scheduleDevicesRefresh();
        },
        onStats: renderStats,
        onStatus: setConnectionState
    });
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
