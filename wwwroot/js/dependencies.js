// Hierarchical Edge Bundling visualization using D3 v7
// Loaded as an ES module via Blazor JS interop.
import * as d3 from 'https://cdn.jsdelivr.net/npm/d3@7/+esm';

let _container = null;
let _cleanup   = null;

// â”€â”€ Public API â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

export function init(containerId, data) {
    _container = document.getElementById(containerId);
    if (!_container) return;
    _cleanup?.();
    _cleanup = null;
    _container.innerHTML = '';
    if (!data?.groups?.length) return;
    // Allow one animation frame so the container is fully laid out before measuring.
    requestAnimationFrame(() => render(data));
}

export function dispose() {
    _cleanup?.();
    _cleanup = null;
    if (_container) { _container.innerHTML = ''; _container = null; }
}

// â”€â”€ Rendering â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

function render(data) {
    if (!_container) return;

    const W = _container.clientWidth  || window.innerWidth;
    const H = _container.clientHeight || window.innerHeight;

    // Background color used for text halo (must match page background)
    const HALO = '#07141e';

    // Radii: arc band is wide and CONTAINS the item text labels.
    // Nodes sit at the inner edge; text extends outward through the band.
    // Category labels positioned via Cartesian coords (parallel text, not radial).
    const outerR  = Math.min(W, H) / 2 * 0.90; // outer edge of arc band
    const arcOutR = outerR;
    const arcInR  = outerR - 135;              // thinner band pushes items outward for more separation
    const nodeR   = arcInR - 8;                // endpoints positioned just outside the colored blocks

    // â”€â”€ D3 hierarchy â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    const root = d3.hierarchy({
        id: '__root__',
        children: data.groups.map(g => ({
            id:       '__g__' + g.name,
            name:     g.name,
            children: g.entries.map(e => ({ id: e.id, name: e.name, link: e.link || null }))
        }))
    });

    d3.cluster().size([2 * Math.PI, nodeR])(root);

    const byId = new Map(root.leaves().map(n => [n.data.id, n]));

    // â”€â”€ Links â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    const lineGen = d3.lineRadial()
        .curve(d3.curveBundle.beta(0.85))
        .radius(d => d.y)
        .angle(d => d.x);

    const allLinks = (data.links || [])
        .filter(l => byId.has(l.fromId) && byId.has(l.toId))
        .map(l => ({
            source:      byId.get(l.fromId),
            target:      byId.get(l.toId),
            severity:    l.severity,
            description: l.description,
            d:           lineGen(byId.get(l.fromId).path(byId.get(l.toId)))
        }));

    // â”€â”€ SVG â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    const svg = d3.create('svg')
        .attr('width',   W)
        .attr('height',  H)
        .attr('overflow','visible')   // let labels render beyond SVG bounds into container
        .style('display','block');

    // Arrow marker â€” fill inherits the path's stroke colour via SVG 2 context-stroke
    svg.append('defs').append('marker')
        .attr('id',          'arr')
        .attr('viewBox',     '0 0 8 6')
        .attr('refX',        '7')
        .attr('refY',        '3')
        .attr('markerWidth', '9')
        .attr('markerHeight','9')
        .attr('orient',      'auto')
        .attr('markerUnits', 'userSpaceOnUse')
        .append('path')
        .attr('d',      'M 0 0 L 8 3 L 0 6 Z')
        .attr('fill',   'context-stroke')
        .attr('stroke', 'none');

    const g = svg.append('g').attr('transform', `translate(${W / 2},${H / 2})`);

    // â”€â”€ Category arc bands & labels â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    const leafCount = root.leaves().length;
    const halfGap   = Math.PI / leafCount;
    const arcGen    = d3.arc().innerRadius(arcInR).outerRadius(arcOutR);

    const catFills = [
        'rgba(18,62,82,0.82)',  'rgba(20,72,75,0.82)',
        'rgba(15,55,85,0.82)',  'rgba(22,68,70,0.82)',
        'rgba(25,58,88,0.82)',  'rgba(17,75,78,0.82)',
        'rgba(12,65,80,0.82)',  'rgba(28,60,72,0.82)',
    ];

    (root.children || []).forEach((grpNode, gi) => {
        const leaves = grpNode.leaves();
        if (!leaves.length) return;

        const sa = leaves[0].x - halfGap;
        const ea = leaves[leaves.length - 1].x + halfGap;
        const ma = (sa + ea) / 2;

        g.append('path')
            .attr('d', arcGen({ startAngle: sa, endAngle: ea }))
            .attr('fill',         catFills[gi % catFills.length])
            .attr('stroke',       'rgba(255,255,255,0.06)')
            .attr('stroke-width', 0.5);

        // Category labels positioned parallel (horizontal) outside the arc band.
        // Convert polar angle to Cartesian position for horizontal text.
        const labelRadius = outerR + 32;
        const catX = labelRadius * Math.cos(ma - Math.PI / 2);
        const catY = labelRadius * Math.sin(ma - Math.PI / 2);
        const catLines = wrapLabel(grpNode.data.name);
        const catText  = g.append('text')
            .attr('x',                  catX)
            .attr('y',                  catY)
            .attr('text-anchor',        'middle')
            .attr('dominant-baseline',  'middle')
            .attr('fill',               '#daeaf5')
            .attr('font-size',          '14px')
            .attr('font-weight',        '700')
            .attr('letter-spacing',     '0.04em')
            .attr('paint-order',        'stroke')
            .attr('stroke',             HALO)
            .attr('stroke-width',       '4')
            .attr('stroke-linejoin',    'round');
        if (catLines.length === 1) {
            catText.text(catLines[0]);
        } else {
            catText.append('tspan').attr('x', catX).attr('dy', '-0.65em').text(catLines[0]);
            catText.append('tspan').attr('x', catX).attr('dy',  '1.30em').text(catLines[1]);
        }
    });

    // ── Edge paths — all visible at low opacity by default ─────────────────────
    const BASE_STROKE  = 'rgba(200,215,225,1)';
    const BASE_OPACITY = 0.18;
    const BASE_WIDTH   = 0.7;

    const edgeLayer = g.append('g');
    const edgeSel   = edgeLayer.selectAll('path')
        .data(allLinks)
        .join('path')
        .attr('fill',           'none')
        .attr('stroke',         BASE_STROKE)
        .attr('stroke-width',   BASE_WIDTH)
        .attr('stroke-opacity', BASE_OPACITY)
        .attr('stroke-linecap', 'round')
        .attr('marker-end',     'none')
        .attr('d',              d => d.d)
        .style('pointer-events', 'none');

    // â”€â”€ Hover tooltip â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    const tip = d3.select(_container).append('div')
        .style('position',       'absolute')
        .style('background',     'rgba(10,22,34,0.94)')
        .style('color',          '#cde6f5')
        .style('font-size',      '12px')
        .style('line-height',    '1.5')
        .style('padding',        '8px 12px')
        .style('border-radius',  '6px')
        .style('border',         '1px solid rgba(116,185,214,0.25)')
        .style('max-width',      '270px')
        .style('pointer-events', 'none')
        .style('opacity',        '0')
        .style('transition',     'opacity 0.12s')
        .style('z-index',        '200');

    // â”€â”€ Leaf nodes & labels â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    let activeId = null;

    const leafLayer = g.append('g');
    const leafGs    = leafLayer.selectAll('g')
        .data(root.leaves())
        .join('g')
        .attr('transform', d => `rotate(${d.x * 180 / Math.PI - 90}) translate(${d.y},0)`)
        .style('cursor', 'pointer')
        .on('click', (event, d) => { event.stopPropagation(); toggle(d.data.id); });

    // Invisible large hit zone
    leafGs.append('circle').attr('r', 8).attr('fill', 'transparent');

    // "i" wiki icons â€” placed inward from the node dot, between node and arc band.
    // Counter-rotated so the glyph always reads upright regardless of leaf angle.
    leafGs.each(function(d) {
        const resolvedLink = resolveWikiLink(data.wikiBaseUrl, d.data.link);
        if (!resolvedLink) return;
        const deg  = d.x * 180 / Math.PI - 90;
        const iconGap = arcOutR - nodeR - 20;  // keep icons inside the thicker band
        const iconG = d3.select(this).append('a')
            .attr('href',      resolvedLink)
            .attr('target',    '_blank')
            .attr('transform', `translate(${iconGap},0) rotate(${-deg})`)
            .on('click', e => e.stopPropagation())
            .style('cursor', 'pointer');

        iconG.append('circle')
            .attr('r',            5)
            .attr('fill',         'rgba(116,185,214,0.25)')
            .attr('stroke',       'rgba(116,185,214,0.6)')
            .attr('stroke-width', 0.7);

        iconG.append('text')
            .attr('text-anchor',       'middle')
            .attr('dominant-baseline', 'central')
            .attr('fill',              '#74b9d6')
            .attr('font-size',         '8px')
            .attr('font-weight',       '700')
            .attr('font-style',        'italic')
            .text('i');
    });

    // Item text labels — node dots removed; text alone identifies each node
    const nodeLblSel = leafGs.append('text')
        .attr('class', 'node-lbl')
        .attr('transform', d => {
            const isRight = d.x < Math.PI;
            const labelOffset = isRight ? 18 : 18;
            return `translate(${labelOffset},0)${!isRight ? ' rotate(180)' : ''}`;
        })
        .attr('text-anchor',       d => d.x < Math.PI ? 'start' : 'end')
        .attr('dominant-baseline', 'middle')
        .attr('fill',              '#c2dde8')
        .attr('font-size',         '11px')
        .attr('letter-spacing',    '0.02em')
        .attr('paint-order',       'stroke')
        .attr('stroke',            HALO)
        .attr('stroke-width',      '4')
        .attr('stroke-linejoin',   'round');

    // Allow item labels to wrap into 2 lines so wedges can be thinner while keeping font size.
    nodeLblSel.each(function(d) {
        const lines = wrapLabel(d.data.name, 14);
        const txt = d3.select(this);
        if (lines.length === 1) {
            txt.text(lines[0]);
            return;
        }
        txt.append('tspan').attr('x', 0).attr('dy', '-0.68em').text(lines[0]);
        txt.append('tspan').attr('x', 0).attr('dy',  '1.36em').text(lines[1]);
    });

    // â”€â”€ Toggle handler â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    function resetEdges() {
        edgeSel
            .attr('stroke',         BASE_STROKE)
            .attr('stroke-width',   BASE_WIDTH)
            .attr('stroke-opacity', BASE_OPACITY)
            .attr('marker-end',     'none')
            .style('pointer-events', 'none');
    }

    function toggle(id) {
        if (activeId === id) {
            activeId = null;
            resetEdges();
            leafGs.select('.node-lbl').attr('fill', '#c2dde8').attr('opacity', 1);
            tip.style('opacity', '0');
            return;
        }

        activeId = id;

        const connected = new Set([id]);
        allLinks.forEach(l => {
            if (l.source.data.id === id) connected.add(l.target.data.id);
            if (l.target.data.id === id) connected.add(l.source.data.id);
        });

        const isActive = d => d.source.data.id === id || d.target.data.id === id;
        edgeSel
            .attr('stroke',         d => isActive(d) ? sevColor(d.severity) : BASE_STROKE)
            .attr('stroke-width',   d => isActive(d) ? sevWidth(d.severity)  : BASE_WIDTH)
            .attr('stroke-opacity', d => isActive(d) ? 0.85 : 0.05)
            .attr('marker-end',     d => isActive(d) ? 'url(#arr)' : 'none')
            .style('pointer-events', d => isActive(d) ? 'all' : 'none');

        edgeSel
            .on('mousemove.tt', function(event, d) {
                if (d.source.data.id !== id && d.target.data.id !== id) return;
                const isFrom = d.source.data.id === id;
                const other  = isFrom ? d.target.data : d.source.data;
                const arrow  = isFrom ? `\u2192 ${other.name}` : `\u2190 ${other.name}`;
                const rect   = _container.getBoundingClientRect();
                tip.html(
                    `<div style="font-weight:600;margin-bottom:4px">${arrow} ` +
                    `<span style="color:${sevColor(d.severity)}">(${sevLabel(d.severity)})</span></div>` +
                    `<div style="color:#aed6ea">${d.description}</div>`)
                   .style('left', `${event.clientX - rect.left + 18}px`)
                   .style('top',  `${event.clientY - rect.top  - 48}px`)
                   .style('opacity', '1');
            })
            .on('mouseleave.tt', () => tip.style('opacity', '0'));

        leafGs.select('.node-lbl')
            .attr('fill', d =>
                d.data.id === id         ? '#FFD700' :
                connected.has(d.data.id) ? '#c5dde8'  : '#2e5a6e')
            .attr('opacity', d => connected.has(d.data.id) ? 1 : 0.25);
    }

    svg.on('click', () => { if (activeId) toggle(activeId); });

    _container.appendChild(svg.node());
    _cleanup = () => { if (_container) _container.innerHTML = ''; };
}

