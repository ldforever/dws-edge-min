/**
 * 配置页：读当前 SDK 配置 + 一键应用（写配置 → 重启校验 → 失败自动回滚）。
 *
 * 界面调的就是命令行那条路径（tools\apply-config.ps1），所以不会出现"界面能过、命令行过不了"。
 *
 * C5 在这页上补三块：
 *   * 相机清单表格编辑（生成同样的清单文本，走同一个 apply 接口）；
 *   * 存图策略表单（写 gateway.ini，校验 + 自动备份）；
 *   * 配置备份与回滚（config\ 与 Cfg\ 下的 .bak-* 都能一键还原）。
 */
import { api } from "./api.js?v=ea50ec78";
import { $, badge, cell, clear, el, notify, positionLabel } from "./dom.js?v=ea50ec78";
const TRIGGER_LABEL = {
    hard: "硬触发（光电）",
    soft: "软触发",
    free: "自由拉流（狂扫）"
};
let summary = null;
export function initConfig() {
    $("btnReloadConfig").addEventListener("click", () => void refreshConfig());
    $("btnFillCameras").addEventListener("click", () => fillCamerasFromConfig());
    $("btnApply").addEventListener("click", () => void applyConfig());
    // C5：相机清单表格
    $("btnCamAdd").addEventListener("click", () => {
        $("camEditRows").appendChild(cameraEditRow("ip", "", ""));
        renumberCameraEditor();
    });
    $("btnCamFill").addEventListener("click", () => refreshConfig());
    $("btnCamCheck").addEventListener("click", () => checkCameraEditor(true));
    $("btnCamApply").addEventListener("click", () => void applyCamerasFromEditor());
    // C5：存图策略与备份
    $("btnStorageSave").addEventListener("click", () => void saveStorage());
    $("btnStorageReload").addEventListener("click", () => void refreshStorage());
    $("btnBackupReload").addEventListener("click", () => void refreshBackups());
}
export async function refreshConfig() {
    const res = await api.config();
    if (res.status !== 200 || !res.data)
        return;
    summary = res.data;
    render();
    renderCameraEditor(res.data.cameras ?? []);
    await refreshStorage();
    await refreshBackups();
}
function render() {
    const c = summary;
    if (!c)
        return;
    const kv = $("cfgSummary");
    kv.textContent = "";
    const rows = [
        ["运行目录", c.runtimeRoot],
        ["SDK 配置文件", c.cfgPath + (c.cfgExists ? "" : "（不存在）")],
        ["provider", c.provider ?? "—"],
        ["相机模式", "mode=" + c.mode + " num=" + c.num + " randWorkMode=" + c.randWorkMode],
        ["启用相机", c.enabledCameras + " 台（声明 " + c.declaredCameras + " 条）"],
        ["触发模式", (TRIGGER_LABEL[c.triggerName] ?? c.triggerName ?? "?") + "（triggerMode=" + c.triggerMode + "）"],
        ["方位映射", c.positionsCount + " 条 · " + c.positionsFile],
        ["一键应用脚本", (c.toolsReady ? "可用 " : "未找到 ") + c.applyScript]
    ];
    for (const [key, value] of rows) {
        kv.appendChild(el("div", key, "k"));
        kv.appendChild(el("div", value));
    }
    if (!c.toolsReady) {
        badge(kv, "找不到 apply-config.ps1：先跑一次 build.ps1（会把 tools 拷到 runtime\\tools）", "err");
    }
    if (c.problems?.length) {
        badge(kv, "配置问题：" + c.problems.join("；"), "err");
    }
    if (c.warnings?.length) {
        badge(kv, "提醒：" + c.warnings.join("；"), "warn");
    }
    fillCamerasFromConfig();
    if (c.lastApply) {
        renderApplyResult(c.lastApply, "（上次应用：" + (c.lastApply.at ?? "") + "）\n\n");
    }
}
function fillCamerasFromConfig() {
    if (!summary?.cameras?.length)
        return;
    const textarea = $("cfgCameras");
    textarea.value = summary.cameras.map((x) => x.line).join("\r\n");
}
function renderApplyResult(result, header = "") {
    $("applyOut").textContent =
        header +
            "结论：" + result.conclusion + "\n" +
            "退出码：" + result.exitCode + " · 耗时 " + (result.durationMs / 1000).toFixed(1) + " 秒\n" +
            (result.commandLine ? "命令：" + result.commandLine + "\n" : "") +
            (result.error ? "错误：" + result.error + "\n" : "") +
            "----------------------------------------\n" +
            (result.output || "（没有输出）");
    badge($("applyBadge"), "退出码 " + result.exitCode + " · " + result.conclusion, exitKind(result.exitCode));
}
function exitKind(exitCode) {
    if (exitCode === 0)
        return "ok";
    if (exitCode === 1 || exitCode === 2)
        return "warn";
    return "err";
}
async function applyConfig() {
    const triggerMode = $("cfgTrigger").value;
    const useCameras = $("cfgUseCameras").checked;
    const skipVerify = $("cfgSkipVerify").checked;
    let cameras = null;
    if (useCameras) {
        cameras = $("cfgCameras")
            .value.split(/\r?\n/)
            .map((line) => line.trim())
            .filter((line) => line.length > 0 && !line.startsWith("#"));
        if (!cameras.length) {
            notify("相机清单是空的：请先填写，或取消勾选「应用上面的相机清单」");
            return;
        }
    }
    if (!triggerMode && !cameras) {
        notify("至少要改一项：触发模式 或 相机清单");
        return;
    }
    const body = {
        triggerMode,
        cameras,
        skipVerify,
        stopHost: $("cfgStopHost").checked,
        restartHost: $("cfgRestartHost").checked
    };
    const button = $("btnApply");
    button.disabled = true;
    button.textContent = "应用中…（启动 SDK 校验，最长 4 分钟）";
    $("applyBadge").textContent = "";
    $("applyOut").textContent =
        "正在执行：\n" + JSON.stringify(body, null, 2) + "\n\n（校验会真的启动一次 SDK，请稍等…）";
    try {
        const res = await api.applyConfig(body);
        if (res.data) {
            renderApplyResult(res.data);
        }
        else {
            $("applyOut").textContent = "调用失败：" + (res.message ?? "HTTP " + res.status);
            badge($("applyBadge"), "调用失败", "err");
        }
    }
    finally {
        button.disabled = false;
        button.textContent = "一键应用";
        await refreshConfig();
    }
}
// ================================================================
// C5：相机清单表格编辑
// ================================================================
/** 方位下拉（与 Core\Config\CameraPositions 的取值一致） */
const POSITION_OPTIONS = ["", "top", "bottom", "left", "right", "front", "rear", "line", "spare"];
function renderCameraEditor(list) {
    const rows = $("camEditRows");
    clear(rows);
    for (const cam of list) {
        rows.appendChild(cameraEditRow(cam.kind ?? "ip", cam.value ?? "", cam.position ?? ""));
    }
    if (list.length === 0) {
        rows.appendChild(cameraEditRow("ip", "", ""));
    }
    renumberCameraEditor();
}
function cameraEditRow(kind, value, position) {
    const tr = el("tr");
    tr.appendChild(cell("", "muted cam-no"));
    const kindTd = el("td");
    const kindSelect = el("select", undefined, "pos");
    for (const option of ["ip", "key", "id"]) {
        const opt = el("option", option);
        opt.value = option;
        kindSelect.appendChild(opt);
    }
    kindSelect.value = kind;
    kindTd.appendChild(kindSelect);
    tr.appendChild(kindTd);
    const valueTd = el("td");
    const input = el("input", undefined, "rule-input");
    input.type = "text";
    input.size = 22;
    input.value = value;
    input.placeholder = kind === "ip" ? "172.20.10.11" : (kind === "key" ? "序列号" : "厂商:序列号");
    valueTd.appendChild(input);
    tr.appendChild(valueTd);
    const posTd = el("td");
    const posSelect = el("select", undefined, "pos");
    for (const option of POSITION_OPTIONS) {
        const opt = el("option", option === "" ? "未设置" : positionLabel(option));
        opt.value = option;
        posSelect.appendChild(opt);
    }
    posSelect.value = position;
    posTd.appendChild(posSelect);
    tr.appendChild(posTd);
    const actionTd = el("td");
    const remove = el("button", "删除", "btn secondary small");
    remove.addEventListener("click", () => {
        tr.remove();
        renumberCameraEditor();
    });
    actionTd.appendChild(remove);
    tr.appendChild(actionTd);
    return tr;
}
function renumberCameraEditor() {
    const rows = Array.from($("camEditRows").children);
    rows.forEach((row, index) => {
        const first = row.children[0];
        if (first)
            first.textContent = String(index + 1);
    });
}
/** 从表格读出一行行相机（kind / value / position） */
function cameraEditorRows() {
    const rows = Array.from($("camEditRows").children);
    const list = [];
    for (const row of rows) {
        const selects = row.querySelectorAll("select");
        const input = row.querySelector("input");
        if (selects.length < 2 || !input)
            continue;
        const kind = selects[0].value;
        const position = selects[1].value;
        const value = input.value.trim();
        if (!value)
            continue; // 空行直接忽略（现场常留一行空的）
        list.push({ kind, value, position });
    }
    return list;
}
function cameraLinesFromEditor() {
    return cameraEditorRows().map((row) => row.kind + "=" + row.value + (row.position ? ",pos=" + row.position : ""));
}
/** 前端先校验一遍（后端还会再校验一次，两边口径一致）：空值、重复、IP 形式、台数提示 */
function checkCameraEditor(showResult) {
    const rows = cameraEditorRows();
    const errors = [];
    const notices = [];
    const seen = new Map();
    rows.forEach((row, index) => {
        const no = index + 1;
        if (row.kind === "ip") {
            // 选了 ip 就要求是合法 IPv4：写错网段是现场最常见的"相机连不上"原因，这里直接拦下来
            const parts = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(row.value);
            const valid = parts !== null && [parts[1], parts[2], parts[3], parts[4]].every((p) => Number(p) <= 255);
            if (!valid) {
                errors.push("第 " + no + " 行不是合法的 IP：" + row.value +
                    "（要填主机名或序列号，请把接入方式改成 id 或 key）");
            }
        }
        const key = row.kind + "=" + row.value.toLowerCase();
        const first = seen.get(key);
        if (first !== undefined) {
            errors.push("第 " + no + " 行与第 " + first + " 行重复：" + row.kind + "=" + row.value);
        }
        else {
            seen.set(key, no);
        }
    });
    if (rows.length === 0) {
        errors.push("清单是空的：至少要有一台相机");
    }
    else if (rows.length < 12 || rows.length > 17) {
        notices.push("当前 " + rows.length + " 台（六面扫现场一般是 12-17 台，确认没漏/没多）");
    }
    if (showResult) {
        const box = $("camEditProblems");
        clear(box);
        for (const item of errors) {
            box.appendChild(el("div", "✗ " + item, "err"));
        }
        for (const item of notices) {
            box.appendChild(el("div", "! " + item, "warnText"));
        }
        if (errors.length === 0) {
            box.appendChild(el("div", "✓ 清单校验通过（" + rows.length + " 台）", "okText"));
        }
        $("camEditMsg").textContent = errors.length > 0
            ? "有问题，先改一下再保存"
            : (notices.length > 0 ? "有提醒（不影响保存）" : "校验通过");
    }
    return { errors, notices };
}
/** 表格 → 保存并应用（走的就是 A8 那条 apply → 校验 → 失败回滚） */
async function applyCamerasFromEditor() {
    const check = checkCameraEditor(true);
    if (check.errors.length > 0) {
        notify("相机清单有 " + check.errors.length + " 个问题，先按红字改一下");
        return;
    }
    const cameras = cameraLinesFromEditor();
    const skipVerify = $("cfgSkipVerify").checked;
    const body = {
        triggerMode: "",
        cameras,
        skipVerify,
        stopHost: $("cfgStopHost").checked,
        restartHost: $("cfgRestartHost").checked
    };
    const button = $("btnCamApply");
    button.disabled = true;
    button.textContent = "应用中…";
    $("applyOut").textContent = "正在按表格里的清单应用：\n" + cameras.join("\n") + "\n\n（校验会真的启动一次 SDK，请稍等…）";
    try {
        const res = await api.applyConfig(body);
        if (res.data) {
            renderApplyResult(res.data);
        }
        else {
            $("applyOut").textContent = "调用失败：" + (res.message ?? "HTTP " + res.status);
            badge($("applyBadge"), "调用失败", "err");
        }
    }
    finally {
        button.disabled = false;
        button.textContent = "保存并应用";
        await refreshConfig();
    }
}
// ================================================================
// C5：存图策略
// ================================================================
export async function refreshStorage() {
    const res = await api.storageConfig();
    if (res.status !== 200 || !res.data) {
        $("storageFile").textContent = "读取失败：" + (res.message ?? "HTTP " + res.status);
        return;
    }
    fillStorage(res.data.options);
    $("storageFile").textContent =
        "配置文件：" + res.data.file +
            "　存图开关写进：[" + res.data.storageSection + "]" +
            (res.data.storageSection === res.data.providerSection
                ? ""
                : "（当前 provider 是 [" + res.data.providerSection + "]，它的段里没有存图开关）") +
            "　图片目录：" + res.data.imageRoot;
    $("storageMsg").textContent = res.data.note ?? "";
}
function fillStorage(options) {
    $("stSaveOriginal").checked = options.saveOriginal !== false;
    $("stSaveWaybill").checked = options.saveWaybill !== false;
    $("stSavePerCamera").checked = options.savePerCamera === true;
    $("stAttachAll").checked = options.attachAllCameraCodeInfo === true;
    $("stCleanupOnStart").checked = options.cleanupOnStart !== false;
    $("stRetentionDays").value = String(options.retentionDays ?? 7);
    $("stMaxDiskPercent").value = String(options.maxDiskPercent ?? 85);
    $("stCleanupInterval").value = String(options.cleanupIntervalMinutes ?? 30);
    $("stSpoolRetention").value = String(options.spoolRetentionDays ?? 7);
    $("stImageDir").value = options.imageDir ?? "";
    $("stProviderImageDir").value = options.providerImageDir ?? "";
}
function numOrZero(id) {
    const value = Number.parseInt($(id).value, 10);
    return Number.isFinite(value) ? value : -1; // 非法填法直接给 -1，让后端校验报错
}
function collectStorage() {
    return {
        saveOriginal: $("stSaveOriginal").checked,
        saveWaybill: $("stSaveWaybill").checked,
        savePerCamera: $("stSavePerCamera").checked,
        attachAllCameraCodeInfo: $("stAttachAll").checked,
        providerImageDir: $("stProviderImageDir").value.trim(),
        imageDir: $("stImageDir").value.trim(),
        retentionDays: numOrZero("stRetentionDays"),
        maxDiskPercent: numOrZero("stMaxDiskPercent"),
        cleanupIntervalMinutes: numOrZero("stCleanupInterval"),
        cleanupOnStart: $("stCleanupOnStart").checked,
        spoolRetentionDays: numOrZero("stSpoolRetention")
    };
}
async function saveStorage() {
    const box = $("storageProblems");
    clear(box);
    const res = await api.saveStorageConfig(collectStorage());
    if (res.status === 200 && res.data?.ok) {
        badge($("storageBadge"), "已保存 " + res.data.changedCount + " 项", "ok");
        $("storageMsg").textContent = (res.data.note ?? "") +
            (res.data.backup ? "　备份：" + res.data.backup : "");
        if (res.data.needRestartHost) {
            box.appendChild(el("div", "! 改完要重启采集宿主才生效（宿主只在启动时读 gateway.ini）", "warnText"));
        }
        await refreshStorage();
        await refreshBackups();
        return;
    }
    badge($("storageBadge"), "保存失败", "err");
    box.appendChild(el("div", "✗ " + (res.message ?? "HTTP " + res.status), "err"));
    $("storageMsg").textContent = "参数没通过校验：配置文件没有被改动";
}
// ================================================================
// C5：配置备份与回滚
// ================================================================
async function refreshBackups() {
    const res = await api.backups();
    const rows = $("backupRows");
    clear(rows);
    if (!res.data) {
        $("backupMsg").textContent = "读取失败：" + (res.message ?? "HTTP " + res.status);
        return;
    }
    const list = res.data;
    $("backupMsg").textContent = list.length > 0 ? "共 " + list.length + " 份备份（新的在前）" : "还没有备份：改一次配置就会生成";
    for (const item of list) {
        const tr = el("tr");
        tr.appendChild(cell(item.fileName, "code"));
        tr.appendChild(cell(item.modified, "muted"));
        tr.appendChild(cell(item.sizeText, "muted"));
        tr.appendChild(cell(item.target + (item.targetExists ? "" : "（目标不存在）"), item.targetExists ? "" : "pending"));
        tr.appendChild(cell(item.effect));
        const action = el("td");
        const button = el("button", "回滚", "btn secondary small");
        button.addEventListener("click", () => {
            if (window.confirm("确定用 " + item.fileName + " 覆盖 " + item.target + " 吗？当前内容会先另存一份。")) {
                void rollbackBackup(item.fileName);
            }
        });
        action.appendChild(button);
        tr.appendChild(action);
        rows.appendChild(tr);
    }
}
async function rollbackBackup(fileName) {
    const res = await api.rollback(fileName);
    if (res.status === 200 && res.data?.ok) {
        badge($("backupMsg"), "已回滚 " + res.data.target, "ok");
        notify("已回滚 " + res.data.target + "\n\n" + (res.data.effect ?? "") +
            (res.data.backupOfCurrent ? "\n回滚前的内容另存为：" + res.data.backupOfCurrent : ""));
        await refreshConfig();
        return;
    }
    badge($("backupMsg"), "回滚失败", "err");
    notify(res.message ?? "回滚失败");
}
