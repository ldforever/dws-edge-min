/**
 * C4：统计看板（简版）。
 *
 * 一屏回答三个问题：**一共多少包、读码率多少、无码率多少**，
 * 并且能切成四个维度看：按相机（谁在干活）、按班次（哪个班干得多）、按小时、按日期。
 *
 * 数据全部来自 B3 的历史库（同一个索引、同一套过滤），所以"看板的数字"就是"库里查出来的数字"。
 * 班次在页面下方可配（默认白班 08:00-20:00、夜班 20:00-08:00，跨天班次的凌晨算前一天）。
 */
import { api } from "./api.js?v=31477bcc";
import { $, badge, csvCell, cell, clear, downloadText, el, notify } from "./dom.js?v=31477bcc";
const DIM_LABEL = {
    camera: "按相机",
    shift: "按班次",
    hour: "按小时",
    day: "按日期"
};
let board = null;
export function initStats() {
    $("btnStatsQuery").addEventListener("click", () => void refreshStats());
    $("btnStatsExport").addEventListener("click", () => exportCsv());
    $("btnStatsToday").addEventListener("click", () => {
        setRange(0);
        void refreshStats();
    });
    $("btnStatsWeek").addEventListener("click", () => {
        setRange(6);
        void refreshStats();
    });
    $("statsDim").addEventListener("change", () => void refreshStats());
    $("btnStatsShiftsSave").addEventListener("click", () => void saveShifts());
    $("btnStatsShiftsAdd").addEventListener("click", () => {
        $("shiftRows").appendChild(shiftRow({ name: "", start: "08:00", end: "20:00" }));
    });
    setRange(0);
}
function dateText(offsetDays) {
    const day = new Date();
    day.setDate(day.getDate() - offsetDays);
    const month = String(day.getMonth() + 1).padStart(2, "0");
    const date = String(day.getDate()).padStart(2, "0");
    return day.getFullYear() + "-" + month + "-" + date;
}
function dateKey(text) {
    return text.replace(/-/g, "");
}
function setRange(daysBack) {
    $("statsFrom").value = dateText(daysBack);
    $("statsTo").value = dateText(0);
}
export async function refreshStats() {
    const from = $("statsFrom").value || dateText(0);
    const to = $("statsTo").value || dateText(0);
    const dimension = $("statsDim").value;
    const deviceId = $("statsDevice").value.trim();
    $("statsMsg").textContent = "查询中…";
    const res = await api.statsBoard(dateKey(from), dateKey(to), dimension, deviceId || undefined);
    if (res.status !== 200 || !res.data) {
        $("statsMsg").textContent = "查询失败：" + (res.message ?? "HTTP " + res.status);
        return;
    }
    board = res.data;
    renderBoard(board);
    await refreshShifts();
}
function pct(value) {
    return (value ?? 0).toFixed(1) + "%";
}
function renderBoard(data) {
    const total = data.totals;
    $("kpiTotal").textContent = String(total.total);
    $("kpiReadRate").textContent = pct(total.readRatePercent);
    $("kpiNoreadRate").textContent = pct(total.noreadRatePercent);
    $("kpiNoread").textContent = String(total.noread);
    $("kpiImages").textContent = total.images + " / " + total.dispatchSent;
    const rows = $("statsRows");
    clear(rows);
    const max = data.groups.reduce((m, g) => Math.max(m, g.total), 0);
    for (const group of data.groups) {
        rows.appendChild(groupRow(group, max));
    }
    $("statsMsg").textContent = data.groupCount > 0
        ? "共 " + data.groupCount + " 组 · 服务端耗时 " + data.elapsedMs + " ms"
        : "这个范围内没有数据";
    $("statsFoot").textContent =
        data.from + " ~ " + data.to + "　维度：" + (DIM_LABEL[data.dimension] ?? data.dimension) +
            "　合计 " + total.total + " 包（有码 " + total.read + " / 无码 " + total.noread + "）" +
            "　有图 " + total.images + "　下发成功 " + total.dispatchSent + "　下发失败 " + total.dispatchFailed +
            (data.unmatchedShifts > 0 ? "　⚠ 有 " + data.unmatchedShifts + " 个包裹没匹配到班次" : "") +
            "　· " + (data.note ?? "");
}
function groupRow(group, max) {
    const tr = el("tr");
    if (group.noreadRatePercent >= 50)
        tr.className = "offline";
    tr.appendChild(cell(group.label, "code"));
    tr.appendChild(cell(group.total));
    tr.appendChild(cell(group.read));
    tr.appendChild(cell(group.noread, group.noread > 0 ? "noread" : "muted"));
    tr.appendChild(cell(pct(group.readRatePercent)));
    tr.appendChild(cell(pct(group.noreadRatePercent), group.noreadRatePercent > 0 ? "noread" : "muted"));
    const barTd = el("td");
    const bar = el("div", undefined, "bar");
    const fill = el("div", undefined, "barfill");
    fill.style.width = (max > 0 ? Math.max(2, Math.round((group.total / max) * 100)) : 0) + "%";
    bar.appendChild(fill);
    barTd.appendChild(bar);
    tr.appendChild(barTd);
    tr.appendChild(cell(group.images, "muted"));
    tr.appendChild(cell(group.dispatchSent, "muted"));
    tr.appendChild(cell((group.firstTime ?? "—") + " ~ " + (group.lastTime ?? "—"), "muted"));
    return tr;
}
function exportCsv() {
    if (!board) {
        notify("先查询一次再导出");
        return;
    }
    const header = ["维度", board.dimension, "范围", board.from + "~" + board.to];
    const columns = ["维度值", "总数", "有码", "无码", "读码率(%)", "无码率(%)", "有图", "下发成功", "下发失败", "重量缺失", "最早", "最晚"];
    const lines = [];
    lines.push("# 统计看板（C4）　" + header.join(" "));
    lines.push(columns.map(csvCell).join(","));
    const all = [board.totals, ...board.groups];
    for (const row of all) {
        lines.push([
            row.key === "all" ? "合计" : row.label,
            row.total, row.read, row.noread,
            row.readRatePercent, row.noreadRatePercent,
            row.images, row.dispatchSent, row.dispatchFailed, row.weightMissing,
            row.firstTime ?? "", row.lastTime ?? ""
        ].map(csvCell).join(","));
    }
    downloadText("dws-stats-" + board.dimension + "-" + board.from.replace(/-/g, "") + "-" + board.to.replace(/-/g, "") + ".csv", lines.join("\r\n"), "text/csv");
}
// ---------------------------------------------------------------- 班次设置
async function refreshShifts() {
    const res = await api.shifts();
    if (!res.data)
        return;
    const rows = $("shiftRows");
    clear(rows);
    for (const shift of res.data.shifts) {
        rows.appendChild(shiftRow({ name: shift.name, start: shift.start, end: shift.end }, shift.span));
    }
    $("shiftsFile").textContent = "配置文件：" + res.data.file + "　" + (res.data.note ?? "");
}
function shiftRow(shift, span) {
    const tr = el("tr");
    const nameTd = el("td");
    const name = el("input", undefined, "rule-input");
    name.type = "text";
    name.size = 12;
    name.value = shift.name;
    name.placeholder = "白班";
    nameTd.appendChild(name);
    tr.appendChild(nameTd);
    const startTd = el("td");
    const start = el("input", undefined, "rule-input");
    start.type = "text";
    start.size = 6;
    start.value = shift.start;
    start.placeholder = "08:00";
    startTd.appendChild(start);
    tr.appendChild(startTd);
    const endTd = el("td");
    const end = el("input", undefined, "rule-input");
    end.type = "text";
    end.size = 6;
    end.value = shift.end;
    end.placeholder = "20:00";
    endTd.appendChild(end);
    tr.appendChild(endTd);
    tr.appendChild(cell(span ?? "—", "muted"));
    const action = el("td");
    const remove = el("button", "删除", "btn secondary small");
    remove.addEventListener("click", () => tr.remove());
    action.appendChild(remove);
    tr.appendChild(action);
    return tr;
}
async function saveShifts() {
    const rows = Array.from($("shiftRows").children);
    const shifts = [];
    for (const row of rows) {
        const inputs = row.querySelectorAll("input");
        if (inputs.length < 3)
            continue;
        const name = inputs[0].value.trim();
        const start = inputs[1].value.trim();
        const end = inputs[2].value.trim();
        if (!name && !start && !end)
            continue;
        shifts.push({ name, start, end });
    }
    if (shifts.length === 0) {
        badge($("shiftsBadge"), "至少要有一个班次", "err");
        return;
    }
    const res = await api.saveShifts(shifts);
    if (res.status === 200 && res.data?.ok) {
        badge($("shiftsBadge"), "已保存 " + shifts.length + " 个班次", "ok");
        await refreshStats();
    }
    else {
        badge($("shiftsBadge"), "保存失败", "err");
        notify(res.message ?? "保存失败");
    }
}
