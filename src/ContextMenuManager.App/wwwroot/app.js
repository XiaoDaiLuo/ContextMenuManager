(() => {
  'use strict';

  // ---------- 基础工具 ----------
  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const svg = name => `<svg><use href="#i-${name}"/></svg>`;
  const cmp = new Intl.Collator('zh-CN', { numeric: true, sensitivity: 'base' }).compare;

  // ---------- 与宿主通信 ----------
  // 在 WebView2 里走 postMessage；用浏览器打开预览服务时走 HTTP。
  const rpc = (() => {
    const wv = window.chrome && window.chrome.webview;
    if (wv) {
      let seq = 0;
      const waiting = new Map();
      wv.addEventListener('message', e => {
        const m = e.data;
        const w = waiting.get(m.id);
        if (!w) return;
        waiting.delete(m.id);
        m.ok ? w.resolve(m.result) : w.reject(new Error(m.error || '调用失败'));
      });
      return (method, args) => new Promise((resolve, reject) => {
        const id = ++seq;
        waiting.set(id, { resolve, reject });
        wv.postMessage({ id, method, args: args || {} });
      });
    }
    return async (method, args) => {
      const r = await fetch('/rpc', { method: 'POST', body: JSON.stringify({ method, args: args || {} }) });
      const j = await r.json();
      if (!j.ok) throw new Error(j.error || '调用失败');
      return j.result;
    };
  })();

  // ---------- 常量 ----------
  const SCENES = [
    { id: 'file', label: '文件', icon: 'file', desc: '右键单击文件时出现的菜单项。' },
    { id: 'folder', label: '文件夹', icon: 'folder', desc: '右键单击文件夹时出现的菜单项。' },
    { id: 'background', label: '桌面与空白处', icon: 'monitor', desc: '右键单击桌面或文件夹空白处时出现的菜单项。' },
    { id: 'drive', label: '磁盘', icon: 'drive', desc: '右键单击磁盘时出现的菜单项。' },
    { id: 'win11', label: 'Win11 新菜单', icon: 'windows', desc: 'Windows 11 新式右键菜单里由应用添加的项。禁用后只对当前用户生效。' },
    { id: 'filetype', label: '指定文件类型', icon: 'filetype', desc: '只对某一种扩展名生效的菜单项。' },
    { id: 'sendto', label: '发送到', icon: 'send', desc: '“发送到”子菜单里的目标。' },
    { id: 'shellnew', label: '新建', icon: 'plus', desc: '“新建”子菜单里的文件类型。' },
    { id: 'dragdrop', label: '拖拽菜单', icon: 'move', desc: '按住右键拖放文件后出现的菜单项。' },
    { id: 'other', label: '其他位置', icon: 'grid', desc: '快捷方式、程序文件、库、此电脑和回收站上的菜单项。' },
  ];
  const KIND = {
    Verb: ['静态项', '写在注册表 shell 下的固定命令'],
    ShellEx: ['扩展', '由软件的 DLL 动态生成的菜单'],
    DragDrop: ['拖拽扩展', '右键拖放时由软件的 DLL 生成的菜单'],
    Packaged: ['应用声明', '由应用包声明的 Win11 新式菜单项'],
    SendTo: ['发送到', '“发送到”目录里的一个文件'],
    ShellNew: ['新建', '“新建”子菜单里的一种文件类型'],
  };
  const ACTION = { Disable: '禁用', Enable: '启用', Delete: '删除' };
  const FLAG = {
    extended: ['按住 Shift 才显示', ''],
    submenu: ['含子菜单', ''],
    unresolved: ['组件缺失', 'warn'],
    machineBlocked: ['被系统策略阻止', 'warn'],
    userBlocked: ['被 Blocked 列表阻止', 'warn'],
    staleMirror: ['有旧的禁用副本', ''],
  };

  // ---------- 状态 ----------
  const state = {
    info: { allowSystem: false, sandbox: false, readOnly: false, elevated: false, shellActions: false, dataDir: '', version: '' },
    items: [],
    fileTypeItems: [],
    ext: '',
    scene: 'file',
    view: 'list',
    query: '',
    filter: 'all',
    showSystem: load('showSystem', false),
    selected: new Set(),
    pending: new Map(),
    openId: null,
    visibleIds: [],
    lastCheckedId: null,
    loading: true,
    restartSuggested: false,
    scanSeq: 0,
    icons: new Map(),
  };

  function load(key, fallback) {
    try { const v = localStorage.getItem('cmm.' + key); return v === null ? fallback : JSON.parse(v); } catch { return fallback; }
  }
  function save(key, value) {
    try { localStorage.setItem('cmm.' + key, JSON.stringify(value)); } catch { /* 存不了就算了 */ }
  }

  const byId = id => state.items.find(i => i.id === id) || state.fileTypeItems.find(i => i.id === id);
  const isLocked = item => item.isProtected || (item.isSystem && !state.info.allowSystem);
  // 把被禁用的扩展或新建项恢复回去不受锁定限制；静态项不算（系统里有些动词本来就是隐藏的）
  const canRestore = item => !item.enabled && ['ShellEx', 'DragDrop', 'ShellNew'].includes(item.kind);
  const lockedFor = (item, want) => isLocked(item) && !(want === 'on' && canRestore(item));
  const effectiveEnabled = item => {
    const p = state.pending.get(item.id);
    return p === 'Disable' ? false : p === 'Enable' ? true : item.enabled;
  };
  const sceneItems = id => id === 'filetype' ? state.fileTypeItems : state.items.filter(i => i.scenes.includes(id));
  const shown = items => state.showSystem ? items : items.filter(i => !i.isSystem);

  function visibleItems() {
    let items = shown(sceneItems(state.scene));
    // 按当前的真实状态筛选：拨了开关但还没应用的项留在原来的筛选结果里，不会突然从列表里消失
    if (state.filter !== 'all') items = items.filter(i => i.enabled === (state.filter === 'enabled'));
    const q = state.query.trim().toLowerCase();
    if (q) {
      items = items.filter(i => [i.title, i.appName, i.publisher, i.keyName, i.registryPath, i.command, i.filePath, i.clsid]
        .some(v => v && String(v).toLowerCase().includes(q)));
    }
    return items;
  }

  // ---------- 侧栏 ----------
  function renderNav() {
    const nav = $('#nav');
    const indicator = $('#navIndicator');
    nav.innerHTML = '';
    nav.appendChild(indicator);
    for (const s of SCENES) {
      nav.insertAdjacentHTML('beforeend',
        `<button class="nav-item" type="button" data-scene="${s.id}">${svg(s.icon)}<span>${esc(s.label)}</span><em></em></button>`);
    }
    nav.insertAdjacentHTML('beforeend', '<div class="nav-sep"></div>');
    nav.insertAdjacentHTML('beforeend',
      `<button class="nav-item" type="button" data-view="history">${svg('history')}<span>历史与回滚</span><em></em></button>`);
    nav.addEventListener('click', e => {
      const b = e.target.closest('.nav-item');
      if (!b) return;
      if (b.dataset.view === 'history') showHistory();
      else showScene(b.dataset.scene);
    });
  }

  function updateNav() {
    for (const b of $$('#nav .nav-item')) {
      const on = state.view === 'history' ? b.dataset.view === 'history' : b.dataset.scene === state.scene;
      b.classList.toggle('on', on);
      if (on) {
        const ind = $('#navIndicator');
        ind.style.transform = `translateY(${b.offsetTop}px)`;
        ind.style.height = b.offsetHeight + 'px';
        ind.style.opacity = 1;
      }
      if (!b.dataset.scene) continue;
      const em = $('em', b);
      const n = b.dataset.scene === 'filetype' && !state.ext ? '' : String(shown(sceneItems(b.dataset.scene)).length);
      if (em.textContent !== n) {
        const had = em.textContent !== '';
        em.textContent = n;
        if (had && !state.loading) { em.classList.remove('bump'); void em.offsetWidth; em.classList.add('bump'); }
      }
    }
  }

  // ---------- 列表 ----------
  function avatarColor(text) {
    let h = 0;
    for (const ch of text) h = (h * 31 + ch.codePointAt(0)) % 360;
    return `hsl(${h} 42% 52%)`;
  }

  function iconHtml(item) {
    const uri = item.iconSource && state.icons.get(item.iconSource);
    const name = item.appName || item.title || '?';
    if (uri) return `<span class="icon has-img" data-src="${esc(item.iconSource)}"><img src="${uri}" alt=""></span>`;
    return `<span class="icon" data-src="${esc(item.iconSource || '')}" style="--avatar:${avatarColor(name)}">${esc([...name.trim()][0] || '?').toUpperCase()}</span>`;
  }

  function tagsHtml(item) {
    const out = [];
    const p = state.pending.get(item.id);
    if (p) out.push(`<span class="tag pend ${p === 'Delete' ? 'del' : ''}" data-act="unpend" title="点击取消这项更改">将${ACTION[p]} ×</span>`);
    if (item.isSystem) out.push(`<span class="tag sys" title="${item.isProtected ? '系统核心项，不允许改动' : 'Windows 自带项'}">${svg('lock')}${item.isProtected ? '核心' : '系统'}</span>`);
    out.push(`<span class="tag" title="${esc(KIND[item.kind][1])}">${KIND[item.kind][0]}</span>`);
    if (item.hive === 'HKCU' && item.kind !== 'Packaged') out.push('<span class="tag" title="只写在当前用户的注册表里">仅当前用户</span>');
    for (const f of item.flags) {
      if (!FLAG[f] || (f === 'userBlocked' && item.kind === 'Packaged')) continue;
      out.push(`<span class="tag ${FLAG[f][1]}">${FLAG[f][0]}</span>`);
    }
    if (item.fileTypes && item.fileTypes.length) out.push(`<span class="tag" title="${esc(item.fileTypes.join('  '))}">${item.fileTypes.length} 种类型</span>`);
    return out.join('');
  }

  function subText(item) {
    const parts = [];
    if (item.appName && item.appName !== item.title) parts.push(item.appName);
    if (item.publisher && item.publisher !== item.appName) parts.push(item.publisher);
    if (!parts.length) parts.push(item.kind === 'Packaged' ? '应用包' : item.keyName.trim());
    return parts.join(' · ');
  }

  function rowClass(item) {
    const p = state.pending.get(item.id);
    return ['row',
      effectiveEnabled(item) ? '' : 'off',
      p ? 'pending' : '',
      p === 'Delete' ? 'del' : '',
      state.selected.has(item.id) ? 'sel' : '',
      state.openId === item.id ? 'open' : '',
    ].filter(Boolean).join(' ');
  }

  function stateText(item) {
    return state.pending.get(item.id) === 'Delete' ? '删除' : effectiveEnabled(item) ? '已启用' : '已禁用';
  }

  function rowHtml(item, i) {
    const locked = isLocked(item) && !canRestore(item);
    return `<div class="${rowClass(item)}" data-id="${esc(item.id)}" style="--i:${Math.min(i, 14)}">
      <div class="row-main">
        <label class="cbx"><input type="checkbox" ${state.selected.has(item.id) ? 'checked' : ''} aria-label="选择 ${esc(item.title)}"><span></span></label>
        ${iconHtml(item)}
        <div class="row-text">
          <div class="row-title"><b title="${esc(item.title)}">${esc(item.title)}</b><span class="tags" style="display:contents">${tagsHtml(item)}</span></div>
          <div class="row-sub">${esc(subText(item))}</div>
        </div>
        <span class="state">${stateText(item)}</span>
        <button class="switch ${locked ? 'locked' : ''}" type="button" role="switch" aria-checked="${effectiveEnabled(item)}" aria-label="启用或禁用 ${esc(item.title)}"></button>
        <button class="chev" type="button" aria-label="详情">${svg('chevron')}</button>
      </div>
      <div class="detail"><div class="detail-inner">${state.openId === item.id ? detailHtml(item) : ''}</div></div>
    </div>`;
  }

  function detailHtml(item) {
    const rows = [];
    const add = (k, v, plain) => { if (v) rows.push(`<dt>${k}</dt><dd class="${plain ? 'plain' : ''}">${esc(v)}</dd>`); };
    add('类型', `${KIND[item.kind][0]}：${KIND[item.kind][1]}`, true);
    add('位置', item.locationLabel + (item.hive === 'HKLM' ? '（所有用户）' : item.hive === 'HKCU' && item.kind !== 'Packaged' ? '（仅当前用户）' : ''), true);
    add(item.kind === 'SendTo' ? '文件' : item.kind === 'Packaged' ? '禁用位置' : '注册表', item.registryPath);
    add('命令', item.command);
    add(item.kind === 'Packaged' ? '应用目录' : '组件', item.kind === 'SendTo' ? null : item.filePath);
    add('CLSID', item.clsid);
    if (item.fileTypes && item.fileTypes.length) add('适用类型', item.fileTypes.join('  '));
    if (item.kind === 'Packaged') add('动词', item.keyName);
    add('发布者', item.publisher, true);
    add('说明', item.note, true);
    if (item.isProtected) add('锁定', '这是系统核心菜单项，禁用后会影响打开文件等基本操作，所以不允许改动。', true);
    else if (item.isSystem && !state.info.allowSystem) add('锁定', 'Windows 自带项默认锁定。如果确实要改，可以在“设置”里打开“允许修改系统项”。', true);

    const acts = [`<button class="link" type="button" data-act="copy">${svg('copy')}复制路径</button>`];
    if (state.info.shellActions && item.filePath) acts.push(`<button class="link" type="button" data-act="reveal">${svg('open')}打开文件位置</button>`);
    if (item.canDelete && !isLocked(item)) acts.push(`<button class="link danger" type="button" data-act="delete">${svg('trash')}永久删除…</button>`);
    return `<dl class="detail-body">${rows.join('')}<div class="detail-actions">${acts.join('')}</div></dl>`;
  }

  function renderList(animate = true) {
    const list = $('#list');
    const sceneAll = sceneItems(state.scene);
    const items = visibleItems();

    if (state.loading) {
      list.innerHTML = Array.from({ length: 7 }, () => '<div class="skeleton"></div>').join('');
      $('#listCount').textContent = '正在扫描…';
      return;
    }

    // 按注册位置分组；组内第三方在前，再按名称排序
    const groups = new Map();
    for (const it of items) {
      if (!groups.has(it.locationLabel)) groups.set(it.locationLabel, []);
      groups.get(it.locationLabel).push(it);
    }
    let i = 0;
    const html = [];
    state.visibleIds = [];
    for (const [label, arr] of groups) {
      arr.sort((a, b) => (a.isSystem - b.isSystem) || cmp(a.title, b.title) || cmp(a.id, b.id));
      if (groups.size > 1 || state.scene === 'filetype') html.push(`<div class="group-title" style="--i:${Math.min(i, 14)}">${esc(label)}</div>`);
      for (const it of arr) {
        html.push(rowHtml(it, i++));
        state.visibleIds.push(it.id);
      }
    }

    for (const id of [...state.selected]) if (!state.visibleIds.includes(id)) state.selected.delete(id);
    if (state.lastCheckedId && !state.visibleIds.includes(state.lastCheckedId)) state.lastCheckedId = null;

    if (!items.length) html.push(emptyHtml(sceneAll));
    list.innerHTML = html.join('');
    if (!animate) for (const el of $$('.row, .group-title', list)) el.style.animation = 'none';

    const hiddenSystem = state.showSystem ? 0 : sceneAll.filter(x => x.isSystem).length;
    const on = items.filter(effectiveEnabled).length;
    $('#listCount').textContent = items.length
      ? `共 ${items.length} 项 · 已启用 ${on} · 已禁用 ${items.length - on}` + (hiddenSystem ? ` · 另有 ${hiddenSystem} 个系统项未显示` : '')
      : '';
    updateSelectAll();
    updateBar();
    loadIcons(items);
  }

  function emptyHtml(sceneAll) {
    if (state.scene === 'filetype' && !state.ext)
      return `<div class="empty">${svg('filetype')}<b>输入扩展名开始扫描</b><span>例如 .jpg、.pdf、.mp4</span></div>`;
    if (state.query.trim() || state.filter !== 'all')
      return `<div class="empty">${svg('search')}<b>没有符合条件的项</b><span>换个关键词，或者切换筛选条件试试。</span></div>`;
    const hidden = state.showSystem ? 0 : sceneAll.filter(x => x.isSystem).length;
    return `<div class="empty">${svg('check')}<b>这里很干净</b><span>没有第三方软件在这里添加菜单项。${hidden ? `<br><button class="link" type="button" data-act="showSystem">显示 ${hidden} 个系统项</button>` : ''}</span></div>`;
  }

  function patchRow(id) {
    const item = byId(id);
    const el = $$('#list .row').find(r => r.dataset.id === id);
    if (!item || !el) return;
    el.className = rowClass(item);
    $('.switch', el).setAttribute('aria-checked', effectiveEnabled(item));
    $('.state', el).textContent = stateText(item);
    $('.tags', el).innerHTML = tagsHtml(item);
    $('.cbx input', el).checked = state.selected.has(id);
  }

  async function loadIcons(items) {
    const need = [...new Set(items.map(i => i.iconSource).filter(s => s && !state.icons.has(s)))];
    for (let i = 0; i < need.length; i += 24) {
      const chunk = need.slice(i, i + 24);
      let res;
      try { res = await rpc('icons', { sources: chunk }); } catch { return; }
      for (const src of chunk) state.icons.set(src, res.icons[src] || null);
      for (const el of $$('#list .icon')) {
        const uri = state.icons.get(el.dataset.src);
        if (uri && !el.classList.contains('has-img')) {
          el.classList.add('has-img');
          el.innerHTML = `<img src="${uri}" alt="">`;
        }
      }
    }
  }

  // ---------- 选择 ----------
  function updateSelectAll() {
    const box = $('#selectAll');
    const n = state.visibleIds.filter(id => state.selected.has(id)).length;
    box.checked = n > 0 && n === state.visibleIds.length;
    box.indeterminate = n > 0 && n < state.visibleIds.length;
    box.disabled = !state.visibleIds.length;
  }

  function setSelected(id, on) {
    on ? state.selected.add(id) : state.selected.delete(id);
    patchRow(id);
  }

  function onCheck(id, checked, shift) {
    if (shift && state.lastCheckedId && state.visibleIds.includes(state.lastCheckedId)) {
      const a = state.visibleIds.indexOf(state.lastCheckedId);
      const b = state.visibleIds.indexOf(id);
      for (const x of state.visibleIds.slice(Math.min(a, b), Math.max(a, b) + 1)) setSelected(x, checked);
    } else {
      setSelected(id, checked);
    }
    state.lastCheckedId = id;
    updateSelectAll();
    updateBar();
  }

  function clearSelection() {
    const ids = [...state.selected];
    state.selected.clear();
    ids.forEach(patchRow);
    updateSelectAll();
    updateBar();
  }

  // ---------- 待应用的更改 ----------
  function setDesired(item, want) {
    if (want === 'delete') state.pending.set(item.id, 'Delete');
    else if ((want === 'on') === item.enabled) state.pending.delete(item.id);
    else state.pending.set(item.id, want === 'on' ? 'Enable' : 'Disable');
  }

  function explainLock(item) {
    toast(item.isProtected
      ? '这是系统核心菜单项，不允许改动。'
      : '这是 Windows 自带项，默认锁定。可以在“设置”里打开“允许修改系统项”。');
  }

  function toggle(id, el) {
    const item = byId(id);
    if (!item) return;
    if (state.info.readOnly) return toast('当前是只读预览，不能改动。');
    const want = effectiveEnabled(item) ? 'off' : 'on';
    // 已经标记为“将启用”的锁定项可以拨回去取消；其余情况下锁定项只允许恢复
    if (lockedFor(item, want) && !state.pending.has(id)) {
      el.classList.remove('shake'); void el.offsetWidth; el.classList.add('shake');
      return explainLock(item);
    }
    setDesired(item, want);
    patchRow(id);
    updateBar();
    refreshCountOnly();
  }

  function bulk(action) {
    if (state.info.readOnly) return toast('当前是只读预览，不能改动。');
    let done = 0, skipped = 0, same = 0;
    for (const id of [...state.selected]) {
      const item = byId(id);
      if (!item) continue;
      const want = action === 'Delete' ? 'delete' : action === 'Enable' ? 'on' : 'off';
      if (lockedFor(item, want) || (action === 'Delete' && !item.canDelete)) { skipped++; continue; }
      setDesired(item, action === 'Delete' ? 'delete' : action === 'Enable' ? 'on' : 'off');
      state.pending.has(id) ? done++ : same++;
    }
    const ids = [...state.selected];
    state.selected.clear();
    ids.forEach(patchRow);
    updateSelectAll();
    updateBar();
    refreshCountOnly();
    const notes = [];
    if (same) notes.push(`${same} 项本来就是${action === 'Enable' ? '启用' : '禁用'}状态`);
    if (skipped) notes.push(`跳过 ${skipped} 个${action === 'Delete' ? '不能删除或' : ''}已锁定的项`);
    toast(done
      ? `已标记 ${done} 项为“${ACTION[action]}”${notes.length ? '（' + notes.join('，') + '）' : ''}。点“应用更改”后生效。`
      : `没有可${ACTION[action]}的项${notes.length ? '：' + notes.join('，') : ''}。`);
  }

  function refreshCountOnly() {
    const items = visibleItems();
    const on = items.filter(effectiveEnabled).length;
    const text = $('#listCount').textContent;
    $('#listCount').textContent = text.replace(/已启用 \d+ · 已禁用 \d+/, `已启用 ${on} · 已禁用 ${items.length - on}`);
  }

  function updateBar() {
    const sel = state.selected.size, pend = state.pending.size;
    $('#selCount').textContent = sel;
    $('#pendCount').textContent = pend;
    $('#abSelection').classList.toggle('hide', !sel);
    $('#abPending').classList.toggle('hide', !pend);
    const show = (sel > 0 || pend > 0) && state.view === 'list';
    $('#actionbar').classList.toggle('show', show);
    $('#app').classList.toggle('lift', show);
  }

  async function applyPending() {
    const changes = [...state.pending].map(([id, action]) => ({ id, action }));
    if (!changes.length) return;
    const items = changes.map(c => ({ ...c, item: byId(c.id) })).filter(c => c.item);
    const order = { Disable: 0, Enable: 1, Delete: 2 };
    items.sort((a, b) => order[a.action] - order[b.action] || cmp(a.item.title, b.item.title));
    const admin = state.info.sandbox || state.info.elevated ? 0 : items.filter(c => c.item.needsAdmin).length;
    const dels = items.filter(c => c.action === 'Delete').length;
    const sys = items.filter(c => c.item.isSystem).length;

    const ok = await dialog({
      title: `应用 ${items.length} 项更改？`,
      body: `<ul>${items.map(c => `<li><i class="${c.action}">${ACTION[c.action]}</i><span title="${esc(c.item.title)}">${esc(c.item.title)}</span><small>${esc(c.item.locationLabel)}</small></li>`).join('')}</ul>
        ${sys ? `<div class="note warn">${svg('alert')}<span>其中 ${sys} 项是 Windows 自带的菜单项。</span></div>` : ''}
        ${dels ? `<div class="note warn">${svg('alert')}<span>${dels} 项会被删除。删除前会完整备份，之后可以在“历史与回滚”里恢复。</span></div>` : ''}
        ${admin ? `<div class="note">${svg('info')}<span>${admin} 项写在所有用户共用的注册表里，应用时会请求管理员授权。</span></div>` : ''}
        <div class="note">${svg('info')}<span>改动前会自动备份，并记入“历史与回滚”，可以随时撤销。</span></div>`,
      buttons: [{ label: '取消', value: false }, { label: '应用', value: true, kind: dels ? 'danger' : 'primary' }],
    });
    if (!ok) return;

    const btn = $('#btnApply');
    btn.disabled = true;
    btn.textContent = '正在应用…';
    let res;
    try {
      res = await rpc('apply', { changes });
    } catch (e) {
      toast('应用失败：' + e.message, { error: true });
      return;
    } finally {
      btn.disabled = false;
      btn.textContent = '应用更改';
    }
    // 只清掉这次发出去的那些；应用期间用户新拨的开关保留
    for (const c of changes) if (state.pending.get(c.id) === c.action) state.pending.delete(c.id);
    if (res.restartSuggested) state.restartSuggested = true;
    await rescan(true);
    reportResult(res, '应用');
  }

  function reportResult(res, verb) {
    const failed = res.items.filter(i => !i.ok);
    const okCount = res.items.filter(i => i.ok && !i.noOp).length;
    if (failed.length) {
      dialog({
        title: `${failed.length} 项${verb}失败` + (okCount ? `，${okCount} 项成功` : ''),
        body: `<ul>${failed.map(f => `<li class="err"><span>${f.undo ? '撤销' : ''}${ACTION[f.action] || ''} ${esc(f.title)}</span><small>${esc(f.error || '未知错误')}</small></li>`).join('')}</ul>`,
        buttons: [{ label: '知道了', value: true, kind: 'primary' }],
      });
    } else if (verb === '应用' && okCount && res.batchId) {
      toast(`已应用 ${okCount} 项更改`, { action: '撤销', onAction: () => undo(res.batchId) });
    } else {
      toast(okCount ? `已${verb} ${okCount} 项` : '没有需要改动的地方');
    }
    renderBanner();
  }

  async function undo(batchId, itemIds) {
    let res;
    try {
      res = await rpc('undo', { batchId, itemIds: itemIds || null });
    } catch (e) {
      return toast('撤销失败：' + e.message, { error: true });
    }
    if (res.restartSuggested) state.restartSuggested = true;
    await rescan(true);
    if (state.view === 'history') await renderHistory(false);
    reportResult(res, '撤销');
  }

  // ---------- 扫描 ----------
  async function rescan(quiet = false) {
    const btn = $('#btnRefresh');
    btn.classList.add('busy');
    if (!quiet) { state.loading = true; renderList(); }
    const seq = ++state.scanSeq;
    try {
      const res = await rpc('scan');
      const ft = state.ext ? (await rpc('scanFileType', { ext: state.ext })).items : null;
      if (seq !== state.scanSeq) return; // 期间又发起了新的扫描，这次的结果已经过期
      state.items = res.items;
      if (ft) state.fileTypeItems = ft;
    } catch (e) {
      if (seq !== state.scanSeq) return;
      toast('扫描失败：' + e.message, { error: true });
    } finally {
      if (seq === state.scanSeq) { state.loading = false; btn.classList.remove('busy'); }
    }
    for (const id of [...state.pending.keys()]) if (!byId(id)) state.pending.delete(id);
    for (const id of [...state.selected]) if (!byId(id)) state.selected.delete(id);
    if (state.openId && !byId(state.openId)) state.openId = null;
    updateNav();
    if (state.view === 'list') renderList(!quiet);
    updateBar();
  }

  // ---------- 视图切换 ----------
  function showScene(id) {
    state.scene = id;
    state.view = 'list';
    state.openId = null;
    const s = SCENES.find(x => x.id === id);
    $('#sceneTitle').textContent = s.label;
    $('#sceneDesc').textContent = s.desc;
    $('#extForm').hidden = id !== 'filetype';
    $('#viewHistory').hidden = true;
    const view = $('#viewList');
    view.hidden = false;
    view.style.animation = 'none'; void view.offsetWidth; view.style.animation = '';
    updateNav();
    renderList();
    updateBar();
    $('#list').scrollTop = 0;
    if (id === 'filetype' && !state.ext) $('#extInput').focus();
  }

  async function showHistory() {
    state.view = 'history';
    $('#viewList').hidden = true;
    $('#viewHistory').hidden = false;
    $('#btnOpenHistoryDir').hidden = !state.info.shellActions;
    updateNav();
    updateBar();
    await renderHistory(true);
  }

  const UNDOABLE = ['applied', 'pending', 'partial'];

  function batchStatus(b) {
    const count = st => b.items.filter(i => i.status === st).length;
    const total = b.items.length;
    const live = count('applied') + count('pending') + count('partial');
    if (count('applied') === total) return ['已生效', ''];
    if (live) return ['部分生效', 'partial'];
    if (count('undone') === total) return ['已撤销', 'undone'];
    if (count('failed') === total) return ['失败', 'failed'];
    if (count('superseded') === total) return ['已被后续改动取代', 'undone'];
    return ['已结束', 'undone'];
  }

  function fmtTime(iso) {
    const d = new Date(iso);
    const p = n => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
  }

  async function renderHistory(animate) {
    const box = $('#history');
    let batches;
    try {
      batches = (await rpc('history')).batches;
    } catch (e) {
      box.innerHTML = `<div class="empty">${svg('alert')}<b>读取历史失败</b><span>${esc(e.message)}</span></div>`;
      return;
    }
    if (!batches.length) {
      box.innerHTML = `<div class="empty">${svg('history')}<b>还没有记录</b><span>应用过的改动会出现在这里，可以随时撤销。</span></div>`;
      return;
    }
    const itemStatus = { applied: '已生效', undone: '已撤销', failed: '失败', pending: '未完成', partial: '部分生效', superseded: '已被取代', noop: '无改动' };
    const itemChip = { applied: '', partial: 'partial', pending: 'partial', failed: 'failed' };
    box.innerHTML = batches.map((b, n) => {
      const [label, cls] = batchStatus(b);
      const canUndo = b.items.some(i => UNDOABLE.includes(i.status));
      return `<article class="batch" data-batch="${esc(b.id)}" style="--i:${Math.min(n, 10)}">
        <div class="batch-head">
          <b>${esc(b.title)}</b><span class="chip ${cls}">${label}</span><time>${fmtTime(b.createdAt)}</time>
          <span class="ab-spacer"></span>
          ${state.info.shellActions && b.items.some(i => i.hasBackup) ? `<button class="btn sm ghost" type="button" data-act="openBackup">${svg('open')}备份文件</button>` : ''}
          ${canUndo && !state.info.readOnly ? `<button class="btn sm" type="button" data-act="undoBatch">${svg('undo')}撤销整批</button>` : ''}
        </div>
        ${b.items.map(i => `<div class="bitem ${esc(i.status)}" data-item="${esc(i.itemId)}">
          <span class="act ${esc(i.action)}">${esc(ACTION[i.action] || i.action)}</span>
          <span class="bt"><b>${esc(i.title)}</b><small>${esc(i.locationLabel)}${i.appName && i.appName !== i.title ? ' · ' + esc(i.appName) : ''}</small>${i.error ? `<span class="err">${esc(i.error)}</span>` : ''}</span>
          <span class="chip ${itemChip[i.status] ?? 'undone'}"${i.status === 'superseded' ? ' title="这一项后来又被改动过，这条记录已经过期"' : ''}>${esc(itemStatus[i.status] || i.status)}</span>
          ${UNDOABLE.includes(i.status) && !state.info.readOnly ? `<button class="link" type="button" data-act="undoItem">${svg('undo')}${i.status === 'applied' ? '撤销' : '尝试恢复'}</button>` : ''}
        </div>`).join('')}
      </article>`;
    }).join('');
    if (!animate) for (const el of $$('.batch', box)) el.style.animation = 'none';
  }

  // ---------- 顶部提示条 ----------
  function renderBanner() {
    const el = $('#banner');
    if (state.restartSuggested) {
      el.className = 'banner';
      el.innerHTML = `${svg('info')}<span>扩展类菜单的改动已经写入。如果右键菜单里还是老样子，重启一次资源管理器就会生效。</span>
        ${state.info.shellActions ? '<button class="btn sm" type="button" data-act="restart">立即重启</button>' : ''}
        <button class="btn sm ghost" type="button" data-act="dismiss">稍后</button>`;
      el.hidden = false;
    } else if (state.info.sandbox) {
      el.className = 'banner info';
      el.innerHTML = `${svg('info')}<span>沙箱预览：这里是仿真数据，改动不会影响真实的右键菜单。</span>`;
      el.hidden = false;
    } else if (state.info.readOnly) {
      el.className = 'banner info';
      el.innerHTML = `${svg('info')}<span>只读预览：显示的是真实菜单项，但不能应用改动。</span>`;
      el.hidden = false;
    } else {
      el.hidden = true;
    }
  }

  // ---------- 弹窗和提示 ----------
  let closeDialog = null;

  function dialog({ title, body, buttons, onMount }) {
    return new Promise(resolve => {
      const ov = $('#overlay');
      ov.className = 'overlay';
      ov.innerHTML = `<div class="dialog" role="dialog" aria-modal="true"><h2>${esc(title)}</h2><div class="dialog-body">${body}</div>
        <div class="dialog-foot">${buttons.map((b, i) => `<button class="btn ${b.kind || ''}" type="button" data-i="${i}">${esc(b.label)}</button>`).join('')}</div></div>`;
      ov.hidden = false;
      const finish = value => {
        if (closeDialog !== finish) return;
        closeDialog = null;
        ov.classList.add('closing');
        setTimeout(() => { ov.hidden = true; ov.innerHTML = ''; ov.classList.remove('closing'); }, 150);
        resolve(value);
      };
      closeDialog = finish;
      ov.onclick = e => {
        if (e.target === ov) return finish(false);
        const b = e.target.closest('.dialog-foot .btn');
        if (b) finish(buttons[+b.dataset.i].value);
      };
      (ov.querySelector('.btn.primary, .btn.danger') || ov.querySelector('.btn')).focus();
      if (onMount) onMount(ov);
    });
  }

  function toast(message, { error = false, action, onAction, duration } = {}) {
    const el = document.createElement('div');
    el.className = 'toast' + (error ? ' err' : '');
    el.innerHTML = `<span>${esc(message)}</span>` + (action ? `<button type="button">${esc(action)}</button>` : '');
    const close = () => {
      if (el.classList.contains('out')) return;
      el.classList.add('out');
      setTimeout(() => el.remove(), 220);
    };
    if (action) $('button', el).onclick = () => { close(); onAction(); };
    const box = $('#toasts');
    while (box.children.length >= 3) box.firstChild.remove();
    box.appendChild(el);
    setTimeout(close, duration || (action ? 8000 : error ? 6000 : 3600));
  }

  async function copyText(text) {
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      const ta = document.createElement('textarea');
      ta.value = text;
      document.body.appendChild(ta);
      ta.select();
      document.execCommand('copy');
      ta.remove();
    }
    toast('已复制');
  }

  function openSettings() {
    const sw = (id, on) => `<button class="switch" type="button" role="switch" id="${id}" aria-checked="${on}"></button>`;
    dialog({
      title: '设置',
      body: `<div class="setting"><div><b>显示系统项</b><small>在列表里显示 Windows 自带的菜单项。</small></div>${sw('setShow', state.showSystem)}</div>
        <div class="setting"><div><b>允许修改系统项</b><small>默认锁定，避免误关 Windows 自带功能。打开后仍然可以在历史里撤销；系统核心项始终锁定。</small></div>${sw('setAllow', state.info.allowSystem)}</div>
        <div class="setting"><div><b>备份和历史的位置</b><small>${esc(state.info.dataDir)}</small></div>${state.info.shellActions ? '<button class="btn sm" type="button" id="setOpenDir">打开</button>' : ''}</div>
        <div class="setting"><div><b>关于</b><small>右键菜单管理器 ${esc(state.info.version)}${state.info.elevated ? ' · 正以管理员身份运行' : ''}</small></div></div>`,
      buttons: [{ label: '完成', value: true, kind: 'primary' }],
      onMount: ov => {
        $('#setShow', ov).onclick = e => {
          setShowSystem(!state.showSystem);
          e.currentTarget.setAttribute('aria-checked', state.showSystem);
        };
        $('#setAllow', ov).onclick = async e => {
          const btn = e.currentTarget;
          const value = !state.info.allowSystem;
          try {
            await rpc('setAllowSystem', { value });
            state.info.allowSystem = value;
            btn.setAttribute('aria-checked', value);
            if (value && !state.showSystem) { setShowSystem(true); $('#setShow', ov).setAttribute('aria-checked', true); }
            if (!value) for (const [id] of [...state.pending]) { const it = byId(id); if (it && it.isSystem) state.pending.delete(id); }
            renderList(false);
            updateBar();
          } catch (err) {
            toast('保存设置失败：' + err.message, { error: true });
          }
        };
        const open = $('#setOpenDir', ov);
        if (open) open.onclick = () => rpc('openHistoryDir', {}).catch(err => toast(err.message, { error: true }));
      },
    });
  }

  function setShowSystem(on) {
    state.showSystem = on;
    save('showSystem', on);
    $('#showSystem').checked = on;
    updateNav();
    renderList();
  }

  // ---------- 事件 ----------
  function wire() {
    const list = $('#list');

    list.addEventListener('click', e => {
      const actEl = e.target.closest('[data-act]');
      if (actEl && actEl.dataset.act === 'showSystem') return setShowSystem(true);
      const row = e.target.closest('.row');
      if (!row) return;
      const id = row.dataset.id;
      const item = byId(id);
      if (!item) return;

      const cb = e.target.closest('.cbx');
      if (cb) {
        if (e.target.tagName === 'INPUT') onCheck(id, e.target.checked, e.shiftKey);
        return;
      }
      const sw = e.target.closest('.switch');
      if (sw) return toggle(id, sw);

      if (actEl) {
        switch (actEl.dataset.act) {
          case 'unpend': state.pending.delete(id); patchRow(id); updateBar(); refreshCountOnly(); return;
          case 'copy': return void copyText(item.registryPath);
          case 'reveal': return void rpc('reveal', { path: item.filePath }).catch(err => toast(err.message, { error: true }));
          case 'delete':
            if (state.info.readOnly) return toast('当前是只读预览，不能改动。');
            setDesired(item, 'delete'); patchRow(id); updateBar();
            return toast('已标记为删除。点“应用更改”后生效，删除前会完整备份。');
        }
        return;
      }
      if (e.target.closest('.detail')) return;

      // 点行的其余部分：展开或收起详情
      const prev = state.openId;
      state.openId = prev === id ? null : id;
      if (prev && prev !== id) patchRow(prev);
      if (state.openId === id) $('.detail-inner', row).innerHTML = detailHtml(item);
      void row.offsetHeight; // 先让浏览器记下收起时的高度，展开才有过渡动画
      patchRow(id);
    });

    $('#selectAll').addEventListener('change', e => {
      for (const id of state.visibleIds) e.target.checked ? state.selected.add(id) : state.selected.delete(id);
      state.visibleIds.forEach(patchRow);
      updateSelectAll();
      updateBar();
    });

    let searchTimer;
    $('#search').addEventListener('input', e => {
      clearTimeout(searchTimer);
      searchTimer = setTimeout(() => { state.query = e.target.value; renderList(false); }, 90);
    });

    $('#filterSeg').addEventListener('click', e => {
      const b = e.target.closest('button');
      if (!b) return;
      state.filter = b.dataset.filter;
      for (const x of $$('#filterSeg button')) x.classList.toggle('on', x === b);
      renderList();
    });

    $('#showSystem').checked = state.showSystem;
    $('#showSystem').addEventListener('change', e => setShowSystem(e.target.checked));
    $('#btnRefresh').addEventListener('click', () => rescan());
    $('#btnSettings').addEventListener('click', openSettings);

    $('#extForm').addEventListener('submit', async e => {
      e.preventDefault();
      let ext = $('#extInput').value.trim().toLowerCase();
      if (!ext) return;
      if (!ext.startsWith('.')) ext = '.' + ext;
      if (!/^\.[\w+\-#!$~.]{1,30}$/.test(ext)) return toast('扩展名的格式不对，例如 .jpg', { error: true });
      $('#extInput').value = ext;
      state.ext = ext;
      state.loading = true;
      renderList();
      try {
        state.fileTypeItems = (await rpc('scanFileType', { ext })).items;
      } catch (err) {
        toast('扫描失败：' + err.message, { error: true });
        state.fileTypeItems = [];
      }
      state.loading = false;
      $('#extHint').textContent = state.fileTypeItems.length ? `${ext} 相关的菜单项：` : `没有找到只对 ${ext} 生效的菜单项。`;
      updateNav();
      renderList();
    });

    $('#actionbar').addEventListener('click', e => {
      const b = e.target.closest('button');
      if (!b) return;
      if (b.dataset.bulk) return bulk(b.dataset.bulk);
      if (b.id === 'btnClearSel') return clearSelection();
      if (b.id === 'btnApply') return void applyPending();
      if (b.id === 'btnDiscard') {
        const ids = [...state.pending.keys()];
        state.pending.clear();
        ids.forEach(patchRow);
        updateBar();
        refreshCountOnly();
      }
    });

    $('#history').addEventListener('click', async e => {
      const b = e.target.closest('[data-act]');
      if (!b) return;
      const batchId = b.closest('.batch').dataset.batch;
      if (b.dataset.act === 'openBackup') return void rpc('openHistoryDir', { batchId }).catch(err => toast(err.message, { error: true }));
      if (b.dataset.act === 'undoItem') return void undo(batchId, [b.closest('.bitem').dataset.item]);
      if (b.dataset.act === 'undoBatch') {
        const title = $('.batch-head b', b.closest('.batch')).textContent;
        const ok = await dialog({
          title: '撤销这一批改动？',
          body: `<p>“${esc(title)}”里已生效的改动会全部恢复原状。</p>`,
          buttons: [{ label: '取消', value: false }, { label: '撤销整批', value: true, kind: 'primary' }],
        });
        if (ok) undo(batchId);
      }
    });
    $('#btnOpenHistoryDir').addEventListener('click', () => rpc('openHistoryDir', {}).catch(err => toast(err.message, { error: true })));

    $('#banner').addEventListener('click', async e => {
      const b = e.target.closest('[data-act]');
      if (!b) return;
      if (b.dataset.act === 'restart') {
        const ok = await dialog({
          title: '重启资源管理器？',
          body: '<p>桌面图标和任务栏会消失几秒再回来，已打开的文件夹窗口会被关闭。其他程序不受影响。</p>',
          buttons: [{ label: '取消', value: false }, { label: '重启', value: true, kind: 'primary' }],
        });
        if (!ok) return;
        try { await rpc('restartExplorer'); toast('资源管理器已重启'); } catch (err) { return toast('重启失败：' + err.message, { error: true }); }
      }
      state.restartSuggested = false;
      renderBanner();
    });

    document.addEventListener('keydown', e => {
      const typing = /^(INPUT|TEXTAREA)$/.test(document.activeElement.tagName) && document.activeElement.type !== 'checkbox';
      if (e.key === 'Escape') {
        if (closeDialog) return closeDialog(false);
        if (typing) return document.activeElement.blur();
        if (state.selected.size) return clearSelection();
      }
      if (closeDialog) return;
      if (e.key === 'F5' || (e.ctrlKey && e.key.toLowerCase() === 'r')) { e.preventDefault(); return void rescan(); }
      if ((e.ctrlKey && e.key.toLowerCase() === 'f') || (e.key === '/' && !typing)) { e.preventDefault(); return $('#search').focus(); }
      if (e.ctrlKey && e.key.toLowerCase() === 'a' && !typing && state.view === 'list') {
        e.preventDefault();
        state.visibleIds.forEach(id => state.selected.add(id));
        state.visibleIds.forEach(patchRow);
        updateSelectAll();
        updateBar();
      }
    });
    window.addEventListener('resize', updateNav);
  }

  // ---------- 启动 ----------
  async function boot() {
    renderNav();
    wire();
    showScene('file');
    try {
      state.info = await rpc('info');
    } catch (e) {
      toast('无法连接到程序后端：' + e.message, { error: true, duration: 15000 });
    }
    $('#envBadge').innerHTML = state.info.sandbox ? '<b>沙箱预览</b>' : state.info.readOnly ? '<b>只读预览</b>' : '';
    renderBanner();
    await rescan();
  }

  boot();
})();
