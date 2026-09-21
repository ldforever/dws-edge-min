/**
 * 设备信息页（A9）：相机清单 + 方位编辑 + 六面概览 + CSV 导出。
 *
 * 方位改动的流程：下拉只是记在本地 dirty 表里（标黄），点"保存方位"才写
 * runtime\config\camera-positions.ini；采集宿主重启后才影响条码上的方位字段，
 * 所以后端会给"已保存但未重启"的行打上 positionPending，这里显示"待重启生效"。
 */
import { api } from "./api.js?v=7e55d4ad";
import { $, cell, clear, csvCell, downloadText, el, positionLabel, FACE_ORDER, notify } from "./dom.js?v=7e55d4ad";
import { statusText } from "./realtime.js?v=7e55d4ad";
/** 待保存的方位改动：key（清单标识或相机标识）→ 方位代码 */
const dirty = new Map();
let view = null;
let refreshTimer = null;
let rowsEl;
export function initDevices() {
    rowsEl = $("devRows");
    $("btnSavePos").addEventListener("click", () => void savePositions());
    $("btnRefreshDevices").addEventListener("click", () => void refreshDevices());
    $("btnExportCsv").addEventListener("click", exportCsv);
}
/** 相机状态变化时刷新设备页（800ms 合并，避免刷新风暴） */
export function scheduleDevicesRefresh() {
    if (!isActive())
        return;
    if (refreshTimer !== null)
        return;
    refreshTimer = window.setTimeout(() => {
        refreshTimer = null;
        void refreshDevices();
    }, 800);
}
function isActive() {
    return $("page-devices").classList.contains("active");
}
export async function refreshDevices() {
    const res = await api.devices();
    if (res.status !== 200 || !res.data)
        return;
    view = res.data;
    render();
}
function render() {
    const v = view;
    if (!v)
        return;
    $("devTotal").textContent = String(v.total ?? 0);
    $("devOnline").textContent = String(v.online ?? 0);
    $("devOffline").textContent = String(v.offline ?? 0);
    $("devMissing").textContent = String(v.declaredMissing ?? 0);
    $("devNoPos").textContent = String(v.positionMissing ?? 0);
    renderFaces(v);
    renderTable(v);
    // 刷新会重画整张表，别把"保存中…"这种临时提示留在界面上
    const msg = $("posMsg");
    if (msg.textContent === "保存中…" && dirty.size === 0)
        msg.textContent = "";
    $("devFoot").textContent =
        "相机 " + v.total + " 台 · 在线 " + v.online +
            " · SDK 发现 " + v.discovered +
            " · 清单未发现 " + v.declaredMissing +
            " · 方位文件 " + v.positionsFile;
}
function renderFaces(v) {
    const facesEl = $("faceCards");
    clear(facesEl);
    for (const name of FACE_ORDER) {
        const face = v.faces.find((f) => f.position === name) ?? {
            position: name,
            label: positionLabel(name),
            total: 0,
            online: 0,
            offline: 0,
            cameras: []
        };
        const card = el("div", "", "face " + (face.total === 0 ? "" : face.offline > 0 ? "part" : "on"));
        card.appendChild(el("div", positionLabel(name), "name"));
        card.appendChild(el("div", String(face.total), "num"));
        card.appendChild(el("div", face.total ? "在线 " + face.online + (face.offline ? " / 离线 " + face.offline : "") : "未使用", "who"));
        card.appendChild(el("div", face.cameras.join("、"), "who"));
        facesEl.appendChild(card);
    }
    // 简易六面位置示意：3×3 网格，空位留白
    const map = [
        [null, "top", "front"],
        ["left", "right", "rear"],
        ["spare", "bottom", "line"]
    ];
    const mapEl = $("faceMap");
    clear(mapEl);
    for (const row of map) {
        for (const name of row) {
            const box = el("div");
            if (name) {
                const face = v.faces.find((f) => f.position === name);
                box.textContent = positionLabel(name) + " " + (face?.total ?? 0);
                if (face?.total)
                    box.className = "has";
            }
            mapEl.appendChild(box);
        }
    }
    $("faceTip").textContent =
        "方位来自 " + v.positionsFile + (v.positionsFileExists ? "（已存在）" : "（文件还不存在，保存一次就会生成）");
}
function renderTable(v) {
    clear(rowsEl);
    $("devEmpty").style.display = v.cameras?.length ? "none" : "block";
    const options = v.positionOptions ?? [];
    for (const c of v.cameras ?? []) {
        const tr = el("tr");
        if (c.discovered === false)
            tr.className = "notfound";
        else if (!c.online)
            tr.className = "offline";
        tr.appendChild(positionCell(c, options));
        tr.appendChild(cell(statusText(c), c.online ? "" : "noread"));
        tr.appendChild(cell(c.declaredLabel ?? "未在清单中", c.declaredLabel ? "code" : "muted"));
        tr.appendChild(cell(c.deviceId, "muted"));
        tr.appendChild(cell(c.model));
        tr.appendChild(cell(c.serialNumber));
        tr.appendChild(cell(c.vendor));
        tr.appendChild(cell(c.firmware));
        tr.appendChild(cell(c.offlineCount ?? 0));
        tr.appendChild(cell(c.reconnectCount ?? 0));
        tr.appendChild(cell(c.codeCount ?? 0));
        tr.appendChild(cell(c.lastChangeTime, "muted"));
        rowsEl.appendChild(tr);
    }
}
function positionCell(c, options) {
    const td = el("td");
    const select = el("select", undefined, "pos");
    const none = el("option", "未设置");
    none.value = "";
    select.appendChild(none);
    for (const o of options) {
        const opt = el("option", positionLabel(o));
        opt.value = o;
        select.appendChild(opt);
    }
    // key 用清单标识（ip/序列号），没有声明时退回相机标识
    const key = c.declaredValue ?? c.deviceId;
    select.value = dirty.get(key) ?? c.position ?? "";
    select.addEventListener("change", () => {
        if (select.value === (c.position ?? "")) {
            dirty.delete(key);
            select.classList.remove("dirty");
        }
        else {
            dirty.set(key, select.value);
            select.classList.add("dirty");
        }
    });
    td.appendChild(select);
    const pending = dirty.has(key) || c.positionPending === true;
    if (pending) {
        td.appendChild(el("span", " 待保存/待重启", "tag"));
    }
    return td;
}
async function savePositions() {
    const msg = $("posMsg");
    if (dirty.size === 0) {
        msg.textContent = "没有改动";
        return;
    }
    msg.textContent = "保存中…";
    const positions = {};
    for (const [key, value] of dirty)
        positions[key] = value;
    const res = await api.savePositions(positions);
    if (res.status === 200 && res.data?.ok) {
        dirty.clear();
        await refreshDevices();
        // 提示放在刷新之后，否则会被重画流程清掉
        msg.textContent = "已保存 " + res.data.changed + " 条（共 " + res.data.count + " 条方位）· " + (res.data.note ?? "");
    }
    else {
        msg.textContent = "保存失败：" + (res.message ?? ("HTTP " + res.status));
    }
}
function exportCsv() {
    const v = view;
    if (!v?.cameras?.length) {
        notify("没有可导出的相机清单");
        return;
    }
    const head = ["方位", "状态", "清单标识", "相机标识", "型号", "序列号", "厂商", "固件", "掉线次数", "恢复次数", "出码数", "最近变化"];
    const lines = [head.map(csvCell).join(",")];
    for (const c of v.cameras) {
        lines.push([
            positionLabel(c.position),
            statusText(c),
            c.declaredLabel ?? "",
            c.deviceId ?? "",
            c.model ?? "",
            c.serialNumber ?? "",
            c.vendor ?? "",
            c.firmware ?? "",
            c.offlineCount ?? 0,
            c.reconnectCount ?? 0,
            c.codeCount ?? 0,
            c.lastChangeTime ?? ""
        ]
            .map(csvCell)
            .join(","));
    }
    downloadText("dws-cameras-" + new Date().toISOString().slice(0, 10) + ".csv", lines.join("\r\n"), "text/csv");
    $("posMsg").textContent = "已导出 " + v.cameras.length + " 台相机";
}
