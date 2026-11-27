// High-DPI Precision Instrument Gauges, Compact & Task-Manager Core Grid, and Enhanced Sparklines
// Zero rAF idle spin — purely event-driven redraw on telemetry updates and ResizeObserver.

function readToken(name, fallback) {
  const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return v || fallback;
}

function isInstrumentMode() {
  return document.documentElement.getAttribute('data-style') !== 'flat';
}

function hexToRgba(color, alpha) {
  const a = Number.isFinite(alpha) ? Math.max(0, Math.min(1, alpha)) : 1;
  if (!color || typeof color !== 'string') return `rgba(43, 95, 217, ${a})`;
  const c = color.trim();

  // Support rgb(...) / rgba(...)
  if (c.startsWith('rgb')) {
    const m = c.match(/rgba?\(\s*(\d+(?:\.\d+)?)\s*[,\s]\s*(\d+(?:\.\d+)?)\s*[,\s]\s*(\d+(?:\.\d+)?)/i);
    if (m) {
      const r = Math.round(Number(m[1]));
      const g = Math.round(Number(m[2]));
      const b = Math.round(Number(m[3]));
      return `rgba(${Number.isNaN(r) ? 43 : r}, ${Number.isNaN(g) ? 95 : g}, ${Number.isNaN(b) ? 217 : b}, ${a})`;
    }
    return `rgba(43, 95, 217, ${a})`;
  }

  if (!c.startsWith('#')) return `rgba(43, 95, 217, ${a})`;
  let hex = c.slice(1);
  if (hex.length === 3 || hex.length === 4) {
    hex = hex.slice(0, 3).split('').map((ch) => ch + ch).join('');
  } else if (hex.length >= 6) {
    hex = hex.slice(0, 6);
  } else {
    return `rgba(43, 95, 217, ${a})`;
  }

  const pr = parseInt(hex.slice(0, 2), 16);
  const pg = parseInt(hex.slice(2, 4), 16);
  const pb = parseInt(hex.slice(4, 6), 16);
  const r = Number.isNaN(pr) ? 43 : pr;
  const g = Number.isNaN(pg) ? 95 : pg;
  const b = Number.isNaN(pb) ? 217 : pb;
  return `rgba(${r}, ${g}, ${b}, ${a})`;
}

/**
 * 240° Precision Instrument Radial Arc Gauge (CPU / GPU / RAM).
 * Crisp vector arcs + 25 outer graduations + tabular percentage + sub-caption.
 * Uses zero shadowBlur so software-composited Safe Mode & Direct Display render razor-sharp.
 */
export class RadialArcGauge {
  constructor(canvas, options = {}) {
    this.canvas = canvas;
    this.ctx = canvas ? canvas.getContext('2d') : null;
    this.sweepDeg = options.sweepDeg ?? 240;
    this.startDeg = options.startDeg ?? 150;
    this.label = options.label || '';
    this.subText = options.subLabel || '';
    this.warnAt = options.warnAt ?? 75;
    this.dangerAt = options.dangerAt ?? 90;
    this.value = 0;
    this.colorOverride = null;

    if (canvas) {
      this._ro = new ResizeObserver(() => this.draw());
      this._ro.observe(canvas);
    }
  }

  dispose() {
    try { this._ro?.disconnect(); } catch {}
  }

  setValue(percent, { subText, colorOverride } = {}) {
    const num = Number(percent);
    this.value = Number.isFinite(num) ? Math.max(0, Math.min(100, num)) : 0;
    if (subText !== undefined) this.subText = String(subText || '');
    this.colorOverride = colorOverride ?? null;
    this.draw();
  }

  draw() {
    if (!this.canvas || !this.ctx) return;
    const rect = this.canvas.getBoundingClientRect();
    if (rect.width <= 12 || rect.height <= 12) return;

    const dpr = window.devicePixelRatio || 1;
    const cw = Math.round(rect.width * dpr);
    const ch = Math.round(rect.height * dpr);
    if (this.canvas.width !== cw || this.canvas.height !== ch) {
      this.canvas.width = cw;
      this.canvas.height = ch;
    }

    const ctx = this.ctx;
    ctx.save();
    ctx.scale(dpr, dpr);
    ctx.clearRect(0, 0, rect.width, rect.height);

    const W = rect.width;
    const H = rect.height;
    const minDim = Math.min(W, H);
    const cx = W * 0.5;
    const cy = H * 0.54;

    const accent = readToken('--accent', '#2B5FD9');
    const warning = readToken('--warning', '#8A5300');
    const danger = readToken('--danger', '#B42318');
    const border = readToken('--border', '#E4E7EC');
    const borderStrong = readToken('--border-strong', '#8A94A3');
    const textMain = readToken('--text', '#1F2937');
    const textMuted = readToken('--text-muted', '#5B6472');

    const activeColor = this.colorOverride
      || (this.value >= this.dangerAt ? danger : (this.value >= this.warnAt ? warning : accent));

    const tickBand = minDim >= 64 ? 4.5 : 3;
    const trackW = Math.max(4, Math.min(6.5, minDim * 0.082));
    const r = Math.max(10, minDim * 0.5 - tickBand - trackW * 0.5 - 2);

    const startRad = (this.startDeg * Math.PI) / 180;
    const sweepRad = (this.sweepDeg * Math.PI) / 180;
    const endRad = startRad + sweepRad;
    const frac = this.value / 100;
    const valRad = startRad + sweepRad * frac;

    // 1. 25 precision outer graduation ticks
    const tickCount = 24;
    const rTickInner = r + trackW * 0.5 + 1.5;
    for (let i = 0; i <= tickCount; i++) {
      const t = i / tickCount;
      const ang = startRad + sweepRad * t;
      const isMajor = i % 6 === 0;
      const len = isMajor ? tickBand : tickBand * 0.55;
      const cos = Math.cos(ang);
      const sin = Math.sin(ang);
      ctx.beginPath();
      ctx.moveTo(cx + cos * rTickInner, cy + sin * rTickInner);
      ctx.lineTo(cx + cos * (rTickInner + len), cy + sin * (rTickInner + len));
      if (t <= frac && this.value > 0.5) {
        ctx.strokeStyle = isMajor ? activeColor : hexToRgba(activeColor, 0.55);
      } else {
        ctx.strokeStyle = isMajor ? borderStrong : border;
      }
      ctx.lineWidth = isMajor ? 1.4 : 1;
      ctx.stroke();
    }

    // 2. Subtle inner hairline ring
    if (r - trackW * 0.5 - 3 > 10) {
      ctx.beginPath();
      ctx.arc(cx, cy, r - trackW * 0.5 - 2.5, startRad, endRad);
      ctx.strokeStyle = hexToRgba(border, 0.65);
      ctx.lineWidth = 1;
      ctx.stroke();
    }

    // 3. Background arc track
    ctx.beginPath();
    ctx.arc(cx, cy, r, startRad, endRad);
    ctx.strokeStyle = border;
    ctx.lineWidth = trackW;
    ctx.lineCap = 'round';
    ctx.stroke();

    // 4. Filled active arc
    if (this.value > 0.2) {
      ctx.beginPath();
      ctx.arc(cx, cy, r, startRad, Math.max(startRad + 0.02, valRad));
      ctx.strokeStyle = activeColor;
      ctx.lineWidth = trackW;
      ctx.lineCap = 'round';
      ctx.stroke();
    }

    // 5. Optional top micro-label inside arc
    if (this.label && minDim >= 68) {
      const lblPx = Math.max(8.5, Math.min(10, minDim * 0.12));
      ctx.font = `600 ${lblPx.toFixed(1)}px "JetBrains Mono", Consolas, monospace`;
      ctx.fillStyle = textMuted;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(this.label, cx, cy - r * 0.44);
    }

    // 6. Center tabular percentage
    const numStr = `${Math.round(this.value)}`;
    const pctFontPx = Math.max(13, Math.min(22, Math.round(minDim * 0.255)));
    const symFontPx = Math.max(9, Math.round(pctFontPx * 0.56));
    const centerY = this.subText ? cy - minDim * 0.02 : cy + 1;

    ctx.font = `600 ${pctFontPx}px "JetBrains Mono", Consolas, monospace`;
    const numW = ctx.measureText(numStr).width;
    ctx.font = `500 ${symFontPx}px "JetBrains Mono", Consolas, monospace`;
    const symW = ctx.measureText('%').width;
    const totalW = numW + symW + 1;
    const startX = cx - totalW * 0.5;

    ctx.textAlign = 'left';
    ctx.textBaseline = 'middle';
    ctx.fillStyle = this.value >= this.dangerAt
      ? danger
      : (this.value >= this.warnAt ? warning : textMain);
    ctx.font = `600 ${pctFontPx}px "JetBrains Mono", Consolas, monospace`;
    ctx.fillText(numStr, startX, centerY);

    ctx.fillStyle = textMuted;
    ctx.font = `500 ${symFontPx}px "JetBrains Mono", Consolas, monospace`;
    ctx.fillText('%', startX + numW + 1, centerY + pctFontPx * 0.12);

    // 7. Sub-caption tucked neatly inside bottom opening
    if (this.subText && minDim >= 52) {
      const subPx = Math.max(9, Math.min(11.5, minDim * 0.14));
      ctx.font = `500 ${subPx.toFixed(1)}px "JetBrains Mono", Consolas, monospace`;
      ctx.fillStyle = textMuted;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(this.subText, cx, cy + r * 0.62, W - 6);
    }

    ctx.restore();
  }
}

/**
 * Dual-Mode High-DPI Logical Processor Visualizer:
 * - "compact" (NEW DEFAULT): Ultra-compact dense vertical load bars + peak tick + subtle live spark
 *   fitting 8–64+ threads cleanly in ~105–145px height (>50% height reduction!).
 * - "cores": Classic Windows Task Manager per-core utilization curves (columns lifted up to 16 for 64T CPUs;
 *   automatically falls back to dense vertical load bars when cells become too small).
 */
export class CpuCoreGridChart {
  constructor(canvas, onHoverCore = null, maxPoints = 60, options = {}) {
    this.canvas = canvas;
    this.ctx = canvas ? canvas.getContext('2d') : null;
    this.onHoverCore = onHoverCore;
    this.maxPoints = maxPoints;
    this.mode = options.mode === 'cores' ? 'cores' : 'compact';
    this.coreHistories = [];
    this.hoverIndex = -1;
    this._cellRects = [];

    if (canvas) {
      this._bindEvents();
      this._ro = new ResizeObserver(() => this.draw());
      this._ro.observe(canvas);
    }
  }

  dispose() {
    try { this._ro?.disconnect(); } catch {}
    if (this.canvas && this._onMoveBound) this.canvas.removeEventListener('mousemove', this._onMoveBound);
    if (this.canvas && this._onLeaveBound) this.canvas.removeEventListener('mouseleave', this._onLeaveBound);
  }

  setMode(mode) {
    const next = mode === 'cores' ? 'cores' : 'compact';
    if (this.mode !== next) {
      this.mode = next;
      this.draw();
    }
  }

  hydrateCoreArrays(coreArrays) {
    if (!Array.isArray(coreArrays)) return;
    for (const arr of coreArrays) {
      if (Array.isArray(arr) && arr.length > 0) {
        this._append(arr);
      }
    }
    this.draw();
  }

  _bindEvents() {
    this._onMoveBound = (e) => {
      const rect = this.canvas.getBoundingClientRect();
      const x = e.clientX - rect.left;
      const y = e.clientY - rect.top;
      let hit = -1;
      for (let i = 0; i < this._cellRects.length; i++) {
        const c = this._cellRects[i];
        if (x >= c.x && x <= c.x + c.w && y >= c.y && y <= c.y + c.h) {
          hit = i;
          break;
        }
      }
      if (hit !== this.hoverIndex) {
        this.hoverIndex = hit;
        this.draw();
        this._emitHover();
      }
    };

    this._onLeaveBound = () => {
      if (this.hoverIndex !== -1) {
        this.hoverIndex = -1;
        this.draw();
        this._emitHover();
      }
    };

    this.canvas.addEventListener('mousemove', this._onMoveBound);
    this.canvas.addEventListener('mouseleave', this._onLeaveBound);
  }

  _emitHover() {
    if (!this.onHoverCore) return;
    if (this.hoverIndex < 0 || this.hoverIndex >= this.coreHistories.length) {
      this.onHoverCore(null);
      return;
    }
    const hist = this.coreHistories[this.hoverIndex];
    const current = hist.length > 0 ? hist[hist.length - 1] : 0;
    let peak = 0;
    let sum = 0;
    for (const v of hist) {
      if (v > peak) peak = v;
      sum += v;
    }
    const avg = hist.length > 0 ? sum / hist.length : 0;
    this.onHoverCore({
      index: this.hoverIndex,
      count: this.coreHistories.length,
      current,
      peak,
      avg
    });
  }

  hydrate(historyPoints) {
    if (!Array.isArray(historyPoints)) return;
    for (const pt of historyPoints) {
      if (Array.isArray(pt.cores) && pt.cores.length > 0) {
        this._append(pt.cores);
      }
    }
    this.draw();
  }

  push(coreUsages, shouldDraw = true) {
    if (!Array.isArray(coreUsages) || coreUsages.length === 0) return;
    this._append(coreUsages);
    if (shouldDraw) {
      this.draw();
      if (this.hoverIndex >= 0) this._emitHover();
    }
  }

  _append(coreUsages) {
    if (this.coreHistories.length !== coreUsages.length) {
      this.coreHistories = coreUsages.map(() => []);
    }
    for (let i = 0; i < coreUsages.length; i++) {
      const arr = this.coreHistories[i];
      arr.push(Math.max(0, Math.min(100, Number(coreUsages[i]) || 0)));
      if (arr.length > this.maxPoints) arr.shift();
    }
  }

  _chooseCompactGrid(n, width, height) {
    if (n <= 1) return { cols: 1, rows: 1 };
    if (n <= 4) return width >= height * 1.4 ? { cols: n, rows: 1 } : { cols: 2, rows: Math.ceil(n / 2) };
    if (n <= 8) return width >= 380 && height < 96 ? { cols: n, rows: 1 } : { cols: Math.ceil(n / 2), rows: 2 };

    // Dense horizontal load-tile layout: prefer 2 rows for 12-32 threads, 4 rows for 33-64 threads
    const maxCols = Math.min(n, n > 32 ? 16 : 16);
    const minCols = Math.min(n, 4);
    let bestCols = Math.min(maxCols, Math.ceil(n / 2));
    let bestScore = Infinity;

    for (let cols = minCols; cols <= maxCols; cols++) {
      const rows = Math.ceil(n / cols);
      const cellW = width / cols;
      const cellH = height / rows;
      if (cellW < 18 || cellH < 20) continue;
      // Favor compact tile proportions (aspect 0.72..1.35) and minimal empty slots
      const aspect = cellW / Math.max(1, cellH);
      const targetAspect = height < 120 ? 0.95 : 1.1;
      const emptySlots = cols * rows - n;
      const rowPenalty = rows > 4 ? (rows - 4) * 0.45 : 0;
      const score = Math.abs(Math.log(aspect / targetAspect)) + emptySlots * 0.16 + rowPenalty;
      if (score < bestScore) {
        bestScore = score;
        bestCols = cols;
      }
    }

    return { cols: bestCols, rows: Math.ceil(n / bestCols) };
  }

  _chooseCurveGrid(n, width, height) {
    if (n <= 1) return { cols: 1, rows: 1 };
    if (n === 2) return width >= height ? { cols: 2, rows: 1 } : { cols: 1, rows: 2 };
    if (n === 4) return { cols: 2, rows: 2 };

    // Lifted column ceiling to Math.min(n, 16) for 32-64+ thread CPUs
    const maxCols = Math.min(n, 16);
    let bestCols = Math.min(maxCols, Math.ceil(Math.sqrt(n * (width / Math.max(1, height)) * 0.65)));
    bestCols = Math.max(2, bestCols);
    let bestScore = Infinity;
    const targetAspect = 1.75;

    for (let cols = 2; cols <= maxCols; cols++) {
      const rows = Math.ceil(n / cols);
      const cellW = width / cols;
      const cellH = height / rows;
      if (cellW < 28 || cellH < 18) continue;
      const aspect = cellW / Math.max(1, cellH);
      const emptySlots = cols * rows - n;
      const score = Math.abs(Math.log(aspect / targetAspect)) + emptySlots * 0.18;
      if (score < bestScore) {
        bestScore = score;
        bestCols = cols;
      }
    }

    return { cols: bestCols, rows: Math.ceil(n / bestCols) };
  }

  draw() {
    if (!this.canvas || !this.ctx) return;
    const rect = this.canvas.getBoundingClientRect();
    if (rect.width <= 10 || rect.height <= 10) return;

    const dpr = window.devicePixelRatio || 1;
    const cw = Math.round(rect.width * dpr);
    const ch = Math.round(rect.height * dpr);
    if (this.canvas.width !== cw || this.canvas.height !== ch) {
      this.canvas.width = cw;
      this.canvas.height = ch;
    }

    const ctx = this.ctx;
    ctx.save();
    ctx.scale(dpr, dpr);
    ctx.clearRect(0, 0, rect.width, rect.height);

    const n = this.coreHistories.length;
    if (n === 0) {
      ctx.fillStyle = readToken('--text-muted', '#5B6472');
      ctx.font = '13px "Noto Sans SC", "Microsoft YaHei UI", sans-serif';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText('正在采集逻辑处理器数据', rect.width / 2, rect.height / 2);
      ctx.restore();
      return;
    }

    const bgPage = readToken('--bg-page', '#F7F8FA');
    const border = readToken('--border', '#E4E7EC');
    const accent = readToken('--accent', '#2B5FD9');
    const warning = readToken('--warning', '#8A5300');
    const danger = readToken('--danger', '#B42318');
    const textMain = readToken('--text', '#1F2937');
    const textMuted = readToken('--text-muted', '#5B6472');
    const isDark = document.documentElement.getAttribute('data-theme') === 'dark';
    const instrument = isInstrumentMode();

    const isCompact = this.mode === 'compact';
    const gap = isCompact ? (n > 24 ? 3 : 4) : (n > 16 ? 4 : 5);
    const { cols, rows } = isCompact
      ? this._chooseCompactGrid(n, rect.width, rect.height)
      : this._chooseCurveGrid(n, rect.width, rect.height);

    const cellW = (rect.width - gap * (cols - 1)) / cols;
    const cellH = (rect.height - gap * (rows - 1)) / rows;
    const useDenseBars = isCompact || cellH < 28 || cellW < 44;

    this._cellRects = new Array(n);

    for (let i = 0; i < n; i++) {
      const col = i % cols;
      const row = Math.floor(i / cols);
      const x = Math.round(col * (cellW + gap));
      const y = Math.round(row * (cellH + gap));
      const w = Math.max(4, Math.floor(cellW));
      const h = Math.max(4, Math.floor(cellH));
      this._cellRects[i] = { x, y, w, h };

      const hist = this.coreHistories[i];
      const latest = hist.length > 0 ? hist[hist.length - 1] : 0;
      let peak = latest;
      for (let k = 0; k < hist.length; k++) {
        if (hist[k] > peak) peak = hist[k];
      }
      const isHovered = i === this.hoverIndex;
      const strokeColor = latest >= 90 ? danger : (latest >= 75 ? warning : accent);
      const radius = w >= 28 && h >= 26 ? 4 : 2;

      ctx.save();
      roundRectPath(ctx, x + 0.5, y + 0.5, w - 1, h - 1, radius);
      ctx.fillStyle = bgPage;
      ctx.fill();
      ctx.clip();

      if (useDenseBars) {
        this._drawCompactBarCell(ctx, {
          i, x, y, w, h, hist, latest, peak, strokeColor, border, textMain, textMuted, isDark, instrument
        });
      } else {
        this._drawCurveCell(ctx, {
          i, x, y, w, h, hist, latest, strokeColor, border, textMain, textMuted, isDark, instrument
        });
      }

      ctx.restore();

      roundRectPath(ctx, x + 0.5, y + 0.5, w - 1, h - 1, radius);
      ctx.strokeStyle = isHovered ? accent : border;
      ctx.lineWidth = isHovered ? 1.5 : 1;
      ctx.stroke();
    }

    ctx.restore();
  }

  _drawCompactBarCell(ctx, p) {
    const { i, x, y, w, h, hist, latest, peak, strokeColor, border, textMain, textMuted, isDark, instrument } = p;
    const showDualHeader = w >= 52 && h >= 34;
    const showCenterPct = !showDualHeader && w >= 26 && h >= 26;
    const headerH = showDualHeader ? 16 : (showCenterPct ? 13 : 2);
    const barBottom = y + h - 1;
    const barAvailH = Math.max(4, h - headerH - 2);
    const fillH = Math.max(latest > 0.5 ? 2 : 0, (latest / 100) * barAvailH);
    const fillTop = barBottom - fillH;

    // Subtle 50% horizontal guide inside compact bar
    if (barAvailH >= 18) {
      const midY = Math.round(barBottom - barAvailH * 0.5) + 0.5;
      ctx.strokeStyle = hexToRgba(border, 0.7);
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(x + 1, midY);
      ctx.lineTo(x + w - 1, midY);
      ctx.stroke();
    }

    // Vertical live load fill bar
    if (fillH > 0) {
      if (instrument) {
        const grad = ctx.createLinearGradient(0, fillTop, 0, barBottom);
        grad.addColorStop(0, hexToRgba(strokeColor, isDark ? 0.58 : 0.42));
        grad.addColorStop(1, hexToRgba(strokeColor, isDark ? 0.14 : 0.08));
        ctx.fillStyle = grad;
      } else {
        ctx.fillStyle = hexToRgba(strokeColor, isDark ? 0.36 : 0.22);
      }
      ctx.fillRect(x + 1, fillTop, w - 2, fillH);

      // Crisp 2px top level cap
      ctx.fillStyle = strokeColor;
      ctx.fillRect(x + 1, fillTop, w - 2, Math.min(2, fillH));
    }

    // Subtle peak-hold micro rule (when peak noticeably above current)
    if (peak >= 8 && peak > latest + 4) {
      const peakY = Math.round(barBottom - (peak / 100) * barAvailH) + 0.5;
      ctx.strokeStyle = hexToRgba(strokeColor, isDark ? 0.5 : 0.42);
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(x + 2, peakY);
      ctx.lineTo(x + w - 2, peakY);
      ctx.stroke();
    }

    // Mini sparkline trace inside spacious compact tiles
    if (hist.length >= 3 && w >= 42 && barAvailH >= 20) {
      const stepX = (w - 4) / (this.maxPoints - 1);
      const startX = x + w - 2 - (hist.length - 1) * stepX;
      ctx.beginPath();
      for (let k = 0; k < hist.length; k++) {
        const px = startX + k * stepX;
        const py = barBottom - (hist[k] / 100) * barAvailH;
        if (k === 0) ctx.moveTo(px, py);
        else ctx.lineTo(px, py);
      }
      ctx.strokeStyle = hexToRgba(strokeColor, isDark ? 0.85 : 0.78);
      ctx.lineWidth = 1.15;
      ctx.lineJoin = 'round';
      ctx.stroke();
    }

    // Readout labels
    if (showDualHeader) {
      const fontPx = w >= 78 && h >= 48 ? 11.5 : 10.5;
      ctx.font = `500 ${fontPx}px "JetBrains Mono", Consolas, monospace`;
      ctx.textBaseline = 'top';
      ctx.textAlign = 'left';
      ctx.fillStyle = textMuted;
      ctx.fillText(`#${i}`, x + 4, y + 3);

      ctx.textAlign = 'right';
      ctx.fillStyle = latest >= 90 ? p.strokeColor : (latest >= 75 ? p.strokeColor : textMain);
      ctx.fillText(`${Math.round(latest)}%`, x + w - 4, y + 3);
    } else if (showCenterPct) {
      ctx.font = `600 10px "JetBrains Mono", Consolas, monospace`;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'top';
      ctx.fillStyle = latest >= 75 ? strokeColor : textMain;
      ctx.fillText(`${Math.round(latest)}`, x + w * 0.5, y + 2);
    }
  }

  _drawCurveCell(ctx, p) {
    const { i, x, y, w, h, hist, latest, strokeColor, border, textMain, textMuted, isDark, instrument } = p;
    const showLabels = w >= 58 && h >= 30;
    const labelFontPx = h >= 62 && w >= 106 ? 12.5 : (h >= 44 && w >= 78 ? 11.5 : 10.5);

    // Subtle Task Manager internal gridlines
    ctx.strokeStyle = border;
    ctx.lineWidth = 1;
    ctx.beginPath();
    const midY = Math.round(y + h * 0.5) + 0.5;
    ctx.moveTo(x, midY);
    ctx.lineTo(x + w, midY);
    if (w >= 72) {
      for (let g = 1; g <= 3; g++) {
        const gx = Math.round(x + (w * g) / 4) + 0.5;
        ctx.moveTo(gx, y);
        ctx.lineTo(gx, y + h);
      }
    }
    ctx.stroke();

    const padTop = showLabels ? Math.min(18, Math.round(h * 0.34)) : 3;
    const plotH = Math.max(8, h - padTop - 2);
    const baselineY = y + h - 1;

    if (hist.length >= 2) {
      const stepX = (w - 2) / (this.maxPoints - 1);
      const startX = x + w - 1 - (hist.length - 1) * stepX;

      ctx.beginPath();
      for (let k = 0; k < hist.length; k++) {
        const px = startX + k * stepX;
        const py = baselineY - (hist[k] / 100) * plotH;
        if (k === 0) ctx.moveTo(px, py);
        else ctx.lineTo(px, py);
      }

      ctx.strokeStyle = strokeColor;
      ctx.lineWidth = 1.5;
      ctx.lineJoin = 'round';
      ctx.stroke();

      ctx.lineTo(x + w - 1, baselineY);
      ctx.lineTo(startX, baselineY);
      ctx.closePath();

      if (instrument) {
        const grad = ctx.createLinearGradient(0, y + padTop, 0, baselineY);
        grad.addColorStop(0, hexToRgba(strokeColor, isDark ? 0.32 : 0.22));
        grad.addColorStop(1, hexToRgba(strokeColor, 0.02));
        ctx.fillStyle = grad;
      } else {
        ctx.fillStyle = hexToRgba(strokeColor, isDark ? 0.22 : 0.14);
      }
      ctx.fill();
    }

    if (showLabels) {
      ctx.font = `500 ${labelFontPx}px "JetBrains Mono", Consolas, monospace`;
      ctx.textBaseline = 'top';
      ctx.textAlign = 'left';
      ctx.fillStyle = textMuted;
      ctx.fillText(`#${i}`, x + 5, y + 3);

      ctx.textAlign = 'right';
      ctx.fillStyle = latest >= 75 ? strokeColor : textMain;
      ctx.fillText(`${Math.round(latest)}%`, x + w - 5, y + 3);
    }
  }
}

/**
 * Enhanced Precision Sparkline Chart.
 * Features:
 * - Batch push(..., shouldDraw) & hydrate(seriesArrays) with zero startup redraw storms
 * - Fixed area-fill baseline (H - padY)
 * - Vertical gradient fill, dashed 25/50/75% rules, right-aligned Y-axis scale labels,
 *   peak-hold dashed indicator + pill, and lightweight hover crosshair inspection!
 */
export class SparklineChart {
  constructor(canvas, seriesConfigs, maxPoints = 60, fixedMax = 100, options = {}) {
    this.canvas = canvas;
    this.ctx = canvas ? canvas.getContext('2d') : null;
    this.seriesConfigs = seriesConfigs;
    this.maxPoints = maxPoints;
    this.fixedMax = fixedMax; // null = auto scale
    this.options = {
      formatTick: options.formatTick || ((v) => `${Math.round(v)}${options.unit || '%'}`),
      showScale: options.showScale !== false,
      showPeak: options.showPeak !== false,
      unit: options.unit ?? '%'
    };
    this.seriesData = seriesConfigs.map(() => []);
    this.hoverIdx = -1;

    if (canvas) {
      this._bindHover();
      this._ro = new ResizeObserver(() => this.draw());
      this._ro.observe(canvas);
    }
  }

  dispose() {
    try { this._ro?.disconnect(); } catch {}
    if (this.canvas && this._onMove) this.canvas.removeEventListener('mousemove', this._onMove);
    if (this.canvas && this._onLeave) this.canvas.removeEventListener('mouseleave', this._onLeave);
  }

  _bindHover() {
    this._onMove = (e) => {
      const rect = this.canvas.getBoundingClientRect();
      if (rect.width <= 10) return;
      const arr = this.seriesData[0] || [];
      if (arr.length < 2) return;
      const x = Math.max(0, Math.min(rect.width, e.clientX - rect.left));
      const stepX = rect.width / (this.maxPoints - 1);
      const offsetX = rect.width - (arr.length - 1) * stepX;
      const idx = Math.round((x - offsetX) / stepX);
      const clamped = Math.max(0, Math.min(arr.length - 1, idx));
      if (clamped !== this.hoverIdx) {
        this.hoverIdx = clamped;
        this.draw();
      }
    };
    this._onLeave = () => {
      if (this.hoverIdx !== -1) {
        this.hoverIdx = -1;
        this.draw();
      }
    };
    this.canvas.addEventListener('mousemove', this._onMove);
    this.canvas.addEventListener('mouseleave', this._onLeave);
  }

  hydrate(samples) {
    if (!Array.isArray(samples)) return;
    this.seriesData = this.seriesConfigs.map(() => []);
    for (const values of samples) {
      if (Array.isArray(values)) {
        this.push(values, false);
      }
    }
    this.draw();
  }

  push(values, shouldDraw = true) {
    for (let i = 0; i < this.seriesData.length; i++) {
      const arr = this.seriesData[i];
      arr.push(Number(values[i]) || 0);
      if (arr.length > this.maxPoints) arr.shift();
    }
    if (shouldDraw) this.draw();
  }

  draw() {
    if (!this.canvas || !this.ctx) return;
    const rect = this.canvas.getBoundingClientRect();
    if (rect.width <= 4 || rect.height <= 4) return;

    const dpr = window.devicePixelRatio || 1;
    const cw = Math.round(rect.width * dpr);
    const ch = Math.round(rect.height * dpr);
    if (this.canvas.width !== cw || this.canvas.height !== ch) {
      this.canvas.width = cw;
      this.canvas.height = ch;
    }

    const ctx = this.ctx;
    ctx.save();
    ctx.scale(dpr, dpr);
    ctx.clearRect(0, 0, rect.width, rect.height);

    const W = rect.width;
    const H = rect.height;
    const padY = 4;
    const plotH = Math.max(4, H - padY * 2);
    const baselineY = H - padY;
    const border = readToken('--border', '#E4E7EC');
    const textMuted = readToken('--text-muted', '#5B6472');
    const bgSurface = readToken('--bg-surface', '#FFFFFF');
    const textMain = readToken('--text', '#1F2937');
    const isDark = document.documentElement.getAttribute('data-theme') === 'dark';
    const rich = isInstrumentMode() || this.fixedMax === null;

    // 1. Horizontal grid rules (25/50/75% in rich mode when tall enough, else 50%)
    ctx.save();
    ctx.strokeStyle = hexToRgba(border, rich ? 0.85 : 1);
    ctx.lineWidth = 1;
    if (rich) ctx.setLineDash([3, 3]);
    const gridFracs = rich && H >= 44 ? [0.25, 0.5, 0.75] : [0.5];
    ctx.beginPath();
    for (const f of gridFracs) {
      const gy = Math.round(padY + plotH * f) + 0.5;
      ctx.moveTo(0, gy);
      ctx.lineTo(W, gy);
    }
    ctx.stroke();
    ctx.restore();

    // 2. Resolve Y-axis max
    let maxVal = this.fixedMax;
    if (maxVal === null) {
      maxVal = 1024;
      for (const arr of this.seriesData) {
        for (const v of arr) {
          if (v > maxVal) maxVal = v;
        }
      }
      maxVal *= 1.15;
    }

    // Peak of primary series (series 0)
    const primaryArr = this.seriesData[0] || [];
    let primaryPeak = 0;
    for (let i = 0; i < primaryArr.length; i++) {
      if (primaryArr[i] > primaryPeak) primaryPeak = primaryArr[i];
    }

    // 3. Series paths + vertical gradient fill (closed cleanly at baselineY = H - padY!)
    const stepX = W / (this.maxPoints - 1);
    for (let s = 0; s < this.seriesData.length; s++) {
      const arr = this.seriesData[s];
      if (arr.length < 2) continue;
      const cfg = this.seriesConfigs[s];
      const color = readToken(cfg.colorVar, '#2B5FD9');
      const offsetX = W - (arr.length - 1) * stepX;

      ctx.save();
      if (cfg.dashed) ctx.setLineDash([4, 3]);
      ctx.beginPath();
      for (let i = 0; i < arr.length; i++) {
        const x = offsetX + i * stepX;
        const norm = Math.max(0, Math.min(1, arr[i] / maxVal));
        const y = baselineY - norm * plotH;
        if (i === 0) ctx.moveTo(x, y);
        else ctx.lineTo(x, y);
      }

      ctx.strokeStyle = color;
      ctx.lineWidth = s === 0 ? 1.6 : 1.3;
      ctx.lineJoin = 'round';
      ctx.stroke();
      ctx.restore();

      if (cfg.fillAlpha) {
        ctx.beginPath();
        for (let i = 0; i < arr.length; i++) {
          const x = offsetX + i * stepX;
          const norm = Math.max(0, Math.min(1, arr[i] / maxVal));
          const y = baselineY - norm * plotH;
          if (i === 0) ctx.moveTo(x, y);
          else ctx.lineTo(x, y);
        }
        ctx.lineTo(W, baselineY);
        ctx.lineTo(offsetX, baselineY);
        ctx.closePath();

        if (rich) {
          const grad = ctx.createLinearGradient(0, padY, 0, baselineY);
          const topAlpha = isDark ? Math.min(0.32, cfg.fillAlpha * 1.85) : Math.min(0.24, cfg.fillAlpha * 1.45);
          grad.addColorStop(0, hexToRgba(color, topAlpha));
          grad.addColorStop(1, hexToRgba(color, 0.02));
          ctx.fillStyle = grad;
        } else {
          ctx.fillStyle = hexToRgba(color, isDark ? cfg.fillAlpha * 1.5 : cfg.fillAlpha);
        }
        ctx.fill();
      }
    }

    // 4. Peak-hold dashed line + compact peak badge (top-left) in rich mode
    if (rich && this.options.showPeak && primaryPeak > 0 && H >= 34 && W >= 110) {
      const pColor = readToken(this.seriesConfigs[0]?.colorVar || '--accent', '#2B5FD9');
      const peakNorm = Math.max(0, Math.min(1, primaryPeak / maxVal));
      const peakY = Math.round(baselineY - peakNorm * plotH) + 0.5;

      ctx.save();
      ctx.setLineDash([2, 3]);
      ctx.strokeStyle = hexToRgba(pColor, isDark ? 0.55 : 0.45);
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(0, peakY);
      ctx.lineTo(W, peakY);
      ctx.stroke();
      ctx.restore();

      if (this.hoverIdx < 0) {
        const peakText = `峰 ${this.options.formatTick(primaryPeak)}`;
        ctx.font = '500 10px "JetBrains Mono", Consolas, monospace';
        const tw = ctx.measureText(peakText).width;
        const bx = 4;
        const by = 3;
        const bw = tw + 8;
        const bh = 14;
        roundRectPath(ctx, bx, by, bw, bh, 3);
        ctx.fillStyle = hexToRgba(bgSurface, isDark ? 0.84 : 0.88);
        ctx.fill();
        ctx.strokeStyle = hexToRgba(border, 0.9);
        ctx.lineWidth = 1;
        ctx.stroke();

        ctx.fillStyle = textMuted;
        ctx.textAlign = 'left';
        ctx.textBaseline = 'middle';
        ctx.fillText(peakText, bx + 4, by + bh * 0.5 + 0.5);
      }
    }

    // 5. Right-aligned Y-axis scale labels
    if (rich && this.options.showScale && W >= 130 && H >= 30) {
      ctx.font = '500 9.5px "JetBrains Mono", Consolas, monospace';
      ctx.textAlign = 'right';
      ctx.fillStyle = hexToRgba(textMuted, 0.85);
      ctx.textBaseline = 'top';
      ctx.fillText(this.options.formatTick(maxVal), W - 4, 2);
      if (H >= 52) {
        ctx.textBaseline = 'middle';
        ctx.fillText(this.options.formatTick(maxVal * 0.5), W - 4, Math.round(padY + plotH * 0.5));
      }
    }

    // 6. Lightweight hover crosshair + value readout pill
    if (this.hoverIdx >= 0 && primaryArr.length >= 2) {
      const idx = Math.min(this.hoverIdx, primaryArr.length - 1);
      const offsetX = W - (primaryArr.length - 1) * stepX;
      const hx = Math.round(offsetX + idx * stepX) + 0.5;

      ctx.save();
      ctx.setLineDash([2, 2]);
      ctx.strokeStyle = hexToRgba(textMuted, 0.7);
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(hx, padY);
      ctx.lineTo(hx, baselineY);
      ctx.stroke();
      ctx.restore();

      const parts = [];
      for (let s = 0; s < this.seriesData.length; s++) {
        const arr = this.seriesData[s];
        if (idx >= arr.length) continue;
        const val = arr[idx];
        const cfg = this.seriesConfigs[s];
        const color = readToken(cfg.colorVar, '#2B5FD9');
        const norm = Math.max(0, Math.min(1, val / maxVal));
        const hy = baselineY - norm * plotH;

        ctx.beginPath();
        ctx.arc(hx, hy, 2.8, 0, Math.PI * 2);
        ctx.fillStyle = color;
        ctx.fill();
        ctx.strokeStyle = bgSurface;
        ctx.lineWidth = 1.2;
        ctx.stroke();

        parts.push(cfg.label ? `${cfg.label} ${this.options.formatTick(val)}` : this.options.formatTick(val));
      }

      if (parts.length > 0) {
        const tip = parts.join(' · ');
        ctx.font = '500 10px "JetBrains Mono", Consolas, monospace';
        const tw = ctx.measureText(tip).width;
        const bw = tw + 10;
        const bh = 15;
        let bx = hx + 6;
        if (bx + bw > W - 4) bx = Math.max(4, hx - bw - 6);
        const by = 3;

        roundRectPath(ctx, bx, by, bw, bh, 3);
        ctx.fillStyle = hexToRgba(bgSurface, 0.94);
        ctx.fill();
        ctx.strokeStyle = border;
        ctx.lineWidth = 1;
        ctx.stroke();

        ctx.fillStyle = textMain;
        ctx.textAlign = 'left';
        ctx.textBaseline = 'middle';
        ctx.fillText(tip, bx + 5, by + bh * 0.5 + 0.5);
      }
    }

    ctx.restore();
  }
}

export function buildMiniSparkSvg(points, isUp) {
  if (!points || points.length < 2) {
    return '<svg class="quote-spark" viewBox="0 0 64 24" aria-hidden="true"></svg>';
  }
  let min = Infinity;
  let max = -Infinity;
  for (const p of points) {
    if (p < min) min = p;
    if (p > max) max = p;
  }
  const range = max - min || 1;
  const coords = points.map((p, i) => {
    const x = (i / (points.length - 1)) * 62 + 1;
    const y = 22 - ((p - min) / range) * 20;
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  });
  const stroke = isUp ? 'var(--up-color)' : 'var(--down-color)';
  return `<svg class="quote-spark" viewBox="0 0 64 24" preserveAspectRatio="none" aria-hidden="true">
    <polyline fill="none" stroke="${stroke}" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" points="${coords.join(' ')}"/>
  </svg>`;
}

function roundRectPath(ctx, x, y, w, h, r) {
  const rad = Math.min(r, w * 0.5, h * 0.5);
  ctx.beginPath();
  ctx.moveTo(x + rad, y);
  ctx.arcTo(x + w, y, x + w, y + h, rad);
  ctx.arcTo(x + w, y + h, x, y + h, rad);
  ctx.arcTo(x, y + h, x, y, rad);
  ctx.arcTo(x, y, x + w, y, rad);
  ctx.closePath();
}
