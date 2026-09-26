/* ══════════════════════════════════════════════════════════════════════════
   WK LoL — interface runtime.

   This file does four things and deliberately no more:
     1. talks to the VB side over the WebView2 bridge,
     2. builds the sidebar and switches views,
     3. renders whatever state the bridge hands back,
     4. makes sure every one of those has a visible, non-blocking state.

   NO BOT LOGIC LIVES HERE. The page decides nothing — it asks, it shows, it
   sends the click on. Anything that looks like a decision (is setup complete,
   may the Orb Walker be switched on, what is wrong with its keys) is answered
   by VB and arrives as a field.
   ══════════════════════════════════════════════════════════════════════════ */

'use strict';

// ── Navigation ────────────────────────────────────────────────────────────
// Grouped by WHAT THE USER IS DOING. Essentials is what the bot does in the
// game; the unlabelled group after it is everything about the program itself.
// Nothing is locked here: until Setup is finished the rail shows the setup
// flow instead of this list, so there is nothing to reach early.
const NAV = [
  { group: null, items: [
    { id: 'home',      label: 'Home',       icon: 'i-home'     },
    { id: 'setup',     label: 'Setup',      icon: 'i-setup'    }
  ]},
  { group: 'Essentials', items: [
    { id: 'orbwalker', label: 'Orb Walker', icon: 'i-target'   }
  ]},
  { group: null, items: [
    { id: 'log',       label: 'Log',        icon: 'i-terminal' },
    { id: 'settings',  label: 'Settings',   icon: 'i-gear'     }
  ]}
];

const SUBTITLE = {
  home:      'Board and module at a glance',
  setup:     'Get the Arduino ready',
  orbwalker: 'Hold a key, run the cycle',
  log:       'Everything the bot has said',
  settings:  'Board and input timing'
};

const $  = (sel, root) => (root || document).querySelector(sel);
const $$ = (sel, root) => Array.prototype.slice.call((root || document).querySelectorAll(sel));

/* ══════════════════════════════════════════════════════════════════════════
   Bridge — the only channel between this page and the bot.

   Request : { id, cmd, data }   web  → host
   Reply   : { id, ok, data }    host → web
   Event   : { evt, data }       host → web, unsolicited

   EVERY CALL HAS A DEADLINE. A promise that never settles is how a web UI
   ends up with a button spinning forever. After TIMEOUT_MS the call rejects,
   the caller un-busies and the user is told — always a way out.
   ══════════════════════════════════════════════════════════════════════════ */
const Bridge = (function () {

  const TIMEOUT_MS = 15000;

  const pending  = new Map();
  const handlers = new Map();
  let   seq      = 0;

  const host = (typeof window.chrome !== 'undefined' && window.chrome.webview) || null;

  function receive(msg) {
    if (!msg) return;

    if (msg.evt) {                          // unsolicited push
      const fn = handlers.get(msg.evt);
      if (fn) fn(msg.data);
      return;
    }

    const entry = pending.get(msg.id);
    if (!entry) return;                     // a late reply to a timed-out call
    pending.delete(msg.id);
    clearTimeout(entry.timer);
    if (msg.ok) entry.resolve(msg.data);
    else        entry.reject(new Error(msg.error || 'The bot refused that request.'));
  }

  if (host) host.addEventListener('message', e => receive(e.data));

  return {
    /**
     * Fire a command and get its reply. Always settles.
     *
     * `timeout` is for the few commands that wait on a PERSON or on hardware rather than on the
     * bot — a key-capture dialog, a firmware upload. Those say how long they are willing to wait,
     * and still always settle.
     */
    call(cmd, data, timeout) {
      if (!host) return Promise.reject(new Error('No bot on the other end — open this through wklol.exe.'));
      const id = ++seq;
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
          pending.delete(id);
          reject(new Error('The bot did not answer in time.'));
        }, timeout || TIMEOUT_MS);
        pending.set(id, { resolve, reject, timer });
        host.postMessage({ id, cmd, data: data || {} });
      });
    },

    /** Subscribe to a push event from the bot. */
    on(evt, fn) { handlers.set(evt, fn); }
  };
})();

/* ══════════════════════════════════════════════════════════════════════════
   Feedback — press, busy, toast.

   The house rule this section exists to enforce: the user must never be able
   to wonder whether a click registered, and must never watch a dead window.
   ══════════════════════════════════════════════════════════════════════════ */
const FX = (function () {

  /** A spinner that flashes for 80ms reads as a glitch, so busy has a floor. */
  const MIN_BUSY_MS = 240;

  /** Ink ripple from the click point. Delegated, so it covers every button. */
  document.addEventListener('pointerdown', e => {
    const btn = e.target.closest && e.target.closest('.btn, .nav-item');
    if (!btn || btn.disabled) return;
    const r    = btn.getBoundingClientRect();
    const size = Math.max(r.width, r.height);
    const ink  = document.createElement('span');
    ink.className = 'ripple';
    ink.style.width = ink.style.height = size + 'px';
    ink.style.left  = (e.clientX - r.left - size / 2) + 'px';
    ink.style.top   = (e.clientY - r.top  - size / 2) + 'px';
    btn.appendChild(ink);
    setTimeout(() => ink.remove(), 480);
  });

  /**
   * Runs a promise with the button showing it. The control is disabled for the
   * duration (so a double click cannot fire twice), held busy for at least
   * MIN_BUSY_MS, and restored on both success AND failure — a rejected call
   * must not leave a dead spinner behind.
   */
  async function busy(btn, promise) {
    if (!btn) return promise;
    if (btn.classList.contains('is-busy')) return;     // already in flight
    const started = Date.now();
    btn.classList.add('is-busy');
    btn.disabled = true;
    try {
      return await promise;
    } finally {
      const held = Date.now() - started;
      setTimeout(() => {
        btn.classList.remove('is-busy');
        btn.disabled = false;
      }, Math.max(0, MIN_BUSY_MS - held));
    }
  }

  const ICONS = { ok: 'i-check', warn: 'i-alert', danger: 'i-alert', info: 'i-info' };

  function toast(message, kind) {
    kind = kind || 'info';
    const el = document.createElement('div');
    el.className = 'toast is-' + kind;
    el.innerHTML = '<svg class="icon"><use href="#' + (ICONS[kind] || ICONS.info) + '"></use></svg>';
    el.appendChild(document.createTextNode(message));
    $('#toasts').appendChild(el);

    setTimeout(() => {
      el.classList.add('is-leaving');
      setTimeout(() => el.remove(), 220);
    }, kind === 'danger' ? 5200 : 3000);
  }

  return { busy, toast };
})();

