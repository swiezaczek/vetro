'use strict';
const assert = require('assert').strict;
const EventEmitter = require('events');
const fs = require('fs');
const os = require('os');
const path = require('path');

const dockModule = require('../service/dock.js');
const {Dock, PICTURE_MODES, DEFAULT_PICTURE_MODES, FALLBACK_PICTURE_MODES, parsePictureModesGroup, parsePictureModesBusctl, effectivePictureModes} = dockModule;
const launcherPlugin = require('../service/plugin-launcher.js');
const kr = require('../service/key-routing.js');
const vk = require('../service/volume-keys.js');
const ui = require('../service/vetro-ui.js');
const backup = require('../service/settings-backup.js');
const report = require('../service/diagnostic-report.js');
const loadService = require('./service-harness.cjs');

function tmpDir(prefix) { return fs.mkdtempSync(path.join(os.tmpdir(), prefix)); }

function groupReply(activeIds) {
    const candidates = [65536, 393224, 65541, 65537, 393216, 458753, 458756, 65552, 393227, 458775, 458776, 65542, 65538, 393217, 65539, 65540, 393218, 393219, 589824, 589825, 655360, 655361];
    const set = new Set(activeIds);
    return '   array [\n' + candidates.map(id => '      int32 ' + (set.has(id) ? 1 : 0)).join('\n') + '\n   ]\n   int32 0\n';
}

function busctlReply(activeIds) {
    const candidates = [65536, 393224, 65541, 65537, 393216, 458753, 458756, 65552, 393227, 458775, 458776, 65542, 65538, 393217, 65539, 65540, 393218, 393219, 589824, 589825, 655360, 655361];
    const set = new Set(activeIds);
    return candidates.map(id => 'i ' + (set.has(id) ? 1 : 2)).join('\n') + '\n';
}

