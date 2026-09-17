using System;
using System.Collections.Generic;
using System.Globalization;

namespace DwsEdge.Core.Model
{
    /// <summary>
    /// 按相机累计"掉线次数 / 恢复次数 / 离线时长"。
    ///
    /// 各 provider 共用：把状态事件交给 Apply()，它会回填统计字段。
    /// 计数保存在内存里（进程重启从 0 开始）；业务平台侧取历史最大值，
    /// 所以宿主重启不会让长期统计回落。
    /// </summary>
    public sealed class CameraRuntimeTracker
    {
        private sealed class State
        {
            public bool HasState;
            public bool Online;
            public int OfflineCount;
            public int ReconnectCount;
            public long LastOfflineAtMs;
            public long LastOfflineDurationMs;
            public long FirstSeenAtMs;
        }

        private readonly Dictionary<string, State> _states =
            new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();

        /// <summary>可选日志回调：参数为（消息, 是否警告）。</summary>
        public Action<string, bool> OnLog { get; set; }

        /// <summary>处理一条相机状态事件，并把统计写回事件的字段。</summary>
        public void Apply(CameraStatusEvent evt, bool isSnapshot)
        {
            if (evt == null || string.IsNullOrEmpty(evt.DeviceId))
            {
                return;
            }

            lock (_sync)
            {
                State state;
                if (!_states.TryGetValue(evt.DeviceId, out state))
                {
                    state = new State();
                    state.FirstSeenAtMs = evt.AtMs;
                    _states[evt.DeviceId] = state;
                }

                if (isSnapshot)
                {
                    // 启动快照只建立基线
                    state.Online = evt.Online;
                    state.HasState = true;
                }
                else if (evt.Online)
                {
                    if (state.HasState && !state.Online)
                    {
                        state.ReconnectCount++;
                        if (state.LastOfflineAtMs > 0)
                        {
                            state.LastOfflineDurationMs = Math.Max(0, evt.AtMs - state.LastOfflineAtMs);
                        }
                        Raise("相机 " + evt.DeviceId + " 已恢复（第 " + state.ReconnectCount + " 次恢复，本次离线 "
                            + (state.LastOfflineDurationMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " 秒）", false);
                    }
                    state.Online = true;
                    state.HasState = true;
                }
                else
                {
                    state.OfflineCount++;
                    state.LastOfflineAtMs = evt.AtMs;
                    state.Online = false;
                    state.HasState = true;
                    Raise("相机 " + evt.DeviceId + " 第 " + state.OfflineCount + " 次掉线", true);
                }

                evt.OfflineCount = state.OfflineCount;
                evt.ReconnectCount = state.ReconnectCount;
                evt.LastOfflineAtMs = state.LastOfflineAtMs;
                evt.LastOfflineDurationMs = state.LastOfflineDurationMs;
                evt.FirstSeenAtMs = state.FirstSeenAtMs;
            }
        }

        private void Raise(string message, bool warning)
        {
            Action<string, bool> handler = OnLog;
            if (handler != null)
            {
                handler(message, warning);
            }
        }
    }
}
