/**
 * 实时监控页：KPI 卡片 + 最新过包（大图 + 滚动列表，实现在 parcelview.ts） + 相机状态墙。
 *
 * 说明：
 *   * 过包区的去重与跟随交给 parcelview.ts —— SSE 会把"补全重量体积后"的同一条记录再推一次，
 *     那里是原地更新那一行并把大图切到最新（读到码的图会叠绿框）；
 *   * 相机状态墙与"设备信息"页共用同一份数据源（平台推送的 camera 事件）。
 */
import { api } from "./api.js";
import { $, cell, clear, dash, el, gb, positionLabel } from "./dom.js";
import { alertLabel, fmtAge, fmtRate } from "./monitor.js";
import { initParcelView, loadParcels, upsertParcel } from "./parcelview.js";
import type { CameraCounter, CameraRecord, MonitorCameraStatus, ParcelRecord, Stats } from "./types.js";

/** 页面首屏拉多少条历史（平台还会通过 SSE 补发最近 20 条） */
const INITIAL_PARCELS = 50;
/** 相机状态墙的视图选择（C2） */
const CAM_VIEW_KEY = "dws.view.cameras";

const cameras = new Map<string, CameraRecord>();
/** C2：B8 那边推来的每台指标（在线率/心跳/告警），和 cameras 按 deviceId 合并成状态墙 */
const monitorStats = new Map<string, MonitorCameraStatus>();

let camRowsEl: HTMLTableSectionElement;
let camEmptyEl: HTMLElement;
let camWallEl: HTMLElement;
let camTableWrapEl: HTMLElement;
let camWallSummaryEl: HTMLElement;

export function initRealtime(): void {
  camRowsEl = $<HTMLTableSectionElement>("camRows");
  camEmptyEl = $("camEmpty");
  camWallEl = $("camWall");
  camTableWrapEl = $("camTableWrap");
  camWallSummaryEl = $("camWallSummary");

  // C1：最新过包区（大图 + 滚动列表 + 绿框）自己管自己
  initParcelView();

  $("btnSoftTrigger").addEventListener("click", () => void softTriggerOnce());
  $("camViewCards").addEventListener("click", () => setCamView("cards"));
  $("camViewTable").addEventListener("click", () => setCamView("table"));

  let savedCam = "";
  try {
    savedCam = window.localStorage.getItem(CAM_VIEW_KEY) ?? "";
  } catch {
    savedCam = ""; // 隐私模式/壳里可能不让用 localStorage，忽略即可
  }
  setCamView(savedCam === "table" ? "table" : "cards");
}

/**
 * A4：软触发一次。
 *
 * 走的是采集宿主的【常驻命令通道】（命名管道）—— 宿主在跑就能直接触发，
 * 不用先停宿主（以前只能再起一个宿主进程去触发，会和正在跑的宿主抢相机，报 3001）。
 * 宿主没在跑时通道连不上，这里会提示先启动采集宿主。
 */
async function softTriggerOnce(): Promise<void> {
  const button = $<HTMLButtonElement>("btnSoftTrigger");
  const result = $("triggerResult");

  button.disabled = true;
  result.className = "muted";
  result.textContent = "正在给采集宿主发软触发…";

  try {
    const res = await api.hostCommand({ command: "soft-trigger" });
    const data = res.data;

    if (!data) {
      result.className = "warnText";
      result.textContent = "软触发失败：" + (res.message ?? ("HTTP " + res.status));
      return;
    }

    if (!data.available) {
      result.className = "warnText";
      result.textContent =
        "命令通道连不上（采集宿主没在跑？）：先用 run.ps1 / runtime\\tools\\start-all.ps1 把宿主起来。" +
        (data.message ? "　" + data.message : "");
      return;
    }

    const firstLine =
      (data.message ?? "")
        .split("\n")
        .map((line) => line.trim())
        .filter((line) => line.length > 0)[0] ?? "";

    result.className = data.ok ? "okText" : "warnText";
    result.textContent = (data.ok ? "已触发（退出码 0）：" : "退出码 " + data.exitCode + "：") + firstLine;
  } catch (e) {
    result.className = "warnText";
    result.textContent = "软触发调用失败：" + (e instanceof Error ? e.message : String(e));
  } finally {
    button.disabled = false;
  }
}

