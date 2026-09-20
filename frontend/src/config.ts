/**
 * 配置页：读当前 SDK 配置 + 一键应用（写配置 → 重启校验 → 失败自动回滚）。
 *
 * 界面调的就是命令行那条路径（tools\apply-config.ps1），所以不会出现"界面能过、命令行过不了"。
 *
 * C5 在这页上补三块：
 *   * 相机清单表格编辑（生成同样的清单文本，走同一个 apply 接口）；
 *   * 存图策略表单（写 gateway.ini，校验 + 自动备份）；
 *   * 配置备份与回滚（config\ 与 Cfg\ 下的 .bak-* 都能一键还原）。
 */
import { api } from "./api.js";
import { $, badge, cell, clear, dash, el, notify, positionLabel } from "./dom.js";
import { confirmBox } from "./ui.js";
import {
  applyBarError,
  applyBarFinish,
  applyBarStart,
  initApplyBar,
  refreshApplyBar,
  setApplyBaseline,
  triggerModeToUi
} from "./applybar.js";
import type {
  ApplyRequest,
  ApplyResult,
  BackupItem,
  ConfigCamera,
  ConfigSummary,
  ConfigTemplateSummary,
  CameraProbeItem,
  StorageOptions
} from "./types.js";

const TRIGGER_LABEL: Record<string, string> = {
  hard: "硬触发（光电）",
  soft: "软触发",
  free: "自由拉流（狂扫）"
};

let summary: ConfigSummary | null = null;

/** 文本模式与表格互相同步时用它防抖（否则会 table→text→table 无限循环） */
let syncing = false;

export function initConfig(): void {
  $("btnReloadConfig").addEventListener("click", () => void refreshConfig());
  $("btnFillCameras").addEventListener("click", () => fillCamerasFromConfig());
  $("btnApply").addEventListener("click", () => void applyConfig());
  // P0：全局应用条 —— 由它来判断"有没有未保存的改动"
  initApplyBar(() => cameraLinesFromEditor(), () => $<HTMLSelectElement>("cfgTrigger").value);
  $<HTMLTextAreaElement>("cfgCameras").addEventListener("input", () => syncTableFromText());
  // C5：相机清单表格
  $("btnCamAdd").addEventListener("click", () => {
    $("camEditRows").appendChild(cameraEditRow("ip", "", ""));
    renumberCameraEditor();
    syncTextFromTable();
  });
  $("btnCamFill").addEventListener("click", () => refreshConfig());
  $("btnCamCheck").addEventListener("click", () => checkCameraEditor(true));
  $("btnCamApply").addEventListener("click", () => void applyCamerasFromEditor());
  // P0：扫描在线相机 + 连通性预检
  $("btnCamScan").addEventListener("click", () => void scanOnlineCameras());
  $("btnCamProbe").addEventListener("click", () => void probeCameras());
  // C5：存图策略与备份
  $("btnStorageSave").addEventListener("click", () => void saveStorage());
  $("btnStorageReload").addEventListener("click", () => void refreshStorage());
  $("btnBackupReload").addEventListener("click", () => void refreshBackups());
  // A8-3：配置模板
  $("btnTplSave").addEventListener("click", () => void saveTemplate());
  $("btnTplReload").addEventListener("click", () => void refreshTemplates());
  $("btnTplApply").addEventListener("click", () => void applyTemplate());
}

export async function refreshConfig(): Promise<void> {
  const res = await api.config();
  if (res.status !== 200 || !res.data) return;
  summary = res.data;
  render();
  renderCameraEditor(res.data.cameras ?? []);

  // P0：把触发模式下拉同步成服务器上的实际值，并把它与清单一起记为"应用基线"，
  // 这样用户一改，顶部那条就会变黄提示"有未保存改动"。
  const mode = triggerModeToUi(res.data.triggerMode);
  if (mode) {
    $<HTMLSelectElement>("cfgTrigger").value = mode;
  }
  setApplyBaseline(mode, cameraLinesFromEditor());

  await refreshStorage();
  await refreshBackups();
  await refreshTemplates();
}

