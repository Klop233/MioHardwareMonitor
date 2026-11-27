import { RadialArcGauge, CpuCoreGridChart, SparklineChart, buildMiniSparkSvg } from './gauges.js';
import { KlineChart } from './kline-chart.js';

// ================= STATE (Driven by AppController / Settings Window + Interactive Desktop Controls) =================
const state = {
  settings: {
    theme: 'light',
    layoutMode: 'hardware',
    colorScheme: 'cn',
    skin: 'miolab',
    cssVariables: null,
    hudStyle: 'instrument',
    oledTrueBlack: false,
    maskPublicIp: false,
    pingTargetHost: '223.5.5.5',
    alerts: null,
    marketEnabled: false,
    donationEnabled: false,
    cpuViewMode: 'compact',
    cpuDisplayTarget: 'auto',
    processSortBy: 'cpu',
    monitorPage: 'overview',
    processTreeViewMode: 'tree',
    processTreeSortBy: 'cpu',
    processTreeAutoFollowTop: true,
    processTreePinnedPid: 0,
    processTreeFilter: '',
    customLayout: null,
    activeSymbolId: 'BTC',
    activeCategory: 'crypto',
    activeInterval: '15m',
    cryptoExchange: 'binance',
    cryptoMarketType: 'futures',
    telemetryIntervalMs: 1500,
    showMA: true,
    showBoll: false,
    showVolume: true,
    oledPixelShift: false,
    hideMonitorTopBar: false,
    fxAmount: 100,
    fxFromCurrency: 'USD',
    fxToCurrency: 'CNY'
  },
  isFullScreen: false,
  isDirectExclusive: false,
  alwaysOnTop: true,
  lockWindowPosition: true,
  windowPositionPreset: 'custom',
  monitors: [],
  targetMonitorDevice: null,
  symbols: [],
  quotes: new Map(),
  fxRates: { USD: 1, CNY: 7.2, CNH: 7.2, EUR: 0.92, JPY: 155, HKD: 7.78, GBP: 0.78 },
  lastTelemetry: null,
  lastNetworkIp: null,
  lastProcessTree: null,
  lastKline: null,
  lastMonitorPage: 'overview',
  lastRenderedActivePid: 0,
  socketHistory: { usages: [], cores: [] },
  collapsedPids: new Set(),
  pendingTerminatePid: 0,
  pendingTerminateTimer: null,
  filterDebounceTimer: null,
  toastTimer: null
};

// ================= WIDGET DOM ANCHORS (FOR CUSTOM LAYOUT SWITCHING) =================
const WIDGET_IDS = ['cpu', 'gpu', 'ram', 'network', 'disk', 'sensors', 'processes', 'kline', 'watchlist', 'fx'];
const MARKET_WIDGET_IDS = new Set(['kline', 'watchlist', 'fx']);
const widgetAnchors = new Map();
for (const id of WIDGET_IDS) {
  const el = document.getElementById(`widget-${id}`);
  if (el && el.parentNode) {
    const marker = document.createComment(`anchor-${id}`);
    el.parentNode.insertBefore(marker, el);
    widgetAnchors.set(id, { el, parent: el.parentNode, marker });
  }
}
const fxBarEl = document.getElementById('fx-readonly-bar');
const fxBarHomeParent = fxBarEl ? fxBarEl.parentNode : null;
const fxCardSlotEl = document.getElementById('fx-card-slot');

// Dynamic per-socket chart registry for multi-CPU systems
let multiCpuPanels = [];
let activeSingleCpuTargetKey = 'combined';

// ================= BRIDGE =================
function postHost(type, payload = {}) {
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage({ type, ...payload });
  }
}

// ================= CHARTS & PRECISION INSTRUMENT GAUGES =================
const cpuGauge = new RadialArcGauge(document.getElementById('gauge-cpu'), {
  label: 'CPU',
  warnAt: 75,
  dangerAt: 90
});

const gpuGauge = new RadialArcGauge(document.getElementById('gauge-gpu'), {
  label: 'GPU',
  warnAt: 80,
  dangerAt: 92
});

const ramGauge = new RadialArcGauge(document.getElementById('gauge-ram'), {
  label: 'RAM',
  warnAt: 80,
  dangerAt: 90
});

const cpuCoreGrid = new CpuCoreGridChart(
  document.getElementById('cpu-cores-canvas'),
  updateCpuCoreHover,
  60,
  { mode: 'compact' }
);

const cpuSpark = new SparklineChart(
  document.getElementById('spark-cpu'),
  [{ colorVar: '--accent', fillAlpha: 0.16 }],
  60,
  100,
  { formatTick: (v) => `${Math.round(v)}%`, unit: '%' }
);

const gpuSpark = new SparklineChart(
  document.getElementById('spark-gpu'),
  [{ colorVar: '--accent', fillAlpha: 0.16 }],
  60,
  100,
  { formatTick: (v) => `${Math.round(v)}%`, unit: '%' }
);

const ramSpark = new SparklineChart(
  document.getElementById('spark-ram'),
  [{ colorVar: '--accent', fillAlpha: 0.16 }],
  60,
  100,
  { formatTick: (v) => `${Math.round(v)}%`, unit: '%' }
);

const netSpark = new SparklineChart(
  document.getElementById('spark-net'),
  [
    { colorVar: '--accent', fillAlpha: 0.16, dashed: false, label: '下行' },
    { colorVar: '--text-muted', fillAlpha: 0, dashed: true, label: '上行' }
  ],
  60,
  null,
  { formatTick: formatSpeedCompact, showScale: true, showPeak: true }
);

const klineChart = new KlineChart(document.getElementById('kline-canvas'), updateKlineHud);

function redrawAllCharts() {
  requestAnimationFrame(() => {
    cpuGauge.draw();
    gpuGauge.draw();
    ramGauge.draw();
    cpuCoreGrid.draw();
    cpuSpark.draw();
    for (const p of multiCpuPanels) {
      p.coreChart.draw();
      p.overallChart.draw();
    }
    gpuSpark.draw();
    ramSpark.draw();
    netSpark.draw();
    if (marketEnabled()) klineChart.draw();
  });
}

// ================= CLOCK (YYYY.MM.DD HH:mm:ss) =================
function updateClock() {
  const now = new Date();
  const y = now.getFullYear();
  const mo = String(now.getMonth() + 1).padStart(2, '0');
  const d = String(now.getDate()).padStart(2, '0');
  const hh = String(now.getHours()).padStart(2, '0');
  const mm = String(now.getMinutes()).padStart(2, '0');
  const ss = String(now.getSeconds()).padStart(2, '0');
  const el = document.getElementById('clock-datetime');
  if (el) el.textContent = `${y}.${mo}.${d} ${hh}:${mm}:${ss}`;
}
updateClock();
setInterval(updateClock, 1000);

// ================= OLED PIXEL SHIFT =================
let oledShiftStep = 0;
const OLED_OFFSETS = [[0, 0], [1, 0], [1, 1], [0, 1], [-1, 1], [-1, 0], [-1, -1], [0, -1]];
setInterval(() => {
  const root = document.getElementById('hud-root');
  if (!root) return;
  if (state.settings.oledPixelShift) {
    oledShiftStep = (oledShiftStep + 1) % OLED_OFFSETS.length;
    const [ox, oy] = OLED_OFFSETS[oledShiftStep];
    root.style.transform = `translate(${ox}px, ${oy}px)`;
  } else {
    root.style.transform = '';
  }
}, 120000);

// ================= HOST MESSAGE HANDLER =================
if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', (event) => {
    const msg = event.data;
    if (!msg || !msg.type) return;

    switch (msg.type) {
      case 'init':
        handleInit(msg.data);
        break;
      case 'settingsUpdated':
        if (msg.data) {
          state.settings = { ...state.settings, ...msg.data };
          applyVisualSettings();
          if (marketEnabled()) {
            renderWatchlist();
            updateFxReadout();
          }
          if (state.lastTelemetry) {
            renderCpuSection(state.lastTelemetry, false);
            renderProcesses(state.lastTelemetry.topProcesses || []);
          }
        }
        break;
      case 'windowState':
        handleWindowState(msg.data);
        break;
      case 'telemetry':
        state.lastTelemetry = msg.data;
        renderTelemetry(msg.data);
        break;
      case 'networkIp':
        state.lastNetworkIp = msg.data;
        renderNetworkIp(msg.data);
        break;
      case 'proctree':
        state.lastProcessTree = msg.data;
        renderProcessTree(msg.data);
        break;
      case 'processActionResult':
        handleProcessActionResult(msg.data);
        break;
      case 'quotes':
        handleQuotes(msg.data);
        break;
      case 'kline':
        handleKline(msg.data);
        break;
      case 'fxRates':
        if (msg.data && msg.data.rates) {
          state.fxRates = { ...state.fxRates, ...msg.data.rates };
          updateFxReadout();
        }
        break;
      case 'symbolsUpdated':
        if (msg.data) {
          state.symbols = msg.data.symbols || [];
          if (msg.data.activeSymbolId) state.settings.activeSymbolId = msg.data.activeSymbolId;
          if (msg.data.activeCategory) state.settings.activeCategory = msg.data.activeCategory;
          applyVisualSettings();
          if (marketEnabled()) renderWatchlist();
        }
        break;
    }
  });
}

function handleInit(data) {
  if (!data) return;
  if (data.settings) {
    state.settings = { ...state.settings, ...data.settings };
  }
  state.symbols = data.symbols || [];

  handleWindowState({
    isFullScreen: data.isFullScreen,
    isDirectExclusive: data.isDirectExclusive,
    alwaysOnTop: data.alwaysOnTop,
    lockWindowPosition: data.lockWindowPosition ?? state.settings.lockWindowPosition ?? true,
    windowPositionPreset: data.windowPositionPreset || state.settings.windowPositionPreset || 'custom',
    targetMonitorDevice: data.targetMonitorDevice || state.settings.targetMonitorDevice,
    monitors: data.monitors || []
  });

  // Batch-hydrate history with zero intermediate redraws
  if (Array.isArray(data.history) && data.history.length > 0) {
    cpuCoreGrid.hydrate(data.history);
    for (const pt of data.history) {
      cpuSpark.push([pt.cpu || 0], false);
      gpuSpark.push([pt.gpu || 0], false);
      ramSpark.push([pt.ram || 0], false);
      netSpark.push([pt.netDn || 0, pt.netUp || 0], false);
      recordSocketHistoryPoint(pt.socketUsages, pt.socketCores);
    }
  }

  applyVisualSettings();
  if (marketEnabled()) {
    renderWatchlist();
    updateFxReadout();
  }
}

function handleWindowState(data) {
  if (!data) return;
  state.isFullScreen = !!data.isFullScreen;
  if (data.isDirectExclusive !== undefined) state.isDirectExclusive = !!data.isDirectExclusive;
  state.alwaysOnTop = !!data.alwaysOnTop;
  if (data.lockWindowPosition !== undefined) state.lockWindowPosition = !!data.lockWindowPosition;
  if (data.windowPositionPreset) state.windowPositionPreset = data.windowPositionPreset;
  if (data.targetMonitorDevice) state.targetMonitorDevice = data.targetMonitorDevice;
  if (Array.isArray(data.monitors)) state.monitors = data.monitors;

  const root = document.documentElement;
  root.setAttribute('data-fullscreen', state.isFullScreen ? 'true' : 'false');
  root.setAttribute('data-window-locked', state.lockWindowPosition ? 'true' : 'false');
  root.setAttribute('data-direct-exclusive', state.isDirectExclusive ? 'true' : 'false');

  const badge = document.getElementById('window-mode-badge');
  if (badge) {
    badge.className = 'status-pill';
    if (state.isDirectExclusive) {
      badge.textContent = '硬件独占';
      badge.classList.add('success');
    } else if (state.isFullScreen) {
      badge.textContent = '占用整屏';
      badge.classList.add('info');
    } else if (state.lockWindowPosition) {
      badge.textContent = '固定已锁定';
      badge.classList.add('neutral');
    } else {
      badge.textContent = '位置已解锁';
      badge.classList.add('warning');
    }
  }

  applyTopBarVisibility();
  if (state.lastTelemetry) {
    renderCpuSection(state.lastTelemetry, false);
  }
  if (state.lastProcessTree) {
    renderProcessTree(state.lastProcessTree);
  }
  redrawAllCharts();
}

function applyTopBarVisibility() {
  const hideBar = !!state.settings.hideMonitorTopBar && (state.isFullScreen || state.lockWindowPosition);
  document.documentElement.setAttribute('data-hide-topbar', hideBar ? 'true' : 'false');
}

