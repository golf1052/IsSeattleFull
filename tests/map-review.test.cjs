const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const script = fs.readFileSync(path.join(__dirname, "..", "wwwroot", "map.js"), "utf8");
const tick = () => new Promise(resolve => setImmediate(resolve));
const response = (body, status = 200) => ({
    ok: status >= 200 && status < 300, status,
    headers: { get: () => "application/json" }, json: async () => body
});
const idle = { state: "idle", message: "Ready", saved: 0 };
const queued = saved => ({ state: "queued", message: "Exclusions saved. Waiting to regenerate exports.", saved });
const succeeded = { state: "succeeded", message: "Exports regenerated.", saved: 0 };

function page(initial = response([]), save = async ({ pins }) => response(queued(pins.length), 202)) {
    const elements = new Map();
    const element = id => {
        if (!elements.has(id)) elements.set(id, {
            hidden: ["reviewEditor", "reviewError", "retryExport"].includes(id), disabled: id === "reviewFields",
            value: id === "colorBy" ? "category" : "", textContent: "", className: "", style: {},
            children: [], events: {},
            addEventListener(name, fn) { this.events[name] = fn; },
            replaceChildren() { this.children = []; },
            append(...items) { this.children.push(...items); },
            setAttribute() {}
        });
        return elements.get(id);
    };
    const layer = () => ({ addTo() { return this; }, on() {}, getLayers: () => [] });
    const map = {
        viewChanges: 0, setView() { this.viewChanges++; return this; }, fitBounds() { this.viewChanges++; },
        createPane() {}, getPane: () => ({ style: {} }), removeLayer() {}
    };
    const requests = [];
    const timers = new Map();
    let timerId = 0;
    let exportStatus = response(idle);
    let mapResponse = response({ type: "FeatureCollection", features: [] });
    vm.runInNewContext(script, {
        L: { map: () => map, tileLayer: layer, canvas: () => ({}), geoJSON: layer },
        Intl, console: { error() {} },
        setTimeout: fn => { timers.set(++timerId, fn); return timerId; },
        clearTimeout: id => timers.delete(id),
        document: { getElementById: element, querySelectorAll: () => [], createElement: () => element(`new-${elements.size}`) },
        fetch: async (url, options) => {
            requests.push({ url, options });
            if (url === "/api/parcels") return mapResponse;
            if (url === "/api/reviewed-exclusions/export") {
                if (options?.method === "POST") { exportStatus = response(queued(0), 202); return exportStatus; }
                return exportStatus;
            }
            if (options?.method === "POST") return save(JSON.parse(options.body));
            return initial;
        }
    });
    return {
        element, requests, map,
        setExportStatus(value) { exportStatus = value; },
        setMapResponse(value) { mapResponse = value; },
        async poll() {
            const callbacks = [...timers.values()];
            timers.clear();
            for (const callback of callbacks) await callback();
        },
        async submit(pins, note) {
            element("exclusionPins").value = pins;
            element("exclusionNote").value = note;
            await element("reviewForm").events.submit({ preventDefault() {} });
        }
    };
}

test("public map views keep the editor hidden and never send review writes", async () => {
    const p = page(response(null, 404));
    await tick();
    assert.equal(p.element("reviewEditor").hidden, true);
    assert.equal(p.element("reviewFields").disabled, true);
    assert.equal(p.requests.filter(request => request.options?.method === "POST").length, 0);
});

test("the local form saves a batch, tracks the export, and reloads the map only after success", async () => {
    const p = page();
    await tick();
    assert.equal(p.element("reviewEditor").hidden, false);
    assert.equal(p.element("reviewFields").disabled, false);
    await p.submit(" 0000000001,\n0000000002  0000000003 ", "  Same occupied site  ");
    const writes = p.requests.filter(request => request.options?.method === "POST");
    assert.equal(writes.length, 1);
    assert.deepEqual(JSON.parse(writes[0].options.body), {
        pins: ["0000000001", "0000000002", "0000000003"], note: "Same occupied site"
    });
    assert.equal(p.element("exclusionPins").value, "");
    assert.equal(p.element("exclusionNote").value, "");
    assert.match(p.element("reviewStatus").textContent, /Waiting to regenerate/);
    assert.equal(p.element("reviewFields").disabled, true);
    assert.equal(p.requests.filter(request => request.url === "/api/parcels").length, 1);
    p.setExportStatus(response({ state: "exporting", message: "Building aerial catalog...", saved: 3 }));
    await p.poll();
    assert.match(p.element("reviewStatus").textContent, /Building aerial/);
    assert.equal(p.element("reviewFields").disabled, true);
    p.setExportStatus(response(succeeded));
    await p.poll();
    assert.equal(p.requests.filter(request => request.url === "/api/parcels").length, 2);
    assert.match(p.element("reviewStatus").textContent, /map is up to date/);
    assert.equal(p.element("reviewFields").disabled, false);
    assert.equal(p.map.viewChanges, 1);
});

