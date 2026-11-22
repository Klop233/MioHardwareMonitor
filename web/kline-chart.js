// High-DPI Candlestick (K-Line) Chart Engine aligned with MioLab Web UI Spec v1.0
// Dynamically consumes Light & Dark design tokens; zero hardcoded dark backgrounds.

export class KlineChart {
  constructor(canvas, onHoverChange) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.onHoverChange = onHoverChange;

    this.candles = [];
    this.decimals = 4;
    this.showMA = true;
    this.showBoll = false;
    this.showVolume = true;

    this.ma5 = [];
    this.ma10 = [];
    this.ma20 = [];
    this.boll = null;

    this.visibleCount = 85;
    this.rightOffset = 0;
    this.hoverIdx = -1;
    this.hoverX = -1;
    this.hoverY = -1;

    this.isDragging = false;
    this.dragStartX = 0;
    this.dragStartOffset = 0;

    this._bindEvents();
    this._ro = new ResizeObserver(() => this.draw());
    this._ro.observe(canvas);
  }

  setData(candles, decimals = 4, resetView = false) {
    const prevLen = this.candles.length;
    this.candles = Array.isArray(candles) ? candles : [];
    this.decimals = decimals;
    this._computeIndicators();

    if (resetView || prevLen === 0) {
      this.visibleCount = Math.min(Math.max(this.candles.length, 30), 90);
      this.rightOffset = 0;
    } else {
      this._clampViewport();
    }

    this.draw();
    this._emitHover();
  }

  setOptions({ showMA, showBoll, showVolume }) {
    if (typeof showMA === 'boolean') this.showMA = showMA;
    if (typeof showBoll === 'boolean') this.showBoll = showBoll;
    if (typeof showVolume === 'boolean') this.showVolume = showVolume;
    this.draw();
  }

  resetView() {
    this.visibleCount = Math.min(Math.max(this.candles.length, 30), 90);
    this.rightOffset = 0;
    this.draw();
  }

  _computeIndicators() {
    const n = this.candles.length;
    this.ma5 = calcSMA(this.candles, 5);
    this.ma10 = calcSMA(this.candles, 10);
    this.ma20 = calcSMA(this.candles, 20);

    const upper = new Array(n).fill(null);
    const mid = this.ma20;
    const lower = new Array(n).fill(null);
    for (let i = 19; i < n; i++) {
      const mean = mid[i];
      let sumSq = 0;
      for (let j = i - 19; j <= i; j++) {
        const d = this.candles[j].close - mean;
        sumSq += d * d;
      }
      const std = Math.sqrt(sumSq / 20);
      upper[i] = mean + 2 * std;
      lower[i] = mean - 2 * std;
    }
    this.boll = { upper, mid, lower };
  }

  _clampViewport() {
    const n = this.candles.length;
    if (n === 0) {
      this.rightOffset = 0;
      return;
    }
    this.visibleCount = Math.max(20, Math.min(Math.max(n, 20), Math.min(240, this.visibleCount)));
    const maxOffset = Math.max(0, n - this.visibleCount);
    this.rightOffset = Math.max(0, Math.min(maxOffset, Math.round(this.rightOffset)));
  }

  _getVisibleRange() {
    const n = this.candles.length;
    if (n === 0) return { start: 0, end: 0 };
    const end = Math.max(0, n - this.rightOffset);
    const start = Math.max(0, end - this.visibleCount);
    return { start, end };
  }

  _bindEvents() {
    const c = this.canvas;

    c.addEventListener('wheel', (e) => {
      if (this.candles.length === 0) return;
      e.preventDefault();
      const factor = e.deltaY > 0 ? 1.14 : 0.88;
      this.visibleCount = Math.round(this.visibleCount * factor);
      this._clampViewport();
      this.draw();
    }, { passive: false });

    c.addEventListener('mousedown', (e) => {
      if (e.button !== 0 || this.candles.length === 0) return;
      this.isDragging = true;
      this.dragStartX = e.clientX;
      this.dragStartOffset = this.rightOffset;
      c.style.cursor = 'grabbing';
    });

    window.addEventListener('mousemove', (e) => {
      if (this.isDragging) {
        const rect = c.getBoundingClientRect();
        const plotW = Math.max(100, rect.width - 84);
        const pxPerBar = plotW / Math.max(1, this.visibleCount);
        const deltaBars = Math.round((e.clientX - this.dragStartX) / Math.max(2, pxPerBar));
        this.rightOffset = this.dragStartOffset + deltaBars;
        this._clampViewport();
        this.draw();
      }
    });

    window.addEventListener('mouseup', () => {
      if (this.isDragging) {
        this.isDragging = false;
        c.style.cursor = 'crosshair';
      }
    });

    c.addEventListener('mousemove', (e) => {
      if (this.candles.length === 0) return;
      const rect = c.getBoundingClientRect();
      this.hoverX = e.clientX - rect.left;
      this.hoverY = e.clientY - rect.top;
      this._updateHoverIndex(rect.width);
      this.draw();
      this._emitHover();
    });

    c.addEventListener('mouseleave', () => {
      this.hoverIdx = -1;
      this.hoverX = -1;
      this.hoverY = -1;
      this.draw();
      this._emitHover();
    });

    c.addEventListener('dblclick', () => {
      this.resetView();
    });
  }

  _updateHoverIndex(width) {
    const padL = 8;
    const padR = 80;
    const plotW = Math.max(40, width - padL - padR);
    const { start, end } = this._getVisibleRange();
    const count = end - start;
    if (count <= 0 || this.hoverX < padL || this.hoverX > padL + plotW) {
      this.hoverIdx = -1;
      return;
    }
    const rel = (this.hoverX - padL) / plotW;
    const idx = start + Math.min(count - 1, Math.max(0, Math.floor(rel * count)));
    this.hoverIdx = idx;
  }

  _emitHover() {
    if (!this.onHoverChange || this.candles.length === 0) return;
    const idx = this.hoverIdx >= 0 && this.hoverIdx < this.candles.length
      ? this.hoverIdx
      : this.candles.length - 1;
    const candle = this.candles[idx];
    const prev = idx > 0 ? this.candles[idx - 1].close : candle.open;
    this.onHoverChange({
      candle,
      prevClose: prev,
      ma5: this.ma5[idx],
      ma10: this.ma10[idx],
      ma20: this.ma20[idx],
      decimals: this.decimals
    });
  }

  draw() {
    const rect = this.canvas.getBoundingClientRect();
    if (rect.width <= 10 || rect.height <= 10) return;

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

    const W = rect.width;
    const H = rect.height;
    ctx.clearRect(0, 0, W, H);

    if (this.candles.length === 0) {
      ctx.restore();
      return;
    }

    const styles = getComputedStyle(document.documentElement);
    const tok = (k, fb) => styles.getPropertyValue(k).trim() || fb;

    const bgSurface = tok('--bg-surface', '#FFFFFF');
    const border = tok('--border', '#E4E7EC');
    const borderStrong = tok('--border-strong', '#8A94A3');
    const textMain = tok('--text', '#1F2937');
    const textMuted = tok('--text-muted', '#5B6472');
    const accentColor = tok('--accent', '#2B5FD9');
    const upColor = tok('--up-color', '#B42318');
    const downColor = tok('--down-color', '#1F7A4A');
    const ma5Color = tok('--ma5', '#8A5300');
    const ma10Color = tok('--ma10', '#2B5FD9');
    const ma20Color = tok('--ma20', '#5B6472');
    const bollFill = tok('--boll-fill', 'rgba(43, 95, 217, 0.06)');

    const { start, end } = this._getVisibleRange();
    const count = end - start;
    if (count <= 0) {
      ctx.restore();
      return;
    }

    let maxVol = 0;
    for (let i = start; i < end; i++) {
      if (this.candles[i].volume > maxVol) maxVol = this.candles[i].volume;
    }
    const drawVol = this.showVolume && maxVol > 0;

    const padL = 8;
    const padR = 80;
    const padT = 34;
    const padB = 24;
    const plotW = Math.max(40, W - padL - padR);
    const totalH = Math.max(60, H - padT - padB);
    const volH = drawVol ? Math.round(totalH * 0.2) : 0;
    const volGap = drawVol ? 8 : 0;
    const priceH = totalH - volH - volGap;

    let minP = Infinity;
    let maxP = -Infinity;
    for (let i = start; i < end; i++) {
      const c = this.candles[i];
      if (c.low < minP) minP = c.low;
      if (c.high > maxP) maxP = c.high;
      if (this.showBoll && this.boll) {
        if (this.boll.upper[i] !== null && this.boll.upper[i] > maxP) maxP = this.boll.upper[i];
        if (this.boll.lower[i] !== null && this.boll.lower[i] < minP) minP = this.boll.lower[i];
      }
    }

    if (!isFinite(minP) || !isFinite(maxP)) {
      ctx.restore();
      return;
    }
    const rawRange = maxP - minP || Math.max(Math.abs(maxP) * 0.002, 0.0001);
    const margin = rawRange * 0.08;
    minP -= margin;
    maxP += margin;
    const pRange = maxP - minP;

    const yForPrice = (p) => padT + (1 - (p - minP) / pRange) * priceH;
    const stepX = plotW / count;
    const xForIdx = (i) => padL + (i - start + 0.5) * stepX;

    // 1. Horizontal gridlines + Y labels
    const yTicks = 5;
    ctx.font = '12px "JetBrains Mono", Consolas, monospace';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'middle';

    for (let t = 0; t <= yTicks; t++) {
      const frac = t / yTicks;
      const y = Math.round(padT + frac * priceH) + 0.5;
      const priceVal = maxP - frac * pRange;

      ctx.strokeStyle = border;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(padL, y);
      ctx.lineTo(padL + plotW, y);
      ctx.stroke();

      ctx.fillStyle = textMuted;
      ctx.fillText(this._fmtPrice(priceVal), padL + plotW + 6, y);
    }

    // Vertical grid & X time ticks
    const xTicks = Math.max(3, Math.min(7, Math.floor(plotW / 120)));
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    for (let t = 0; t < xTicks; t++) {
      const idx = Math.min(end - 1, start + Math.round((t / Math.max(1, xTicks - 1)) * (count - 1)));
      const x = Math.round(xForIdx(idx)) + 0.5;

      ctx.strokeStyle = border;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(x, padT);
      ctx.lineTo(x, H - padB);
      ctx.stroke();

      ctx.fillStyle = textMuted;
      ctx.fillText(this.candles[idx].timeLabel || '', x, H - padB + 6);
    }

    // 2. Bollinger Bands
    if (this.showBoll && this.boll) {
      ctx.beginPath();
      let started = false;
      for (let i = start; i < end; i++) {
        const u = this.boll.upper[i];
        if (u === null) continue;
        const x = xForIdx(i);
        const y = yForPrice(u);
        if (!started) { ctx.moveTo(x, y); started = true; }
        else ctx.lineTo(x, y);
      }
      for (let i = end - 1; i >= start; i--) {
        const l = this.boll.lower[i];
        if (l === null) continue;
        ctx.lineTo(xForIdx(i), yForPrice(l));
      }
      if (started) {
        ctx.closePath();
        ctx.fillStyle = bollFill;
        ctx.fill();
      }
      this._drawLine(this.boll.upper, start, end, xForIdx, yForPrice, borderStrong, 1);
      this._drawLine(this.boll.lower, start, end, xForIdx, yForPrice, borderStrong, 1);
    }

    // 3. Candlesticks & Volume bars
    const candleW = Math.max(2, Math.min(18, stepX * 0.66));
    const volTop = padT + priceH + volGap;

    if (drawVol) {
      ctx.strokeStyle = border;
      ctx.beginPath();
      ctx.moveTo(padL, volTop - 3.5);
      ctx.lineTo(padL + plotW, volTop - 3.5);
      ctx.stroke();
    }

    for (let i = start; i < end; i++) {
      const c = this.candles[i];
      const isUp = c.close >= c.open;
      const color = isUp ? upColor : downColor;
      const cx = Math.round(xForIdx(i)) + 0.5;

      const yH = yForPrice(c.high);
      const yL = yForPrice(c.low);
      const yO = yForPrice(c.open);
      const yC = yForPrice(c.close);

      ctx.strokeStyle = color;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(cx, yH);
      ctx.lineTo(cx, yL);
      ctx.stroke();

      const bodyTop = Math.min(yO, yC);
      const bodyH = Math.max(1.5, Math.abs(yC - yO));
      ctx.fillStyle = color;
      ctx.fillRect(cx - candleW / 2, bodyTop, candleW, bodyH);

      if (drawVol && c.volume > 0) {
        const vh = Math.max(1, (c.volume / maxVol) * volH);
        ctx.fillStyle = color;
        ctx.globalAlpha = 0.35;
        ctx.fillRect(cx - candleW / 2, volTop + volH - vh, candleW, vh);
        ctx.globalAlpha = 1;
      }
    }

    // 4. Moving Averages
    if (this.showMA) {
      this._drawLine(this.ma5, start, end, xForIdx, yForPrice, ma5Color, 1.3);
      this._drawLine(this.ma10, start, end, xForIdx, yForPrice, ma10Color, 1.3);
      this._drawLine(this.ma20, start, end, xForIdx, yForPrice, ma20Color, 1.3);
    }

    // 5. Latest Price Dashed Line & Right-Axis Pill
    const last = this.candles[this.candles.length - 1];
    const lastY = Math.max(padT, Math.min(padT + priceH, yForPrice(last.close)));
    const lastColor = last.close >= last.open ? upColor : downColor;

    ctx.save();
    ctx.setLineDash([4, 4]);
    ctx.strokeStyle = lastColor;
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(padL, Math.round(lastY) + 0.5);
    ctx.lineTo(padL + plotW, Math.round(lastY) + 0.5);
    ctx.stroke();
    ctx.restore();

    const pillH = 20;
    const pillY = Math.max(padT, Math.min(padT + priceH - pillH, lastY - pillH / 2));
    ctx.fillStyle = lastColor;
    roundRect(ctx, padL + plotW + 2, pillY, padR - 4, pillH, 4);
    ctx.fill();
    ctx.fillStyle = '#FFFFFF';
    ctx.font = '500 12px "JetBrains Mono", Consolas, monospace';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'middle';
    ctx.fillText(this._fmtPrice(last.close), padL + plotW + 6, pillY + pillH / 2);

    // 6. Crosshair & Floating Labels (Light & Dark Token Safe)
    if (this.hoverIdx >= start && this.hoverIdx < end && !this.isDragging) {
      const cx = Math.round(xForIdx(this.hoverIdx)) + 0.5;
      const cy = Math.max(padT, Math.min(padT + priceH, this.hoverY));

      ctx.save();
      ctx.setLineDash([3, 3]);
      ctx.strokeStyle = borderStrong;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(cx, padT);
      ctx.lineTo(cx, H - padB);
      ctx.moveTo(padL, Math.round(cy) + 0.5);
      ctx.lineTo(padL + plotW, Math.round(cy) + 0.5);
      ctx.stroke();
      ctx.restore();

      const hoverPrice = maxP - ((cy - padT) / priceH) * pRange;
      ctx.fillStyle = bgSurface;
      ctx.strokeStyle = accentColor;
      ctx.lineWidth = 1;
      roundRect(ctx, padL + plotW + 2, cy - 10, padR - 4, 20, 4);
      ctx.fill();
      ctx.stroke();
      ctx.fillStyle = textMain;
      ctx.font = '500 12px "JetBrains Mono", Consolas, monospace';
      ctx.textAlign = 'left';
      ctx.textBaseline = 'middle';
      ctx.fillText(this._fmtPrice(hoverPrice), padL + plotW + 6, cy);

      const tLabel = this.candles[this.hoverIdx].timeLabel || '';
      const tw = Math.max(82, ctx.measureText(tLabel).width + 14);
      const tx = Math.max(padL, Math.min(padL + plotW - tw, cx - tw / 2));
      ctx.fillStyle = bgSurface;
      roundRect(ctx, tx, H - padB + 2, tw, 20, 4);
      ctx.fill();
      ctx.stroke();
      ctx.fillStyle = textMain;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(tLabel, tx + tw / 2, H - padB + 12);
    }

    ctx.restore();
  }

  _drawLine(arr, start, end, xForIdx, yForPrice, color, width) {
    const ctx = this.ctx;
    ctx.beginPath();
    let started = false;
    for (let i = start; i < end; i++) {
      const v = arr[i];
      if (v === null || v === undefined) continue;
      const x = xForIdx(i);
      const y = yForPrice(v);
      if (!started) { ctx.moveTo(x, y); started = true; }
      else ctx.lineTo(x, y);
    }
    if (started) {
      ctx.strokeStyle = color;
      ctx.lineWidth = width;
      ctx.stroke();
    }
  }

  _fmtPrice(v) {
    if (!isFinite(v)) return '—';
    if (Math.abs(v) >= 10000) return v.toFixed(1);
    return v.toFixed(this.decimals);
  }
}

function calcSMA(candles, period) {
  const out = new Array(candles.length).fill(null);
  let sum = 0;
  for (let i = 0; i < candles.length; i++) {
    sum += candles[i].close;
    if (i >= period) sum -= candles[i - period].close;
    if (i >= period - 1) out[i] = sum / period;
  }
  return out;
}

function roundRect(ctx, x, y, w, h, r) {
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.arcTo(x + w, y, x + w, y + h, r);
  ctx.arcTo(x + w, y + h, x, y + h, r);
  ctx.arcTo(x, y + h, x, y, r);
  ctx.arcTo(x, y, x + w, y, r);
  ctx.closePath();
}
