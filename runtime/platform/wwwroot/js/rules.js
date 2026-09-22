/**
 * 条码过滤规则（B2）：表格编辑 + 保存 + 规则测试。
 *
 * 设计取舍：
 *   * 规则直接在表格里编辑，一行一条；"优先级"小的先判断，第一条命中的规则决定结果；
 *   * 「测试」用的是**当前界面上正在编辑的规则**（还没保存也能试），
 *     这样现场可以先把规则调好、看清楚每一条码的判定，再决定保存；
 *   * 保存后平台立即生效（平台每秒检查一次规则文件，不用重启）。
 */
import { api } from "./api.js?v=91099a1e";
import { $, badge, cell, clear, el, notify } from "./dom.js?v=91099a1e";
const ACTION_KEEP = "keep";
const ACTION_DROP = "drop";
let current = null;
/** 表格里正在编辑的规则（保存/测试都用它） */
let editing = [];
export function initRules() {
    $("btnAddRule").addEventListener("click", () => {
        collectRules();
        editing.push({ name: "新规则", priority: nextPriority(), enabled: true, action: ACTION_KEEP });
        renderRuleRows();
    });
    $("btnSaveRules").addEventListener("click", () => void saveRules());
    $("btnReloadRules").addEventListener("click", () => void refreshRules());
    $("btnTestRules").addEventListener("click", () => void testRules());
    $("btnLoadFiltered").addEventListener("click", () => void loadFiltered());
}
export async function refreshRules() {
    const res = await api.rules();
    if (res.status !== 200 || !res.data) {
        $("ruleMsg").textContent = "读取规则失败：" + (res.message ?? "HTTP " + res.status);
        return;
    }
    current = res.data;
    editing = current.rules.map((r) => ({ ...r }));
    renderMeta();
    renderRuleRows();
    $("ruleMsg").textContent = "规则文件：" + current.file + (current.exists ? "" : "（还不存在，保存后生成）");
}
function renderMeta() {
    if (!current)
        return;
    $("ruleDefaultAction").value = current.defaultAction;
    $("ruleIgnoreCase").checked = current.ignoreCase;
    $("ruleSummary").textContent =
        "共 " + current.ruleCount + " 条规则，启用 " + current.enabledCount + " 条；"
            + "没有规则命中时按默认动作处理。";
}
function nextPriority() {
    let max = 0;
    for (const r of editing) {
        if (r.priority > max)
            max = r.priority;
    }
    return max + 10;
}
// ---------------------------------------------------------------- 表格渲染
function textInput(value, size, placeholder) {
    const input = el("input");
    input.type = "text";
    input.value = value === null || value === undefined ? "" : String(value);
    input.placeholder = placeholder;
    input.size = size;
    return input;
}
function renderRuleRows() {
    const body = $("ruleRows");
    clear(body);
    $("ruleEmpty").style.display = editing.length ? "none" : "block";
    editing.forEach((rule, index) => {
        const tr = el("tr");
        const priority = textInput(rule.priority, 4, "10");
        priority.className = "rule-input num";
        priority.oninput = () => {
            rule.priority = Number(priority.value) || 0;
        };
        const priorityTd = el("td");
        priorityTd.appendChild(priority);
        tr.appendChild(priorityTd);
        const name = textInput(rule.name, 18, "规则名");
        name.className = "rule-input";
        name.oninput = () => {
            rule.name = name.value;
        };
        const nameTd = el("td");
        nameTd.appendChild(name);
        tr.appendChild(nameTd);
        const action = el("select", undefined, "rule-input");
        for (const [value, label] of [[ACTION_KEEP, "保留"], [ACTION_DROP, "丢弃"]]) {
            const option = el("option", label);
            option.value = value;
            action.appendChild(option);
        }
        action.value = rule.action === ACTION_DROP ? ACTION_DROP : ACTION_KEEP;
        action.onchange = () => {
            rule.action = action.value;
        };
        const actionTd = el("td");
        actionTd.appendChild(action);
        tr.appendChild(actionTd);
        tr.appendChild(pairCell(textInput(rule.minLength ?? "", 3, "最小"), textInput(rule.maxLength ?? "", 3, "最大"), (v) => { rule.minLength = v === "" ? null : Number(v); }, (v) => { rule.maxLength = v === "" ? null : Number(v); }));
        tr.appendChild(listCell(textInput(rule.prefix ?? "", 8, "前缀"), (v) => { rule.prefix = v; }));
        tr.appendChild(listCell(textInput(rule.suffix ?? "", 8, "后缀"), (v) => { rule.suffix = v; }));
        tr.appendChild(listCell(textInput(rule.regex ?? "", 16, "^JD\\d{10}$"), (v) => { rule.regex = v; }));
        tr.appendChild(listCell(textInput((rule.whitelist ?? []).join(","), 12, "SF*,*0001"), (v) => { rule.whitelist = splitList(v); }));
        tr.appendChild(listCell(textInput((rule.blacklist ?? []).join(","), 12, "TEST*"), (v) => { rule.blacklist = splitList(v); }));
        const enabled = el("input");
        enabled.type = "checkbox";
        enabled.checked = rule.enabled;
        enabled.onchange = () => {
            rule.enabled = enabled.checked;
        };
        const enabledTd = el("td");
        enabledTd.appendChild(enabled);
        tr.appendChild(enabledTd);
        const remove = el("button", "删除", "btn secondary small");
        remove.onclick = () => {
            collectRules();
            editing.splice(index, 1);
            renderRuleRows();
        };
        const removeTd = el("td");
        removeTd.appendChild(remove);
        tr.appendChild(removeTd);
        body.appendChild(tr);
    });
}
/** 一行里放两个输入框（长度 min/max 用） */
function pairCell(first, second, onFirst, onSecond) {
    first.className = "rule-input num";
    second.className = "rule-input num";
    first.oninput = () => onFirst(first.value.trim());
    second.oninput = () => onSecond(second.value.trim());
    const td = el("td");
    td.appendChild(first);
    td.appendChild(document.createTextNode(" ~ "));
    td.appendChild(second);
    return td;
}
function listCell(input, onInput) {
    input.className = "rule-input";
    input.oninput = () => onInput(input.value.trim());
    const td = el("td");
    td.appendChild(input);
    return td;
}
function splitList(text) {
    return text
        .split(",")
        .map((s) => s.trim())
        .filter((s) => s.length > 0);
}
/**
 * 把表格里的值收集回 editing。
 * （输入框是边输边写进 editing 的，这里主要是在增删行前把当前 UI 状态固化一次）
 */
