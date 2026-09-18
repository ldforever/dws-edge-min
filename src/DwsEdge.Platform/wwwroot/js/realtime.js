/**
 * 实时监控页：KPI 卡片 + 最新过包（卡片墙 / 表格可切换） + 相机状态表。
 *
 * 说明：
 *   * 过包表按 traceId 去重 —— SSE 会把"补全重量体积后"的同一条记录再推一次，
 *     直接原地更新那一行，比追加两行清楚（同时仍能看到 更新次数 ×2）；
 *   * C1：默认用"卡片墙"展示最新包裹（缩略图 + 条码 + 时间 + 相机 + 状态），
 *     点缩略图看原图；喜欢表格的现场可以切回表格，选择记在浏览器里；
 *   * 相机状态表与"设备信息"页共用同一份数据源（平台推送的 camera 事件）。
 */
import { api } from "./api.js?v=62d56586";
import { $, cell, clear, el, gb, imageCell, positionLabel, dash } from "./dom.js?v=62d56586";
import { alertLabel, fmtAge, fmtRate } from "./monitor.js?v=62d56586";
const MAX_ROWS = 120;
/** 卡片墙最多留多少张（一屏大概 6-12 张，多出来的往下滚） */
const MAX_CARDS = 30;
/** 页面首屏拉多少条历史（平台还会通过 SSE 补发最近 20 条） */
const INITIAL_PARCELS = 50;
/** 视图选择存在浏览器里，现场刷新页面不用每次重新点 */
const VIEW_KEY = "dws.view.parcels";
/** 相机状态墙的视图选择（C2） */
const CAM_VIEW_KEY = "dws.view.cameras";
const rowByTrace = new Map();
const cameras = new Map();
/** C2：B8 那边推来的每台指标（在线率/心跳/告警），和 cameras 按 deviceId 合并成状态墙 */
const monitorStats = new Map();
let rowsEl;
let emptyEl;
let camRowsEl;
let camEmptyEl;
let cardWallEl;
let cardEmptyEl;
let tableViewEl;
let camWallEl;
let camTableWrapEl;
let camWallSummaryEl;
const cards = new Map();
export function initRealtime() {
    rowsEl = $("rows");
    emptyEl = $("emptyTip");
    camRowsEl = $("camRows");
    camEmptyEl = $("camEmpty");
    cardWallEl = $("cardWall");
    cardEmptyEl = $("cardEmpty");
    tableViewEl = $("tableView");
    camWallEl = $("camWall");
    camTableWrapEl = $("camTableWrap");
    camWallSummaryEl = $("camWallSummary");
    $("viewCards").addEventListener("click", () => setView("cards"));
    $("viewTable").addEventListener("click", () => setView("table"));
    $("camViewCards").addEventListener("click", () => setCamView("cards"));
    $("camViewTable").addEventListener("click", () => setCamView("table"));
    let saved = "";
    let savedCam = "";
    try {
        saved = window.localStorage.getItem(VIEW_KEY) ?? "";
        savedCam = window.localStorage.getItem(CAM_VIEW_KEY) ?? "";
    }
    catch {
        saved = ""; // 隐私模式/壳里可能不让用 localStorage，忽略即可
        savedCam = "";
    }
    setView(saved === "table" ? "table" : "cards");
    setCamView(savedCam === "table" ? "table" : "cards");
}
/** C2：相机状态墙 / 表格 两种视图切换（和过包区一样记住选择） */
function setCamView(view) {
    const cardsOn = view === "cards";
    camWallEl.style.display = cardsOn ? "" : "none";
    camWallSummaryEl.style.display = cardsOn ? "" : "none";
    camTableWrapEl.style.display = cardsOn ? "none" : "";
    $("camViewCards").className = "btn small" + (cardsOn ? "" : " secondary");
    $("camViewTable").className = "btn small" + (cardsOn ? " secondary" : "");
    try {
        window.localStorage.setItem(CAM_VIEW_KEY, view);
    }
    catch {
        // 忽略
    }
}
/** 切换"卡片墙 / 表格"两种视图 */
function setView(view) {
    const cardsOn = view === "cards";
    cardWallEl.style.display = cardsOn ? "" : "none";
    cardEmptyEl.style.display = cardsOn && cards.size === 0 ? "block" : "none";
    tableViewEl.style.display = cardsOn ? "none" : "";
    $("viewCards").className = "btn small" + (cardsOn ? "" : " secondary");
    $("viewTable").className = "btn small" + (cardsOn ? " secondary" : "");
    try {
        window.localStorage.setItem(VIEW_KEY, view);
    }
    catch {
        // 存不了就算了，不影响使用
    }
}
export function renderStats(s) {
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
export function renderParcel(p, flash) {
    emptyEl.style.display = "none";
    const key = p.traceId ?? (p.time ?? "") + "|" + (p.codes ?? []).join(",");
    const previous = rowByTrace.get(key);
    if (previous) {
        previous.remove();
        rowByTrace.delete(key);
    }
    const tr = el("tr");
    tr.dataset.key = key;
    if (flash)
        tr.className = "new";
    tr.appendChild(cell(p.time));
    const codeTd = el("td");
    const details = p.codeDetails?.length
        ? p.codeDetails
        : (p.codes ?? []).map((value) => ({ value }));
    if (details.length) {
        codeTd.className = "code";
        details.forEach((d, i) => {
            if (i > 0)
                codeTd.appendChild(document.createTextNode(" , "));
            codeTd.appendChild(document.createTextNode(d.value));
            const tags = [];
            if (d.position)
                tags.push(positionLabel(d.position));
            if (d.kind && d.kind !== "unknown")
                tags.push(d.kind.toUpperCase());
            if (tags.length) {
                codeTd.appendChild(el("span", " (" + tags.join("/") + ")", "tag"));
            }
        });
    }
    else {
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
    }
    else if (p.dispatchState === "failed") {
        stageTd.appendChild(el("span", " 下发失败" + (p.dispatchAttempts ? "×" + p.dispatchAttempts : ""), "pending"));
    }
    tr.appendChild(stageTd);
    // B7：直接显示缩略图（点击看原图），列表里不再拉原图
    tr.appendChild(imageCell((p.imageCount ?? 0) > 0 ? p.firstImagePath : null, p.imageCount ?? 0));
    rowsEl.insertBefore(tr, rowsEl.firstChild);
    rowByTrace.set(key, tr);
    while (rowsEl.children.length > MAX_ROWS) {
        const last = rowsEl.lastElementChild;
        if (!last)
            break;
        rowByTrace.delete(last.dataset.key ?? "");
        rowsEl.removeChild(last);
    }
    // C1：同一份数据也渲染成卡片（卡片墙才是默认视图）
    upsertCard(key, p, flash);
}
// ---------------------------------------------------------------- C1 过包卡片墙
/** 一个包裹的"状态"徽标：读码 / 无码 / 待补全 / 已下发 / 下发失败 */
function cardTags(p) {
    const tags = [];
    const count = p.codeCount ?? (p.codes ?? []).length;
    if (count > 0) {
        tags.push(el("span", "读码 " + count, "badge ok"));
    }
    else {
        tags.push(el("span", "NOREAD", "badge err"));
    }
    if (p.complete === false) {
        tags.push(el("span", "待补全", "badge warn"));
    }
    if ((p.updates ?? 0) > 1) {
        tags.push(el("span", "更新 ×" + p.updates, "tag"));
    }
    if ((p.imageCount ?? 0) > 0) {
        tags.push(el("span", "图 " + p.imageCount, "tag"));
    }
    if (p.dispatchState === "sent") {
        tags.push(el("span", "已下发", "badge ok"));
    }
    else if (p.dispatchState === "failed") {
        tags.push(el("span", "下发失败" + (p.dispatchAttempts ? "×" + p.dispatchAttempts : ""), "badge err"));
    }
    // 被规则丢掉的码：现场最常问"为什么是 NOREAD"，直接写在卡片上
    const dropped = p.filteredCodes ?? [];
    if (dropped.length > 0) {
        tags.push(el("span", "规则丢弃 " + dropped.length, "tag"));
    }
    return tags;
}
function buildCard(key) {
    const root = el("div", undefined, "pcard");
    root.dataset.key = key;
    const link = el("a", undefined, "pcard-thumb");
    link.target = "_blank";
    const img = el("img", undefined, "thumb");
    img.loading = "lazy";
    img.alt = "包裹图片";
    const placeholder = el("span", "无图", "ph");
    link.appendChild(img);
    link.appendChild(placeholder);
    const body = el("div", undefined, "pcard-body");
    const code = el("div", undefined, "pcard-code");
    const meta = el("div", undefined, "pcard-meta");
    const tags = el("div", undefined, "pcard-tags");
    const extra = el("div", undefined, "pcard-extra");
    body.appendChild(code);
    body.appendChild(meta);
    body.appendChild(tags);
    body.appendChild(extra);
    root.appendChild(link);
    root.appendChild(body);
    return { root, link, img, placeholder, code, meta, tags, extra };
}
function updateCard(parts, p) {
    // 条码：值 + 方位/类型标签（和表格里的显示口径一致）
    const details = p.codeDetails?.length
        ? p.codeDetails
        : (p.codes ?? []).map((value) => ({ value }));
    clear(parts.code);
    if (details.length === 0) {
        parts.code.className = "pcard-code noread";
        parts.code.textContent = "NOREAD";
    }
    else {
        parts.code.className = "pcard-code";
        details.forEach((d, i) => {
            if (i > 0)
                parts.code.appendChild(document.createTextNode("  "));
            parts.code.appendChild(document.createTextNode(d.value));
            const marks = [];
            if (d.position)
                marks.push(positionLabel(d.position));
            if (d.kind && d.kind !== "unknown")
                marks.push(d.kind.toUpperCase());
            if (marks.length)
                parts.code.appendChild(el("span", " " + marks.join("/"), "tag"));
        });
    }
    parts.meta.textContent = (p.time ?? "") + "　·　" + (p.deviceId ?? "未知相机");
    clear(parts.tags);
    for (const tag of cardTags(p)) {
        parts.tags.appendChild(tag);
    }
    // 重量/体积：分阶段 provider 先给条码、后给重量体积，这里补上就立刻能看到
    const bits = [];
    const weight = p.weightGrams ?? -1;
    const volume = p.volumeMm3 ?? 0;
    if (weight > 0)
        bits.push((weight / 1000).toFixed(2) + " kg");
    const lengthMm = p.lengthMm ?? 0;
    if (lengthMm > 0) {
        bits.push(Math.round(lengthMm) + "×" + Math.round(p.widthMm ?? 0) + "×" + Math.round(p.heightMm ?? 0) + " mm");
    }
    if (volume > 0)
        bits.push(Math.round(volume / 1000) + " cm³");
    parts.extra.textContent = bits.join("　·　");
    parts.extra.style.display = bits.length ? "" : "none";
    // 缩略图：只有路径变了才换 src，避免同一包裹更新时重复请求/闪白
    const path = (p.imageCount ?? 0) > 0 ? p.firstImagePath : null;
    if (path) {
        const thumbUrl = "/api/images/thumb?w=320&path=" + encodeURIComponent(path);
        if (parts.img.getAttribute("src") !== thumbUrl) {
            parts.img.setAttribute("src", thumbUrl);
        }
        parts.img.style.display = "";
        parts.placeholder.style.display = "none";
        parts.link.href = "/api/images?path=" + encodeURIComponent(path);
        parts.link.title = "点击查看原图：" + path;
    }
    else {
        parts.img.removeAttribute("src");
        parts.img.style.display = "none";
        parts.placeholder.style.display = "";
        parts.link.removeAttribute("href");
        parts.link.title = "这个包裹没有图片";
    }
}
function upsertCard(key, p, flash) {
    let parts = cards.get(key);
    if (!parts) {
        parts = buildCard(key);
        cards.set(key, parts);
    }
    updateCard(parts, p);
    // 更新过的包裹挪到最前面：卡片墙永远按"最近一次更新"倒序，和表格口径一致
    cardWallEl.insertBefore(parts.root, cardWallEl.firstChild);
    // 每张卡都要保留自己的高亮动画：先摘掉类、强制重排、再加回来
    parts.root.classList.remove("new");
    if (flash) {
        void parts.root.offsetWidth;
        parts.root.classList.add("new");
    }
    cardEmptyEl.style.display = "none";
    while (cardWallEl.children.length > MAX_CARDS) {
        const last = cardWallEl.lastElementChild;
        if (!last)
            break;
        cards.delete(last.dataset.key ?? "");
        cardWallEl.removeChild(last);
    }
}
export function upsertCamera(camera) {
    if (!camera?.deviceId)
        return;
    cameras.set(camera.deviceId, { ...cameras.get(camera.deviceId), ...camera });
    renderCameras();
    renderCameraWall();
}
export function replaceCameras(list) {
    cameras.clear();
    for (const c of list) {
        if (c?.deviceId)
            cameras.set(c.deviceId, c);
    }
    renderCameras();
    renderCameraWall();
}
/**
 * C2：收下 B8 的监控快照（在线率/心跳/活动告警），和相机基础状态合起来渲染状态墙。
 * 由 main.ts 在收到 SSE 的 type=monitor 时调用（monitor.ts 只负责设备信息页那块表）。
 */
export function applyMonitorStats(list) {
    for (const item of list ?? []) {
        if (!item?.camera)
            continue;
        monitorStats.set(item.camera, item);
        // 监控快照里带着权威的出码计数，顺手补进 cameras（有些相机可能还没收到 camera 事件）
        const known = cameras.get(item.camera);
        if (known) {
            if (typeof item.codeCount === "number")
                known.codeCount = item.codeCount;
            if (item.lastCodeTime)
                known.lastCodeTime = item.lastCodeTime;
            if (typeof item.offlineCountTotal === "number")
                known.offlineCount = item.offlineCountTotal;
        }
    }
    renderCameras();
    renderCameraWall();
}
/** C2：收下轻量的"相机计数"推送（出一包就变的那部分），只更新数字，不做整表刷新 */
export function applyCameraCounters(list) {
    for (const item of list ?? []) {
        if (!item?.camera)
            continue;
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
            };
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
export function statusText(c) {
    if (c.discovered === false)
        return "未发现";
    return c.online ? "在线" : "离线";
}
function renderCameras() {
    const list = Array.from(cameras.values()).sort((a, b) => {
        const pa = a.positionOrder ?? 99;
        const pb = b.positionOrder ?? 99;
        if (pa !== pb)
            return pa - pb;
        return a.deviceId.localeCompare(b.deviceId);
    });
    clear(camRowsEl);
    camEmptyEl.style.display = list.length ? "none" : "block";
    for (const c of list) {
        const tr = el("tr");
        if (!c.online)
            tr.className = "offline";
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
function cameraProblem(c, alerts) {
    if (c.discovered === false)
        return "未发现";
    if (!c.online)
        return "离线";
    if (alerts.length > 0)
        return alertLabel(alerts[0].code);
    return null;
}
function numberBox(value, label, className) {
    const box = el("div", undefined, "camnum" + (className ? " " + className : ""));
    box.appendChild(el("div", value, "n"));
    box.appendChild(el("div", label, "l"));
    return box;
}
/**
 * C2 相机状态墙的一格：方位 + 相机 + 在线状态 + 出码数 + 掉线次数 + 心跳 + 在线率 + 告警徽标。
 * 数据是两路合起来的：相机基础状态（type=camera / /api/cameras）+ 监控指标（type=monitor）+ 计数（type=camera-count）。
 */
function cameraCell(c) {
    const m = monitorStats.get(c.deviceId);
    const alerts = m?.alerts ?? [];
    const missing = c.discovered === false;
    const offline = missing || !c.online;
    const critical = alerts.some((a) => a.severity === "critical");
    const root = el("div", undefined, "camcell " + (missing ? "missing" : offline ? "offline" : "online") +
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
    if (identity)
        root.appendChild(el("div", identity, "camline muted"));
    if (alerts.length > 0) {
        const box = el("div", undefined, "camalerts");
        for (const a of alerts) {
            box.appendChild(el("span", alertLabel(a.code), "badge " + (a.severity === "critical" ? "err" : "warn")));
        }
        root.appendChild(box);
    }
    root.addEventListener("click", () => {
        $("tab-devices").click();
    });
    return root;
}
/** C2：渲染相机状态墙（每台一格）+ 一行汇总（在线/累计出码/异常台数） */
function renderCameraWall() {
    if (!camWallEl)
        return;
    const list = Array.from(cameras.values()).sort((a, b) => {
        const pa = a.positionOrder ?? 99;
        const pb = b.positionOrder ?? 99;
        if (pa !== pb)
            return pa - pb;
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
export async function loadInitial() {
    const parcels = await api.parcels(INITIAL_PARCELS);
    if (parcels.data?.length) {
        emptyEl.style.display = "none";
        for (let i = parcels.data.length - 1; i >= 0; i--) {
            const p = parcels.data[i];
            if (p)
                renderParcel(p, false);
        }
    }
    const stats = await api.stats();
    if (stats.data)
        renderStats(stats.data);
    const cams = await api.cameras();
    if (cams.data)
        replaceCameras(cams.data);
    // C2：出码计数单独取一次（相机状态墙首屏就要显示"这台相机出过多少码"）
    const counters = await api.cameraCounters();
    if (counters.data)
        applyCameraCounters(counters.data);
}
