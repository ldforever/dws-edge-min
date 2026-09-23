/**
 * 实时页「最新过包」：**上面一张大图 + 下面一个限高可滚动的过包列表**。
 *
 * 为什么不再用"卡片墙 / 表格"切换：
 *   现场要的是"图看实物、列表看清单"**同时**在视野里；切换按钮意味着一次只能看一半，
 *   而且卡片墙那张缩略图太小，看不清面单、也看不清解码框。
 *
 * 绿框怎么来的：采集宿主从相机 SDK 取回每个条码的点坐标，换算成 0~1 的归一化值随事件带出来
 *   （见 DwsEdge.Core.Model.ImageBox）。这里按显示尺寸换算成 SVG 折线画在图上 ——
 *   所以图怎么缩放框都不会画偏，缩略图/原图/不同分辨率都是同一套坐标。
 *   读到码才有框；无码（noread）照样显示拍到的图，只是没有框。
 *
 * 跟随策略（现场最在意的一条）：
 *   默认"跟着最新"：新包裹一来，大图和列表都跳到它；
 *   一旦有人在列表里点了一行、或者把列表往下滚，就**暂停跟随**（这时候再自动跳会把手里的东西翻掉），
 *   点「回到最新」恢复。
 */
import { api } from "./api.js?v=025e0e75";
import { $, clear, el, positionLabel } from "./dom.js?v=025e0e75";
/** 列表最多留多少行（再多就把最旧的从 DOM 里摘掉；平台侧保留的是最近若干条） */
const MAX_ROWS = 200;
/** 列表滚动位置在这个值以内，就算"在顶部"，自动恢复跟随 */
const FOLLOW_TOP = 6;
const SVG_NS = "http://www.w3.org/2000/svg";
let listEl;
let imageEl;
let boxLayerEl;
let emptyEl;
let infoEl;
let followBtn;
/** 当前列表里有哪些行（key → 行元素），以及每条记录的原始数据（大图要用） */
const rows = new Map();
const records = new Map();
/** 是否跟着最新（false = 用户正在看某一条或翻列表） */
let following = true;
function keyOf(p) {
    return p.traceId ?? (p.time ?? "") + "|" + (p.codes ?? []).join(",");
}
export function initParcelView() {
    listEl = $("pvList");
    imageEl = $("pvImage");
    boxLayerEl = $("pvBoxLayer");
    emptyEl = $("pvEmpty");
    infoEl = $("pvInfo");
    followBtn = $("pvFollow");
    followBtn.addEventListener("click", () => {
        following = true;
        updateFollowButton();
        listEl.scrollTop = 0;
        const newest = listEl.firstElementChild;
        if (newest?.dataset.key)
            select(newest.dataset.key);
    });
    // 滚到下面 → 暂停跟随；滚回顶部 → 自动恢复跟随
    listEl.addEventListener("scroll", () => {
        if (listEl.scrollTop <= FOLLOW_TOP) {
            if (!following) {
                following = true;
                updateFollowButton();
                const newest = listEl.firstElementChild;
                if (newest?.dataset.key)
                    select(newest.dataset.key);
            }
            return;
        }
        if (following) {
            following = false;
            updateFollowButton();
        }
    });
    emptyEl.style.display = "";
}
/** 新包裹 / 同一条的更新：都走这里 */
export function upsertParcel(p, flash) {
    const key = keyOf(p);
    records.set(key, p);
    const old = rows.get(key);
    if (old)
        old.remove();
    const row = buildRow(p, key);
    if (flash)
        row.className = "plistrow new";
    listEl.insertBefore(row, listEl.firstElementChild);
    rows.set(key, row);
    emptyEl.style.display = "none";
    while (listEl.childElementCount > MAX_ROWS) {
        const last = listEl.lastElementChild;
        if (!last)
            break;
        const lastKey = last.dataset.key;
        last.remove();
        if (lastKey) {
            rows.delete(lastKey);
            records.delete(lastKey);
        }
    }
    if (following) {
        listEl.scrollTop = 0;
        select(key);
    }
}
/** 首屏：把平台上已有的包裹灌进列表（从最旧到最新，最新的排最上面） */
export function loadParcels(list) {
    for (let i = list.length - 1; i >= 0; i--) {
        const p = list[i];
        if (p)
            upsertParcel(p, false);
    }
    following = true;
    updateFollowButton();
}
/** 选中一条：列表高亮 + 大图切过去 */
function select(key) {
    for (const [rowKey, row] of rows) {
        row.classList.toggle("sel", rowKey === key);
    }
    renderViewer(records.get(key) ?? null);
}
function updateFollowButton() {
    followBtn.style.display = following ? "none" : "";
    followBtn.textContent = following ? "回到最新" : "回到最新（已暂停跟随）";
}
function buildRow(p, key) {
    const row = el("div", undefined, "plistrow");
    row.dataset.key = key;
    row.appendChild(el("span", p.time ?? "—", "cell-time"));
    const code = codeText(p);
    row.appendChild(el("span", code, "cell-code" + (code === "NOREAD" ? " noread" : "")));
    row.appendChild(el("span", p.deviceId ?? "—", "cell-dev"));
    row.appendChild(el("span", positionText(p), "cell-pos"));
    row.appendChild(el("span", weightText(p), "cell-weight"));
    row.appendChild(el("span", stateText(p), "cell-state"));
    row.addEventListener("click", () => {
        following = false;
        updateFollowButton();
        select(key);
    });
    return row;
}
function codeText(p) {
    const codes = p.codes ?? [];
    return codes.length > 0 ? codes.join(" ") : "NOREAD";
}
function positionText(p) {
    const first = (p.codeDetails ?? [])[0];
    return first?.position ? positionLabel(first.position) : "—";
}
function weightText(p) {
    const grams = p.weightGrams ?? 0;
    return grams > 0 ? (grams / 1000).toFixed(2) + " kg" : "—";
}
function stateText(p) {
    if ((p.codeCount ?? 0) === 0)
        return "无码";
    if (p.complete === false)
        return "待补全";
    if ((p.updates ?? 0) > 1)
        return "已读码 ×" + p.updates;
    return "已读码";
}
/** 画大图 + 绿框 + 单号 */
function renderViewer(p) {
    boxLayerEl.textContent = "";
    if (!p) {
        imageEl.removeAttribute("src");
        imageEl.style.display = "none";
        emptyEl.style.display = "";
        infoEl.textContent = "—";
        return;
    }
    clear(infoEl);
    infoEl.appendChild(el("span", p.time ?? "—", "vinfo-time"));
    infoEl.appendChild(el("span", codeText(p), "vinfo-code" + ((p.codeCount ?? 0) === 0 ? " noread" : "")));
    infoEl.appendChild(el("span", p.deviceId ?? "—", "vinfo-dev"));
    infoEl.appendChild(el("span", positionText(p), "vinfo-pos"));
    infoEl.appendChild(el("span", weightText(p), "vinfo-weight"));
    if ((p.lengthMm ?? 0) > 0) {
        infoEl.appendChild(el("span", volumeText(p), "vinfo-vol"));
    }
    infoEl.appendChild(el("span", stateText(p), "vinfo-state"));
    const path = p.firstImagePath;
    if (!path) {
        imageEl.removeAttribute("src");
        imageEl.style.display = "none";
        // 有包裹但没图（例如只上报了条码）：说清楚，别让人以为界面坏了
        emptyEl.textContent = "这一包没有图片（只上报了条码）";
        emptyEl.style.display = "";
        return;
    }
    imageEl.style.display = "";
    emptyEl.style.display = "none";
    const url = api.imageUrl(path);
    if (imageEl.getAttribute("src") !== url)
        imageEl.src = url;
    drawBoxes(p);
}
function volumeText(p) {
    const l = Math.round((p.lengthMm ?? 0) / 10);
    const w = Math.round((p.widthMm ?? 0) / 10);
    const h = Math.round((p.heightMm ?? 0) / 10);
    return l + "×" + w + "×" + h + " cm";
}
/**
 * 把归一化坐标画成 SVG 折线。
 * viewBox 固定成 0~1、preserveAspectRatio=none，于是"坐标 = 百分比"，缩放不用管；
 * 线宽用 vector-effect=non-scaling-stroke 保住视觉粗细，不然会被非等比缩放拉扁。
 */
