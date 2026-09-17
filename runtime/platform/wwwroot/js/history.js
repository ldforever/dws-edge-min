/**
 * 历史查询页（B3）：按时间 / 条码 / 相机 / 无码 / 下发状态检索，看图片，导出 CSV。
 *
 * 说明：
 *   * 查询走服务端的"按 traceId 收敛后的索引"，所以 10 万条也是秒级；
 *   * 返回里带 total（命中总数）和 elapsedMs（服务端耗时），界面直接显示，方便现场自证性能；
 *   * 导出是打开一个下载链接（CSV，UTF-8 BOM，Excel 直接能开）。
 */
import { api } from "./api.js?v=4e5f5a2b";
import { $, cell, clear, el, imageCell, notify, positionLabel } from "./dom.js?v=4e5f5a2b";
let lastResult = null;
export function initHistory() {
    $("btnHistoryQuery").addEventListener("click", () => {
        currentOffset = 0;
        void refreshHistory();
    });
    $("btnHistoryPrev").addEventListener("click", () => void page(-1));
    $("btnHistoryNext").addEventListener("click", () => void page(1));
    $("btnHistoryExport").addEventListener("click", exportCsv);
    $("btnHistoryToday").addEventListener("click", () => {
        setRange(0);
        currentOffset = 0;
        void refreshHistory();
    });
    $("btnHistoryWeek").addEventListener("click", () => {
        setRange(6);
        currentOffset = 0;
        void refreshHistory();
    });
    // 默认查最近一天
    setRange(1);
}
let currentOffset = 0;
function setRange(daysBack) {
    const today = new Date();
    const from = new Date(today.getTime() - daysBack * 86400000);
    $("histFrom").value = isoDate(from);
    $("histTo").value = isoDate(today);
}
function isoDate(date) {
    const y = date.getFullYear();
    const m = String(date.getMonth() + 1).padStart(2, "0");
    const d = String(date.getDate()).padStart(2, "0");
    return y + "-" + m + "-" + d;
}
function readFilter() {
    const limit = Number($("histLimit").value) || 50;
    return {
        from: $("histFrom").value,
        to: $("histTo").value,
        code: $("histCode").value.trim(),
        deviceId: $("histDevice").value.trim(),
        noread: $("histNoread").value,
        dispatchState: $("histDispatch").value,
        hasImage: $("histImage").value,
        limit,
        offset: currentOffset
    };
}
async function page(direction) {
    const filter = readFilter();
    const next = currentOffset + direction * filter.limit;
    if (next < 0)
        return;
    if (lastResult && next >= lastResult.total)
        return;
    currentOffset = next;
    await refreshHistory();
}
export async function refreshHistory() {
    const filter = readFilter();
    $("histSummary").textContent = "查询中…";
    const res = await api.history(filter);
    if (res.status !== 200 || !res.data) {
        $("histSummary").textContent = "查询失败：" + (res.message ?? "HTTP " + res.status);
        return;
    }
    lastResult = res.data;
    render(res.data, filter);
}
function render(result, filter) {
    const body = $("histRows");
    clear(body);
    $("histEmpty").style.display = result.items.length ? "none" : "block";
    for (const record of result.items) {
        body.appendChild(renderRow(record));
    }
    const from = result.total === 0 ? 0 : filter.offset + 1;
    const to = filter.offset + result.returned;
    $("histSummary").textContent =
        "命中 " + result.total + " 条，显示 " + from + "-" + to +
            " · 服务端耗时 " + result.elapsedMs + " ms" +
            (result.fromIndex ? "（走索引）" : "（读快照）");
    $("btnHistoryPrev").toggleAttribute("disabled", filter.offset <= 0);
    $("btnHistoryNext").toggleAttribute("disabled", to >= result.total);
}
function renderRow(record) {
    const tr = el("tr");
    if (record.codeCount === 0) {
        tr.className = "notfound";
    }
    tr.appendChild(cell(record.time, "muted"));
    const codeTd = el("td");
    const codes = record.codeDetails?.length
        ? record.codeDetails
        : (record.codes ?? []).map((value) => ({ value }));
    if (codes.length) {
        codeTd.className = "code";
        codes.forEach((c, i) => {
            if (i > 0)
                codeTd.appendChild(document.createTextNode(" , "));
            codeTd.appendChild(document.createTextNode(c.value));
            const tags = [];
            if (c.position)
                tags.push(positionLabel(c.position));
            if (c.kind && c.kind !== "unknown")
                tags.push(c.kind.toUpperCase());
            if (tags.length)
                codeTd.appendChild(el("span", " (" + tags.join("/") + ")", "tag"));
        });
    }
    else {
        codeTd.className = "noread";
        codeTd.textContent = "NOREAD";
        const dropped = record.filteredCodes ?? [];
        if (dropped.length) {
            const tag = el("span", "  丢弃：" + dropped.map((f) => f.code).join(" , "), "tag");
            tag.style.color = "#d29922";
            codeTd.appendChild(tag);
        }
    }
    tr.appendChild(codeTd);
    tr.appendChild(cell(record.deviceId, "muted"));
    tr.appendChild(cell(record.codeCount === 0 ? "是" : "否", record.codeCount === 0 ? "noread" : ""));
    tr.appendChild(cell((record.weightGrams ?? 0) > 0 ? record.weightGrams : "—"));
    const sizeTd = el("td", "", "muted");
    const lengthMm = record.lengthMm ?? 0;
    sizeTd.textContent = lengthMm > 0
        ? Math.round(lengthMm) + "×" + Math.round(record.widthMm ?? 0) + "×" + Math.round(record.heightMm ?? 0)
        : "—";
    tr.appendChild(sizeTd);
    // B7：直接显示缩略图（点击看原图）
    tr.appendChild(imageCell(record.firstImagePath, record.imageCount ?? 0, 120));
    const stateTd = el("td");
    const state = record.dispatchState ?? "pending";
    const label = state === "sent" ? "已下发" : state === "failed" ? "下发失败" : "待下发";
    stateTd.appendChild(el("span", label, "badge " + (state === "sent" ? "ok" : state === "failed" ? "err" : "warn")));
    const attempts = record.dispatchAttempts ?? 0;
    if (attempts > 0) {
        stateTd.appendChild(el("span", " ×" + attempts, "tag"));
    }
    tr.appendChild(stateTd);
    tr.appendChild(cell(record.traceId, "muted"));
    return tr;
}
function exportCsv() {
    const filter = readFilter();
    if (lastResult && lastResult.total === 0) {
        notify("当前条件下没有数据可导出");
        return;
    }
    const url = api.historyExportUrl(filter);
    const a = el("a");
    a.href = url;
    a.download = "";
    a.click();
    $("histSummary").textContent = "已开始导出（浏览器会下载 CSV）…";
}
