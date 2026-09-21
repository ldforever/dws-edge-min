async function request(url, options) {
    // 同源 Cookie（登录会话）要带上：不写 credentials 时 fetch 对同源默认就会带，
    // 但这里显式声明，避免以后换成跨源部署时悄悄丢会话。
    const init = { method: options?.method ?? "GET", credentials: "same-origin" };
    if (options?.json !== undefined) {
        init.headers = { "Content-Type": "application/json" };
        init.body = JSON.stringify(options.json);
    }
    let res;
    try {
        res = await fetch(url, init);
    }
    catch (e) {
        // 网络层失败：服务没起来、页面被关闭等
        return { status: 0, data: null, message: describe(e) };
    }
    const text = await res.text();
    let parsed = null;
    if (text) {
        try {
            parsed = JSON.parse(text);
        }
        catch {
            parsed = { raw: text };
        }
    }
    const result = { status: res.status, data: parsed ?? null };
    // B9：会话过期/未登录 —— 交给 auth 模块弹登录框（登录接口自己的 401 不算，那是密码错）
    if (res.status === 401 && !options?.silent401 && url.indexOf("/api/auth/login") !== 0) {
        if (unauthorizedHandler)
            unauthorizedHandler();
    }
    if (!res.ok) {
        const body = parsed;
        result.message = body?.error ?? body?.raw ?? `HTTP ${res.status}`;
    }
    return result;
}
/** B9：收到 401 时通知界面（由 auth.ts 注册，用来弹出登录框） */
let unauthorizedHandler = null;
export function onUnauthorized(handler) {
    unauthorizedHandler = handler;
}
function describe(e) {
    if (e instanceof Error)
        return e.message;
    return String(e);
}
export const api = {
    stats: () => request("/api/stats"),
    parcels: (limit) => request(`/api/parcels?limit=${limit}`),
    cameras: () => request("/api/cameras"),
    /** C2：相机计数（出码数/掉线次数/方位），相机状态墙首屏用 */
    cameraCounters: () => request("/api/cameras/counters"),
    devices: () => request("/api/devices"),
    positions: () => request("/api/camera-positions"),
    savePositions: (positions) => request("/api/camera-positions", { method: "POST", json: { positions } }),
    config: () => request("/api/config"),
    applyConfig: (body) => request("/api/config/apply", { method: "POST", json: body }),
    /** A4：给正在跑的采集宿主发命令（软触发 / 补码），不用停宿主 */
    hostCommand: (body) => request("/api/host/command", { method: "POST", json: body }),
    /** A4：命令通道是否可用（宿主在不在跑） */
    hostChannel: () => request("/api/host/channel"),
    /** 采集宿主状态（运行中 / 正在等相机重试 / 未运行） */
    hostStatus: () => request("/api/host/status", { silent401: true }),
    /** P0：相机连通性预检（ping + 与 SDK 发现结果对照） */
    cameraProbe: (ips) => request("/api/camera-probe", { method: "POST", json: { ips } }),
    // ---- B2 条码过滤规则 ----
    rules: () => request("/api/rules"),
    saveRules: (ruleSet) => request("/api/rules", { method: "POST", json: ruleSet }),
    testRules: (codes, ruleSet) => request("/api/rules/test", {
        method: "POST",
        json: { codes, ruleset: ruleSet }
    }),
    filteredCodes: (limit) => request(`/api/rules/filtered?limit=${limit}`),
    // ---- B1 去重指纹归档 ----
    dedup: () => request("/api/dedup"),
    compactDedup: () => request("/api/dedup/compact", { method: "POST" }),
    // ---- B3 历史查询与导出 ----
    history: (filter) => request("/api/history?" + historyQueryString(filter, true)),
    /** 导出用的 URL（点击后由浏览器直接下载 CSV） */
    historyExportUrl: (filter) => "/api/history/export?" + historyQueryString(filter, false),
    // ---- B4 下游 TCP 输出 ----
    downstream: () => request("/api/downstream"),
    saveDownstream: (options) => request("/api/downstream", { method: "POST", json: options }),
    testDownstream: (options) => request("/api/downstream/test", {
        method: "POST",
        json: options
    }),
    previewDownstream: (template) => request("/api/downstream/preview", { method: "POST", json: { template } }),
    downstreamLog: (limit) => request(`/api/downstream/log?limit=${limit}`),
    /** 图片按需读取接口（只允许图片根目录内的文件） */
    imageUrl: (path) => "/api/images?path=" + encodeURIComponent(path),
    /** B7：缩略图（BMP 真缩小并缓存，JPEG 回退原图） */
    imageThumbUrl: (path, width) => "/api/images/thumb?w=" + width + "&path=" + encodeURIComponent(path),
    imageInfo: (path) => request("/api/images/info?path=" + encodeURIComponent(path)),
    // ---- B8 相机状态监控与告警 ----
    monitorSummary: () => request("/api/monitor/summary"),
    monitorCameras: () => request("/api/monitor/cameras"),
    monitorEvents: (limit, camera) => request("/api/monitor/events?limit=" + limit + (camera ? "&camera=" + encodeURIComponent(camera) : "")),
    monitorAlerts: (limit, activeOnly) => request("/api/monitor/alerts?limit=" + limit + "&activeOnly=" + activeOnly),
    monitorConfig: () => request("/api/monitor/config"),
    saveMonitorConfig: (options) => request("/api/monitor/config", { method: "POST", json: options }),
    // ---- B9 账号与鉴权 ----
    authStatus: () => request("/api/auth/status"),
    login: (username, password) => request("/api/auth/login", { method: "POST", json: { username, password } }),
    logout: () => request("/api/auth/logout", { method: "POST" }),
    me: () => request("/api/auth/me"),
    changePassword: (username, oldPassword, newPassword) => request("/api/auth/password", {
        method: "POST",
        json: { username, oldPassword, newPassword }
    }),
    authUsers: () => request("/api/auth/users"),
    addUser: (username, password, role, note) => request("/api/auth/users", {
        method: "POST",
        json: { username, password, role, note }
    }),
    updateUser: (username, role, enabled, note) => request("/api/auth/users/update", {
        method: "POST",
        json: { username, role, enabled, note }
    }),
    deleteUser: (username) => request("/api/auth/users/delete", { method: "POST", json: { username } }),
    resetPassword: (username, password) => request("/api/auth/users/reset-password", {
        method: "POST",
        json: { username, password }
    }),
    authConfig: () => request("/api/auth/config"),
    saveAuthConfig: (options) => request("/api/auth/config", { method: "POST", json: options }),
    rotateServiceKey: () => request("/api/auth/service-key", { method: "POST" }),
    authEvents: (limit) => request("/api/auth/events?limit=" + limit),
    // ---- C5 配置页（简版）----
    storageConfig: () => request("/api/config/storage"),
    saveStorageConfig: (options) => request("/api/config/storage", { method: "POST", json: options }),
    /** 备份列表（rules / downstream / monitor / auth / gateway.ini / Cfg 的 .bak-* 都在这） */
    backups: () => request("/api/config/backups"),
    rollback: (file) => request("/api/config/rollback", { method: "POST", json: { file } }),
    // ---- C4 统计看板（简版）----
    statsBoard: (from, to, dimension, deviceId) => request("/api/stats/board?from=" + from + "&to=" + to + "&dimension=" + dimension +
        (deviceId ? "&deviceId=" + encodeURIComponent(deviceId) : "")),
    shifts: () => request("/api/stats/shifts"),
    saveShifts: (shifts) => request("/api/stats/shifts", { method: "POST", json: { shifts } }),
    // ---- C6 日志查看与导出 ----
    diagSources: () => request("/api/diag/sources"),
    diagFiles: (source, from, to) => request("/api/diag/files?source=" + encodeURIComponent(source) + "&from=" + from + "&to=" + to),
    diagTail: (source, file, lines) => request("/api/diag/tail?source=" + encodeURIComponent(source) + "&file=" + encodeURIComponent(file) +
        "&lines=" + lines),
    /** 单文件下载地址（浏览器直接下载） */
    diagDownloadUrl: (source, file) => "/api/diag/download?source=" + encodeURIComponent(source) + "&file=" + encodeURIComponent(file),
    /** 一键打包地址（zip） */
    diagBundleUrl: (from, to, sources) => "/api/diag/bundle?from=" + from + "&to=" + to + "&sources=" + encodeURIComponent(sources.join(",")),
    // ---- A8-3 配置模板 ----
    templates: () => request("/api/config/templates"),
    saveTemplate: (name, note) => request("/api/config/templates", {
        method: "POST",
        json: { name, note }
    }),
    templateDiff: (name) => request("/api/config/templates/diff?name=" + encodeURIComponent(name)),
    applyTemplate: (body) => request("/api/config/templates/apply", { method: "POST", json: body }),
    deleteTemplate: (name) => request("/api/config/templates/delete", { method: "POST", json: { name } }),
    templateDownloadUrl: (name) => "/api/config/templates/download?name=" + encodeURIComponent(name)
};
export const STREAM_URL = "/api/stream";
/** 历史查询串：导出时不要 limit/offset（要全量） */
function historyQueryString(filter, paged) {
    const params = new URLSearchParams();
    params.set("from", filter.from);
    params.set("to", filter.to);
    if (filter.code)
        params.set("code", filter.code);
    if (filter.deviceId)
        params.set("deviceId", filter.deviceId);
    if (filter.noread !== "")
        params.set("noread", filter.noread);
    if (filter.dispatchState !== "")
        params.set("dispatchState", filter.dispatchState);
    if (filter.hasImage !== "")
        params.set("hasImage", filter.hasImage);
    if (paged) {
        params.set("limit", String(filter.limit));
        params.set("offset", String(filter.offset));
    }
    return params.toString();
}