// â”€â”€ Severity helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

const SEV_COLORS = ['#c0392b', '#e74c3c', '#f0a099', '#8e9eab', '#82e0aa', '#2ecc71', '#1e8449'];
const SEV_LABELS = ['Strong negative', 'Negative', 'Weak negative', 'Neutral',
                    'Weak positive', 'Positive', 'Strong positive'];

function sevColor(s) { return SEV_COLORS[(s + 3)] ?? '#8e9eab'; }
function sevWidth(s) { return Math.abs(s) * 0.65 + 0.8; }
function sevLabel(s) { return SEV_LABELS[(s + 3)] ?? ''; }

// Builds icon href from dependency entry link using wiki_base_url from config.
// Supports absolute links, wiki://PageName, and relative page names.
function resolveWikiLink(baseUrl, link) {
    if (!link || typeof link !== 'string') return null;
    const trimmed = link.trim();
    if (!trimmed) return null;

    if (/^https?:\/\//i.test(trimmed)) return trimmed;

    if (!baseUrl || typeof baseUrl !== 'string' || !baseUrl.trim()) {
        return /^wiki:\/\//i.test(trimmed) ? null : trimmed;
    }

    const base = baseUrl.trim().replace(/\/$/, '');
    const page = trimmed.replace(/^wiki:\/\//i, '').replace(/^\/+/, '');
    return page ? `${base}/${page}` : null;
}

// ── Label wrapping helper ─────────────────────────────────────────────────────
// Splits a string roughly in half at a word boundary so long category names
// render on two lines instead of extending beyond the viewport edge.
function wrapLabel(str, maxLen = 13) {
    if (str.length <= maxLen) return [str];
    const mid = Math.floor(str.length / 2);
    // prefer break just before midpoint, then just after
    let i = str.lastIndexOf(' ', mid + 2);
    if (i <= 0) i = str.indexOf(' ', mid);
    if (i <= 0) return [str];
    return [str.slice(0, i), str.slice(i + 1)];
}