/* ══════════════════════════════════════════════════════════════════════════
   ExitDialog — the question the window's X asks.

   The X belongs to the native window, so the page never sees the click: the
   shell cancels the close and pushes "app.close" with what closing would stop.
   The page answers "app.close.shown" at once — a shell that hears nothing asks
   natively instead, so a broken page can never make the bot impossible to close.

   Keep running is the default in every sense: it has the focus, Escape and a
   click on the scrim both mean it, and Enter on the focused button gives it.
   ══════════════════════════════════════════════════════════════════════════ */
const ExitDialog = (function () {

  let open_     = false;
  let closing   = false;
  let lastFocus = null;

  const scrim = () => $('#exit-modal');
  const card  = () => $('#exit-modal .modal');

  function paint(data) {
    const running = (data && data.running) || [];

    const chips = $('#exit-running');
    chips.textContent = '';
    $('#exit-modal .modal-section-label').hidden = running.length === 0;
    if (running.length === 0) {
      const idle = document.createElement('span');
      idle.className = 'modal-idle';
      idle.textContent = 'Nothing is running — closing loses no work in progress.';
      chips.appendChild(idle);
    } else {
      running.forEach((name, i) => {
        const tag = document.createElement('span');
        tag.className = 'tag';
        tag.style.animationDelay = (80 + i * 40) + 'ms';
        tag.innerHTML = '<span class="dot is-ok is-live"></span>';
        tag.appendChild(document.createTextNode(name));
        chips.appendChild(tag);
      });
    }
  }

  /** The shell asked. A second ask while it is up draws the eye back instead of stacking. */
  function open(data) {
    paint(data);
    // Said first, before anything can fail: this is what stops the shell asking natively.
    Bridge.call('app.close.shown').catch(() => {});

    if (open_) {
      const c = card();
      c.classList.remove('is-nudging');
      void c.offsetWidth;                          // restart the animation
      c.classList.add('is-nudging');
      $('#exit-cancel').focus();
      return;
    }

    open_   = true;
    closing = false;
    lastFocus = document.activeElement;
    const s = scrim();
    s.classList.remove('is-leaving');
    card().classList.remove('is-closing', 'is-nudging');
    s.hidden = false;
    document.body.classList.add('is-modal');
    $('#exit-cancel').focus();
  }

  function dismiss() {
    if (!open_ || closing) return;
    open_ = false;
    const s = scrim();
    s.classList.add('is-leaving');
    setTimeout(() => {
      if (open_) return;                           // reopened while leaving
      s.hidden = true;
      s.classList.remove('is-leaving');
      document.body.classList.remove('is-modal');
      if (lastFocus && lastFocus.focus) lastFocus.focus();
    }, 190);
  }

  /** Ends the bot. The reply may never arrive — the process is gone — so nothing waits on it. */
  function end(btn) {
    if (closing) return;
    closing = true;
    card().classList.add('is-closing');
    btn.classList.add('is-busy');
    $$('#exit-modal .btn').forEach(b => { b.disabled = true; });
    Bridge.call('app.exit').catch(err => {
      // Only reached when the bot refused — a closed bot never answers.
      closing = false;
      card().classList.remove('is-closing');
      btn.classList.remove('is-busy');
      $$('#exit-modal .btn').forEach(b => { b.disabled = false; });
      FX.toast(err.message, 'danger');
    });
  }

  /** Tab stays inside the card; Escape is Keep running. */
  function onKey(e) {
    if (!open_) return;
    if (e.key === 'Escape') { e.preventDefault(); dismiss(); return; }
    if (e.key !== 'Tab') {
      if (e.key !== 'Enter' && e.key !== ' ') e.stopPropagation();
      return;
    }
    const buttons = $$('#exit-modal .btn').filter(b => !b.hidden && !b.disabled);
    if (!buttons.length) return;
    const i = buttons.indexOf(document.activeElement);
    const next = e.shiftKey ? (i <= 0 ? buttons.length - 1 : i - 1)
                            : (i === buttons.length - 1 ? 0 : i + 1);
    e.preventDefault();
    buttons[next].focus();
  }

  function wire() {
    $('#exit-cancel').addEventListener('click', dismiss);
    $('#exit-confirm').addEventListener('click', e => end(e.currentTarget));
    // The scrim itself, not the card: a click beside the question is not an answer to close.
    scrim().addEventListener('pointerdown', e => { if (e.target === scrim()) dismiss(); });
    document.addEventListener('keydown', onKey, true);
  }

  return { wire, open };
})();

/* ══════════════════════════════════════════════════════════════════════════
   App
   ══════════════════════════════════════════════════════════════════════════ */
