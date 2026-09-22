/**
 * T1：页头状态条 —— 五个常驻胶囊（宿主 / 命令通道 / 相机 / 下游 / 图片盘）+ 实时通道。
 *
 * 为什么要有它：现场打电话问"为什么不出包"，只看实时页的 KPI 是看不出来的 ——
 * 可能是宿主没起、命令通道断了、相机掉线、下游没连、图片盘满了。
 * 这五件事任何一个不满足都出不了包，所以放在页头常驻，5 秒刷一次。
 *
 * 轮询要"安静"，三条规矩：
 *   1) 没登录（或登录框弹着）时**不发请求**，只显示"登录后显示" ——
 *      否则后台轮询拿到 401 会把用户正在输密码的光标抢回用户名框（见 auth.ts 的 showMask）；
 *   2) 单个接口失败只染它自己那一个胶囊，不会把整条状态条拖红；
 *   3) 只在状态真的变化时写 DOM，避免每 5 秒无谓地动一次布局。
 */
import { api } from "./api.js?v=e1f3c4b4";
import { canRead } from "./auth.js?v=e1f3c4b4";
import { $, gb } from "./dom.js?v=e1f3c4b4";
import { renderNavBadges } from "./navbadges.js?v=e1f3c4b4";
/** 五个胶囊的 id（顺序 = 页头从左到右） */
const CAPSULES = [
    { id: "hostState", name: "采集宿主" },
    { id: "sbChannel", name: "命令通道" },
    { id: "sbCameras", name: "相机" },
    { id: "sbDownstream", name: "下游" },
    { id: "sbDisk", name: "图片盘" }
];
const CHANNEL_RESULT = "unknown";
let timer = null;
/** 5 秒轮询一次；失败不弹提示（页头状态本来就该是"安静的"） */
export function initStatusBar() {
    void refreshStatusBar();
    if (timer !== null)
        window.clearInterval(timer);
    timer = window.setInterval(() => void refreshStatusBar(), 5000);
}
/**
 * 取胶囊里的文字节点（没有就建一个）。
 * 胶囊的第一个子元素是彩色小圆点，不能和文字一起清掉 —— 所以文字单独放一个 span 里。
 */
