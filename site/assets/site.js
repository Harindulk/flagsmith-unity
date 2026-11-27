// Flagsmith for Unity site: the playground's flag state, the controls that edit it, and small page behaviours.
(() => {
  const $ = (s, el = document) => el.querySelector(s);
  const $$ = (s, el = document) => [...el.querySelectorAll(s)];

  // ---- Playground: play/index.html reads window.playground to answer the game's Flagsmith requests.
  const flags = { run_speed: 7, jump_force: 13, gravity_scale: 3.2, coin_value: 1, player_color: '#FFB020', double_jump: false, spike_walls: true };
  let requests = 0;
  const feed = $('#feed');
  window.playground = {
    flags,
    onRequest() { requests++; const p = $('#polls'); if (p) p.textContent = requests + (requests === 1 ? ' request' : ' requests') + ' answered'; },
    onEvents(events) {
      if (!feed) return;
      if (feed.dataset.empty !== 'no') { feed.innerHTML = ''; feed.dataset.empty = 'no'; }
      for (const e of events) {
        const li = document.createElement('li');
        const name = document.createElement('b'); name.textContent = e.event;
        const detail = document.createElement('span');
        detail.textContent = e.event === '$flag_exposure' ? (e.feature_name + ': ' + e.value) : (e.value != null ? e.value + ' m' : '');
        li.append(name, detail);
        feed.prepend(li);
      }
      while (feed.children.length > 4) feed.lastChild.remove();
    },
  };

  const flash = name => { const r = $(`.row[data-flag="${name}"]`); if (!r) return; r.classList.remove('changed'); void r.offsetWidth; r.classList.add('changed'); };
  const set = (name, value) => { flags[name] = value; flash(name); };

  const range = (id, out, name) => {
    const el = $(id); if (!el) return;
    el.addEventListener('input', () => { $(out).textContent = el.value; });
    el.addEventListener('change', () => set(name, Number(el.value)));
  };
  range('#f-run', '#o-run', 'run_speed');
  range('#f-grav', '#o-grav', 'gravity_scale');

  $$('.swatches button').forEach(b => b.addEventListener('click', () => {
    $$('.swatches button').forEach(o => o.setAttribute('aria-pressed', String(o === b)));
    set('player_color', b.dataset.color);
  }));
  $$('.seg button').forEach(b => b.addEventListener('click', () => {
    $$('.seg button').forEach(o => o.setAttribute('aria-pressed', String(o === b)));
    set('coin_value', Number(b.dataset.coin));
  }));

  // Every switch for one flag stays in step (the panel's spike_walls and the big kill switch).
  const renderSwitch = name => {
    $$(`[data-switch="${name}"]`).forEach(s => s.setAttribute('aria-checked', String(flags[name])));
    if (name === 'spike_walls') {
      const kill = $('#kill'); if (!kill) return;
      kill.classList.toggle('off', !flags.spike_walls);
      $('#kill-word').textContent = flags.spike_walls ? 'on' : 'off';
      $('#kill-state').textContent = flags.spike_walls ? 'enabled' : 'disabled';
    }
  };
  $$('[data-switch]').forEach(s => s.addEventListener('click', () => {
    const name = s.dataset.switch;
    set(name, !flags[name]);
    renderSwitch(name);
  }));

  const load = $('#load-game');
  if (load) load.addEventListener('click', () => {
    const frame = document.createElement('iframe');
    frame.src = 'play/?autoplay';
    frame.title = 'Flag Runner, a Unity WebGL game using Flagsmith for Unity';
    frame.allow = 'fullscreen';
    $('#start').replaceWith(frame);
    frame.addEventListener('load', () => frame.focus());
  });

  // ---- How it works: one slider drives the clause and the fragment.
  const stages = [
    ['Someone edits a flag', 'In the Flagsmith dashboard, or in Window > Flagsmith inside Unity, run_speed goes from 7 to 11.'],
    ['The game asks again', 'FlagsmithBehaviour fetches this player\'s flags on a timer: every 2 seconds while you tune, every 60 in release.'],
    ['The field follows the flag', 'Bind sets every [FlagsmithValue] member, then FlagsChanged fires once, only if something really changed.'],
    ['The player feels it', 'The next jump uses the new value. No rebuild, no restart, no store review.'],
  ];
  const stage = $('#stage');
  if (stage) {
    const show = () => {
      const i = Number(stage.value);
      $('#st-h').textContent = stages[i][0];
      $('#st-p').textContent = stages[i][1];
      $$('.frag').forEach(f => { f.hidden = Number(f.dataset.stage) !== i; });
      $$('#stage-names li').forEach((li, j) => li.classList.toggle('on', j === i));
      stage.style.setProperty('--fill', (i / 3 * 100) + '%');
      stage.setAttribute('aria-valuetext', stages[i][0]);
    };
    stage.addEventListener('input', show);
    $$('#stage-names li').forEach((li, j) => li.addEventListener('click', () => { stage.value = j; show(); }));
    show();
  }

  // ---- Tabs (arrow keys move between them).
  const tabs = $$('[role=tab]');
  const select = t => {
    tabs.forEach(o => { const on = o === t; o.setAttribute('aria-selected', String(on)); o.tabIndex = on ? 0 : -1; $('#' + o.getAttribute('aria-controls')).hidden = !on; });
  };
  tabs.forEach((t, i) => {
    t.addEventListener('click', () => select(t));
    t.addEventListener('keydown', e => {
      const d = { ArrowDown: 1, ArrowRight: 1, ArrowUp: -1, ArrowLeft: -1 }[e.key];
      if (!d) return;
      e.preventDefault();
      const n = tabs[(i + d + tabs.length) % tabs.length]; select(n); n.focus();
    });
  });

  // ---- Copy buttons.
  const copy = async (text, btn) => {
    try { await navigator.clipboard.writeText(text); btn.textContent = 'Copied'; } catch { btn.textContent = 'Select and copy'; }
    setTimeout(() => { btn.textContent = 'Copy'; }, 1600);
  };
  const cu = $('#copy-url'); if (cu) cu.addEventListener('click', () => copy($('#git-url').value, cu));
  $$('.doc pre').forEach(pre => {
    const wrap = document.createElement('div'); wrap.className = 'copy';
    pre.replaceWith(wrap); wrap.append(pre);
    const b = document.createElement('button'); b.type = 'button'; b.textContent = 'Copy';
    b.addEventListener('click', () => copy(pre.innerText, b));
    wrap.append(b);
  });

  // ---- Docs menu on phones.
  const mb = $('.menu-btn');
  if (mb) mb.addEventListener('click', () => { const n = $('.docs-nav'); const open = n.classList.toggle('open'); mb.setAttribute('aria-expanded', String(open)); });

  // ---- Section entrances.
  const io = 'IntersectionObserver' in window ? new IntersectionObserver(es => es.forEach(e => {
    if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); }
  }), { rootMargin: '0px 0px -10% 0px' }) : null;
  $$('.reveal').forEach(el => io ? io.observe(el) : el.classList.add('in'));
})();
