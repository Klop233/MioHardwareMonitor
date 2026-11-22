// High-DPI Task-Manager Logical Processor Grid & Flat MioLab Sparkline Renderers

function readToken(name, fallback) {
  const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return v || fallback;
}

function hexToRgba(color, alpha) {
  if (!color || !color.startsWith('#')) return `rgba(43, 95, 217, ${alpha})`;
  let hex = color.slice(1);
  if (hex.length === 3) hex = hex.split('').map((c) => c + c).join('');
  const r = parseInt(hex.slice(0, 2), 16) || 43;
  const g = parseInt(hex.slice(2, 4), 16) || 95;
  const b = parseInt(hex.slice(4, 6), 16) || 217;
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

/**
 * Windows Task Manager style Logical Processors Grid.
 * Renders all N logical processors on a single high-DPI canvas with
 * per-core 60-sample utilization curves, subtle grid lines, core index,
 * live percentage readout, and hover inspection.
 */
export class CpuCoreGridChart {
  constructor(canvas, onHoverCore = null, maxPoints = 60) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.onHoverCore = onHoverCore;
    this.maxPoints = maxPoints;
    this.coreHistories = [];
    this.hoverIndex = -1;
    this._cellRects = [];

    this._bindEvents();
    this._ro = new ResizeObserver(() => this.draw());
    this._ro.observe(canvas);
  }

  dispose() {
    try { this._ro.disconnect(); } catch {}
    if (this._onMoveBound) this.canvas.removeEventListener('mousemove', this._onMoveBound);
    if (this._onLeaveBound) this.canvas.removeEventListener('mouseleave', this._onLeaveBound);
  }

  hydrateCoreArrays(coreArrays) {
    if (!Array.isArray(coreArrays)) return;
    for (const arr of coreArrays) {
      if (Array.isArray(arr) && arr.length > 0) {
        this.push(arr, false);
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

  push(coreUsages) {
    if (!Array.isArray(coreUsages) || coreUsages.length === 0) return;
    this._append(coreUsages);
    this.draw();
    if (this.hoverIndex >= 0) this._emitHover();
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

  _chooseGrid(n, width, height) {
    if (n <= 1) return { cols: 1, rows: 1 };
    if (n === 2) return width >= height ? { cols: 2, rows: 1 } : { cols: 1, rows: 2 };
    if (n === 4) return { cols: 2, rows: 2 };

    // Choose cols/rows giving each cell a clean Task-Manager aspect ratio (~1.6–2.4)
    let bestCols = Math.ceil(Math.sqrt(n));
    let bestScore = Infinity;
    const targetAspect = 1.9;

    for (let cols = 2; cols <= Math.min(n, 8); cols++) {
      const rows = Math.ceil(n / cols);
      const cellW = width / cols;
      const cellH = height / rows;
      if (cellW < 54 || cellH < 28) continue;
      const aspect = cellW / Math.max(1, cellH);
      const emptySlots = cols * rows - n;
      const score = Math.abs(Math.log(aspect / targetAspect)) + emptySlots * 0.22;
      if (score < bestScore) {
        bestScore = score;
        bestCols = cols;
      }
    }

    return { cols: bestCols, rows: Math.ceil(n / bestCols) };
  }

  draw() {
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
      ctx.font = '14px "Noto Sans SC", "Microsoft YaHei UI", sans-serif';
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

    const gap = n > 16 ? 4 : 6;
    const { cols, rows } = this._chooseGrid(n, rect.width, rect.height);
    const cellW = (rect.width - gap * (cols - 1)) / cols;
    const cellH = (rect.height - gap * (rows - 1)) / rows;

    this._cellRects = new Array(n);
    const showLabels = cellW >= 62 && cellH >= 32;
    const labelFontPx = cellH >= 64 && cellW >= 110 ? 13 : (cellH >= 46 && cellW >= 82 ? 12 : 11);

    for (let i = 0; i < n; i++) {
      const col = i % cols;
      const row = Math.floor(i / cols);
      const x = Math.round(col * (cellW + gap));
      const y = Math.round(row * (cellH + gap));
      const w = Math.floor(cellW);
      const h = Math.floor(cellH);
      this._cellRects[i] = { x, y, w, h };

      const hist = this.coreHistories[i];
      const latest = hist.length > 0 ? hist[hist.length - 1] : 0;
      const isHovered = i === this.hoverIndex;

      // Cell container (flat MioLab surface with 4px radius)
      ctx.save();
      roundRectPath(ctx, x + 0.5, y + 0.5, w - 1, h - 1, 4);
      ctx.fillStyle = bgPage;
      ctx.fill();
      ctx.clip();

      // Subtle Task Manager internal gridlines (horizontal 50% + vertical 25/50/75%)
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

      // Utilization series curve
      const strokeColor = latest >= 90 ? danger : (latest >= 75 ? warning : accent);
      const fillAlpha = isDark ? 0.24 : 0.14;
      const padTop = showLabels ? Math.min(18, Math.round(h * 0.34)) : 3;
      const plotH = Math.max(8, h - padTop - 2);

      if (hist.length >= 2) {
        const stepX = (w - 2) / (this.maxPoints - 1);
        const startX = x + w - 1 - (hist.length - 1) * stepX;

        ctx.beginPath();
        for (let k = 0; k < hist.length; k++) {
          const px = startX + k * stepX;
          const py = y + h - 1 - (hist[k] / 100) * plotH;
          if (k === 0) ctx.moveTo(px, py);
          else ctx.lineTo(px, py);
        }

        ctx.strokeStyle = strokeColor;
        ctx.lineWidth = 1.5;
        ctx.lineJoin = 'round';
        ctx.stroke();

        ctx.lineTo(x + w - 1, y + h - 1);
        ctx.lineTo(startX, y + h - 1);
        ctx.closePath();
        ctx.fillStyle = hexToRgba(strokeColor, fillAlpha);
        ctx.fill();
      }

      // Per-core top labels (#0 left, 24% right)
      if (showLabels) {
        ctx.font = `500 ${labelFontPx}px "JetBrains Mono", Consolas, monospace`;
        ctx.textBaseline = 'top';

        ctx.textAlign = 'left';
        ctx.fillStyle = textMuted;
        ctx.fillText(`#${i}`, x + 5, y + 3);

        ctx.textAlign = 'right';
        ctx.fillStyle = latest >= 90 ? danger : (latest >= 75 ? warning : textMain);
        ctx.fillText(`${Math.round(latest)}%`, x + w - 5, y + 3);
      }

      ctx.restore();

      // Crisp 1px outer border (accent on hover)
      roundRectPath(ctx, x + 0.5, y + 0.5, w - 1, h - 1, 4);
      ctx.strokeStyle = isHovered ? accent : border;
      ctx.lineWidth = isHovered ? 1.5 : 1;
      ctx.stroke();
    }

    ctx.restore();
  }
}

/**
 * Clean flat sparkline chart strictly following MioLab tokens (no glow/shadow).
 */
export class SparklineChart {
  constructor(canvas, seriesConfigs, maxPoints = 60, fixedMax = 100) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.seriesConfigs = seriesConfigs; // [{ colorVar: '--accent', fillAlpha: 0.14, dashed: false }]
    this.maxPoints = maxPoints;
    this.fixedMax = fixedMax; // null = auto scale
    this.seriesData = seriesConfigs.map(() => []);

    this._ro = new ResizeObserver(() => this.draw());
    this._ro.observe(canvas);
  }

  dispose() {
    try { this._ro.disconnect(); } catch {}
  }

  push(values) {
    for (let i = 0; i < this.seriesData.length; i++) {
      const arr = this.seriesData[i];
      arr.push(Number(values[i]) || 0);
      if (arr.length > this.maxPoints) arr.shift();
    }
    this.draw();
  }

  draw() {
    const rect = this.canvas.getBoundingClientRect();
    if (rect.width <= 4 || rect.height <= 4) return;

    const dpr = window.devicePixelRatio || 1;
    const w = Math.round(rect.width * dpr);
    const h = Math.round(rect.height * dpr);
    if (this.canvas.width !== w || this.canvas.height !== h) {
      this.canvas.width = w;
      this.canvas.height = h;
    }

    const ctx = this.ctx;
    ctx.save();
    ctx.scale(dpr, dpr);
    ctx.clearRect(0, 0, rect.width, rect.height);

    const W = rect.width;
    const H = rect.height;
    const padY = 4;
    const border = readToken('--border', '#E4E7EC');
    const isDark = document.documentElement.getAttribute('data-theme') === 'dark';

    // Subtle 50% gridline
    ctx.strokeStyle = border;
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(0, Math.round(H * 0.5) + 0.5);
    ctx.lineTo(W, Math.round(H * 0.5) + 0.5);
    ctx.stroke();

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

    for (let s = 0; s < this.seriesData.length; s++) {
      const arr = this.seriesData[s];
      if (arr.length < 2) continue;
      const cfg = this.seriesConfigs[s];
      const color = readToken(cfg.colorVar, '#2B5FD9');

      const stepX = W / (this.maxPoints - 1);
      const offsetX = W - (arr.length - 1) * stepX;

      ctx.save();
      if (cfg.dashed) ctx.setLineDash([4, 3]);
      ctx.beginPath();
      for (let i = 0; i < arr.length; i++) {
        const x = offsetX + i * stepX;
        const norm = Math.max(0, Math.min(1, arr[i] / maxVal));
        const y = H - padY - norm * (H - padY * 2);
        if (i === 0) ctx.moveTo(x, y);
        else ctx.lineTo(x, y);
      }

      ctx.strokeStyle = color;
      ctx.lineWidth = 1.5;
      ctx.lineJoin = 'round';
      ctx.stroke();
      ctx.restore();

      if (cfg.fillAlpha) {
        ctx.lineTo(W, H);
        ctx.lineTo(offsetX, H);
        ctx.closePath();
        ctx.fillStyle = hexToRgba(color, isDark ? cfg.fillAlpha * 1.5 : cfg.fillAlpha);
        ctx.fill();
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
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.arcTo(x + w, y, x + w, y + h, r);
  ctx.arcTo(x + w, y + h, x, y + h, r);
  ctx.arcTo(x, y + h, x, y, r);
  ctx.arcTo(x, y, x + w, y, r);
  ctx.closePath();
}