test("invalid IDs, repeated IDs, and missing notes do not submit a partial batch", async () => {
    const p = page();
    await tick();
    for (const [pins, note] of [["123", "Reason"], ["0000000001,0000000001", "Reason"], ["0000000001", " "]]) {
        await p.submit(pins, note);
        assert.equal(p.element("reviewError").hidden, false);
        assert.equal(p.element("exclusionPins").value, pins);
    }
    assert.equal(p.requests.filter(request => request.options?.method === "POST").length, 0);
});

test("existing exclusions cannot silently receive replacement notes", async () => {
    const p = page(response([{ pin: "0000000001" }]));
    await tick();
    await p.submit("0000000002, 0000000001", "Replacement");
    assert.match(p.element("reviewError").textContent, /Already reviewed/);
    assert.equal(p.requests.filter(request => request.options?.method === "POST").length, 0);
});

test("save failures preserve the entered IDs and note for correction", async () => {
    const p = page(response([]), async () => response({ detail: "Exclusions changed; retry." }, 409));
    await tick();
    await p.submit("0000000001", "Reason");
    assert.equal(p.element("exclusionPins").value, "0000000001");
    assert.equal(p.element("exclusionNote").value, "Reason");
    assert.equal(p.element("reviewFields").disabled, false);
    assert.match(p.element("reviewError").textContent, /Exclusions changed/);
});

test("failed exports can be retried without saving the same exclusions again", async () => {
    const p = page();
    await tick();
    await p.submit("0000000001", "Reason");
    p.setExportStatus(response({ state: "failed", message: "Exclusions saved, but exports failed.", error: "Missing cache." }));
    await p.poll();
    assert.match(p.element("reviewStatus").textContent, /Exclusions saved, but exports failed/);
    assert.match(p.element("reviewError").textContent, /Missing cache/);
    assert.equal(p.element("retryExport").textContent, "Retry export");
    await p.element("retryExport").events.click();
    assert.equal(p.element("reviewFields").disabled, true);
    p.setExportStatus(response(succeeded));
    await p.poll();
    assert.equal(p.requests.filter(request => request.url === "/api/reviewed-exclusions" && request.options?.method === "POST").length, 1);
    assert.equal(p.requests.filter(request => request.url === "/api/reviewed-exclusions/export" && request.options?.method === "POST").length, 1);
    assert.equal(p.element("retryExport").hidden, true);
});

test("lost export status offers a status check rather than submitting another job", async () => {
    const p = page();
    await tick();
    await p.submit("0000000001", "Reason");
    p.setExportStatus(response({ detail: "Server unavailable" }, 503));
    await p.poll();
    assert.equal(p.element("reviewFields").disabled, true);
    assert.equal(p.element("retryExport").textContent, "Check export status");
    p.setExportStatus(response(succeeded));
    await p.element("retryExport").events.click();
    assert.equal(p.requests.filter(request => request.options?.method === "POST").length, 1);
    assert.equal(p.element("reviewFields").disabled, false);
});

test("reopening the map resumes monitoring a running export", async () => {
    const p = page();
    p.setExportStatus(response({ state: "exporting", message: "Preparing aerial catalog..." }));
    await tick();
    assert.equal(p.element("reviewFields").disabled, true);
    assert.match(p.element("reviewStatus").textContent, /Preparing aerial/);
    p.setExportStatus(response(succeeded));
    await p.poll();
    assert.equal(p.element("reviewFields").disabled, false);
    assert.equal(p.requests.filter(request => request.options?.method === "POST").length, 0);
    assert.equal(p.requests.filter(request => request.url === "/api/parcels").length, 2);
});

test("a map reload failure retries reading the map without rebuilding the exports", async () => {
    const p = page();
    await tick();
    await p.submit("0000000001", "Reason");
    p.setExportStatus(response(succeeded));
    p.setMapResponse(response({ detail: "Map temporarily unavailable" }, 503));
    await p.poll();
    assert.match(p.element("reviewStatus").textContent, /Exports regenerated, but the map/);
    assert.equal(p.element("retryExport").textContent, "Reload map");
    p.setMapResponse(response({ type: "FeatureCollection", features: [] }));
    await p.element("retryExport").events.click();
    assert.match(p.element("reviewStatus").textContent, /map is up to date/);
    assert.equal(p.requests.filter(request => request.options?.method === "POST").length, 1);
    assert.equal(p.element("errorMessage").hidden, true);
});

test("an unreadable review file is reported instead of enabling an empty editor", async () => {
    const p = page(response({ detail: "Check reviewed-exclusions.json." }, 503));
    await tick();
    assert.equal(p.element("reviewEditor").hidden, false);
    assert.equal(p.element("reviewFields").disabled, true);
    assert.match(p.element("reviewError").textContent, /Check reviewed-exclusions/);
});
