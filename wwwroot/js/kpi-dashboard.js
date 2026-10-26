// KPI Dashboard widget rendering (bar chart + time-range slider) using D3 v7.
// Loaded as an ES module via Blazor JS interop. Multiple widgets can be mounted at once,
// so state is tracked per container id rather than via module-level singletons.
import * as d3 from 'https://cdn.jsdelivr.net/npm/d3@7/+esm';
import { makeDraggable } from './draggable.js';

const _widgetLayouts = new Map(); // widgetId -> cleanup fn
const _charts  = new Map(); // containerId -> cleanup fn
const _chartTokens = new Map(); // containerId -> latest scheduled render token
const _sliders = new Map(); // containerId -> { cleanup }

function sanitizeClass(name) {
    return String(name).replace(/[^a-zA-Z0-9]/g, '_');
}

// ── Bar tooltip ──────────────────────────────────────────────────────────────
// One shared element on <body> (not inside the widget, whose overflow would clip it).
// Blazor's enhanced navigation rebuilds <body>'s children and drops it, while this module (and
// the reference below) survives, so showTooltip() recreates the element whenever it was detached.

let _tooltipEl = null;

function prettyUnit(unit) {
    return unit ? unit.replace(/(\w)2\b/g, '$1\u00B2') : ''; // km2 -> km²
}

function formatValue(value) {
    // Small values (e.g. bycatch) keep three significant digits instead of rounding to 0.
    const options = Math.abs(value) < 1 ? { maximumSignificantDigits: 3 } : { maximumFractionDigits: 3 };
    return value.toLocaleString(undefined, options);
}

function showTooltip(event, text) {
    if (!_tooltipEl?.isConnected) {
        _tooltipEl = document.createElement('div');
        _tooltipEl.className = 'kpi-chart-tooltip';
        document.body.appendChild(_tooltipEl);
    }
    _tooltipEl.textContent = text;
    _tooltipEl.style.display = 'block';

    const gap = 14;
    const { width, height } = _tooltipEl.getBoundingClientRect();
    const fitsRight = event.clientX + gap + width <= window.innerWidth;
    _tooltipEl.style.left = (fitsRight ? event.clientX + gap : event.clientX - gap - width) + 'px';
    _tooltipEl.style.top  = Math.max(0, event.clientY - height / 2) + 'px';
}

function hideTooltip() {
    if (_tooltipEl) _tooltipEl.style.display = 'none';
}
// ── Chart ────────────────────────────────────────────────────────────────────

/**
 * Renders a grouped/stacked bar chart for one KPI widget.
 * @param {string} containerId
 * @param {string} seriesDataJson - JSON: { unit, stacked, series: [{ name, color, points: [{ month, value }] }] }
 */
export function renderWidget(containerId, seriesDataJson) {
    const container = document.getElementById(containerId);
    if (!container) return;
    disposeChart(containerId);
    container.innerHTML = '';

    const data = JSON.parse(seriesDataJson);
    if (!data?.series?.length) {
        container.innerHTML = '<div class="kpi-widget-empty">No bars selected</div>';
        return;
    }

    // Guard against a stale scheduled frame drawing after a newer renderWidget() call for
    // the same container (which would append a second, overlapping SVG since renderChart
    // doesn't know about earlier in-flight calls).
    const token = (_chartTokens.get(containerId) || 0) + 1;
    _chartTokens.set(containerId, token);

    // Allow one animation frame so the container is fully laid out before measuring.
    requestAnimationFrame(() => {
        if (_chartTokens.get(containerId) !== token) return;
        renderChart(container, data);
    });
    // Redraws on any size change of the container (widget resize/snap as well as window resize).
    const observer = new ResizeObserver(() => {
        requestAnimationFrame(() => {
            if (_chartTokens.get(containerId) !== token) return;
            renderChart(container, data);
        });
    });
    observer.observe(container);
    _charts.set(containerId, () => { observer.disconnect(); container.innerHTML = ''; });
}

