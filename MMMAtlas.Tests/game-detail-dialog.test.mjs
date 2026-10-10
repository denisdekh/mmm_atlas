import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = (await readFile(new URL(
    "../MMMAtlas/wwwroot/js/game-detail-dialog.js", import.meta.url), "utf8"))
    .replace(/^import .*;\r?\n/gm, "") + "\nexport { renderGame };";

class Element {
    constructor(tag) {
        this.tagName = tag;
        this.children = [];
        this.attributes = {};
        this.dataset = {};
        this.style = {};
        this.classList = { add() {} };
    }
    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.children = children; }
    setAttribute(name, value) { this.attributes[name] = value; }
}

const stubDependencies = `
const initializeDashboardGameImage = () => {};
const createAxisTooltip = () => document.createElement("span");
`;
const { renderGame } = await import(
    `data:text/javascript;base64,${Buffer.from(stubDependencies + source).toString("base64")}`);

test("community header contains the count then vote button, without a separate actions row", (t) => {
    globalThis.document = {
        createElement: (tag) => new Element(tag),
        body: { dataset: { authenticated: "true" } }
    };
    t.after(() => { delete globalThis.document; });
    const content = new Element("div");
    const game = { gameId: 2, name: "Game", voteCount: 5,
        microRatio: 0.5, mesoRatio: 0.3, macroRatio: 0.2 };
    renderGame(content, game, { chunks: new Map(), totalCount: 0 }, {
        ownedGame: { hasVoted: false }, dataCollectionEnabled: true
    });
    const score = content.children[1];
    assert.equal(score.children.length, 2);
    const [header, chart] = score.children;
    assert.equal(header.children[0].textContent, "Community score");
    assert.equal(header.children[1].textContent, "5 votes");
    assert.equal(header.children[2].className, "game-detail-voting");
    assert.equal(header.children[2].children[0].textContent, "Submit vote");
    assert.equal(chart.children[0].children[1].children[0].style.width, "100%");
    assert.equal(chart.children[1].children[1].children[0].style.width, "60%");
    assert.equal(chart.children[2].children[1].children[0].style.width, "40%");
    renderGame(content, game, { chunks: new Map(), totalCount: 0 }, {
        ownedGame: null, dataCollectionEnabled: false
    });
    const disabled = content.children[1].children[0].children[2].children[0];
    assert.equal(disabled.textContent, "Ownership data not available");
    assert.equal(disabled.disabled, true);
});