function render(): void {
  const c = summary;
  if (!c) return;

  const kv = $("cfgSummary");
  kv.textContent = "";

  const rows: [string, string][] = [
    ["运行目录", c.runtimeRoot],
    ["SDK 配置文件", c.cfgPath + (c.cfgExists ? "" : "（不存在）")],
    ["provider", c.provider ?? "—"],
    ["相机模式", "mode=" + c.mode + " num=" + c.num + " randWorkMode=" + c.randWorkMode],
    ["启用相机", c.enabledCameras + " 台（声明 " + c.declaredCameras + " 条）"],
    ["触发模式", (TRIGGER_LABEL[c.triggerName] ?? c.triggerName ?? "?") + "（triggerMode=" + c.triggerMode + "）"],
    ["方位映射", c.positionsCount + " 条 · " + c.positionsFile],
    ["一键应用脚本", (c.toolsReady ? "可用 " : "未找到 ") + c.applyScript]
  ];

  for (const [key, value] of rows) {
    kv.appendChild(el("div", key, "k"));
    kv.appendChild(el("div", value));
  }

  if (!c.toolsReady) {
    badge(kv, "找不到 apply-config.ps1：先跑一次 build.ps1（会把 tools 拷到 runtime\\tools）", "err");
  }
  if (c.problems?.length) {
    badge(kv, "配置问题：" + c.problems.join("；"), "err");
  }
  if (c.warnings?.length) {
    badge(kv, "提醒：" + c.warnings.join("；"), "warn");
  }

  fillCamerasFromConfig();

  if (c.lastApply) {
    renderApplyResult(c.lastApply, "（上次应用：" + (c.lastApply.at ?? "") + "）\n\n");
  }
}

function fillCamerasFromConfig(): void {
  if (!summary?.cameras?.length) return;
  const textarea = $<HTMLTextAreaElement>("cfgCameras");
  textarea.value = summary.cameras.map((x) => x.line).join("\r\n");
}

function renderApplyResult(result: ApplyResult, header = ""): void {
  $("applyOut").textContent =
    header +
    "结论：" + result.conclusion + "\n" +
    "退出码：" + result.exitCode + " · 耗时 " + (result.durationMs / 1000).toFixed(1) + " 秒\n" +
    (result.commandLine ? "命令：" + result.commandLine + "\n" : "") +
    (result.error ? "错误：" + result.error + "\n" : "") +
    "----------------------------------------\n" +
    (result.output || "（没有输出）");

  badge($("applyBadge"), "退出码 " + result.exitCode + " · " + result.conclusion, exitKind(result.exitCode));
}

function exitKind(exitCode: number): "ok" | "warn" | "err" {
  if (exitCode === 0) return "ok";
  if (exitCode === 1 || exitCode === 2) return "warn";
  return "err";
}

async function applyConfig(): Promise<void> {
  const triggerMode = $<HTMLSelectElement>("cfgTrigger").value;
  const useCameras = $<HTMLInputElement>("cfgUseCameras").checked;
  const skipVerify = $<HTMLInputElement>("cfgSkipVerify").checked;

  let cameras: string[] | null = null;
  if (useCameras) {
    // P0：以"相机页表格"为准（文本与表格本来就同步）；表格空时才退回文本框
    const fromTable = cameraLinesFromEditor();
    cameras = fromTable.length > 0
      ? fromTable
      : $<HTMLTextAreaElement>("cfgCameras")
          .value.split(/\r?\n/)
          .map((line) => line.trim())
          .filter((line) => line.length > 0 && !line.startsWith("#"));

    if (!cameras.length) {
      notify("相机清单是空的：请先填写，或取消勾选「应用上面的相机清单」");
      return;
    }
  }

  const body: ApplyRequest = {
    triggerMode,
    cameras,
    skipVerify,
    stopHost: $<HTMLInputElement>("cfgStopHost").checked,
    restartHost: $<HTMLInputElement>("cfgRestartHost").checked
  };

  // P0：进度与结果都走顶部那条全局应用条（切页也不会中断提示）
  applyBarStart();
  $("applyOut").textContent =
    "正在执行：\n" + JSON.stringify(body, null, 2) + "\n\n（校验会真的启动一次 SDK，请稍等…）";

  try {
    const res = await api.applyConfig(body);
    if (res.data) {
      renderApplyResult(res.data);
      applyBarFinish(res.data.exitCode === 0, res.data.conclusion ?? "");
    } else {
      $("applyOut").textContent = "调用失败：" + (res.message ?? "HTTP " + res.status);
      badge($("applyBadge"), "调用失败", "err");
      applyBarError(res.message ?? "HTTP " + res.status);
    }
  } catch (e) {
    applyBarError(e instanceof Error ? e.message : String(e));
    throw e;
  } finally {
    await refreshConfig();
  }
}

// ================================================================
// C5：相机清单表格编辑
// ================================================================

/** 方位下拉（与 Core\Config\CameraPositions 的取值一致） */
const POSITION_OPTIONS: string[] = ["", "top", "bottom", "left", "right", "front", "rear", "line", "spare"];

