/**
 * B9：账号与鉴权。
 *
 * 界面上分四块：
 *   * 登录遮罩 —— 未登录时挡住管理操作（读接口没开保护时可以直接"先不登录"看数据）；
 *   * 顶栏用户区 —— 显示当前账号与角色，带退出；
 *   * 配置页"账号与安全" —— 改自己的密码、管理员改策略 / 管账号 / 看登录记录 / 轮换服务令牌；
 *   * 401 统一处理 —— 任何接口回 401（会话过期、被别人踢下线）都自动弹回登录框。
 */
import { api, onUnauthorized } from "./api.js?v=38da8d49";
import { $, badge, cell, clear, el, notify } from "./dom.js?v=38da8d49";
import { confirmBox } from "./ui.js?v=38da8d49";
const ROLE_LABEL = {
    admin: "管理员",
    operator: "操作员",
    viewer: "只读"
};
const EVENT_LABEL = {
    "login-ok": "登录成功",
    "login-fail": "密码错误",
    "login-locked": "账号锁定",
    logout: "退出 / 会话结束",
    "password-change": "修改密码",
    "user-add": "新增账号",
    "user-update": "账号变更",
    "user-delete": "删除账号",
    "config-change": "策略变更"
};
let status = null;
/** "先不登录"跳过遮罩（只在本页面生命周期内有效） */
let skipped = false;
let isAdmin = false;
export function initAuth() {
    onUnauthorized(() => {
        // 任何管理接口返回 401：会话没了，立刻弹回登录框
        void refreshAuth(true);
    });
    $("btnLogin").addEventListener("click", () => void doLogin());
    $("btnSkipLogin").addEventListener("click", () => {
        skipped = true;
        hideMask();
    });
    $("btnLogout").addEventListener("click", () => void doLogout());
    for (const id of ["loginUser", "loginPass"]) {
        $(id).addEventListener("keydown", (event) => {
            if (event.key === "Enter")
                void doLogin();
        });
    }
    $("btnChangePwd").addEventListener("click", () => void doChangePassword());
    $("btnAuthReload").addEventListener("click", () => void refreshAuthPanel());
    $("btnAuthSave").addEventListener("click", () => void saveAuthConfig());
    $("btnRotateKey").addEventListener("click", () => void rotateServiceKey());
    $("btnAddUser").addEventListener("click", () => void addUser());
    $("btnLoadAuthEvents").addEventListener("click", () => void loadEvents());
}
/** 页面打开 / 会话变化时问一次平台：要不要登录、我是谁 */
export async function refreshAuth(showMessage = false) {
    const res = await api.authStatus();
    if (!res.data) {
        $("loginSub").textContent = "平台不可达：" + (res.message ?? "网络错误");
        showMask();
        return;
    }
    status = res.data;
    renderHeader();
    if (!status.enabled) {
        skipped = true;
        hideMask();
    }
    else if (status.authenticated) {
        skipped = false;
        hideMask();
    }
    else if (!skipped) {
        showMask();
    }
    if (showMessage && status.enabled && !status.authenticated) {
        $("loginMsg").textContent = "登录已过期，请重新登录";
    }
}
/**
 * T1：现在能不能读管理接口的数据？
 *   已登录 / 根本没开鉴权 / 只读接口没受保护 —— 三种情况都算"能读"。
 *   页头状态条用它来决定"要不要发这一轮轮询"：还没问过平台（status 为空）时先别发，
 *   否则那一轮必然拿 401，而 401 会弹登录框、把用户正在输密码的光标抢回用户名框。
 */
