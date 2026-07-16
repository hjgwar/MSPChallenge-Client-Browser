// MSP Challenge OpenLayers interop module
// Requires: ol (OpenLayers global) and proj4 (global) loaded before this module.

// Register EPSG:3035 (ETRS89 / LAEA Europe) with proj4 and tell OpenLayers about it.
proj4.defs('EPSG:3035',
    '+proj=laea +lat_0=52 +lon_0=10 +x_0=4321000 +y_0=3210000 ' +
    '+ellps=GRS80 +towgs84=0,0,0,0,0,0,0 +units=m +no_defs');
ol.proj.proj4.register(proj4);

let map = null;
const vectorLayers = {};  // layerId -> ol.layer.Vector
const rasterLayers = {};  // layerId -> ol.layer.Image
// Stores the original (pre-colormap) greyscale canvas per raster layer for click hit-testing.
const rasterRawCanvases = {};  // layerId -> { canvas: OffscreenCanvas, extent: [minX,minY,maxX,maxY] }

// Decode a data URL into an OffscreenCanvas (preserving the raw pixel data).
function decodeToOffscreenCanvas(dataUrl) {
    return new Promise((resolve, reject) => {
        const img = new Image();
        img.onload = () => {
            try {
                const oc = new OffscreenCanvas(img.width, img.height);
                oc.getContext('2d').drawImage(img, 0, 0);
                resolve(oc);
            } catch (e) { reject(e); }
        };
        img.onerror = reject;
        img.src = dataUrl;
    });
}

// ── Public exports ───────────────────────────────────────────────────────────

/**
 * Initialise the OpenLayers map with an EPSG:3035 view.
 * @param {string} elementId - id of the container div
 * @param {number} lat       - initial centre latitude (WGS84)
 * @param {number} lng       - initial centre longitude (WGS84)
 * @param {number} zoom      - initial zoom level
 */
export function initMap(elementId, lat, lng, zoom) {
    if (map) { map.setTarget(null); map = null; }

    const center = ol.proj.transform([lng, lat], 'EPSG:4326', 'EPSG:3035');

    map = new ol.Map({
        target: elementId,
        controls: [],
        layers: [],
        view: new ol.View({
            projection: 'EPSG:3035',
            center,
            zoom
        })
    });
}

/**
 * Add a vector layer from MSP geometry objects (api/Layer/Get payload).
 * Coordinates are in EPSG:3035 — fed directly to OpenLayers without reprojection.
 * Polygon holes are resolved via the subtractive id array.
 *
 * @param {string} layerId        - unique layer id
 * @param {string} geometriesJson - JSON string of the payload array
 * @param {string} geoType        - layer_geotype (polygon, line, point, …)
 * @param {string} color          - hex colour string
 */
export function addVectorLayer(layerId, geometriesJson, geoType, typeColors, visible = true, labelKey = null) {
    removeLayer(layerId);

    const geometries = JSON.parse(geometriesJson);
    const gt = (geoType || 'polygon').toLowerCase();
    // Normalise: single string → single-element array
    const colors = Array.isArray(typeColors) ? typeColors : [typeColors || '#3388ff'];

    // Build id → coordinates lookup so subtractive holes can be resolved
    const coordsById = {};
    for (const g of geometries) {
        if (Array.isArray(g.geometry)) coordsById[g.id] = g.geometry;
    }

    const features = [];
    for (const g of geometries) {
        if (!Array.isArray(g.geometry) || g.geometry.length === 0) continue;

        let olGeom;
        if (gt === 'polygon' || gt === 'polygons') {
            // Exterior ring + subtractive interior rings (holes)
            const rings = [g.geometry];
            if (Array.isArray(g.subtractive)) {
                for (const holeId of g.subtractive) {
                    if (coordsById[holeId]) rings.push(coordsById[holeId]);
                }
            }
            olGeom = new ol.geom.Polygon(rings);
        } else if (gt === 'line' || gt === 'lines') {
            olGeom = new ol.geom.LineString(g.geometry);
        } else {
            olGeom = new ol.geom.Point(g.geometry[0] ?? [0, 0]);
        }

        const labelVal = (labelKey && g.data && typeof g.data === 'object')
            ? String(g.data[labelKey] ?? '')
            : '';

        features.push(new ol.Feature({ geometry: olGeom, mspId: g.id, mspType: (parseInt(g.type, 10) || 0), mspLabel: labelVal, mspData: g.data ?? {} }));
    }

    if (features.length === 0) return;

    const styleFunction = (feature, resolution) => {
        if (feature.get('_projHidden')) return null;
        const idx = feature.get('mspType') ?? 0;
        const hex = colors[idx] ?? colors[0] ?? '#3388ff';
        const label = (labelKey && resolution < 5000) ? (feature.get('mspLabel') || '') : '';
        return new ol.style.Style({
            fill:   new ol.style.Fill({ color: hexToRgba(hex, 0.45) }),
            stroke: new ol.style.Stroke({ color: hexToRgba(hex, 0.9), width: 1.5 }),
            image:  new ol.style.Circle({
                radius: 5,
                fill:   new ol.style.Fill({ color: hexToRgba(hex, 0.45) }),
                stroke: new ol.style.Stroke({ color: hexToRgba(hex, 0.9), width: 1 })
            }),
            text: label ? new ol.style.Text({
                text: label,
                font: '11px/1 sans-serif',
                fill:   new ol.style.Fill({ color: '#ffffff' }),
                stroke: new ol.style.Stroke({ color: '#333333', width: 2.5 }),
                offsetY: -14,
                overflow: true
            }) : null
        });
    };

    const layer = new ol.layer.Vector({
        source: new ol.source.Vector({ features }),
        style: styleFunction
    });

    layer.setVisible(visible);
    map.addLayer(layer);
    vectorLayers[layerId] = layer;
}

/**
 * Linearly interpolate between two RGBA arrays.
 */
function lerpColor(c1, c2, t) {
    return [
        Math.round(c1[0] + (c2[0] - c1[0]) * t),
        Math.round(c1[1] + (c2[1] - c1[1]) * t),
        Math.round(c1[2] + (c2[2] - c1[2]) * t),
        Math.round(c1[3] + (c2[3] - c1[3]) * t)
    ];
}

/**
 * Remap greyscale pixels using a colour map, with optional smooth interpolation.
 * @param {string}      dataUrl     - data: URL of the source PNG
 * @param {Array|null}  colorMap    - [{value: number, rgba: [r,g,b,a]}, ...] sorted ascending by value (0-255 scale)
 * @param {number|null} minCutoff   - grey value (0-255); pixels <= this become fully transparent
 * @param {boolean}     interpolate - true = smooth lerp between stops; false = hard step at each stop
 * @returns {Promise<string>} - data: URL of the recoloured PNG
 */