function collectRules() {
    // 值已经通过 oninput 写进 editing，这里无需额外处理；保留函数是为了语义清晰
}
// ---------------------------------------------------------------- 保存
function draftRuleSet() {
    return {
        defaultAction: $("ruleDefaultAction").value || ACTION_KEEP,
        ignoreCase: $("ruleIgnoreCase").checked,
        rules: editing
    };
}
async function saveRules() {
    const ruleSet = draftRuleSet();
    $("ruleMsg").textContent = "保存中…";
    const res = await api.saveRules(ruleSet);
    if (res.status === 200 && res.data?.ok) {
        $("ruleMsg").textContent = "已保存并生效：" + res.data.enabledCount + " 条启用规则" +
            (res.data.backup ? "（原文件已备份）" : "");
        badge($("ruleBadge"), "已保存", "ok");
        await refreshRules();
        await testRules();
    }
    else {
        $("ruleMsg").textContent = "保存失败：" + (res.message ?? "HTTP " + res.status);
        badge($("ruleBadge"), "保存失败", "err");
    }
}
// ---------------------------------------------------------------- 规则测试
async function testRules() {
    const text = $("ruleTestCodes").value;
    const codes = text
        .split(/\r?\n|,|;/)
        .map((s) => s.trim())
        .filter((s) => s.length > 0);
    if (!codes.length) {
        notify("请先在测试框里粘贴几个条码（每行一个）");
        return;
    }
    const res = await api.testRules(codes, draftRuleSet());
    const body = $("ruleTestRows");
    clear(body);
    if (res.status !== 200 || !res.data) {
        $("ruleTestSummary").textContent = "测试失败：" + (res.message ?? "HTTP " + res.status);
        return;
    }
    const report = res.data;
    for (const decision of report.decisions) {
        const tr = el("tr");
        tr.appendChild(cell(decision.code, decision.kept ? "code" : "noread"));
        tr.appendChild(cell(decision.kept ? "保留" : "丢弃", decision.kept ? "" : "noread"));
        tr.appendChild(cell(decision.matchedRule ?? "（无规则命中）", decision.matchedRule ? "" : "muted"));
        tr.appendChild(cell(decision.reason, "muted"));
        body.appendChild(tr);
    }
    const tag = report.usingDraft ? "（用的是界面上未保存的规则）" : "（用的是当前生效的规则）";
    $("ruleTestSummary").textContent =
        "保留 " + report.kept + " 条 / 丢弃 " + report.dropped + " 条（共 " + report.decisions.length + " 条）；默认动作 " +
            (report.defaultAction === ACTION_DROP ? "丢弃" : "保留") + tag;
    const problems = $("ruleProblems");
    if (report.problems?.length) {
        badge(problems, "规则问题：" + report.problems.join("；"), "err");
    }
    else {
        clear(problems);
    }
}
// ---------------------------------------------------------------- 最近被丢掉的码
async function loadFiltered() {
    const res = await api.filteredCodes(50);
    const body = $("filteredRows");
    clear(body);
    if (res.status !== 200 || !res.data) {
        $("filteredSummary").textContent = "读取失败：" + (res.message ?? "HTTP " + res.status);
        return;
    }
    for (const item of res.data) {
        const tr = el("tr");
        tr.appendChild(cell(item.time, "muted"));
        tr.appendChild(cell(item.code, "noread"));
        tr.appendChild(cell(item.rule ?? "（默认动作）", item.rule ? "" : "muted"));
        tr.appendChild(cell(item.traceId, "muted"));
        tr.appendChild(cell(item.reason, "muted"));
        body.appendChild(tr);
    }
    $("filteredSummary").textContent = res.data.length
        ? "本次运行被丢弃 " + res.data.length + " 条（最多 200 条；重启后从零开始，历史请看过包行上的「丢弃」标记）"
        : "本次运行还没有丢弃过条码（重启后从零开始，历史请看过包行上的「丢弃」标记）";
}
