import { inferKind, toast } from "./ui.js?v=91099a1e";
/** 取元素（找不到直接抛，避免后面出现"神秘的空指针"） */
export function $(id) {
    const el = document.getElementById(id);
    if (!el) {
        throw new Error("页面缺少元素：#" + id);
    }
    return el;
}
/** 创建元素并设置文本/类名 */
export function el(tag, text, className) {
    const node = document.createElement(tag);
    if (text !== undefined)
        node.textContent = text;
    if (className)
        node.className = className;
    return node;
}
/** 造一个表格单元格 */
export function cell(text, className) {
    return el("td", text === undefined || text === null ? "" : String(text), className);
}
/** 清空一个容器 */
export function clear(node) {
    node.textContent = "";
}
/** 把一个数值单元格按"有没有值"决定显示 —  */
export function dash(value) {
    return value === undefined || value === null || value === "" ? "—" : String(value);
}
const POSITION_LABEL = {
    top: "顶面",
    bottom: "底面",
    left: "左侧",
    right: "右侧",
    front: "前侧",
    rear: "后侧",
    line: "线体",
    spare: "备用"
};
/** 方位代码 → 中文名（后端 CameraPositions.Label 的前端对应版本） */
export function positionLabel(position) {
    if (!position)
        return "未设置";
    return POSITION_LABEL[position] ?? position;
}
/** 六面视图用得到的固定顺序 */
export const FACE_ORDER = ["top", "bottom", "left", "right", "front", "rear"];
/** 字节 → GB 文本 */
export function gb(bytes) {
    return (Number(bytes ?? 0) / 1024 / 1024 / 1024).toFixed(1) + " GB";
}
/** 在指定容器里放一个彩色徽标，并返回它（用于"退出码 + 结论"这类提示） */
export function badge(container, text, kind) {
    clear(container);
    container.appendChild(el("span", text, "badge " + kind));
}
/** 下载文本文件（CSV 导出用；BOM 让 Excel 正确识别 UTF-8） */
export function downloadText(fileName, content, mime) {
    const blob = new Blob(["\ufeff" + content], { type: mime + ";charset=utf-8" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = fileName;
    a.click();
    URL.revokeObjectURL(a.href);
}
/** CSV 转义 */
export function csvCell(value) {
    return '"' + String(value ?? "").replace(/"/g, '""') + '"';
}
/**
 * 页面提示：右下角自绘 toast（C7，实现在 ui.ts）。
 *
 * 以前是 window.alert —— 它会把整个界面冻住（WebView2 壳里连 SSE 都跟着卡），
 * 而且扫一个包弹一次窗根本没法用。现在改成不打断操作的右下角提示：
 *   * 单参数的老调用（全项目二十多处）一个字都不用改：语义由 inferKind() 猜；
 *     ok 绿 3 秒、warn 黄 6 秒、err 红常驻（必须手动关）；
 *   * 遇到猜不准的文案，调用方可以显式传第二个参数：notify(msg, "err")。
 */
export function notify(message, kind) {
    toast(message, kind ?? inferKind(message));
}
/** 打开图片：浏览器里新开窗口，壳（WebView2）里由壳接管 NewWindowRequested */
export function openImage(url) {
    window.open(url, "_blank");
}
/**
 * B7：图片单元格 —— 直接显示缩略图（按需加载），点击看原图。
 * 缩略图由平台生成并缓存，列表里不会去拉几十张原图。
 */
export function imageCell(path, imageCount, thumbWidth = 160) {
    const td = el("td");
    if (!path) {
        td.className = "muted";
        td.textContent = "—";
        return td;
    }
    const link = el("a", undefined, "img");
    link.href = "/api/images?path=" + encodeURIComponent(path);
    link.target = "_blank";
    link.title = "点击查看原图：" + path;
    const img = el("img", undefined, "thumb");
    img.src = "/api/images/thumb?w=" + thumbWidth + "&path=" + encodeURIComponent(path);
    img.loading = "lazy";
    img.alt = "包裹图片";
    link.appendChild(img);
    td.appendChild(link);
    if (imageCount > 1) {
        td.appendChild(el("span", "×" + imageCount, "tag"));
    }
    return td;
}
