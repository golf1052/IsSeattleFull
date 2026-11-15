const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const script = fs.readFileSync(path.join(__dirname, "..", "wwwroot", "aerial.js"), "utf8");
const tick = () => new Promise(resolve => setImmediate(resolve));
const group = (id, address) => ({
    id, bounds: [-122.3, 47.6, -122.299, 47.601], displayAddress: address,
    additionalAddresses: [], lotAreaSquareFeet: 3400, lotAreaIsApproximate: false
});
const catalog = { version: 1, revision: "one", groups: [group("a", "First St"), group("b", "Second St"), group("c", "Third St")] };

function page(data = catalog, boundaryFetch = async () => ({ ok: true, json: async () => ({ type: "FeatureCollection", features: [] }) })) {
    const elements = new Map();
    function element(id) {
        if (!elements.has(id)) {
            const item = {
                events: {}, hidden: false, checked: id === "showParcels", disabled: false, value: "0",
                textContent: "", className: "", children: [], attributes: {},
                addEventListener(name, fn) { this.events[name] = fn; },
                setAttribute(name, value) { this.attributes[name] = value; },
                replaceChildren() { this.children = []; },
                append(item) { this.children.push(item); }
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
    const map = { fitBounds() {}, invalidateSize() {}, removeLayer(layer) { layers.delete(layer); } };
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
    const context = vm.createContext({
        L, Intl, AbortController, console: { error() {} },
        Math: Object.assign(Object.create(Math), { random: () => 0.5 }),
        ResizeObserver: class { observe() {} },
        setTimeout: (fn, delay) => { const id = ++sequence; timers.set(id, { fn, delay }); return id; },
        clearTimeout: id => timers.delete(id),
        document: { getElementById: element, createElement: () => element(`new-${++sequence}`) },
        fetch: async (url, options) => {
            requests.push(url);
            if (url === "/api/aerial-groups") return { ok: true, json: async () => data };
            return boundaryFetch(url, options);
        }
    });
    vm.runInContext(script, context);
    return {
        element, requests, images, boundaryLayers, layers,
        move(index) {
            element("place").value = String(index);
            element("place").events.input();
            for (const [id, timer] of timers) if (timer.delay === 150) { timers.delete(id); timer.fn(); }
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
    const single = page({ ...catalog, groups: [catalog.groups[0]] });
    await tick();
    assert.equal(single.element("place").disabled, true);
    assert.equal(single.element("place").max, "0");
});

test("a stale boundary revision retries by reloading the catalog", async () => {
    const p = page(catalog, async () => ({ ok: false, status: 409 }));
    await tick();
    assert.match(p.element("imageStatus").textContent, /catalog changed/);
    p.element("retry").events.click();
    await tick();
    assert.equal(p.requests.filter(url => url === "/api/aerial-groups").length, 2);
});
