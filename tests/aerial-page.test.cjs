const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const script = fs.readFileSync(path.join(__dirname, "..", "wwwroot", "aerial.js"), "utf8");
const tick = () => new Promise(resolve => setImmediate(resolve));
const group = (id, address, parcelPins = [`${id}-parcel`]) => ({
    id, bounds: [-122.3, 47.6, -122.299, 47.601], displayAddress: address,
    parcelPins, lotAreaSquareFeet: 3400, lotAreaIsApproximate: false
});
const catalog = { version: 2, revision: "one", groups: [group("a", "First St"), group("b", "Second St"), group("c", "Third St")] };

function page(data = catalog, boundaryFetch = async () => ({ ok: true, json: async () => ({ type: "FeatureCollection", features: [] }) })) {
    const elements = new Map();
    function element(id) {
        if (!elements.has(id)) {
            const item = {
                events: {}, hidden: false, checked: id === "showParcels", disabled: false, value: "0",
                textContent: "", className: "", children: [], attributes: {},
                addEventListener(name, fn) { this.events[name] = fn; },
                focus(options) { document.activeElement = this; this.focusOptions = options; },
                setAttribute(name, value) { this.attributes[name] = value; },
                replaceChildren() { this.children = []; },
                append(...items) { this.children.push(...items); }
            };
            item.classList = { contains: value => item.className.split(" ").includes(value) };
            elements.set(id, item);
        }
        return elements.get(id);
    }
    const layers = new Set();
    const images = [];
    const boundaryLayers = [];
    const timers = new Map();
    let sequence = 0;
    let resize;
    const map = {
        events: {}, on(name, fn) { this.events[name] = fn; },
        zoom: 18.75, getZoom() { return this.zoom; },
        fitBounds() {}, invalidateSize() {}, removeLayer(layer) { layers.delete(layer); }
    };
    const layer = () => ({
        handlers: {}, on(name, fn) { this.handlers[name] = fn; },
        addTo() { layers.add(this); return this; }
    });
    const L = {
        map: () => map,
        latLngBounds: () => ({ pad() { return this; } }),
        tileLayer: () => { const item = layer(); images.push(item); return item; },
        geoJSON: data => { const item = layer(); item.data = data; boundaryLayers.push(item); return item; }
    };
    const requests = [];
    const random = { value: 0.5 };
    const document = {
        activeElement: null, getElementById: element, createElement: () => element(`new-${++sequence}`)
    };
    const context = vm.createContext({
        L, Intl, AbortController, URLSearchParams, console: { error() {} },
        Math: Object.assign(Object.create(Math), { random: () => random.value }),
        ResizeObserver: class { constructor(fn) { resize = fn; } observe() {} },
        setTimeout: (fn, delay) => { const id = ++sequence; timers.set(id, { fn, delay }); return id; },
        clearTimeout: id => timers.delete(id),
        document,
        fetch: async (url, options) => {
            requests.push(url);
            if (url === "/api/aerial-groups") return { ok: true, json: async () => data };
            return boundaryFetch(url, options);
        }
    });
    vm.runInContext(script, context);
    function flushSlider() {
        for (const [id, timer] of timers) if (timer.delay === 150) { timers.delete(id); timer.fn(); }
    }
    return {
        element, requests, images, boundaryLayers, layers, random, flushSlider,
        get focusedElement() { return document.activeElement; },
        clickMap() { map.events.click(); },
        resize(zoom) { map.zoom = zoom; resize(); },
        move(index) {
            element("place").value = String(index);
            element("place").events.input();
            flushSlider();
        },
        finishImage(image = images.at(-1)) { image.handlers.tileload(); image.handlers.load(); }
    };
}

test("random start spans groups; slider reaches endpoints and retains Show Parcels state", async () => {
    const p = page();
    await tick();
    assert.equal(p.element("address").textContent, "Second St");
    assert.equal(p.element("place").max, "2");
    p.finishImage();
    assert.equal(p.element("imageMessage").hidden, true);
    p.move(0); await tick();
    assert.equal(p.element("address").textContent, "First St");
    p.element("showParcels").checked = false;
    p.element("showParcels").events.change(); await tick();
    const requestCount = p.requests.length;
    p.move(2); await tick();
    assert.equal(p.element("address").textContent, "Third St");
    assert.equal(p.element("showParcels").checked, false);
    assert.equal(p.requests.length, requestCount);
    assert.equal(p.element("lotArea").textContent, "3,400 sq ft");
});

