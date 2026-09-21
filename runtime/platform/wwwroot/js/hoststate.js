/**
 * P0：采集宿主状态（页头常驻）。
 *
 * 为什么要有它：宿主以前"SDK 起不来就退出"，现场只看到相机离线、不知道是软件没起、还是没接相机。
 * 现在宿主会在启动失败时按配置重试，并把状态写进 logs\host-status.json；平台把它整理成
 * "运行中 / 正在等相机（第 N 次重试，M 秒后再试）/ 未运行"，这里每 5 秒刷一次显示在页头。
 */
import { api } from "./api.js?v=7e55d4ad";
import { $ } from "./dom.js?v=7e55d4ad";
let timer = null;
/** 5 秒轮询一次；失败也不弹提示（页头状态本来就该是"安静的"） */
export function initHostState() {
    void refreshHostState();
    if (timer !== null)
        window.clearInterval(timer);
    timer = window.setInterval(() => void refreshHostState(), 5000);
}
export async function refreshHostState() {
    const el = $("hostState");
    // 登录框弹着的时候不轮询、也不更新状态：那时页面还没进入工作状态，
    // 后台请求还可能把用户的输入打断（焦点/提示）。
    const mask = $("loginMask");
    if (mask && !mask.classList.contains("hidden")) {
        el.className = "hoststate unknown";
        el.textContent = "采集宿主：登录后显示";
        return;
    }
    const res = await api.hostStatus();
    if (!res.data) {
        el.className = "hoststate unknown";
        el.textContent = "采集宿主：状态未知";
        el.title = res.message ?? "平台接口不可达";
        return;
    }
    const status = res.data;
    el.title = status.note + (status.message ? "\n" + status.message : "");
    if (status.state === "running") {
        el.className = "hoststate ok";
        el.textContent = "采集宿主：运行中";
        return;
    }
    if (status.state === "retrying") {
        el.className = "hoststate warn";
        el.textContent = "采集宿主：" + status.note;
        return;
    }
    el.className = "hoststate bad";
    el.textContent = "采集宿主：" + status.note;
}