function applyRasterColorMap(dataUrl, colorMap, minCutoff, interpolate) {
    return new Promise((resolve) => {
        const img = new Image();
        img.onload = () => {
            const canvas = document.createElement('canvas');
            canvas.width  = img.width;
            canvas.height = img.height;
            const ctx  = canvas.getContext('2d');
            ctx.drawImage(img, 0, 0);
            const imgd = ctx.getImageData(0, 0, canvas.width, canvas.height);
            const d    = imgd.data;

            for (let i = 0; i < d.length; i += 4) {
                if (d[i + 3] === 0) continue; // preserve fully transparent pixels
                const grey = d[i]; // R channel (greyscale source: R = G = B)

                // Minimum value cutoff: make low-value pixels fully transparent
                if (minCutoff !== null && minCutoff !== undefined && grey < minCutoff) {
                    d[i + 3] = 0;
                    continue;
                }

                if (!colorMap || colorMap.length === 0) continue;

                let mapped;
                if (interpolate && colorMap.length > 1) {
                    // Smooth lerp: find the two bracketing stops and blend
                    if (grey <= colorMap[0].value) {
                        mapped = colorMap[0].rgba;
                    } else if (grey >= colorMap[colorMap.length - 1].value) {
                        mapped = colorMap[colorMap.length - 1].rgba;
                    } else {
                        for (let k = 0; k < colorMap.length - 1; k++) {
                            if (grey >= colorMap[k].value && grey <= colorMap[k + 1].value) {
                                const span = colorMap[k + 1].value - colorMap[k].value;
                                const t    = span > 0 ? (grey - colorMap[k].value) / span : 0;
                                mapped = lerpColor(colorMap[k].rgba, colorMap[k + 1].rgba, t);
                                break;
                            }
                        }
                    }
                } else {
                    // Hard step: first stop whose value >= grey
                    mapped = colorMap[colorMap.length - 1].rgba;
                    for (const entry of colorMap) {
                        if (grey <= entry.value) { mapped = entry.rgba; break; }
                    }
                }

                d[i]     = mapped[0];
                d[i + 1] = mapped[1];
                d[i + 2] = mapped[2];
                d[i + 3] = mapped[3];
            }

            ctx.putImageData(imgd, 0, 0);
            resolve(canvas.toDataURL('image/png'));
        };
        img.src = dataUrl;
    });
}

/**
 * Add a raster image layer (api/Layer/GetRaster payload).
 * @param {string}      layerId     - unique layer id
 * @param {string}      imageData   - base64-encoded PNG (with or without data-URI prefix)
 * @param {number[][]}  projBounds  - [[x1,y1],[x2,y2]] in EPSG:3035
 * @param {number}      opacity     - 0–1
 * @param {boolean}     visible
 * @param {Array|null}  colorMap    - [{value, rgba:[r,g,b,a]}, ...] sorted ascending; null = no remapping
 * @param {number|null} minCutoff   - grey value (0-255); pixels <= this become transparent
 * @param {boolean}     interpolate - smooth lerp between colour stops (default true)
 */
export async function addRasterLayer(layerId, imageData, projBounds, opacity, visible = true, colorMap = null, minCutoff = null, interpolate = true) {
    removeLayer(layerId);

    // OL ImageStatic extent: [minX, minY, maxX, maxY]
    const extent = [projBounds[0][0], projBounds[0][1], projBounds[1][0], projBounds[1][1]];
    let dataUrl = imageData.startsWith('data:')
        ? imageData
        : `data:image/png;base64,${imageData}`;

    // Decode the original greyscale data before any colour-mapping so we can
    // hit-test clicks against the raw grey value (not the colourised output).
    try {
        const rawCanvas = await decodeToOffscreenCanvas(dataUrl);
        rasterRawCanvases[layerId] = { canvas: rawCanvas, extent };
    } catch (_) { /* OffscreenCanvas not available or image failed – hit-test will be skipped */ }

    if ((colorMap && colorMap.length > 0) || minCutoff !== null) {
        dataUrl = await applyRasterColorMap(dataUrl, colorMap, minCutoff, interpolate);
    }

    const layer = new ol.layer.Image({
        source: new ol.source.ImageStatic({
            url: dataUrl,
            projection: 'EPSG:3035',
            imageExtent: extent
        }),
        opacity: opacity ?? 0.9
    });

    layer.setVisible(visible);
    map.addLayer(layer);
    rasterLayers[layerId] = layer;
}

export function setLayerZIndex(layerId, zIndex) {
    const layer = vectorLayers[layerId] ?? rasterLayers[layerId];
    if (layer) layer.setZIndex(zIndex);
}

export function setLayerVisible(layerId, visible) {
    const layer = vectorLayers[layerId] ?? rasterLayers[layerId];
    if (layer) layer.setVisible(visible);
}

export function removeLayer(layerId) {
    if (vectorLayers[layerId]) {
        map.removeLayer(vectorLayers[layerId]);
        delete vectorLayers[layerId];
    }
    if (rasterLayers[layerId]) {
        map.removeLayer(rasterLayers[layerId]);
        delete rasterLayers[layerId];
    }
    if (rasterRawCanvases[layerId]) {
        delete rasterRawCanvases[layerId];
    }
}

export function fitToLayer(layerId) {
    if (vectorLayers[layerId]) {
        const extent = vectorLayers[layerId].getSource().getExtent();
        if (!ol.extent.isEmpty(extent)) {
            map.getView().fit(extent, { padding: [20, 20, 20, 20], duration: 500 });
        }
    } else if (rasterLayers[layerId]) {
        const extent = rasterLayers[layerId].getSource().getImageExtent();
        if (extent) {
            map.getView().fit(extent, { padding: [20, 20, 20, 20], duration: 500 });
        }
    }
}

/**
 * Fit the view to the given layer with no padding so that either the horizontal
 * or vertical edges of the layer extent align exactly with the viewport edges.
 * Used for the _PLAYAREA layer to set the initial view.
 */
function fitExtentCover(extent) {
    const view     = map.getView();
    const mapSize  = map.getSize();   // [width, height] in CSS px
    const extW     = ol.extent.getWidth(extent);
    const extH     = ol.extent.getHeight(extent);
    // "cover": use the smaller resolution (higher zoom) so no empty space remains
    const res      = Math.min(extW / mapSize[0], extH / mapSize[1]);
    view.setCenter(ol.extent.getCenter(extent));
    view.setResolution(res);
}

export function fitToPlayArea(layerId) {
    if (vectorLayers[layerId]) {
        const extent = vectorLayers[layerId].getSource().getExtent();
        if (!ol.extent.isEmpty(extent)) {
            fitExtentCover(extent);
        }
    } else if (rasterLayers[layerId]) {
        const extent = rasterLayers[layerId].getSource().getImageExtent();
        if (extent) {
            fitExtentCover(extent);
        }
    }
}

export function setView(lat, lng, zoom, animate = true) {
    if (!map) return;
    const center = ol.proj.transform([lng, lat], 'EPSG:4326', 'EPSG:3035');
    const view = map.getView();
    if (animate) {
        view.animate({ center, zoom, duration: 300 });
    } else {
        view.setCenter(center);
        view.setZoom(zoom);
    }
}

export function getViewState() {
    if (!map) return null;
    const view = map.getView();
    if (!view) return null;
    const center3035 = view.getCenter();
    if (!center3035) return null;
    const [lng, lat] = ol.proj.transform(center3035, 'EPSG:3035', 'EPSG:4326');
    return { lat, lng, zoom: view.getZoom() };
}

export function invalidateSize() {
    if (map) map.updateSize();
}

export function scrollElementToBottom(selector) {
    if (!selector) return;
    const el = document.querySelector(selector);
    if (!el) return;
    el.scrollTop = el.scrollHeight;
}

let _clickHandler = null;
let _dotNetRef = null;

/**
 * Register a .NET callback invoked with click info from the topmost visible layer.
 * @param {object} dotNetRef - DotNetObjectReference with an OnMapClick(string) method
 */