test("Refresh always selects a different random group without reloading the catalog or changing outline preferences", async () => {
    const p = page();
    await tick();
    assert.equal(p.element("refresh").disabled, false);
    p.element("showParcels").checked = false;
    p.element("showParcels").events.change();
    const requestCount = p.requests.length;
    for (const index of [0, 1, 2]) {
        for (const draw of [0, 0.5, 0.999999]) {
            p.move(index); await tick();
            p.random.value = draw;
            p.element("refresh").events.click(); await tick();
            const selected = Number(p.element("place").value);
            assert.notEqual(selected, index);
            assert.ok(selected >= 0 && selected < catalog.groups.length);
            assert.equal(p.element("address").textContent, catalog.groups[selected].displayAddress);
            assert.equal(p.element("showParcels").checked, false);
        }
    }
    assert.equal(p.requests.length, requestCount);
});

test("Refresh cancels a pending slider update and works with two available groups", async () => {
    const p = page({ ...catalog, groups: catalog.groups.slice(0, 2) });
    await tick();
    assert.equal(p.element("place").value, "1");
    p.element("place").value = "1";
    p.element("place").events.input();
    p.element("refresh").events.click(); await tick();
    p.flushSlider(); await tick();
    assert.equal(p.element("place").value, "0");
    assert.equal(p.element("address").textContent, "First St");
    p.element("refresh").events.click(); await tick();
    assert.equal(p.element("place").value, "1");
    assert.equal(p.element("address").textContent, "Second St");
});

test("clicking the aerial map focuses the slider without scrolling or changing the selection", async () => {
    const p = page();
    await tick();
    const requestCount = p.requests.length;
    p.clickMap();
    assert.equal(p.focusedElement, p.element("place"));
    assert.equal(p.element("place").focusOptions.preventScroll, true);
    assert.equal(p.element("place").value, "1");
    assert.equal(p.element("address").textContent, "Second St");
    assert.equal(p.requests.length, requestCount);
    p.move(2); await tick();
    assert.equal(p.focusedElement, p.element("place"));
    assert.equal(p.element("address").textContent, "Third St");
});

test("Google Maps links use the selected group's geometry and fitted satellite zoom", async () => {
    const p = page({ ...catalog, groups: [
        { ...group("a", null), bounds: [-122.4, 47.5, -122.39, 47.51] },
        group("b", "Near Second St")
    ] });
    await tick();
    const link = p.element("googleMaps");
    assert.equal(link.hidden, false);
    let url = new URL(link.href);
    assert.equal(url.origin, "https://www.google.com");
    assert.equal(url.pathname, "/maps/@");
    assert.equal(url.searchParams.get("api"), "1");
    assert.equal(url.searchParams.get("map_action"), "map");
    assert.equal(url.searchParams.get("basemap"), "satellite");
    assert.equal(url.searchParams.get("center"), "47.6005,-122.2995");
    assert.equal(url.searchParams.get("zoom"), "18");
    p.move(0); await tick();
    url = new URL(link.href);
    const [latitude, longitude] = url.searchParams.get("center").split(",").map(Number);
    assert.ok(Math.abs(latitude - 47.505) < 1e-10);
    assert.ok(Math.abs(longitude + 122.395) < 1e-10);
    assert.equal(link.hidden, false);
    assert.equal(p.element("address").textContent, "Address unavailable");
    p.resize(16.5);
    assert.equal(new URL(link.href).searchParams.get("zoom"), "16");
});