function renderCameraEditor(list: ConfigCamera[]): void {
  const rows = $<HTMLTableSectionElement>("camEditRows");
  clear(rows);
  for (const cam of list) {
    rows.appendChild(cameraEditRow(cam.kind ?? "ip", cam.value ?? "", cam.position ?? ""));
  }
  if (list.length === 0) {
    rows.appendChild(cameraEditRow("ip", "", ""));
  }
  renumberCameraEditor();
}

function cameraEditRow(kind: string, value: string, position: string): HTMLTableRowElement {
  const tr = el("tr");
  tr.appendChild(cell("", "muted cam-no"));

  const kindTd = el("td");
  const kindSelect = el("select", undefined, "pos");
  for (const option of ["ip", "key", "id"]) {
    const opt = el("option", option);
    opt.value = option;
    kindSelect.appendChild(opt);
  }
  (kindSelect as HTMLSelectElement).value = kind;
  kindTd.appendChild(kindSelect);
  tr.appendChild(kindTd);

  const valueTd = el("td");
  const input = el("input", undefined, "rule-input") as HTMLInputElement;
  input.type = "text";
  input.size = 22;
  input.value = value;
  input.placeholder = kind === "ip" ? "172.20.10.11" : (kind === "key" ? "序列号" : "厂商:序列号");
  valueTd.appendChild(input);
  tr.appendChild(valueTd);

  const posTd = el("td");
  const posSelect = el("select", undefined, "pos");
  for (const option of POSITION_OPTIONS) {
    const opt = el("option", option === "" ? "未设置" : positionLabel(option));
    opt.value = option;
    posSelect.appendChild(opt);
  }
  (posSelect as HTMLSelectElement).value = position;
  posTd.appendChild(posSelect);
  tr.appendChild(posTd);

  // P0：这一行任何改动都要（1）同步到文本模式（2）让顶部应用条提示"有未保存改动"
  const onEdit = () => {
    syncTextFromTable();
  };
  input.addEventListener("input", onEdit);
  (kindSelect as HTMLSelectElement).addEventListener("change", onEdit);
  (posSelect as HTMLSelectElement).addEventListener("change", onEdit);

  // P0：预检列 —— 点「连通性预检」后在这里显示 ping / 是否被 SDK 发现
  const probeTd = el("td", "—", "muted probe");
  tr.appendChild(probeTd);

  const actionTd = el("td");
  const remove = el("button", "删除", "btn secondary small");
  remove.addEventListener("click", () => {
    tr.remove();
    renumberCameraEditor();
    syncTextFromTable();
  });
  actionTd.appendChild(remove);
  tr.appendChild(actionTd);
  return tr;
}

function renumberCameraEditor(): void {
  const rows = Array.from($("camEditRows").children) as HTMLTableRowElement[];
  rows.forEach((row, index) => {
    const first = row.children[0];
    if (first) first.textContent = String(index + 1);
  });
}

/** 从表格读出一行行相机（kind / value / position） */
function cameraEditorRows(): { kind: string; value: string; position: string }[] {
  const rows = Array.from($("camEditRows").children) as HTMLTableRowElement[];
  const list: { kind: string; value: string; position: string }[] = [];
  for (const row of rows) {
    const selects = row.querySelectorAll("select");
    const input = row.querySelector("input") as HTMLInputElement | null;
    if (selects.length < 2 || !input) continue;
    const kind = (selects[0] as HTMLSelectElement).value;
    const position = (selects[1] as HTMLSelectElement).value;
    const value = input.value.trim();
    if (!value) continue;   // 空行直接忽略（现场常留一行空的）
    list.push({ kind, value, position });
  }
  return list;
}

function cameraLinesFromEditor(): string[] {
  return cameraEditorRows().map((row) =>
    row.kind + "=" + row.value + (row.position ? ",pos=" + row.position : ""));
}

/** 表格 → 文本模式（保持两边是同一份清单）＋顺便刷新顶部应用条的"改动"提示 */
function syncTextFromTable(): void {
  if (syncing) return;
  syncing = true;
  try {
    $<HTMLTextAreaElement>("cfgCameras").value = cameraLinesFromEditor().join("\r\n");
  } finally {
    syncing = false;
  }
  refreshApplyBar();
}

