/**
 * 实时推送（SSE）封装。
 *
 * 平台在连接建立时会先补发：最近 20 条包裹 + 统计 + 当前所有相机状态，
 * 所以这里不用自己再拉一遍初始数据；断线后 2 秒自动重连。
 */
import { STREAM_URL } from "./api.js";
import type { StreamMessage } from "./types.js";

export interface StreamHandlers {
  onParcel: (data: Extract<StreamMessage, { type: "parcel" }>["data"]) => void;
  onCamera: (data: Extract<StreamMessage, { type: "camera" }>["data"]) => void;
  onStats: (data: Extract<StreamMessage, { type: "stats" }>["data"]) => void;
  /** B8：监控快照（在线率/心跳/活动告警） */
  onMonitor: (data: Extract<StreamMessage, { type: "monitor" }>["data"]) => void;
  /** B8：单条告警产生/恢复，用来即时弹提示 */
  onAlert: (data: Extract<StreamMessage, { type: "alert" }>["data"]) => void;
  /** 连接状态变化（用于右上角小圆点） */
  onStatus: (connected: boolean) => void;
  /** B9：推送断了（可能是会话过期）——让界面顺手刷一次登录态 */
  onError?: () => void;
}

const RECONNECT_DELAY_MS = 2000;

export function connectStream(handlers: StreamHandlers): void {
  const es = new EventSource(STREAM_URL);

  es.onopen = () => handlers.onStatus(true);

  es.onerror = () => {
    handlers.onStatus(false);
    if (handlers.onError) handlers.onError();
    es.close();
    window.setTimeout(() => connectStream(handlers), RECONNECT_DELAY_MS);
  };

  es.onmessage = (event: MessageEvent<string>) => {
    let msg: StreamMessage;
    try {
      msg = JSON.parse(event.data) as StreamMessage;
    } catch {
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
      case "monitor":
        handlers.onMonitor(msg.data);
        break;
      case "alert":
        handlers.onAlert(msg.data);
        break;
    }
  };
}