function applyLayoutDom() {
  const s = state.settings;
  const isCustom = (s.layoutMode || 'hardware') === 'custom';
  const marketOn = marketEnabled();
  const builtinMain = document.getElementById('builtin-main');
  const customGrid = document.getElementById('custom-grid');
  if (!builtinMain || !customGrid) return;

  if (isCustom) {
    const cfg = s.customLayout || { columns: 12, rows: 8, gapPx: 12, widgets: [] };
    const cols = Math.max(4, Math.min(16, Number(cfg.columns) || 12));
    const rows = Math.max(2, Math.min(16, Number(cfg.rows) || 8));
    const gap = Math.max(4, Math.min(24, Number(cfg.gapPx) || 12));

    customGrid.style.setProperty('--custom-cols', String(cols));
    customGrid.style.setProperty('--custom-rows', String(rows));
    customGrid.style.setProperty('--custom-gap', `${gap}px`);

    if (fxBarEl && fxCardSlotEl && fxBarEl.parentNode !== fxCardSlotEl) {
      fxCardSlotEl.appendChild(fxBarEl);
    }

    const byId = new Map();
    if (Array.isArray(cfg.widgets)) {
      for (const w of cfg.widgets) {
        if (w && w.id) byId.set(String(w.id).toLowerCase(), w);
      }
    }

    for (const id of WIDGET_IDS) {
      const anchor = widgetAnchors.get(id);
      if (!anchor) continue;
      const el = anchor.el;
      const w = byId.get(id);

      if (el.parentNode !== customGrid) {
        customGrid.appendChild(el);
      }

      const gatedOff = !marketOn && MARKET_WIDGET_IDS.has(id);
      if (!gatedOff && w && w.visible) {
        const colSpan = Math.max(1, Math.min(cols, Number(w.colSpan) || 1));
        const rowSpan = Math.max(1, Math.min(rows, Number(w.rowSpan) || 1));
        const col = Math.max(1, Math.min(cols - colSpan + 1, Number(w.col) || 1));
        const row = Math.max(1, Math.min(rows - rowSpan + 1, Number(w.row) || 1));
        el.classList.remove('hidden', 'widget-hidden');
        el.style.gridColumn = `${col} / span ${colSpan}`;
        el.style.gridRow = `${row} / span ${rowSpan}`;
      } else {
        el.classList.add('widget-hidden');
        el.style.gridColumn = '';
        el.style.gridRow = '';
      }
    }

    builtinMain.classList.add('hidden');
    customGrid.classList.remove('hidden');
  } else {
    customGrid.classList.add('hidden');
    builtinMain.classList.remove('hidden');

    for (const id of WIDGET_IDS) {
      const anchor = widgetAnchors.get(id);
      if (!anchor) continue;
      const el = anchor.el;
      el.style.gridColumn = '';
      el.style.gridRow = '';
      el.classList.remove('widget-hidden');
      if (id === 'fx') {
        el.classList.add('hidden');
      }
      if (el.parentNode !== anchor.parent) {
        anchor.parent.insertBefore(el, anchor.marker.nextSibling);
      }
    }

    if (fxBarEl && fxBarHomeParent && fxBarEl.parentNode !== fxBarHomeParent) {
      fxBarHomeParent.appendChild(fxBarEl);
    }
  }
}

function marketEnabled() {
  return state.settings.marketEnabled === true;
}

function normalizeCpuViewMode(mode) {
  const m = String(mode || 'compact').trim().toLowerCase();
  if (m === 'cores' || m === 'overall') return m;
  return 'compact';
}

function formatHistoryWindow() {
  const intervalMs = Math.max(500, Math.min(5000, Number(state.settings.telemetryIntervalMs) || 1500));
  const totalSec = Math.round((60 * intervalMs) / 1000);
  if (totalSec >= 120 && totalSec % 60 === 0) return `${totalSec / 60} 分钟`;
  if (totalSec >= 120) return `${(totalSec / 60).toFixed(1)} 分钟`;
  return `${totalSec} 秒`;
}

/** Pushes the active skin palette (computed host-side from SkinCatalog) onto :root as CSS variables. */
function applySkinVariables() {
  const vars = state.settings.cssVariables;
  const root = document.documentElement;
  if (!vars || typeof vars !== 'object') return;
  for (const [key, value] of Object.entries(vars)) {
    if (key.startsWith('--') && typeof value === 'string') {
      root.style.setProperty(key, value);
    }
  }
  root.setAttribute('data-skin', state.settings.skin || 'miolab');
}

function applyVisualSettings() {
  const s = state.settings;
  const root = document.documentElement;
  const theme = s.theme === 'dark' ? 'dark' : 'light';
  const hudStyle = s.hudStyle === 'flat' ? 'flat' : 'instrument';
  const oledBlack = !!(s.oledTrueBlack && theme === 'dark');
  const marketOn = marketEnabled();
  const monitorPage = String(s.monitorPage || 'overview').toLowerCase() === 'proctree' ? 'proctree' : 'overview';
  const enteredProcTree = monitorPage === 'proctree' && state.lastMonitorPage !== 'proctree';
  state.lastMonitorPage = monitorPage;
  const cpuView = normalizeCpuViewMode(s.cpuViewMode);

  let layoutMode = s.layoutMode || 'hardware';
  if (!marketOn && (layoutMode === 'market' || layoutMode === 'split')) layoutMode = 'hardware';

  root.setAttribute('data-theme', theme);
  root.setAttribute('data-colors', s.colorScheme || 'cn');
  root.setAttribute('data-layout', layoutMode);
  root.setAttribute('data-market', marketOn ? 'on' : 'off');
  root.setAttribute('data-style', hudStyle);
  root.setAttribute('data-hud-style', hudStyle);
  root.setAttribute('data-oled-black', oledBlack ? 'true' : 'false');
  root.setAttribute('data-page', monitorPage);
  root.setAttribute('data-cpu-view', cpuView);

  applySkinVariables();
  applyTopBarVisibility();
  applyLayoutDom();

  // Top bar page tabs & direct display page pill
  const tabOverview = document.getElementById('tab-page-overview');
  const tabProctree = document.getElementById('tab-page-proctree');
  if (tabOverview) tabOverview.classList.toggle('active', monitorPage === 'overview');
  if (tabProctree) tabProctree.classList.toggle('active', monitorPage === 'proctree');
  const directPagePill = document.getElementById('direct-page-pill');
  if (directPagePill) {
    directPagePill.textContent = monitorPage === 'proctree' ? '进程与线程树' : '硬件总览';
  }

  document.getElementById('compact-hw-strip')?.classList.toggle('hidden', layoutMode !== 'market');
  document.getElementById('compact-market-strip')?.classList.toggle('hidden', !marketOn || layoutMode !== 'hardware');

  // CPU view mode: compact (default) | cores | overall
  const showCores = cpuView !== 'overall';
  cpuCoreGrid.setMode(cpuView === 'cores' ? 'cores' : 'compact');
  document.getElementById('cpu-cores-wrap')?.classList.toggle('hidden', !showCores);
  document.getElementById('cpu-overall-wrap')?.classList.toggle('hidden', showCores);
  document.getElementById('cpu-core-hover')?.classList.toggle('hidden', !showCores);

  const viewPill = document.getElementById('cpu-view-pill');
  if (viewPill) {
    viewPill.textContent = cpuView === 'overall' ? '总体曲线' : (cpuView === 'cores' ? '逻辑处理器' : '紧凑核心格');
  }

  for (const p of multiCpuPanels) {
    p.coreChart.setMode(cpuView === 'cores' ? 'cores' : 'compact');
    p.coresWrap.classList.toggle('hidden', !showCores);
    p.overallWrap.classList.toggle('hidden', showCores);
    p.hoverEl.classList.toggle('hidden', !showCores);
  }

  updateCpuGraphCaption(state.lastTelemetry?.cpu?.coreUsages?.length || 0);

  // Process sort readout (overview card)
  const sortByMem = (s.processSortBy || 'cpu') === 'mem';
  const procSortPill = document.getElementById('proc-sort-pill');
  if (procSortPill) procSortPill.textContent = sortByMem ? '按内存排序' : '按 CPU 排序';
  const colCpu = document.getElementById('col-hdr-cpu');
  const colMem = document.getElementById('col-hdr-mem');
  if (colCpu) {
    colCpu.textContent = sortByMem ? 'CPU 占用' : 'CPU 占用 ↓';
    colCpu.classList.toggle('col-sorted', !sortByMem);
  }
  if (colMem) {
    colMem.textContent = sortByMem ? '内存占用 ↓' : '内存占用';
    colMem.classList.toggle('col-sorted', sortByMem);
  }

  // Sync Process Tree toolbar state from host settings
  syncProcessTreeControlsFromSettings();

  if (enteredProcTree) {
    state.lastRenderedActivePid = 0;
    requestAnimationFrame(() => {
      const tbody = document.getElementById('pt-process-list');
      const activeRow = tbody?.querySelector('.pt-row.active');
      if (tbody && tbody.offsetParent !== null && activeRow) {
        activeRow.scrollIntoView({ block: 'nearest' });
        state.lastRenderedActivePid = Number(state.lastProcessTree?.activePid) || 0;
      }
    });
  }

  // Re-render IP display so maskPublicIp toggles take effect immediately
  if (state.lastNetworkIp) {
    renderNetworkIp(state.lastNetworkIp);
  }

  // K-Line read-only badges
  const ivLabel = (s.activeInterval || '15m').toUpperCase();
  const klIv = document.getElementById('kl-interval-pill');
  if (klIv) {
    klIv.textContent = ivLabel === '1M' || ivLabel === '5M' || ivLabel === '15M' ? ivLabel.toLowerCase() : ivLabel;
  }

  const activeInds = [];
  if (s.showMA) activeInds.push('MA');
  if (s.showBoll) activeInds.push('BOLL');
  if (s.showVolume) activeInds.push('VOL');
  const klInd = document.getElementById('kl-indicators-pill');
  if (klInd) klInd.textContent = activeInds.length > 0 ? activeInds.join(' · ') : '纯 K 线';

  const catNames = { crypto: '虚拟货币', cn: 'A 股', us: '美股', fx: '外汇 / 黄金', all: '全部行情' };
  const wlTitle = document.getElementById('watchlist-cat-title');
  if (wlTitle) wlTitle.textContent = catNames[s.activeCategory] || '全部行情';

  const exPill = document.getElementById('watchlist-exchange-pill');
  if (exPill) {
    const activeSym = state.symbols.find((x) => x.id === s.activeSymbolId);
    const showCryptoPill = s.activeCategory === 'crypto' || s.activeCategory === 'all' || activeSym?.category === 'crypto';
    if (showCryptoPill) {
      const exMap = { binance: 'Binance', okx: 'OKX', bybit: 'Bybit', bitget: 'Bitget', gate: 'Gate.io' };
      const exName = exMap[s.cryptoExchange] || 'Binance';
      const mtName = (s.cryptoMarketType || 'futures') === 'futures' ? '永续' : '现货';
      exPill.textContent = `${exName} · ${mtName}`;
      exPill.classList.remove('hidden');
    } else {
      exPill.classList.add('hidden');
    }
  }

  klineChart.setOptions({ showMA: !!s.showMA, showBoll: !!s.showBoll, showVolume: !!s.showVolume });
  redrawAllCharts();
}

function updateCpuGraphCaption(coreCount) {
  const el = document.getElementById('cpu-graph-caption');
  if (!el) return;
  const cpuView = normalizeCpuViewMode(state.settings.cpuViewMode);
  const winStr = formatHistoryWindow();
  if (cpuView === 'overall') {
    el.textContent = `总体利用率（${winStr}实时曲线）`;
  } else if (cpuView === 'cores') {
    el.textContent = coreCount > 0
      ? `逻辑处理器（共 ${coreCount} 线程 · ${winStr}曲线）`
      : `所有逻辑处理器（${winStr}实时曲线）`;
  } else {
    el.textContent = coreCount > 0
      ? `逻辑处理器（共 ${coreCount} 线程 · 紧凑视图 · ${winStr}）`
      : `逻辑处理器（紧凑核心格 · ${winStr}）`;
  }
}