/** 文本模式 → 表格（批量粘贴后立刻看到表格） */
function syncTableFromText(): void {
  if (syncing) return;
  const lines = $<HTMLTextAreaElement>("cfgCameras")
    .value.split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0 && !line.startsWith("#"));

  const parsed: ConfigCamera[] = [];
  for (const line of lines) {
    const parts = line.split(",");
    const head = (parts[0] ?? "").trim();
    const eq = head.indexOf("=");
    if (eq <= 0) continue;
    const kind = head.substring(0, eq).trim().toLowerCase();
    const value = head.substring(eq + 1).trim();
    let position = "";
    for (let i = 1; i < parts.length; i++) {
      const opt = parts[i].trim();
      if (opt.startsWith("pos=")) position = opt.substring(4).trim();
    }
    if (!value || (kind !== "ip" && kind !== "key" && kind !== "id")) continue;
    parsed.push({
      index: parsed.length + 1,
      kind,
      value,
      line: kind + "=" + value + (position ? ",pos=" + position : ""),
      position
    });
  }

  syncing = true;
  try {
    renderCameraEditor(parsed);
  } finally {
    syncing = false;
  }
  $("camEditMsg").textContent = "文本已同步到表格：" + parsed.length + " 台";
  refreshApplyBar();
}

function cameraMsg(text: string, kind: "muted" | "probe-ok" | "probe-bad" | "probe-warn" = "muted"): void {
  const el0 = $("camEditMsg");
  el0.className = kind;
  el0.textContent = text;
}

/** P0：扫描在线相机 —— 把 SDK 已经发现的设备直接补进清单（用 id=厂商:序列号，和 SDK 上报身份一致） */
async function scanOnlineCameras(): Promise<void> {
  cameraMsg("正在向采集宿主要在线相机列表…");
  const res = await api.devices();
  if (res.status !== 200 || !res.data) {
    cameraMsg("扫描失败：" + (res.message ?? "HTTP " + res.status), "probe-bad");
    return;
  }

  const discovered = (res.data.cameras ?? []).filter((cam) => cam.discovered);
  const existing = new Set(cameraEditorRows().map((row) => row.kind + "=" + row.value.toLowerCase()));
  let added = 0;

  for (const cam of discovered) {
    const value = cam.deviceId ?? "";
    if (!value) continue;
    if (existing.has("id=" + value.toLowerCase())) continue;
    if (cam.declaredValue && existing.has(((cam.declaredKind ?? "") + "=" + cam.declaredValue).toLowerCase())) continue;

    $("camEditRows").appendChild(cameraEditRow("id", value, cam.position ?? ""));
    added++;
  }

  renumberCameraEditor();
  syncTextFromTable();
  cameraMsg(
    "扫描到 " + discovered.length + " 台在线相机，补进清单 " + added + " 行" +
    (added > 0 ? "（记得点顶部「一键应用」生效）" : "（清单里已经有了）"),
    added > 0 ? "probe-warn" : "muted"
  );
}

/** P0：连通性预检 —— 平台 ping + 和 SDK 发现列表对照，结果直接写进每一行的"预检"列 */
async function probeCameras(): Promise<void> {
  const rows = cameraEditorRows();
  const ips = rows.filter((row) => row.kind === "ip").map((row) => row.value);
  if (ips.length === 0) {
    cameraMsg("预检只对 ip= 的行有效：清单里没有 IP 形式的相机", "probe-warn");
    return;
  }

  cameraMsg("正在预检 " + ips.length + " 个 IP…");
  const res = await api.cameraProbe(ips);
  if (res.status !== 200 || !res.data) {
    cameraMsg("预检失败：" + (res.message ?? "HTTP " + res.status), "probe-bad");
    return;
  }

  const byIp = new Map<string, CameraProbeItem>();
  for (const item of res.data.results ?? []) {
    byIp.set(item.ip.toLowerCase(), item);
  }

  const trs = Array.from($("camEditRows").children) as HTMLTableRowElement[];
  let okCount = 0;
  let badCount = 0;

  trs.forEach((tr, index) => {
    const row = rows[index];
    const cellEl = tr.querySelector("td.probe") as HTMLElement | null;
    if (!cellEl || !row) return;

    if (row.kind !== "ip") {
      cellEl.className = "muted probe";
      cellEl.textContent = "（非 IP，不预检）";
      return;
    }

    const item = byIp.get(row.value.toLowerCase());
    if (!item) {
      cellEl.className = "muted probe";
      cellEl.textContent = "—";
      return;
    }

    const kind = item.ping && item.discovered ? "probe-ok" : (item.ping ? "probe-warn" : "probe-bad");
    if (kind === "probe-ok") okCount++; else badCount++;
    cellEl.className = kind + " probe";
    cellEl.textContent = item.message + (item.pingMs > 0 ? "（" + item.pingMs + "ms）" : "");
    cellEl.title = item.deviceId ? "SDK 标识：" + item.deviceId : "SDK 没有发现这台相机";
  });

  cameraMsg(
    "预检完成：" + okCount + " 台正常" + (badCount > 0 ? "，" + badCount + " 台要看一眼（悬停预检单元格看 SDK 标识）" : ""),
    badCount > 0 ? "probe-warn" : "probe-ok"
  );
}

