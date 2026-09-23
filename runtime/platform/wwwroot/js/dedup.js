/**
 * 去重指纹归档（B1）的状态展示 + 手动整理。
 *
 * 现场关心两件事：
 *   * 现在保护着多少个包裹（= 重复上报还会不会被丢掉）；
 *   * 磁盘上有没有堆积（WAL 行数、索引大小、上次整理时间）。
 */
import { api } from "./api.js?v=802163b8";
import { $, badge, el } from "./dom.js?v=802163b8";
export function initDedup() {
    $("btnCompactDedup").addEventListener("click", () => void compact());
    $("btnRefreshDedup").addEventListener("click", () => void refreshDedup());
}
export async function refreshDedup() {
    const res = await api.dedup();
    if (res.status !== 200 || !res.data) {
        $("dedupSummary").textContent = "读取失败：" + (res.message ?? "HTTP " + res.status);
        return;
    }
    render(res.data);
}
function kb(bytes) {
    return bytes < 1024 ? bytes + " B" : (bytes / 1024).toFixed(1) + " KB";
}
function render(stats) {
    const kv = $("dedupSummary");
    kv.textContent = "";
    const rows = [
        ["归档目录", stats.directory],
        ["保护的包裹（traceId）", stats.indexEntries + " 个 · " + stats.indexKeys + " 条指纹"],
        ["索引大小", kb(stats.indexBytes)],
        ["未合并的 WAL", stats.walLines + " 行 / " + kb(stats.walBytes)],
        ["保留期", stats.retentionDays + " 天（更久没出现的记录会被清掉）"],
        ["自动整理", "每 " + stats.compactEveryMinutes + " 分钟检查一次，WAL 超过 " + stats.compactWhenWalLines + " 行就整理"],
        ["上次整理", stats.lastCompactAt ?? "（还没整理过）"],
        ["累计整理 / 清理过期", stats.compactions + " 次 / " + stats.droppedExpired + " 条"],
        ["导入的旧格式", stats.importedLegacy + " 条"]
    ];
    for (const [key, value] of rows) {
        kv.appendChild(el("div", key, "k"));
        kv.appendChild(el("div", value));
    }
}
async function compact() {
    $("dedupMsg").textContent = "整理中…";
    const res = await api.compactDedup();
    if (res.status === 200 && res.data) {
        render(res.data);
        $("dedupMsg").textContent =
            "整理完成：WAL " + res.data.walLines + " 行，索引 " + res.data.indexEntries + " 个包裹";
        badge($("dedupBadge"), "已整理", "ok");
    }
    else {
        $("dedupMsg").textContent = "整理失败：" + (res.message ?? "HTTP " + res.status);
        badge($("dedupBadge"), "整理失败", "err");
    }
}