// ================= RENDER TELEMETRY =================
function renderTelemetry(t) {
  if (!t) return;

  const uptimeEl = document.getElementById('uptime-badge');
  if (uptimeEl) uptimeEl.textContent = formatUptime(t.uptimeSeconds);

  // ---- CSV Recording Topbar Pill ----
  const recPill = document.getElementById('rec-topbar-pill');
  if (recPill) {
    if (t.isRecording) {
      recPill.textContent = `● REC ${formatDurationMmSs(t.recordingDurationSeconds || 0)}`;
      recPill.classList.remove('hidden');
    } else {
      recPill.classList.add('hidden');
    }
  }

  // ---- Thermal & Load Alerts ----
  renderAlerts(t.alerts);

  // ---- CPU (Single & Multi-CPU Support) ----
  const cpu = t.cpu || {};
  const cpuUsage = cpu.totalUsagePercent || 0;
  renderCpuSection(t, true);

  // ---- GPU ----
  const gpu = t.gpu || {};
  const gpuUsage = gpu.usagePercent || 0;
  document.getElementById('gpu-name').textContent = gpu.name || 'GPU';
  document.getElementById('gpu-pct').textContent = `${Math.round(gpuUsage)}%`;
  document.getElementById('gpu-clock').textContent = gpu.coreClockMhz ? `${Math.round(gpu.coreClockMhz)} MHz` : '—';

  const gpuSubText = gpu.temperatureC != null
    ? `${Math.round(gpu.temperatureC)} °C`
    : (gpu.coreClockMhz ? `${Math.round(gpu.coreClockMhz)}M` : '—');
  gpuGauge.setValue(gpuUsage, { subText: gpuSubText });

  const gTemp = gpu.temperatureC != null ? `${Math.round(gpu.temperatureC)}°C` : '—';
  const gHot = gpu.hotspotTempC != null ? `${Math.round(gpu.hotspotTempC)}°C` : '—';
  const gpuTempLabel = document.getElementById('gpu-temp-label');
  if (gpu.memoryTempC != null) {
    if (gpuTempLabel) gpuTempLabel.textContent = '核心/热点/显存';
    document.getElementById('gpu-temp').textContent = `${gTemp} / ${gHot} / ${Math.round(gpu.memoryTempC)}°C`;
  } else {
    if (gpuTempLabel) gpuTempLabel.textContent = '温度 / 热点';
    document.getElementById('gpu-temp').textContent = `${gTemp} / ${gHot}`;
  }

  const gPwr = gpu.powerWatts != null ? `${Math.round(gpu.powerWatts)} W` : '—';
  const gFan = gpu.fanRpm != null
    ? `${Math.round(gpu.fanRpm)} RPM`
    : (gpu.fanPercent != null ? `${Math.round(gpu.fanPercent)}%` : '—');
  document.getElementById('gpu-power-fan').textContent = `${gPwr} / ${gFan}`;

  const gCoreClk = gpu.coreClockMhz ? `${Math.round(gpu.coreClockMhz)}` : '—';
  const gMemClk = gpu.memoryClockMhz ? `${Math.round(gpu.memoryClockMhz)}` : '—';
  document.getElementById('gpu-mem-clk').textContent = (gpu.coreClockMhz || gpu.memoryClockMhz)
    ? `${gCoreClk} / ${gMemClk} MHz`
    : '—';

  const gpuHealth = document.getElementById('gpu-health-pill');
  gpuHealth.className = 'status-pill';
  if (gpu.temperatureC != null && gpu.temperatureC >= 85) {
    gpuHealth.textContent = `过热 ${Math.round(gpu.temperatureC)} °C`;
    gpuHealth.classList.add('danger');
  } else if (gpu.temperatureC != null && gpu.temperatureC >= 74) {
    gpuHealth.textContent = `偏热 ${Math.round(gpu.temperatureC)} °C`;
    gpuHealth.classList.add('warning');
  } else {
    gpuHealth.textContent = '运行正常';
    gpuHealth.classList.add('success');
  }

  const vramPct = Math.round(gpu.vramUsagePercent || 0);
  document.getElementById('gpu-vram-text').textContent =
    `${(gpu.vramUsedGb || 0).toFixed(1)} / ${(gpu.vramTotalGb || 0).toFixed(1)} GB (${vramPct}%)`;
  const vramBar = document.getElementById('gpu-vram-bar');
  vramBar.style.width = `${Math.min(100, vramPct)}%`;
  vramBar.className = `progress-fill ${vramPct >= 90 ? 'danger' : (vramPct >= 75 ? 'warn' : '')}`;

  const igpuRow = document.getElementById('igpu-row');
  if (Array.isArray(t.allGpus) && t.allGpus.length > 1) {
    const sub = t.allGpus[1];
    igpuRow.classList.remove('hidden');
    igpuRow.innerHTML = `<span>辅助显卡：${escapeHtml(sub.name)}</span><span class="mono">${Math.round(sub.usagePercent || 0)}%</span>`;
  } else {
    igpuRow.classList.add('hidden');
  }
  gpuSpark.push([gpuUsage]);

  // ---- Memory ----
  const mem = t.memory || {};
  const memPct = Math.round(mem.usagePercent || 0);
  document.getElementById('ram-pct').textContent = `${memPct}%`;
  document.getElementById('ram-used-total').textContent = `${(mem.usedGb || 0).toFixed(1)} / ${(mem.totalGb || 0).toFixed(1)} GB`;
  document.getElementById('ram-used-only').textContent = `${(mem.usedGb || 0).toFixed(1)} GB`;
  document.getElementById('ram-avail').textContent = `${(mem.availableGb || 0).toFixed(1)} GB`;
  document.getElementById('ram-total-only').textContent = `${(mem.totalGb || 0).toFixed(1)} GB`;
  ramGauge.setValue(mem.usagePercent || 0, { subText: `${(mem.usedGb || 0).toFixed(1)} GB` });

  const commitDt = document.getElementById('ram-commit-dt');
  const commitDd = document.getElementById('ram-commit');
  if (commitDt && commitDd) {
    if (mem.committedGb && mem.committedGb > 0.05) {
      commitDt.classList.remove('hidden');
      commitDd.classList.remove('hidden');
      commitDd.textContent = mem.commitLimitGb && mem.commitLimitGb > 0
        ? `${mem.committedGb.toFixed(1)} / ${mem.commitLimitGb.toFixed(0)} GB`
        : `${mem.committedGb.toFixed(1)} GB`;
    } else {
      commitDt.classList.add('hidden');
      commitDd.classList.add('hidden');
    }
  }

  const ramBar = document.getElementById('ram-bar');
  ramBar.style.width = `${Math.min(100, memPct)}%`;
  ramBar.className = `progress-fill ${memPct >= 90 ? 'danger' : (memPct >= 80 ? 'warn' : '')}`;

  const ramHealth = document.getElementById('ram-health-pill');
  ramHealth.className = 'status-pill';
  if (memPct >= 90) {
    ramHealth.textContent = '占用过高';
    ramHealth.classList.add('danger');
  } else if (memPct >= 80) {
    ramHealth.textContent = '占用偏高';
    ramHealth.classList.add('warning');
  } else {
    ramHealth.textContent = '正常';
    ramHealth.classList.add('success');
  }

  const speedEl = document.getElementById('ram-speed');
  if (mem.speedMhz) {
    speedEl.textContent = `${mem.speedMhz} MT/s`;
    speedEl.classList.remove('hidden');
  } else {
    speedEl.classList.add('hidden');
  }
  ramSpark.push([mem.usagePercent || 0]);

  // ---- Session Min / Avg / Max Stats ----
  renderSessionStats(t.sessionStats);

  // ---- Network ----
  const net = t.network || {};
  document.getElementById('net-adapter').textContent = net.adapterName || '网络接口';
  document.getElementById('net-dn').textContent = formatSpeed(net.downloadBytesPerSec || 0);
  document.getElementById('net-up').textContent = formatSpeed(net.uploadBytesPerSec || 0);
  document.getElementById('net-session').textContent =
    `↓ ${formatMb(net.totalDownloadedMb || 0)} / ↑ ${formatMb(net.totalUploadedMb || 0)}`;
  renderNetworkLatency(net.latency);
  netSpark.push([net.downloadBytesPerSec || 0, net.uploadBytesPerSec || 0]);

  // ---- Disk ----
  const disk = t.disk || {};
  document.getElementById('disk-read').textContent = formatSpeed(disk.readBytesPerSec || 0);
  document.getElementById('disk-write').textContent = formatSpeed(disk.writeBytesPerSec || 0);
  renderDrives(disk.drives || []);

  // ---- Cooling & System Sensors ----
  renderSensors(t.sensors, !!t.isElevated);

  // ---- Top Processes ----
  renderProcesses(t.topProcesses || []);

  // ---- Compact Hardware Strip ----
  const stripCpuEl = document.getElementById('strip-cpu');
  if (stripCpuEl) {
    if (Array.isArray(t.cpus) && t.cpus.length > 1) {
      stripCpuEl.textContent = t.cpus.map((c) => `CPU${c.index + 1} ${Math.round(c.totalUsagePercent || 0)}%`).join(' / ');
    } else {
      stripCpuEl.textContent = `${Math.round(cpuUsage)}% · ${(cpu.currentClockGhz || 0).toFixed(1)} GHz`;
    }
  }
  const gpuTempStr = gpu.temperatureC != null ? ` · ${Math.round(gpu.temperatureC)} °C` : '';
  document.getElementById('strip-gpu').textContent = `${Math.round(gpuUsage)}%${gpuTempStr}`;
  document.getElementById('strip-vram').textContent = `${(gpu.vramUsedGb || 0).toFixed(1)} / ${(gpu.vramTotalGb || 0).toFixed(0)} GB`;
  document.getElementById('strip-ram').textContent = `${memPct}% (${(mem.usedGb || 0).toFixed(1)} GB)`;
  document.getElementById('strip-net-dn').textContent = formatSpeed(net.downloadBytesPerSec || 0);
  document.getElementById('strip-net-up').textContent = formatSpeed(net.uploadBytesPerSec || 0);
}

function renderSessionStats(ss) {
  const cpuEl = document.getElementById('cpu-session-stats');
  const gpuEl = document.getElementById('gpu-session-stats');
  const ramEl = document.getElementById('ram-session-stats');
  const valid = ss && ss.sampleCount >= 2;

  if (cpuEl) {
    if (!valid || ss.cpuUsage?.max == null) {
      cpuEl.classList.add('hidden');
    } else {
      const segs = [`占用 均 ${Math.round(ss.cpuUsage.avg ?? 0)}% · 峰 ${Math.round(ss.cpuUsage.max)}%`];
      if (ss.cpuTemp?.max != null) segs.push(`温度 均 ${Math.round(ss.cpuTemp.avg ?? 0)}°C · 峰 ${Math.round(ss.cpuTemp.max)}°C`);
      if (ss.cpuPower?.max != null) segs.push(`功耗 峰 ${Math.round(ss.cpuPower.max)}W`);
      cpuEl.textContent = `本次统计：${segs.join(' | ')}`;
      cpuEl.classList.remove('hidden');
    }
  }

  if (gpuEl) {
    if (!valid || ss.gpuUsage?.max == null) {
      gpuEl.classList.add('hidden');
    } else {
      const segs = [`占用 均 ${Math.round(ss.gpuUsage.avg ?? 0)}% · 峰 ${Math.round(ss.gpuUsage.max)}%`];
      if (ss.gpuTemp?.max != null) segs.push(`温度 峰 ${Math.round(ss.gpuTemp.max)}°C`);
      if (ss.gpuPower?.max != null) segs.push(`功耗 峰 ${Math.round(ss.gpuPower.max)}W`);
      gpuEl.textContent = `本次：${segs.join(' | ')}`;
      gpuEl.classList.remove('hidden');
    }
  }

  if (ramEl) {
    if (!valid || ss.ramUsage?.max == null) {
      ramEl.classList.add('hidden');
    } else {
      ramEl.textContent = `本次：低 ${Math.round(ss.ramUsage.min ?? 0)}% · 均 ${Math.round(ss.ramUsage.avg ?? 0)}% · 峰 ${Math.round(ss.ramUsage.max)}%`;
      ramEl.classList.remove('hidden');
    }
  }
}

function renderNetworkLatency(lat) {
  const pill = document.getElementById('net-ping-pill');
  if (!pill) return;
  if (!lat || !lat.target) {
    pill.classList.add('hidden');
    return;
  }

  pill.className = 'status-pill mono';
  if (lat.ok && lat.pingMs != null && Number.isFinite(lat.pingMs)) {
    const ms = Math.round(lat.pingMs);
    pill.textContent = `${ms} ms`;
    if (ms < 50) pill.classList.add('success');
    else if (ms < 110) pill.classList.add('warning');
    else pill.classList.add('danger');

    const jitterPart = lat.jitterMs != null ? ` · 抖动 ${lat.jitterMs.toFixed(1)} ms` : '';
    const lossPart = ` · 丢包 ${Math.round(lat.packetLossPercent || 0)}%`;
    pill.title = `延迟目标：${lat.target} · Ping ${lat.pingMs.toFixed(1)} ms${jitterPart}${lossPart}`;
  } else {
    pill.textContent = '超时';
    pill.classList.add('danger');
    pill.title = `延迟目标：${lat.target} · 探测超时（丢包 ${Math.round(lat.packetLossPercent || 0)}%）`;
  }
  pill.classList.remove('hidden');
}

function renderAlerts(alerts) {
  const list = Array.isArray(alerts) ? alerts : [];
  for (const id of WIDGET_IDS) {
    const el = document.getElementById(`widget-${id}`);
    if (el) el.classList.remove('card-alert-warning', 'card-alert-danger');
  }

  const byWidget = new Map();
  let hasDanger = false;
  for (const a of list) {
    if (!a) continue;
    const sev = a.severity === 'danger' ? 'danger' : 'warning';
    if (sev === 'danger') hasDanger = true;
    const wid = String(a.widgetId || '').toLowerCase();
    if (wid) {
      const prev = byWidget.get(wid);
      if (prev !== 'danger') byWidget.set(wid, sev);
    }
  }

  for (const [wid, sev] of byWidget.entries()) {
    const el = document.getElementById(`widget-${wid}`);
    if (el) el.classList.add(sev === 'danger' ? 'card-alert-danger' : 'card-alert-warning');
  }

  const pill = document.getElementById('alert-topbar-pill');
  if (!pill) return;
  if (list.length === 0) {
    pill.classList.add('hidden');
    pill.textContent = '';
    pill.title = '';
    return;
  }

  const lead = list.find((x) => x.severity === 'danger') || list[0];
  pill.className = `status-pill ${hasDanger ? 'danger' : 'warning'}`;
  pill.textContent = list.length === 1 ? `⚠ ${lead.title}` : `⚠ ${list.length} 项告警 · ${lead.title}`;
  pill.title = list.map((x) => x.message || x.title).join('\n');
  pill.classList.remove('hidden');
}