/** 前端先校验一遍（后端还会再校验一次，两边口径一致）：空值、重复、IP 形式、台数提示 */
function checkCameraEditor(showResult: boolean): { errors: string[]; notices: string[] } {
  const rows = cameraEditorRows();
  const errors: string[] = [];
  const notices: string[] = [];
  const seen = new Map<string, number>();

  rows.forEach((row, index) => {
    const no = index + 1;
    if (row.kind === "ip") {
      // 选了 ip 就要求是合法 IPv4：写错网段是现场最常见的"相机连不上"原因，这里直接拦下来
      const parts = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(row.value);
      const valid = parts !== null && [parts[1], parts[2], parts[3], parts[4]].every((p) => Number(p) <= 255);
      if (!valid) {
        errors.push("第 " + no + " 行不是合法的 IP：" + row.value +
          "（要填主机名或序列号，请把接入方式改成 id 或 key）");
      }
    }
    const key = row.kind + "=" + row.value.toLowerCase();
    const first = seen.get(key);
    if (first !== undefined) {
      errors.push("第 " + no + " 行与第 " + first + " 行重复：" + row.kind + "=" + row.value);
    } else {
      seen.set(key, no);
    }
  });

  if (rows.length === 0) {
    errors.push("清单是空的：至少要有一台相机");
  } else if (rows.length < 12 || rows.length > 17) {
    notices.push("当前 " + rows.length + " 台（六面扫现场一般是 12-17 台，确认没漏/没多）");
  }

  if (showResult) {
    const box = $("camEditProblems");
    clear(box);
    for (const item of errors) {
      box.appendChild(el("div", "✗ " + item, "err"));
    }
    for (const item of notices) {
      box.appendChild(el("div", "! " + item, "warnText"));
    }
    if (errors.length === 0) {
      box.appendChild(el("div", "✓ 清单校验通过（" + rows.length + " 台）", "okText"));
    }
    $("camEditMsg").textContent = errors.length > 0
      ? "有问题，先改一下再保存"
      : (notices.length > 0 ? "有提醒（不影响保存）" : "校验通过");
  }

  return { errors, notices };
}

/** 表格 → 保存并应用（走的就是 A8 那条 apply → 校验 → 失败回滚） */
async function applyCamerasFromEditor(): Promise<void> {
  const check = checkCameraEditor(true);
  if (check.errors.length > 0) {
    notify("相机清单有 " + check.errors.length + " 个问题，先按红字改一下");
    return;
  }

  const cameras = cameraLinesFromEditor();
  const skipVerify = $<HTMLInputElement>("cfgSkipVerify").checked;
  const body: ApplyRequest = {
    triggerMode: "",
    cameras,
    skipVerify,
    stopHost: $<HTMLInputElement>("cfgStopHost").checked,
    restartHost: $<HTMLInputElement>("cfgRestartHost").checked
  };

  const button = $<HTMLButtonElement>("btnCamApply");
  button.disabled = true;
  button.textContent = "应用中…";
  applyBarStart();
  $("applyOut").textContent = "正在按表格里的清单应用：\n" + cameras.join("\n") + "\n\n（校验会真的启动一次 SDK，请稍等…）";

  try {
    const res = await api.applyConfig(body);
    if (res.data) {
      renderApplyResult(res.data);
      applyBarFinish(res.data.exitCode === 0, res.data.conclusion ?? "");
    } else {
      $("applyOut").textContent = "调用失败：" + (res.message ?? "HTTP " + res.status);
      badge($("applyBadge"), "调用失败", "err");
      applyBarError(res.message ?? "HTTP " + res.status);
    }
  } catch (e) {
    applyBarError(e instanceof Error ? e.message : String(e));
    throw e;
  } finally {
    button.disabled = false;
    button.textContent = "保存并应用";
    await refreshConfig();
  }
}

// ================================================================
// C5：存图策略
// ================================================================