function renderChart(container, data) {
    container.innerHTML = ''; // defensive: never stack multiple SVGs in the same container
    hideTooltip(); // the hovered bar may be about to be replaced, which fires no mouseleave
    const W = container.clientWidth  || 300;
    const H = container.clientHeight || 180;
    const margin = { top: 28, right: 8, bottom: 22, left: 42 };
    const innerW = Math.max(10, W - margin.left - margin.right);
    const innerH = Math.max(10, H - margin.top - margin.bottom);

    const months = Array.from(new Set(data.series.flatMap(s => s.points.map(p => p.month)))).sort((a, b) => a - b);
    if (months.length === 0) return;

    const x = d3.scaleBand().domain(months).range([0, innerW]).padding(0.25);
    const xSub = d3.scaleBand().domain(data.series.map(s => s.name)).range([0, x.bandwidth()]).padding(0.1);

    const maxValue = d3.max(data.series.flatMap(s => s.points.map(p => p.value))) || 1;
    const y = d3.scaleLinear().domain([0, maxValue * 1.1]).range([innerH, 0]).nice();

    const svg = d3.create('svg').attr('width', W).attr('height', H);
    const g = svg.append('g').attr('transform', `translate(${margin.left},${margin.top})`);

    // Label width budget decides how many ticks fit, so labels never overlap at any widget width.
    const tickLabelWidth = 62;
    const maxTicks = Math.max(1, Math.floor(innerW / tickLabelWidth));
    const tickStep = Math.ceil(months.length / maxTicks);
    const labelByMonth = new Map(data.series.flatMap(s => s.points.map(p => [p.month, p.label])));

    g.append('g')
        .attr('transform', `translate(0,${innerH})`)
        .call(d3.axisBottom(x)
            .tickValues(months.filter((_, i) => i % tickStep === 0))
            .tickFormat(m => labelByMonth.get(m) ?? m))
        .attr('font-family', null) // d3-axis forces sans-serif; inherit the app font instead
        .selectAll('text').attr('fill', '#f8f9fa').style('font-size', 'var(--font-size-small)');

    g.append('g')
        .call(d3.axisLeft(y).ticks(4))
        .attr('font-family', null)
        .selectAll('text').attr('fill', '#f8f9fa').style('font-size', 'var(--font-size-small)');

    g.selectAll('.domain, .tick line').attr('stroke', '#6c757d');

    if (data.unit) {
        svg.append('text')
            .attr('x', 4).attr('y', 11)
            .attr('fill', '#adb5bd').style('font-size', 'var(--font-size-small)')
            .text(prettyUnit(data.unit));
    }

    for (const series of data.series) {
        g.selectAll(`rect.bar-${sanitizeClass(series.name)}`)
            .data(series.points)
            .join('rect')
            .attr('class', `bar-${sanitizeClass(series.name)}`)
            .attr('x', d => x(d.month) + xSub(series.name))
            .attr('width', xSub.bandwidth())
            .attr('y', d => y(Math.max(0, d.value)))
            .attr('height', d => Math.abs(y(d.value) - y(0)))
            .attr('fill', series.color)
            .on('mouseenter mousemove', (event, d) =>
                showTooltip(event, `${series.name}: ${formatValue(d.value)}${data.unit ? ' ' + prettyUnit(data.unit) : ''}`))
            .on('mouseleave', hideTooltip);
    }

    container.appendChild(svg.node());
}

export function disposeChart(containerId) {
    hideTooltip();
    _charts.get(containerId)?.();
    _charts.delete(containerId);
}

/** Viewport position of an element, for placing viewport-fixed popups next to it. */
export function getElementRect(elementId) {
    const rect = document.getElementById(elementId)?.getBoundingClientRect();
    return { left: rect?.left ?? 0, bottom: rect?.bottom ?? 0, viewportWidth: window.innerWidth };
}

// ── Time-range slider (d3-brush) ──────────────────────────────────────────────

/**
 * Renders a two-handle horizontal range slider spanning [minMonth, maxMonth], with the selected
 * start and end month shown below the edges of the selection. It redraws itself whenever its
 * container changes size.
 * monthLabels[i] is the display text of month (minMonth + i).
 * Invokes dotNetRef.OnRangeChanged(startMonth, endMonth) when the player has dragged a handle.
 */