function renderSensors(sensors, isElevated) {
  const s = sensors || {};
  const moboNameEl = document.getElementById('sensors-mobo-name');
  if (moboNameEl) moboNameEl.textContent = s.motherboardName || '系统传感器';

  const pwrPill = document.getElementById('sensors-power-pill');
  if (pwrPill) {
    if (s.estimatedSystemPowerWatts != null && s.estimatedSystemPowerWatts > 0) {
      pwrPill.textContent = `整机 ~${Math.round(s.estimatedSystemPowerWatts)} W`;
      pwrPill.classList.remove('hidden');
    } else {
      pwrPill.classList.add('hidden');
    }
  }

  const fans = Array.isArray(s.fans) ? s.fans : [];
  const moboTemps = Array.isArray(s.motherboardTemps) ? s.motherboardTemps : [];
  const storageTemps = Array.isArray(s.storageTemps) ? s.storageTemps : [];
  const bat = s.battery || null;

  const hasAnyOther = moboTemps.length > 0 || storageTemps.length > 0 || bat != null;

  // Fans
  const fansSec = document.getElementById('sensors-fans-sec');
  const fansList = document.getElementById('sensors-fans-list');
  if (fansSec && fansList) {
    if (fans.length > 0) {
      fansSec.classList.remove('hidden');
      fansList.innerHTML = fans.slice(0, 6).map((f) => {
        const pctStr = f.percent != null ? ` (${Math.round(f.percent)}%)` : '';
        return `<div class="sensor-row">
          <span class="sensor-label" title="${escapeHtml(f.name)}">${escapeHtml(f.name)}</span>
          <span class="sensor-val mono">${Math.round(f.rpm || 0)} RPM${pctStr}</span>
        </div>`;
      }).join('');
    } else {
      fansSec.classList.remove('hidden');
      fansList.innerHTML = `<div class="sensor-row"><span class="sensor-label">${isElevated ? '未检测到转速信号' : '管理员权限可读取主板风扇'}</span><span class="sensor-val mono">—</span></div>`;
    }
  }

  // Motherboard / VRM Temps
  const moboSec = document.getElementById('sensors-mobo-sec');
  const moboList = document.getElementById('sensors-mobo-list');
  if (moboSec && moboList) {
    if (moboTemps.length > 0) {
      moboSec.classList.remove('hidden');
      moboList.innerHTML = moboTemps.slice(0, 6).map((m) => {
        const cls = m.tempC >= 85 ? 'val-danger' : (m.tempC >= 70 ? 'val-warn' : '');
        return `<div class="sensor-row">
          <span class="sensor-label" title="${escapeHtml(m.name)}">${escapeHtml(m.name)}</span>
          <span class="sensor-val mono ${cls}">${Math.round(m.tempC)} °C</span>
        </div>`;
      }).join('');
    } else {
      moboSec.classList.toggle('hidden', hasAnyOther);
      if (!hasAnyOther) {
        moboList.innerHTML = `<div class="sensor-row"><span class="sensor-label">${isElevated ? '无主板温度传感器' : '需管理员权限'}</span><span class="sensor-val mono">—</span></div>`;
      }
    }
  }

  // Storage Temps / Health
  const stoSec = document.getElementById('sensors-storage-sec');
  const stoList = document.getElementById('sensors-storage-list');
  if (stoSec && stoList) {
    if (storageTemps.length > 0) {
      stoSec.classList.remove('hidden');
      stoList.innerHTML = storageTemps.slice(0, 6).map((d) => {
        const tVal = d.tempC != null ? `${Math.round(d.tempC)}°C` : '—';
        const lifeVal = d.lifeRemainingPercent != null ? ` · ${Math.round(d.lifeRemainingPercent)}%` : '';
        const cls = d.tempC != null && d.tempC >= 62 ? 'val-danger' : (d.tempC != null && d.tempC >= 52 ? 'val-warn' : '');
        return `<div class="sensor-row">
          <span class="sensor-label" title="${escapeHtml(d.name)}">${escapeHtml(d.name)}</span>
          <span class="sensor-val mono ${cls}">${tVal}${lifeVal}</span>
        </div>`;
      }).join('');
    } else {
      stoSec.classList.add('hidden');
    }
  }

  // Battery (only on laptops / UPS)
  const batSec = document.getElementById('sensors-battery-sec');
  const batBody = document.getElementById('sensors-battery-body');
  if (batSec && batBody) {
    if (bat) {
      batSec.classList.remove('hidden');
      const statusText = bat.isCharging ? '充电中' : (bat.onAcPower ? '外接电源' : '电池供电');
      const chargeCls = bat.chargePercent <= 15 ? 'val-danger' : (bat.chargePercent <= 30 ? 'val-warn' : 'val-success');
      const rows = [
        `<div class="sensor-row"><span class="sensor-label">电量状态</span><span class="sensor-val mono ${chargeCls}">${Math.round(bat.chargePercent)}% · ${statusText}</span></div>`
      ];
      if (bat.powerWatts != null) {
        rows.push(`<div class="sensor-row"><span class="sensor-label">充放电功率</span><span class="sensor-val mono">${bat.powerWatts.toFixed(1)} W</span></div>`);
      }
      if (bat.remainingMinutes != null && bat.remainingMinutes > 0) {
        const hh = Math.floor(bat.remainingMinutes / 60);
        const mm = bat.remainingMinutes % 60;
        rows.push(`<div class="sensor-row"><span class="sensor-label">预计续航</span><span class="sensor-val mono">${hh > 0 ? `${hh}h ${mm}m` : `${mm}m`}</span></div>`);
      }
      if (bat.wearPercent != null) {
        rows.push(`<div class="sensor-row"><span class="sensor-label">损耗度</span><span class="sensor-val mono">${bat.wearPercent.toFixed(1)}%</span></div>`);
      }
      batBody.innerHTML = rows.join('');
    } else {
      batSec.classList.add('hidden');
    }
  }
}

function recordSocketHistoryPoint(socketUsages, socketCores) {
  if (!Array.isArray(socketUsages) && !Array.isArray(socketCores)) return;
  const count = Math.max(
    Array.isArray(socketUsages) ? socketUsages.length : 0,
    Array.isArray(socketCores) ? socketCores.length : 0
  );
  for (let i = 0; i < count; i++) {
    if (!state.socketHistory.usages[i]) state.socketHistory.usages[i] = [];
    if (!state.socketHistory.cores[i]) state.socketHistory.cores[i] = [];

    if (Array.isArray(socketUsages) && socketUsages[i] != null) {
      state.socketHistory.usages[i].push(Number(socketUsages[i]) || 0);
      if (state.socketHistory.usages[i].length > 60) state.socketHistory.usages[i].shift();
    }
    if (Array.isArray(socketCores) && Array.isArray(socketCores[i])) {
      state.socketHistory.cores[i].push(socketCores[i]);
      if (state.socketHistory.cores[i].length > 60) state.socketHistory.cores[i].shift();
    }
  }
}

function applyCpuHealthPill(pillEl, usage, tempC) {
  pillEl.className = 'status-pill';
  if (tempC != null && tempC >= 85) {
    pillEl.textContent = `过热 ${Math.round(tempC)} °C`;
    pillEl.classList.add('danger');
  } else if (tempC != null && tempC >= 72) {
    pillEl.textContent = `偏热 ${Math.round(tempC)} °C`;
    pillEl.classList.add('warning');
  } else if (usage >= 90) {
    pillEl.textContent = '高负载';
    pillEl.classList.add('danger');
  } else if (usage >= 75) {
    pillEl.textContent = '负载偏高';
    pillEl.classList.add('warning');
  } else {
    pillEl.textContent = '健康正常';
    pillEl.classList.add('success');
  }
}

function ensureMultiCpuPanels(count) {
  const container = document.getElementById('cpu-multi-body');
  if (!container) return;
  if (multiCpuPanels.length === count) return;

  for (const p of multiCpuPanels) {
    p.coreChart.dispose();
    p.overallChart.dispose();
  }
  multiCpuPanels = [];
  container.innerHTML = '';

  const cpuView = normalizeCpuViewMode(state.settings.cpuViewMode);
  const showCores = cpuView !== 'overall';
  for (let i = 0; i < count; i++) {
    const box = document.createElement('div');
    box.className = 'cpu-socket-box';
    box.innerHTML = `
      <div class="cpu-socket-head">
        <div class="cpu-socket-title-wrap">
          <span class="cpu-socket-label mono">CPU ${i + 1}</span>
          <span class="cpu-socket-name">检测中</span>
        </div>
        <span class="status-pill success">健康正常</span>
      </div>
      <div class="cpu-socket-kpis">
        <div class="cpu-socket-kpi"><span class="label-muted">利用率</span><span class="mono cpu-socket-kpi-big">0%</span></div>
        <div class="cpu-socket-kpi"><span class="label-muted">频率</span><span class="mono cpu-socket-kpi-val">—</span></div>
        <div class="cpu-socket-kpi"><span class="label-muted">核/线</span><span class="mono cpu-socket-kpi-val">—</span></div>
        <div class="cpu-socket-kpi"><span class="label-muted">温度/功耗</span><span class="mono cpu-socket-kpi-val">—</span></div>
      </div>
      <div class="cpu-socket-bar">
        <span class="label-muted cpu-socket-caption">逻辑处理器</span>
        <span class="mono cpu-socket-hover ${showCores ? '' : 'hidden'}">悬停查看单核</span>
      </div>
      <div class="cpu-socket-canvas-wrap cpu-socket-cores-wrap ${showCores ? '' : 'hidden'}">
        <canvas class="full-canvas"></canvas>
      </div>
      <div class="cpu-socket-canvas-wrap cpu-socket-overall-wrap ${showCores ? 'hidden' : ''}">
        <canvas class="full-canvas"></canvas>
      </div>`;
    container.appendChild(box);

    const kpiVals = box.querySelectorAll('.cpu-socket-kpi-val');
    const hoverEl = box.querySelector('.cpu-socket-hover');
    const coresWrap = box.querySelector('.cpu-socket-cores-wrap');
    const overallWrap = box.querySelector('.cpu-socket-overall-wrap');
    const coreCanvas = coresWrap.querySelector('canvas');
    const overallCanvas = overallWrap.querySelector('canvas');

    const sockIndex = i;
    const coreChart = new CpuCoreGridChart(coreCanvas, (info) => {
      if (!info) {
        hoverEl.textContent = '悬停查看单核';
      } else {
        hoverEl.textContent = `CPU${sockIndex + 1} #${info.index} · ${Math.round(info.current)}% (峰 ${Math.round(info.peak)}%)`;
      }
    }, 60, { mode: cpuView === 'cores' ? 'cores' : 'compact' });

    const overallChart = new SparklineChart(
      overallCanvas,
      [{ colorVar: '--accent', fillAlpha: 0.16 }],
      60,
      100,
      { formatTick: (v) => `${Math.round(v)}%`, unit: '%' }
    );

    if (Array.isArray(state.socketHistory.cores[i])) {
      coreChart.hydrateCoreArrays(state.socketHistory.cores[i]);
    }
    if (Array.isArray(state.socketHistory.usages[i])) {
      overallChart.hydrate(state.socketHistory.usages[i].map((u) => [u]));
    }

    multiCpuPanels.push({
      el: box,
      labelEl: box.querySelector('.cpu-socket-label'),
      nameEl: box.querySelector('.cpu-socket-name'),
      healthEl: box.querySelector('.status-pill'),
      pctEl: box.querySelector('.cpu-socket-kpi-big'),
      clockEl: kpiVals[0],
      coresEl: kpiVals[1],
      tempPowerEl: kpiVals[2],
      captionEl: box.querySelector('.cpu-socket-caption'),
      hoverEl,
      coresWrap,
      overallWrap,
      coreChart,
      overallChart
    });
  }
}