function drawBoxes(p) {
    const boxes = p.imageBoxes ?? [];
    if (boxes.length === 0) {
        return;
    }
    const svg = document.createElementNS(SVG_NS, "svg");
    svg.setAttribute("viewBox", "0 0 1 1");
    svg.setAttribute("preserveAspectRatio", "none");
    svg.setAttribute("class", "boxsvg");
    for (const box of boxes) {
        const points = (box.points ?? []).filter((pt) => Array.isArray(pt) && pt.length >= 2);
        if (points.length < 2)
            continue;
        const line = document.createElementNS(SVG_NS, "polyline");
        line.setAttribute("points", points.map((pt) => pt[0] + "," + pt[1]).join(" "));
        line.setAttribute("class", "boxline");
        line.setAttribute("vector-effect", "non-scaling-stroke");
        svg.appendChild(line);
        if (box.code) {
            let minX = 1;
            let minY = 1;
            for (const pt of points) {
                if (typeof pt[0] === "number" && pt[0] < minX)
                    minX = pt[0];
                if (typeof pt[1] === "number" && pt[1] < minY)
                    minY = pt[1];
            }
            const label = el("span", box.code, "boxlabel");
            label.style.left = Math.max(0, minX) * 100 + "%";
            label.style.top = Math.max(0, minY) * 100 + "%";
            boxLayerEl.appendChild(label);
        }
    }
    boxLayerEl.appendChild(svg);
}