export async function refreshStorage(): Promise<void> {
  const res = await api.storageConfig();
  if (res.status !== 200 || !res.data) {
    $("storageFile").textContent = "读取失败：" + (res.message ?? "HTTP " + res.status);
    return;
  }
  fillStorage(res.data.options);
  $("storageFile").textContent =
    "配置文件：" + res.data.file +
    "　存图开关写进：[" + res.data.storageSection + "]" +
    (res.data.storageSection === res.data.providerSection
      ? ""
      : "（当前 provider 是 [" + res.data.providerSection + "]，它的段里没有存图开关）") +
    "　图片目录：" + res.data.imageRoot;
  $("storageMsg").textContent = res.data.note ?? "";
}

function fillStorage(options: StorageOptions): void {
  $<HTMLInputElement>("stSaveOriginal").checked = options.saveOriginal !== false;
  $<HTMLInputElement>("stSaveWaybill").checked = options.saveWaybill !== false;
  $<HTMLInputElement>("stSavePerCamera").checked = options.savePerCamera === true;
  $<HTMLInputElement>("stAttachAll").checked = options.attachAllCameraCodeInfo === true;
  $<HTMLInputElement>("stCleanupOnStart").checked = options.cleanupOnStart !== false;
  $<HTMLInputElement>("stRetentionDays").value = String(options.retentionDays ?? 7);
  $<HTMLInputElement>("stMaxDiskPercent").value = String(options.maxDiskPercent ?? 85);
  $<HTMLInputElement>("stCleanupInterval").value = String(options.cleanupIntervalMinutes ?? 30);
  $<HTMLInputElement>("stSpoolRetention").value = String(options.spoolRetentionDays ?? 7);
  $<HTMLInputElement>("stImageDir").value = options.imageDir ?? "";
  $<HTMLInputElement>("stProviderImageDir").value = options.providerImageDir ?? "";
}

function numOrZero(id: string): number {
  const value = Number.parseInt($<HTMLInputElement>(id).value, 10);
  return Number.isFinite(value) ? value : -1;   // 非法填法直接给 -1，让后端校验报错
}

function collectStorage(): StorageOptions {
  return {
    saveOriginal: $<HTMLInputElement>("stSaveOriginal").checked,
    saveWaybill: $<HTMLInputElement>("stSaveWaybill").checked,
    savePerCamera: $<HTMLInputElement>("stSavePerCamera").checked,
    attachAllCameraCodeInfo: $<HTMLInputElement>("stAttachAll").checked,
    providerImageDir: $<HTMLInputElement>("stProviderImageDir").value.trim(),
    imageDir: $<HTMLInputElement>("stImageDir").value.trim(),
    retentionDays: numOrZero("stRetentionDays"),
    maxDiskPercent: numOrZero("stMaxDiskPercent"),
    cleanupIntervalMinutes: numOrZero("stCleanupInterval"),
    cleanupOnStart: $<HTMLInputElement>("stCleanupOnStart").checked,
    spoolRetentionDays: numOrZero("stSpoolRetention")
  };
}

async function saveStorage(): Promise<void> {
  const box = $("storageProblems");
  clear(box);
  const res = await api.saveStorageConfig(collectStorage());

  if (res.status === 200 && res.data?.ok) {
    badge($("storageBadge"), "已保存 " + res.data.changedCount + " 项", "ok");
    $("storageMsg").textContent = (res.data.note ?? "") +
      (res.data.backup ? "　备份：" + res.data.backup : "");
    if (res.data.needRestartHost) {
      box.appendChild(el("div", "! 改完要重启采集宿主才生效（宿主只在启动时读 gateway.ini）", "warnText"));
    }
    await refreshStorage();
    await refreshBackups();
    return;
  }

  badge($("storageBadge"), "保存失败", "err");
  box.appendChild(el("div", "✗ " + (res.message ?? "HTTP " + res.status), "err"));
  $("storageMsg").textContent = "参数没通过校验：配置文件没有被改动";
}

// ================================================================
// C5：配置备份与回滚
// ================================================================

async function refreshBackups(): Promise<void> {
  const res = await api.backups();
  const rows = $<HTMLTableSectionElement>("backupRows");
  clear(rows);
  if (!res.data) {
    $("backupMsg").textContent = "读取失败：" + (res.message ?? "HTTP " + res.status);
    return;
  }
  const list = res.data as BackupItem[];
  $("backupMsg").textContent = list.length > 0 ? "共 " + list.length + " 份备份（新的在前）" : "还没有备份：改一次配置就会生成";

  for (const item of list) {
    const tr = el("tr");
    tr.appendChild(cell(item.fileName, "code"));
    tr.appendChild(cell(item.modified, "muted"));
    tr.appendChild(cell(item.sizeText, "muted"));
    tr.appendChild(cell(item.target + (item.targetExists ? "" : "（目标不存在）"), item.targetExists ? "" : "pending"));
    tr.appendChild(cell(item.effect));

    const action = el("td");
    const button = el("button", "回滚", "btn secondary small");
    // C7：确认框是自绘的异步对话框，回调跟着改成 async
    button.addEventListener("click", async () => {
      const yes = await confirmBox("确定用 " + item.fileName + " 覆盖 " + item.target + " 吗？当前内容会先另存一份。", {
        title: "回滚配置",
        danger: true
      });
      if (!yes) return;
      void rollbackBackup(item.fileName);
    });
    action.appendChild(button);
    tr.appendChild(action);
    rows.appendChild(tr);
  }
}

