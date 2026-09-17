async function request(url, options) {
    const init = { method: options?.method ?? "GET" };
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
    if (!res.ok) {
        const body = parsed;
        result.message = body?.error ?? body?.raw ?? `HTTP ${res.status}`;
    }
    return result;
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
    devices: () => request("/api/devices"),
    positions: () => request("/api/camera-positions"),
    savePositions: (positions) => request("/api/camera-positions", { method: "POST", json: { positions } }),
    config: () => request("/api/config"),
    applyConfig: (body) => request("/api/config/apply", { method: "POST", json: body }),
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
    /** 图片按需读取接口（只允许图片根目录内的文件） */
    imageUrl: (path) => "/api/images?path=" + encodeURIComponent(path)
};
export const STREAM_URL = "/api/stream";
