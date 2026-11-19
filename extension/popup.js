"use strict";

const host = "org.softvault.local";
const element = id => document.getElementById(id);
const panels = ["saveForm", "generator"];
let context;
let generated = "";
let busy = false;
let firstFillButton = null;

function status(message = "") {
  element("status").textContent = message;
}

async function native(request) {
  const reply = await chrome.runtime.sendNativeMessage(host, { origin: context.origin, ...request });
  if (reply?.error) throw new Error(reply.error);
  if (!reply) throw new Error("桌面密码库未返回结果。");
  return reply;
}

// Executed in an isolated content world in the exact document the user opened.
function pageForm(expectedOrigin, mode, payload) {
  if (location.protocol !== "https:" || location.origin !== expectedOrigin || window.top !== window)
    return { error: "页面来源已变化。" };

  const visible = field => !field.disabled && !field.readOnly && field.getClientRects().length > 0 &&
    getComputedStyle(field).visibility === "visible";
  const passwords = [...document.querySelectorAll("input[type=password]")].filter(visible);
  const selected = document.activeElement;
  const focusedPassword = passwords.includes(selected) ? selected : null;
  const selectedForm = selected instanceof HTMLInputElement ? selected.form : null;
  const inFocusedForm = selectedForm ? passwords.filter(field => field.form === selectedForm) : passwords;
  let password = focusedPassword ?? (inFocusedForm.length === 1 ? inFocusedForm[0] : null);
  if (mode === "generated" && !password) {
    const newPasswords = inFocusedForm.filter(field => field.autocomplete === "new-password");
    if (newPasswords.length === 1) password = newPasswords[0];
  }
  if (!password) return { error: "请先点选页面的密码框，再打开小窗。" };
  const form = password.form;
  let action;
  try { action = form?.action ? new URL(form.action) : null; } catch { return { error: "表单地址无法确认。" }; }
  if (action && action.origin !== expectedOrigin) return { error: "表单提交地址与当前网站不同。" };

  const scope = form ? [...form.elements] : [...document.querySelectorAll("input")];
  const possible = scope.filter(field => field instanceof HTMLInputElement && visible(field) &&
    (field.type === "text" || field.type === "email" || field.type === "tel"));
  const tagged = possible.filter(field => field.autocomplete.split(/\s+/).includes("username"));
  const named = possible.filter(field => /^(user(name)?|email|login|account|identifier)$/i.test(field.name || field.id));
  const user = tagged.length === 1 ? tagged[0] : named.length === 1 ? named[0] :
    possible.length === 1 ? possible[0] : null;
  if (mode !== "generated" && !user) return { error: "无法唯一确认用户名框。" };

  if (mode === "read") return { user: user.value, password: password.value };
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set;
  const fill = (input, value) => {
    setter.call(input, value);
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new Event("change", { bubbles: true }));
  };
  if (mode !== "generated") fill(user, payload.user);
  if (!password.isConnected || !visible(password)) return { error: "表单在填写时发生了变化。" };
  fill(password, payload.password);
  return { filled: true };
}

async function inPage(mode, payload = {}) {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (tab?.id !== context.tabId) throw new Error("当前标签页已切换。");
  const [run] = await chrome.scripting.executeScript({
    target: { tabId: context.tabId, documentIds: [context.documentId] },
    func: pageForm, args: [context.origin, mode, payload]
  });
  if (!run?.result) throw new Error("当前页面无法访问。");
  if (run.result.error) throw new Error(run.result.error);
  return run.result;
}

function operate(action) {
  return async event => {
    event?.preventDefault();
    if (busy) return;
    busy = true;
    status("");
    const controls = [...document.querySelectorAll("button")];
    for (const control of controls) control.disabled = true;
    try { await action(); }
    catch (error) { status(error.message || "操作没有完成。"); }
    finally {
      busy = false;
      for (const control of controls) control.disabled = false;
    }
  };
}

function showPanel(id) {
  for (const panel of panels) element(panel).hidden = panel !== id;
}

