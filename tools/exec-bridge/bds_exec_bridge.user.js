// ==UserScript==
// @name         BDS Exec Bridge
// @namespace    bds-exec-bridge
// @version      0.6.0
// @description  Auto-execute BDS control blocks from DeepSeek chat (opt-in)
// @author       petri
// @match        https://chat.deepseek.com/*
// @run-at       document-idle
// @grant        none
// ==/UserScript==

(function () {
    'use strict';

    var PORT = 7788;
    var HOST = '127.0.0.1';
    var BASE = 'http://' + HOST + ':' + PORT;
    var EXEC_URL = BASE + '/exec';
    var LOG_URL  = BASE + '/log';
    var PING_URL = BASE + '/ping';
    var INIT_URL = BASE + '/init';
    var CONNECT_URL    = BASE + '/connect';
    var DISCONNECT_URL = BASE + '/disconnect';
    var HEARTBEAT_URL  = BASE + '/heartbeat';
    var CONNECTED_URL  = BASE + '/connected';

    var TIMEOUT_MS = 30000;
    var STABLE_MS = 1500;
    var HEARTBEAT_MS = 10000;
    var INJECTED_ATTR = 'data-bds-exec-injected';
    var CODE_ATTR = 'data-bds-code';
    var KIND_ATTR = 'data-bds-kind';
    var ID_ATTR = 'data-bds-id';

    // Client id persisted per-tab so a reload keeps the same connection.
    var CLIENT_ID;
    try {
        CLIENT_ID = sessionStorage.getItem('bds_client_id');
        if (!CLIENT_ID) {
            CLIENT_ID = Math.random().toString(36).slice(2, 8);
            sessionStorage.setItem('bds_client_id', CLIENT_ID);
        }
    } catch (e) {
        CLIENT_ID = Math.random().toString(36).slice(2, 8);
    }

    var TAGS = {
        'e':  { kind: 'exec-e',  implemented: true,  postOutput: false, auto: true  },
        'er': { kind: 'exec-er', implemented: true,  postOutput: true,  auto: true  },
        'n':  { kind: 'new',     implemented: false, postOutput: false, auto: false },
        'c':  { kind: 'change',  implemented: false, postOutput: false, auto: false }
    };

    var FENCE_RE = /powershell-(er|e|n|c)(?:=([a-z0-9]{6}))?/i;
    var BODY_ID_RE = /^\s*#\s*bds-id:\s*([a-z0-9]{6})\b/i;
    var ANSI_RE = /(?:\x1b\[[0-9;]*[A-Za-z])|(?:\[[0-9;]{1,3}m)/g;

    var blockStates = new Map();
    var executedIds = new Set();
    var loggedFenceMiss = {};
    var badgeEl = null;
    var buttonCounter = 0;
    var isConnected = false;

    function fmt(msg) { return '[' + CLIENT_ID + '] ' + msg; }

    function sendLog(level, msg) {
        var fn = level === 'error' ? console.error : (level === 'warn' ? console.warn : console.log);
        fn.call(console, '[bds-exec]', fmt(msg));
        try {
            fetch(LOG_URL, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ level: level, message: fmt(msg) }),
                keepalive: true
            }).catch(function () { });
        } catch (e) { }
    }

    function djb2(s) {
        var h = 5381;
        for (var i = 0; i < s.length; i++) h = ((h << 5) + h + s.charCodeAt(i)) | 0;
        return h;
    }

    function stripAnsi(s) { return (s || '').replace(ANSI_RE, ''); }

    function lsGet(k) { try { return sessionStorage.getItem(k); } catch (e) { return null; } }
    function lsSet(k, v) { try { sessionStorage.setItem(k, v); } catch (e) { } }
    function lsDel(k) { try { sessionStorage.removeItem(k); } catch (e) { } }

    function autoFireEnabled() {
        if (!isConnected) return false;
        if (lsGet('bds_exec_paused') === '1') return false;
        return lsGet('bds_exec_auto') === '1';
    }

    function currentMode() {
        if (!isConnected) return 'offline';
        if (lsGet('bds_exec_paused') === '1') return 'PAUSED';
        if (lsGet('bds_exec_auto') === '1') return 'AUTO';
        return 'manual';
    }

    // ---- UI panel ----

    function buildPanel() {
        if (badgeEl) return;
        badgeEl = document.createElement('div');
        badgeEl.style.cssText = [
            'position: fixed','bottom: 8px','right: 8px','z-index: 2147483647',
            'padding: 4px 6px','font: 12px/1.4 ui-monospace, monospace',
            'border-radius: 12px','border: 1px solid rgba(127,127,127,0.35)',
            'background: rgba(0,0,0,0.75)','color: #e2e8f0','user-select: none',
            'box-shadow: 0 2px 6px rgba(0,0,0,0.35)','display: flex',
            'gap: 6px','align-items: center'
        ].join(';');

        var label = document.createElement('span');
        label.id = 'bds-mode-label';
        label.style.padding = '0 6px';
        badgeEl.appendChild(label);

        var connectBtn = document.createElement('button');
        connectBtn.id = 'bds-connect-btn';
        connectBtn.type = 'button';
        connectBtn.textContent = 'connect';
        connectBtn.style.cssText = buttonCss();
        connectBtn.addEventListener('click', onConnectClick);
        badgeEl.appendChild(connectBtn);

        var autoBtn = document.createElement('button');
        autoBtn.type = 'button';
        autoBtn.textContent = 'auto';
        autoBtn.style.cssText = buttonCss();
        autoBtn.addEventListener('click', function () {
            if (lsGet('bds_exec_auto') === '1') { lsDel('bds_exec_auto'); }
            else { lsSet('bds_exec_auto', '1'); }
            refreshPanel();
            sendLog('info', 'toggle auto → mode=' + currentMode());
        });
        badgeEl.appendChild(autoBtn);

        var pauseBtn = document.createElement('button');
        pauseBtn.type = 'button';
        pauseBtn.textContent = 'pause';
        pauseBtn.style.cssText = buttonCss();
        pauseBtn.addEventListener('click', function () {
            if (lsGet('bds_exec_paused') === '1') { lsDel('bds_exec_paused'); }
            else { lsSet('bds_exec_paused', '1'); }
            refreshPanel();
            sendLog('info', 'toggle pause → mode=' + currentMode());
        });
        badgeEl.appendChild(pauseBtn);

        document.body.appendChild(badgeEl);
        refreshPanel();
    }

    function buttonCss() {
        return [
            'font: inherit','padding: 2px 8px','border-radius: 8px',
            'border: 1px solid rgba(127,127,127,0.4)',
            'background: rgba(255,255,255,0.08)','color: inherit','cursor: pointer'
        ].join(';');
    }

    function refreshPanel() {
        if (!badgeEl) return;
        var label = badgeEl.querySelector('#bds-mode-label');
        var cbtn  = badgeEl.querySelector('#bds-connect-btn');
        if (label) {
            var m = currentMode();
            label.textContent = 'bds: ' + m;
            var color = m === 'offline' ? 'rgba(148,163,184,0.9)'
                      : m === 'PAUSED'  ? 'rgba(248,113,113,0.95)'
                      : m === 'AUTO'    ? 'rgba(251,191,36,0.95)'
                                        : 'rgba(74,222,128,0.9)';
            label.style.color = color;
        }
        if (cbtn) {
            cbtn.textContent = isConnected ? 'disconnect' : 'connect';
            cbtn.style.color = isConnected ? 'rgba(74,222,128,0.95)' : '';
        }
    }

    function onConnectClick() {
        if (isConnected) {
            fetch(DISCONNECT_URL, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ client_id: CLIENT_ID })
            })
                .then(function (r) { return r.json(); })
                .then(function (j) {
                    isConnected = false;
                    sendLog('info', 'disconnected (ok=' + j.ok + ')');
                    refreshPanel();
                })
                .catch(function (e) {
                    sendLog('error', 'disconnect failed: ' + String(e));
                });
            return;
        }
        fetch(CONNECT_URL, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ client_id: CLIENT_ID })
        })
            .then(function (r) {
                return r.json().then(function (j) { return { status: r.status, body: j }; });
            })
            .then(function (res) {
                if (res.status === 200 && res.body.ok) {
                    isConnected = true;
                    sendLog('info', 'connected as holder');
                    refreshPanel();
                } else {
                    var holder = res.body.holder || 'unknown';
                    sendLog('warn', 'connect denied: another tab (' + holder + ') is connected');
                    flashButton('busy: ' + holder);
                }
            })
            .catch(function (e) { sendLog('error', 'connect failed: ' + String(e)); });
    }

    function flashButton(text) {
        var cbtn = badgeEl && badgeEl.querySelector('#bds-connect-btn');
        if (!cbtn) return;
        var original = cbtn.textContent;
        cbtn.textContent = text;
        setTimeout(function () { refreshPanel(); }, 2500);
    }

    // ---- connection state ----

    function syncConnectionState() {
        fetch(CONNECTED_URL)
            .then(function (r) { return r.json(); })
            .then(function (j) {
                var holder = j.holder || '';
                if (holder === CLIENT_ID) {
                    if (!isConnected) sendLog('info', 'resuming connection (holder match)');
                    isConnected = true;
                } else if (holder === '') {
                    isConnected = false;
                } else {
                    isConnected = false;
                }
                refreshPanel();
            })
            .catch(function () { });
    }

    function heartbeatLoop() {
        if (!isConnected) return;
        fetch(HEARTBEAT_URL, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ client_id: CLIENT_ID })
        })
            .then(function (r) {
                if (r.status === 409) {
                    sendLog('warn', 'heartbeat rejected; another tab took over');
                    isConnected = false;
                    refreshPanel();
                }
            })
            .catch(function () { });
    }

    // ---- handshake ----

    function initHandshake() {
        sendLog('info', 'init: url=' + location.href + ' mode=' + currentMode() + ' client=' + CLIENT_ID);
        fetch(INIT_URL, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ url: location.href })
        })
            .then(function (r) { return r.json(); })
            .then(function (j) {
                executedIds = new Set(j.ids || []);
                if (j.connected === CLIENT_ID) { isConnected = true; }
                refreshPanel();
                sendLog('info', 'init ok, known ids=' + executedIds.size + ' connected=' + (j.connected || '-'));
            })
            .catch(function (e) { sendLog('error', 'init failed: ' + String(e)); });
    }

    function pingBridge() {
        fetch(PING_URL)
            .then(function (r) { return r.json(); })
            .then(function (j) { sendLog('info', 'bridge online, version=' + j.version); })
            .catch(function (e) { sendLog('error', 'bridge unreachable: ' + String(e)); });
    }

    function findInput() {
        var tas = document.querySelectorAll('textarea');
        for (var i = 0; i < tas.length; i++) if (tas[i].offsetParent !== null) return tas[i];
        var ces = document.querySelectorAll('[contenteditable="true"]');
        for (var k = 0; k < ces.length; k++) if (ces[k].offsetParent !== null) return ces[k];
        return null;
    }

    function setInputText(text) {
        var el = findInput();
        if (!el) { sendLog('error', 'no chat input found'); return false; }
        el.focus();
        if (el.tagName === 'TEXTAREA') {
            var proto = window.HTMLTextAreaElement.prototype;
            var desc = Object.getOwnPropertyDescriptor(proto, 'value');
            if (desc && desc.set) { desc.set.call(el, text); } else { el.value = text; }
            el.dispatchEvent(new Event('input', { bubbles: true }));
        } else {
            el.innerText = text;
            try { el.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: text })); }
            catch (e) { el.dispatchEvent(new Event('input', { bubbles: true })); }
        }
        try { el.scrollIntoView({ behavior: 'smooth', block: 'center' }); } catch (e) { }
        sendLog('info', 'output injected (' + text.length + ' chars)');
        return true;
    }

    function formatOutput(resp) {
        var p = [];
        if (resp.timed_out) p.push('(timed out after ' + resp.duration_ms + ' ms)');
        p.push('$ exit=' + resp.exit_code + '  (' + resp.duration_ms + ' ms)');
        if (resp.stdout) p.push(stripAnsi(resp.stdout).replace(/\r\n/g, '\n').replace(/\s+$/, ''));
        if (resp.stderr) { p.push('--- stderr ---'); p.push(stripAnsi(resp.stderr).replace(/\r\n/g, '\n').replace(/\s+$/, '')); }
        if (resp.error) { p.push('--- error ---'); p.push(stripAnsi(resp.error)); }
        return '```\n' + p.join('\n') + '\n```\n';
    }

    function execute(code, btn, opts) {
        opts = opts || {};
        var original = btn.textContent;
        btn.disabled = true;
        btn.textContent = 'running...';
        btn.style.background = 'rgba(59,130,246,0.35)';
        sendLog('info', 'exec START kind=' + opts.kind + ' source=' + (opts.source || 'manual') +
            ' id=' + (opts.id || '-') + ' len=' + code.length);

        fetch(EXEC_URL, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ command: code, timeout_ms: TIMEOUT_MS, url: opts.url || '', id: opts.id || '' })
        })
            .then(function (r) {
                if (!r.ok) return r.text().then(function (t) { throw new Error('HTTP ' + r.status + ': ' + t); });
                return r.json();
            })
            .then(function (resp) {
                btn.disabled = false;
                if (resp.already_executed) {
                    sendLog('info', 'exec SKIPPED (already executed) id=' + opts.id);
                    btn.textContent = 'already done';
                    btn.style.background = 'rgba(148,163,184,0.35)';
                    if (opts.id) executedIds.add(opts.id);
                    setTimeout(function () { btn.textContent = original; btn.style.background = ''; }, 3000);
                    return;
                }
                sendLog('info', 'exec DONE exit=' + resp.exit_code + ' dur=' + resp.duration_ms + 'ms');
                if (resp.timed_out) { btn.textContent = 'timed out'; btn.style.background = 'rgba(245,158,11,0.35)'; }
                else if (resp.exit_code === 0) { btn.textContent = 'ok'; btn.style.background = 'rgba(34,197,94,0.35)'; }
                else { btn.textContent = 'exit ' + resp.exit_code; btn.style.background = 'rgba(239,68,68,0.35)'; }
                if (opts.id) executedIds.add(opts.id);
                if (opts.postOutput) setInputText(formatOutput(resp));
                setTimeout(function () { btn.textContent = original; btn.style.background = ''; }, 3000);
            })
            .catch(function (e) {
                btn.disabled = false;
                btn.textContent = 'error';
                btn.style.background = 'rgba(239,68,68,0.35)';
                sendLog('error', 'exec FAILED: ' + String(e));
                setTimeout(function () { btn.textContent = original; btn.style.background = ''; }, 3000);
            });
    }

    function parseFence(headerText) {
        var m = FENCE_RE.exec(headerText || '');
        if (!m) return null;
        var tag = m[1].toLowerCase();
        var id = (m[2] || '').toLowerCase();
        var meta = TAGS[tag];
        if (!meta) return null;
        return { tag: tag, id: id, meta: meta };
    }

    function extractBodyId(codeText) {
        var firstLine = (codeText || '').split('\n', 1)[0] || '';
        var m = BODY_ID_RE.exec(firstLine);
        return m ? m[1].toLowerCase() : '';
    }

    function detectFence(preEl) {
        var sib = preEl.previousElementSibling;
        var hops = 0;
        while (sib && hops < 3) {
            var t = (sib.textContent || '').trim();
            var parsed = parseFence(t);
            if (parsed) return parsed;
            sib = sib.previousElementSibling;
            hops++;
        }
        return null;
    }

    function isVisible(el) {
        if (!el) return false;
        var r = el.getBoundingClientRect();
        if (r.width === 0 || r.height === 0) return false;
        var vh = window.innerHeight || document.documentElement.clientHeight;
        return r.bottom > 0 && r.top < vh;
    }

    function makeButton(codeText, kind, id, implemented) {
        buttonCounter++;
        var n = buttonCounter;
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.textContent = implemented ? 'Execute' : 'not implemented';
        btn.title = 'kind=' + kind + ' id=' + (id || '-');
        btn.setAttribute('data-bds-btn-id', String(n));
        btn.setAttribute(CODE_ATTR, codeText || '');
        btn.setAttribute(KIND_ATTR, kind || '');
        btn.setAttribute(ID_ATTR, id || '');
        btn.style.cssText = [
            'display: inline-block','margin: 4px 8px','padding: 2px 10px',
            'font-size: 12px','line-height: 1.4','border-radius: 4px',
            'border: 1px solid rgba(127,127,127,0.4)',
            'background: rgba(127,127,127,0.12)','color: inherit',
            'cursor: pointer','font-family: inherit','pointer-events: auto',
            'position: relative','z-index: 10'
        ].join(';');
        if (!implemented) { btn.disabled = true; btn.style.opacity = '0.6'; btn.style.cursor = 'not-allowed'; }

        function onClick(ev, source) {
            try {
                if (ev) { ev.preventDefault(); ev.stopPropagation(); }
                if (!implemented) { sendLog('warn', 'BTN#' + n + ' kind=' + kind + ' not implemented'); return; }
                var code = btn.getAttribute(CODE_ATTR) || '';
                var id2 = btn.getAttribute(ID_ATTR) || '';
                sendLog('info', 'BTN#' + n + ' ' + source + ' kind=' + kind + ' id=' + (id2 || '-') + ' codeLen=' + code.length);
                if (!code) { sendLog('error', 'BTN#' + n + ' no code'); return; }
                execute(code, btn, { kind: kind, id: id2, url: location.href, postOutput: kind === 'exec-er', source: 'manual' });
            } catch (e) { sendLog('error', 'BTN#' + n + ' threw: ' + String(e)); }
        }

        if (implemented) {
            btn.addEventListener('click', function (ev) { onClick(ev, 'click'); });
            btn.addEventListener('pointerdown', function (ev) { onClick(ev, 'pointerdown'); });
        }
        return btn;
    }

    function injectButton(preEl, codeText, parsed) {
        if (preEl.hasAttribute(INJECTED_ATTR)) return null;
        preEl.setAttribute(INJECTED_ATTR, '1');
        var parent = preEl.parentElement;
        if (!parent) return null;
        var btn = makeButton(codeText, parsed.meta.kind, parsed.id, parsed.meta.implemented);
        parent.insertBefore(btn, preEl);
        return btn;
    }

    function tryAutoFire(preEl, state, btn, codeText, parsed) {
        if (state.autoFired) return false;
        var meta = parsed.meta;
        if (!meta.auto || !meta.implemented) { state.autoFired = true; return false; }
        if (!isConnected) {
            state.autoFired = true;
            sendLog('info', 'auto-fire skipped (not connected)');
            return false;
        }
        if (!autoFireEnabled()) {
            state.autoFired = true;
            sendLog('info', 'auto-fire skipped (mode=' + currentMode() + ')');
            return false;
        }
        if (!isVisible(preEl)) return false;
        if (!parsed.id) {
            state.autoFired = true;
            sendLog('warn', 'auto-fire skipped (no id)');
            return false;
        }
        if (executedIds.has(parsed.id)) {
            state.autoFired = true;
            sendLog('info', 'auto-fire skipped (already in DB) id=' + parsed.id);
            return false;
        }
        state.autoFired = true;
        sendLog('info', 'auto-fire TRIGGER kind=' + meta.kind + ' id=' + parsed.id);
        execute(codeText, btn, { kind: meta.kind, id: parsed.id, url: location.href, postOutput: meta.postOutput, source: 'auto' });
        return true;
    }

    function updateState(preEl, codeText, parsed) {
        var hash = djb2(codeText);
        var st = blockStates.get(preEl);
        if (!st) {
            blockStates.set(preEl, { lastHash: hash, stableSince: Date.now(), loggedReady: false, autoFired: false, tag: parsed.tag, id: parsed.id });
            return;
        }
        if (st.lastHash !== hash) {
            st.lastHash = hash;
            st.stableSince = Date.now();
            st.loggedReady = false;
            return;
        }
        var stableMs = Date.now() - st.stableSince;
        if (!st.loggedReady && stableMs >= STABLE_MS) {
            st.loggedReady = true;
            sendLog('info', 'block READY tag=' + parsed.tag + ' id=' + (parsed.id || '-') +
                ' len=' + codeText.length + ' stable=' + stableMs + 'ms');
        }
    }

    function scan() {
        var pres = document.querySelectorAll('pre');
        var injected = 0, ready = 0, fired = 0;
        for (var i = 0; i < pres.length; i++) {
            var preEl = pres[i];
            var parsed = detectFence(preEl);
            if (!parsed) {
                var sig = (preEl.previousElementSibling ? (preEl.previousElementSibling.textContent || '').trim().slice(0, 40) : '(none)');
                if (!loggedFenceMiss[sig]) { loggedFenceMiss[sig] = true; sendLog('info', 'fence miss: ' + JSON.stringify(sig)); }
                continue;
            }
            var codeEl = preEl.querySelector('code');
            var codeText = (codeEl && codeEl.textContent) || preEl.textContent || '';
            if (!codeText.trim()) continue;

            if (!parsed.id) {
                var bodyId = extractBodyId(codeText);
                if (bodyId) parsed.id = bodyId;
            }

            updateState(preEl, codeText, parsed);
            var st = blockStates.get(preEl);
            if (st && st.loggedReady) ready++;

            var btn = preEl.__bds_btn;
            if (!btn && st && st.loggedReady) {
                btn = injectButton(preEl, codeText, parsed);
                if (btn) {
                    preEl.__bds_btn = btn;
                    injected++;
                    sendLog('info', 'injected button kind=' + parsed.meta.kind + ' id=' + (parsed.id || '-') + ' codeLen=' + codeText.length);
                }
            }
            if (btn && btn.getAttribute(CODE_ATTR) !== codeText) btn.setAttribute(CODE_ATTR, codeText);

            if (st && st.loggedReady && btn && !st.autoFired && parsed.meta.auto && parsed.meta.implemented) {
                if (tryAutoFire(preEl, st, btn, codeText, parsed)) fired++;
            }
        }
        if (injected > 0) sendLog('info', 'scan: +' + injected + ' buttons, ' + ready + ' ready, ' + fired + ' fired');
    }

    var scanScheduled = false;
    function scheduleScan() {
        if (scanScheduled) return;
        scanScheduled = true;
        setTimeout(function () { scanScheduled = false; scan(); }, 150);
    }

    function attachObserver() {
        var target = document.documentElement || document.body;
        if (!target) { setTimeout(attachObserver, 200); return; }
        var obs = new MutationObserver(function () { scheduleScan(); });
        obs.observe(target, { childList: true, subtree: true, characterData: true });
        window.addEventListener('scroll', scheduleScan, { passive: true });
        sendLog('info', 'observer + scroll listener attached');
    }

    buildPanel();
    initHandshake();
    pingBridge();
    attachObserver();
    setInterval(syncConnectionState, 5000);
    setInterval(heartbeatLoop, HEARTBEAT_MS);
    setInterval(scan, 2000);
    setInterval(refreshPanel, 2000);
    scan();
})();