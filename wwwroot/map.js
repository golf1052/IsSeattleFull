(() => {
    "use strict";

    const categories = {
        vacant_or_cleared: { label: "Vacant / cleared", color: "#007f8b" },
        surface_parking: { label: "Surface parking", color: "#b76a00" },
        low_value_improvements: { label: "Low-value improvements", color: "#7d4bd1" },
        needs_review: { label: "Unresolved assessment", color: "#d34a32" }
    };
    const statuses = {
        candidate: { label: "Candidate", color: "#166f49" },
        needs_review: { label: "Needs review", color: "#d34a32" }
    };
    const number = new Intl.NumberFormat("en-US");
    const currency = new Intl.NumberFormat("en-US", {
        style: "currency", currency: "USD", maximumFractionDigits: 0
    });
    const element = (id) => document.getElementById(id);
    const showError = (message) => {
        element("errorMessage").textContent = message;
        element("errorMessage").hidden = false;
        element("loadStatus").textContent = "The parcel map could not be loaded.";
    };
    if (!globalThis.L) {
        showError("The map library could not be loaded. Check your internet connection and reload this page.");
        return;
    }

    const map = L.map("map", { preferCanvas: true }).setView([47.6062, -122.3321], 11);
    const tiles = L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
        maxZoom: 19,
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
    }).addTo(map);
    tiles.on("tileerror", () => {
        element("errorMessage").textContent = "Some street-map tiles could not be loaded. Parcel boundaries remain available; check your connection.";
        element("errorMessage").hidden = false;
    });
    const renderer = L.canvas({ padding: 0.3 });
    map.createPane("aerialImagery");
    map.getPane("aerialImagery").style.zIndex = "250";
    let features = [];
    let parcelLayer;
    let selectedLayer;
    let aerialLayer;
    const layersByPin = new Map();
    const aerialGroupsByPin = new Map();

    function style(feature) {
        const p = feature.properties;
        const palette = element("colorBy").value === "status" ? statuses[p.status] : categories[p.category];
        return { color: palette.color, fillColor: palette.color, weight: 1.5, opacity: 0.95, fillOpacity: aerialLayer ? 0.08 : 0.3 };
    }

    function refreshStyles() {
        if (parcelLayer) parcelLayer.setStyle(style);
        if (selectedLayer) selectedLayer.setStyle({ weight: 3.5, fillOpacity: aerialLayer ? 0.18 : 0.55 });
    }

    function showAerial(group) {
        if (aerialLayer) map.removeLayer(aerialLayer);
        const [west, south, east, north] = group.Bounds;
        const bounds = L.latLngBounds([[south, west], [north, east]]).pad(0.25);
        let loaded = 0;
        let failed = 0;
        const layer = L.tileLayer(
            "https://gismaps.kingcounty.gov/arcgis/rest/services/BaseMaps/KingCo_Aerial_2025/MapServer/tile/{z}/{y}/{x}",
            {
                pane: "aerialImagery", bounds, noWrap: true, maxZoom: 21, maxNativeZoom: 21,
                attribution: '2025 imagery &copy; EagleView Technologies, Inc., ' +
                    '<a href="https://gismaps.kingcounty.gov/arcgis/rest/services/BaseMaps/KingCo_Aerial_2025/MapServer" target="_blank" rel="noopener">King County</a>'
            }
        );
        aerialLayer = layer;
        element("hideAerial").hidden = false;
        element("aerialStatus").className = "status";
        element("aerialStatus").textContent = `Loading 2025 aerial imagery for ${group.Id}...`;
        layer.on("tileload", () => { loaded++; });
        layer.on("tileerror", () => {
            failed++;
            if (aerialLayer !== layer) return;
            element("aerialStatus").className = "error";
            element("aerialStatus").textContent = "Some aerial imagery tiles failed to load. Parcel outlines remain available; retry or return to street view.";
        });
        layer.on("load", () => {
            if (aerialLayer !== layer || failed > 0) return;
            element("aerialStatus").className = loaded > 0 ? "status" : "error";
            element("aerialStatus").textContent = loaded > 0
                ? `Showing ${group.Id}: imagery acquired in 2025, not a live view. Source: EagleView Technologies, Inc. / King County.`
                : "No aerial tiles were returned for this view. Zoom to the group or return to street view.";
        });
        layer.addTo(map);
        refreshStyles();
        map.fitBounds(bounds, { padding: [30, 30], maxZoom: 20 });
    }

    element("hideAerial").addEventListener("click", () => {
        if (aerialLayer) map.removeLayer(aerialLayer);
        aerialLayer = null;
        element("hideAerial").hidden = true;
        element("aerialStatus").className = "status";
        element("aerialStatus").textContent = "Street view. Select a parcel to preview its group's aerial imagery.";
        refreshStyles();
    });

    function drawLegend() {
        const legend = element("colorLegend");
        legend.replaceChildren();
        const palette = element("colorBy").value === "status" ? statuses : categories;
        for (const item of Object.values(palette)) {
            const row = document.createElement("div");
            row.className = "legend-item";
            const swatch = document.createElement("span");
            swatch.className = "swatch";
            swatch.style.borderColor = item.color;
            swatch.style.backgroundColor = item.color + "4d";
            swatch.setAttribute("aria-hidden", "true");
            const label = document.createElement("span");
            label.textContent = item.label;
            row.append(swatch, label);
            legend.append(row);
        }
    }

    function showDetails(feature) {
        const p = feature.properties;
        element("detailsHeading").textContent = `PIN ${p.pin}`;
        const details = element("parcelDetails");
        details.replaceChildren();
        const badge = document.createElement("span");
        badge.className = p.status === "needs_review" ? "badge review" : "badge";
        badge.textContent = statuses[p.status].label;
        const values = document.createElement("dl");
        const field = (label, value) => {
            const term = document.createElement("dt");
            term.textContent = label;
            const description = document.createElement("dd");
            description.textContent = value;
            values.append(term, description);
        };
        field("Category", categories[p.category].label);
        const aerialGroup = aerialGroupsByPin.get(p.pin);
        if (aerialGroup) {
            field("Aerial-photo group", `${aerialGroup.Id}: ${number.format(aerialGroup.ParcelCount)} parcel(s)`);
            field("Group PINs", aerialGroup.ParcelPins.join(", "));
        }
        if (p.neighborException) {
            field("Neighbor-based exception", `${p.neighborException.OverriddenExclusions.map(reason => reason.replaceAll("_", " ")).join("; ")}; ` +
                `adjacent accepted parcel(s): ${p.neighborException.NeighborPins.join(", ")}. Source attributes remain unchanged.`);
        }
        field("Property name", p.propertyNames?.length ? p.propertyNames.join("; ") : "Not recorded");
        field("Address", p.addresses.length ? p.addresses.join("; ") : "No address recorded");
        field("Appraised improvements", p.appraisedImprovementValue === null ? "Unresolved in source data" : currency.format(p.appraisedImprovementValue));
        field(p.assessment || p.assessor ? "Source tax roll year(s)" : "Tax roll year", p.taxRollYears.map((year) => year ?? "Unknown").join(", "));
        if (p.assessment || p.assessor) {
            const assessment = p.assessment ?? p.assessor.Assessment;
            field("Value source", assessment?.Source?.replaceAll("_", " ") || "No eligible assessment supplied");
            if (assessment?.SelectionReason) field("Value selection", assessment.SelectionReason.replaceAll("_", " "));
            if (p.asOfTaxRollYear) field("Compared through tax year", `${p.asOfTaxRollYear} (unchanged values can carry forward)`);
        }
        field("Present use", p.presentUses.map((use) => `${use.description || "Unknown"} (${use.code ?? "no code"})`).join("; "));
        field("Lot area (sq ft)", p.lotSquareFeet.map((area) => area === null ? "Unknown" : number.format(area)).join(", "));
        field("Tax accounts", p.accountNumbers.length ? p.accountNumbers.map((account) => account ?? "Unknown").join(", ")
            : "No account link supplied; parcel history can still provide a value");
        details.append(badge, values);
        if (aerialGroup) {
            const actions = document.createElement("div");
            actions.className = "map-actions";
            const fit = document.createElement("button");
            fit.type = "button";
            fit.textContent = "Fit whole aerial group";
            fit.addEventListener("click", () => {
                const [west, south, east, north] = aerialGroup.Bounds;
                map.fitBounds([[south, west], [north, east]], { padding: [30, 30], maxZoom: 18 });
            });
            actions.append(fit);
            const aerial = document.createElement("button");
            aerial.type = "button";
            aerial.textContent = "Show 2025 aerial imagery";
            aerial.addEventListener("click", () => showAerial(aerialGroup));
            actions.append(aerial);
            details.append(actions);
        }
        const list = (label, entries, warning) => {
            if (!entries.length) return;
            const heading = document.createElement("p");
            heading.className = "detail-label";
            heading.textContent = label;
            const items = document.createElement("ul");
            items.className = warning ? "detail-list warning-list" : "detail-list";
            for (const entry of entries) {
                const item = document.createElement("li");
                item.textContent = entry.replaceAll("_", " ");
                items.append(item);
            }
            details.append(heading, items);
        };
        list("Why it is included", p.inclusionReasons, false);
        list("Review warnings", p.warnings, true);
        if (p.assessor) {
            const a = p.assessor;
            list("Assessor building records", (a.Buildings || []).map((building) =>
                `${building.Kind} #${building.Number}: ${building.SquareFeet === null ? "unknown area" : number.format(building.SquareFeet) + " sq ft"}, ` +
                `built ${building.YearBuilt || "unknown"}, ${building.PercentComplete === null ? "completion unknown" : building.PercentComplete + "% complete"}`), false);
            if (a.Condo) {
                const detail = document.createElement("p");
                detail.className = "muted";
                detail.textContent = `Condo units: ${a.Condo.KnownUnitAssessments} with assessments of ${a.Condo.UnitPins.length} listed ` +
                    `(${a.Condo.ExpectedUnits ?? "unknown"} expected). ${a.Condo.CompleteValuationCoverage ? "Complete valuation coverage." : "Incomplete valuation coverage."}`;
                details.append(detail);
            }
            list("Recent assessor permit records", (a.Permits || []).map((permit) =>
                `${permit.Number}: ${permit.Type || "type unknown"}, ${permit.Status || "status unknown"}`), false);
        }
        if (feature.geometry === null) {
            list("Not mapped", ["Boundary geometry is missing or invalid; this parcel cannot be located on the map."], true);
        }
    }

    function select(feature, layer) {
        if (selectedLayer) selectedLayer.setStyle(style(selectedLayer.feature));
        selectedLayer = layer;
        if (layer) {
            layer.setStyle({ weight: 3.5, fillOpacity: aerialLayer ? 0.18 : 0.55 });
            layer.bringToFront();
        }
        showDetails(feature);
    }

    function applyFilters() {
        const allowed = new Set(Array.from(document.querySelectorAll('input[name="category"]:checked'), (input) => input.value));
        const reviewOnly = element("reviewOnly").checked;
        const visible = features.filter((feature) => feature.geometry !== null &&
            allowed.has(feature.properties.category) && (!reviewOnly || feature.properties.status === "needs_review"));
        if (parcelLayer) map.removeLayer(parcelLayer);
        selectedLayer = null;
        layersByPin.clear();
        parcelLayer = L.geoJSON(visible, {
            renderer,
            style,
            onEachFeature(feature, layer) {
                layersByPin.set(feature.properties.pin, layer);
                layer.on("click", () => select(feature, layer));
            }
        }).addTo(map);
        element("visibleCount").textContent = number.format(visible.length);
        element("reviewCount").textContent = number.format(visible.filter((feature) => feature.properties.status === "needs_review").length);
        element("fitParcels").disabled = visible.length === 0;
        element("searchStatus").textContent = "";
        element("detailsHeading").textContent = "Select a boundary";
        element("parcelDetails").replaceChildren();
        const hint = document.createElement("p");
        hint.className = "muted";
        hint.textContent = visible.length ? "Click an outlined parcel or search by PIN to inspect it." : "No parcels match these filters.";
        element("parcelDetails").append(hint);
        drawLegend();
    }

    element("colorBy").addEventListener("change", () => {
        refreshStyles();
        drawLegend();
    });
    document.querySelectorAll('input[name="category"], #reviewOnly')
        .forEach((input) => input.addEventListener("change", applyFilters));
    element("fitParcels").addEventListener("click", () => {
        if (parcelLayer.getLayers().length) map.fitBounds(parcelLayer.getBounds(), { padding: [24, 24], maxZoom: 17 });
    });
    element("parcelSearch").addEventListener("submit", (event) => {
        event.preventDefault();
        const pin = element("pinSearch").value.trim().toUpperCase();
        const feature = features.find((item) => item.properties.pin === pin);
        if (!feature) {
            element("searchStatus").textContent = "That PIN is not in the candidate export.";
            return;
        }
        const layer = layersByPin.get(pin);
        select(feature, layer);
        if (!feature.geometry) {
            element("searchStatus").textContent = "Found in the export, but no usable boundary is available.";
        } else if (!layer) {
            element("searchStatus").textContent = "Found, but hidden by the current filters. Change the active filters to map it.";
        } else {
            element("searchStatus").textContent = "Parcel selected.";
            map.fitBounds(layer.getBounds(), { padding: [40, 40], maxZoom: 18 });
        }
    });

    async function load() {
        try {
            const response = await fetch("/api/parcels", { cache: "no-cache" });
            if (!response.ok) {
                const problem = await response.json();
                throw new Error(problem.detail || `Parcel request failed (HTTP ${response.status}).`);
            }
            const data = await response.json();
            if (data.type !== "FeatureCollection" || !Array.isArray(data.features)) {
                throw new Error("The export is not a GeoJSON FeatureCollection.");
            }
            for (const feature of data.features) {
                const p = feature.properties;
                if (!p || !Object.hasOwn(categories, p.category) || !Object.hasOwn(statuses, p.status) ||
                    typeof p.pin !== "string" ||
                    !["addresses", "taxRollYears", "presentUses", "lotSquareFeet", "accountNumbers", "inclusionReasons", "warnings"]
                        .every((key) => Array.isArray(p[key])) ||
                    (p.appraisedImprovementValue !== null && typeof p.appraisedImprovementValue !== "number") ||
                    (feature.geometry !== null && !["Polygon", "MultiPolygon"].includes(feature.geometry?.type))) {
                    throw new Error("A parcel has unsupported attributes or geometry. Regenerate the export.");
                }
            }
            features = data.features;
            const aerialGroups = data.aerialGroups ?? [];
            if (!Array.isArray(aerialGroups)) throw new Error("Aerial group metadata is invalid. Regenerate the export.");
            aerialGroupsByPin.clear();
            const exportedPins = new Set(features.map(feature => feature.properties.pin));
            for (const group of aerialGroups) {
                if (typeof group.Id !== "string" || !Array.isArray(group.ParcelPins) ||
                    group.ParcelCount !== group.ParcelPins.length || group.ParcelCount < 1 ||
                    !Array.isArray(group.Bounds) || group.Bounds.length !== 4 || !group.Bounds.every(Number.isFinite) ||
                    group.Bounds[0] >= group.Bounds[2] || group.Bounds[1] >= group.Bounds[3]) {
                    throw new Error("An aerial group has invalid members or crop bounds. Regenerate the export.");
                }
                for (const pin of group.ParcelPins) {
                    if (!exportedPins.has(pin) || aerialGroupsByPin.has(pin))
                        throw new Error("Aerial group membership conflicts with the parcel export. Regenerate the export.");
                    aerialGroupsByPin.set(pin, group);
                }
            }
            const missing = features.filter((feature) => feature.geometry === null).length;
            for (const count of document.querySelectorAll("[data-count]")) {
                count.textContent = number.format(features.filter((feature) => feature.properties.category === count.dataset.count).length);
            }
            const source = data.sources?.find((item) => item.file === "parcels.geojson");
            const date = source?.downloadedAtUtc ? new Date(source.downloadedAtUtc) : null;
            const bulk = data.sources?.find((item) => item.file === "assessor-bulk");
            element("sourceDate").textContent = bulk
                ? `Assessor bulk extract: ${bulk.dataExtracted}. Values compared through tax year ${data.assessmentDataBasis?.asOfTaxRollYear ?? "unknown"}. ` +
                    `GIS geometry/attribute cross-check retrieved ${date && !Number.isNaN(date.getTime()) ? date.toLocaleDateString("en-US") : "on an unverified date"}.`
                : date && !Number.isNaN(date.getTime())
                    ? `King County parcel snapshot: ${date.toLocaleDateString("en-US")}. Retrieval date, not assessment date.`
                    : "King County parcel snapshot: retrieval date unverified.";
            element("filters").disabled = false;
            applyFilters();
            element("errorMessage").hidden = true;
            element("loadStatus").textContent = `${number.format(features.length)} exported candidates. ${number.format(missing)} without usable geometry are not mapped.` +
                (aerialGroups.length ? ` ${number.format(aerialGroups.length)} aerial-photo groups.` : "");
            return true;
        } catch (error) {
            console.error("Unable to load parcel map:", error);
            element("filters").disabled = true;
            showError(error.message);
            return false;
        }
    }

    const reviewedPins = new Set();
    let exportTimer;
    let retryExportAction;

    async function readReviewResponse(response) {
        if (!response.ok) {
            let message = `Parcel review request failed (HTTP ${response.status}).`;
            if (response.headers.get("content-type")?.includes("json")) {
                const problem = await response.json();
                message = problem.detail || problem.title || message;
            }
            throw new Error(message);
        }
        return response.json();
    }

    function showReviewError(message) {
        element("reviewError").textContent = message;
        element("reviewError").hidden = false;
    }

    function showExportRetry(label, action) {
        element("retryExport").textContent = label;
        element("retryExport").hidden = false;
        element("retryExport").disabled = false;
        retryExportAction = action;
    }

    async function readReviewedPins() {
        const response = await fetch("/api/reviewed-exclusions", { cache: "no-store" });
        if (response.status === 404) return false;
        element("reviewEditor").hidden = false;
        const entries = await readReviewResponse(response);
        if (!Array.isArray(entries) || entries.some(entry => typeof entry?.pin !== "string"))
            throw new Error("The reviewed exclusions response is invalid. Check the local server logs.");
        reviewedPins.clear();
        for (const entry of entries) reviewedPins.add(entry.pin);
        return true;
    }

    async function watchExport(status = null, reloadMap = true) {
        clearTimeout(exportTimer);
        element("retryExport").hidden = true;
        element("reviewError").hidden = true;
        try {
            status ??= await readReviewResponse(await fetch("/api/reviewed-exclusions/export", { cache: "no-store" }));
            if (!["idle", "queued", "exporting", "publishing", "succeeded", "failed"].includes(status.state) ||
                typeof status.message !== "string")
                throw new Error("The export status is invalid. Check the local server logs.");
            const busy = ["queued", "exporting", "publishing"].includes(status.state);
            element("reviewFields").disabled = busy;
            element("reviewStatus").textContent = status.message;
            if (busy) {
                exportTimer = setTimeout(() => watchExport(), 1000);
            } else if (status.state === "failed") {
                showReviewError(status.error || "The export failed. Saved exclusions are retained; check the local server logs.");
                showExportRetry("Retry export", startExport);
            } else if (status.state === "succeeded") {
                if (!reloadMap) {
                    element("reviewStatus").textContent = "Saved exclusions have been exported.";
                } else if (await load()) {
                    element("reviewStatus").textContent = "Exclusions saved and exports regenerated. The map is up to date.";
                } else {
                    element("reviewStatus").textContent = "Exports regenerated, but the map could not be reloaded.";
                    showExportRetry("Reload map", () => watchExport(status));
                }
            } else {
                showExportRetry("Regenerate exports", startExport);
            }
        } catch (error) {
            console.error("Unable to read local export progress:", error);
            element("reviewFields").disabled = true;
            element("reviewStatus").textContent = "Could not confirm export status. Saved exclusions have not been removed.";
            showReviewError(error.message);
            showExportRetry("Check export status", checkExportStatus);
        }
    }

    async function checkExportStatus() {
        try {
            await readReviewedPins();
            await watchExport();
        } catch (error) {
            console.error("Unable to reload reviewed exclusions:", error);
            showReviewError(error.message);
        }
    }

    async function startExport() {
        element("reviewFields").disabled = true;
        element("retryExport").disabled = true;
        element("reviewError").hidden = true;
        element("reviewStatus").textContent = "Starting export using saved exclusions...";
        try {
            const response = await fetch("/api/reviewed-exclusions/export", {
                method: "POST", headers: { "Content-Type": "application/json" }, cache: "no-store"
            });
            await watchExport(await readReviewResponse(response));
        } catch (error) {
            console.error("Unable to start a local export:", error);
            element("reviewStatus").textContent = "Export start was not confirmed. Saved exclusions are retained.";
            showReviewError(error.message);
            showExportRetry("Check export status", checkExportStatus);
        }
    }

    element("retryExport").addEventListener("click", () => retryExportAction?.());

    async function loadReviewEditor() {
        try {
            if (!await readReviewedPins()) return;
            await watchExport(null, false);
        } catch (error) {
            console.error("Unable to load the local parcel review editor:", error);
            element("reviewEditor").hidden = false;
            showReviewError(error.message);
        }
    }

    element("reviewForm").addEventListener("submit", async (event) => {
        event.preventDefault();
        if (element("reviewFields").disabled) return;
        element("reviewError").hidden = true;
        element("reviewStatus").textContent = "";
        const pins = element("exclusionPins").value.trim().split(/[\s,]+/).filter(Boolean);
        const note = element("exclusionNote").value.trim();
        if (pins.length === 0 || pins.some(pin => !/^[0-9]{10}$/.test(pin))) {
            showReviewError("Enter parcel IDs containing exactly 10 digits, including leading zeros.");
            return;
        }
        if (new Set(pins).size !== pins.length) {
            showReviewError("A parcel ID was entered more than once. Each ID should appear only once.");
            return;
        }
        if (!note) {
            showReviewError("Enter a note explaining why these parcels should be excluded.");
            return;
        }
        const existing = pins.filter(pin => reviewedPins.has(pin));
        if (existing.length > 0) {
            showReviewError(`Already reviewed: ${existing.join(", ")}. Existing notes will not be overwritten.`);
            return;
        }
        element("reviewFields").disabled = true;
        element("retryExport").disabled = true;
        element("reviewStatus").textContent = "Saving reviewed exclusions...";
        try {
            const response = await fetch("/api/reviewed-exclusions", {
                method: "POST", headers: { "Content-Type": "application/json" }, cache: "no-store",
                body: JSON.stringify({ pins, note })
            });
            const result = await readReviewResponse(response);
            if (result.saved !== pins.length)
                throw new Error("The save response was incomplete. Check reviewed-exclusions.json before retrying.");
            for (const pin of pins) reviewedPins.add(pin);
            element("exclusionPins").value = "";
            element("exclusionNote").value = "";
            await watchExport(result);
        } catch (error) {
            console.error("Unable to save local parcel exclusions:", error);
            element("reviewStatus").textContent = "Exclusions were not confirmed saved.";
            showReviewError(error.message);
            element("reviewFields").disabled = false;
            showExportRetry("Check export status", checkExportStatus);
        }
    });

    load();
    loadReviewEditor();
})();