test("parcel IDs use period separators and keep correct Assessor links when changing views", async () => {
    const p = page({ ...catalog, groups: [
        group("a", "First St", ["0000000003"]),
        group("b", "Second St", ["0000000001", "0000000002"])
    ] });
    await tick();
    assert.equal(p.element("parcelIds").hidden, false);
    assert.equal(p.element("parcelIds").open, false);
    const labels = () => p.element("parcelIdList").children.map(item => item.children[0]);
    assert.deepEqual(labels().map(label => label.textContent), ["0000000001", "0000000002"]);
    assert.deepEqual(p.element("parcelIdList").children.map(item => item.children[1]), [". ", ". "]);
    const links = () => p.element("parcelIdList").children.map(item => item.children.at(-1));
    assert.deepEqual(links().map(link => new URL(link.href).searchParams.get("ParcelNbr")), ["0000000001", "0000000002"]);
    for (const link of links()) {
        const url = new URL(link.href);
        assert.equal(url.origin, "https://blue.kingcounty.com");
        assert.equal(url.pathname, "/Assessor/eRealProperty/Dashboard.aspx");
        assert.equal(link.textContent, "View on Assessor");
        assert.equal(link.target, "_blank");
        assert.equal(link.rel, "noopener noreferrer");
        assert.ok(link.attributes["aria-label"].includes(url.searchParams.get("ParcelNbr")));
    }
    p.element("parcelIds").open = true;
    p.element("showParcels").checked = false;
    p.element("showParcels").events.change(); await tick();
    const requestCount = p.requests.length;
    p.move(0); await tick();
    assert.deepEqual(labels().map(label => label.textContent), ["0000000003"]);
    assert.equal(p.element("parcelIdList").children[0].children[1], ". ");
    assert.deepEqual(links().map(link => new URL(link.href).searchParams.get("ParcelNbr")), ["0000000003"]);
    assert.equal(p.element("parcelIds").open, false);
    assert.equal(p.requests.length, requestCount);
});

test("missing or invalid parcel IDs fail explicitly instead of displaying an incomplete list", async () => {
    for (const parcelPins of [undefined, [], [123], [""], ["one", "one"]]) {
        const p = page({ ...catalog, groups: [{ ...catalog.groups[0], parcelPins }] });
        await tick();
        assert.match(p.element("imageStatus").textContent, /invalid attributes/);
        assert.equal(p.element("controls").disabled, true);
        assert.equal(p.element("parcelIds").hidden, true);
        assert.deepEqual(p.element("parcelIdList").children, []);
    }
});

test("stale image and outline loads cannot replace a newer selection", async () => {
    const pending = [];
    const p = page(catalog, () => new Promise(resolve => pending.push(resolve)));
    await tick();
    const oldImage = p.images[0];
    p.move(2); await tick();
    pending[0]({ ok: true, json: async () => ({ type: "FeatureCollection", features: [] }) });
    await tick();
    assert.equal(p.boundaryLayers.length, 0);
    p.finishImage(oldImage);
    assert.equal(p.element("imageMessage").hidden, false);
    pending[1]({ ok: true, json: async () => ({ type: "FeatureCollection", features: [] }) });
    await tick();
    assert.equal(p.boundaryLayers.length, 1);
    p.finishImage();
    assert.equal(p.element("imageMessage").hidden, true);
    assert.equal(p.element("address").textContent, "Third St");
});

test("imagery failure leaves current address and retry available", async () => {
    const p = page();
    await tick();
    p.images[0].handlers.tileerror();
    p.finishImage();
    assert.equal(p.element("imageMessage").hidden, false);
    assert.equal(p.element("retry").hidden, false);
    assert.equal(p.element("address").textContent, "Second St");
});

test("empty catalogs fail explicitly; single groups disable the range", async () => {
    const empty = page({ ...catalog, groups: [] });
    await tick();
    assert.match(empty.element("imageStatus").textContent, /No aerial places/);
    assert.equal(empty.element("controls").disabled, true);
    assert.equal(empty.element("googleMaps").hidden, true);
    const single = page({ ...catalog, groups: [catalog.groups[0]] });
    await tick();
    assert.equal(single.element("place").disabled, true);
    assert.equal(single.element("place").max, "0");
    assert.equal(single.element("refresh").disabled, true);
    const imageCount = single.images.length;
    single.element("refresh").events.click();
    assert.equal(single.images.length, imageCount);
});

test("a stale boundary revision retries by reloading the catalog", async () => {
    const p = page(catalog, async () => ({ ok: false, status: 409 }));
    await tick();
    assert.match(p.element("imageStatus").textContent, /catalog changed/);
    p.element("retry").events.click();
    await tick();
    assert.equal(p.requests.filter(url => url === "/api/aerial-groups").length, 2);
});