export function canRead() {
    if (!status)
        return false;
    if (!status.enabled)
        return true;
    if (status.authenticated)
        return true;
    return !status.protectRead;
}
function renderHeader() {
    const box = $("userBox");
    const logout = $("btnLogout");
    const skip = $("btnSkipLogin");
    isAdmin = (status?.role ?? "") === "admin";
    if (!status?.enabled) {
        box.textContent = "未启用鉴权";
        logout.style.display = "none";
    }
    else if (status.authenticated) {
        const who = status.username ?? "";
        const role = ROLE_LABEL[status.role ?? ""] ?? status.role ?? "";
        box.textContent = who + "（" + role + (status.viaServiceKey ? " · 服务令牌" : "") + "）";
        logout.style.display = "";
    }
    else {
        box.textContent = "未登录";
        logout.style.display = "none";
    }
    // 读接口没开保护时，允许"只读浏览"：现场大屏不需要账号
    skip.style.display = status?.enabled && !status.authenticated && !status.protectRead ? "" : "none";
    $("loginHint").textContent = status?.initialPasswordPending
        ? "初始密码文件还在：" + (status.initialPasswordFile ?? "") + "（登录后请尽快改密并删除它）"
        : "";
}
function showMask() {
    const mask = $("loginMask");
    // 只在"本来藏着的"时候抢焦点：否则任何后台请求拿到 401 都会把正在输密码的光标拽回用户名框
    const wasHidden = mask.classList.contains("hidden");
    mask.classList.remove("hidden");
    if (wasHidden) {
        $("loginUser").focus?.();
    }
}
function hideMask() {
    $("loginMask").classList.add("hidden");
}
async function doLogin() {
    const username = $("loginUser").value.trim();
    const password = $("loginPass").value;
    if (!username || !password) {
        $("loginMsg").textContent = "请输入用户名和密码";
        return;
    }
    const button = $("btnLogin");
    button.disabled = true;
    $("loginMsg").textContent = "登录中…";
    const res = await api.login(username, password);
    button.disabled = false;
    if (res.status === 200 && res.data?.ok) {
        $("loginMsg").textContent = "";
        $("loginPass").value = "";
        await refreshAuth();
        await refreshAuthPanel();
        if (res.data.mustChangePassword) {
            // P0：配置页拆成四个页签后，账号面板在【系统与安全】里；顺手把用户带过去，免得找不着
            notify("登录成功。这是初始密码/被重置的密码，请到【系统与安全 → 账号与安全 → 改密码】里尽快修改。");
            $("tab-system").click();
        }
        return;
    }
    // 失败：把"还能试几次"直接显示出来
    const remain = res.status === 423 ? "（账号已锁定）" : "";
    $("loginMsg").textContent = (res.message ?? "登录失败") + remain;
}
async function doLogout() {
    await api.logout();
    skipped = false;
    await refreshAuth();
    await refreshAuthPanel();
}
// ---------------------------------------------------------------- 账号与安全面板
export async function refreshAuthPanel() {
    await refreshAuth();
    const camsPanel = $("authLoggedIn");
    const outPanel = $("authLoggedOut");
    if (!status?.enabled) {
        $("authSummary").textContent = "平台未启用鉴权（auth.json 里 enabled=false）：所有接口都不需要登录。";
        outPanel.style.display = "none";
        camsPanel.style.display = "";
        $("authAdminOnly").style.display = "none";
        return;
    }
    if (!status.authenticated) {
        $("authSummary").textContent =
            "未登录。管理接口需要登录后才能调用" + (status.protectRead ? "（读接口也已开启保护）" : "（读接口仍可直接访问）") + "。";
        outPanel.style.display = "";
        camsPanel.style.display = "none";
        return;
    }
    $("authSummary").textContent =
        "已登录：" + (status.username ?? "") + "（" + (ROLE_LABEL[status.role ?? ""] ?? status.role) + "）" +
            "　在线会话 " + status.activeSessions + " 个" +
            (status.viaServiceKey ? "　⚠ 当前用的是服务令牌" : "");
    outPanel.style.display = "none";
    camsPanel.style.display = "";
    $("authAdminOnly").style.display = isAdmin ? "" : "none";
    if (!isAdmin)
        return;
    const res = await api.authConfig();
    if (res.data) {
        fillAuthConfig(res.data.options);
        $("authFile").textContent =
            "策略：" + res.data.file + "　账号：" + res.data.usersFile + "　审计：" + res.data.dataDirectory + "\\auth-events-*.jsonl";
    }
    await loadUsers();
    await loadEvents();
}
function num(id) {
    const value = Number.parseInt($(id).value, 10);
    return Number.isFinite(value) ? value : 0;
}
function fillAuthConfig(options) {
    $("authEnabled").checked = options.enabled !== false;
    $("authProtectRead").checked = options.protectRead === true;
    $("authAllowServiceKey").checked = options.allowServiceKey !== false;
    $("authMaxFail").value = String(options.maxFailures ?? 5);
    $("authLockMin").value = String(options.lockMinutes ?? 15);
    $("authWindowMin").value = String(options.failureWindowMinutes ?? 10);
    $("authSessionMin").value = String(options.sessionMinutes ?? 480);
    $("authServiceKey").textContent = options.serviceKey || "（未生成）";
}
function collectAuthConfig() {
    return {
        enabled: $("authEnabled").checked,
        protectRead: $("authProtectRead").checked,
        allowServiceKey: $("authAllowServiceKey").checked,
        serviceKey: $("authServiceKey").textContent ?? "",
        serviceKeyRole: "admin",
        maxFailures: num("authMaxFail"),
        lockMinutes: num("authLockMin"),
        failureWindowMinutes: num("authWindowMin"),
        sessionMinutes: num("authSessionMin")
    };
}
async function saveAuthConfig() {
    const res = await api.saveAuthConfig(collectAuthConfig());
    if (res.status === 200 && res.data?.ok) {
        badge($("authBadge"), "已保存并生效", "ok");
        $("authMsg").textContent = res.data.backup ? "旧配置已备份：" + res.data.backup : "已写入 auth.json";
        await refreshAuthPanel();
    }
    else {
        badge($("authBadge"), "保存失败", "err");
        $("authMsg").textContent = res.message ?? "未知错误";
    }
}
async function rotateServiceKey() {
    // C7：window.confirm → 自绘确认框（异步）
    const yes = await confirmBox("轮换后旧的服务令牌立刻失效，所有用它的脚本/上位机都要更新。继续？", {
        title: "轮换服务令牌",
        danger: true
    });
    if (!yes)
        return;
    const res = await api.rotateServiceKey();
    if (res.data?.ok) {
        $("authServiceKey").textContent = res.data.serviceKey;
        badge($("authBadge"), "令牌已轮换", "ok");
        $("authMsg").textContent = "新令牌已写入 auth.json（脚本 / 上位机请同步更新）";
    }
}
async function doChangePassword() {
    const oldPassword = $("chgOld").value;
    const newPassword = $("chgNew").value;
    const confirm = $("chgNew2").value;
    if (!newPassword || newPassword !== confirm) {
        badge($("chgMsg"), "两次输入的新密码不一致", "err");
        return;
    }
    const res = await api.changePassword(null, oldPassword, newPassword);
    if (res.status === 200) {
        badge($("chgMsg"), "密码已修改", "ok");
        $("chgOld").value = "";
        $("chgNew").value = "";
        $("chgNew2").value = "";
        await refreshAuthPanel();
    }
    else {
        badge($("chgMsg"), res.message ?? "修改失败", "err");
    }
}
async function loadUsers() {
    const res = await api.authUsers();
    const rows = $("authUserRows");
    clear(rows);
    if (!res.data?.users)
        return;
    for (const user of res.data.users) {
        rows.appendChild(userRow(user));
    }
}
function userRow(user) {
    const tr = el("tr");
    if (!user.enabled)
        tr.className = "offline";
    tr.appendChild(cell(user.username, "code"));
    tr.appendChild(cell(ROLE_LABEL[user.role] ?? user.role));
    tr.appendChild(cell(user.enabled ? "启用" : "禁用", user.enabled ? "" : "noread"));
    tr.appendChild(cell(user.locked ? "锁定 " + user.lockedSeconds + " 秒" : (user.failedCount > 0 ? "失败 " + user.failedCount + " 次" : "—"), user.locked ? "noread" : "muted"));
    tr.appendChild(cell(user.mustChangePassword ? "待改密" : "—", user.mustChangePassword ? "pending" : "muted"));
    tr.appendChild(cell(user.lastLoginAt ?? "—", "muted"));
    tr.appendChild(cell(user.lastLoginIp ?? "—", "muted"));
    const action = el("td");
    const toggle = el("button", user.enabled ? "禁用" : "启用", "btn secondary small");
    toggle.addEventListener("click", () => void updateUser(user.username, null, !user.enabled));
    action.appendChild(toggle);
    const reset = el("button", "重置密码", "btn secondary small");
    reset.style.marginLeft = "6px";
    reset.addEventListener("click", () => void resetPassword(user.username));
    action.appendChild(reset);
    const roleButton = el("button", "改角色", "btn secondary small");
    roleButton.style.marginLeft = "6px";
    roleButton.addEventListener("click", () => void changeRole(user.username, user.role));
    action.appendChild(roleButton);
    const remove = el("button", "删除", "btn secondary small");
    remove.style.marginLeft = "6px";
    remove.addEventListener("click", () => void removeUser(user.username));
    action.appendChild(remove);
    tr.appendChild(action);
    return tr;
}
async function updateUser(username, role, enabled) {
    const res = await api.updateUser(username, role, enabled, null);
    if (res.status !== 200)
        notify(res.message ?? "操作失败");
    await loadUsers();
}
async function changeRole(username, current) {
    const role = window.prompt("角色：admin（管理员）/ operator（操作员）/ viewer（只读）", current);
    if (!role)
        return;
    await updateUser(username, role.trim().toLowerCase(), null);
}
async function resetPassword(username) {
    const password = window.prompt("给 " + username + " 设置新密码（至少 8 位，含字母和数字）");
    if (!password)
        return;
    const res = await api.resetPassword(username, password);
    if (res.status !== 200) {
        notify(res.message ?? "重置失败");
        return;
    }
    notify("已重置 " + username + " 的密码，请转告本人并尽快修改。");
    await loadUsers();
}
async function removeUser(username) {
    // C7：window.confirm → 自绘确认框（异步）
    const yes = await confirmBox("确定删除账号 " + username + " ？", { title: "删除账号", danger: true });
    if (!yes)
        return;
    const res = await api.deleteUser(username);
    if (res.status !== 200) {
        notify(res.message ?? "删除失败");
        return;
    }
    await loadUsers();
}
async function addUser() {
    const username = $("newUser").value.trim();
    const password = $("newPass").value;
    const role = $("newRole").value;
    if (!username || !password) {
        badge($("newUserMsg"), "用户名和密码都要填", "err");
        return;
    }
    const res = await api.addUser(username, password, role);
    if (res.status === 200) {
        badge($("newUserMsg"), "已新增 " + username, "ok");
        $("newUser").value = "";
        $("newPass").value = "";
        await loadUsers();
    }
    else {
        badge($("newUserMsg"), res.message ?? "新增失败", "err");
    }
}
async function loadEvents() {
    const res = await api.authEvents(100);
    const rows = $("authEventRows");
    clear(rows);
    if (!res.data)
        return;
    for (const item of res.data) {
        const tr = el("tr");
        if (item.kind === "login-fail" || item.kind === "login-locked")
            tr.className = "offline";
        tr.appendChild(cell(item.time, "muted"));
        tr.appendChild(cell(item.username ?? "—", "code"));
        tr.appendChild(cell(EVENT_LABEL[item.kind] ?? item.kind));
        tr.appendChild(cell(item.ip ?? "—", "muted"));
        tr.appendChild(cell(item.detail ?? "—"));
        rows.appendChild(tr);
    }
}
