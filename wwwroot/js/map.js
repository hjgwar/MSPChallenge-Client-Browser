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

        features.push(new ol.Feature({ geometry: olGeom, mspId: g.id, mspType: g.type ?? 0, mspLabel: labelVal, mspData: g.data ?? {} }));
    }

    if (features.length === 0) return;

    const styleFunction = (feature, resolution) => {
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
                if (minCutoff !== null && minCutoff !== undefined && grey <= minCutoff) {
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

export function setView(lat, lng, zoom) {
    if (!map) return;
    const center = ol.proj.transform([lng, lat], 'EPSG:4326', 'EPSG:3035');
    map.getView().animate({ center, zoom, duration: 300 });
}

export function invalidateSize() {
    if (map) map.updateSize();
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
            const z = layer.getZIndex() ?? 0;
            if (z > topZIndex) {
                topZIndex = z;
                topProps  = feature.get('mspData') ?? {};
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
                    topProps   = {};
                }
            }
        }

        if (topLayerId !== null) {
            _dotNetRef.invokeMethodAsync('OnMapClick', JSON.stringify({
                layerId: topLayerId,
                props:   topProps
            }));
        }
    };

    map.on('singleclick', _clickHandler);
}

export function unregisterClickHandler() {
    if (map && _clickHandler) {
        map.un('singleclick', _clickHandler);
        _clickHandler = null;
    }
    _dotNetRef = null;
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