const App = (function () {

  let state   = null;           // { build, setup, engine, orb }
  let current = 'home';         // the screen the markup opens on — see index.html

  // ── Sidebar ─────────────────────────────────────────────────────────────

  function buildNav() {
    const nav = $('#nav');
    const frag = document.createDocumentFragment();

    NAV.forEach((section, i) => {
      if (section.group) {
        const h = document.createElement('div');
        h.className   = 'nav-group';
        h.textContent = section.group;
        frag.appendChild(h);
      } else if (i > 0) {
        // A group with no name still needs to read as a group, and a plain rule says exactly
        // that where a heading would have to invent a word.
        const rule = document.createElement('div');
        rule.className = 'nav-rule';
        frag.appendChild(rule);
      }
      section.items.forEach(item => {
        const b = document.createElement('button');
        b.className   = 'nav-item' + (item.id === current ? ' is-active' : '');
        b.dataset.nav = item.id;
        b.innerHTML =
          '<svg class="icon"><use href="#' + item.icon + '"></use></svg>' +
          '<span class="nav-label">' + item.label + '</span>';
        b.addEventListener('click', () => go(item.id));
        frag.appendChild(b);
      });
    });

    nav.appendChild(frag);
  }

  function go(id) {
    if (id === current) return;
    current = id;

    $$('.nav-item').forEach(n => n.classList.toggle('is-active', n.dataset.nav === id));
    $$('.view').forEach(v => v.classList.toggle('is-active', v.dataset.view === id));

    const label = NAV.reduce((f, s) => f || s.items.find(i => i.id === id), null);
    $('#view-title').textContent = label ? label.label : id;
    $('#view-sub').textContent   = SUBTITLE[id] || '';

    // The console draws nothing while it is not the screen on show, so it has to
    // be told which it is. Everything it missed is rebuilt from the buffer here.
    logActive(id === 'log');

    // Setup outside the flow is a checklist to review: the board card looks again as it opens,
    // so what it says is about now. Looking touches no port — see ArduinoSetup.ScanAsync.
    if (id === 'setup' && !wizardActive()) run(null, 'setup.scan', null, renderSetup, SCAN_MS);
  }

  // ── Rendering ───────────────────────────────────────────────────────────

  function unskeleton(el) { el.classList.remove('skel'); }

  function paintTag(el, text, kind) {
    el.textContent = text;
    el.className = 'tag' + (kind ? ' is-' + kind : '');
  }

  /** Paints a module switch and its label. */
  function paintSwitch(id, on) {
    const box = $('#' + id);
    box.checked = !!on;
    box.closest('.switch').querySelector('.switch-label').textContent = on ? 'On' : 'Off';
  }

  /** The checklist Home's setup card and the flow's last step both draw. */
  function paintSteps(host, steps) {
    host.innerHTML = '';
    (steps || []).forEach(step => {
      const el = document.createElement('div');
      el.className = 'step' + (step.done ? ' is-done' : '');
      el.innerHTML =
        '<span class="step-mark"><svg class="icon" style="width:9px;height:9px">' +
          '<use href="#i-check"></use></svg></span>' +
        '<span class="step-name"></span>' +
        '<span class="step-note"></span>';
      el.querySelector('.step-name').textContent = step.name;
      el.querySelector('.step-note').textContent = step.note || '';
      host.appendChild(el);
    });
  }

  function renderSetupCard(s) {
    paintSteps($('#setup-steps'), s.steps);

    const done  = (s.steps || []).filter(x => x.done).length;
    const total = Math.max(1, (s.steps || []).length);
    $('#setup-meter').style.width = (done / total * 100) + '%';
    $('#setup-meter').className   = 'meter-fill is-' + (s.complete && done === total ? 'ok' : 'warn');
    paintTag($('#setup-tag'),
             s.complete ? (done === total ? 'Complete' : done + ' of ' + total) : done + ' of ' + total,
             s.complete && done === total ? 'ok' : 'warn');

    // Loud only while it is the thing standing between the user and a running bot.
    const open = $('#setup-open');
    open.querySelector('.btn-body').textContent = s.complete ? 'Review setup' : 'Resume setup';
    open.classList.toggle('is-primary', !s.complete);
  }

  function renderEngine(e) {
    $('#run-text').textContent = e.text || (e.running ? 'Running' : 'Idle');
    $('#run-dot').className    = 'dot ' + (e.running ? 'is-ok is-live' : '');
  }

  /** The Orb Walker as Home shows it: whether it is on, its key, and the cycle in one line. */
  function renderOrbCard(o) {
    const master = $('#home-orb-master'), cycle = $('#home-orb-cycle');
    master.textContent = o.master.isSet ? o.master.text + (o.toggle ? ' · toggle' : ' · hold') : 'Not set';
    master.classList.toggle('is-empty', !o.master.isSet);
    cycle.textContent = o.cycle;
    [master, cycle].forEach(unskeleton);

    if (o.enabled)    paintTag($('#home-orb-tag'), 'On', 'ok');
    else if (o.ready) paintTag($('#home-orb-tag'), 'Off', null);
    else              paintTag($('#home-orb-tag'), o.problems.length + ' to fix', 'warn');
  }

  /* ══════════════════════════════════════════════════════════════════════
     Setup

     FIRST RUN IS A FLOW. While setup has never been finished the page is
     the four steps and nothing else — the rail trades its navigation for
     them — and a step moves on only once the bot says it passed. Entering a
     step runs its check, so the only thing a person does is what the step
     asks for: plug the board in, agree to flash it.

     AFTERWARDS IT IS A CHECKLIST. The same three cards, each with the button
     that runs its check again. Nothing is run by opening it but the look for
     the board, which touches no port.
     ══════════════════════════════════════════════════════════════════════ */

  /** Budgets for the calls that wait on hardware rather than on the bot. */
  const SCAN_MS  = 30 * 1000;                 // WMI can be slow on a busy machine
  const BOARD_MS = 90 * 1000;                 // every COM port is knocked on in turn
  const FLASH_MS = 16 * 60 * 1000;            // the first flash downloads a toolchain

  /** Keeps looking while there is nothing to find, so plugging the cable in is the whole action. */
  const RESCAN_MS = 1500;

  let wizardStep = 1;
  let shownStep  = 0;
  let rescan     = null;

  function wizardActive() {
    return !!(state && state.setup && !state.setup.complete);
  }

  /**
   * What each step says about itself: the page header, and the footer's line while the step is
   * still open. The footer's line once it clears is built from the step's own note, so it names
   * what was actually found rather than a generic "done".
   */
  const FLOW = {
    1: { title: 'Find the board',
         lead: 'An Arduino Pro Micro, Leonardo or Micro — any board with an ATmega32U4, which is the ' +
               'chip that can pretend to be a keyboard. A Nano or an Uno cannot: their USB is a separate ' +
               'chip, not the microcontroller. Plug it in with a cable that carries data.',
         waiting: 'Plug the board in — this keeps looking.',
         done: note => 'Found: ' + note + '.' },
    2: { title: 'Put the firmware on it',
         lead: 'The board has to be running the current wk_hid before it can be a keyboard and click ' +
               'in place. This is a one-off: once it is on, it stays on — a board flashed by wk needs ' +
               'it once more.',
         waiting: 'The board has to answer as wk_hid to continue.',
         done: note => 'The board answers: ' + note + '.' },
    3: { title: 'Check it end to end',
         lead: 'Connecting is not the same as working. This moves the pointer and reads back what ' +
               'Windows actually saw — the cursor jumps three times — then asks the board what it ' +
               'believes it is holding.',
         waiting: 'The check has to pass to continue.',
         done: note => 'Checked — ' + note.toLowerCase() + '.' },
    4: { title: 'Ready',
         lead: 'The board is found, flashed and checked. Finish opens the rest of WK; Setup stays one ' +
               'click away if the board ever needs checking again. The game still has to be the window ' +
               'in front — the board is a real keyboard and types wherever Windows points it.',
         waiting: 'Something changed since the last step — go back through Setup.',
         done: () => 'Everything the bot needs is in place.' }
  };

  /** One step's verdict: the bold line and the reason under it, coloured by tone. */
  function paintVerdict(host, v) {
    host.hidden = !(v && v.title);
    if (host.hidden) return;
    host.className = 'verdict' + (v.tone ? ' is-' + v.tone : '');
    host.querySelector('.verdict-title').textContent = v.title;
    host.querySelector('.verdict-detail').textContent = v.detail || '';
  }

  /** Everything Setup draws, from one answer — the cards, Home's checklist and the flow. */
  function renderSetup(s) {
    if (!s) return;
    const was = state.setup;
    state.setup = s;
    const steps = s.steps || [];

    // Board
    paintTag($('#board-tag'), steps[0] ? steps[0].note : '—', s.board.tone);
    paintVerdict($('#board-verdict'), s.board);
    const sel = $('#board-port');
    const keep = s.port || '';
    sel.innerHTML = '';
    const auto = document.createElement('option');
    auto.value = '';
    auto.textContent = 'Auto-detect';
    sel.appendChild(auto);
    const ports = (s.ports || []).slice();
    if (s.port && ports.indexOf(s.port) < 0) ports.push(s.port);
    ports.forEach(p => {
      const o = document.createElement('option');
      o.value = p;
      o.textContent = (s.ports || []).indexOf(p) < 0 ? p + ' (not connected)' : p;
      sel.appendChild(o);
    });
    sel.value = keep;

    // Firmware
    paintTag($('#fw-tag'), steps[1] ? steps[1].note : '—', s.firmware.verdict.tone);
    paintVerdict($('#fw-verdict'), s.firmware.verdict);
    $('#fw-flash').hidden = !s.firmware.canFlash;
    if (!s.firmware.canFlash) $('#fw-confirm').hidden = true;

    // Check
    paintTag($('#check-tag'), steps[2] ? steps[2].note : '—', s.check.verdict.tone);
    paintVerdict($('#check-verdict'), s.check.verdict);
    const facts = $('#check-facts');
    facts.innerHTML = '';
    (s.check.facts || []).forEach(f => {
      const el = document.createElement('div');
      el.className = 'field';
      el.innerHTML = '<div class="field-label"></div><div class="field-value"></div>';
      el.querySelector('.field-label').textContent = f.label;
      el.querySelector('.field-value').textContent = f.value;
      facts.appendChild(el);
    });

    renderSetupCard(s);
    renderWizardGate();

    // The flow just ended: the rest of WK opens, and every screen reads its state now — nothing
    // past Setup could be asked for while the gate was shut.
    if (was && !was.complete && s.complete) {
      go('home');
      refreshScreens();
      Bridge.call('log.state').then(logAdopt).catch(() => {});
    }
  }

  /** The page header for the step on screen. Re-enters only when the step changes. */
  function paintFlowHead() {
    if (shownStep === wizardStep) return;
    const info = FLOW[wizardStep];
    $('#flow-count').textContent = 'Step ' + wizardStep + ' of 4';
    $('#flow-title').textContent = info.title;
    $('#flow-lead').textContent  = info.lead;
    const head = $('#flow-head');
    head.classList.remove('is-in');
    void head.offsetWidth;                                // restart the animation
    head.classList.add('is-in');
    $('[data-view="setup"]').scrollTop = 0;
    shownStep = wizardStep;
  }

  /**
   * The setup flow. It runs while setup has never been finished, and it is driven by wizardStep
   * rather than by the bot's currentStep: a step whose check happens to pass already is still
   * shown and still needs its own Continue, so nothing is skipped because it was quick this time.
   */
  function renderWizardGate() {
    const s = state && state.setup;
    if (!s) return;

    const active = wizardActive();
    document.body.classList.toggle('is-wizard-active', active);
    $('[data-view="setup"]').classList.toggle('is-wizard', active);
    $('#flow-rail').hidden = !active;
    if (!active) { stopRescan(); return; }

    if (current !== 'setup') go('setup');

    const steps = s.steps || [];
    const isDone = n => n === 4 ? s.currentStep === 4 : !!(steps[n - 1] && steps[n - 1].done);
    const info = FLOW[wizardStep];

    paintFlowHead();

    $$('[data-wizard-step]').forEach(el =>
      el.classList.toggle('is-current-step', parseInt(el.dataset.wizardStep, 10) === wizardStep));

    // The rail: where the user is, what is behind them, and what each step found.
    $$('[data-rail-step]').forEach(el => {
      const n = parseInt(el.dataset.railStep, 10);
      el.classList.toggle('is-current', n === wizardStep);
      el.classList.toggle('is-done', n < wizardStep);
      el.querySelector('.flow-rail-note').textContent =
        n < 4 ? ((steps[n - 1] && steps[n - 1].note) || '') : (isDone(4) ? 'All set' : '');
    });

    if (wizardStep === 4) paintSteps($('#flow-summary'), steps);

    // The footer: what is still missing, or what the step found, and the way forward.
    const ok = isDone(wizardStep);
    const note = wizardStep < 4 && steps[wizardStep - 1] ? steps[wizardStep - 1].note : '';
    $('#flow-status').textContent = ok ? info.done(note) : info.waiting;
    $('#flow-dot').className = 'dot ' + (ok ? 'is-ok' : 'is-warn');
    $('#flow-next-text').textContent = wizardStep === 4 ? 'Finish' : 'Continue';
    $('#flow-next').disabled = !ok;
    $('#flow-back').hidden = wizardStep === 1;

    // Step 1 keeps looking until there is a board.
    if (wizardStep === 1 && !ok) startRescan(); else stopRescan();
  }

  function startRescan() {
    if (rescan) return;
    rescan = setInterval(() => {
      if (!wizardActive() || wizardStep !== 1) { stopRescan(); return; }
      Bridge.call('setup.scan', null, SCAN_MS).then(renderSetup).catch(() => {});
    }, RESCAN_MS);
  }

  function stopRescan() {
    clearInterval(rescan);
    rescan = null;
  }

  /** What entering a step does by itself — the step's own check, so nobody has to press it. */
  function enterStep(n) {
    if (!wizardActive()) return;
    if (n === 1) run($('#board-scan'), 'setup.scan', null, renderSetup, SCAN_MS);
    if (n === 2) run($('#fw-ask'), 'setup.firmware', null, renderSetup, BOARD_MS);
    if (n === 3) run($('#check-run'), 'setup.check', null, renderSetup, BOARD_MS);
  }

  function flash(btn, confirmed) {
    $('#fw-confirm').hidden = true;
    run(btn, 'setup.flash', { confirmed: !!confirmed }, res => renderSetup(res.state), FLASH_MS);
  }

  function wireSetup() {
    $('#board-scan').addEventListener('click', e =>
      run(e.currentTarget, 'setup.scan', null, renderSetup, SCAN_MS));

    $('#board-port').addEventListener('change', e =>
      run(null, 'setup.port', { port: e.target.value }, renderSetup, SCAN_MS));

    $('#fw-ask').addEventListener('click', e =>
      run(e.currentTarget, 'setup.firmware', null, renderSetup, BOARD_MS));

    // The first flash downloads the build tools, and that is asked first — inline, next to the
    // button, because the thing being agreed to should be named where it is agreed to.
    $('#fw-flash').addEventListener('click', e => {
      if (state.setup.firmware.needsDownload) { $('#fw-confirm').hidden = false; return; }
      flash(e.currentTarget, false);
    });
    $('#fw-confirm-cancel').addEventListener('click', () => { $('#fw-confirm').hidden = true; });
    $('#fw-confirm-go').addEventListener('click', () => flash($('#fw-flash'), true));

    $('#check-run').addEventListener('click', e =>
      run(e.currentTarget, 'setup.check', null, renderSetup, BOARD_MS));

    // The flow's way forward. It only moves on from a step whose check has passed — the button is
    // disabled otherwise — and the last step is the one that writes it down.
    $('#flow-next').addEventListener('click', e => {
      if (wizardStep < 4) {
        wizardStep++;
        renderWizardGate();
        enterStep(wizardStep);
        return;
      }
      run(e.currentTarget, 'setup.finish', null, res => {
        if (res.orb) renderOrbCard(res.orb);
        renderSetup(res.state);
      });
    });

    // Back re-asks the step it lands on, as the step's own check would: whatever sent the user
    // back — a cable pulled, another program on the port — is exactly what that check is for.
    $('#flow-back').addEventListener('click', () => {
      if (wizardStep <= 1) return;
      wizardStep--;
      renderWizardGate();
      if (wizardStep < 3) enterStep(wizardStep);
    });

    // The upload narrating itself while it works. An empty line means done.
    Bridge.on('setup.progress', p => {
      const el = $('#fw-progress');
      const text = p && p.text;
      el.hidden = !text;
      if (text) el.querySelector('.progress-text').textContent = text;
    });
  }

  /* ══════════════════════════════════════════════════════════════════════
     Orb Walker

     The cycle as rows, in the order it runs. A key is never typed here: the
     button opens the native capture dialog, and the bot stores what it saw.
     The switch asks; the answer is what the module ended up as, and when it
     refused, the reason is the toast.
     ══════════════════════════════════════════════════════════════════════ */

  /** A key capture waits on a person reading a dialog, not on the bot. */
  const WAITS_ON_PERSON = 10 * 60 * 1000;

  let orb = null;

  function renderOrb(o) {
    if (!o) return;
    orb = o;
    state.orb = o;
    paintSwitch('orb-enabled', o.enabled);

    if (o.enabled)    paintTag($('#orb-tag'), 'Running', 'ok');
    else if (o.ready) paintTag($('#orb-tag'), 'Ready', 'ok');
    else              paintTag($('#orb-tag'), o.problems.length + ' to fix', 'warn');

    ['master', 'hold', 'press'].forEach(slot => {
      const key = o[slot];
      const cap = $('#orb-' + slot);
      cap.textContent = key.isSet ? key.text : 'not set';
      cap.classList.toggle('is-empty', !key.isSet);
      $('[data-slot="' + slot + '"] [data-slot-label]').textContent = key.isSet ? 'Change' : 'Set key';
    });

    options($('#orb-settings [name="button"]'), o.buttons);
    ['delay', 'clickDelay'].forEach(name => {
      const box = $('#orb-settings [name="' + name + '"]');
      box.min = o.delayMin;
      box.max = o.delayMax;
    });
    fill('#orb-settings', { toggle: o.toggle, delay: o.delay, button: o.button, clickDelay: o.clickDelay });

    const note = $('#orb-problems');
    note.hidden = !o.problems.length;
    note.querySelector('span').textContent = o.problems.join('\n');

    renderOrbCard(o);
  }

  function wireOrb() {
    $('#orb-enabled').addEventListener('change', e => {
      run(null, 'orbwalker.enabled', { enabled: e.target.checked }, res => renderOrb(res.state), BOARD_MS)
        .then(() => {
          // On a failed call nothing replied, so the switch goes back to what is known.
          if (orb) paintSwitch('orb-enabled', orb.enabled);
        });
    });

    $$('[data-slot]').forEach(b => b.addEventListener('click', () =>
      run(b, 'orbwalker.key', { slot: b.dataset.slot }, res => renderOrb(res.state), WAITS_ON_PERSON)));

    // Saved as it changes — a number box on the way out of it — and never through an Apply.
    $('#orb-settings').addEventListener('change', e => {
      if (!e.target.name) return;
      run(null, 'orbwalker.save', collect('#orb-settings'), res => renderOrb(res.state));
    });
  }

  /* ══════════════════════════════════════════════════════════════════════
     Settings

     How the bot works rather than what it does. Every card applies as it is
     changed, and nothing on it opens the board by itself: a screen that
     merely loads is not asked to go and open a serial port.
     ══════════════════════════════════════════════════════════════════════ */

  function renderSettings(s) {
    if (!s) return;
    fill('#set-input', {
      keyMin: s.input.keyMin, keyMax: s.input.keyMax,
      clickMin: s.input.clickMin, clickMax: s.input.clickMax
    });
    $$('#set-input input[type="number"]').forEach(el => { el.max = s.input.max; });
    $('#set-board-port').textContent = s.board.port
      ? s.board.port + ' — pinned in Setup'
      : 'Auto-detect — whichever port answers as wk_hid';
  }

  function checkBoard(btn) {
    run(btn, 'settings.board', null, res => {
      if (!res) return;
      $('#set-board-status').textContent = res.text;
      $('#set-board-status').className = 'is-' + (res.connected ? 'ok' : 'warn');
    }, BOARD_MS);
  }

  function wireSettings() {
    $('#set-input').addEventListener('change', () =>
      run(null, 'settings.input', collect('#set-input'), res => renderSettings(res.state)));

    $('#set-board-check').addEventListener('click', e => checkBoard(e.currentTarget));
  }

  /** Fills a select from a list of labels, where the INDEX is the stored value. */
  function options(select, labels) {
    if (select.options.length === (labels || []).length) return;   // already built
    select.innerHTML = '';
    (labels || []).forEach((label, i) => {
      const o = document.createElement('option');
      o.value = i;
      o.textContent = label;
      select.appendChild(o);
    });
  }

  function fill(hostSel, values) {
    const host = $(hostSel);
    Object.keys(values).forEach(name => {
      const el = host.querySelector('[name="' + name + '"]');
      if (!el) return;
      if (el.type === 'checkbox') el.checked = !!values[name];
      else el.value = values[name];
    });
  }

  /** Reads a card back out. Checkboxes as booleans, everything else as a number or text. */
  function collect(hostSel) {
    const out = {};
    $$(hostSel + ' [name]').forEach(el => {
      if (el.type === 'checkbox') out[el.name] = el.checked;
      else if (el.type === 'number' || el.tagName === 'SELECT') out[el.name] = parseFloat(el.value);
      else out[el.name] = el.value;
    });
    return out;
  }

  /* ══════════════════════════════════════════════════════════════════════════
     Log — the console.

     WHAT IT IS. A live view of Core.Logger. The bot writes every line to a ring
     in memory and to this session's file before anything here sees it, so this
     screen can be cleared, filtered, closed or broken without a line being lost.
     That is what lets it be aggressive about what it draws.

     ONE ELEMENT, ONE TEXT NODE, PER LINE. No spans inside a line, no badge for
     the level, no markup in the message. It keeps a thousand lines cheap to
     build and to scroll, and — the part that matters when something has gone
     wrong — it means a selection dragged across the console comes out of the
     clipboard exactly as it went in.

     THREE BUFFERS, AND THEY ARE NOT THE SAME SIZE. The bot's ring holds 4000
     lines; this page holds LOG_KEEP of them so filtering and copying can reach
     past what is drawn; the DOM holds LOG_DRAW, because nothing is gained by a
     browser laying out five thousand lines nobody has scrolled to.

     NOTHING IS DRAWN WHILE THE SCREEN IS SHUT. Every view stays in the DOM here,
     so a console left appending in the background would go on costing layout on
     every batch for a screen nobody is looking at. Lines are buffered either
     way and the whole thing is rebuilt when it opens.
     ══════════════════════════════════════════════════════════════════════════ */

  // Core.LogLevel's own numbering, and the tags the session file writes, so a
  // line copied off this screen reads like a line out of that file.
  const LOG_TAG   = ['INFO', 'WARN', 'ERR ', 'TRC '];
  const LOG_CLASS = ['is-info', 'is-warn', 'is-error', 'is-trace'];

  const LOG_KEEP = 5000;    // lines the page holds
  const LOG_DRAW = 1200;    // lines on screen at once

  const log = {
    rows:    [],                          // every line held, oldest first
    counts:  [0, 0, 0, 0],
    levels:  [true, true, true, false],   // trace off, like the bot's own console
    sources: [],
    source:  '',
    text:    '',
    follow:  true,
    wrap:    false,
    active:  false,
    shown:   0,
    missed:  0,
    dropped: 0,
    file:    null
  };

  const logPad = (s, n) => (s = s || '—', s.length >= n ? s : s + ' '.repeat(n - s.length));

  /**
   * One line, as one string. The columns are padded rather than laid out,
   * because a monospace block under white-space:pre lines up for free and a
   * table of five thousand rows does not.
   */
  function logText(r) {
    return r.time + '  ' + LOG_TAG[r.level] + '  ' + logPad(r.source, 11) + '  ' +
           String(r.text == null ? '' : r.text).replace(/\r\n?/g, '\n');
  }

  function logMatches(r) {
    if (!log.levels[r.level]) return false;
    if (log.source && r.source !== log.source) return false;
    if (log.text && (r.source + ' ' + r.text).toLowerCase().indexOf(log.text) < 0) return false;
    return true;
  }

  function logNode(r) {
    const el = document.createElement('div');
    el.className = 'log-line ' + LOG_CLASS[r.level];
    el.textContent = logText(r);
    return el;
  }

  function logAtEnd() {
    const out = $('#log-out');
    return out.scrollHeight - out.scrollTop - out.clientHeight < 24;
  }

  /**
   * Emptying the box resets scrollTop, and the scroll event for it lands a few
   * milliseconds LATER — after any flag set around the assignment has been
   * cleared. So "that one was ours" is a short window rather than a flag, and it
   * is opened only by a rebuild: the tail needs no protection, because a scroll
   * to the bottom moves DOWN and only an upward one is read as leaving.
   */
  let logQuiet   = 0;      // scroll events are ignored until this moment
  let logLastTop = 0;

  function logHush() { logQuiet = Date.now() + 200; }

  function logToEnd() {
    const out = $('#log-out');
    out.scrollTop = out.scrollHeight;
  }

  /** Redraws everything the filters allow. Called when the screen opens and on every filter change. */
  function logRebuild() {
    const out = $('#log-out');
    logHush();
    out.textContent = '';

    const hits = log.rows.filter(logMatches);
    log.shown = hits.length;

    if (!hits.length) {
      const empty = document.createElement('div');
      empty.className = 'log-empty';
      empty.textContent = log.rows.length
        ? 'No line matches these filters.'
        : 'Nothing has been logged yet.';
      out.appendChild(empty);
    } else {
      const frag = document.createDocumentFragment();
      hits.slice(-LOG_DRAW).forEach(r => frag.appendChild(logNode(r)));
      out.appendChild(frag);
    }

    logPaintFoot();
    if (log.follow) logToEnd();
  }

  /** Appends lines that are already known to pass the filters. */
  function logDraw(hits) {
    const out   = $('#log-out');
    const empty = out.querySelector('.log-empty');
    if (empty) empty.remove();

    const frag = document.createDocumentFragment();
    hits.forEach(r => frag.appendChild(logNode(r)));
    out.appendChild(frag);

    // Past the cap the oldest nodes go. While the view is NOT following, the
    // scroll position is corrected by exactly what was removed — otherwise
    // reading the middle of the console would be dragged upwards by lines
    // arriving at the bottom, which is the one thing a paused view must not do.
    let extra = out.childElementCount - LOG_DRAW;
    if (extra > 0) {
      const before = out.scrollHeight;
      while (extra-- > 0 && out.firstElementChild) out.removeChild(out.firstElementChild);
      if (!log.follow) out.scrollTop = Math.max(0, out.scrollTop - (before - out.scrollHeight));
    }

    if (log.follow) logToEnd();
  }

  /**
   * A batch from the bot. Buffered whatever screen is open; drawn only if this
   * one is. Lines are deduplicated by sequence, so a snapshot that arrives after
   * a push — turning the trace on does exactly that — cannot print twice.
   */
  function logPush(batch) {
    if (!batch) return;
    if (batch.dropped) log.dropped += batch.dropped;
    // Only ever present on the batch after it changed: the session file is opened
    // a moment after the bridge is built, so the first snapshot can be too early
    // to know it. See LogFeed.Flush.
    if (batch.file) log.file = batch.file;

    const last = log.rows.length ? log.rows[log.rows.length - 1].seq : 0;
    const rows = (batch.rows || []).filter(r => r.seq > last);
    if (!rows.length && !batch.dropped) return;

    rows.forEach(r => {
      log.rows.push(r);
      log.counts[r.level]++;
      logSource(r.source);
    });

    const hits = rows.filter(logMatches);
    log.shown += hits.length;
    logTrim();

    if (log.active && hits.length) logDraw(hits);
    if (hits.length && (!log.active || !log.follow)) log.missed += hits.length;

    logPaintCounts();
    logPaintJump();
    logPaintFoot();
  }

  /** Drops the oldest lines once the page is full. They are still in the session file. */
  function logTrim() {
    const over = log.rows.length - LOG_KEEP;
    if (over <= 0) return;
    log.rows.splice(0, over).forEach(r => {
      log.counts[r.level]--;
      if (logMatches(r)) log.shown--;
    });
  }

  /**
   * Replaces everything with a snapshot from the bot — the open, a clear, a
   * trace switch.
   *
   * Lines pushed WHILE the snapshot was in flight are kept: the ring was read
   * before they were written, so dropping them would leave a hole that nothing
   * ever fills in. Clearing empties the buffer itself first, because there the
   * whole point is that what came before goes.
   */
  function logAdopt(s) {
    if (!s) return;
    const rows = s.rows || [];
    const top  = rows.length ? rows[rows.length - 1].seq : 0;

    log.rows    = rows.concat(log.rows.filter(r => r.seq > top));
    log.file    = s.file || null;
    log.dropped = s.dropped || 0;
    log.levels[3] = !!s.trace;

    log.counts  = [0, 0, 0, 0];
    log.sources = [];
    log.rows.forEach(r => {
      log.counts[r.level]++;
      if (r.source && log.sources.indexOf(r.source) < 0) log.sources.push(r.source);
    });
    log.sources.sort();

    logPaintSources();
    logPaintCounts();
    if (log.active) {
      logRebuild();
    } else {
      log.shown = log.rows.filter(logMatches).length;
      logPaintFoot();
    }
    logPaintJump();
  }

  /** Opening and leaving the screen. See the header on why nothing is drawn while it is shut. */
  function logActive(on) {
    if (log.active === on) return;
    log.active = on;
    if (!on) return;
    if (log.follow) log.missed = 0;
    logRebuild();
    logPaintJump();
  }

  function logSource(name) {
    if (!name || log.sources.indexOf(name) >= 0) return;
    log.sources.push(name);
    log.sources.sort();
    logPaintSources();
  }

  function logPaintSources() {
    const sel  = $('#log-source');
    const keep = log.source;
    sel.textContent = '';

    const all = document.createElement('option');
    all.value = '';
    all.textContent = 'Every module';
    sel.appendChild(all);

    log.sources.forEach(name => {
      const o = document.createElement('option');
      o.value = name;
      o.textContent = name;
      sel.appendChild(o);
    });

    // A chosen module that is no longer in the list — after a clear — leaves the
    // box on "every" rather than filtering against a name nothing can match.
    sel.value = keep;
    if (sel.value !== keep) log.source = '';
  }

  /** 1240 → "1.2k". A five-figure count on a button is a number nobody reads. */
  function logShort(n) {
    return n < 1000 ? String(n) : (n / 1000).toFixed(n < 10000 ? 1 : 0) + 'k';
  }

  function logPaintCounts() {
    $$('#log-levels .seg-btn').forEach(b => {
      const lvl = +b.dataset.level;
      b.querySelector('b').textContent = logShort(log.counts[lvl]);
      b.classList.toggle('is-on', log.levels[lvl]);
    });
  }

  function logPaintJump() {
    const on = log.active && !log.follow && log.missed > 0;
    $('#log-jump').hidden = !on;
    if (on) $('#log-jump-text').textContent =
      log.missed === 1 ? '1 new line' : log.missed.toLocaleString() + ' new lines';
  }

  function logPaintFoot() {
    const total = log.rows.length;
    let text = log.shown === total
      ? total.toLocaleString() + (total === 1 ? ' line' : ' lines')
      : log.shown.toLocaleString() + ' of ' + total.toLocaleString() + ' lines';
    if (total >= LOG_KEEP) text += ' · older ones are in the session file';
    if (log.dropped) text += ' · ' + log.dropped.toLocaleString() + ' missed while the page was busy';
    $('#log-count').textContent = text;

    const file = $('#log-file');
    file.textContent = log.file || 'No session file';
    file.title       = log.file || '';
  }

  /** What copy and save hand over: everything the filters allow, not only what is drawn. */
  function logSelection() {
    return log.rows.filter(logMatches).map(logText).join('\r\n');
  }

  function logFollow(on) {
    if (log.follow === on) { if (on) logToEnd(); return; }
    log.follow = on;
    $('#log-follow').classList.toggle('is-on', on);
    $('#log-follow').title = on ? 'Following new lines' : 'Follow new lines';
    if (on) { log.missed = 0; logToEnd(); }
    logPaintJump();
  }

  function wireLog() {
    $$('#log-levels .seg-btn').forEach(b => b.addEventListener('click', e => {
      const lvl  = +b.dataset.level;
      const want = !log.levels[lvl];

      // THE TRACE IS SWITCHED AT THE SOURCE. Asking for it tells the bot to start
      // forwarding it, and the reply carries what the ring kept while nobody was
      // asking — so turning it on fills the screen with the ticks that led up to
      // now instead of starting at the next one. See LogFeed.
      if (lvl === 3) {
        run(e.currentTarget, 'log.trace', { on: want }, logAdopt);
        return;
      }

      log.levels[lvl] = want;
      logPaintCounts();
      if (log.active) logRebuild();
    }));

    let typed = null;
    $('#log-search').addEventListener('input', e => {
      const value = e.target.value.trim().toLowerCase();
      clearTimeout(typed);
      // Debounced: rebuilding on every keystroke is a thousand nodes per letter.
      typed = setTimeout(() => {
        log.text = value;
        if (log.active) logRebuild();
      }, 120);
    });
    $('#log-search').addEventListener('keydown', e => {
      if (e.key !== 'Escape') return;
      e.target.value = '';
      log.text = '';
      if (log.active) logRebuild();
    });

    $('#log-source').addEventListener('change', e => {
      log.source = e.target.value;
      if (log.active) logRebuild();
    });

    $('#log-follow').addEventListener('click', () => logFollow(!log.follow));
    $('#log-jump').addEventListener('click', () => logFollow(true));

    $('#log-wrap').addEventListener('click', e => {
      log.wrap = !log.wrap;
      $('#log-out').classList.toggle('is-wrap', log.wrap);
      e.currentTarget.classList.toggle('is-on', log.wrap);
      if (log.follow) logToEnd();
    });

    // FOLLOWING IS WHERE THE VIEW IS, not a mode to fight with: scrolling up
    // stops it — that is what scrolling up means — and scrolling back to the
    // bottom starts it again without anything being clicked.
    //
    // A scroll EVENT cannot say who caused it: the tail, a rebuild, a trim and
    // the window being resized all raise one, and every early version of this
    // turned following off by itself while the user watched. Two rules settle
    // it. Only a scroll that moved UP is read as leaving the tail — the tail
    // itself moves down, and a resize moves nothing, it changes the height under
    // a fixed scrollTop. And a rebuild, which resets scrollTop to the top a
    // moment after it empties the box, is hushed for as long as that takes.
    $('#log-out').addEventListener('scroll', () => {
      const top = $('#log-out').scrollTop;
      const up  = top < logLastTop - 1;
      logLastTop = top;
      if (Date.now() < logQuiet) return;
      if (up) logFollow(false);
      else if (logAtEnd()) logFollow(true);
    });

    // A window that got shorter leaves the last line above the fold with nothing
    // to say so — no scroll, no click, and a console that quietly stopped
    // following. Re-tailed here, and only when it was following to begin with.
    window.addEventListener('resize', () => {
      if (log.active && log.follow) logToEnd();
    });

    $('#log-copy').addEventListener('click', e =>
      run(e.currentTarget, 'log.copy', { text: logSelection(), lines: log.shown }));

    $('#log-save').addEventListener('click', e =>
      run(e.currentTarget, 'log.save', { text: logSelection(), lines: log.shown }));

    $('#log-folder').addEventListener('click', e => run(e.currentTarget, 'log.open'));

    // Clearing is the page AND the bot's floor for it, in one call: without the
    // second half, the next snapshot would bring back everything just cleared.
    $('#log-clear').addEventListener('click', e =>
      run(e.currentTarget, 'log.clear', null, res => {
        log.missed = 0;
        log.rows   = [];              // see logAdopt: the only place the tail is not kept
        logAdopt(res && res.state);
      }));
  }

  // ── Actions ─────────────────────────────────────────────────────────────

  /**
   * One shape for every user action: busy the control, toast the outcome.
   *
   * A REPLY MAY CARRY ITS OWN TONE. Most messages are "that worked", but some are a refusal the
   * bot answers successfully — a switch that would not turn on. Those arrive with a kind, because
   * a warning painted in the success colour is read as approval.
   */
  async function run(btn, cmd, data, onOk, timeout) {
    try {
      const res = await FX.busy(btn, Bridge.call(cmd, data, timeout));
      if (res && res.message) FX.toast(res.message, res.kind || 'ok');
      if (onOk) onOk(res);
    } catch (err) {
      FX.toast(err.message, 'danger');
    }
  }

  /**
   * Theme. Light is the product's default and stays the fallback everywhere:
   * nothing here reads prefers-color-scheme, because the choice is the user's
   * and not the operating system's.
   */
  function wireTheme() {
    const btn  = $('#theme-toggle');
    const icon = $('#theme-icon').querySelector('use');

    const paint = dark => {
      icon.setAttribute('href', dark ? '#i-sun' : '#i-moon');
      btn.title = dark ? 'Switch to light theme' : 'Switch to dark theme';
    };
    paint(document.documentElement.dataset.theme === 'dark');

    btn.addEventListener('click', () => {
      const dark = document.documentElement.dataset.theme !== 'dark';
      if (dark) document.documentElement.dataset.theme = 'dark';
      else      delete document.documentElement.dataset.theme;
      paint(dark);
      try { localStorage.setItem('wk.theme', dark ? 'dark' : 'light'); } catch (e) { /* not fatal */ }
    });
  }

  function wire() {
    wireTheme();
    wireSetup();
    wireOrb();
    wireSettings();
    wireLog();

    // A card that reports a value owned by another screen sends the user there rather than
    // offering a second place to edit it. One home per setting, and a signpost to it.
    $$('[data-go]').forEach(b => b.addEventListener('click', () => go(b.dataset.go)));

    // Closing the bot: the window's X, relayed by the shell as a question for this page.
    ExitDialog.wire();
    Bridge.on('app.close', ExitDialog.open);

    // Pushes from the engine. These arrive unprompted and must never disturb
    // whatever the user is doing — they repaint a label, nothing more.
    Bridge.on('engine', renderEngine);
    Bridge.on('toast',  t => FX.toast(t.message, t.kind || 'info'));

    // The console, in batches. It buffers whatever screen is open — see logPush.
    Bridge.on('log', logPush);
  }

  /**
   * Every screen that reads its own slice of the settings, and how to refill it. Read once the
   * gate is open — every command past Setup is refused before that.
   */
  const SCREENS = [
    { cmd: 'orbwalker.state', label: 'Orb Walker', render: s => renderOrb(s) },
    { cmd: 'settings.state',  label: 'settings',   render: s => renderSettings(s) }
  ];

  async function refreshScreens() {
    for (const screen of SCREENS) {
      try {
        screen.render(await Bridge.call(screen.cmd));
      } catch (err) {
        // One screen failing is not the others' problem.
        FX.toast('Could not read the ' + screen.label + ': ' + err.message, 'warn');
      }
    }
  }

  // ── Boot ────────────────────────────────────────────────────────────────

  async function boot() {
    buildNav();
    wire();

    // The cards are already on screen as skeletons; this fills them. A failure
    // here leaves the shell usable and says so, rather than a blank window.
    try {
      const booted = await Bridge.call('app.bootstrap');
      state = booted;
      $('#build-tag').textContent = (booted.build || 'dev').toUpperCase();
      renderEngine(booted.engine || {});
      renderOrbCard(booted.orb);

      // Setup first: when it has never been finished this is where the flow takes over the
      // window, and the first step starts looking for the board by itself. Taken out of the state
      // before it is drawn, because renderSetup reads what was there to tell the flow ending
      // apart from a flow that never ran.
      const setup = booted.setup;
      state.setup = null;
      renderSetup(setup);
      if (wizardActive()) {
        enterStep(1);
        return;
      }

      // Each screen's own slice, then the console. The log belongs to the session rather than
      // to the settings, and the push keeps it current from here on.
      await refreshScreens();
      try {
        logAdopt(await Bridge.call('log.state'));
      } catch (err) {
        FX.toast('Could not read the log: ' + err.message, 'warn');
      }
    } catch (err) {
      FX.toast('Could not read the bot state: ' + err.message, 'danger');
      $$('.skel').forEach(el => el.classList.remove('skel'));
      $('#run-text').textContent = 'Not connected';
      $('#run-dot').className    = 'dot is-danger';
    }
  }

  return { boot };
})();

document.addEventListener('DOMContentLoaded', App.boot);