/** C2：相机状态墙 / 表格 两种视图切换（和过包区一样记住选择） */
function setCamView(view: "cards" | "table"): void {
  const cardsOn = view === "cards";
  camWallEl.style.display = cardsOn ? "" : "none";
  camWallSummaryEl.style.display = cardsOn ? "" : "none";
  camTableWrapEl.style.display = cardsOn ? "none" : "";
  $("camViewCards").className = "btn small" + (cardsOn ? "" : " secondary");
  $("camViewTable").className = "btn small" + (cardsOn ? " secondary" : "");
  try {
    window.localStorage.setItem(CAM_VIEW_KEY, view);
  } catch {
    // 忽略
  }
}

export function renderStats(s: Stats): void {
  $("kpiParcels").textContent = String(s.parcels ?? 0);
  $("kpiRate").textContent = ((s.readRate ?? 0) * 100).toFixed(1) + "%";
  $("kpiNoread").textContent = String(s.noread ?? 0);
  $("kpiCameras").textContent = (s.camerasOnline ?? 0) + " / " + (s.camerasTotal ?? 0);

  $("foot").textContent =
    // 事件/重复事件是"本次运行"的计数（进程内），包裹相关的计数是从历史恢复的累计值，
    // 标签上区分开，避免"重复事件 0 但合并包裹 3"看起来自相矛盾。
    "本次收到事件 " + (s.events ?? 0) + " 条（重复丢弃 " + (s.duplicateEvents ?? 0) + "）" +
    " · 合并包裹 " + (s.mergedParcels ?? 0) +
    " · 待下发 " + (s.dispatchPending ?? 0) + (s.dispatchFailed > 0 ? "（失败 " + s.dispatchFailed + "）" : "") +
    " · 落盘图片 " + (s.imageFileCount ?? 0) + " 张 / " + gb(s.imageDiskBytes) +
    " · 磁盘 " + (s.diskUsedPercent ?? 0) + "%（剩余 " + gb(s.diskFreeBytes) + "）" +
    " · 解析失败 " + (s.parseErrors ?? 0) +
    " · 服务器时间 " + (s.serverTime ?? "");
}


/**
 * C1：最新过包 —— 大图 + 限高滚动列表的实现在 parcelview.ts。
 * 这里只做转发：SSE 推来新包裹 / 同一包裹的重量体积补全，都走同一入口。
 */
export function renderParcel(p: ParcelRecord, flash: boolean): void {
  upsertParcel(p, flash);
}

export function upsertCamera(camera: CameraRecord): void {
  if (!camera?.deviceId) return;
  cameras.set(camera.deviceId, { ...cameras.get(camera.deviceId), ...camera });
  renderCameras();
  renderCameraWall();
}

export function replaceCameras(list: CameraRecord[]): void {
  cameras.clear();
  for (const c of list) {
    if (c?.deviceId) cameras.set(c.deviceId, c);
  }
  renderCameras();
  renderCameraWall();
}

/**
 * C2：收下 B8 的监控快照（在线率/心跳/活动告警），和相机基础状态合起来渲染状态墙。
 * 由 main.ts 在收到 SSE 的 type=monitor 时调用（monitor.ts 只负责设备信息页那块表）。
 */
export function applyMonitorStats(list: MonitorCameraStatus[]): void {
  for (const item of list ?? []) {
    if (!item?.camera) continue;
    monitorStats.set(item.camera, item);
    // 监控快照里带着权威的出码计数，顺手补进 cameras（有些相机可能还没收到 camera 事件）
    const known = cameras.get(item.camera);
    if (known) {
      if (typeof item.codeCount === "number") known.codeCount = item.codeCount;
      if (item.lastCodeTime) known.lastCodeTime = item.lastCodeTime;
      if (typeof item.offlineCountTotal === "number") known.offlineCount = item.offlineCountTotal;
    }
  }
  renderCameras();
  renderCameraWall();
}

