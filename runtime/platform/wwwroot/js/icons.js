/**
 * T0.6：导航图标（内联 SVG，零依赖）。
 *
 * 为什么不用图标字体 / 图标库 / CDN：
 *   现场是**离线**环境，任何外链都可能在装机那天变成空白方块；
 *   图标库（lucide 之类）要么带几百 KB 的运行时，要么要额外的构建步骤。
 *   一共就十来个图标，把路径内联进来最省事，也最好排查。
 *
 * 用法：在 index.html 里给元素加 `data-icon="activity"`（可再用 data-icon-size 指定像素），
 * 启动时 initIcons() 扫一遍填进去。图标是纯装饰（aria-hidden），
 * 万一 JS 没起来，按钮上的文字仍然在，功能不受影响。
 *
 * 加新图标：往 SHAPES 里加一条即可。拼进去的永远是这里写死的常量，
 * 不要往里塞用户输入（那是 XSS 的经典入口）。
 */
/** 图标名 → SVG 子元素（24×24 视图框、描边风格） */
const SHAPES = {
    // 品牌：扫码框 + 三条码纹
    "scan-barcode": '<path d="M3 7V5a2 2 0 0 1 2-2h2"/><path d="M17 3h2a2 2 0 0 1 2 2v2"/>' +
        '<path d="M21 17v2a2 2 0 0 1-2 2h-2"/><path d="M7 21H5a2 2 0 0 1-2-2v-2"/>' +
        '<path d="M7 8v8"/><path d="M11 8v8"/><path d="M15 8v8"/>',
    // 实时监控：心跳线
    activity: '<polyline points="22 12 18 12 15 21 9 3 6 12 2 12"/>',
    // 设备信息：相机
    camera: '<path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h3l2-3h6l2 3h3a2 2 0 0 1 2 2z"/>' +
        '<circle cx="12" cy="13" r="4"/>',
    // 历史查询：放大镜
    search: '<circle cx="11" cy="11" r="7"/><path d="M20 20l-3.6-3.6"/>',
    // 统计：柱状图
    chart: '<path d="M12 20V10"/><path d="M18 20V4"/><path d="M6 20v-4"/>',
    // 诊断：检查表 + 十字
    diagnosis: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M12 9v6"/><path d="M9 12h6"/>',
    // 相机清单：列表
    list: '<path d="M8 6h13"/><path d="M8 12h13"/><path d="M8 18h13"/>' +
        '<path d="M3 6h.01"/><path d="M3 12h.01"/><path d="M3 18h.01"/>',
    // 输出对接：纸飞机
    send: '<path d="M22 2 11 13"/><path d="M22 2l-7 20-4-9-9-4z"/>',
    // 规则：漏斗
    filter: '<path d="M22 3H2l8 9.46V19l4 2v-8.54z"/>',
    // 系统与安全：盾牌 + 对勾
    shield: '<path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><path d="M9 12l2 2 4-4"/>'
};
const SVG_NS = "http://www.w3.org/2000/svg";
function build(name, size) {
    const shape = SHAPES[name];
    if (!shape)
        return null;
    const markup = '<svg xmlns="' + SVG_NS + '" viewBox="0 0 24 24" width="' + size + '" height="' + size +
        '" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' +
        shape + "</svg>";
    const parsed = new DOMParser().parseFromString(markup, "image/svg+xml");
    return parsed.documentElement;
}
/** 把页面上所有 [data-icon] 填上图标（已经填过就跳过，重复调用安全） */
export function initIcons(root = document) {
    for (const host of Array.from(root.querySelectorAll("[data-icon]"))) {
        if (host.querySelector("svg"))
            continue;
        const size = Number(host.dataset.iconSize ?? 16);
        const node = build(host.dataset.icon ?? "", Number.isFinite(size) && size > 0 ? size : 16);
        if (!node)
            continue;
        node.setAttribute("aria-hidden", "true");
        node.setAttribute("focusable", "false");
        host.appendChild(node);
    }
}
