/**
 * 前端入口：装配页签、实时推送、首屏数据。
 *
 * 页面结构留在 index.html（骨架 + 文案），逻辑全在这里和各个模块里 —— 没有框架，
 * 也没有全局变量：模块之间只通过 import 通信，方便以后换壳（WebView2）或加页面。
 */
import { api } from "./api.js";
import { connectStream } from "./sse.js";
import { $ } from "./dom.js";
import { initRealtime, loadInitial, renderParcel, renderStats, upsertCamera } from "./realtime.js";
import { initDevices, refreshDevices, scheduleDevicesRefresh } from "./devices.js";
import { initConfig, refreshConfig } from "./config.js";
import { initRules, refreshRules } from "./rules.js";
import { initDedup, refreshDedup } from "./dedup.js";

type PageName = "realtime" | "devices" | "config";
const PAGES: PageName[] = ["realtime", "devices", "config"];

function showPage(name: PageName): void {
  for (const page of PAGES) {
    $("page-" + page).classList.toggle("active", page === name);
    $("tab-" + page).classList.toggle("active", page === name);
  }

  // 切页时按需拉一次数据（数据不多，够用且简单）
  if (name === "devices") void refreshDevices();
  if (name === "config") {
    void refreshConfig();
    void refreshRules();
    void refreshDedup();
  }
}

function setConnectionState(connected: boolean): void {
  $("dot").className = "dot " + (connected ? "on" : "off");
  $("conn").textContent = connected ? "已连接" : "重连中…";
}

function bootstrap(): void {
  initRealtime();
  initDevices();
  initConfig();
  initRules();
  initDedup();

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
let statsTimer: number | null = null;
function refreshStatsSoon(): void {
  if (statsTimer !== null) return;
  statsTimer = window.setTimeout(() => {
    statsTimer = null;
    void api.stats().then((res) => {
      if (res.data) renderStats(res.data);
    });
  }, 500);
}

bootstrap();
