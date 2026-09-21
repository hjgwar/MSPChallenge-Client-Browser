// KPI Dashboard widget rendering (bar chart + time-range slider) using D3 v7.
// Loaded as an ES module via Blazor JS interop. Multiple widgets can be mounted at once,
// so state is tracked per container id rather than via module-level singletons.
import * as d3 from 'https://cdn.jsdelivr.net/npm/d3@7/+esm';

const _charts  = new Map(); // containerId -> cleanup fn
const _chartTokens = new Map(); // containerId -> latest scheduled render token
const _sliders = new Map(); // containerId -> { cleanup }

function sanitizeClass(name) {
    return String(name).replace(/[^a-zA-Z0-9]/g, '_');
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
    if (!data?.series?.length) return;

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
    _charts.set(containerId, () => { container.innerHTML = ''; });
}

function renderChart(container, data) {
    container.innerHTML = ''; // defensive: never stack multiple SVGs in the same container
    const W = container.clientWidth  || 300;
    const H = container.clientHeight || 180;
    const margin = { top: 16, right: 8, bottom: 22, left: 42 };
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

    g.append('g')
        .attr('transform', `translate(0,${innerH})`)
        .call(d3.axisBottom(x).tickValues(months.filter((_, i) => i % Math.ceil(months.length / 8 || 1) === 0)))
        .selectAll('text').attr('fill', '#f8f9fa').attr('font-size', 9);

    g.append('g')
        .call(d3.axisLeft(y).ticks(4))
        .selectAll('text').attr('fill', '#f8f9fa').attr('font-size', 9);

    g.selectAll('.domain, .tick line').attr('stroke', '#6c757d');

    if (data.unit) {
        svg.append('text')
            .attr('x', 4).attr('y', 11)
            .attr('fill', '#adb5bd').attr('font-size', 9)
            .text(data.unit);
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
            .attr('fill', series.color);
    }

    container.appendChild(svg.node());
}

export function disposeChart(containerId) {
    _charts.get(containerId)?.();
    _charts.delete(containerId);
}

// ── Time-range slider (d3-brush) ──────────────────────────────────────────────

/**
 * Renders a two-handle horizontal range slider spanning [minMonth, maxMonth].
 * Invokes dotNetRef.OnRangeChanged(startMonth, endMonth) when the player drags a handle.
 */
export function renderRangeSlider(containerId, minMonth, maxMonth, selectedStart, selectedEnd, dotNetRef) {
    const container = document.getElementById(containerId);
    if (!container) return;
    disposeRangeSlider(containerId);
    container.innerHTML = '';

    const W = container.clientWidth || 240;
    const H = container.clientHeight || 24;
    const margin = 6;
    const innerW = Math.max(10, W - margin * 2);

    const clampedMax = Math.max(minMonth, maxMonth);
    // A zero-width domain (e.g. session still at month 0) makes scaleLinear divide by zero
    // and return NaN, which breaks brush.move() and throws inside this function.
    const domainMax = clampedMax > minMonth ? clampedMax : minMonth + 1;
    const scale = d3.scaleLinear().domain([minMonth, domainMax]).range([0, innerW]).clamp(true);

    const svg = d3.create('svg').attr('width', W).attr('height', H);
    const g = svg.append('g').attr('transform', `translate(${margin},0)`);

    g.append('line')
        .attr('x1', 0).attr('x2', innerW).attr('y1', H / 2).attr('y2', H / 2)
        .attr('stroke', '#6c757d').attr('stroke-width', 2);

    const brush = d3.brushX()
        .extent([[0, H / 2 - 7], [innerW, H / 2 + 7]])
        .on('end', (event) => {
            // event.sourceEvent is only set for genuine user drags — ignore our own
            // programmatic brush.move() call below, which would otherwise re-trigger
            // 'end' and loop forever between JS and .NET.
            if (!event.selection || !event.sourceEvent) return;
            const [x0, x1] = event.selection.map(v => Math.round(scale.invert(v)));
            dotNetRef.invokeMethodAsync('OnRangeChanged', x0, x1);
        });

    const brushG = g.append('g').call(brush);
    const startMonth = selectedStart ?? minMonth;
    const endMonth   = selectedEnd   ?? clampedMax;
    brushG.call(brush.move, [scale(startMonth), scale(endMonth)]);

    container.appendChild(svg.node());
    _sliders.set(containerId, { cleanup: () => { container.innerHTML = ''; } });
}

export function disposeRangeSlider(containerId) {
    _sliders.get(containerId)?.cleanup?.();
    _sliders.delete(containerId);
}
