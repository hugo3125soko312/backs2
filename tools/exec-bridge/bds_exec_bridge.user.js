// ==UserScript==
// @name         BDS Exec Bridge
// @namespace    bds-exec-bridge
// @version      0.1.0
// @description  Execute powershell-run code blocks from DeepSeek chat on the local machine
// @author       petri
// @match        https://chat.deepseek.com/*
// @run-at       document-idle
// @grant        none
// ==/UserScript==

(function () {
'use strict';

var PORT = 7788;
var ENDPOINT = 'http://127.0.0.1:' + PORT + '/exec';
var PING = 'http://127.0.0.1:' + PORT + '/ping';
var FENCE_LANG = 'powershell-run';
var TIMEOUT_MS = 30000;
var INJECTED_ATTR = 'data-bds-exec-injected';

function log() {
var args = Array.prototype.slice.call(arguments);
args.unshift('[bds-exec]');
console.log.apply(console, args);
}

function warn() {
var args = Array.prototype.slice.call(arguments);
args.unshift('[bds-exec]');
console.warn.apply(console, args);
}

function pingBridge() {
fetch(PING)
.then(function (r) { return r.json(); })
.then(function (j) { log('bridge online:', j); })
.catch(function (e) { warn('bridge not reachable on port', PORT, e); });
}

function findInput() {
var tas = document.querySelectorAll('textarea');
var i;
for (i = 0; i < tas.length; i++) {
if (tas[i].offsetParent !== null) return tas[i];
}
var ces = document.querySelectorAll('[contenteditable="true"]');
for (i = 0; i < ces.length; i++) {
if (ces[i].offsetParent !== null) return ces[i];
}
return null;
}

function setInputText(text) {
var el = findInput();
if (!el) {
warn('could not find chat input');
return false;
}
el.focus();
if (el.tagName === 'TEXTAREA') {
var proto = window.HTMLTextAreaElement.prototype;
var desc = Object.getOwnPropertyDescriptor(proto, 'value');
if (desc && desc.set) {
desc.set.call(el, text);
} else {
el.value = text;
}
el.dispatchEvent(new Event('input', { bubbles: true }));
} else {
el.innerText = text;
try {
el.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: text }));
} catch (e) {
el.dispatchEvent(new Event('input', { bubbles: true }));
}
}
try { el.scrollIntoView({ behavior: 'smooth', block: 'center' }); } catch (e) {}
return true;
}

function formatOutput(resp) {
var parts = [];
if (resp.timed_out) {
parts.push('(timed out after ' + resp.duration_ms + ' ms)');
}
parts.push(' exit=' + resp.exit_code + '  (' + resp.duration_ms + ' ms)');
        if (resp.stdout) {
            parts.push(resp.stdout.replace(/\r\n/g, '\n').replace(/\s+/, ''));
}
if (resp.stderr) {
parts.push('--- stderr ---');
parts.push(resp.stderr.replace(/\r\n/g, '\n').replace(/\s+$/, ''));
}
if (resp.error) {
parts.push('--- error ---');
parts.push(resp.error);
}
return '`\n' + parts.join('\n') + '\n`\n';
}

function execute(code, btn) {
var originalLabel = btn.textContent;
btn.disabled = true;
btn.textContent = 'running...';
btn.style.background = 'rgba(59,130,246,0.35)';

fetch(ENDPOINT, {
method: 'POST',
headers: { 'Content-Type': 'application/json' },
body: JSON.stringify({ command: code, timeout_ms: TIMEOUT_MS })
})
.then(function (r) {
if (!r.ok) {
return r.text().then(function (t) {
throw new Error('HTTP ' + r.status + ': ' + t);
});
}
return r.json();
})
.then(function (resp) {
btn.disabled = false;
if (resp.timed_out) {
btn.textContent = 'timed out';
btn.style.background = 'rgba(245,158,11,0.35)';
} else if (resp.exit_code === 0) {
btn.textContent = 'ok';
btn.style.background = 'rgba(34,197,94,0.35)';
} else {
btn.textContent = 'exit ' + resp.exit_code;
btn.style.background = 'rgba(239,68,68,0.35)';
}
var formatted = formatOutput(resp);
setInputText(formatted);
setTimeout(function () {
btn.textContent = originalLabel;
btn.style.background = '';
}, 3000);
})
.catch(function (e) {
btn.disabled = false;
btn.textContent = 'error';
btn.style.background = 'rgba(239,68,68,0.35)';
warn('exec failed:', String(e));
setTimeout(function () {
btn.textContent = originalLabel;
btn.style.background = '';
}, 3000);
});
}

function getLanguage(codeEl) {
var cls = codeEl.className || '';
var m = cls.match(/language-([a-z0-9_-]+)/i);
if (m) return m[1].toLowerCase();

// DeepSeek sometimes shows the language in a sibling header element.
var pre = codeEl.closest ? codeEl.closest('pre') : null;
if (pre) {
var sib = pre.previousElementSibling;
while (sib) {
var txt = (sib.textContent || '').trim().toLowerCase();
if (txt === FENCE_LANG) return FENCE_LANG;
sib = sib.previousElementSibling;
}
}
return '';
}

function makeButton(codeEl) {
var btn = document.createElement('button');
btn.type = 'button';
btn.textContent = 'Execute';
btn.title = 'Run this block on the local machine via bds-exec-bridge';
btn.style.cssText = [
'display: inline-block',
'margin: 4px 8px',
'padding: 2px 10px',
'font-size: 12px',
'line-height: 1.4',
'border-radius: 4px',
'border: 1px solid rgba(127,127,127,0.4)',
'background: rgba(127,127,127,0.12)',
'color: inherit',
'cursor: pointer',
'font-family: inherit'
].join(';');
btn.addEventListener('click', function (ev) {
ev.preventDefault();
ev.stopPropagation();
execute(codeEl.textContent || '', btn);
});
return btn;
}

function injectIntoBlock(codeEl) {
if (codeEl.hasAttribute(INJECTED_ATTR)) return;
var pre = codeEl.closest ? codeEl.closest('pre') : null;
if (!pre) return;
codeEl.setAttribute(INJECTED_ATTR, '1');
var parent = pre.parentElement;
if (!parent) return;
var btn = makeButton(codeEl);
parent.insertBefore(btn, pre);
}

function scan() {
var codes = document.querySelectorAll('pre code');
var i;
for (i = 0; i < codes.length; i++) {
var codeEl = codes[i];
if (codeEl.hasAttribute(INJECTED_ATTR)) continue;
var lang = getLanguage(codeEl);
if (lang !== FENCE_LANG) continue;
injectIntoBlock(codeEl);
}
}

var observer = new MutationObserver(function () { scan(); });
observer.observe(document.body, { childList: true, subtree: true });

scan();
pingBridge();
log('loaded; watching for ```' + FENCE_LANG + ' blocks on https://chat.deepseek.com');
})();