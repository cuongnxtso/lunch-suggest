// Thin Leaflet wrapper used from Blazor. Tiles: OpenStreetMap (free, attribution required).
// selected = place clicked in the list; voted = poll option with your vote. Distinct so one never looks like the other.
const colors = { place: '#6c757d', candidate: '#0d6efd', voted: '#198754', picked: '#dc3545', selected: '#fd7e14' };
const sizes = { place: 6, candidate: 8, voted: 9, picked: 12, selected: 13 };

export function createMap(el, lat, lng, radius) {
    const map = L.map(el);
    // Places sit above the meeting-point pin (marker pane, 600), or the nearest ones hide under it. Tooltips are 650.
    map.createPane('places').style.zIndex = 620;
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

// "Suggest a place": each click on the map moves one purple pin there and tells .NET where it is.
export function startPick(m, dotnet) {
    stopPick(m);
    m.map.getContainer().style.cursor = 'crosshair';
    m.onPick = e => {
        if (m.pick) m.pick.setLatLng(e.latlng);
        else m.pick = L.circleMarker(e.latlng, { pane: 'places', radius: 12, color: '#6f42c1', fillOpacity: 0.5, weight: 3 }).addTo(m.map);
        dotnet.invokeMethodAsync('OnMapPicked', e.latlng.lat, e.latlng.lng);
    };
    m.map.on('click', m.onPick);
}

export function stopPick(m) {
    if (m.onPick) m.map.off('click', m.onPick);
    m.pick?.remove();
    m.onPick = m.pick = null;
    m.map.getContainer().style.cursor = '';
}

// places: [{ name, lat, lng, kind: 'place' | 'candidate' | 'voted' | 'picked' | 'selected' }]
export function setPlaces(m, places, fit) {
    m.layer.clearLayers();
    if (fit && places.length) {
        const c = m.center.getLatLng();
        m.map.fitBounds(L.latLngBounds(places.map(p => [p.lat, p.lng])).extend(c.toBounds(m.circle.getRadius() * 2)), { padding: [20, 20] });
    }
    let selected = null;
    for (const p of places) {
        const label = document.createElement('span');
        label.textContent = p.name; // textContent, never innerHTML: names come from public map data
        const isSelected = p.kind === 'selected';
        if (isSelected) selected = `${p.lat},${p.lng}`;
        L.circleMarker([p.lat, p.lng], {
            pane: 'places',
            radius: sizes[p.kind],
            color: colors[p.kind], fillOpacity: 0.8, weight: isSelected ? 4 : 2,
        }).bindTooltip(label, { permanent: isSelected }).addTo(m.layer);
        // Pan only when the selection changes, not on every refresh (others voting re-renders the map).
        if (isSelected && selected !== m.selected) m.map.panTo([p.lat, p.lng]);
    }
    m.selected = selected;
}
