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
import { api } from "./api.js?v=025e0e75";
import { canRead } from "./auth.js?v=025e0e75";
import { $, gb } from "./dom.js?v=025e0e75";
import { renderNavBadges } from "./navbadges.js?v=025e0e75";
/**
 * 「宿主说了算」的共享口径 —— 判定只写一份，谁要用谁订阅。
 *
 * 为什么要有它：页头状态条、实时页 KPI、相机状态墙都在回答同一个问题 ——
 * **这些相机现在到底能不能用？** 而平台侧那份相机清单（/api/devices、/api/cameras、/api/stats）
 * 是"上一次宿主上报的结果"：宿主没在跑、或 SDK 起不来（3000/3001/2200）时它不会更新，
 * 于是三处都跟着显示"在线"。判定分散写必然判出三个结论，所以放在这里统一。
 *
 * 判定顺序：读不到宿主状态 → 未知；宿主在重试（认得出返回码给红，认不出给黄）；
 * 宿主没在跑（状态文件过期说"过期"，否则说"没在跑"）；只有宿主在跑才认平台那份相机清单。
 */
export const HOST_STATUS_STALE_SECONDS = 90;
let verdict = {
    kind: "unknown",
    label: "待确认",
    note: "还没读到采集宿主状态",
    camerasReliable: false,
    host: null
};
const verdictListeners = new Set();
/** 发布新结论：状态条每 5 秒刷一次就喂一次，订阅方（实时页）跟着重画 */
export function setHostVerdict(next) {
    verdict = next;
    for (const listener of Array.from(verdictListeners)) {
        listener(verdict);
    }
}
export function getHostVerdict() {
    return verdict;
}
export function onHostVerdict(listener) {
    verdictListeners.add(listener);
}
/** SDK 返回码 → 人话（3000 没相机 / 3001 被占用 / 2200 没加密狗） */
export function sdkReason(code) {
    if (code === 3000) {
        return "SDK 返回 3000：没有相机连上（相机没上电 / 网段不对 / 上次会话没释放）";
    }
    if (code === 3001) {
        return "SDK 返回 3001：相机被别的程序占用（另一个宿主 / 大华工具还没关）";
    }
    if (code === 2200) {
        return "SDK 返回 2200：找不到加密狗";
    }
    return null;
}
/** 从宿主状态算出结论（纯函数，方便以后加断言） */
export function verdictFromHost(host) {
    if (!host) {
        return {
            kind: "unknown",
            label: "状态未知",
            note: "读不到采集宿主状态，无法判断相机是不是真的连上了",
            camerasReliable: false,
            host: null
        };
    }
    if (host.state === "retrying") {
        const reason = sdkReason(host.code);
        if (reason) {
            const short = host.code === 3000 ? "SDK 发现不到" : host.code === 3001 ? "相机被占用" : "没有加密狗";
            return { kind: "bad", label: short, note: reason + "\n" + host.note, camerasReliable: false, host };
        }
        return {
            kind: "warn",
            label: "待确认",
            note: "采集宿主正在重试，还没确认相机能不能用\n" + host.note,
            camerasReliable: false,
            host
        };
    }
    if (host.state !== "running") {
        if (host.ageSeconds > HOST_STATUS_STALE_SECONDS) {
            return {
                kind: "unknown",
                label: "状态过期",
                note: "宿主状态文件已经 " + Math.round(host.ageSeconds) + " 秒没更新（超过 " +
                    HOST_STATUS_STALE_SECONDS + " 秒即视为过期）；下面这份清单是上一次会话留下的，不能当作现在的结论",
                camerasReliable: false,
                host
            };
        }
        return { kind: "unknown", label: "宿主没在跑", note: host.note, camerasReliable: false, host };
    }
    return { kind: "ok", label: "在线", note: host.note, camerasReliable: true, host };
}
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
/** 「相机」胶囊：判定口径见文件顶部那份 HostVerdict（三处共用同一份结论） */
function renderCameras(res, host, cameras) {
    const current = verdictFromHost(host);
    setHostVerdict(current); // 实时页的 KPI 与相机状态墙订阅它，保证三处口径一致
    if (!res.data) {
        set("sbCameras", "unknown", "相机：未知", res.message ?? "平台接口不可达");
        return;
    }
    const view = res.data;
    const detail = deviceDetail(view, cameras);
    // 宿主不在跑 / 正在重试：一律不报绿，按 SDK 返回码说具体原因
    if (!current.camerasReliable) {
        set("sbCameras", current.kind === "ok" ? "unknown" : current.kind, "相机：" + current.label, current.note + "\n" + detail);
        return;
    }
    // 宿主在跑：这时平台那份相机清单才是可信的
    if (view.total === 0) {
        set("sbCameras", "unknown", "相机：未配置", "清单里没有启用的相机");
        return;
    }
    const text = "相机：" + view.online + "/" + view.total + " 在线";
    if (view.online === 0) {
        set("sbCameras", "bad", text, detail);
        return;
    }
    if (view.online < view.total) {
        set("sbCameras", "warn", text, detail);
        return;
    }
    set("sbCameras", "ok", text, detail);
}
/** 悬停提示的公共部分：清单台数 / SDK 发现台数 / 最近一次相机数据多久以前 */
function deviceDetail(view, cameras) {
    const lines = [];
    lines.push("清单 " + view.total + " 台（在线 " + view.online + "，离线 " + view.offline + "）" +
        (view.declaredMissing > 0 ? "，其中 " + view.declaredMissing + " 台 SDK 没发现" : ""));
    const age = freshestHeartbeatAge(cameras);
    if (age !== null) {
        lines.push("最近一次相机数据：" + ageText(age) + "（空闲线体上会一直变大，不作为故障判据）");
    }
    return lines.join("\n");
}
/** 所有相机里"最新那次心跳"距今多久（秒）；没有记录返回 null */
function freshestHeartbeatAge(cameras) {
    if (!cameras || cameras.length === 0) {
        return null;
    }
    let best = null;
    for (const camera of cameras) {
        const age = camera.lastHeartbeatAgeSeconds;
        if (typeof age !== "number" || age < 0) {
            continue;
        }
        if (best === null || age < best) {
            best = age;
        }
    }
    return best;
}
function ageText(seconds) {
    if (seconds < 90) {
        return Math.round(seconds) + " 秒前";
    }
    return Math.round(seconds / 60) + " 分钟前";
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
        // 未登录时也把结论置成"未知"，别让实时页拿上一次的"可信"继续显示在线
        setHostVerdict(verdictFromHost(null));
        return;
    }
    // 这几个接口互不依赖：并发发，最慢的那个决定这一轮耗时
    // monitorCameras 只用来给"相机"胶囊补一句"最近一次相机数据多久以前"，判定颜色不靠它
    const [host, channel, devices, downstream, stats, monitor, monitorCameras] = await Promise.all([
        api.hostStatus(),
        api.hostChannel(),
        api.devices(),
        api.downstreamQuiet(),
        api.stats(),
        api.monitorSummary(),
        api.monitorCameras()
    ]);
    const hostState = renderHost(host);
    renderChannel(channel, hostState);
    renderCameras(devices, host.data, monitorCameras.data);
    renderDownstream(downstream);
    renderDisk(stats);
    // T0.6：侧边栏角标复用这一轮的结果，不再单独轮一次
    renderNavBadges(devices.data, monitor.data);
}