function renderCpuSection(t, pushSample = true) {
  const agg = t.cpu || {};
  const cpus = Array.isArray(t.cpus) && t.cpus.length > 0
    ? t.cpus
    : [{
        index: 0,
        label: 'CPU 1',
        name: agg.name || 'CPU',
        physicalCores: agg.physicalCores || 0,
        logicalProcessors: agg.logicalProcessors || 0,
        totalUsagePercent: agg.totalUsagePercent || 0,
        currentClockGhz: agg.currentClockGhz || 0,
        baseClockGhz: agg.baseClockGhz || 0,
        temperatureC: agg.temperatureC ?? null,
        powerWatts: agg.powerWatts ?? null,
        coreUsages: agg.coreUsages || []
      }];

  if (pushSample) {
    recordSocketHistoryPoint(
      cpus.map((c) => c.totalUsagePercent || 0),
      cpus.map((c) => c.coreUsages || [])
    );
  }

  const target = (state.settings.cpuDisplayTarget || 'auto').toLowerCase();
  const isMulti = cpus.length > 1;
  const parsedIdx = /^\d+$/.test(target) ? parseInt(target, 10) : -1;
  const focusSocket = parsedIdx >= 0 && parsedIdx < cpus.length ? cpus[parsedIdx] : null;
  const showSplitMulti = isMulti && (target === 'auto' || target === 'all') && !focusSocket;

  const singleBody = document.getElementById('cpu-single-body');
  const multiBody = document.getElementById('cpu-multi-body');
  const titleEl = document.getElementById('cpu-title');
  const nameEl = document.getElementById('cpu-name');
  const socketPill = document.getElementById('cpu-socket-pill');
  const healthEl = document.getElementById('cpu-health-pill');
  const cpuView = normalizeCpuViewMode(state.settings.cpuViewMode);
  const showCores = cpuView !== 'overall';

  if (showSplitMulti) {
    singleBody.classList.add('hidden');
    multiBody.classList.remove('hidden');

    titleEl.textContent = 'CPU 监控';
    nameEl.textContent = agg.name || `${cpus.length} 路处理器`;
    socketPill.textContent = `${cpus.length} 路分显`;
    socketPill.classList.remove('hidden');
    applyCpuHealthPill(healthEl, agg.totalUsagePercent || 0, agg.temperatureC ?? null);

    ensureMultiCpuPanels(cpus.length);

    for (let i = 0; i < cpus.length; i++) {
      const sock = cpus[i];
      const p = multiCpuPanels[i];
      if (!p) continue;

      const u = sock.totalUsagePercent || 0;
      p.labelEl.textContent = sock.label || `CPU ${i + 1}`;
      p.nameEl.textContent = sock.name || 'CPU';
      p.pctEl.textContent = `${Math.round(u)}%`;
      p.clockEl.textContent = `${(sock.currentClockGhz || 0).toFixed(2)} GHz`;
      p.coresEl.textContent = `${sock.physicalCores || 0}C/${sock.logicalProcessors || 0}T`;
      const tStr = sock.temperatureC != null ? `${Math.round(sock.temperatureC)}°C` : '—';
      const wStr = sock.powerWatts != null ? `${Math.round(sock.powerWatts)}W` : '—';
      p.tempPowerEl.textContent = `${tStr}/${wStr}`;
      applyCpuHealthPill(p.healthEl, u, sock.temperatureC ?? null);

      const nCore = Array.isArray(sock.coreUsages) ? sock.coreUsages.length : 0;
      p.captionEl.textContent = showCores ? `${nCore} 逻辑核心` : '总体利用率';
      p.coresWrap.classList.toggle('hidden', !showCores);
      p.overallWrap.classList.toggle('hidden', showCores);
      p.hoverEl.classList.toggle('hidden', !showCores);

      if (pushSample) {
        p.overallChart.push([u]);
        if (nCore > 0) p.coreChart.push(sock.coreUsages);
      }
    }
    return;
  }

  // Single CPU / Focused Socket / Combined Multi-CPU view
  multiBody.classList.add('hidden');
  singleBody.classList.remove('hidden');

  const viewCpu = focusSocket || agg;
  const viewKey = focusSocket ? `sock-${focusSocket.index}` : 'combined';

  if (activeSingleCpuTargetKey !== viewKey) {
    activeSingleCpuTargetKey = viewKey;
    cpuCoreGrid.coreHistories = [];
    cpuSpark.seriesData = cpuSpark.seriesConfigs.map(() => []);
    if (focusSocket) {
      const i = focusSocket.index;
      if (Array.isArray(state.socketHistory.cores[i])) {
        cpuCoreGrid.hydrateCoreArrays(state.socketHistory.cores[i]);
      }
      if (Array.isArray(state.socketHistory.usages[i])) {
        cpuSpark.hydrate(state.socketHistory.usages[i].map((u) => [u]));
      }
    }
  }

  titleEl.textContent = focusSocket ? `${focusSocket.label} 监控` : 'CPU 监控';
  nameEl.textContent = viewCpu.name || 'CPU';

  if (isMulti) {
    socketPill.textContent = focusSocket ? `${focusSocket.label} / 共 ${cpus.length} 路` : `${cpus.length} 路合并`;
    socketPill.classList.remove('hidden');
  } else {
    socketPill.classList.add('hidden');
  }

  const usage = viewCpu.totalUsagePercent || 0;
  document.getElementById('cpu-pct').textContent = `${Math.round(usage)}%`;
  document.getElementById('cpu-clock').textContent =
    `${(viewCpu.currentClockGhz || 0).toFixed(2)} / ${(viewCpu.baseClockGhz || 0).toFixed(2)} GHz`;
  document.getElementById('cpu-cores').textContent =
    `${viewCpu.physicalCores || 0} 核 / ${viewCpu.logicalProcessors || 0} 线程`;

  cpuGauge.setValue(usage, {
    subText: `${(viewCpu.currentClockGhz || 0).toFixed(2)} GHz`
  });

  // Non-Admin Temp Hint handling
  const tempEl = document.getElementById('cpu-temp-power');
  const elevBtn = document.getElementById('cpu-elevate-btn');
  const elevHint = document.getElementById('cpu-elevate-hint');
  const needsAdminForTemp = t.isElevated === false && viewCpu.temperatureC == null;

  if (needsAdminForTemp) {
    tempEl.textContent = viewCpu.powerWatts != null ? `— / ${Math.round(viewCpu.powerWatts)} W` : '未提权';
    if (elevBtn) elevBtn.classList.toggle('hidden', state.isDirectExclusive);
    if (elevHint) elevHint.classList.toggle('hidden', !state.isDirectExclusive);
  } else {
    const tempStr = viewCpu.temperatureC != null ? `${Math.round(viewCpu.temperatureC)} °C` : '—';
    const powerStr = viewCpu.powerWatts != null ? `${Math.round(viewCpu.powerWatts)} W` : '—';
    tempEl.textContent = `${tempStr} / ${powerStr}`;
    if (elevBtn) elevBtn.classList.add('hidden');
    if (elevHint) elevHint.classList.add('hidden');
  }

  applyCpuHealthPill(healthEl, usage, viewCpu.temperatureC ?? null);

  if (pushSample) {
    cpuSpark.push([usage]);
    if (Array.isArray(viewCpu.coreUsages) && viewCpu.coreUsages.length > 0) {
      cpuCoreGrid.push(viewCpu.coreUsages);
    }
  } else {
    cpuCoreGrid.draw();
    cpuSpark.draw();
  }

  const coreCount = Array.isArray(viewCpu.coreUsages) ? viewCpu.coreUsages.length : 0;
  updateCpuGraphCaption(coreCount);
}

function updateCpuCoreHover(info) {
  const el = document.getElementById('cpu-core-hover');
  if (!el) return;
  if (!info) {
    el.textContent = '悬停核心查看单核读数';
    return;
  }
  el.textContent = `核心 #${info.index} · 当前 ${Math.round(info.current)}% · 均值 ${Math.round(info.avg)}% · 峰值 ${Math.round(info.peak)}%`;
}

// ================= RENDER DOMESTIC / OVERSEAS IPv4 + PURITY / FRAUD + PRIVACY MASK =================
function maskIpForDisplay(ip) {
  if (!ip) return '';
  if (!state.settings.maskPublicIp) return ip;
  const parts = String(ip).trim().split('.');
  if (parts.length === 4) {
    return `${parts[0]}.•••.•••.${parts[3]}`;
  }
  if (ip.length > 6) {
    return `${ip.slice(0, 3)}•••${ip.slice(-3)}`;
  }
  return '•••.•••';
}

function buildIpRiskTooltip(ep) {
  const lines = [];
  if (ep.purityScore != null || ep.riskLabel) {
    lines.push(`纯净度：${ep.purityScore != null ? `${ep.purityScore} / 100` : '—'}${ep.riskLabel ? `（${ep.riskLabel}）` : ''}`);
  }
  if (ep.fraudScore != null) {
    lines.push(`欺诈风险分：${ep.fraudScore} / 100（越低越纯净）`);
  }
  if (ep.ipType) {
    const flags = [];
    if (ep.isResidential === true) flags.push('住宅');
    else if (ep.isResidential === false) flags.push('非住宅');
    if (ep.isBroadcast === true) flags.push('广播IP');
    lines.push(`IP 类型：${ep.ipType}${flags.length > 0 ? `（${flags.join(' · ')}）` : ''}`);
  }
  const netOrg = [ep.asn, ep.isp].filter(Boolean).join(' · ');
  if (netOrg) lines.push(`ASN / 运营商：${netOrg}`);
  if (ep.location) lines.push(`归属地：${ep.location}`);
  const sources = [ep.source, ep.riskSource].filter((v, idx, arr) => v && arr.indexOf(v) === idx);
  if (sources.length > 0) lines.push(`数据来源：${sources.join(' + ')}`);
  return lines.join('\n');
}

function renderNetworkIp(info) {
  if (!info) return;
  const pill = document.getElementById('ip-status-pill');
  if (pill) {
    pill.className = 'status-pill';
    if (info.isChecking) {
      pill.textContent = '检测中';
      pill.classList.add('info');
    } else if (state.settings.maskPublicIp) {
      pill.textContent = 'IP 已脱敏';
      pill.classList.add('warning');
    } else {
      pill.textContent = 'IPv4';
      pill.classList.add('neutral');
    }
  }

  renderSingleIp(
    info.domestic,
    info.isChecking,
    document.getElementById('ip-domestic'),
    document.getElementById('ip-domestic-loc'),
    document.getElementById('ip-domestic-risk'),
    document.getElementById('strip-ip-cn')
  );

  renderSingleIp(
    info.overseas,
    info.isChecking,
    document.getElementById('ip-overseas'),
    document.getElementById('ip-overseas-loc'),
    document.getElementById('ip-overseas-risk'),
    document.getElementById('strip-ip-intl')
  );
}

function renderSingleIp(ep, isChecking, addrEl, locEl, riskEl, stripEl) {
  if (!addrEl || !locEl) return;
  addrEl.classList.remove('ip-err');

  if (ep && ep.ok && ep.ip) {
    const shownIp = maskIpForDisplay(ep.ip);
    const tip = buildIpRiskTooltip(ep);
    addrEl.textContent = shownIp;
    addrEl.title = tip || (ep.source ? `IPv4 来源：${ep.source}` : 'IPv4 地址');
    locEl.textContent = ep.location || '—';
    locEl.title = ep.location || '';
    if (stripEl) stripEl.textContent = shownIp;

    if (riskEl) {
      const hasRisk = ep.purityScore != null || ep.fraudScore != null || !!ep.ipType;
      if (hasRisk) {
        const htmlSegs = [];
        if (ep.purityScore != null) {
          htmlSegs.push(`<span>纯净 ${escapeHtml(String(ep.purityScore))}</span>`);
        }
        if (ep.fraudScore != null) {
          if (ep.purityScore != null) {
            htmlSegs.push(`<span class="ip-risk-fraud"> · 欺诈 ${escapeHtml(String(ep.fraudScore))}</span>`);
          } else {
            htmlSegs.push(`<span>欺诈 ${escapeHtml(String(ep.fraudScore))}</span>`);
          }
        }
        if (ep.ipType) {
          const prefix = (ep.purityScore != null || ep.fraudScore != null) ? ' · ' : '';
          htmlSegs.push(`<span>${prefix}${escapeHtml(String(ep.ipType))}</span>`);
        }
        riskEl.innerHTML = htmlSegs.join('');

        const lvl = String(ep.riskLevel || '').toLowerCase();
        const riskCls = lvl === 'pure' ? 'risk-pure'
          : (lvl === 'low' ? 'risk-low'
          : (lvl === 'medium' ? 'risk-medium'
          : (lvl === 'high' ? 'risk-high' : 'risk-neutral')));
        riskEl.className = `ip-risk-pill mono ${riskCls}`;
        riskEl.title = tip;
      } else {
        riskEl.className = 'ip-risk-pill mono hidden';
        riskEl.textContent = '';
        riskEl.title = '';
      }
    }
  } else if (isChecking) {
    if (!addrEl.textContent || addrEl.textContent === '—' || addrEl.textContent === '未连通') {
      addrEl.textContent = '检测中';
    }
    if (!locEl.textContent) locEl.textContent = '—';
  } else {
    addrEl.textContent = '未连通';
    addrEl.classList.add('ip-err');
    addrEl.title = '当前出口未连通';
    locEl.textContent = '—';
    if (riskEl) {
      riskEl.className = 'ip-risk-pill mono hidden';
      riskEl.textContent = '';
    }
    if (stripEl) stripEl.textContent = '—';
  }
}

function renderDrives(drives) {
  const container = document.getElementById('drive-list');
  if (!container) return;
  if (!drives || drives.length === 0) {
    container.innerHTML = '<div class="label-muted">—</div>';
    return;
  }
  container.innerHTML = drives.map((d) => {
    const pct = Math.min(100, Math.round(d.usagePercent || 0));
    const cls = pct >= 90 ? 'danger' : (pct >= 80 ? 'warn' : '');
    const tempChip = d.temperatureC != null
      ? `<span class="drive-temp-chip mono ${d.temperatureC >= 60 ? 'val-danger' : (d.temperatureC >= 50 ? 'val-warn' : '')}">${Math.round(d.temperatureC)}°C</span>`
      : '';
    return `
      <div class="drive-item">
        <div class="drive-top">
          <span class="drive-name-wrap">
            <span class="drive-name">${escapeHtml(d.name)} <span class="label-muted">${escapeHtml(d.label)}</span></span>
            ${tempChip}
          </span>
          <span class="mono label-muted">可用 ${d.freeGb} / ${d.totalGb} GB (${pct}%)</span>
        </div>
        <div class="progress-track">
          <div class="progress-fill ${cls}" style="width: ${pct}%"></div>
        </div>
      </div>`;
  }).join('');
}

function renderProcesses(procs) {
  const container = document.getElementById('proc-list');
  if (!container) return;
  if (!procs || procs.length === 0) {
    container.innerHTML = '<div class="proc-row"><span>—</span><span class="align-right mono">—</span><span class="align-right mono">—</span><span class="align-right mono">—</span></div>';
    return;
  }
  const sortByMem = (state.settings.processSortBy || 'cpu') === 'mem';
  const sorted = [...procs].sort((a, b) =>
    sortByMem
      ? (b.memoryMb - a.memoryMb)
      : (b.cpuPercent - a.cpuPercent || b.memoryMb - a.memoryMb)
  );
  container.innerHTML = sorted.slice(0, 8).map((p) => `
    <div class="proc-row" role="row" data-pid="${p.pid}" title="${state.isDirectExclusive ? escapeHtml(p.name) : `点击在进程与线程树中查看 ${escapeHtml(p.name)} (PID ${p.pid})`}">
      <span class="proc-name" role="cell">${escapeHtml(p.name)}</span>
      <span class="align-right mono label-muted" role="cell">${p.pid}</span>
      <span class="align-right mono" role="cell">${p.cpuPercent.toFixed(1)}%</span>
      <span class="align-right mono" role="cell">${formatMemMb(p.memoryMb)}</span>
    </div>`).join('');
}

