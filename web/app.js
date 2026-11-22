import { CpuCoreGridChart, SparklineChart, buildMiniSparkSvg } from './gauges.js';
import { KlineChart } from './kline-chart.js';

// ================= STATE (100% driven by Settings Window / AppController) =================
const state = {
  settings: {
    theme: 'light',
    layoutMode: 'split',
    colorScheme: 'cn',
    cpuViewMode: 'cores',
    processSortBy: 'cpu',
    activeSymbolId: 'BTC',
    activeCategory: 'crypto',
    activeInterval: '15m',
    cryptoExchange: 'binance',
    cryptoMarketType: 'futures',
    telemetryIntervalMs: 1000,
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
  lastKline: null
};

// ================= BRIDGE =================
function postHost(type, payload = {}) {
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage({ type, ...payload });
  }
}

// ================= CHARTS =================
const cpuCoreGrid = new CpuCoreGridChart(
  document.getElementById('cpu-cores-canvas'),
  updateCpuCoreHover,
  60
);

const cpuSpark = new SparklineChart(
  document.getElementById('spark-cpu'),
  [{ colorVar: '--accent', fillAlpha: 0.14 }],
  60,
  100
);

const gpuSpark = new SparklineChart(
  document.getElementById('spark-gpu'),
  [{ colorVar: '--accent', fillAlpha: 0.14 }],
  60,
  100
);

const ramSpark = new SparklineChart(
  document.getElementById('spark-ram'),
  [{ colorVar: '--accent', fillAlpha: 0.14 }],
  60,
  100
);

const netSpark = new SparklineChart(
  document.getElementById('spark-net'),
  [
    { colorVar: '--accent', fillAlpha: 0.14, dashed: false },
    { colorVar: '--text-muted', fillAlpha: 0, dashed: true }
  ],
  60,
  null
);

const klineChart = new KlineChart(document.getElementById('kline-canvas'), updateKlineHud);

function redrawAllCharts() {
  requestAnimationFrame(() => {
    cpuCoreGrid.draw();
    cpuSpark.draw();
    gpuSpark.draw();
    ramSpark.draw();
    netSpark.draw();
    klineChart.draw();
  });
}

// ================= CLOCK (MioLab 03.04: YYYY.MM.DD HH:mm:ss) =================
function updateClock() {
  const now = new Date();
  const y = now.getFullYear();
  const mo = String(now.getMonth() + 1).padStart(2, '0');
  const d = String(now.getDate()).padStart(2, '0');
  const hh = String(now.getHours()).padStart(2, '0');
  const mm = String(now.getMinutes()).padStart(2, '0');
  const ss = String(now.getSeconds()).padStart(2, '0');
  document.getElementById('clock-datetime').textContent = `${y}.${mo}.${d} ${hh}:${mm}:${ss}`;
}
updateClock();
setInterval(updateClock, 1000);

// ================= OLED PIXEL SHIFT =================
let oledShiftStep = 0;
const OLED_OFFSETS = [[0, 0], [1, 0], [1, 1], [0, 1], [-1, 1], [-1, 0], [-1, -1], [0, -1]];
setInterval(() => {
  const root = document.getElementById('hud-root');
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
          renderWatchlist();
          updateFxReadout();
          if (state.lastTelemetry) renderProcesses(state.lastTelemetry.topProcesses || []);
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
          renderWatchlist();
        }
        break;
    }
  });
}

function handleInit(data) {
  if (data.settings) {
    state.settings = { ...state.settings, ...data.settings };
  }
  state.symbols = data.symbols || [];
  handleWindowState({
    isFullScreen: data.isFullScreen,
    alwaysOnTop: data.alwaysOnTop,
    lockWindowPosition: data.lockWindowPosition ?? state.settings.lockWindowPosition ?? true,
    windowPositionPreset: data.windowPositionPreset || state.settings.windowPositionPreset || 'custom',
    targetMonitorDevice: data.targetMonitorDevice || state.settings.targetMonitorDevice,
    monitors: data.monitors || []
  });

  if (Array.isArray(data.history) && data.history.length > 0) {
    cpuCoreGrid.hydrate(data.history);
    for (const pt of data.history) {
      cpuSpark.push([pt.cpu || 0]);
      gpuSpark.push([pt.gpu || 0]);
      ramSpark.push([pt.ram || 0]);
      netSpark.push([pt.netDn || 0, pt.netUp || 0]);
    }
  }

  applyVisualSettings();
  renderWatchlist();
  updateFxReadout();
}