export function registerClickHandler(dotNetRef) {
    if (!map) return;
    _dotNetRef = dotNetRef;
    if (_clickHandler) map.un('singleclick', _clickHandler);

    _clickHandler = (evt) => {
        let topLayerId = null;
        let topProps   = null;
        let topZIndex  = -Infinity;

        // Iterate all vector features at this pixel; track the one on the topmost layer
        map.forEachFeatureAtPixel(evt.pixel, (feature, layer) => {
            if (feature.get('_isBadge') && !feature.get('_isRestrictionBadge')) return; // decorative icons — not clickable
            const z = layer.getZIndex() ?? 0;
            if (z > topZIndex) {
                topZIndex = z;
                // Merge mspData with internal _msp* fields so C# can access type/layer info
                const data = Object.assign({}, feature.get('mspData') ?? {});
                const mspType = feature.get('mspType');
                if (mspType !== undefined && mspType !== null) data._mspType = mspType;
                const origLayerId = feature.get('mspOriginalLayerId');
                if (origLayerId) data._mspOriginalLayerId = origLayerId;
                topProps = data;
                for (const [id, l] of Object.entries(vectorLayers)) {
                    if (l === layer) { topLayerId = id; break; }
                }
            }
        }, { hitTolerance: 4 });

        // Check raster layers — wins only if its z-index is higher than any vector hit
        const coord = evt.coordinate;
        for (const [id, layer] of Object.entries(rasterLayers)) {
            if (!layer.getVisible()) continue;
            const extent = layer.getSource().getImageExtent();
            if (ol.extent.containsCoordinate(extent, coord)) {
                const z = layer.getZIndex() ?? 0;
                if (z > topZIndex) {
                    topZIndex  = z;
                    topLayerId = id;

                    // Sample the raw grey value from the original (pre-colormap) offscreen canvas
                    // using coordinate → fractional pixel math. This avoids reading the
                    // colourised composite canvas, which would give the wrong value.
                    let greyValue = null;
                    const rawData = rasterRawCanvases[id];
                    if (rawData) {
                        const { canvas: rc, extent: re } = rawData;
                        const [minX, minY, maxX, maxY] = re;
                        const fracX = (coord[0] - minX) / (maxX - minX);
                        const fracY = (coord[1] - minY) / (maxY - minY);
                        if (fracX >= 0 && fracX <= 1 && fracY >= 0 && fracY <= 1) {
                            // Image Y is flipped relative to map Y (image origin = top-left).
                            const px = Math.min(Math.floor(fracX * rc.width),  rc.width  - 1);
                            const py = Math.min(Math.floor((1 - fracY) * rc.height), rc.height - 1);
                            const imgd = rc.getContext('2d').getImageData(px, py, 1, 1).data;
                            if (imgd[3] > 0) greyValue = imgd[0]; // R == grey in greyscale PNG
                        }
                    }

                    topProps = greyValue !== null ? { _rasterGrey: greyValue } : {};
                }
            }
        }

        if (topLayerId !== null) {
            _dotNetRef.invokeMethodAsync('OnMapClick', JSON.stringify({
                layerId: topLayerId,
                props:   topProps,
                clientX: evt.originalEvent.clientX,
                clientY: evt.originalEvent.clientY
            }));
        }
    };

    map.on('singleclick', _clickHandler);
}

export function focusOnCoordinate(x, y) {
    if (!map) return;
    const view = map.getView();
    const currentZoom = view.getZoom() ?? 6;
    const targetZoom = Math.max(currentZoom, 9);
    view.animate({ center: [x, y], zoom: targetZoom, duration: 350 });
}

// ── Issue markers ─────────────────────────────────────────────────────────────

let _issueMarkersLayer = null;

/**
 * Renders colour-coded circle markers on the map at each restriction-issue location.
 * @param {string} markersJson  JSON array of { x, y, severity } objects (EPSG:3035 coords).
 */
export function showIssueMarkers(markersJson) {
    if (!map) return;
    clearIssueMarkers();

    const markers = JSON.parse(markersJson);
    const features = markers.map(m => {
        const feature = new ol.Feature({
            geometry: new ol.geom.Point([m.x, m.y])
        });
        const isError = (m.severity || '').toUpperCase() === 'ERROR';
        feature.setStyle(new ol.style.Style({
            image: new ol.style.Circle({
                radius: 8,
                fill: new ol.style.Fill({ color: isError ? '#e74c3c' : '#f39c12' }),
                stroke: new ol.style.Stroke({ color: '#ffffff', width: 2.5 })
            })
        }));
        return feature;
    });

    const source = new ol.source.Vector({ features });
    _issueMarkersLayer = new ol.layer.Vector({ source, zIndex: 9999 });
    map.addLayer(_issueMarkersLayer);
}

/**
 * Removes all restriction-issue markers added by showIssueMarkers().
 */
export function clearIssueMarkers() {
    if (_issueMarkersLayer && map) {
        map.removeLayer(_issueMarkersLayer);
        _issueMarkersLayer = null;
    }
}

export function unregisterClickHandler() {
    if (map && _clickHandler) {
        map.un('singleclick', _clickHandler);
        _clickHandler = null;
    }
    _dotNetRef = null;
}

// ── Legend drag-to-reorder ───────────────────────────────────────────────────
// All drag logic runs client-side; only the final from→to indices are sent
// to Blazor via invokeMethodAsync, avoiding SignalR flooding from dragover.

let _legendDotNetRef = null;
let _legendDragFromIdx = null;
let _legendListenersAttached = false;

export function initLegendDrag(dotNetRef) {
    _legendDotNetRef = dotNetRef; // always update — stale ref after hot reload would silently fail
    if (_legendListenersAttached) return;
    _legendListenersAttached = true;

    document.addEventListener('dragstart', e => {
        const row = e.target.closest('.legend-layer[data-legend-idx]');
        if (!row) return;
        _legendDragFromIdx = parseInt(row.dataset.legendIdx, 10);
        // Firefox requires setData to be called or drag won't fire drop
        e.dataTransfer.setData('text/plain', String(_legendDragFromIdx));
        e.dataTransfer.effectAllowed = 'move';
    });

    document.addEventListener('dragover', e => {
        const row = e.target.closest('.legend-layer[data-legend-idx]');
        if (row) {
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
        }
    });

    document.addEventListener('drop', e => {
        const row = e.target.closest('.legend-layer[data-legend-idx]');
        if (!row || _legendDragFromIdx === null) { _legendDragFromIdx = null; return; }
        e.preventDefault();
        const toIdx = parseInt(row.dataset.legendIdx, 10);
        const fromIdx = _legendDragFromIdx;
        _legendDragFromIdx = null;
        if (fromIdx === toIdx) return;
        _legendDotNetRef.invokeMethodAsync('ReorderLegend', fromIdx, toIdx);
    });

    document.addEventListener('dragend', () => { _legendDragFromIdx = null; });
}

// ── Helpers ──────────────────────────────────────────────────────────────────

function hexToRgba(hex, alpha) {
    if (!hex || !hex.startsWith('#')) return `rgba(51,136,255,${alpha ?? 1})`;
    // Normalise short form #RGB → #RRGGBB
    const h = hex.length === 4
        ? '#' + hex[1] + hex[1] + hex[2] + hex[2] + hex[3] + hex[3]
        : hex;
    const r = parseInt(h.slice(1, 3), 16);
    const g = parseInt(h.slice(3, 5), 16);
    const b = parseInt(h.slice(5, 7), 16);
    // 8-char #RRGGBBAA: use embedded alpha unless caller overrides
    const a = alpha ?? (h.length === 9 ? parseInt(h.slice(7, 9), 16) / 255 : 1);
    return `rgba(${r},${g},${b},${a})`;
}