// ================= PAGE 2: PROCESS HACKER PROCESS & LIVE THREAD TREE =================
const SORT_LABELS = {
  cpu: 'CPU',
  mem: '内存',
  io: 'I/O',
  threads: '线程',
  handles: '句柄',
  pid: 'PID',
  name: '名称'
};

const CAT_LABELS = {
  system: '系统核心',
  service: '系统服务',
  elevated: '管理员提权',
  self: '自身进程',
  normal: '普通进程'
};

function syncProcessTreeControlsFromSettings() {
  const s = state.settings;
  const viewMode = s.processTreeViewMode === 'flat' ? 'flat' : 'tree';
  const sortBy = String(s.processTreeSortBy || 'cpu').toLowerCase();
  const autoFollow = s.processTreeAutoFollowTop !== false;
  const filter = s.processTreeFilter || '';

  document.getElementById('pt-view-tree')?.classList.toggle('active', viewMode === 'tree');
  document.getElementById('pt-view-flat')?.classList.toggle('active', viewMode === 'flat');
  document.getElementById('pt-tree-fold-group')?.classList.toggle('hidden', viewMode !== 'tree');

  document.querySelectorAll('#pt-sort-group .pt-chip').forEach((btn) => {
    btn.classList.toggle('active', btn.dataset.sort === sortBy);
  });

  const autoBtn = document.getElementById('pt-autofollow-btn');
  if (autoBtn) autoBtn.classList.toggle('active', autoFollow);

  const filterInput = document.getElementById('pt-filter-input');
  const clearBtn = document.getElementById('pt-filter-clear');
  if (filterInput && document.activeElement !== filterInput && filterInput.value !== filter) {
    filterInput.value = filter;
  }
  if (clearBtn) clearBtn.classList.toggle('hidden', !(filterInput ? filterInput.value : filter));
}

function renderProcessTree(snap) {
  if (!snap) return;

  // Summary strip
  const procsEl = document.getElementById('pt-stat-procs');
  const thrEl = document.getElementById('pt-stat-threads');
  const hdlEl = document.getElementById('pt-stat-handles');
  if (procsEl) procsEl.textContent = (snap.totalProcesses || 0).toLocaleString('en-US');
  if (thrEl) thrEl.textContent = (snap.totalThreads || 0).toLocaleString('en-US');
  if (hdlEl) hdlEl.textContent = (snap.totalHandles || 0).toLocaleString('en-US');

  // Direct Display Exclusive read-only status pills
  const dMode = document.getElementById('pt-direct-mode-pill');
  const dFollow = document.getElementById('pt-direct-follow-pill');
  const dFilter = document.getElementById('pt-direct-filter-pill');
  if (dMode) {
    const viewLbl = snap.viewMode === 'flat' ? '扁平排行' : '树状层级';
    const sortLbl = SORT_LABELS[snap.sortBy] || 'CPU';
    dMode.textContent = `${viewLbl} · 按 ${sortLbl}`;
  }
  if (dFollow) {
    if (snap.autoFollowTop) {
      dFollow.textContent = '硬件独占 · 自动追踪 Top CPU';
      dFollow.className = 'status-pill info';
    } else if (snap.pinnedTargetActive && snap.activePid > 0) {
      const selName = snap.selectedProcess?.name ? `${snap.selectedProcess.name} ` : '';
      dFollow.textContent = `锁定目标 · ${selName}(PID ${snap.activePid})`;
      dFollow.className = 'status-pill success';
    } else if (!snap.autoFollowTop && snap.pinnedPid > 0 && snap.activePid > 0) {
      const selName = snap.selectedProcess?.name ? `${snap.selectedProcess.name} ` : '';
      dFollow.textContent = `原锁定已退出 · 回退 ${selName}(PID ${snap.activePid})`;
      dFollow.className = 'status-pill warning';
    } else if (snap.activePid > 0) {
      const selName = snap.selectedProcess?.name ? `${snap.selectedProcess.name} ` : '';
      dFollow.textContent = `硬件独占 · ${selName}(PID ${snap.activePid})`;
      dFollow.className = 'status-pill info';
    } else {
      dFollow.textContent = '硬件独占 · 实时监控';
      dFollow.className = 'status-pill neutral';
    }
  }
  if (dFilter) {
    if (snap.filter) {
      dFilter.textContent = `筛选: ${snap.filter}`;
      dFilter.classList.remove('hidden');
    } else {
      dFilter.classList.add('hidden');
    }
  }

  // Sync active toggle buttons with live snapshot
  document.getElementById('pt-view-tree')?.classList.toggle('active', snap.viewMode !== 'flat');
  document.getElementById('pt-view-flat')?.classList.toggle('active', snap.viewMode === 'flat');
  document.getElementById('pt-tree-fold-group')?.classList.toggle('hidden', snap.viewMode === 'flat');
  document.querySelectorAll('#pt-sort-group .pt-chip').forEach((btn) => {
    btn.classList.toggle('active', btn.dataset.sort === snap.sortBy);
  });
  document.getElementById('pt-autofollow-btn')?.classList.toggle('active', !!snap.autoFollowTop);

  // Filter collapsed children in interactive Tree mode (never hide children in Direct Exclusive or when searching)
  const list = Array.isArray(snap.processes) ? snap.processes : [];
  const allowCollapse = snap.viewMode !== 'flat' && !state.isDirectExclusive && !snap.filter;
  const visibleNodes = [];
  let hiddenUnderDepth = -1;

  for (let i = 0; i < list.length; i++) {
    const node = list[i];
    if (allowCollapse && hiddenUnderDepth >= 0) {
      if (node.depth > hiddenUnderDepth) continue;
      hiddenUnderDepth = -1;
    }
    visibleNodes.push(node);
    if (allowCollapse && node.hasChildren && state.collapsedPids.has(node.pid)) {
      hiddenUnderDepth = node.depth;
    }
  }

  const shownCountEl = document.getElementById('pt-shown-count');
  if (shownCountEl) shownCountEl.textContent = `${visibleNodes.length} 项`;

  const tbody = document.getElementById('pt-process-list');
  if (tbody) {
    const prevScroll = tbody.scrollTop;
    if (visibleNodes.length === 0) {
      tbody.innerHTML = `<div class="pt-empty-state">${snap.filter ? '没有匹配筛选条件的进程' : '暂无进程数据'}</div>`;
    } else {
      const htmlParts = new Array(visibleNodes.length);
      for (let i = 0; i < visibleNodes.length; i++) {
        const p = visibleNodes[i];
        const isActive = p.pid === snap.activePid;
        const catCls = `cat-${p.category || 'normal'}`;
        const lifeCls = `life-${p.lifecycle || 'normal'}`;
        const collapsed = state.collapsedPids.has(p.pid);

        const guidesHtml = snap.viewMode !== 'flat' && p.treeGuides
          ? `<span class="pt-guides mono">${escapeHtml(p.treeGuides)}</span>`
          : '';
        const toggleHtml = allowCollapse && p.hasChildren
          ? `<button type="button" class="pt-toggle-btn" data-toggle-pid="${p.pid}" title="${collapsed ? '展开子进程' : '折叠子进程'}">${collapsed ? '▸' : '▾'}</button>`
          : '';
        const lifeTag = p.lifecycle === 'new'
          ? '<span class="pt-life-tag new">新</span>'
          : (p.lifecycle === 'exited' ? '<span class="pt-life-tag exited">退出</span>' : '');

        const cpuPct = Math.max(0, Number(p.cpuPercent) || 0);
        const cpuBarW = Math.min(100, cpuPct);
        const cpuBarCls = cpuPct >= 75 ? 'danger' : (cpuPct >= 35 ? 'warn' : '');
        const ioTotal = (p.ioReadBytesPerSec || 0) + (p.ioWriteBytesPerSec || 0);
        const titleStr = p.imagePath ? `${p.name}\n${p.imagePath}` : p.name;

        htmlParts[i] = `
          <div class="pt-row ${catCls} ${lifeCls}${isActive ? ' active' : ''}" role="row" data-pid="${p.pid}" title="${escapeHtml(titleStr)}">
            <div class="pt-name-cell">${guidesHtml}${toggleHtml}<span class="pt-proc-name">${escapeHtml(p.name)}</span>${lifeTag}</div>
            <span class="align-right mono label-muted">${p.pid}</span>
            <div class="pt-cpu-cell mono">
              ${cpuBarW > 0.2 ? `<span class="pt-cpu-bar ${cpuBarCls}" style="width:${cpuBarW.toFixed(1)}%"></span>` : ''}
              <span class="pt-cpu-text ${cpuPct >= 75 ? 'val-danger' : (cpuPct >= 35 ? 'val-warn' : '')}">${cpuPct.toFixed(1)}%</span>
            </div>
            <span class="align-right mono">${formatMemMb(p.workingSetMb || 0)}</span>
            <span class="align-right mono label-muted pt-col-io">${ioTotal > 0 ? formatSpeedCompact(ioTotal) : '—'}</span>
            <span class="align-right mono pt-col-thr">${p.threadCount || 0}</span>
            <span class="align-right mono label-muted pt-col-hdl">${p.handleCount || 0}</span>
            <span class="align-right label-muted pt-col-pri">${escapeHtml(p.priority || '—')}</span>
          </div>`;
      }
      tbody.innerHTML = htmlParts.join('');
    }
    tbody.scrollTop = prevScroll;

    const nextActivePid = Number(snap.activePid) || 0;
    if (nextActivePid > 0 && nextActivePid !== state.lastRenderedActivePid) {
      if (tbody.offsetParent !== null) {
        const activeRow = tbody.querySelector('.pt-row.active');
        if (activeRow) {
          activeRow.scrollIntoView({ block: 'nearest' });
          state.lastRenderedActivePid = nextActivePid;
        }
      }
    } else if (nextActivePid === 0) {
      state.lastRenderedActivePid = 0;
    }
  }

  renderProcessDetail(snap.selectedProcess, snap);
}