/** C2：收下轻量的"相机计数"推送（出一包就变的那部分），只更新数字，不做整表刷新 */
export function applyCameraCounters(list: CameraCounter[]): void {
  for (const item of list ?? []) {
    if (!item?.camera) continue;
    let known = cameras.get(item.camera);
    if (!known) {
      known = {
        deviceId: item.camera,
        online: item.online,
        discovered: item.discovered,
        position: item.position ?? undefined,
        positionLabel: item.positionLabel ?? undefined,
        model: item.model ?? undefined,
        serialNumber: item.serialNumber ?? undefined,
        offlineCount: item.offlineCount,
        codeCount: item.codeCount
      } as CameraRecord;
      cameras.set(item.camera, known);
    }
    known.codeCount = item.codeCount;
    known.lastCodeTime = item.lastCodeTime ?? known.lastCodeTime;
    known.offlineCount = item.offlineCount;
    if (item.position && !known.position) {
      known.position = item.position;
      known.positionLabel = item.positionLabel ?? undefined;
    }
  }
  renderCameras();
  renderCameraWall();
}

/** 状态文案：在线 / 离线 / 未发现（清单里声明了但 SDK 没报） */
export function statusText(c: CameraRecord): string {
  if (c.discovered === false) return "未发现";
  return c.online ? "在线" : "离线";
}

function renderCameras(): void {
  const list = Array.from(cameras.values()).sort((a, b) => {
    const pa = a.positionOrder ?? 99;
    const pb = b.positionOrder ?? 99;
    if (pa !== pb) return pa - pb;
    return a.deviceId.localeCompare(b.deviceId);
  });

  clear(camRowsEl);
  camEmptyEl.style.display = list.length ? "none" : "block";

  for (const c of list) {
    const tr = el("tr");
    if (!c.online) tr.className = "offline";

    tr.appendChild(cell(positionLabel(c.position)));
    tr.appendChild(cell(c.declaredLabel ?? c.deviceId, "code"));
    tr.appendChild(cell(statusText(c), c.online ? "" : "noread"));
    tr.appendChild(cell(c.model));
    tr.appendChild(cell(c.serialNumber));
    tr.appendChild(cell(c.offlineCount ?? 0));
    tr.appendChild(cell(c.reconnectCount ?? 0));
    tr.appendChild(cell(dash(c.lastOfflineDurationText)));
    tr.appendChild(cell(c.codeCount ?? 0));
    tr.appendChild(cell(dash(c.lastCodeTime), "muted"));
    tr.appendChild(cell(c.lastChangeTime, "muted"));

    camRowsEl.appendChild(tr);
  }
}

// ---------------------------------------------------------------- C2 相机状态墙

/** 一台相机的"异常摘要"：没有异常返回 null（墙上的红黄底色和徽标都用它） */
function cameraProblem(c: CameraRecord, alerts: { code: string; severity: string; message: string }[]): string | null {
  if (c.discovered === false) return "未发现";
  if (!c.online) return "离线";
  if (alerts.length > 0) return alertLabel(alerts[0].code);
  return null;
}

function numberBox(value: string, label: string, className?: string): HTMLElement {
  const box = el("div", undefined, "camnum" + (className ? " " + className : ""));
  box.appendChild(el("div", value, "n"));
  box.appendChild(el("div", label, "l"));
  return box;
}

/**
 * C2 相机状态墙的一格：方位 + 相机 + 在线状态 + 出码数 + 掉线次数 + 心跳 + 在线率 + 告警徽标。
 * 数据是两路合起来的：相机基础状态（type=camera / /api/cameras）+ 监控指标（type=monitor）+ 计数（type=camera-count）。
 */