function handleWindowState(data) {
  state.isFullScreen = !!data.isFullScreen;
  state.alwaysOnTop = !!data.alwaysOnTop;
  if (data.lockWindowPosition !== undefined) state.lockWindowPosition = !!data.lockWindowPosition;
  if (data.windowPositionPreset) state.windowPositionPreset = data.windowPositionPreset;
  if (data.targetMonitorDevice) state.targetMonitorDevice = data.targetMonitorDevice;
  if (Array.isArray(data.monitors)) state.monitors = data.monitors;

  document.documentElement.setAttribute('data-fullscreen', state.isFullScreen ? 'true' : 'false');
  document.documentElement.setAttribute('data-window-locked', state.lockWindowPosition ? 'true' : 'false');

  const badge = document.getElementById('window-mode-badge');
  if (badge) {
    badge.className = 'status-pill';
    if (state.isFullScreen) {
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
  redrawAllCharts();
}

function applyTopBarVisibility() {
  const hideBar = !!state.settings.hideMonitorTopBar && (state.isFullScreen || state.lockWindowPosition);
  document.documentElement.setAttribute('data-hide-topbar', hideBar ? 'true' : 'false');
}

function applyVisualSettings() {
  const s = state.settings;
  const theme = s.theme === 'dark' ? 'dark' : 'light';
  document.documentElement.setAttribute('data-theme', theme);
  document.documentElement.setAttribute('data-colors', s.colorScheme || 'cn');
  document.documentElement.setAttribute('data-layout', s.layoutMode || 'split');
  applyTopBarVisibility();

  document.getElementById('compact-hw-strip').classList.toggle('hidden', s.layoutMode !== 'market');
  document.getElementById('compact-market-strip').classList.toggle('hidden', s.layoutMode !== 'hardware');

  // CPU view mode (driven by Settings Window)
  const showCores = (s.cpuViewMode || 'cores') !== 'overall';
  document.getElementById('cpu-cores-wrap').classList.toggle('hidden', !showCores);
  document.getElementById('cpu-overall-wrap').classList.toggle('hidden', showCores);
  document.getElementById('cpu-core-hover').classList.toggle('hidden', !showCores);
  document.getElementById('cpu-view-pill').textContent = showCores ? '逻辑处理器' : '总体曲线';
  const nCores = state.lastTelemetry?.cpu?.coreUsages?.length || 0;
  document.getElementById('cpu-graph-caption').textContent = showCores
    ? (nCores > 0 ? `逻辑处理器（共 ${nCores} 个 · 60 秒实时曲线）` : '所有逻辑处理器（60 秒实时曲线）')
    : '总体利用率（60 秒实时曲线）';

  // Process sort readout (driven by Settings Window)
  const sortByMem = (s.processSortBy || 'cpu') === 'mem';
  document.getElementById('proc-sort-pill').textContent = sortByMem ? '按内存排序' : '按 CPU 排序';
  document.getElementById('col-hdr-cpu').textContent = sortByMem ? 'CPU 占用' : 'CPU 占用 ↓';
  document.getElementById('col-hdr-mem').textContent = sortByMem ? '内存占用 ↓' : '内存占用';
  document.getElementById('col-hdr-cpu').classList.toggle('col-sorted', !sortByMem);
  document.getElementById('col-hdr-mem').classList.toggle('col-sorted', sortByMem);

  // K-Line read-only interval & indicator badges
  const ivLabel = (s.activeInterval || '15m').toUpperCase();
  document.getElementById('kl-interval-pill').textContent = ivLabel === '1M' || ivLabel === '5M' || ivLabel === '15M'
    ? ivLabel.toLowerCase()
    : ivLabel;

  const activeInds = [];
  if (s.showMA) activeInds.push('MA');
  if (s.showBoll) activeInds.push('BOLL');
  if (s.showVolume) activeInds.push('VOL');
  document.getElementById('kl-indicators-pill').textContent = activeInds.length > 0 ? activeInds.join(' · ') : '纯 K 线';

  // Watchlist read-only category & exchange header
  const catNames = { crypto: '虚拟货币', cn: 'A 股', us: '美股', fx: '外汇 / 黄金', all: '全部行情' };
  document.getElementById('watchlist-cat-title').textContent = catNames[s.activeCategory] || '全部行情';

  const exPill = document.getElementById('watchlist-exchange-pill');
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

  klineChart.setOptions({ showMA: !!s.showMA, showBoll: !!s.showBoll, showVolume: !!s.showVolume });
  redrawAllCharts();
}

// ================= RENDER TELEMETRY =================
function renderTelemetry(t) {
  if (!t) return;

  document.getElementById('uptime-badge').textContent = formatUptime(t.uptimeSeconds);

  // ---- CPU ----
  const cpu = t.cpu || {};
  const cpuUsage = cpu.totalUsagePercent || 0;
  document.getElementById('cpu-name').textContent = cpu.name || 'CPU';
  document.getElementById('cpu-pct').textContent = `${Math.round(cpuUsage)}%`;
  document.getElementById('cpu-clock').textContent =
    `${(cpu.currentClockGhz || 0).toFixed(2)} / ${(cpu.baseClockGhz || 0).toFixed(2)} GHz`;
  document.getElementById('cpu-cores').textContent =
    `${cpu.physicalCores || 0} 核 / ${cpu.logicalProcessors || 0} 线程`;

  const tempStr = cpu.temperatureC != null ? `${Math.round(cpu.temperatureC)} °C` : '—';
  const powerStr = cpu.powerWatts != null ? `${Math.round(cpu.powerWatts)} W` : '—';
  document.getElementById('cpu-temp-power').textContent = `${tempStr} / ${powerStr}`;

  const cpuHealth = document.getElementById('cpu-health-pill');
  cpuHealth.className = 'status-pill';
  if (cpu.temperatureC != null && cpu.temperatureC >= 85) {
    cpuHealth.textContent = `过热 ${Math.round(cpu.temperatureC)} °C`;
    cpuHealth.classList.add('danger');
  } else if (cpu.temperatureC != null && cpu.temperatureC >= 72) {
    cpuHealth.textContent = `偏热 ${Math.round(cpu.temperatureC)} °C`;
    cpuHealth.classList.add('warning');
  } else if (cpuUsage >= 90) {
    cpuHealth.textContent = '高负载';
    cpuHealth.classList.add('danger');
  } else if (cpuUsage >= 75) {
    cpuHealth.textContent = '负载偏高';
    cpuHealth.classList.add('warning');
  } else {
    cpuHealth.textContent = '健康正常';
    cpuHealth.classList.add('success');
  }

  cpuSpark.push([cpuUsage]);
  if (Array.isArray(cpu.coreUsages) && cpu.coreUsages.length > 0) {
    cpuCoreGrid.push(cpu.coreUsages);
    if ((state.settings.cpuViewMode || 'cores') !== 'overall') {
      document.getElementById('cpu-graph-caption').textContent =
        `逻辑处理器（共 ${cpu.coreUsages.length} 个 · 60 秒实时曲线）`;
    }
  }

  // ---- GPU ----
  const gpu = t.gpu || {};
  const gpuUsage = gpu.usagePercent || 0;
  document.getElementById('gpu-name').textContent = gpu.name || 'GPU';
  document.getElementById('gpu-pct').textContent = `${Math.round(gpuUsage)}%`;
  document.getElementById('gpu-clock').textContent = gpu.coreClockMhz ? `${Math.round(gpu.coreClockMhz)} MHz` : '—';

  const gTemp = gpu.temperatureC != null ? `${Math.round(gpu.temperatureC)} °C` : '—';
  const gHot = gpu.hotspotTempC != null ? `${Math.round(gpu.hotspotTempC)} °C` : '—';
  document.getElementById('gpu-temp').textContent = `${gTemp} / ${gHot}`;

  const gPwr = gpu.powerWatts != null ? `${Math.round(gpu.powerWatts)} W` : '—';
  const gFan = gpu.fanRpm != null
    ? `${Math.round(gpu.fanRpm)} RPM`
    : (gpu.fanPercent != null ? `${Math.round(gpu.fanPercent)}%` : '—');
  document.getElementById('gpu-power-fan').textContent = `${gPwr} / ${gFan}`;
  document.getElementById('gpu-mem-clk').textContent = gpu.memoryClockMhz ? `${Math.round(gpu.memoryClockMhz)} MHz` : '—';

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

  // ---- Network ----
  const net = t.network || {};
  document.getElementById('net-adapter').textContent = net.adapterName || '网络接口';
  document.getElementById('net-dn').textContent = formatSpeed(net.downloadBytesPerSec || 0);
  document.getElementById('net-up').textContent = formatSpeed(net.uploadBytesPerSec || 0);
  document.getElementById('net-session').textContent =
    `↓ ${formatMb(net.totalDownloadedMb || 0)} / ↑ ${formatMb(net.totalUploadedMb || 0)}`;
  netSpark.push([net.downloadBytesPerSec || 0, net.uploadBytesPerSec || 0]);

  // ---- Disk ----
  const disk = t.disk || {};
  document.getElementById('disk-read').textContent = formatSpeed(disk.readBytesPerSec || 0);
  document.getElementById('disk-write').textContent = formatSpeed(disk.writeBytesPerSec || 0);
  renderDrives(disk.drives || []);

  // ---- Top Processes ----
  renderProcesses(t.topProcesses || []);

  // ---- Compact Hardware Strip ----
  document.getElementById('strip-cpu').textContent = `${Math.round(cpuUsage)}% · ${(cpu.currentClockGhz || 0).toFixed(1)} GHz`;
  const gpuTempStr = gpu.temperatureC != null ? ` · ${Math.round(gpu.temperatureC)} °C` : '';
  document.getElementById('strip-gpu').textContent = `${Math.round(gpuUsage)}%${gpuTempStr}`;
  document.getElementById('strip-vram').textContent = `${(gpu.vramUsedGb || 0).toFixed(1)} / ${(gpu.vramTotalGb || 0).toFixed(0)} GB`;
  document.getElementById('strip-ram').textContent = `${memPct}% (${(mem.usedGb || 0).toFixed(1)} GB)`;
  document.getElementById('strip-net-dn').textContent = formatSpeed(net.downloadBytesPerSec || 0);
  document.getElementById('strip-net-up').textContent = formatSpeed(net.uploadBytesPerSec || 0);
}

function updateCpuCoreHover(info) {
  const el = document.getElementById('cpu-core-hover');
  if (!el) return;
  if (!info) {
    el.textContent = '悬停核心查看单核读数';
    return;
  }
  el.textContent = `核心 #${info.index} · 当前 ${Math.round(info.current)}% · 峰值 ${Math.round(info.peak)}% · 均值 ${Math.round(info.avg)}%`;
}

// ================= RENDER DOMESTIC / OVERSEAS IPv4 =================
function renderNetworkIp(info) {
  if (!info) return;
  const pill = document.getElementById('ip-status-pill');
  pill.className = 'status-pill';
  if (info.isChecking) {
    pill.textContent = '检测中';
    pill.classList.add('info');
  } else {
    pill.textContent = '仅 IPv4';
    pill.classList.add('neutral');
  }

  renderSingleIp(
    info.domestic,
    info.isChecking,
    document.getElementById('ip-domestic'),
    document.getElementById('ip-domestic-loc'),
    document.getElementById('strip-ip-cn')
  );

  renderSingleIp(
    info.overseas,
    info.isChecking,
    document.getElementById('ip-overseas'),
    document.getElementById('ip-overseas-loc'),
    document.getElementById('strip-ip-intl')
  );
}

function renderSingleIp(ep, isChecking, addrEl, locEl, stripEl) {
  addrEl.classList.remove('ip-err');
  if (ep && ep.ok && ep.ip) {
    addrEl.textContent = ep.ip;
    addrEl.title = ep.source ? `IPv4 来源：${ep.source}` : 'IPv4 地址';
    locEl.textContent = ep.location || '—';
    if (stripEl) stripEl.textContent = ep.ip;
  } else if (isChecking) {
    if (!addrEl.textContent || addrEl.textContent === '—' || addrEl.textContent === '未连通') {
      addrEl.textContent = '检测中';
    }
    if (!locEl.textContent) locEl.textContent = '—';
  } else {
    addrEl.textContent = '未连通';
    addrEl.classList.add('ip-err');
    locEl.textContent = '—';
    if (stripEl) stripEl.textContent = '—';
  }
}

function renderDrives(drives) {
  const container = document.getElementById('drive-list');
  if (!drives || drives.length === 0) {
    container.innerHTML = '<div class="label-muted">—</div>';
    return;
  }
  container.innerHTML = drives.map((d) => {
    const pct = Math.min(100, Math.round(d.usagePercent || 0));
    const cls = pct >= 90 ? 'danger' : (pct >= 80 ? 'warn' : '');
    return `
      <div class="drive-item">
        <div class="drive-top">
          <span class="drive-name">${escapeHtml(d.name)} <span class="label-muted">${escapeHtml(d.label)}</span></span>
          <span class="mono label-muted">可用 ${d.freeGb} GB / ${d.totalGb} GB (${pct}%)</span>
        </div>
        <div class="progress-track">
          <div class="progress-fill ${cls}" style="width: ${pct}%"></div>
        </div>
      </div>`;
  }).join('');
}

function renderProcesses(procs) {
  const container = document.getElementById('proc-list');
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
  container.innerHTML = sorted.slice(0, 5).map((p) => `
    <div class="proc-row" role="row">
      <span class="proc-name" role="cell" title="${escapeHtml(p.name)}">${escapeHtml(p.name)}</span>
      <span class="align-right mono label-muted" role="cell">${p.pid}</span>
      <span class="align-right mono" role="cell">${p.cpuPercent.toFixed(1)}%</span>
      <span class="align-right mono" role="cell">${p.memoryMb >= 1024 ? (p.memoryMb / 1024).toFixed(1) + ' GB' : Math.round(p.memoryMb) + ' MB'}</span>
    </div>`).join('');
}

// ================= RENDER MARKET & K-LINE =================
function handleQuotes(quotes) {
  if (!Array.isArray(quotes)) return;
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
  if (!payload) return;
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

// ================= READ-ONLY FX CONVERSION DISPLAY =================
function updateFxReadout() {
  const s = state.settings;
  const amount = Number(s.fxAmount ?? 100);
  const from = s.fxFromCurrency || 'USD';
  const to = s.fxToCurrency || 'CNY';

  const rFrom = state.fxRates[from];
  const rTo = state.fxRates[to];
  const exprEl = document.getElementById('fx-expression');
  const rateEl = document.getElementById('fx-unit-rate');

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

// ================= WINDOW DRAG (ONLY WHEN UNLOCKED IN SETTINGS) & SHORTCUTS =================
document.querySelectorAll('[data-drag="true"]').forEach((el) => {
  el.addEventListener('mousedown', (e) => {
    if (e.button === 0 && e.detail === 1 && !state.isFullScreen && !state.lockWindowPosition) {
      postHost('startDrag');
    }
  });
});

document.addEventListener('keydown', (e) => {
  if (e.key === 'F11') {
    e.preventDefault();
    postHost('toggleFullScreen');
  } else if (e.key === 'Escape') {
    if (state.isFullScreen) {
      postHost('setFullScreen', { value: false });
    }
  } else if (e.ctrlKey && e.key === ',') {
    e.preventDefault();
    postHost('openControlWindow');
  } else if (e.ctrlKey && e.key >= '1' && e.key <= '9') {
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

function escapeHtml(str) {
  return String(str || '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}