async function list() {
  const { items } = await native({ op: "list" });
  const target = element("accounts");
  target.replaceChildren();
  firstFillButton = null;
  element("keyboardHint").hidden = items.length === 0;
  if (!items.length) status("这个网站还没有保存的账号。");
  for (const item of items) {
    const row = document.createElement("article");
    row.className = "account";
    const description = document.createElement("div");
    const name = document.createElement("strong");
    name.textContent = item.name;
    const user = document.createElement("p");
    user.textContent = item.user;
    description.append(name, user);
    const button = document.createElement("button");
    button.textContent = "填写";
    button.className = "primary";
    button.addEventListener("click", operate(async () => {
      const credential = await native({ op: "fill", id: item.id });
      await inPage("pair", credential);
      window.close();
    }));
    row.append(description, button);
    target.append(row);
    if (!firstFillButton) firstFillButton = button;
  }
}

async function generate() {
  const length = Number(element("length").value);
  if (!Number.isInteger(length) || length < 12 || length > 96) {
    throw new Error("密码长度请输入 12 至 96。");
  }
  const result = await native({ op: "generate", length, symbols: element("symbols").checked });
  generated = result.password;
  element("generated").textContent = generated;
}

element("openSave").addEventListener("click", () => showPanel("saveForm"));
element("openGenerator").addEventListener("click", operate(async () => { showPanel("generator"); await generate(); }));
element("again").addEventListener("click", operate(generate));
element("readPage").addEventListener("click", operate(async () => {
  const values = await inPage("read");
  element("user").value = values.user;
  element("password").value = values.password;
  status("请核对账号，再加密保存。");
}));
element("saveGenerated").addEventListener("click", () => {
  if (!generated) return;
  showPanel("saveForm");
  element("password").value = generated;
});
element("fillGenerated").addEventListener("click", operate(async () => {
  if (!generated) throw new Error("请先生成密码。");
  await inPage("generated", { password: generated });
  status("已填写密码。可以继续带入保存。");
}));
element("saveForm").addEventListener("submit", operate(async () => {
  const request = { op: "save", user: element("user").value, password: element("password").value };
  const result = await native(request);
  if (result.updateId) {
    if (!window.confirm("更新这个网站的同名账号？")) return;
    await native({ ...request, id: result.updateId });
  }
  element("saveForm").reset();
  element("generated").textContent = "";
  generated = "";
  showPanel(null);
  await list();
  status("账号已加密保存。");
}));

document.addEventListener("keydown", event => {
  if (event.key === "Escape") {
    event.preventDefault();
    window.close();
    return;
  }
  if (event.key !== "Enter" || event.repeat || busy || !firstFillButton ||
      event.altKey || event.ctrlKey || event.shiftKey || event.metaKey ||
      !element("saveForm").hidden || !element("generator").hidden ||
      event.target.closest?.("input, textarea, select, button, a")) return;
  event.preventDefault();
  firstFillButton.click();
});

(async () => {
  if (chrome.commands) {
    try {
      const shortcut = (await chrome.commands.getAll()).find(command => command.name === "_execute_action");
      if (shortcut?.shortcut !== "Alt+C") {
        const notice = element("shortcutNotice");
        notice.textContent = "请在浏览器的扩展快捷键页面，将 KeyLib 设为 Alt+C。";
        notice.hidden = false;
      }
    } catch { /* The popup remains usable through its toolbar icon. */ }
  }
  try {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!tab?.id || !tab.url?.startsWith("https://")) throw new Error("请打开一个 HTTPS 登录页面。");
    const [checked] = await chrome.scripting.executeScript({
      target: { tabId: tab.id }, func: () => location.origin
    });
    if (!checked?.documentId || checked.result !== new URL(tab.url).origin)
      throw new Error("当前页面无法确认，请重新打开小窗。");
    context = { tabId: tab.id, documentId: checked.documentId, origin: checked.result };
    element("site").textContent = checked.result;
    status("");
    await list();
    element("main").hidden = false;
  } catch (error) { status(error.message || "请先运行并解锁KeyLib。"); }
})();