function renderProcessDetail(detail, snap) {
  const nameEl = document.getElementById('pt-det-name');
  const pidEl = document.getElementById('pt-det-pid');
  const catEl = document.getElementById('pt-det-cat');
  const priEl = document.getElementById('pt-det-pri');
  const pinEl = document.getElementById('pt-det-pin-badge');
  const pathEl = document.getElementById('pt-det-path');
  const deniedEl = document.getElementById('pt-det-denied-pill');
  const parentEl = document.getElementById('pt-det-parent');
  const uptimeEl = document.getElementById('pt-det-uptime');
  const memEl = document.getElementById('pt-det-mem');
  const ioEl = document.getElementById('pt-det-io');
  const openBtn = document.getElementById('pt-act-open-loc');
  const copyBtn = document.getElementById('pt-act-copy-path');
  const termBtn = document.getElementById('pt-act-terminate');
  const thrCountEl = document.getElementById('pt-thread-count-pill');
  const thrListEl = document.getElementById('pt-thread-list');

  if (!detail) {
    if (nameEl) nameEl.textContent = '暂无选中进程';
    if (pidEl) pidEl.textContent = 'PID —';
    catEl?.classList.add('hidden');
    priEl?.classList.add('hidden');
    pinEl?.classList.add('hidden');
    deniedEl?.classList.add('hidden');
    if (pathEl) { pathEl.textContent = '—'; pathEl.title = ''; }
    if (parentEl) parentEl.textContent = '—';
    if (uptimeEl) uptimeEl.textContent = '—';
    if (memEl) memEl.textContent = '—';
    if (ioEl) ioEl.textContent = '—';
    if (openBtn) openBtn.disabled = true;
    if (copyBtn) copyBtn.disabled = true;
    if (termBtn) { termBtn.disabled = true; termBtn.classList.remove('confirming'); termBtn.textContent = '结束进程'; }
    if (thrCountEl) thrCountEl.textContent = '0 线程';
    if (thrListEl) thrListEl.innerHTML = '<div class="pt-empty-state">点击左侧任意进程或开启自动追踪以查看实时线程树</div>';
    return;
  }

  // Cancel pending terminate confirm if inspected PID changed
  if (state.pendingTerminatePid && state.pendingTerminatePid !== detail.pid) {
    resetPendingTerminate();
  }

  if (nameEl) { nameEl.textContent = detail.name || `PID ${detail.pid}`; nameEl.title = detail.name || ''; }
  if (pidEl) pidEl.textContent = `PID ${detail.pid}`;

  if (catEl) {
    const cat = detail.category || 'normal';
    catEl.textContent = CAT_LABELS[cat] || '普通进程';
    catEl.className = `status-pill ${cat === 'elevated' ? 'warning' : (cat === 'self' ? 'info' : (cat === 'service' ? 'success' : 'neutral'))}`;
  }
  if (priEl) {
    if (detail.priority) {
      priEl.textContent = `优先级 ${detail.priority}`;
      priEl.classList.remove('hidden');
    } else {
      priEl.classList.add('hidden');
    }
  }
  if (pinEl) {
    pinEl.classList.remove('hidden');
    if (snap?.autoFollowTop) {
      pinEl.textContent = '自动追踪 Top';
      pinEl.className = 'status-pill info';
    } else if (snap?.pinnedTargetActive) {
      pinEl.textContent = '已锁定观察';
      pinEl.className = 'status-pill success';
    } else if (snap?.pinnedPid > 0) {
      pinEl.textContent = `原目标 PID ${snap.pinnedPid} 已退出 · 回退 Top`;
      pinEl.className = 'status-pill warning';
    } else {
      pinEl.textContent = '自动追踪 Top';
      pinEl.className = 'status-pill info';
    }
  }

  if (pathEl) {
    pathEl.textContent = detail.imagePath || '（内核/受保护进程，无可见磁盘路径）';
    pathEl.title = detail.imagePath || '';
  }
  deniedEl?.classList.toggle('hidden', !detail.accessDenied);

  if (parentEl) {
    parentEl.textContent = detail.parentPid > 0
      ? `${detail.parentName || '—'} (${detail.parentPid})`
      : '无父进程';
  }
  if (uptimeEl) {
    const startPart = detail.startTimeText || '—';
    const upPart = detail.uptimeSeconds > 0 ? ` · ${formatUptimeCompact(detail.uptimeSeconds)}` : '';
    uptimeEl.textContent = `${startPart}${upPart}`;
  }
  if (memEl) {
    memEl.textContent = `${formatMemMb(detail.workingSetMb || 0)} / ${formatMemMb(detail.privateMb || 0)}`;
  }
  if (ioEl) {
    ioEl.textContent = `读 ${formatMb(detail.totalIoReadMb || 0)} / 写 ${formatMb(detail.totalIoWriteMb || 0)}`;
  }

  if (openBtn) openBtn.disabled = detail.pid <= 4 || !detail.imagePath;
  if (copyBtn) copyBtn.disabled = !detail.imagePath;
  if (termBtn) {
    termBtn.disabled = !detail.canTerminate;
    if (state.pendingTerminatePid === detail.pid) {
      termBtn.classList.add('confirming');
      termBtn.textContent = `确认结束 PID ${detail.pid}?`;
    } else {
      termBtn.classList.remove('confirming');
      termBtn.textContent = detail.canTerminate ? '结束进程' : '受保护';
    }
  }

  const threads = Array.isArray(detail.threads) ? detail.threads : [];
  if (thrCountEl) {
    const totalT = detail.threadCount || threads.length;
    thrCountEl.textContent = threads.length < totalT
      ? `显示 ${threads.length} / ${totalT} 线程`
      : `${totalT} 线程`;
  }

  if (thrListEl) {
    const prevScroll = thrListEl.scrollTop;
    if (threads.length === 0) {
      thrListEl.innerHTML = `<div class="pt-empty-state">${detail.accessDenied ? '系统受保护进程，请以管理员身份运行查看线程明细' : '暂无线程明细'}</div>`;
    } else {
      thrListEl.innerHTML = threads.map((th) => {
        const totalCpu = Math.max(0, Number(th.cpuPercent) || 0);
        const userCpu = Math.max(0, Number(th.userCpuPercent) || 0);
        const kernelCpu = Math.max(0, Number(th.kernelCpuPercent) || 0);
        // Scale dual-tone bar to 0..100% (amplify lightly so even 2-10% active threads are visually distinct)
        const uW = Math.min(100, userCpu);
        const kW = Math.min(100 - uW, kernelCpu);
        const addrHtml = formatStartAddressHtml(th.startAddress || '—');
        const st = String(th.state || 'Wait');
        const stCls = st === 'Running' ? 'st-running' : (st === 'Active' ? 'st-active' : 'st-wait');
        const stLabel = st === 'Running' ? '运行' : (st === 'Active' ? '活跃' : '等待');

        return `
          <div class="pt-thread-row" role="row">
            <span class="mono label-muted">${th.tid}</span>
            <div class="pt-tcpu-cell mono" title="总计 ${totalCpu.toFixed(1)}% · 用户态 ${userCpu.toFixed(1)}% · 内核态 ${kernelCpu.toFixed(1)}%">
              <span class="pt-tbar-track">
                ${uW > 0 ? `<span class="pt-tbar-user" style="width:${uW.toFixed(1)}%"></span>` : ''}
                ${kW > 0 ? `<span class="pt-tbar-kernel" style="width:${kW.toFixed(1)}%"></span>` : ''}
              </span>
              <span class="${totalCpu >= 20 ? 'val-warn' : ''}">${totalCpu.toFixed(1)}%</span>
            </div>
            <span class="pt-start-addr mono" title="${escapeHtml(th.startAddress || '—')}">${addrHtml}</span>
            <span class="align-right mono label-muted pt-tcol-pri" title="基础 ${th.basePriority} / 动态 ${th.dynamicPriority}">${escapeHtml(th.priorityLabel || String(th.dynamicPriority ?? '—'))}</span>
            <span class="align-right mono label-muted pt-tcol-tot">${formatCpuSeconds(th.totalCpuSeconds || 0)}</span>
            <span class="pt-state-pill ${stCls}">${stLabel}</span>
          </div>`;
      }).join('');
    }
    thrListEl.scrollTop = prevScroll;
  }
}

function formatStartAddressHtml(addr) {
  const s = String(addr || '—');
  const plusIdx = s.indexOf('+');
  if (plusIdx > 0) {
    return `<span class="pt-addr-mod">${escapeHtml(s.slice(0, plusIdx))}</span>${escapeHtml(s.slice(plusIdx))}`;
  }
  return escapeHtml(s);
}

function showProcessToast(message, isOk = true) {
  const el = document.getElementById('pt-action-toast');
  if (!el) return;
  if (state.toastTimer) clearTimeout(state.toastTimer);
  el.textContent = message;
  el.className = `pt-action-toast ${isOk ? 'ok' : 'err'}`;
  state.toastTimer = setTimeout(() => {
    el.classList.add('hidden');
  }, 3600);
}

function handleProcessActionResult(res) {
  if (!res) return;
  resetPendingTerminate();
  showProcessToast(res.message || (res.success ? '操作成功' : '操作失败'), !!res.success);
}

function resetPendingTerminate() {
  state.pendingTerminatePid = 0;
  if (state.pendingTerminateTimer) {
    clearTimeout(state.pendingTerminateTimer);
    state.pendingTerminateTimer = null;
  }
  const btn = document.getElementById('pt-act-terminate');
  if (btn) {
    btn.classList.remove('confirming');
    const detail = state.lastProcessTree?.selectedProcess;
    btn.textContent = detail && !detail.canTerminate ? '受保护' : '结束进程';
  }
}

// ================= INTERACTIVE UI EVENT BINDINGS =================
// 1. Topbar Page Switcher
document.querySelectorAll('#page-switcher .page-tab').forEach((btn) => {
  btn.addEventListener('click', () => {
    if (state.isDirectExclusive) return;
    const page = btn.dataset.page === 'proctree' ? 'proctree' : 'overview';
    state.settings.monitorPage = page;
    applyVisualSettings();
    postHost('setMonitorPage', { page });
  });
});

// 2. Non-Admin CPU Temp Elevation Button
document.getElementById('cpu-elevate-btn')?.addEventListener('click', () => {
  if (state.isDirectExclusive) return;
  postHost('restartAsAdmin');
});

// 3. Overview Top Processes row click -> jump to Process Tree pinned on that PID (Desktop mode)
document.getElementById('proc-list')?.addEventListener('click', (e) => {
  if (state.isDirectExclusive) return;
  const row = e.target.closest('.proc-row[data-pid]');
  if (!row) return;
  const pid = parseInt(row.dataset.pid, 10);
  if (!Number.isFinite(pid) || pid <= 0) return;
  postHost('setProcessTreeOptions', { pinnedPid: pid, autoFollowTop: false });
  postHost('setMonitorPage', { page: 'proctree' });
});

// 4. Process Tree Toolbar Controls (Desktop mode)
document.getElementById('pt-view-tree')?.addEventListener('click', () => {
  if (state.isDirectExclusive) return;
  postHost('setProcessTreeOptions', { viewMode: 'tree' });
});

document.getElementById('pt-view-flat')?.addEventListener('click', () => {
  if (state.isDirectExclusive) return;
  postHost('setProcessTreeOptions', { viewMode: 'flat' });
});

document.querySelectorAll('#pt-sort-group .pt-chip').forEach((btn) => {
  btn.addEventListener('click', () => {
    if (state.isDirectExclusive) return;
    const sortBy = btn.dataset.sort;
    if (sortBy) postHost('setProcessTreeOptions', { sortBy });
  });
});

document.getElementById('pt-autofollow-btn')?.addEventListener('click', () => {
  if (state.isDirectExclusive) return;
  const next = !(state.lastProcessTree ? state.lastProcessTree.autoFollowTop : state.settings.processTreeAutoFollowTop);
  const pinnedPid = next ? 0 : (state.lastProcessTree?.activePid || 0);
  postHost('setProcessTreeOptions', { autoFollowTop: next, pinnedPid });
});

document.getElementById('pt-expand-all')?.addEventListener('click', () => {
  if (state.isDirectExclusive) return;
  state.collapsedPids.clear();
  if (state.lastProcessTree) renderProcessTree(state.lastProcessTree);
});

document.getElementById('pt-collapse-all')?.addEventListener('click', () => {
  if (state.isDirectExclusive) return;
  const list = state.lastProcessTree?.processes;
  if (Array.isArray(list)) {
    for (const p of list) {
      if (p.hasChildren) state.collapsedPids.add(p.pid);
    }
    renderProcessTree(state.lastProcessTree);
  }
});

// Filter input with debounce
const ptFilterInput = document.getElementById('pt-filter-input');
const ptFilterClear = document.getElementById('pt-filter-clear');
ptFilterInput?.addEventListener('input', () => {
  const val = ptFilterInput.value.trim();
  ptFilterClear?.classList.toggle('hidden', !ptFilterInput.value);
  if (state.filterDebounceTimer) clearTimeout(state.filterDebounceTimer);
  state.filterDebounceTimer = setTimeout(() => {
    postHost('setProcessTreeOptions', { filter: val });
  }, 180);
});

ptFilterClear?.addEventListener('click', () => {
  if (!ptFilterInput) return;
  ptFilterInput.value = '';
  ptFilterClear.classList.add('hidden');
  if (state.filterDebounceTimer) clearTimeout(state.filterDebounceTimer);
  postHost('setProcessTreeOptions', { filter: '' });
  ptFilterInput.focus();
});

// 5. Event Delegation for Process Tree rows & expand/collapse toggles
document.getElementById('pt-process-list')?.addEventListener('click', (e) => {
  if (state.isDirectExclusive) return;

  const toggleBtn = e.target.closest('.pt-toggle-btn[data-toggle-pid]');
  if (toggleBtn) {
    e.stopPropagation();
    const pid = parseInt(toggleBtn.dataset.togglePid, 10);
    if (Number.isFinite(pid)) {
      if (state.collapsedPids.has(pid)) state.collapsedPids.delete(pid);
      else state.collapsedPids.add(pid);
      if (state.lastProcessTree) renderProcessTree(state.lastProcessTree);
    }
    return;
  }

  const row = e.target.closest('.pt-row[data-pid]');
  if (!row) return;
  const pid = parseInt(row.dataset.pid, 10);
  if (!Number.isFinite(pid) || pid <= 0) return;

  resetPendingTerminate();
  document.querySelectorAll('#pt-process-list .pt-row.active').forEach((el) => el.classList.remove('active'));
  row.classList.add('active');
  postHost('setProcessTreeOptions', { pinnedPid: pid, autoFollowTop: false });
});

// 6. Detail Inspector Actions (Open Location / Copy Path / Two-Step Safe Terminate)
document.getElementById('pt-act-open-loc')?.addEventListener('click', () => {
  if (state.isDirectExclusive) return;
  const pid = state.lastProcessTree?.selectedProcess?.pid;
  if (pid > 0) postHost('openProcessLocation', { pid });
});

document.getElementById('pt-act-copy-path')?.addEventListener('click', async () => {
  if (state.isDirectExclusive) return;
  const path = state.lastProcessTree?.selectedProcess?.imagePath;
  if (!path) return;
  try {
    await navigator.clipboard.writeText(path);
    showProcessToast('已复制进程可执行文件路径', true);
  } catch {
    try {
      const ta = document.createElement('textarea');
      ta.value = path;
      ta.style.position = 'fixed';
      ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.select();
      document.execCommand('copy');
      ta.remove();
      showProcessToast('已复制进程可执行文件路径', true);
    } catch {
      showProcessToast('复制路径失败', false);
    }
  }
});

document.getElementById('pt-act-terminate')?.addEventListener('click', () => {
  if (state.isDirectExclusive) return;
  const detail = state.lastProcessTree?.selectedProcess;
  if (!detail || !detail.canTerminate) return;

  if (state.pendingTerminatePid === detail.pid) {
    const targetPid = detail.pid;
    resetPendingTerminate();
    postHost('terminateProcess', { pid: targetPid });
    return;
  }

  resetPendingTerminate();
  state.pendingTerminatePid = detail.pid;
  const btn = document.getElementById('pt-act-terminate');
  if (btn) {
    btn.classList.add('confirming');
    btn.textContent = `确认结束 PID ${detail.pid}?`;
  }
  state.pendingTerminateTimer = setTimeout(resetPendingTerminate, 3500);
});