async function rollbackBackup(fileName: string): Promise<void> {
  const res = await api.rollback(fileName);
  if (res.status === 200 && res.data?.ok) {
    badge($("backupMsg"), "已回滚 " + res.data.target, "ok");
    notify("已回滚 " + res.data.target + "\n\n" + (res.data.effect ?? "") +
      (res.data.backupOfCurrent ? "\n回滚前的内容另存为：" + res.data.backupOfCurrent : ""));
    await refreshConfig();
    return;
  }
  badge($("backupMsg"), "回滚失败", "err");
  notify(res.message ?? "回滚失败");
}

// ================================================================
// A8-3：配置模板（相机清单 + 触发模式 + 存图策略）
// ================================================================

/** 当前在"套用"框里选中的模板名 */
let templatePicked: string | null = null;

async function refreshTemplates(): Promise<void> {
  const res = await api.templates();
  const rows = $<HTMLTableSectionElement>("tplRows");
  clear(rows);
  if (!res.data) {
    $("tplMsg").textContent = "读取失败：" + (res.message ?? "HTTP " + res.status);
    return;
  }

  const list = res.data.templates ?? [];
  $("tplMsg").textContent = list.length > 0
    ? "共 " + list.length + " 份模板　·　目录：" + res.data.directory
    : "还没有模板：填个名字点「把当前配置另存为模板」　·　目录：" + res.data.directory;

  for (const tpl of list) {
    rows.appendChild(templateRow(tpl));
  }
}

function templateRow(tpl: ConfigTemplateSummary): HTMLTableRowElement {
  const tr = el("tr");
  tr.appendChild(cell(tpl.name, "code"));
  tr.appendChild(cell(dash(tpl.note), "muted"));
  tr.appendChild(cell(dash(tpl.createdAt), "muted"));
  tr.appendChild(cell(tpl.cameraCount));
  tr.appendChild(cell((tpl.triggerName ?? "") + "（" + dash(tpl.triggerMode) + "）"));
  tr.appendChild(cell(tpl.retentionDays ?? 0));
  tr.appendChild(cell(dash(tpl.source), "muted"));

  const action = el("td");

  const diff = el("button", "对比", "btn secondary small");
  diff.addEventListener("click", () => void showTemplateDiff(tpl.name));
  action.appendChild(diff);

  const apply = el("button", "套用", "btn secondary small");
  apply.style.marginLeft = "6px";
  apply.addEventListener("click", () => {
    templatePicked = tpl.name;
    $("tplApplyBox").style.display = "";
    $("tplApplyWhich").textContent = "套用模板「" + tpl.name + "」：";
    $("tplDiff").textContent = "已选中模板「" + tpl.name + "」，勾选要套用的内容后点「套用选中的内容」。\n（建议先点「对比」确认会改什么）";
  });
  action.appendChild(apply);

  const download = el("a", "导出", "btn secondary small");
  download.href = api.templateDownloadUrl(tpl.name);
  download.style.marginLeft = "6px";
  download.style.textDecoration = "none";
  action.appendChild(download);

  const remove = el("button", "删除", "btn secondary small");
  remove.style.marginLeft = "6px";
  // C7：确认框是自绘的异步对话框，回调跟着改成 async
  remove.addEventListener("click", async () => {
    if (!await confirmBox("删除模板「" + tpl.name + "」？", { title: "删除模板", danger: true })) return;
    void api.deleteTemplate(tpl.name).then(async (res) => {
      if (res.status !== 200) {
        notify(res.message ?? "删除失败");
        return;
      }
      if (templatePicked === tpl.name) {
        templatePicked = null;
        $("tplApplyBox").style.display = "none";
      }
      await refreshTemplates();
    });
  });
  action.appendChild(remove);

  tr.appendChild(action);
  return tr;
}

