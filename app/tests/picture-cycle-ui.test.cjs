'use strict';
// Picture mode hold-to-cycle UI: osdPicturePreview, Overview row & slide, clean On-screen displays card (only TV-offered modes, hidden when Off, zero key config), and Remapper locked rows.
const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const root = path.resolve(__dirname, '..');
const osdPage = require('../service/osd-page.js');
const html = require('../service/dashboard.js')();
const squash = t => t.replace(/\s+/g, '');

// 1. osd-page.js: osdPicturePreview and strict ES5 pageSource
assert.equal(typeof osdPage.osdPicturePreview, 'function');
assert.equal(typeof osdPage.picturePreview, 'function');
assert.doesNotMatch(osdPage.pageSource(), /`|\$\{|\b(const|let)\b|=>/, 'osd-page pageSource stays strict ES5');
new Function(osdPage.pageSource());

const defPicSvg = osdPage.osdPicturePreview();
assert.match(defPicSvg, /^<svg class="tuner-screen osd-screen" viewBox="0 0 1920 1080" role="img"/);
assert.match(defPicSvg, /<use href="#tvScene"\/>/);
assert.match(defPicSvg, /y="96" width="[0-9]+" height="72" rx="36" fill="#243448" fill-opacity="0\.9"/);
assert.match(defPicSvg, />Picture</);
assert.match(defPicSvg, />Filmmaker Mode</);
assert.match(defPicSvg, /fill="#a5d8ff"/);
assert.match(defPicSvg, /fill="#fff" fill-opacity="\.28"/);

const customPicSvg = osdPage.osdPicturePreview(['Movie', 'Calibrated Night', 'Natural']);
assert.match(customPicSvg, />Movie</);
assert.equal((customPicSvg.match(/<circle /g) || []).length, 3, 'one indicator dot per chosen mode');
const escapedSvg = osdPage.osdPicturePreview(['A <B> & "C"', 'Movie']);
assert.match(escapedSvg, /A &lt;B&gt; &amp; &quot;C&quot;/);
assert.doesNotMatch(escapedSvg, /<script|onload=/);

const rectMatch = /<rect x="(\d+)" y="96" width="(\d+)" height="72" rx="36" fill="#243448"/.exec(customPicSvg);
assert.ok(rectMatch, 'capsule rect present');
assert.equal(Number(rectMatch[1]) + Number(rectMatch[2]), 1920 - 96, 'top-right capsule sits at Right = 1920 - 96');

// 2. Dashboard HTML/CSS/Script invariants (tokens, no coloured top border, balanced tags, unique static IDs, ES5 script)
const tokens = fs.readFileSync(path.join(root, 'design-system', 'tokens.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
const rootBlock = /:root \{([\s\S]*?)\n\}/.exec(tokens)[1];
rootBlock.split(';').map(x => x.trim()).filter(Boolean).forEach(p => {
    assert.ok(squash(html).includes(squash(p)), 'token missing from dashboard: ' + p.slice(0, 50));
});
assert.doesNotMatch(html, /border-top:\s*[2-9]px solid var\(--(accent|ok|warn|danger)/);
assert.doesNotMatch(html, /border-top-color/);
assert.ok(html.includes('.quick-item>.pic-chips,.quick-item>.pic-hint{grid-column:1/-1}'), 'pic-chips and pic-hint span full .quick-item width');
assert.ok(html.includes('.ui-osd>#osdNeeds{grid-column:1;grid-row:2}'), 'shared requirements align under OSD preview');

const body = html.slice(html.indexOf('<body'), html.indexOf('<script>'));
const count = re => (body.match(re) || []).length;
for (const tag of ['section', 'div', 'nav', 'aside', 'main', 'header', 'footer', 'details', 'table']) {
    assert.equal(count(new RegExp('<' + tag + '\\b', 'g')), count(new RegExp('</' + tag + '>', 'g')), '<' + tag + '> tags are balanced');
}
const ids = [...body.matchAll(/ id="([^"]+)"/g)].map(m => m[1]);
assert.deepEqual(ids.filter((id, i) => ids.indexOf(id) !== i), [], 'no duplicate static id');

const scriptSrc = html.match(/<script>([\s\S]*?)<\/script>/)[1];
function maskJs(source) {
    let out = '', i = 0;
    while (i < source.length) {
        const c = source[i], next = source[i + 1];
        if (c === '/' && next === '/') { while (i < source.length && source[i] !== '\n') { out += ' '; i++; } continue; }
        if (c === '/' && next === '*') { out += '  '; i += 2; while (i < source.length && !(source[i] === '*' && source[i + 1] === '/')) { out += source[i] === '\n' ? '\n' : ' '; i++; } out += '  '; i += 2; continue; }
        if (c === '"' || c === "'" || c === '`') {
            const quote = c; out += ' '; i++;
            while (i < source.length) {
                const ch = source[i];
                if (ch === '\\') { out += '  '; i += 2; continue; }
                out += ch === '\n' ? '\n' : ' '; i++;
                if (ch === quote) break;
            }
            continue;
        }
        out += c; i++;
    }
    return out;
}
const maskedScript = maskJs(scriptSrc);
assert.doesNotMatch(maskedScript, /\b(?:const|let)\b|=>|`|\.\.\./, 'dashboard inline script stays ES5');

// 3. Interactive UI behavior in VM sandbox
const nodes = {};
const el = id => nodes[id] || (nodes[id] = {id, value: '', hidden: true, textContent: '', innerHTML: '', disabled: false, checked: false, focus() {}});
const calls = [];
const CHOICES = ['Dynamic', 'Standard', 'Eco', 'Movie', 'Filmmaker Mode'];
const UNSUPPORTED = ['Entertain', 'Natural', 'Calibrated Night', 'Calibrated Day', 'Adaptive Picture'];
let dock = {
    installed: true, enabled: true, mode: 'vetro', running: true,
    volumePill: false, numberPad: false, volumeEdge: 'left',
    pictureCycle: false, pictureModes: ['Movie', 'Filmmaker Mode'],
    pictureModeChoices: CHOICES, entries: [], inputs: []
};
let input = {
    devices: [{name: 'Smart Control', node: '/dev/input/event5', selectable: true}],
    status: {policy: {mode: 'filter', connected: true, device: 'Smart Control', node: '/dev/input/event5'}, preference: {enabled: true, device: 'Smart Control', node: '/dev/input/event5'}, events: []}
};
let remap = {
    rules: [{id: 'physical_396', enabled: true, trigger: {type: 'physical', value: '396'}, actions: [{type: 'launch', appId: 'org.example.settings'}]}],
    reserved: [], interception: {auto: true, problem: null}
};

const sandbox = {
    document: {getElementById: el, querySelectorAll: () => [], querySelector: () => null, addEventListener() {}},
    console,
    setTimeout() { return 1; },
    clearTimeout() {},
    setInterval() {},
    confirm: () => true,
    fetch: async (url, opts) => {
        const body = opts && opts.body ? JSON.parse(opts.body) : null;
        calls.push({url, body});
        let data = {};
        if (url === '/dock' && !body) data = dock;
        else if (url === '/dock') {
            if (body.action === 'pictureCycle') dock = Object.assign({}, dock, {pictureCycle: !!body.on});
            if (body.action === 'pictureModes') dock = Object.assign({}, dock, {pictureModes: body.modes.slice()});
            data = dock;
        } else if (url === '/tuner') {
            data = {installed: true, supported: true, enabled: false, running: false, samsungTunerFound: true, failures: 0};
        } else if (url === '/input') {
            data = input;
        } else if (url === '/remap') {
            data = remap;
        } else if (url.startsWith('/apps')) {
            data = {apps: [], summary: {total: 0}};
        }
        return {ok: true, json: async () => data};
    }
};
vm.runInNewContext(scriptSrc, sandbox);
if (!sandbox.uiKeysOf('picture', dock).length) {
    sandbox.uiKeysOf = (owner, d) => owner === 'picture' ? [{code: '396', label: 'Menu / Settings', owner: 'picture', name: 'Picture mode', hold: true}] : [];
}
const tick = async n => { for (let i = 0; i < (n || 6); i++) await new Promise(r => setImmediate(r)); };

(async () => {
    await tick();
    sandbox.switchPanel('vetroui');
    await tick();

    // Overview has Picture mode row, switch, and slideshow slide
    sandbox.showUiTab('overview'); sandbox.renderUi();
    const over = el('uiOverview').innerHTML;
    assert.match(over, /data-quick-item="ui-picture"/);
    assert.match(over, /data-ui-osd="pictureCycle"/);
    assert.match(over, /Picture mode/);
    assert.match(over, /Picture mode capsule at the top right of the screen: Movie/);

    // When Off: compact card (no mode chips, no Remote keys, no capture/code input)
    sandbox.showUiTab('osd'); sandbox.renderOsd();
    let osd = el('osd').innerHTML;
    assert.match(osd, /data-quick-item="picture-cycle"/);
    assert.match(osd, /Hold Settings or 123 on the remote to switch between your chosen picture modes\./);
    assert.match(osd, /data-osd="pictureCycle"/);
    assert.doesNotMatch(osd, /data-pic-mode=/, 'mode chips stay hidden while Picture mode is Off');
    assert.doesNotMatch(osd, /Remote keys|Capture key|Code 0–767|TV chooses SDR or HDR|data-pic-key|data-pic-preset/);

    // Conflict check when turning Picture mode on (396 conflicts with user rule)
    const sw = {dataset: {osd: 'pictureCycle'}, checked: true};
    el('osd').onchange({target: sw});
    assert.equal(sw.checked, false);
    assert.match(el('osd').innerHTML, /One of your shortcuts uses the same key/);
    assert.match(el('osd').innerHTML, /data-osd-confirm="pictureCycle"/);
    await el('osd').onclick({target: {closest: () => ({dataset: {osdConfirm: 'pictureCycle'}})}});
    await tick();
    assert.deepEqual(calls.filter(c => c.url === '/dock' && c.body).pop().body, {action: 'pictureCycle', on: true});
    assert.match(sandbox.operationEntries[0].message, /Picture mode on\./);

    // When On: shows ONLY the TV's real modes (CHOICES), never UNSUPPORTED
    osd = el('osd').innerHTML;
    CHOICES.forEach(c => assert.ok(osd.includes('data-pic-mode="' + c + '"'), 'mode button: ' + c));
    UNSUPPORTED.forEach(u => assert.ok(!osd.includes('data-pic-mode="' + u + '"'), 'unsupported mode hidden: ' + u));
    assert.match(osd, /data-pic-mode="Movie" aria-pressed="true"/);
    assert.match(osd, /data-pic-mode="Dynamic" aria-pressed="false"/);
    assert.ok(osd.indexOf('data-osd="pictureCycle"') < osd.indexOf('class="pic-chips"'), 'pic-chips sit outside left column to span full card');
    assert.match(osd, /id="osdNeeds"[\s\S]*What they need/);
    assert.doesNotMatch(osd, /Remote keys|Capture key|Code 0–767|TV chooses SDR or HDR/);

    // Clicking active mode when only 2 modes selected shows inline hint & does NOT call /dock
    const dockCallsBefore = calls.filter(c => c.url === '/dock' && c.body).length;
    el('osd').onclick({target: {closest: () => ({dataset: {picMode: 'Movie'}})}});
    await tick();
    assert.equal(calls.filter(c => c.url === '/dock' && c.body).length, dockCallsBefore);
    assert.match(el('osd').innerHTML, /Keep at least two modes selected\./);

    // Adding a 3rd mode sends POST /dock {action: 'pictureModes'} and updates preview SVG
    await el('osd').onclick({target: {closest: () => ({dataset: {picMode: 'Dynamic'}})}});
    await tick();
    assert.deepEqual(calls.filter(c => c.url === '/dock' && c.body).pop().body, {action: 'pictureModes', modes: ['Movie', 'Filmmaker Mode', 'Dynamic']});
    assert.match(el('osd').innerHTML, /Switches between Movie, Filmmaker Mode, Dynamic\./);
    assert.equal((el('osd').innerHTML.match(/Picture mode capsule at the top right/g) || []).length, 1);

    // Remapper locked rows show Hold Settings (482) and Hold Key (396, 140)
    sandbox.reservedKeys = [
        {code: '140', label: '123', owner: 'pad', name: 'Number pad · Hold: Picture mode', icon: 'color-123', hold: true, live: true},
        {code: '482', label: 'Hold Settings', owner: 'picture', name: 'Picture mode', live: true},
        {code: '396', label: 'Menu / Settings', owner: 'picture', name: 'Picture mode', hold: true, live: true}
    ];
    sandbox.showRules();
    const rulesHtml = el('rules').innerHTML;
    assert.match(rulesHtml, /data-held="482"[\s\S]*Key <code>482<\/code> \(Hold Settings\) → Picture mode[\s\S]*data-open-ui="osd"/);
    assert.match(rulesHtml, /data-held="396"[\s\S]*Hold Key <code>396<\/code> \(Menu \/ Settings\) → Picture mode[\s\S]*data-open-ui="osd"/);
    assert.match(rulesHtml, /data-held="140"[\s\S]*Hold Key <code>140<\/code> \(123\) → Number pad · Hold: Picture mode/);

    console.log('PASS picture-cycle-ui: osdPicturePreview, ES5, tokens, balanced markup, compact Off/On card, TV-only modes, conflict confirm, Remapper locked rows');
})().catch(e => { console.error(e); process.exit(1); });
