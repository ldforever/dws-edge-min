/**
 * 实时推送（SSE）封装。
 *
 * 平台在连接建立时会先补发：最近 20 条包裹 + 统计 + 当前所有相机状态，
 * 所以这里不用自己再拉一遍初始数据；断线后 2 秒自动重连。
 */
import { STREAM_URL } from "./api.js?v=bee1f896";
const RECONNECT_DELAY_MS = 2000;
export function connectStream(handlers) {
    const es = new EventSource(STREAM_URL);
    es.onopen = () => handlers.onStatus(true);
    es.onerror = () => {
        handlers.onStatus(false);
        es.close();
        window.setTimeout(() => connectStream(handlers), RECONNECT_DELAY_MS);
    };
    es.onmessage = (event) => {
        let msg;
        try {
            msg = JSON.parse(event.data);
        }
        catch {
            return; // 半截消息，忽略
        }
        switch (msg.type) {
            case "parcel":
                handlers.onParcel(msg.data);
                break;
            case "camera":
                handlers.onCamera(msg.data);
                break;
            case "stats":
                handlers.onStats(msg.data);
                break;
        }
    };
}
