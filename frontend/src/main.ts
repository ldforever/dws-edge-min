/**
 * 前端入口：装配页签、实时推送、首屏数据。
 *
 * 页面结构留在 index.html（骨架 + 文案），逻辑全在这里和各个模块里 —— 没有框架，
 * 也没有全局变量：模块之间只通过 import 通信，方便以后换壳（WebView2）或加页面。
 */
import { api } from "./api.js";
import { connectStream } from "./sse.js";
import { $ } from "./dom.js";
import {
  applyCameraCounters,
  applyMonitorStats,
  initRealtime,
  loadInitial,
  renderParcel,
  renderStats,
  upsertCamera
} from "./realtime.js";
import { initDevices, refreshDevices, scheduleDevicesRefresh } from "./devices.js";
import { initConfig, refreshConfig } from "./config.js";
import { initRules, refreshRules } from "./rules.js";
import { initDedup, refreshDedup } from "./dedup.js";
import { initHistory, refreshHistory } from "./history.js";
import { initDownstream, refreshDownstream } from "./downstream.js";
import { initMonitor, refreshMonitor, refreshMonitorConfig, renderAlert, renderMonitorSnapshot } from "./monitor.js";
import { initAuth, refreshAuth, refreshAuthPanel } from "./auth.js";
import { initStats, refreshStats } from "./stats.js";
import { initDiag, refreshDiag } from "./diag.js";

/**
 * P0：页签按"现场任务"分，不再按代码模块分：
 *   监控类（实时/设备/历史/统计/诊断） + 配置类（相机/输出对接/规则/系统与安全）。
 * 配置类被拆开的原因：原来一个"配置"页塞了 10 个板块、50 多个输入框。
 * 应用配置这件事独立成页头下方那条全局「一键应用」条（applybar.ts）。
 */
type PageName = "realtime" | "devices" | "history" | "stats" | "diag" | "cameras" | "output" | "rules" | "system";
const PAGES: PageName[] = ["realtime", "devices", "history", "stats", "diag", "cameras", "output", "rules", "system"];

function showPage(name: PageName): void {
  for (const page of PAGES) {
    $("page-" + page).classList.toggle("active", page === name);
    $("tab-" + page).classList.toggle("active", page === name);
  }

  // 切页时按需拉一次数据（数据不多，够用且简单）
  if (name === "devices") {
    void refreshDevices();
    void refreshMonitor();
  }
  if (name === "history") void refreshHistory();
  if (name === "stats") void refreshStats();
  if (name === "diag") void refreshDiag();
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
  initHistory();
  initDownstream();
  initMonitor();
  initAuth();
  initStats();
  initDiag();

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
  // B9：先问一次登录态，决定要不要弹登录框
  void refreshAuth();
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