(async () => {
    // 1. Pure D-Bus parsers & effectivePictureModes
    assert.deepEqual(parsePictureModesGroup(groupReply([65536, 65537, 65552, 65538, 655360])), ['Dynamic', 'Standard', 'Eco', 'Movie', 'Filmmaker Mode']);
    assert.equal(parsePictureModesGroup('   array [\n      int32 1\n   ]\n   int32 -1\n'), null);
    assert.equal(parsePictureModesGroup('   array [\n      int32 1\n   ]\n   int32 0\n'), null);
    assert.deepEqual(parsePictureModesBusctl(busctlReply([65536, 65537, 65542, 65538])), ['Dynamic', 'Standard', 'Natural', 'Movie']);
    assert.equal(parsePictureModesBusctl('i 1\ni 2\n'), null);
    assert.deepEqual(effectivePictureModes(['Movie', 'Filmmaker Mode'], ['Dynamic', 'Standard', 'Natural', 'Movie']), ['Movie', 'Dynamic']);

    // 2. Dock state, defaults, probing, validation, file writing, startScript, launch (Vetro + custom)
    const dir = tmpDir('vetro-pic-dock-');
    try {
        fs.mkdirSync(path.join(dir, 'res'), {recursive: true});
        fs.writeFileSync(path.join(dir, 'tvlauncher-dotnet.dll'), 'dll');
        fs.writeFileSync(path.join(dir, 'res', 'glass_tile.png'), 'png');
        const stateFile = path.join(dir, 'vetro-dock.json');
        const appsFile = path.join(dir, 'vetro-dock-apps.tsv');
        fs.writeFileSync(appsFile, 'app1\tApp 1\torg.example.app\n');
        fs.writeFileSync(stateFile, JSON.stringify({enabled: true, since: 1700000000}));

        assert.deepEqual(DEFAULT_PICTURE_MODES, ['Movie', 'Filmmaker Mode']);
        assert.deepEqual(FALLBACK_PICTURE_MODES, ['Dynamic', 'Standard', 'Movie', 'Filmmaker Mode']);

        let keyNotified = 0, refreshes = 0, probeMode = 'group';
        const dock = new Dock({
            execute: async cmd => {
                if (cmd.includes('CheckPictureModeByGroup')) {
                    if (probeMode === 'group') return {exit_code: 0, stdout: groupReply([65536, 65537, 65552, 65538, 655360])};
                    if (probeMode === 'fallback') return {exit_code: 1, stdout: ''};
                    if (probeMode === 'locked') return {exit_code: 0, stdout: groupReply([])};
                }
                if (cmd.includes('CheckPictureModeByMode')) {
                    if (probeMode === 'fallback') return {exit_code: 0, stdout: busctlReply([65536, 65537, 65542, 65538])};
                    return {exit_code: 0, stdout: busctlReply([])};
                }
                return {exit_code: 0, stdout: 'dock=\nhome=0\nlog:\n'};
            },
            dir,
            stateFile,
            appsFile,
            customAppsFile: path.join(dir, 'vetro-dock-custom.tsv'),
            inputsFile: path.join(dir, 'vetro-dock-inputs.tsv'),
            allAppsFile: path.join(dir, 'vetro-dock-allapps.tsv'),
            onVolumeChange: () => { keyNotified++; }
        });
        dock.refresh = async () => { refreshes++; return {}; };

        // Backward compat with old state file (before probing: fallback 4 core modes)
        const oldState = dock.readState();
        assert.equal(oldState.pictureCycle, false);
        assert.deepEqual(oldState.pictureModes, ['Movie', 'Filmmaker Mode']);
        assert.equal('pictureKeys' in oldState, false, 'no configurable pictureKeys in state');
        assert.equal(dock.pictureModesFile, path.join(dir, 'vetro-dock-picture-modes.txt'));
        assert.match(dock.startScript(), /--picture-modes-file '[^']*vetro-dock-picture-modes\.txt'/);

        const st0 = await dock.statusNow();
        assert.equal(st0.pictureCycle, false);
        assert.deepEqual(st0.pictureModes, ['Movie', 'Filmmaker Mode']);
        assert.deepEqual(st0.pictureModeChoices, FALLBACK_PICTURE_MODES);

        // status({preview: true}) probes real TV modes via dbus-send group call and persists knownPictureModes
        const previewSt = await dock.status({preview: true});
        assert.deepEqual(previewSt.pictureModeChoices, ['Dynamic', 'Standard', 'Eco', 'Movie', 'Filmmaker Mode']);
        assert.deepEqual(dock.readState().knownPictureModes, ['Dynamic', 'Standard', 'Eco', 'Movie', 'Filmmaker Mode']);

        // Locked / Game mode (< 2 offered) does NOT overwrite knownPictureModes
        dock.pictureModesCache = null;
        probeMode = 'locked';
        const lockedSt = await dock.status({preview: true});
        assert.deepEqual(lockedSt.pictureModeChoices, ['Dynamic', 'Standard', 'Eco', 'Movie', 'Filmmaker Mode']);

        // Fallback to busctl per-mode checks on older firmware without CheckPictureModeByGroup
        dock.pictureModesCache = null;
        probeMode = 'fallback';
        const fbSt = await dock.status({preview: true});
        assert.deepEqual(fbSt.pictureModeChoices, ['Dynamic', 'Standard', 'Natural', 'Movie']);
        assert.deepEqual(fbSt.pictureModes, ['Movie', 'Dynamic'], 'preferred modes filtered & padded to 2 offered on this TV');

        // setPictureModes writes file without restarting launcher & validates against known TV choices
        await assert.rejects(dock.setPictureModes(['Movie']), /at least two/i);
        await assert.rejects(dock.setPictureModes(['Movie', 'Bogus']), /Unknown picture mode/);
        await assert.rejects(dock.setPictureModes(['Movie', 'Filmmaker Mode']), /Unsupported picture mode on this TV/);
        await assert.rejects(dock.setPictureModes(['Movie', 'Movie']), /Duplicate picture mode/);
        const st1 = await dock.setPictureModes(['Standard', 'Movie', 'Natural']);
        assert.equal(refreshes, 0, 'no launcher restart needed for pictureModes');
        assert.deepEqual(st1.pictureModes, ['Standard', 'Movie', 'Natural']);
        assert.equal(fs.readFileSync(dock.pictureModesFile, 'utf8'), 'Standard\nMovie\nNatural\n');

        await dock.setPictureCycle(true);
        assert.equal(keyNotified, 1);
        assert.equal(dock.readState().pictureCycle, true);

        // launch() writes pictureModesFile in both Vetro and custom modes
        fs.unlinkSync(dock.pictureModesFile);
        dock.waitRunning = async () => ({running: true, pid: 100});
        await dock.launch(true);
        assert.equal(fs.readFileSync(dock.pictureModesFile, 'utf8'), 'Standard\nMovie\nNatural\n');

        fs.unlinkSync(dock.pictureModesFile);
        dock.patchState({enabled: false, customApp: 'org.example.custom'});
        assert.match(dock.startScript(), /--home-launch org\.example\.custom.*--picture-modes-file /);
        await dock.launch(false);
        assert.equal(fs.readFileSync(dock.pictureModesFile, 'utf8'), 'Standard\nMovie\nNatural\n');

        // 3. plugin-launcher.js actions
        const plugin = launcherPlugin.definition(dock).create({
            vetro: {
                agentConnected: () => false,
                json: (res, obj, code) => { res.code = code || 200; res.body = obj; },
                readJson: (req, res, cb) => cb(req._body)
            }
        });
        function callPlugin(body) {
            return new Promise(resolve => {
                const req = {_body: body, method: 'POST', url: '/action'};
                const res = {};
                const origJson = (r, obj, code) => { resolve({code: code || 200, body: obj}); };
                const p2 = launcherPlugin.definition(dock).create({
                    vetro: {agentConnected: () => false, json: origJson, readJson: (rq, rs, cb) => cb(rq._body)}
                });
                p2.route(req, res, 'action');
            });
        }
        assert.equal(plugin.status().enabled, true);
        const rCycle = await callPlugin({action: 'pictureCycle', on: false});
        assert.equal(rCycle.code, 200); assert.equal(rCycle.body.pictureCycle, false);
        const rModes = await callPlugin({action: 'pictureModes', modes: ['Movie', 'Dynamic']});
        assert.equal(rModes.code, 200); assert.deepEqual(rModes.body.pictureModes, ['Movie', 'Dynamic']);
        const rOldKeys = await callPlugin({action: 'pictureKeys', keys: ['140']});
        assert.equal(rOldKeys.code, 409, 'removed pictureKeys action is rejected');
        const rBad = await callPlugin({action: 'pictureModes', modes: ['Movie']});
        assert.equal(rBad.code, 409);
    } finally {
        fs.rmSync(dir, {recursive: true, force: true});
    }

    // 4. key-routing.js dockWordCommand('picnext')
    assert.ok(kr.dockWordCommand('picnext').includes("echo 'picnext' >&3"));
    assert.equal(kr.dockWordCommand('picprev'), null);

    // 5. volume-keys.js: 482 BLE hold (single step, no repeat), 140/396 tap vs hold + toast tap-window, 127/590 :hold
    {
        const reinjected = [], relayed = [], sent = [];
        const res = await vk.sendWord('picnext', 'XF86Focus', {
            write: () => { const e = new Error('no'); e.code = 'ENOENT'; throw e; },
            relay: async () => { throw new Error('launcher down'); },
            reinject: k => { reinjected.push(k); }
        });
        assert.equal(res, 'native');
        assert.deepEqual(reinjected, ['XF86Focus'], '482 reinjects XF86Focus when launcher is down');
        reinjected.length = 0;

        const rep = vk.repeater({
            write: (p, t) => sent.push(t),
            relay: async w => { relayed.push(w); },
            reinject: k => reinjected.push(k)
        });
        const F = {pad: true, picture: true};

        // 482 (SolarCell / BLE hold of 123/Settings): 1 picnext on down, repeat/up ignored (no overshoot on 2 modes)
        await rep.event({code: 482, action: 'down', decision: 'blocked'}, F);
        await rep.event({code: 482, action: 'repeat', decision: 'blocked'}, F);
        await rep.event({code: 482, action: 'up', decision: 'blocked'}, F);
        assert.deepEqual(sent, ['picnext\n']);
        assert.equal(rep.active(), false);

        // Within 2.7 s toast window after 482, short tap of 140 or 396 steps picture mode immediately (no pad, no XF86Menu)
        sent.length = 0;
        await rep.event({code: 140, action: 'down', decision: 'blocked'}, F);
        await rep.event({code: 140, action: 'up', decision: 'blocked'}, F);
        assert.deepEqual(sent, ['picnext\n'], 'tap 140 during toast window steps picture mode instead of opening pad');
        await rep.event({code: 396, action: 'down', decision: 'blocked'}, F);
        await rep.event({code: 396, action: 'up', decision: 'blocked'}, F);
        assert.deepEqual(sent, ['picnext\n', 'picnext\n'], 'tap 396 during toast window steps picture mode instead of reinjecting XF86Menu');
        assert.deepEqual(reinjected, []);

        // Reset toast window cleanly with stop()
        rep.stop();
        assert.equal(rep.active(), false);

        // 127:hold fires 1 picnext and does NOT auto-repeat
        sent.length = 0;
        await rep.event({code: 127, action: 'hold', decision: 'blocked'}, F);
        assert.deepEqual(sent, ['picnext\n']);
        assert.equal(rep.active(), false);
        await rep.event({code: 127, action: 'up', decision: 'blocked'}, F);
        rep.stop();

        // Shared 140 key outside window: short tap (<700 ms) sends 'pad' when pad=true, or reinjects XF86More when pad=false
        sent.length = 0;
        await rep.event({code: 140, action: 'down', decision: 'blocked'}, F);
        assert.deepEqual(sent, [], 'pad is NOT sent on down when 140 is shared with picture');
        assert.equal(rep.active(), true);
        await new Promise(r => setTimeout(r, 120));
        await rep.event({code: 140, action: 'up', decision: 'blocked'}, F);
        assert.deepEqual(sent, ['pad\n'], 'short tap sends pad on up');
        assert.equal(rep.active(), false);

        await rep.event({code: 140, action: 'down', decision: 'blocked'}, {pad: false, picture: true});
        await new Promise(r => setTimeout(r, 80));
        await rep.event({code: 140, action: 'up', decision: 'blocked'}, {pad: false, picture: true});
        assert.deepEqual(reinjected, ['XF86More'], 'short 140 tap reinjects XF86More when Number pad is off');
        reinjected.length = 0;

        // 396 short tap outside window reinjects XF86Menu
        await rep.event({code: 396, action: 'down', decision: 'blocked'}, F);
        await new Promise(r => setTimeout(r, 80));
        await rep.event({code: 396, action: 'up', decision: 'blocked'}, F);
        assert.deepEqual(reinjected, ['XF86Menu'], 'short 396 tap reinjects XF86Menu');
        reinjected.length = 0;

        // Shared 140 key: hold (>= 700 ms) fires 1 picnext and NOT pad on up
        sent.length = 0;
        await rep.event({code: 140, action: 'down', decision: 'blocked'}, F);
        await rep.event({code: 140, action: 'repeat', decision: 'blocked'}, F);
        assert.deepEqual(sent, [], 'repeat during hold-wait does not fire early');
        await new Promise(r => setTimeout(r, 780));
        assert.deepEqual(sent, ['picnext\n'], 'hold fires picnext at 700 ms');
        await rep.event({code: 140, action: 'up', decision: 'blocked'}, F);
        assert.deepEqual(sent, ['picnext\n'], 'up after hold does not send pad');
        assert.equal(rep.active(), false);
        rep.stop();

        // stop() during pending hold cancels both hold and pad
        sent.length = 0;
        await rep.event({code: 140, action: 'down', decision: 'blocked'}, F);
        rep.stop();
        assert.equal(rep.active(), false);
        await new Promise(r => setTimeout(r, 780));
        assert.deepEqual(sent, [], 'stop() cancelled pending hold');

        // At most 1 picnext in-flight when relay is slow
        let inFlight = 0, maxInFlight = 0, picWrites = 0, releaseSlow;
        const slowRep = vk.repeater({
            write: () => { const e = new Error('no'); e.code = 'EACCES'; throw e; },
            relay: () => {
                picWrites++;
                inFlight++;
                if (inFlight > maxInFlight) maxInFlight = inFlight;
                return new Promise(r => { releaseSlow = () => { inFlight--; r(); }; });
            },
            reinject: () => {}
        });
        slowRep.event({code: 482, action: 'down', decision: 'blocked'}, F);
        await new Promise(r => setTimeout(r, 20));
        assert.equal(picWrites, 1);
        assert.equal(maxInFlight, 1);
        releaseSlow();
        await new Promise(r => setTimeout(r, 20));
        slowRep.stop();
    }

    // 6. main.js integration (works on both Windows & Linux by stubbing updater)
    {
        const starts = [], commands = [];
        class Bridge {
            constructor(execute, onEvent, hooks) { this.onEvent = onEvent; this.hooks = hooks; this.current = {mode: 'off', connected: false}; }
            inventory() { return Promise.resolve([{name: 'Smart Control', node: '/dev/input/event5', unique: true, selectable: true}]); }
            status() { return {policy: this.current, events: []}; }
            stop() { this.current = {mode: 'off', connected: false}; if (this.hooks && this.hooks.onStop) this.hooks.onStop(); }
            async start(device, mode, tokens) { starts.push({device, mode, tokens}); this.current = {mode, connected: true, device: 'Smart Control', node: device}; return this.status(); }
        }
        let remapDisk = JSON.stringify({rules: [{id: 'physical_127', enabled: true, trigger: {type: 'physical', value: '127'}, actions: [{type: 'launch', appId: 'example.should_not_run'}]}]});
        let pending = null;
        const service = loadService(path.resolve(__dirname, '..'), {
            dependencies: {
                fs: Object.assign({}, fs, {
                    readFileSync: function (p, enc) {
                        if (String(p).endsWith('vetro-remap.json')) return remapDisk;
                        if (String(p).endsWith('vetro-dock.json')) return '';
                        return fs.readFileSync.apply(fs, arguments);
                    },
                    existsSync: () => false,
                    writeFileSync: (p, s) => { pending = s; },
                    renameSync: () => { remapDisk = pending; },
                    unlinkSync: () => {}
                }),
                './updater.js': {Updater: function () { this.status = () => ({}); this.notePid = () => {}; this.confirm = () => Promise.resolve(); }, packagePort: () => null},
                './physical-input.js': {Bridge},
                './platform.js': {
                    start() {},
                    status: () => ({agent: {connected: true}}),
                    execute: cmd => { commands.push(cmd); return Promise.resolve({exit_code: 0, stdout: ''}); }
                }
            },
            setInterval: () => ({unref() {}})
        });
        service.context.dock.readState = () => ({enabled: true, customApp: null, volumePill: false, numberPad: false, pictureCycle: true});
        assert.deepEqual(JSON.parse(JSON.stringify(service.context.keyFlags())), {volume: false, pad: false, picture: true});

        const ok = await service.context.vetroUiInput();
        assert.equal(ok, true);
        assert.equal(starts.length, 1);
        assert.deepEqual(Array.from(starts[0].tokens).sort(), ['127', '140', '396', '482', '590:hold'], 'user rule on 127 keeps plain 127; 140/396/482 plain; 590 :hold');

        // hold-wait never triggers remap rule; 482 down triggers picnext and not remap rule
        service.context.physical.onEvent({code: 127, action: 'down', decision: 'hold-wait'});
        service.context.physical.onEvent({code: 482, action: 'down', decision: 'blocked'});
        service.context.physical.onEvent({code: 482, action: 'up', decision: 'blocked'});
        await new Promise(r => setTimeout(r, 40));
        assert.ok(commands.some(c => c.includes("echo 'picnext'")), '482 down sent picnext via dockWordCommand');
        assert.ok(!commands.some(c => c.includes('example.should_not_run')), 'hold-wait never triggered user shortcut');

        // Verify reservedKeys on GET /remap includes fixed picture keys
        const remap = await new Promise(resolve => {
            const req = new EventEmitter(); req.method = 'GET'; req.url = '/remap';
            service.router(req, {writeHead() {}, end(s) { resolve(JSON.parse(s)); }});
            req.emit('end');
        });
        assert.deepEqual(remap.reserved.map(k => k.code), ['140', '482', '396', '127', '590']);
        assert.equal(remap.reserved[0].hold, true);
        assert.equal(remap.reserved[1].hold, undefined);
        service.lifecycle.onStop();
    }

    // 7. vetro-ui + settings-backup + diagnostic-report consistency
    assert.equal(typeof ui.pictureKeys, 'function');
    assert.ok(backup.build({dock: {enabled: false, pictureCycle: true}}).sections.launcher.pictureCycle);
    assert.equal(report.projectors.launcher({pictureCycle: true, pictureModes: ['Movie', 'Standard']}).pictureModeCount, 2);

    console.log('PASS picture-cycle: D-Bus probe+fallback, fixed Settings keys (482 BLE + 140/396/127/590), tap-window, launcher plugin, FIFO routing, vetro-ui, main integration, backup, diagnostics');
})().catch(e => { console.error(e); process.exitCode = 1; });
