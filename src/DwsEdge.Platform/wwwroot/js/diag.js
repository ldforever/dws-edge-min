/**
 * C6：日志查看与导出（诊断页）。
 *
 * 三件事：
 *   * 一屏看清"日志都在哪、有多大、最新是什么时候"（六个来源卡片）；
 *   * 按时间范围列出文件，能直接看尾部（不用把几百 MB 拉到浏览器里）；
 *   * 一键打包成 zip（按来源分目录 + README），拿到别的机器上离线分析。
 *
 * 打包走浏览器直接下载（服务端流式写 zip），所以大包也不会把界面卡住。
 */
import { api } from "./api.js?v=d9a080c0";
import { $, badge, cell, clear, el, notify } from "./dom.js?v=d9a080c0";
/** 勾选进打包的来源（默认全选） */
const checked = new Set();
let overview = null;
let current = null;
export function initDiag() {
    $("btnDiagReload").addEventListener("click", () => void refreshDiag());
    $("btnDiagBundle").addEventListener("click", () => bundle());
    $("btnDiagToday").addEventListener("click", () => {
        setRange(0);
        void loadFiles();
    });
    $("btnDiag3").addEventListener("click", () => {
        setRange(2);
        void loadFiles();
    });
    $("btnDiagWeek").addEventListener("click", () => {
        setRange(6);
        void loadFiles();
    });
    setRange(2);
}
function setRange(daysBack) {
    const to = new Date();
    const from = new Date();
    from.setDate(from.getDate() - daysBack);
    $("diagFrom").value = dateText(from);
    $("diagTo").value = dateText(to);
}
function dateText(day) {
    const month = String(day.getMonth() + 1).padStart(2, "0");
    const date = String(day.getDate()).padStart(2, "0");
    return day.getFullYear() + "-" + month + "-" + date;
}
function dateKey(text) {
    return text.replace(/-/g, "");
}
export async function refreshDiag() {
    const res = await api.diagSources();
    if (res.status !== 200 || !res.data) {
        $("diagMsg").textContent = "读取失败：" + (res.message ?? "HTTP " + res.status);
        return;
    }
    overview = res.data;
    // 第一次进来默认全选；之后保留用户的勾选（来源增删时按 id 兜底）
    if (checked.size === 0) {
        for (const source of overview.sources)
            checked.add(source.id);
    }
    renderSources(overview.sources);
    $("diagMsg").textContent =
        "共 " + overview.totalFiles + " 个文件 / " + overview.sizeText + "　·　" + (overview.note ?? "");
    if (!current && overview.sources.length > 0) {
        current = overview.sources[0].id;
    }
    await loadFiles();
}
function renderSources(list) {
    const box = $("diagSources");
    clear(box);
    for (const source of list) {
        const card = el("div", undefined, "camcell " + (source.fileCount > 0 ? "online" : "missing"));
        card.dataset.source = source.id;
        if (source.id === current)
            card.classList.add("picked");
        card.style.cursor = "pointer";
        const head = el("div", undefined, "camhead");
        const pick = el("input");
        pick.type = "checkbox";
        pick.checked = checked.has(source.id);
        pick.title = "勾上 = 打包时包含这个来源";
        pick.addEventListener("click", (event) => {
            event.stopPropagation();
            if (pick.checked)
                checked.add(source.id);
            else
                checked.delete(source.id);
            $("diagPick").textContent = "打包包含 " + checked.size + " 个来源";
        });
        head.appendChild(pick);
        head.appendChild(el("span", source.name, "camname"));
        head.appendChild(el("span", source.fileCount + " 个", "camstate"));
        card.appendChild(head);
        const nums = el("div", undefined, "camnums");
        nums.appendChild(numBox(source.sizeText, "大小"));
        nums.appendChild(numBox(String(source.fileCount), "文件数"));
        card.appendChild(nums);
        card.appendChild(el("div", "最新：" + (source.newestFile ?? "（没有文件）"), "camline"));
        card.appendChild(el("div", source.newestTime ?? "—", "camline muted"));
        card.title = source.note + "\n目录：" + source.directory;
        card.addEventListener("click", () => {
            current = source.id;
            renderSources(list);
            void loadFiles();
        });
        box.appendChild(card);
    }
    $("diagPick").textContent = "打包包含 " + checked.size + " 个来源";
}
function numBox(value, label) {
    const box = el("div", undefined, "camnum");
    box.appendChild(el("div", value, "n"));
    box.appendChild(el("div", label, "l"));
    return box;
}
async function loadFiles() {
    if (!current)
        return;
    const from = $("diagFrom").value || dateText(new Date());
    const to = $("diagTo").value || dateText(new Date());
    const res = await api.diagFiles(current, dateKey(from), dateKey(to));
    const rows = $("diagFileRows");
    clear(rows);
    if (res.status !== 200 || !res.data) {
        $("diagMsg").textContent = "列文件失败：" + (res.message ?? "HTTP " + res.status);
        return;
    }
    $("diagFilesOf").textContent =
        res.data.name + "：" + res.data.fileCount + " 个文件（" + res.data.sizeText + "），范围 " + res.data.from + " ~ " + res.data.to;
    for (const file of res.data.files) {
        rows.appendChild(fileRow(res.data.source, file));
    }
    if (res.data.files.length === 0) {
        const tr = el("tr");
        const td = el("td", "这个范围里没有文件（换个时间范围或来源）", "muted");
        td.colSpan = 5;
        tr.appendChild(td);
        rows.appendChild(tr);
    }
}
function fileRow(sourceId, file) {
    const tr = el("tr");
    tr.appendChild(cell(file.name, "code"));
    tr.appendChild(cell(file.sizeText, "muted"));
    tr.appendChild(cell(file.day, "muted"));
    tr.appendChild(cell(file.modified, "muted"));
    const action = el("td");
    const view = el("button", "查看", "btn secondary small");
    view.addEventListener("click", () => void openTail(sourceId, file.name));
    action.appendChild(view);
    const link = el("a", "下载", "btn secondary small");
    link.href = api.diagDownloadUrl(sourceId, file.name);
    link.style.marginLeft = "6px";
    link.style.textDecoration = "none";
    action.appendChild(link);
    tr.appendChild(action);
    return tr;
}
async function openTail(sourceId, fileName) {
    $("diagTail").textContent = "读取中…";
    const res = await api.diagTail(sourceId, fileName, 200);
    if (res.status !== 200 || !res.data) {
        $("diagTail").textContent = "读取失败：" + (res.message ?? "HTTP " + res.status);
        return;
    }
    $("diagTailTitle").textContent =
        res.data.file + "（尾部 " + res.data.lines + " 行 · " + res.data.sizeText + " · " + res.data.modified + "）";
    $("diagTail").textContent = res.data.content.join("\n");
}
function bundle() {
    if (!overview) {
        notify("先刷新一次来源列表");
        return;
    }
    if (checked.size === 0) {
        notify("至少勾一个来源再打包");
        return;
    }
    const from = $("diagFrom").value || dateText(new Date());
    const to = $("diagTo").value || dateText(new Date());
    const url = api.diagBundleUrl(dateKey(from), dateKey(to), Array.from(checked));
    badge($("diagBadge"), "正在打包…", "warn");
    window.location.href = url;
    window.setTimeout(() => badge($("diagBadge"), "已发起下载（浏览器里看下载项）", "ok"), 1200);
}