export function renderRangeSlider(containerId, minMonth, maxMonth, selectedStart, selectedEnd, monthLabels, dotNetRef) {
    const container = document.getElementById(containerId);
    if (!container) return;
    disposeRangeSlider(containerId);

    const clampedMax = Math.max(minMonth, maxMonth);
    // A zero-width domain (e.g. session still at month 0) makes scaleLinear divide by zero
    // and return NaN, which breaks brush.move() and throws inside this function.
    const domainMax = clampedMax > minMonth ? clampedMax : minMonth + 1;
    const state = { start: selectedStart ?? minMonth, end: selectedEnd ?? clampedMax };
    const labelOf = month => monthLabels?.[month - minMonth] ?? String(month);

    const margin = 10;        // room for the handles, which straddle the ends of the track
    const trackH = 8, handleH = 20, trackY = 11;
    const labelGap = 6;
    const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

    let drawnSize = '';
    const draw = () => {
        const W = container.clientWidth || 240;
        const H = container.clientHeight || 44;
        drawnSize = `${W}x${H}`;
        container.innerHTML = '';

        const innerW = Math.max(10, W - margin * 2);
        const scale = d3.scaleLinear().domain([minMonth, domainMax]).range([0, innerW]).clamp(true);
        const toMonth = x => Math.round(scale.invert(x));

        const svg = d3.create('svg').attr('width', W).attr('height', H);
        const g = svg.append('g').attr('transform', `translate(${margin},0)`);

        g.append('rect').attr('class', 'kpi-slider-track')
            .attr('x', 0).attr('y', trackY - trackH / 2).attr('width', innerW).attr('height', trackH).attr('rx', trackH / 2);
        const fill = g.append('rect').attr('class', 'kpi-slider-fill')
            .attr('y', trackY - trackH / 2).attr('height', trackH);

        const labelStart = svg.append('text').attr('class', 'kpi-slider-label').attr('y', H - 4);
        const labelEnd   = svg.append('text').attr('class', 'kpi-slider-label').attr('y', H - 4);

        // x0/x1 are the selection edges in track pixels.
        const update = (x0, x1) => {
            fill.attr('x', x0).attr('width', Math.max(0, x1 - x0));

            const m0 = toMonth(x0), m1 = toMonth(x1);
            labelStart.text(labelOf(m0));
            labelEnd.text(m0 === m1 ? '' : labelOf(m1));

            const w0 = labelStart.node().getBBox().width;
            const w1 = m0 === m1 ? 0 : labelEnd.node().getBBox().width;
            // 1px inset: glyphs overhang their text box slightly, which would clip at the SVG edge.
            let lx = clamp(margin + x0 - w0 / 2, 1, W - w0 - 1);
            let rx = clamp(margin + x1 - w1 / 2, 1, W - w1 - 1);
            if (m0 === m1 || lx + w0 + labelGap > rx) {
                // Too close together: show the labels side by side, centred under the selection.
                const total = w0 + (w1 ? labelGap + w1 : 0);
                lx = clamp(margin + (x0 + x1) / 2 - total / 2, 1, Math.max(1, W - total - 1));
                rx = lx + w0 + labelGap;
            }
            labelStart.attr('x', lx);
            labelEnd.attr('x', rx);
        };

        // d3-brush makes its handles handleSize taller than the extent (half of it above, half below),
        // so the extent is shrunk by that amount to get handles of exactly handleH, centred on the track.
        const handleSize = 8;
        const extentH = handleH - handleSize;
        const brush = d3.brushX()
            .extent([[0, trackY - extentH / 2], [innerW, trackY + extentH / 2]])
            .handleSize(handleSize)
            .on('start brush end', event => {
                if (!event.selection) {
                    // A plain click on the track clears a brush; put the current selection back.
                    if (event.type === 'end' && event.sourceEvent)
                        brushG.call(brush.move, [scale(state.start), scale(state.end)]);
                    return;
                }
                const [x0, x1] = event.selection;
                update(x0, x1);

                // event.sourceEvent is only set for genuine user drags, so our own programmatic
                // brush.move() calls never loop between JS and .NET.
                if (event.type === 'end' && event.sourceEvent) {
                    state.start = toMonth(x0);
                    state.end   = toMonth(x1);
                    dotNetRef.invokeMethodAsync('OnRangeChanged', state.start, state.end);
                }
            });

        // The SVG must be in the DOM before brush.move(), because update() measures label widths.
        container.appendChild(svg.node());
        const brushG = g.append('g').attr('class', 'kpi-slider-brush').call(brush);
        brushG.call(brush.move, [scale(state.start), scale(state.end)]);
    };

    draw();

    let frame = 0;
    const observer = new ResizeObserver(() => {
        cancelAnimationFrame(frame);
        frame = requestAnimationFrame(() => {
            if (`${container.clientWidth || 240}x${container.clientHeight || 44}` !== drawnSize) draw();
        });
    });
    observer.observe(container);

    _sliders.set(containerId, {
        cleanup: () => { cancelAnimationFrame(frame); observer.disconnect(); container.innerHTML = ''; }
    });
}
export function disposeRangeSlider(containerId) {
    _sliders.get(containerId)?.cleanup?.();
    _sliders.delete(containerId);
}