function cameraCell(c: CameraRecord): HTMLElement {
  const m = monitorStats.get(c.deviceId);
  const alerts = m?.alerts ?? [];
  const missing = c.discovered === false;
  const offline = missing || !c.online;
  const critical = alerts.some((a) => a.severity === "critical");

  const root = el("div", undefined,
    "camcell " + (missing ? "missing" : offline ? "offline" : "online") +
    (alerts.length > 0 ? (critical ? " alarm" : " warn") : ""));
  root.dataset.camera = c.deviceId;
  root.dataset.codeCount = String(c.codeCount ?? 0);
  root.title = "点击查看这台相机的详情（设备信息页）";

  const head = el("div", undefined, "camhead");
  head.appendChild(el("span", positionLabel(c.position), "campos"));
  head.appendChild(el("span", c.declaredLabel ?? c.deviceId, "camname code"));
  head.appendChild(el("span", missing ? "未发现" : c.online ? "在线" : "离线", "camstate"));
  root.appendChild(head);

  const nums = el("div", undefined, "camnums");
  nums.appendChild(numberBox(String(c.codeCount ?? 0), "出码", "big"));
  nums.appendChild(numberBox(String(c.offlineCount ?? 0), "掉线", (c.offlineCount ?? 0) > 0 ? "bad" : undefined));
  nums.appendChild(numberBox(m ? fmtAge(m.lastHeartbeatAgeSeconds) : "—", "心跳"));
  nums.appendChild(numberBox(m ? fmtRate(m.onlineRatePercent) : "—", "在线率"));
  root.appendChild(nums);

  root.appendChild(el("div", "最近出码 " + dash(c.lastCodeTime), "camline"));
  const identity = [c.model, c.serialNumber].filter((v) => !!v).join(" · ");
  if (identity) root.appendChild(el("div", identity, "camline muted"));

  if (alerts.length > 0) {
    const box = el("div", undefined, "camalerts");
    for (const a of alerts) {
      box.appendChild(el("span", alertLabel(a.code),
        "badge " + (a.severity === "critical" ? "err" : "warn")));
    }
    root.appendChild(box);
  }

  root.addEventListener("click", () => {
    $("tab-devices").click();
  });
  return root;
}

/** C2：渲染相机状态墙（每台一格）+ 一行汇总（在线/累计出码/异常台数） */
function renderCameraWall(): void {
  if (!camWallEl) return;

  const list = Array.from(cameras.values()).sort((a, b) => {
    const pa = a.positionOrder ?? 99;
    const pb = b.positionOrder ?? 99;
    if (pa !== pb) return pa - pb;
    return a.deviceId.localeCompare(b.deviceId);
  });

  clear(camWallEl);
  camEmptyEl.style.display = list.length ? "none" : "block";
  for (const c of list) {
    camWallEl.appendChild(cameraCell(c));
  }

  const online = list.filter((c) => c.online && c.discovered !== false).length;
  const codes = list.reduce((sum, c) => sum + (c.codeCount ?? 0), 0);
  const bad = list.filter((c) => cameraProblem(c, monitorStats.get(c.deviceId)?.alerts ?? []) !== null).length;
  camWallSummaryEl.textContent = list.length > 0
    ? "在线 " + online + " / " + list.length + "　累计出码 " + codes + "　异常 " + bad + " 台"
    : "";
}

export async function loadInitial(): Promise<void> {
  const parcels = await api.parcels(INITIAL_PARCELS);
  if (parcels.data?.length) {
    loadParcels(parcels.data);
  }

  const stats = await api.stats();
  if (stats.data) renderStats(stats.data);

  const cams = await api.cameras();
  if (cams.data) replaceCameras(cams.data);

  // C2：出码计数单独取一次（相机状态墙首屏就要显示"这台相机出过多少码"）
  const counters = await api.cameraCounters();
  if (counters.data) applyCameraCounters(counters.data);
}