function label(el) {
    const found = el.querySelector(".sbar-text");
    if (found)
        return found;
    for (const node of Array.from(el.childNodes)) {
        if (node.nodeType === Node.TEXT_NODE)
            node.remove();
    }
    const span = document.createElement("span");
    span.className = "sbar-text";
    el.appendChild(span);
    return span;
}
/** 改一个胶囊：颜色跟着 state，文字跟着 text，鼠标悬停提示跟着 title */
function set(id, state, text, title) {
    const el = $(id);
    el.classList.remove("ok", "warn", "bad", "unknown");
    el.classList.add(state);
    const textNode = label(el);
    if (textNode.textContent !== text)
        textNode.textContent = text;
    if (title !== undefined)
        el.title = title;
}
function renderHost(res) {
    if (!res.data) {
        set("hostState", "unknown", "采集宿主：状态未知", res.message ?? "平台接口不可达");
        return CHANNEL_RESULT;
    }
    const status = res.data;
    const title = status.note + (status.message ? "\n" + status.message : "");
    if (status.state === "running") {
        set("hostState", "ok", "采集宿主：运行中", title);
        return status.state;
    }
    if (status.state === "retrying") {
        set("hostState", "warn", "采集宿主：" + status.note, title);
        return status.state;
    }
    set("hostState", "bad", "采集宿主：" + status.note, title);
    return status.state;
}
function renderChannel(res, hostState) {
    if (!res.data) {
        set("sbChannel", "unknown", "命令通道：未知", res.message ?? "平台接口不可达");
        return;
    }
    const channel = res.data;
    const title = (channel.pipeName ? "管道：" + channel.pipeName + "\n" : "") + channel.message;
    if (channel.available) {
        set("sbChannel", "ok", "命令通道：可用", title);
        return;
    }
    // 宿主压根没在跑的时候，命令通道当然是断的 —— 那是宿主那一格的问题，这里不再报一次红
    if (hostState === "running") {
        set("sbChannel", "bad", "命令通道：不可用", title);
        return;
    }
    set("sbChannel", "warn", "命令通道：未就绪", title);
}
function renderCameras(res) {
    if (!res.data) {
        set("sbCameras", "unknown", "相机：未知", res.message ?? "平台接口不可达");
        return;
    }
    const view = res.data;
    if (view.total === 0) {
        set("sbCameras", "unknown", "相机：未配置", "清单里没有启用的相机");
        return;
    }
    const text = "相机：" + view.online + "/" + view.total + " 在线";
    const title = "清单 " + view.total + " 台，在线 " + view.online + " 台，离线 " + view.offline + " 台";
    if (view.online === 0) {
        set("sbCameras", "bad", text, title);
        return;
    }
    if (view.online < view.total) {
        set("sbCameras", "warn", text, title);
        return;
    }
    set("sbCameras", "ok", text, title);
}
function renderDownstream(res) {
    const stats = res.data?.stats ?? null;
    if (!stats) {
        // 下游状态接口受登录保护：没登录时不是"未知"，是"还没权限看"，别用红色吓人
        if (res.status === 401) {
            set("sbDownstream", "unknown", "下游：登录后显示", "下游状态需要登录后才能读取");
            return;
        }
        set("sbDownstream", "unknown", "下游：未知", res.message ?? "平台接口不可达");
        return;
    }
    if (!stats.enabled) {
        set("sbDownstream", "unknown", "下游：未启用", "还没配置下游对接（输出对接页）");
        return;
    }
    // 积压是"能自愈的慢"，不是"断了"，所以并到文字里，不改颜色
    const queued = stats.queueDepth > 0 ? "（积压 " + stats.queueDepth + "）" : "";
    const counters = "已发 " + stats.sent + "，失败 " + stats.failed + "，重传 " + stats.retries;
    if (stats.httpMode) {
        const trouble = stats.failed > 0 || stats.queueDepth > 0;
        set("sbDownstream", trouble ? "warn" : "ok", "下游：HTTP 推送" + queued, "目标：" + (stats.httpTarget ?? "—") + "\n" + counters);
        return;
    }
    if (stats.serverMode) {
        const connected = stats.listening && stats.clientCount > 0;
        set("sbDownstream", connected ? "ok" : "warn", "下游：监听中（" + stats.clientCount + " 连接）" + queued, "监听：" + (stats.listenTarget ?? "—") + "\n" + counters);
        return;
    }
    const title = "目标：" + (stats.target || "—") + "\n" + counters
        + (stats.lastError ? "\n最近错误：" + stats.lastError : "");
    set("sbDownstream", stats.connected ? "ok" : "warn", "下游：" + (stats.connected ? "已连接" : "未连接") + queued, title);
}
function renderDisk(res) {
    if (!res.data || !res.data.diskTotalBytes) {
        set("sbDisk", "unknown", "图片盘：未知", res.message ?? "没有拿到磁盘信息");
        return;
    }
    const stats = res.data;
    const percent = Math.round(stats.diskUsedPercent);
    const title = "占用 " + percent + "%（剩余 " + gb(stats.diskFreeBytes) + " / 共 " + gb(stats.diskTotalBytes) + "）"
        + "\n图片 " + stats.imageFileCount + " 个，" + gb(stats.imageDiskBytes)
        + "\n超过 85% 会造成丢图，请尽快清理或改到别的盘";
    if (percent >= 85) {
        set("sbDisk", "bad", "图片盘：" + percent + "%", title);
        return;
    }
    if (percent >= 70) {
        set("sbDisk", "warn", "图片盘：" + percent + "%", title);
        return;
    }
    set("sbDisk", "ok", "图片盘：" + percent + "%", title);
}
export async function refreshStatusBar() {
    // 没登录就不问：后台请求拿到 401 会弹登录框、抢走正在输密码的光标
    if (!canRead()) {
        for (const capsule of CAPSULES) {
            set(capsule.id, "unknown", capsule.name + "：登录后显示");
        }
        // T0.6：导航角标也跟着清空，别留着上一次的旧数字
        renderNavBadges(null, null);
        return;
    }
    // 这几个接口互不依赖：并发发，最慢的那个决定这一轮耗时
    const [host, channel, devices, downstream, stats, monitor] = await Promise.all([
        api.hostStatus(),
        api.hostChannel(),
        api.devices(),
        api.downstreamQuiet(),
        api.stats(),
        api.monitorSummary()
    ]);
    const hostState = renderHost(host);
    renderChannel(channel, hostState);
    renderCameras(devices);
    renderDownstream(downstream);
    renderDisk(stats);
    // T0.6：侧边栏角标复用这一轮的结果，不再单独轮一次
    renderNavBadges(devices.data, monitor.data);
}