// ── Widget layout (drag to move, native resize handle snapped to grid spans) ──

// The grid itself is the source of truth: read the resolved track sizes rather than
// duplicating the CSS (auto-fill columns, grid-auto-rows, gap) here.
function gridMetrics(grid) {
    const cs = getComputedStyle(grid);
    const rect = grid.getBoundingClientRect();
    const colWidths = cs.gridTemplateColumns.split(' ').map(parseFloat);
    return {
        columns: colWidths.length,
        colStep: colWidths[0] + parseFloat(cs.columnGap),
        rowStep: parseFloat(cs.gridAutoRows) + parseFloat(cs.rowGap),
        colGap:  parseFloat(cs.columnGap),
        rowGap:  parseFloat(cs.rowGap),
        originX: rect.left + parseFloat(cs.borderLeftWidth) + parseFloat(cs.paddingLeft),
        originY: rect.top  + parseFloat(cs.borderTopWidth)  + parseFloat(cs.paddingTop),
    };
}

const MIN_SPAN_W = 3; // narrower than this can't fit a legend row, the chart and the title bar
const MIN_SPAN_H = 2;

/**
 * Wires up moving (drag the title bar) and resizing (native CSS resize handle) of one widget.
 * Both gestures are converted to grid cells and reported to
 * dotNetRef.OnMoved(column, row, columnCount) / dotNetRef.OnResized(spanW, spanH, columnCount).
 */
export function initWidgetLayout(widgetId, dotNetRef) {
    const widgetEl = document.getElementById(widgetId);
    if (!widgetEl) return;
    disposeWidgetLayout(widgetId);

    const cleanups = [];

    const handle = widgetEl.querySelector('.kpi-widget-title');
    if (handle) {
        cleanups.push(makeDraggable(handle, {
            onStart: () => widgetEl.classList.add('kpi-widget--dragging'),
            onMove: (dx, dy) => { widgetEl.style.transform = `translate(${dx}px, ${dy}px)`; },
            onEnd: (dx, dy, cancelled) => {
                // Measure while the drag transform is still applied.
                const rect = widgetEl.getBoundingClientRect();
                widgetEl.classList.remove('kpi-widget--dragging');
                widgetEl.style.transform = '';
                if (cancelled || (Math.abs(dx) < 3 && Math.abs(dy) < 3)) return;

                const m = gridMetrics(widgetEl.parentElement);
                const column = Math.round((rect.left - m.originX) / m.colStep);
                const row    = Math.round((rect.top  - m.originY) / m.rowStep);
                dotNetRef.invokeMethodAsync('OnMoved', column, row, m.columns);
            },
        }));
    }

    // The browser's resize handle sets inline width/height on the element. Once the gesture
    // settles, convert that to grid spans and clear it so the grid placement takes over again.
    let timer;
    const observer = new ResizeObserver(() => {
        if (!widgetEl.style.width && !widgetEl.style.height) return; // grid-driven change, not a user resize
        clearTimeout(timer);
        timer = setTimeout(() => {
            const m = gridMetrics(widgetEl.parentElement);
            const w = Math.max(MIN_SPAN_W, Math.round((widgetEl.offsetWidth  + m.colGap) / m.colStep));
            const h = Math.max(MIN_SPAN_H, Math.round((widgetEl.offsetHeight + m.rowGap) / m.rowStep));
            widgetEl.style.width = widgetEl.style.height = '';
            dotNetRef.invokeMethodAsync('OnResized', w, h, m.columns);
        }, 150);
    });
    observer.observe(widgetEl);
    cleanups.push(() => { clearTimeout(timer); observer.disconnect(); });

    _widgetLayouts.set(widgetId, () => cleanups.forEach(fn => fn()));
}

export function disposeWidgetLayout(widgetId) {
    _widgetLayouts.get(widgetId)?.();
    _widgetLayouts.delete(widgetId);
}