async function saveTemplate(): Promise<void> {
  const name = $<HTMLInputElement>("tplName").value.trim();
  const note = $<HTMLInputElement>("tplNote").value.trim();
  if (!name) {
    badge($("tplBadge"), "先填模板名", "err");
    return;
  }

  const res = await api.saveTemplate(name, note);
  if (res.status === 200 && res.data?.ok) {
    badge($("tplBadge"), "已保存 " + name, "ok");
    $("tplMsg").textContent = "模板已写入：" + res.data.file + "（含 " + res.data.cameraCount + " 台相机）";
    $<HTMLInputElement>("tplName").value = "";
    $<HTMLInputElement>("tplNote").value = "";
    await refreshTemplates();
  } else {
    badge($("tplBadge"), "保存失败", "err");
    notify(res.message ?? "保存失败");
  }
}

async function showTemplateDiff(name: string): Promise<void> {
  const res = await api.templateDiff(name);
  if (res.status !== 200 || !res.data) {
    $("tplDiff").textContent = "对比失败：" + (res.message ?? "HTTP " + res.status);
    return;
  }

  const data = res.data;
  const lines: string[] = [];
  lines.push("模板「" + data.name + "」　创建于 " + (data.templateCreatedAt ?? "?") + "　来源：" + (data.templateSource ?? "?"));
  lines.push("模板：相机 " + data.template.cameraCount + " 台 / 触发模式 " + (data.template.triggerMode ?? "?") +
    "　　当前：相机 " + data.current.cameraCount + " 台 / 触发模式 " + (data.current.triggerMode ?? "?"));
  lines.push("");
  if (data.same) {
    lines.push("✓ 当前配置和模板一致，不用套用");
  } else {
    lines.push("套用后会改 " + data.changeCount + " 处：");
    for (const c of data.changes) {
      lines.push("  [" + c.area + "] " + c.item + "　" + c.kind + "：模板=" + c.template + "　当前=" + c.current);
    }
  }
  $("tplDiff").textContent = lines.join("\n");
}

async function applyTemplate(): Promise<void> {
  if (!templatePicked) {
    notify("先在列表里点某份模板的「套用」");
    return;
  }

  const body = {
    name: templatePicked,
    applyCameras: $<HTMLInputElement>("tplApplyCameras").checked,
    applyTrigger: $<HTMLInputElement>("tplApplyTrigger").checked,
    applyStorage: $<HTMLInputElement>("tplApplyStorage").checked,
    skipVerify: $<HTMLInputElement>("tplApplySkipVerify").checked,
    stopHost: $<HTMLInputElement>("cfgStopHost").checked,
    restartHost: $<HTMLInputElement>("cfgRestartHost").checked
  };

  if (!body.applyCameras && !body.applyTrigger && !body.applyStorage) {
    notify("至少勾一项要套用的内容");
    return;
  }
  // C7：window.confirm → 自绘确认框（异步）
  const yes = await confirmBox(
    "确定把模板「" + templatePicked + "」套用到本机吗？\n（相机清单/触发模式会写 cfg 并做校验，失败自动回滚）",
    { title: "套用模板", danger: true }
  );
  if (!yes) return;

  const button = $<HTMLButtonElement>("btnTplApply");
  button.disabled = true;
  button.textContent = "套用中…";
  $("tplDiff").textContent = "正在套用…（会启动一次 SDK 做校验，最长 4 分钟；勾了 -SkipVerify 则只写配置）";

  try {
    const res = await api.applyTemplate(body);
    if (res.status === 200 && res.data?.ok) {
      const applied = res.data.applied as { cameras?: number; triggerMode?: string | null } | null;
      badge($("tplBadge"), "已套用 " + res.data.name, "ok");
      const apply = res.data.apply as { conclusion?: string; exitCode?: number } | null;
      $("tplDiff").textContent =
        "套用完成：模板「" + res.data.name + "」\n" +
        "  相机清单：" + (applied?.cameras ?? 0) + " 台\n" +
        "  触发模式：" + (applied?.triggerMode ?? "（未套用）") + "\n" +
        (apply ? "  一键应用结论：" + (apply.conclusion ?? "") + "（退出码 " + (apply.exitCode ?? 0) + "）\n" : "") +
        (res.data.storage ? "  存图策略：已写入 gateway.ini（重启采集宿主后生效）\n" : "") +
        "\n" + (res.data.note ?? "");
      await refreshConfig();
    } else {
      badge($("tplBadge"), "套用失败", "err");
      $("tplDiff").textContent = "套用失败：" + (res.message ?? "HTTP " + res.status);
    }
  } finally {
    button.disabled = false;
    button.textContent = "套用选中的内容";
  }
}
