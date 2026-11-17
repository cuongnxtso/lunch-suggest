// Thin Leaflet wrapper used from Blazor. Tiles: OpenStreetMap (free, attribution required).
const colors = { place: '#6c757d', candidate: '#0d6efd', picked: '#dc3545' };

export function createMap(el, lat, lng, radius) {
    const map = L.map(el);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        referrerPolicy: 'strict-origin-when-cross-origin',
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    }).addTo(map);
    const center = L.marker([lat, lng]).addTo(map);
    const circle = L.circle([lat, lng], { radius, fill: false, weight: 1 }).addTo(map);
    // Bounds from the point, not the circle: the circle has no projection until the map has a view.
    map.fitBounds(L.latLng(lat, lng).toBounds(radius * 2));
    return { map, center, circle, layer: L.layerGroup().addTo(map) };
}

// places: [{ name, lat, lng, kind: 'place' | 'candidate' | 'picked' }]
export function setPlaces(m, places, fit) {
    m.layer.clearLayers();
    if (fit && places.length) {
        const c = m.center.getLatLng();
        m.map.fitBounds(L.latLngBounds(places.map(p => [p.lat, p.lng])).extend(c.toBounds(m.circle.getRadius() * 2)), { padding: [20, 20] });
    }
    for (const p of places) {
        const label = document.createElement('span');
        label.textContent = p.name; // textContent, never innerHTML: names come from public map data
        L.circleMarker([p.lat, p.lng], {
            radius: p.kind === 'picked' ? 12 : p.kind === 'candidate' ? 8 : 6,
            color: colors[p.kind], fillOpacity: 0.8, weight: 2,
        }).bindTooltip(label).addTo(m.layer);
    }
}