// ── Plan geometry overlay ─────────────────────────────────────────────────────

const PLAN_OVERLAY_ID = '__plan_overlay__';

const planOverlayStyle = new ol.style.Style({
    fill:   new ol.style.Fill({ color: 'rgba(255, 215, 0, 0.20)' }),
    stroke: new ol.style.Stroke({ color: '#FFD700', width: 2.5 }),
    image:  new ol.style.Circle({
        radius: 7,
        fill:   new ol.style.Fill({ color: 'rgba(255, 215, 0, 0.50)' }),
        stroke: new ol.style.Stroke({ color: '#FFD700', width: 2 })
    })
});

const worldStateUnmodifiedStyle = new ol.style.Style({
    fill:   new ol.style.Fill({ color: 'rgba(0, 0, 0, 0)' }),
    stroke: new ol.style.Stroke({ color: 'rgba(0, 0, 0, 0)', width: 0 }),
    image:  new ol.style.Circle({
        radius: 0,
        fill:   new ol.style.Fill({ color: 'rgba(0, 0, 0, 0)' }),
        stroke: new ol.style.Stroke({ color: 'rgba(0, 0, 0, 0)', width: 0 })
    })
});

const deletionHighlightStyle = new ol.style.Style({
    fill:   new ol.style.Fill({ color: 'rgba(220, 53, 69, 0.25)' }),
    stroke: new ol.style.Stroke({ color: '#dc3545', width: 2.5, lineDash: [6, 4] }),
    image:  new ol.style.Circle({
        radius: 7,
        fill:   new ol.style.Fill({ color: 'rgba(220, 53, 69, 0.50)' }),
        stroke: new ol.style.Stroke({ color: '#dc3545', width: 2, lineDash: [4, 3] })
    })
});

/** Creates an ol.style.Icon of a green circle with a white plus sign. */
function createPlusBadgeIcon() {
    const size = 14;
    const canvas = document.createElement('canvas');
    canvas.width = size; canvas.height = size;
    const ctx = canvas.getContext('2d');
    ctx.fillStyle = '#28a745';
    ctx.beginPath();
    ctx.arc(size / 2, size / 2, size / 2, 0, 2 * Math.PI);
    ctx.fill();
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(3, size / 2 - 1.5, size - 6, 3);   // horizontal bar
    ctx.fillRect(size / 2 - 1.5, 3, 3, size - 6);   // vertical bar
    return new ol.style.Icon({
        img:          canvas,
        size:         [size, size],
        anchor:       [0, 1],
        anchorXUnits: 'fraction',
        anchorYUnits: 'fraction'
    });
}

/** Creates an ol.style.Icon of an orange circle with a white pencil. */
function createEditBadgeIcon() {
    const size = 14;
    const canvas = document.createElement('canvas');
    canvas.width = size; canvas.height = size;
    const ctx = canvas.getContext('2d');
    ctx.fillStyle = '#fd7e14';
    ctx.beginPath();
    ctx.arc(size / 2, size / 2, size / 2, 0, 2 * Math.PI);
    ctx.fill();
    ctx.strokeStyle = '#ffffff';
    ctx.lineWidth   = 1.5;
    ctx.lineCap     = 'round';
    ctx.lineJoin    = 'round';
    // Pencil shaft
    ctx.beginPath();
    ctx.moveTo(10, 3.5);
    ctx.lineTo(4.5, 9);
    ctx.stroke();
    // Pencil tip (small triangle)
    ctx.beginPath();
    ctx.moveTo(4.5, 9);
    ctx.lineTo(3.5, 11);
    ctx.lineTo(5.5, 10.5);
    ctx.closePath();
    ctx.stroke();
    return new ol.style.Icon({
        img:          canvas,
        size:         [size, size],
        anchor:       [0, 1],
        anchorXUnits: 'fraction',
        anchorYUnits: 'fraction'
    });
}

/** Creates an ol.style.Icon of a red circle with a white minus bar. */
function createMinusBadgeIcon() {
    const size = 14;
    const canvas = document.createElement('canvas');
    canvas.width = size; canvas.height = size;
    const ctx = canvas.getContext('2d');
    ctx.fillStyle = '#dc3545';
    ctx.beginPath();
    ctx.arc(size / 2, size / 2, size / 2, 0, 2 * Math.PI);
    ctx.fill();
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(3, size / 2 - 1.5, size - 6, 3);
    return new ol.style.Icon({
        img:            canvas,
        size:           [size, size],
        anchor:         [0, 1],        // bottom-left of icon at the point → icon appears above-right
        anchorXUnits:   'fraction',
        anchorYUnits:   'fraction'
    });
}

function restrictionRank(severity) {
    switch ((severity || '').toUpperCase()) {
        case 'ERROR': return 3;
        case 'WARNING': return 2;
        case 'INFO': return 1;
        default: return 0;
    }
}

function mostSevereRestriction(restrictions) {
    if (!Array.isArray(restrictions) || restrictions.length === 0) return null;
    let best = null;
    let bestRank = 0;
    for (const restriction of restrictions) {
        const rank = restrictionRank(restriction);
        if (rank > bestRank) {
            bestRank = rank;
            best = restriction;
        }
    }
    return best;
}

function createRestrictionBadgeIcon(severity) {
    const key = (severity || '').toUpperCase();
    const size = 14;
    const canvas = document.createElement('canvas');
    canvas.width = size;
    canvas.height = size;
    const ctx = canvas.getContext('2d');

    let fill = '#0d6efd';
    let glyph = 'i';
    if (key === 'WARNING') {
        fill = '#ffc107';
        glyph = '!';
    } else if (key === 'ERROR') {
        fill = '#dc3545';
        glyph = 'x';
    }

    ctx.fillStyle = fill;
    ctx.beginPath();
    ctx.arc(size / 2, size / 2, size / 2, 0, 2 * Math.PI);
    ctx.fill();

    ctx.fillStyle = '#ffffff';
    ctx.font = 'bold 10px sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(glyph, size / 2, size / 2 + 0.5);

    return new ol.style.Icon({
        img: canvas,
        size: [size, size],
        anchor: [0, 1],
        anchorXUnits: 'fraction',
        anchorYUnits: 'fraction',
        displacement: [11, -9]
    });
}

