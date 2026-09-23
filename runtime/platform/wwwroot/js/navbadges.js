/**
 * T0.6：侧边栏导航项上的数字角标。
 *
 * 挂什么、为什么：
 *   * 「设备信息」挂**未处理告警数** —— 这是"有事要处理"的入口，红了就该点进去看；
 *   * 「相机」挂**清单台数** —— 现场最常问"我这套到底接了几台"，一眼可见。
 *
 * 数据都来自现有接口（`/api/monitor/summary` 与 `/api/devices`），不新增接口。
 * 这里只负责"把数字画上去"，取数由 statusbar.ts 那一轮轮询一起带回来 ——
 * 两个模块各轮一次的话，同一批接口每 5 秒会被打两遍。
 *
 * 数字为 0 就留空：`.navbadge:empty { display: none }`（CSS 里），
 * 所以"没事"的时候界面上不会挂一个 0，看着像故障。
 */
import { $ } from "./dom.js?v=802163b8";
const ALERT_LIMIT = 99;
function set(node, count, kind, title) {
    const shown = count > ALERT_LIMIT ? ALERT_LIMIT + "+" : count > 0 ? String(count) : "";
    if (node.textContent !== shown)
        node.textContent = shown;
    const className = "navbadge " + kind;
    if (node.className !== className)
        node.className = className;
    node.title = count > 0 ? title + "：" + count : "";
}
/** 画角标；传 null 表示"这次没拿到数据"（未登录 / 接口挂了），角标清空 */
export function renderNavBadges(devices, monitor) {
    const alerts = monitor && monitor.enabled ? monitor.activeAlerts : 0;
    set($("badgeDevices"), alerts, "alert", "未处理告警");
    set($("badgeCameras"), devices ? devices.total : 0, "info", "相机清单台数");
}
