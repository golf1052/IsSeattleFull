(() => {
    "use strict";
    const element = id => document.getElementById(id);
    const number = new Intl.NumberFormat("en-US", { maximumFractionDigits: 0 });
    let catalog;
    let map;
    let tiles;
    let outlines;
    let generation = 0;
    let controller;
    let debounce;
    let timeout;
    let selectedIndex = 0;
    let retryAction;
    const boundaryCache = new Map();

    function message(text, error = false, retry = null) {
        element("imageMessage").hidden = false;
        element("imageStatus").textContent = text;
        element("imageStatus").className = error ? "error" : "";
        element("retry").hidden = retry === null;
        retryAction = retry;
    }
    element("retry").addEventListener("click", () => retryAction?.());

    async function boundaries(group, token) {
        controller?.abort();
        controller = new AbortController();
        if (outlines) map.removeLayer(outlines);
        outlines = null;
        if (!element("showParcels").checked) return;
        let reloadCatalog = false;
        try {
            let data = boundaryCache.get(group.id);
            if (!data) {
                const response = await fetch(`/api/aerial-groups/${encodeURIComponent(group.id)}/parcels?revision=${encodeURIComponent(catalog.revision)}`,
                    { signal: controller.signal, cache: "no-cache" });
                if (!response.ok) {
                    if (response.status === 409) {
                        reloadCatalog = true;
                        throw new Error("The parcel catalog changed. Reload the catalog to continue.");
                    }
                    throw new Error(`Parcel outlines could not load (HTTP ${response.status}).`);
                }
                data = await response.json();
                if (data.type !== "FeatureCollection" || !Array.isArray(data.features) ||
                    data.features.some(feature => !["Polygon", "MultiPolygon"].includes(feature.geometry?.type))) {
                    throw new Error("Parcel outlines have invalid geometry.");
                }
                if (token !== generation) return;
                boundaryCache.set(group.id, data);
                if (boundaryCache.size > 20) boundaryCache.delete(boundaryCache.keys().next().value);
            }
            if (token !== generation || !element("showParcels").checked) return;
            outlines = L.geoJSON(data, { interactive: false, style: { color: "#fff", weight: 2, fill: false, opacity: 1 } }).addTo(map);
        } catch (error) {
            if (error.name === "AbortError" || token !== generation) return;
            console.error("Unable to load parcel outlines:", error);
            message(error.message, true, reloadCatalog ? load : () => selectGroup(selectedIndex));
        }
    }

    function fit(group) {
        const [west, south, east, north] = group.bounds;
        map.fitBounds(L.latLngBounds([[south, west], [north, east]]).pad(0.2), { animate: false, maxZoom: 20 });
        const query = new URLSearchParams({
            api: "1", map_action: "map", center: `${(south + north) / 2},${(west + east) / 2}`,
            zoom: String(Math.min(21, Math.max(0, Math.floor(map.getZoom())))), basemap: "satellite"
        });
        element("googleMaps").href = `https://www.google.com/maps/@?${query}`;
    }

    function selectGroup(index) {
        const group = catalog.groups[index];
        if (!group) {
            message("This aerial place is unavailable. Reload the catalog.", true, load);
            return;
        }
        selectedIndex = index;
        const token = ++generation;
        clearTimeout(timeout);
        controller?.abort();
        if (tiles) map.removeLayer(tiles);
        if (outlines) map.removeLayer(outlines);
        outlines = null;
        element("place").value = String(index);
        element("position").textContent = `${number.format(index + 1)} / ${number.format(catalog.groups.length)}`;
        element("place").setAttribute("aria-valuetext", `${index + 1} of ${catalog.groups.length}: ${group.displayAddress ?? "Address unavailable"}`);
        element("address").textContent = group.displayAddress ?? "Address unavailable";
        element("lotArea").textContent = group.lotAreaSquareFeet === null ? "Lot area unavailable"
            : `${group.lotAreaIsApproximate ? "Approx. " : ""}${number.format(group.lotAreaSquareFeet)} sq ft`;
        element("parcelIds").hidden = false;
        element("parcelIds").open = false;
        element("parcelIdList").replaceChildren();
        for (const pin of group.parcelPins) {
            const item = document.createElement("li");
            const label = document.createElement("span");
            label.className = "parcel-pin";
            label.textContent = pin;
            const link = document.createElement("a");
            link.href = `https://blue.kingcounty.com/Assessor/eRealProperty/Dashboard.aspx?ParcelNbr=${encodeURIComponent(pin)}`;
            link.textContent = "View on Assessor";
            link.className = "assessor-link";
            link.target = "_blank";
            link.rel = "noopener noreferrer";
            link.setAttribute("aria-label", `View parcel ${pin} on King County Assessor`);
            item.append(label, ". ", link);
            element("parcelIdList").append(item);
        }
        element("aerial").setAttribute("aria-label", `2025 aerial view: ${group.displayAddress ?? "Address unavailable"}`);
        message("Loading 2025 imagery...");
        fit(group);
        element("googleMaps").hidden = false;
        const layer = L.tileLayer(
            "https://gismaps.kingcounty.gov/arcgis/rest/services/BaseMaps/KingCo_Aerial_2025/MapServer/tile/{z}/{y}/{x}",
            {
                noWrap: true, maxZoom: 21, maxNativeZoom: 21,
                attribution: '2025 imagery &copy; EagleView Technologies, Inc. / <a href="https://gismaps.kingcounty.gov/arcgis/rest/services/BaseMaps/KingCo_Aerial_2025/MapServer" target="_blank" rel="noopener">King County</a>'
            });
        let loaded = 0;
        let failed = false;
        layer.on("tileload", () => { loaded++; });
        layer.on("tileerror", () => {
            failed = true;
            if (token !== generation) return;
            clearTimeout(timeout);
            message("Some aerial imagery could not load. Retry this view; its address remains available.", true, () => selectGroup(index));
        });
        layer.on("load", () => {
            if (token !== generation || failed || loaded === 0) return;
            clearTimeout(timeout);
            if (!element("imageStatus").classList.contains("error")) element("imageMessage").hidden = true;
        });
        tiles = layer;
        layer.addTo(map);
        timeout = setTimeout(() => {
            if (token === generation) message("Aerial imagery is taking too long to load. Retry this view.", true, () => selectGroup(index));
        }, 20000);
        boundaries(group, token);
    }

    async function load() {
        try {
            if (!globalThis.L) throw new Error("The map library could not load. Check your internet connection and retry.");
            const response = await fetch("/api/aerial-groups", { cache: "no-cache" });
            if (!response.ok) {
                const problem = await response.json();
                throw new Error(problem.detail || `Aerial catalog request failed (HTTP ${response.status}).`);
            }
            const data = await response.json();
            if (data.version !== 2 || typeof data.revision !== "string" || !Array.isArray(data.groups))
                throw new Error("Aerial catalog is invalid. Run 'dotnet run -- catalog' to regenerate it.");
            if (data.groups.length === 0) throw new Error("No aerial places are available. Export parcels and prepare the catalog first.");
            const ids = new Set();
            for (const group of data.groups) {
                if (typeof group.id !== "string" || ids.has(group.id) ||
                    !Array.isArray(group.bounds) || group.bounds.length !== 4 || !group.bounds.every(Number.isFinite) ||
                    group.bounds[0] >= group.bounds[2] || group.bounds[1] >= group.bounds[3] ||
                    group.displayAddress !== null && typeof group.displayAddress !== "string" ||
                    !Array.isArray(group.parcelPins) || group.parcelPins.length === 0 ||
                    !group.parcelPins.every(pin => typeof pin === "string" && pin.trim().length > 0) ||
                    new Set(group.parcelPins).size !== group.parcelPins.length ||
                    group.lotAreaSquareFeet !== null && (typeof group.lotAreaSquareFeet !== "number" || !Number.isFinite(group.lotAreaSquareFeet) || group.lotAreaSquareFeet <= 0) ||
                    typeof group.lotAreaIsApproximate !== "boolean") {
                    throw new Error("An aerial place has invalid attributes. Regenerate the catalog.");
                }
                ids.add(group.id);
            }
            if (catalog?.revision !== data.revision) boundaryCache.clear();
            catalog = data;
            if (!map) {
                map = L.map("aerial", {
                    zoomControl: false, dragging: false, scrollWheelZoom: false, doubleClickZoom: false,
                    touchZoom: false, boxZoom: false, keyboard: false, zoomSnap: 0
                });
                map.on("click", () => element("place").focus({ preventScroll: true }));
                new ResizeObserver(() => {
                    map.invalidateSize();
                    if (catalog) fit(catalog.groups[selectedIndex]);
                }).observe(element("aerial"));
            }
            element("controls").disabled = false;
            element("place").max = String(catalog.groups.length - 1);
            element("place").disabled = catalog.groups.length === 1;
            element("refresh").disabled = catalog.groups.length === 1;
            selectGroup(Math.floor(Math.random() * catalog.groups.length));
        } catch (error) {
            console.error("Unable to prepare aerial browser:", error);
            element("controls").disabled = true;
            element("address").textContent = "Address unavailable";
            element("parcelIds").hidden = true;
            element("parcelIds").open = false;
            element("parcelIdList").replaceChildren();
            element("googleMaps").hidden = true;
            message(error.message, true, globalThis.L ? load : () => location.reload());
        }
    }
    element("place").addEventListener("input", () => {
        clearTimeout(debounce);
        debounce = setTimeout(() => selectGroup(Number(element("place").value)), 150);
    });
    element("showParcels").addEventListener("change", () => {
        if (catalog) boundaries(catalog.groups[selectedIndex], generation);
    });
    element("refresh").addEventListener("click", () => {
        if (!catalog || element("controls").disabled || catalog.groups.length < 2) return;
        clearTimeout(debounce);
        const offset = 1 + Math.floor(Math.random() * (catalog.groups.length - 1));
        selectGroup((selectedIndex + offset) % catalog.groups.length);
    });
    load();
})();