// ================= RENDER MARKET & K-LINE (Preserved intact when MarketEnabled) =================
function handleQuotes(quotes) {
  if (!marketEnabled() || !Array.isArray(quotes)) return;
  for (const q of quotes) {
    state.quotes.set(q.id, q);
    if (q.price > 0) {
      if (q.id === 'USDCNH') state.fxRates.CNH = q.price;
      if (q.id === 'USDCNY') state.fxRates.CNY = q.price;
      if (q.id === 'EURUSD') state.fxRates.EUR = 1 / q.price;
      if (q.id === 'USDJPY') state.fxRates.JPY = q.price;
    }
  }

  const now = new Date();
  document.getElementById('market-updated').textContent =
    `更新 ${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}:${String(now.getSeconds()).padStart(2, '0')}`;

  renderWatchlist();
  renderCompactMarketStrip();
  updateFxReadout();

  const activeQ = state.quotes.get(state.settings.activeSymbolId);
  if (activeQ && activeQ.price > 0 && state.lastKline && state.lastKline.symbolId === activeQ.id) {
    updateKlineHeader({
      ...state.lastKline,
      lastPrice: activeQ.price,
      change: activeQ.change,
      changePercent: activeQ.changePercent,
      high24h: activeQ.high || state.lastKline.high24h,
      low24h: activeQ.low || state.lastKline.low24h,
      markPrice: activeQ.markPrice ?? state.lastKline.markPrice,
      fundingRate: activeQ.fundingRate ?? state.lastKline.fundingRate
    });
  }
}

function handleKline(payload) {
  if (!marketEnabled() || !payload) return;
  const resetView = !state.lastKline
    || state.lastKline.symbolId !== payload.symbolId
    || state.lastKline.interval !== payload.interval;

  state.lastKline = payload;
  const statusEl = document.getElementById('kline-status');

  if (payload.error && (!payload.candles || payload.candles.length === 0)) {
    statusEl.textContent = payload.error;
    statusEl.classList.remove('hidden');
  } else {
    statusEl.classList.add('hidden');
  }

  updateKlineHeader(payload);
  klineChart.setData(payload.candles || [], payload.decimals || 4, resetView);
}

function updateKlineHeader(p) {
  const dec = p.decimals || 4;
  document.getElementById('kl-code').textContent = p.displayCode || p.symbolId;
  document.getElementById('kl-name').textContent = p.name || '';
  document.getElementById('kl-source').textContent = p.source || 'LIVE';

  const frEl = document.getElementById('kl-funding');
  if (p.fundingRate != null && isFinite(p.fundingRate) && p.marketType === 'futures') {
    const pct = p.fundingRate * 100;
    frEl.textContent = `费率 ${pct >= 0 ? '+' : ''}${pct.toFixed(4)}%`;
    frEl.classList.remove('hidden');
  } else {
    frEl.classList.add('hidden');
  }

  const markWrap = document.getElementById('kl-mark-wrap');
  if (p.markPrice != null && p.markPrice > 0 && p.marketType === 'futures') {
    document.getElementById('kl-mark').textContent = formatPrice(p.markPrice, dec);
    markWrap.classList.remove('hidden');
  } else {
    markWrap.classList.add('hidden');
  }

  const priceEl = document.getElementById('kl-price');
  const chgEl = document.getElementById('kl-change');
  const isUp = (p.change || 0) >= 0;

  priceEl.textContent = p.lastPrice > 0 ? formatPrice(p.lastPrice, dec) : '—';
  priceEl.classList.toggle('up', isUp);
  priceEl.classList.toggle('down', !isUp);

  const sign = isUp ? '+' : '';
  chgEl.textContent = p.lastPrice > 0
    ? `${sign}${formatPrice(p.change || 0, dec)} (${sign}${(p.changePercent || 0).toFixed(2)}%)`
    : '—';
  chgEl.className = `mono status-pill ${p.lastPrice > 0 ? (isUp ? 'up' : 'down') : 'neutral'}`;

  document.getElementById('kl-high').textContent = p.high24h > 0 ? formatPrice(p.high24h, dec) : '—';
  document.getElementById('kl-low').textContent = p.low24h > 0 ? formatPrice(p.low24h, dec) : '—';
}

function updateKlineHud({ candle, prevClose, ma5, ma10, ma20, decimals }) {
  if (!candle) return;
  const d = decimals || 4;
  document.getElementById('hud-o').textContent = formatPrice(candle.open, d);
  document.getElementById('hud-h').textContent = formatPrice(candle.high, d);
  document.getElementById('hud-l').textContent = formatPrice(candle.low, d);
  document.getElementById('hud-c').textContent = formatPrice(candle.close, d);

  const pct = prevClose > 0 ? ((candle.close - prevClose) / prevClose) * 100 : 0;
  const chgEl = document.getElementById('hud-chg');
  chgEl.textContent = `${pct >= 0 ? '+' : ''}${pct.toFixed(2)}%`;
  chgEl.style.color = pct >= 0 ? 'var(--up-color)' : 'var(--down-color)';

  document.getElementById('hud-ma5').textContent = ma5 != null ? formatPrice(ma5, d) : '—';
  document.getElementById('hud-ma10').textContent = ma10 != null ? formatPrice(ma10, d) : '—';
  document.getElementById('hud-ma20').textContent = ma20 != null ? formatPrice(ma20, d) : '—';
}

function renderWatchlist() {
  const grid = document.getElementById('watchlist-grid');
  const cat = state.settings.activeCategory || 'crypto';

  const items = state.symbols.filter((s) => cat === 'all' || s.category === cat);
  document.getElementById('watchlist-count-pill').textContent = String(items.length);

  grid.innerHTML = items.map((sym) => {
    const q = state.quotes.get(sym.id);
    const dec = q && q.decimals ? q.decimals : sym.decimals;
    const price = q && q.price > 0 ? formatPrice(q.price, dec) : '—';
    const pctVal = q ? q.changePercent : 0;
    const isUp = pctVal >= 0;
    const pctText = q && q.price > 0 ? `${isUp ? '+' : ''}${pctVal.toFixed(2)}%` : '—';
    const active = sym.id === state.settings.activeSymbolId ? 'active' : '';
    const displayCode = q?.displayCode || (sym.category === 'crypto' && state.settings.cryptoMarketType === 'futures' ? `${sym.displayCode} 永续` : sym.displayCode);
    const subLabel = q?.name || sym.name;

    return `
      <div class="quote-tile readonly ${active}">
        <div class="quote-top">
          <span class="mono quote-code">${escapeHtml(displayCode)}</span>
          <span class="mono quote-pct ${isUp ? 'up' : 'down'}">${pctText}</span>
        </div>
        <div class="quote-mid">
          <div>
            <div class="mono quote-price">${price}</div>
            <div class="quote-name" title="${escapeHtml(subLabel)}">${escapeHtml(subLabel)}</div>
          </div>
          ${buildMiniSparkSvg(q?.sparkline, isUp)}
        </div>
      </div>`;
  }).join('');
}

function renderCompactMarketStrip() {
  const strip = document.getElementById('compact-market-strip');
  const topIds = ['BTC', 'ETH', 'SOL', 'USDCNH', 'EURCNY', 'XAUUSD', 'CN_SSE', 'CN_600519', 'US_IXIC', 'US_NVDA'];
  strip.innerHTML = topIds.map((id) => {
    const q = state.quotes.get(id);
    if (!q || q.price <= 0) return '';
    const isUp = q.changePercent >= 0;
    return `
      <div class="ticker-chip readonly mono">
        <strong>${escapeHtml(q.displayCode)}</strong>
        <span>${formatPrice(q.price, q.decimals)}</span>
        <span style="color:${isUp ? 'var(--up-color)' : 'var(--down-color)'};font-weight:500">${isUp ? '+' : ''}${q.changePercent.toFixed(2)}%</span>
      </div>`;
  }).join('');
}

function updateFxReadout() {
  const s = state.settings;
  const amount = Number(s.fxAmount ?? 100);
  const from = s.fxFromCurrency || 'USD';
  const to = s.fxToCurrency || 'CNY';

  const rFrom = state.fxRates[from];
  const rTo = state.fxRates[to];
  const exprEl = document.getElementById('fx-expression');
  const rateEl = document.getElementById('fx-unit-rate');
  if (!exprEl || !rateEl) return;

  if (!isFinite(amount) || !rFrom || !rTo) {
    exprEl.textContent = `${amount} ${from} = — ${to}`;
    rateEl.textContent = '—';
    return;
  }

  const unitRate = rTo / rFrom;
  const converted = amount * unitRate;
  const resDec = converted >= 1000 ? 2 : 4;
  const formattedAmt = Number.isInteger(amount) ? amount.toLocaleString('en-US') : amount.toString();
  const formattedRes = converted.toLocaleString('en-US', { minimumFractionDigits: resDec, maximumFractionDigits: resDec });
  exprEl.textContent = `${formattedAmt} ${from} = ${formattedRes} ${to}`;
  rateEl.textContent = `1 ${from} = ${unitRate.toFixed(4)} ${to}`;
}

// ================= WINDOW DRAG (ONLY WHEN UNLOCKED) & KEYBOARD SHORTCUTS =================
document.querySelectorAll('[data-drag="true"]').forEach((el) => {
  el.addEventListener('mousedown', (e) => {
    if (e.button === 0 && e.detail === 1 && !state.isFullScreen && !state.lockWindowPosition) {
      postHost('startDrag');
    }
  });
});

document.addEventListener('keydown', (e) => {
  const inInput = document.activeElement && (document.activeElement.tagName === 'INPUT' || document.activeElement.tagName === 'TEXTAREA');

  if (e.key === 'F11') {
    e.preventDefault();
    postHost('toggleFullScreen');
  } else if (e.key === 'Escape') {
    if (inInput && document.activeElement === ptFilterInput) {
      ptFilterInput.blur();
      return;
    }
    if (state.pendingTerminatePid) {
      resetPendingTerminate();
      return;
    }
    if (state.isFullScreen) {
      postHost('setFullScreen', { value: false });
    }
  } else if (e.ctrlKey && e.key === 'Tab') {
    e.preventDefault();
    postHost('toggleMonitorPage');
  } else if (e.altKey && (e.key === '1' || e.code === 'Digit1')) {
    e.preventDefault();
    postHost('setMonitorPage', { page: 'overview' });
  } else if (e.altKey && (e.key === '2' || e.code === 'Digit2')) {
    e.preventDefault();
    postHost('setMonitorPage', { page: 'proctree' });
  } else if (e.ctrlKey && e.key === ',') {
    e.preventDefault();
    postHost('openControlWindow');
  } else if (e.ctrlKey && !e.altKey && e.key >= '1' && e.key <= '9') {
    const idx = parseInt(e.key, 10) - 1;
    if (state.monitors[idx]) {
      e.preventDefault();
      postHost('setMonitor', { deviceName: state.monitors[idx].deviceName });
    }
  }
});

// ================= HELPERS =================
function formatPrice(v, dec = 4) {
  if (!isFinite(v)) return '—';
  if (Math.abs(v) >= 10000) return v.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  return v.toFixed(dec);
}

function formatSpeed(bytesPerSec) {
  if (bytesPerSec >= 1024 * 1024 * 1024) return `${(bytesPerSec / (1024 * 1024 * 1024)).toFixed(2)} GB/s`;
  if (bytesPerSec >= 1024 * 1024) return `${(bytesPerSec / (1024 * 1024)).toFixed(2)} MB/s`;
  if (bytesPerSec >= 1024) return `${(bytesPerSec / 1024).toFixed(1)} KB/s`;
  return `${Math.round(bytesPerSec)} B/s`;
}

function formatSpeedCompact(bytesPerSec) {
  if (bytesPerSec >= 1024 * 1024 * 1024) return `${(bytesPerSec / (1024 * 1024 * 1024)).toFixed(1)}G/s`;
  if (bytesPerSec >= 1024 * 1024) return `${(bytesPerSec / (1024 * 1024)).toFixed(1)}M/s`;
  if (bytesPerSec >= 1024) return `${(bytesPerSec / 1024).toFixed(0)}K/s`;
  return `${Math.round(bytesPerSec)}B/s`;
}

function formatMemMb(mb) {
  if (mb >= 1024) return `${(mb / 1024).toFixed(2)} GB`;
  if (mb >= 100) return `${Math.round(mb)} MB`;
  return `${mb.toFixed(1)} MB`;
}

function formatMb(mb) {
  if (mb >= 1024) return `${(mb / 1024).toFixed(2)} G`;
  return `${Math.round(mb)} M`;
}

function formatUptime(sec) {
  const s = Math.max(0, Math.floor(sec || 0));
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  if (d > 0) return `${d} 天 ${h} 小时`;
  if (h > 0) return `${h} 小时 ${m} 分`;
  return `${m} 分钟`;
}

function formatUptimeCompact(sec) {
  const s = Math.max(0, Math.floor(sec || 0));
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m ${s % 60}s`;
  return `${s}s`;
}

function formatDurationMmSs(sec) {
  const s = Math.max(0, Math.floor(sec || 0));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const rem = s % 60;
  if (h > 0) {
    return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(rem).padStart(2, '0')}`;
  }
  return `${String(m).padStart(2, '0')}:${String(rem).padStart(2, '0')}`;
}

function formatCpuSeconds(sec) {
  const s = Math.max(0, Number(sec) || 0);
  if (s >= 3600) return `${(s / 3600).toFixed(1)}h`;
  if (s >= 60) return `${Math.floor(s / 60)}m${Math.round(s % 60)}s`;
  if (s >= 10) return `${s.toFixed(0)}s`;
  return `${s.toFixed(1)}s`;
}

function escapeHtml(str) {
  return String(str || '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}
