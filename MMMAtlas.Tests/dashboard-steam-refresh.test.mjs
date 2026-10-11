import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = await readFile(new URL("../MMMAtlas/wwwroot/js/dashboard-steam-refresh.js", import.meta.url), "utf8");

function setup(fetchResponse) {
    const button = { disabled: false, textContent: "Refresh Steam library" };
    const form = { action: "/api/me/steam-data/refresh", querySelector: () => button,
        addEventListener: (_, handler) => { form.submit = () => handler({ preventDefault() {} }); } };
    const region = { replaceChildren: (toast) => { region.message = toast.textContent; } };
    let reloads = 0, requests = 0;
    const document = {
        querySelector: (selector) => selector === "[data-steam-refresh]" ? form : region,
        createElement: () => ({ remove() {} })
    };
    const window = { setTimeout() {}, location: { reload: () => { reloads++; } } };
    const fetch = async (url, options) => {
        requests++;
        assert.equal(url, form.action);
        assert.equal(options.method, "POST");
        assert.equal(options.body.form, form);
        return await fetchResponse();
    };
    new Function("document", "window", "fetch", "FormData", source)(document, window, fetch,
        class { constructor(form) { this.form = form; } });
    return { form, button, region, reloads: () => reloads, requests: () => requests };
}

test("rate limited refresh shows a toast and restores the button without reloading", async () => {
    const ui = setup(async () => ({ status: 429, ok: false }));
    await ui.form.submit();
    assert.match(ui.region.message, /twice every 30 minutes/);
    assert.equal(ui.reloads(), 0);
    assert.equal(ui.button.disabled, false);
    assert.equal(ui.button.textContent, "Refresh Steam library");
});

test("successful refresh reloads the dashboard and prevents duplicate submissions", async () => {
    let resolve;
    const pending = new Promise(done => { resolve = done; });
    const ui = setup(() => pending);
    const submitted = ui.form.submit();
    assert.equal(ui.button.disabled, true);
    await ui.form.submit();
    assert.equal(ui.requests(), 1);
    resolve({ ok: true, json: async () => ({ libraryStatus: "Available" }) });
    await submitted;
    assert.equal(ui.reloads(), 1);
});

test("unavailable Steam data or a network failure shows an error and allows retry", async () => {
    for (const response of [async () => ({ ok: true, json: async () => ({ libraryStatus: "Unavailable" }) }),
        async () => { throw new Error("offline"); }]) {
        const ui = setup(response);
        await ui.form.submit();
        assert.match(ui.region.message, /try again later/i);
        assert.equal(ui.reloads(), 0);
        assert.equal(ui.button.disabled, false);
    }
});