export function showPlanGeometry(layersJson) {
    // Remove any existing plan overlay first
    removeLayer(PLAN_OVERLAY_ID);

    const layers = JSON.parse(layersJson);
    const features = [];
    const minusBadge = createMinusBadgeIcon();
    const plusBadge  = createPlusBadgeIcon();
    const editBadge  = createEditBadgeIcon();

    for (const layer of layers) {
        const gt = (layer.geoType || 'point').toLowerCase();

        // New/altered plan geometry — gold style, with plus or edit badge
        for (const geo of (layer.geometries ?? [])) {
            const coordSet = geo.coords;
            if (!coordSet || coordSet.length === 0) continue;
            let olGeom;
            if (gt === 'polygon' || gt === 'polygons') {
                olGeom = new ol.geom.Polygon([coordSet]);
            } else if (gt === 'line' || gt === 'lines') {
                olGeom = new ol.geom.LineString(coordSet);
            } else {
                olGeom = new ol.geom.Point(coordSet[0]);
            }
            const planFeatId = geo.id || ('plan_' + features.length + '_' + Math.random().toString(36).slice(2));
            const planFeat = new ol.Feature({ geometry: olGeom, mspOriginalLayerId: layer.originalLayerId, mspType: (geo.mspType ?? 0), mspId: planFeatId });
            planFeat.setId(planFeatId);
            features.push(planFeat);

            // Badge placed at the bounding-box centre of the geometry
            const badge  = geo.isNew ? plusBadge : editBadge;
            const center = ol.extent.getCenter(olGeom.getExtent());
            const badgeFeature = new ol.Feature({ geometry: new ol.geom.Point(center), _isBadge: true, _badgeForFeature: String(planFeatId) });
            badgeFeature.setStyle(new ol.style.Style({ image: badge }));
            features.push(badgeFeature);

            const restrictionSeverity = mostSevereRestriction(geo.restrictions);
            const restrictionMarkers = Array.isArray(geo.restrictionMarkers) ? geo.restrictionMarkers : [];
            if (restrictionMarkers.length > 0) {
                for (const marker of restrictionMarkers) {
                    const markerCoord = Array.isArray(marker.coord) && marker.coord.length >= 2
                        ? marker.coord
                        : center;
                    const restrictionBadgeFeature = new ol.Feature({
                        geometry: new ol.geom.Point(markerCoord),
                        _isBadge: true,
                        _isRestrictionBadge: true,
                        mspData: {
                            _restrictionSeverity: marker.severity ?? '',
                            _restrictionMessage: marker.message ?? '',
                            _restrictionSourceLayer: marker.sourceLayer ?? '',
                            _restrictionTargetLayer: marker.targetLayer ?? '',
                            _restrictionChangeKind: marker.changeKind ?? ''
                        }
                    });
                    restrictionBadgeFeature.setStyle(new ol.style.Style({ image: createRestrictionBadgeIcon(marker.severity) }));
                    features.push(restrictionBadgeFeature);
                }
            } else if (restrictionSeverity) {
                const restrictionBadgeFeature = new ol.Feature({
                    geometry: new ol.geom.Point(center),
                    _isBadge: true,
                    _isRestrictionBadge: true,
                    mspData: {
                        _restrictionSeverity: restrictionSeverity,
                        _restrictionMessage: 'Restriction overlap detected'
                    }
                });
                restrictionBadgeFeature.setStyle(new ol.style.Style({ image: createRestrictionBadgeIcon(restrictionSeverity) }));
                features.push(restrictionBadgeFeature);
            }
        }

        // Deleted geometry — look up in the base layer, highlight red + minus badge
        if (Array.isArray(layer.deletedIds) && layer.deletedIds.length > 0) {
            const baseLayer = vectorLayers[layer.originalLayerId];
            if (baseLayer) {
                const source = baseLayer.getSource();
                for (const persistentId of layer.deletedIds) {
                    const baseFeature = source.getFeatures()
                        .find(f => String(f.get('mspId')) === String(persistentId));
                    if (!baseFeature) continue;

                    const geom = baseFeature.getGeometry();
                    if (!geom) continue;

                    // Red dashed highlight over the geometry
                    const deletedGeomFeature = new ol.Feature({ geometry: geom.clone() });
                    deletedGeomFeature.setId(persistentId);
                    deletedGeomFeature.set('mspId', persistentId);
                    deletedGeomFeature.set('mspOriginalLayerId', layer.originalLayerId);
                    const baseType = baseFeature.get('mspType') ?? 0;
                    deletedGeomFeature.set('mspType', baseType);
                    deletedGeomFeature.set('_worldStateId', String(persistentId));
                    deletedGeomFeature.set('_isMarkedForDeletion', true);
                    // Set original state fields so downstream logic recognizes this as an unmodified world-state feature
                    const origCoords = geometryToCoords(geom).map(c => [...c]);
                    deletedGeomFeature.set('_worldStateOrigCoords', origCoords);
                    deletedGeomFeature.set('_worldStateOrigType', baseType);
                    deletedGeomFeature.setStyle(deletionHighlightStyle);
                    features.push(deletedGeomFeature);

                    // Minus badge placed at the geometry's bounding-box centre
                    const center = ol.extent.getCenter(geom.getExtent());
                    const badgeFeature = new ol.Feature({ geometry: new ol.geom.Point(center), _isBadge: true, _badgeForFeature: String(persistentId) });
                    badgeFeature.setStyle(new ol.style.Style({ image: minusBadge }));
                    features.push(badgeFeature);
                }
            }
        }
    }

    const overlayLayer = new ol.layer.Vector({
        source: new ol.source.Vector({ features }),
        style:  feature => feature.getStyle() ?? planOverlayStyle,
        zIndex: 99999
    });

    map.addLayer(overlayLayer);
    vectorLayers[PLAN_OVERLAY_ID] = overlayLayer;
}

export function clearPlanOverlay() {
    removeLayer(PLAN_OVERLAY_ID);
}

// ── Geometry Drawing Tool ─────────────────────────────────────────────────────

let _drawDotNetRef    = null;
let _drawTargetLayerId = null;
let _drawModify       = null;
let _drawDraw         = null;
let _drawSelect       = null;
let _modifyStartCoords = {};

function getPlanOverlaySource() {
    const layer = vectorLayers[PLAN_OVERLAY_ID];
    return layer ? layer.getSource() : null;
}

// Ensures the plan overlay layer exists (creates an empty one if needed).
// Call this before any geometry drawing/editing operation.
function ensurePlanOverlay() {
    if (!vectorLayers[PLAN_OVERLAY_ID]) {
        const overlayLayer = new ol.layer.Vector({
            source: new ol.source.Vector({ features: [] }),
            style:  feature => feature.getStyle() ?? planOverlayStyle,
            zIndex: 99999
        });
        map.addLayer(overlayLayer);
        vectorLayers[PLAN_OVERLAY_ID] = overlayLayer;
    }
    return vectorLayers[PLAN_OVERLAY_ID].getSource();
}

function getPlanOverlayFeatureById(featureId) {
    const source = getPlanOverlaySource();
    if (!source) return null;
    const byId = source.getFeatureById(featureId);
    if (byId) return byId;
    return source.getFeatures().find(f => f.get('mspId') === featureId) ?? null;
}

function geometryToCoords(geom) {
    if (geom instanceof ol.geom.Polygon)    return geom.getCoordinates()[0];
    if (geom instanceof ol.geom.LineString) return geom.getCoordinates();
    if (geom instanceof ol.geom.Point)      return [geom.getCoordinates()];
    return [];
}

function setGeometryFromCoords(geom, coords) {
    if      (geom instanceof ol.geom.Polygon)    geom.setCoordinates([coords]);
    else if (geom instanceof ol.geom.LineString) geom.setCoordinates(coords);
    else if (geom instanceof ol.geom.Point)      geom.setCoordinates(coords[0] ?? [0, 0]);
}

function olDrawType(geoType) {
    const gt = (geoType || '').toLowerCase();
    if (gt === 'polygon' || gt === 'polygons') return 'Polygon';
    if (gt === 'line'    || gt === 'lines')    return 'LineString';
    return 'Point';
}

export function stopGeometryEditing() {
    if (_drawModify) { map.removeInteraction(_drawModify); _drawModify = null; }
    if (_drawDraw)   { map.removeInteraction(_drawDraw);   _drawDraw   = null; }
    if (_drawSelect) { map.removeInteraction(_drawSelect); _drawSelect = null; }
    _modifyStartCoords = {};
    _drawDotNetRef     = null;
    _drawTargetLayerId = null;
}

