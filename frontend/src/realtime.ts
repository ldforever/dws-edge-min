/**
 * 实时监控页：KPI 卡片 + 最新过包表 + 相机状态表。
 *
 * 说明：
 *   * 过包表按 traceId 去重 —— SSE 会把"补全重量体积后"的同一条记录再推一次，
 *     直接原地更新那一行，比追加两行清楚（同时仍能看到 更新次数 ×2）；
 *   * 相机状态表与"设备信息"页共用同一份数据源（平台推送的 camera 事件）。
 */
import { api } from "./api.js";
import { $, cell, clear, el, gb, positionLabel, dash } from "./dom.js";
import type { CameraRecord, CodeDetail, ParcelRecord, Stats } from "./types.js";

const MAX_ROWS = 120;
/** 页面首屏拉多少条历史（平台还会通过 SSE 补发最近 20 条） */
const INITIAL_PARCELS = 50;

const rowByTrace = new Map<string, HTMLTableRowElement>();
const cameras = new Map<string, CameraRecord>();

let rowsEl: HTMLTableSectionElement;
let emptyEl: HTMLElement;
let camRowsEl: HTMLTableSectionElement;
let camEmptyEl: HTMLElement;

export function initRealtime(): void {
  rowsEl = $<HTMLTableSectionElement>("rows");
  emptyEl = $("emptyTip");
  camRowsEl = $<HTMLTableSectionElement>("camRows");
  camEmptyEl = $("camEmpty");
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

export function renderParcel(p: ParcelRecord, flash: boolean): void {
  emptyEl.style.display = "none";

  const key = p.traceId ?? (p.time ?? "") + "|" + (p.codes ?? []).join(",");
  const previous = rowByTrace.get(key);
  if (previous) {
    previous.remove();
    rowByTrace.delete(key);
  }

  const tr = el("tr");
  tr.dataset.key = key;
  if (flash) tr.className = "new";

  tr.appendChild(cell(p.time));

  const codeTd = el("td");
  const details: CodeDetail[] = p.codeDetails?.length
    ? p.codeDetails
    : (p.codes ?? []).map((value) => ({ value }));

  if (details.length) {
    codeTd.className = "code";
    details.forEach((d, i) => {
      if (i > 0) codeTd.appendChild(document.createTextNode(" , "));
      codeTd.appendChild(document.createTextNode(d.value));

      const tags: string[] = [];
      if (d.position) tags.push(positionLabel(d.position));
      if (d.kind && d.kind !== "unknown") tags.push(d.kind.toUpperCase());
      if (tags.length) {
        codeTd.appendChild(el("span", " (" + tags.join("/") + ")", "tag"));
      }
    });
  } else {
    codeTd.className = "noread";
    codeTd.textContent = "NOREAD";
    // B2：无码的时候把"被规则丢掉的码"直接显示出来，现场一眼就知道是过滤掉的还是真没读到
    const dropped = p.filteredCodes ?? [];
    if (dropped.length) {
      const values = dropped.map((f) => f.code).join(" , ");
      const rules = dropped.map((f) => f.rule).filter((r) => !!r);
      const tip = el("span", "  丢弃：" + values + (rules.length ? "（规则 " + rules.join("/") + "）" : ""), "tag");
      tip.style.color = "#d29922";
      codeTd.appendChild(tip);
    }
  }
  tr.appendChild(codeTd);

  tr.appendChild(cell(p.deviceId, "muted"));

  const stageTd = el("td", "", "muted");
  stageTd.textContent = (p.stage ?? "") + ((p.updates ?? 0) > 1 ? " ×" + p.updates : "");
  if ((p.lengthMm ?? 0) > 0) {
    stageTd.textContent +=
      " · " + Math.round(p.lengthMm ?? 0) + "×" + Math.round(p.widthMm ?? 0) + "×" + Math.round(p.heightMm ?? 0);
  }
  if (p.complete === false) {
    stageTd.appendChild(el("span", " 待补全", "pending"));
  }
  // B1：下发状态只在"已下发/失败"时显示 —— 全员"待下发"会变成噪音
  if (p.dispatchState === "sent") {
    stageTd.appendChild(el("span", " 已下发", "tag"));
  } else if (p.dispatchState === "failed") {
    stageTd.appendChild(el("span", " 下发失败" + (p.dispatchAttempts ? "×" + p.dispatchAttempts : ""), "pending"));
  }
  tr.appendChild(stageTd);

  const imgTd = el("td");
  if ((p.imageCount ?? 0) > 0 && p.firstImagePath) {
    const a = el("a", "查看 (" + p.imageCount + ")", "img");
    a.href = api.imageUrl(p.firstImagePath);
    a.target = "_blank";
    imgTd.appendChild(a);
  } else {
    imgTd.className = "muted";
    imgTd.textContent = "—";
  }
  tr.appendChild(imgTd);

  rowsEl.insertBefore(tr, rowsEl.firstChild);
  rowByTrace.set(key, tr);

  while (rowsEl.children.length > MAX_ROWS) {
    const last = rowsEl.lastElementChild as HTMLTableRowElement | null;
    if (!last) break;
    rowByTrace.delete(last.dataset.key ?? "");
    rowsEl.removeChild(last);
  }
}

export function upsertCamera(camera: CameraRecord): void {
  if (!camera?.deviceId) return;
  cameras.set(camera.deviceId, camera);
  renderCameras();
}

export function replaceCameras(list: CameraRecord[]): void {
  cameras.clear();
  for (const c of list) {
    if (c?.deviceId) cameras.set(c.deviceId, c);
  }
  renderCameras();
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
    tr.appendChild(cell(c.lastChangeTime, "muted"));

    camRowsEl.appendChild(tr);
  }
}

export async function loadInitial(): Promise<void> {
  const parcels = await api.parcels(INITIAL_PARCELS);
  if (parcels.data?.length) {
    emptyEl.style.display = "none";
    for (let i = parcels.data.length - 1; i >= 0; i--) {
      const p = parcels.data[i];
      if (p) renderParcel(p, false);
    }
  }

  const stats = await api.stats();
  if (stats.data) renderStats(stats.data);

  const cams = await api.cameras();
  if (cams.data) replaceCameras(cams.data);
}