export function startGeometryEdit(targetLayerId, dotNetRef) {
    stopGeometryEditing();
    _drawDotNetRef     = dotNetRef;
    _drawTargetLayerId = targetLayerId;
    const source = ensurePlanOverlay();
    if (!map) return;

    // Restrict editing to features belonging to this layer only.
    _drawModify = new ol.interaction.Modify({
        source:          source,
        filter:          f => !f.get('_isBadge') && f.get('mspOriginalLayerId') === targetLayerId,
        deleteCondition: () => false,
    });

    _drawModify.on('modifystart', evt => {
        for (const f of evt.features.getArray()) {
            const id = f.get('mspId');
            if (id) _modifyStartCoords[id] = geometryToCoords(f.getGeometry()).map(c => [...c]);
        }
    });

    _drawModify.on('modifyend', evt => {
        for (const f of evt.features.getArray()) {
            const id = f.get('mspId');
            if (!id) continue;
            const oldCoords = _modifyStartCoords[id];
            delete _modifyStartCoords[id];
            if (!oldCoords) continue;
            const newCoords = geometryToCoords(f.getGeometry()).map(c => [...c]);
            
            // If this is a world-state feature, check if it's been modified
            const worldStateId = f.get('_worldStateId');
            if (worldStateId && !f.get('_worldStateModified')) {
                const origCoords = f.get('_worldStateOrigCoords');
                if (origCoords && !_coordsEqual(origCoords, newCoords)) {
                    // Feature was modified - switch to gold style
                    f.set('_worldStateModified', true);
                    f.setStyle(null); // Use default planOverlayStyle (gold)
                }
            }
            
            _drawDotNetRef?.invokeMethodAsync('OnGeometryModified',
                JSON.stringify({ featureId: id, worldStateId: worldStateId ?? null, oldCoords, newCoords }));
        }
    });

    map.addInteraction(_drawModify);

    const selectedStyle = new ol.style.Style({
        fill:   new ol.style.Fill({ color: 'rgba(255, 165, 0, 0.35)' }),
        stroke: new ol.style.Stroke({ color: '#ff6b00', width: 2.5, lineDash: [6, 3] }),
        image:  new ol.style.Circle({
            radius: 8,
            fill:   new ol.style.Fill({ color: 'rgba(255, 165, 0, 0.55)' }),
            stroke: new ol.style.Stroke({ color: '#ff6b00', width: 2 }),
        }),
    });

    _drawSelect = new ol.interaction.Select({
        filter:    f => !f.get('_isBadge') && f.get('mspOriginalLayerId') === targetLayerId,
        layers:    [vectorLayers[PLAN_OVERLAY_ID]],
        style:     selectedStyle,
    });

    _drawSelect.on('select', evt => {
        if (evt.selected.length > 0) {
            const f      = evt.selected[0];
            const coords = geometryToCoords(f.getGeometry()).map(c => [...c]);
            _drawDotNetRef?.invokeMethodAsync('OnGeometrySelected', JSON.stringify({
                featureId: f.get('mspId'), typeIndex: f.get('mspType') ?? 0, coords,
                worldStateId: f.get('_worldStateId') ?? null,
            }));
        } else {
            _drawDotNetRef?.invokeMethodAsync('OnGeometrySelected',
                JSON.stringify({ featureId: null }));
        }
    });

    map.addInteraction(_drawSelect);
}

export function startGeometryCreate(targetLayerId, geoType, typeIndex, dotNetRef) {
    stopGeometryEditing();
    _drawDotNetRef     = dotNetRef;
    _drawTargetLayerId = targetLayerId;
    const source = ensurePlanOverlay();
    if (!map) return;

    _drawDraw = new ol.interaction.Draw({
        source, type: olDrawType(geoType),
    });

    _drawDraw.on('drawend', evt => {
        const f      = evt.feature;
        const tempId = 'new_' + Date.now() + '_' + Math.random().toString(36).slice(2);
        f.set('mspId', tempId);
        f.set('mspOriginalLayerId', targetLayerId);
        f.set('mspType', typeIndex);
        f.setId(tempId);
        const coords = geometryToCoords(f.getGeometry()).map(c => [...c]);
        _drawDotNetRef?.invokeMethodAsync('OnGeometryCreated', JSON.stringify({
            tempId, originalLayerId: targetLayerId, typeIndex, geoType, coords,
        }));
    });

    map.addInteraction(_drawDraw);
}

export function setFeatureCoords(featureId, coords) {
    const f = getPlanOverlayFeatureById(featureId);
    if (!f) return;
    setGeometryFromCoords(f.getGeometry(), coords);
    getPlanOverlaySource()?.changed();
}

export function setFeatureType(featureId, typeIndex) {
    const f = getPlanOverlayFeatureById(featureId);
    if (!f) return;
    f.set('mspType', typeIndex);
    getPlanOverlaySource()?.changed();
}

export function removeFeatureFromOverlay(featureId) {
    const source = getPlanOverlaySource();
    if (!source) return;
    const f = getPlanOverlayFeatureById(featureId);
    if (f) source.removeFeature(f);
}

export function addFeatureToOverlay(featureJson) {
    const source = getPlanOverlaySource();
    if (!source) return;
    const data = JSON.parse(featureJson);
    const { featureId, originalLayerId, typeIndex, geoType, coords } = data;
    const gt = (geoType || '').toLowerCase();
    let olGeom;
    if      (gt === 'polygon' || gt === 'polygons') olGeom = new ol.geom.Polygon([coords]);
    else if (gt === 'line'    || gt === 'lines')    olGeom = new ol.geom.LineString(coords);
    else                                            olGeom = new ol.geom.Point(coords[0] ?? [0, 0]);
    const f = new ol.Feature({ geometry: olGeom });
    f.set('mspId', featureId);
    f.set('mspOriginalLayerId', originalLayerId);
    f.set('mspType', typeIndex ?? 0);
    if (data.worldStateId) {
        f.set('_worldStateId', data.worldStateId);
        f.set('_worldStateOrigCoords', coords.map(c => [...c]));
    }
    f.setId(featureId);
    source.addFeature(f);
}

/**
 * Add or update a badge for a specific feature in the overlay.
 * Removes any existing badge for this feature first.
 * @param {string} featureId The feature to badge
 * @param {string} badgeType 'plus', 'edit', 'minus', or 'none' to remove badge
 */
export function updateFeatureBadge(featureId, badgeType) {
    const source = getPlanOverlaySource();
    if (!source) return;
    
    const feature = getPlanOverlayFeatureById(featureId);
    if (!feature) return;
    
    // Normalize featureId for consistent comparison
    const normalizedId = String(featureId);
    
    // Remove existing badge for this feature
    const existingBadges = source.getFeatures().filter(f => 
        f.get('_isBadge') && 
        String(f.get('_badgeForFeature')) === normalizedId &&
        !f.get('_isRestrictionBadge')
    );
    existingBadges.forEach(b => source.removeFeature(b));
    
    if (badgeType === 'none') return;
    
    // Create new badge
    let badge;
    if (badgeType === 'plus') badge = createPlusBadgeIcon();
    else if (badgeType === 'edit') badge = createEditBadgeIcon();
    else if (badgeType === 'minus') badge = createMinusBadgeIcon();
    else return;
    
    const geom = feature.getGeometry();
    if (!geom) return;
    
    const center = ol.extent.getCenter(geom.getExtent());
    const badgeFeature = new ol.Feature({ 
        geometry: new ol.geom.Point(center), 
        _isBadge: true,
        _badgeForFeature: normalizedId
    });
    badgeFeature.setStyle(new ol.style.Style({ image: badge }));
    source.addFeature(badgeFeature);
}

/**
 * Check if a feature has been modified from its original world-state coordinates or type.
 * @param {string} featureId The feature ID to check
 * @returns {boolean} True if the feature has been modified (coords or type changed)
 */
export function getFeatureModifiedStatus(featureId) {
    const feature = getPlanOverlayFeatureById(featureId);
    if (!feature) return false;
    
    // Check if coordinates were modified
    if (feature.get('_worldStateModified') === true) return true;
    
    // Also check if type was modified
    const origType = feature.get('_worldStateOrigType');
    const currentType = feature.get('mspType');
    if (origType !== undefined && currentType !== origType) return true;
    
    return false;
}

/**
 * Mark a world-state feature as deleted: apply red deletion styling and add minus badge.
 * Does NOT remove the feature from the overlay.
 * @param {string} featureId The feature to mark as deleted
 */
export function markFeatureAsDeleted(featureId) {
    const source = getPlanOverlaySource();
    if (!source) return;
    
    const feature = getPlanOverlayFeatureById(featureId);
    if (!feature) return;
    
    // Mark as deleted so it's filtered from save operations
    feature.set('_isMarkedForDeletion', true);
    
    // Apply red deletion styling
    feature.setStyle(deletionHighlightStyle);
    
    // Add minus badge
    updateFeatureBadge(featureId, 'minus');
}

/**
 * Unmark a world-state feature from deletion: restore normal styling.
 * The badge should be updated separately via updateFeatureBadge.
 * @param {string} featureId The feature to unmark
 */
export function unmarkFeatureAsDeleted(featureId) {
    const feature = getPlanOverlayFeatureById(featureId);
    if (!feature) return;
    
    // Clear deletion flag
    feature.unset('_isMarkedForDeletion');
    
    // Restore appropriate style based on modified status
    // Check if feature was modified (coords or type changed) before deletion
    const wasModified = feature.get('_worldStateModified') === true;
    if (wasModified) {
        // Modified features get gold overlay style
        feature.setStyle(null); // null uses layer default planOverlayStyle
    } else {
        // Unmodified world-state features should be transparent
        feature.setStyle(worldStateUnmodifiedStyle);
    }
}

/**
 * Load world-state (projected) features for a layer into the plan overlay as editable items.
 * Skips features already present in the overlay (plan's own geometry).
 * Each feature gets _worldStateId and _worldStateOrigCoords for change-detection on save.
 * @param {string} featuresJson  JSON array of {id, layerId, geoType, typeIndex, coords, worldStateId}
 */
export function loadWorldStateFeatures(featuresJson) {
    const source = ensurePlanOverlay();
    const features = JSON.parse(featuresJson);
    for (const item of features) {
        if (source.getFeatureById(String(item.id))) continue; // already present
        const gt = (item.geoType || 'polygon').toLowerCase();
        const coords = item.coords;
        if (!coords || coords.length === 0) continue;
        let olGeom;
        if      (gt === 'polygon' || gt === 'polygons') olGeom = new ol.geom.Polygon([coords]);
        else if (gt === 'line'    || gt === 'lines')    olGeom = new ol.geom.LineString(coords);
        else                                            olGeom = new ol.geom.Point(coords[0] ?? [0, 0]);
        const f = new ol.Feature({ geometry: olGeom });
        f.set('mspId',                String(item.id));
        f.set('mspOriginalLayerId',   item.layerId);
        f.set('mspType',              item.typeIndex ?? 0);
        f.set('_worldStateId',        String(item.id));                    // id = worldStateId for base/prior features
        f.set('_worldStateOrigCoords', coords.map(c => [...c]));          // snapshot for change detection
        f.set('_worldStateOrigType',  item.typeIndex ?? 0);                // original type for change detection
        f.set('_worldStateModified',  false);                              // not modified yet
        f.setStyle(worldStateUnmodifiedStyle);                             // transparent until modified
        f.setId(String(item.id));
        source.addFeature(f);
    }
}

/**
 * When the geometry tool closes, remove world-state features whose coordinates and type are unchanged.
 * Modified world-state features are kept in the overlay so they can be saved on Accept.
 * @param {string} layerId
 */
export function removeUnchangedWorldStateFeatures(layerId) {
    const source = getPlanOverlaySource();
    if (!source) return;
    const toRemove = [];
    for (const f of source.getFeatures()) {
        if (f.get('mspOriginalLayerId') !== layerId) continue;
        if (!f.get('_worldStateId')) continue; // not a world-state feature
        const origCoords = f.get('_worldStateOrigCoords');
        const origType = f.get('_worldStateOrigType');
        if (!origCoords) { toRemove.push(f); continue; }
        const curCoords = geometryToCoords(f.getGeometry());
        const curType = f.get('mspType');
        // Check both coordinates and type - if either changed, keep the feature
        const coordsChanged = !_coordsEqual(origCoords, curCoords);
        const typeChanged = origType !== undefined && curType !== origType;
        if (!coordsChanged && !typeChanged) toRemove.push(f); // unchanged
        // else: modified — keep in overlay until Accept
    }
    for (const f of toRemove) source.removeFeature(f);
}

function _coordsEqual(a, b) {
    if (a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) {
        if (!a[i] || !b[i]) return false;
        if (Math.abs(a[i][0] - b[i][0]) > 0.01) return false;
        if (Math.abs(a[i][1] - b[i][1]) > 0.01) return false;
    }
    return true;
}

/**
 * Returns JSON array of all plan-overlay features for a layer.
 * Used by AcceptEditAsync to determine geometry changes to save.
 * Each entry: { featureId, typeIndex, worldStateId, originalCoords, coords }
 */
export function getOverlayFeaturesJson(layerId) {
    const source = getPlanOverlaySource();
    if (!source) return '[]';
    const result = [];
    for (const f of source.getFeatures()) {
        if (f.get('mspOriginalLayerId') !== layerId) continue;
        if (f.get('_isBadge')) continue;
        // Exclude features marked for deletion - they're handled separately via _deletedWorldStateIds
        if (f.get('_isMarkedForDeletion')) continue;
        const coords     = geometryToCoords(f.getGeometry()).map(c => [...c]);
        const origCoords = f.get('_worldStateOrigCoords');
        result.push({
            featureId:      String(f.getId() ?? f.get('mspId') ?? ''),
            typeIndex:      f.get('mspType') ?? 0,
            worldStateId:   f.get('_worldStateId') ?? null,
            originalCoords: origCoords ? origCoords.map(c => [...c]) : null,
            coords,
        });
    }
    return JSON.stringify(result);
}

/**
 * Hide specific base-layer features that have been deleted or superseded by earlier approved plans,
 * so the map reflects the world state at the time the viewed plan would be implemented.
 * @param {string} projectionJson  JSON: { hidden: { layerId: [featureId, ...], ... }, added: [{ layerId, geoType, id, typeIndex, coords }] }
 */
export function applyPlanProjection(projectionJson) {
    const projection = JSON.parse(projectionJson);

    // Step 1 — inject geometries added or altered by prior plans into their base layers.
    // Do this BEFORE hiding so the hide pass can also catch plan-added features.
    const addedList = Array.isArray(projection.added) ? projection.added : [];
    for (const item of addedList) {
        const layer = vectorLayers[item.layerId];
        if (!layer) continue;
        const gt = (item.geoType || 'point').toLowerCase();
        const coords = item.coords;
        if (!coords || coords.length === 0) continue;

        let olGeom;
        if (gt === 'polygon' || gt === 'polygons') {
            olGeom = new ol.geom.Polygon([coords]);
        } else if (gt === 'line' || gt === 'lines') {
            olGeom = new ol.geom.LineString(coords);
        } else {
            olGeom = new ol.geom.Point(coords[0]);
        }
        const feature = new ol.Feature({
            geometry: olGeom,
            mspId: item.id,
            mspType: item.typeIndex ?? 0,
            _projAdded: true
        });
        layer.getSource().addFeature(feature);
    }

    // Step 2 — hide deleted / superseded features (original snapshot and projection-added alike).
    const hiddenMap = projection.hidden ?? {};
    for (const [layerId, ids] of Object.entries(hiddenMap)) {
        const layer = vectorLayers[layerId];
        if (!layer) continue;
        const idSet = new Set(ids.map(String));
        for (const feature of layer.getSource().getFeatures()) {
            if (idSet.has(String(feature.get('mspId')))) {
                feature.set('_projHidden', true);
            }
        }
        layer.getSource().changed();
    }
}

/** Restore all features hidden/added by applyPlanProjection. */
export function clearPlanProjection() {
    for (const layer of Object.values(vectorLayers)) {
        if (typeof layer.getSource !== 'function') continue;
        const source = layer.getSource();
        // Remove projection-added features.
        const toRemove = source.getFeatures().filter(f => f.get('_projAdded'));
        for (const f of toRemove) source.removeFeature(f);
        // Unhide projection-hidden features.
        let changed = toRemove.length > 0;
        for (const feature of source.getFeatures()) {
            if (feature.get('_projHidden')) {
                feature.unset('_projHidden');
                changed = true;
            }
        }
        if (changed) source.changed();
    }
}

export function setPlanOverlayVisible(visible) {
    const layer = vectorLayers[PLAN_OVERLAY_ID];
    if (layer) layer.setVisible(visible);
}

/** Hide a specific base geometry feature by its ID (used when editing modifies world-state geometry). */
export function hideBaseGeometry(layerId, featureId) {
    const layer = vectorLayers[layerId];
    if (!layer) return;
    const source = layer.getSource();
    for (const feature of source.getFeatures()) {
        if (String(feature.get('mspId')) === String(featureId)) {
            feature.set('_projHidden', true);
            source.changed();
            break;
        }
    }
}

/** Unhide a specific base geometry feature by its ID (used when undo restores original state). */
export function unhideBaseGeometry(layerId, featureId) {
    const layer = vectorLayers[layerId];
    if (!layer) return;
    const source = layer.getSource();
    for (const feature of source.getFeatures()) {
        if (String(feature.get('mspId')) === String(featureId)) {
            if (feature.get('_projHidden')) {
                feature.unset('_projHidden');
                source.changed();
            }
            break;
        }
    }
}

/** Check if a feature matches its original state (both coordinates and type). */
export function isFeatureInOriginalState(featureId) {
    if (!PLAN_OVERLAY_ID || !vectorLayers[PLAN_OVERLAY_ID]) return false;
    const overlaySource = vectorLayers[PLAN_OVERLAY_ID].getSource();
    const overlayFeature = overlaySource.getFeatures().find(f => String(f.getId()) === String(featureId));
    if (!overlayFeature) return false;
    
    const worldStateId = overlayFeature.get('_worldStateId');
    if (!worldStateId) return false; // Not a world-state feature
    
    const origCoords = overlayFeature.get('_worldStateOrigCoords');
    const origType = overlayFeature.get('_worldStateOrigType');
    if (!origCoords) return false;
    
    const currentGeom = overlayFeature.getGeometry();
    const currentType = overlayFeature.get('mspType');
    if (!currentGeom) return false;
    
    // Check if type matches original
    if (origType !== undefined && currentType !== origType) {
        return false;
    }
    
    let currentCoords;
    if (currentGeom.getType() === 'Polygon') {
        currentCoords = currentGeom.getCoordinates()[0];
    } else if (currentGeom.getType() === 'LineString') {
        currentCoords = currentGeom.getCoordinates();
    } else if (currentGeom.getType() === 'Point') {
        currentCoords = [currentGeom.getCoordinates()];
    } else {
        return false;
    }
    
    // Compare coordinates using same tolerance as _coordsEqual for consistency
    if (currentCoords.length !== origCoords.length) return false;
    
    for (let i = 0; i < currentCoords.length; i++) {
        if (Math.abs(currentCoords[i][0] - origCoords[i][0]) > 0.01 ||
            Math.abs(currentCoords[i][1] - origCoords[i][1]) > 0.01) {
            return false;
        }
    }
    
    return true;
}

/** Check if a feature's coordinates and type match original and unhide base if so. */
export function checkAndUnhideIfOriginal(layerId, featureId) {
    if (isFeatureInOriginalState(featureId)) {
        const overlaySource = vectorLayers[PLAN_OVERLAY_ID].getSource();
        const overlayFeature = overlaySource.getFeatures().find(f => String(f.getId()) === String(featureId));
        if (overlayFeature) {
            const worldStateId = overlayFeature.get('_worldStateId');
            if (worldStateId) {
                unhideBaseGeometry(layerId, worldStateId);
            }
        }
    }
}

// ── Draggable floating panels ─────────────────────────────────────────────────

/**
 * Make a panel element draggable via a handle child that has the
 * `plans-panel-drag-handle` class.  Called automatically by the
 * MutationObserver set up in initMap.
 */
function initPanelDrag(panel) {
    if (panel._dragInitialized) return;
    panel._dragInitialized = true;

    const handle = panel.querySelector('.plans-panel-drag-handle');
    if (!handle) return;

    let startX, startY, startLeft, startTop;

    handle.addEventListener('pointerdown', e => {
        // Only primary button
        if (e.button !== 0) return;
        // Don't swallow clicks on interactive children (close button, etc.)
        if (e.target.closest('button, a, input, select')) return;
        e.preventDefault();
        handle.setPointerCapture(e.pointerId);

        // Resolve current position: if still using CSS transform centering,
        // switch to explicit left/top so dragging works predictably.
        const rect = panel.getBoundingClientRect();
        panel.style.left      = rect.left + 'px';
        panel.style.top       = rect.top  + 'px';
        panel.style.transform = 'none';
        panel.classList.add('plans-panel--dragged');

        startX    = e.clientX;
        startY    = e.clientY;
        startLeft = rect.left;
        startTop  = rect.top;
    });

    handle.addEventListener('pointermove', e => {
        if (e.buttons !== 1) return;
        const dx = e.clientX - startX;
        const dy = e.clientY - startY;
        // Clamp inside the viewport with a small margin
        const margin = 8;
        const newLeft = Math.max(margin, Math.min(window.innerWidth  - panel.offsetWidth  - margin, startLeft + dx));
        const newTop  = Math.max(margin, Math.min(window.innerHeight - panel.offsetHeight - margin, startTop  + dy));
        panel.style.left = newLeft + 'px';
        panel.style.top  = newTop  + 'px';
    });
}

// Watch for the plans panel being added to the DOM and auto-init drag on it.
(function observeFloatingPanels() {
    const tryInit = root => {
        const panel = root.id === 'plans-panel-float' ? root
            : root.querySelector?.('#plans-panel-float');
        if (panel) initPanelDrag(panel);
    };

    const observer = new MutationObserver(mutations => {
        for (const m of mutations) {
            for (const node of m.addedNodes) {
                if (node.nodeType === 1) tryInit(node);
            }
        }
    });

    observer.observe(document.body, { childList: true, subtree: true });
}());
