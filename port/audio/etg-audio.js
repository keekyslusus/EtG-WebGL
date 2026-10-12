// A small Wwise-like playback engine for EtG's soundbanks, converted by build_audio.py.
//
// The game keeps calling AkSoundEngine (PostEvent, SetSwitch, SetState, SetRTPCValue...);
// WebShim.WwiseShim forwards those calls through EtgWebShim.jslib to window.EtgAudio here.
// audio.json holds the bank's object graph: events -> actions -> nodes (sounds, random/sequence,
// switch and blend containers, music) -> busses. This file interprets that graph with Web Audio.
//
// Implemented: Play/Stop/Pause/Resume/SetState/SetSwitch/SetGameParameter/Set* actions, random and
// sequence containers (weights, avoid-repeat, shuffle, step and continuous), switch containers, blend
// containers (all layers), volume/pitch/LPF/HPF including randomizers, state and RTPC offsets,
// bus hierarchy, max instances, 2D distance attenuation and panning, interactive music (switch and
// playlist containers, segments, transition rules). Not implemented: effects (reverb, EQ), aux
// sends, ducking, blend crossfade curves, music stingers (EtG has none).
(function () {
  'use strict';

  const LOG = '[EtgAudio]';
  const PARAM = { 0: 'vol', 2: 'pitch', 3: 'lpf', 4: 'hpf', 5: 'busvol', 6: 'makeup' };
  const CACHE_LIMIT = 200 * 1024 * 1024;   // decoded PCM kept around (float32)
  const PREDECODE_MAX_SECONDS = 1.0;       // short sounds are decoded ahead of time,
  const PREDECODE_BUDGET = 0.4;            // until the cache is this full
  const DEFAULT_PRIORITY = 50;
  const LATE_DROP = 0.3;                   // one-shots that would start this late are dropped

  let base = '';
  let version = '';
  let data = null, nodes = null, events = null, media = null, names = {};
  let ctx = null, masterIn = null, analyser = null;
  let offline = null;                     // OfflineAudioContext while api.render() runs (tests)
  let random = Math.random;
  let timers = [];                        // offline: [{t, fn}] on the context clock
  const inflight = new Set();             // decodes in progress (offline rendering waits for them)
  let packs = [];
  const buffers = new Map();               // media id -> {promise, buffer, bytes, last, refs}
  let cacheBytes = 0;
  const busses = new Map();                // bus id -> {id, input, filter, output, parent}
  const stateValues = new Map();           // state group -> state
  const switchValues = new Map();          // game object -> Map(group -> switch)
  const rtpcGlobal = new Map();            // rtpc -> value
  const rtpcObj = new Map();               // game object -> Map(rtpc -> value)
  const objects = new Map();               // game object -> {x, y, z}
  const listener = { x: 0, y: 0, z: 0 };
  const voices = new Set();                // Voice and SequenceInstance
  const seqState = new Map();              // random/sequence container (+object) -> playlist state
  const modifiers = new Map();             // 'node|obj' -> {vol, pitch, lpf, hpf, busvol}
  const chains = new Map();                // node id -> [node, parent, ..., top]
  const warned = new Set();
  let maxVoices = 128, volumeThreshold = -50; // from Init.bnk (STMG)
  let voiceSerial = 0;
  let nextPlayingId = 1 << 30; // ids from the jslib bridge count up from 1
  let onLoaded = null;
  let unlocked = false;

  // --- small helpers -----------------------------------------------------------------------

  const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);
  const dbToGain = (db) => (db <= -96 ? 0 : Math.pow(10, db / 20));
  const centsToRate = (c) => Math.pow(2, c / 1200);
  // Wwise's 0..100 filter values; 0 = no filtering
  const lpfToHz = (v) => 20000 * Math.pow(50 / 20000, clamp(v, 0, 100) / 100);
  const hpfToHz = (v) => 20 * Math.pow(10000 / 20, clamp(v, 0, 100) / 100);
  const rand = (lo, hi) => lo + random() * (hi - lo);

  // setTimeout, or the context clock while rendering offline.
  function later(fn, ms) {
    if (offline) timers.push({ t: ctx.currentTime + ms / 1000, fn });
    else setTimeout(fn, ms);
  }

  // Moves an AudioParam towards a value: over fadeMs (exponential approach, ~95% at fadeMs), or with
  // a short smoothing time constant. Before anything has been rendered the value is set outright.
  function approach(param, value, fadeMs, smoothing) {
    const t = ctx.currentTime;
    if (t === 0) {
      param.cancelScheduledValues(0);
      param.setValueAtTime(value, 0);
      return;
    }
    param.setTargetAtTime(value, t, fadeMs > 0 ? fadeMs / 3000 : smoothing);
  }

  // Seeded PRNG for reproducible test renders.
  function mulberry32(seed) {
    let a = seed >>> 0;
    return () => {
      a = (a + 0x6D2B79F5) >>> 0;
      let t = a;
      t = Math.imul(t ^ (t >>> 15), t | 1);
      t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }
  const objKey = (o) => (o === undefined || o === null ? -1 : o | 0);

  function warnOnce(key, ...msg) {
    if (!warned.has(key)) {
      warned.add(key);
      console.warn(LOG, ...msg);
    }
  }

  function nameOf(id) {
    return names[id] || String(id);
  }

  // Wwise IDs are FNV-1 32-bit hashes of the lower-case name.
  function hash(str) {
    let h = 2166136261;
    str = str.toLowerCase();
    for (let i = 0; i < str.length; i++) {
      h = Math.imul(h, 16777619) >>> 0;
      h = (h ^ (str.charCodeAt(i) & 0xff)) >>> 0;
    }
    return h;
  }

  // Piecewise-linear curve [[x, y, interp]...]; dB-scaled curves store linear gain - 1.
  function evalCurve(points, x, scaling) {
    let y;
    if (x <= points[0][0]) {
      y = points[0][1];
    } else if (x >= points[points.length - 1][0]) {
      y = points[points.length - 1][1];
    } else {
      for (let i = 1; i < points.length; i++) {
        if (x <= points[i][0]) {
          const a = points[i - 1], b = points[i];
          const t = (x - a[0]) / (b[0] - a[0] || 1);
          y = interp(a[1], b[1], t, a[2]);
          break;
        }
      }
    }
    return scaling === 2 ? 20 * Math.log10(Math.max(y + 1, 1e-5)) : y;
  }

  // AkCurveInterpolation: 0 Log3, 1 Sine, 2 Log1, 3 InvSCurve, 4 Linear, 5 SCurve, 6 Exp1, 7 SineRecip, 8 Exp3, 9 Constant
  function interp(a, b, t, kind) {
    switch (kind) {
      case 0: t = 1 - Math.pow(1 - t, 3); break;
      case 1: t = Math.sin(t * Math.PI / 2); break;
      case 2: t = 1 - (1 - t) * (1 - t); break;
      case 3: t = t < 0.5 ? 0.5 * Math.sin(t * Math.PI) : 1 - 0.5 * Math.sin(t * Math.PI); break;
      case 5: t = (1 - Math.cos(t * Math.PI)) / 2; break;
      case 6: t = t * t; break;
      case 7: t = 1 - Math.cos(t * Math.PI / 2); break;
      case 8: t = t * t * t; break;
      case 9: t = 0; break;
      default: break;
    }
    return a + (b - a) * t;
  }

  function node(id) {
    return nodes[id];
  }

  // [id, parent, grandparent...] through the actor-mixer / container hierarchy (not busses).
  function chainOf(id) {
    let c = chains.get(id);
    if (!c) {
      c = [];
      for (let cur = id; cur && nodes[cur] && c.length < 32; cur = nodes[cur].parent) {
        c.push(cur);
      }
      chains.set(id, c);
    }
    return c;
  }

  function outputBusOf(chain) {
    for (const id of chain) {
      const n = nodes[id];
      if (n && n.bus) {
        return n.bus;
      }
    }
    return 0;
  }

  function positioningOf(chain) {
    for (const id of chain) {
      const n = nodes[id];
      if (n && n.pos) {
        return n.pos;
      }
    }
    return null;
  }

  function getRtpc(id, obj) {
    const m = rtpcObj.get(obj);
    if (m && m.has(id)) {
      return m.get(id);
    }
    return rtpcGlobal.get(id);
  }

  // Adds a node's own properties + active state and RTPC offsets + Set* action modifiers.
  function accumulate(acc, id, obj) {
    const n = nodes[id];
    if (!n) {
      return;
    }
    addProps(acc, n.props);
    if (n.states) {
      for (const [group, list] of n.states) {
        const cur = stateValues.get(group);
        if (cur === undefined) {
          continue;
        }
        for (const [state, inst] of list) {
          if (state === cur && nodes[inst]) {
            addProps(acc, nodes[inst].props);
          }
        }
      }
    }
    if (n.rtpc) {
      for (const r of n.rtpc) {
        const name = PARAM[r.param];
        if (r.type !== 0 || !name) {
          continue;
        }
        const x = getRtpc(r.id, obj);
        if (x !== undefined) {
          acc[name] += evalCurve(r.points, x, r.scaling);
        }
      }
    }
    const m = modifiers.get(id + '|' + obj) || modifiers.get(id + '|-1');
    if (m) {
      addProps(acc, m);
    }
  }

  function addProps(acc, p) {
    if (!p) {
      return;
    }
    if (p.vol) acc.vol += p.vol;
    if (p.makeup) acc.makeup += p.makeup;
    if (p.pitch) acc.pitch += p.pitch;
    if (p.lpf) acc.lpf += p.lpf;
    if (p.hpf) acc.hpf += p.hpf;
    if (p.busvol) acc.busvol += p.busvol;
  }

  const newAcc = () => ({ vol: 0, makeup: 0, pitch: 0, lpf: 0, hpf: 0, busvol: 0 });

  // --- busses ------------------------------------------------------------------------------

  // y(x) for x in [-2, 2], sampled for a WaveShaper fed with x / 2: linear up to -1 dBFS, then
  // approaching full scale smoothly.
  function softClipCurve() {
    const n = 8193, knee = Math.pow(10, -1 / 20), range = 1 - knee;
    const curve = new Float32Array(n);
    for (let i = 0; i < n; i++) {
      const x = (i / (n - 1)) * 4 - 2;
      const a = Math.abs(x);
      curve[i] = a <= knee ? x : Math.sign(x) * (knee + range * Math.tanh((a - knee) / range));
    }
    return curve;
  }

  function buildBusses() {
    // The master bus's brick-wall peak limiter (-1 dB): an instantaneous soft clipper. Exact below
    // -1 dBFS, rounds off what goes above. (A DynamicsCompressorNode pulled transients like gun shots
    // down by ~10 dB even below its threshold - found by the audiodiff test.)
    const limiter = ctx.createWaveShaper();
    limiter.curve = softClipCurve();
    limiter.oversample = '2x';
    const toShaper = ctx.createGain();
    toShaper.gain.value = 0.5; // the curve covers inputs up to +6 dBFS
    toShaper.connect(limiter);
    limiter.connect(ctx.destination);
    masterIn = ctx.createGain();
    masterIn.connect(toShaper);
    analyser = ctx.createAnalyser();
    analyser.fftSize = 2048;
    limiter.connect(analyser);

    for (const id in nodes) {
      const n = nodes[id];
      const ir = data.reverbs && data.reverbs[id];
      // Aux busses only exist as their measured effect (Foyer, Dimension...); the others add nothing.
      if (n.type !== 'bus' && !(n.type === 'auxbus' && ir)) {
        continue;
      }
      const aux = n.type === 'auxbus';
      const input = ctx.createGain();
      const filter = ctx.createBiquadFilter();
      filter.type = 'lowpass';
      filter.frequency.value = 22000;
      const output = ctx.createGain();
      input.connect(filter);
      // A measured impulse response stands for the bus's whole effect chain; otherwise its EQs. An
      // aux bus stays silent until its response is loaded (its direct signal would double the sound).
      const last = ir ? filter : effectChain(n.fx, filter);
      if (!aux) last.connect(output);
      if (ir) loadReverb(ir, last, output);
      busses.set(+id, { id: +id, input, filter, output, parent: n.parent, aux });
    }
    for (const b of busses.values()) {
      const parent = busses.get(b.parent);
      b.output.connect(parent ? parent.input : masterIn);
    }
    updateBusses(0);
  }

  // HDR busses (EtG: Dungeon, all sound effects). Wwise sets the top of a window by the loudest voice
  // playing on the bus (its volume without make-up gain) and attenuates every voice by
  // top * (1 - 1/ratio), the top being at least the threshold: with EtG's ratio of 50 and threshold of
  // -15 dB the loudest sound plays at about its make-up gain whatever its volume (down to -15 dB),
  // quieter ones relative to it. A louder voice moves the window at once,
  // when it ends the window falls back over the release time. Fitted to the real Wwise with audiodiff.
  function hdrBusOf(busId) {
    for (let b = busses.get(busId); b; b = busses.get(b.parent)) {
      if (nodes[b.id].hdr) return b.id;
    }
    return 0;
  }

  // Follows the envelopes of the voices on every HDR bus (offline renders call it every step).
  function hdrTick() {
    if (!ctx) return;
    for (const [id, b] of busses) {
      if (b.hdrTop !== undefined) updateHdr(id, ctx.currentTime);
    }
  }

  function updateHdr(busId, at) {
    const b = busses.get(busId);
    const h = nodes[busId].hdr;
    if (!b || !h) return;
    let top = -Infinity;
    const t = Math.max(at, ctx.currentTime);
    for (const v of voices) {
      if (v.hdrBus === busId && !v.stopped && v.hdrLevel !== undefined) top = Math.max(top, v.hdrLevelAt(t));
    }
    if (top === -Infinity) return; // nothing playing: the next voice moves the window
    top = Math.max(top, h.threshold); // the window never goes below the threshold
    const gain = dbToGain(-top * (1 - 1 / Math.max(h.ratio, 1)));
    const param = b.input.gain;
    const now = ctx.currentTime;
    at = Math.max(at, now);
    if (b.hdrTop === undefined || top >= b.hdrTop) {
      param.cancelScheduledValues(at);
      param.setValueAtTime(gain, at);
    } else {
      param.cancelScheduledValues(at);
      param.setTargetAtTime(gain, at, Math.max(h.release, 0.01) / 3);
    }
    b.hdrTop = top;
  }

  // Wwise Parametric EQ filter types -> BiquadFilterNode types
  const EQ_TYPES = ['lowpass', 'highpass', 'bandpass', 'notch', 'lowshelf', 'highshelf', 'peaking'];

  // Appends the supported effects of an fx list ([[index, effect id]...]) after `node`; returns the
  // last node. Parametric EQs become biquads, Delay a feedback delay line; the other plugins
  // (distortion, flanger, harmonizer, tremolo, compressor) are skipped.
  function effectChain(fx, node) {
    if (!fx) return node;
    for (const [, id] of fx) {
      const e = data.effects && data.effects[id];
      if (!e) continue;
      if (e.plugin === 'parametric_eq') {
        for (const band of e.bands) {
          if (!band.on || EQ_TYPES[band.type] === undefined) continue;
          const f = ctx.createBiquadFilter();
          f.type = EQ_TYPES[band.type];
          f.frequency.value = Math.min(band.freq, ctx.sampleRate / 2 - 100);
          f.Q.value = band.q;
          f.gain.value = band.gain;
          node.connect(f);
          node = f;
        }
      } else if (e.plugin === 'delay') {
        // dry * (1 - mix) + echo * mix, the echo fed back by `feedback` %
        const dry = ctx.createGain();
        dry.gain.value = 1 - e.mix / 100;
        const delay = ctx.createDelay(Math.max(1, e.time * 2));
        delay.delayTime.value = e.time;
        const feedback = ctx.createGain();
        feedback.gain.value = Math.min(e.feedback / 100, 0.95);
        const wet = ctx.createGain();
        wet.gain.value = e.mix / 100;
        const sum = ctx.createGain();
        node.connect(dry);
        dry.connect(sum);
        node.connect(delay);
        delay.connect(feedback);
        feedback.connect(delay);
        delay.connect(wet);
        wet.connect(sum);
        node = sum;
      } else {
        continue;
      }
      if (e.output) {
        const g = ctx.createGain();
        g.gain.value = dbToGain(e.output);
        node.connect(g);
        node = g;
      }
    }
    return node;
  }


  // Seconds an effect chain keeps sounding after its input stops (echoes down to -60 dB, at most 4 s).
  function effectTail(chain) {
    let tail = 0;
    for (const id of chain) {
      for (const [, fxId] of nodes[id].fx || []) {
        const e = data.effects && data.effects[fxId];
        if (e && e.plugin === 'delay') {
          const fb = Math.min(e.feedback / 100, 0.95);
          const repeats = fb > 0.001 ? Math.log(0.001) / Math.log(fb) : 1;
          tail = Math.max(tail, Math.min(4, e.time * (repeats + 1)));
        }
      }
    }
    return tail;
  }

  // Swaps the bus's direct path for a ConvolverNode once the impulse response is loaded.
  const reverbBuffers = new Map(); // url -> Promise<AudioBuffer>
  function loadReverb(url, from, to) {
    let p = reverbBuffers.get(url);
    if (!p) {
      p = fetchBuffer(url).then((b) => ctx.decodeAudioData(b));
      reverbBuffers.set(url, p);
    }
    const target = ctx;
    const done = p.then((buf) => {
      if (ctx !== target) return; // the context was replaced (offline test render)
      const conv = ctx.createConvolver();
      conv.normalize = false;
      conv.buffer = buf;
      from.disconnect();
      from.connect(conv);
      conv.connect(to);
    }, (e) => warnOnce('reverb' + url, 'cannot load reverb', url, e));
    inflight.add(done);
    done.then(() => inflight.delete(done));
  }

  function updateBusses(fade) {
    for (const b of busses.values()) {
      const acc = newAcc();
      accumulate(acc, b.id, -1);
      const gain = dbToGain(acc.vol + acc.makeup + acc.busvol);
      // aux busses are switched on their input: a convolver fed silence stops costing CPU
      approach(b.aux ? b.input.gain : b.output.gain, gain, fade, 0.01);
      approach(b.filter.frequency, acc.lpf > 0 ? lpfToHz(acc.lpf) : 22000, fade, 0.05);
    }
  }

  function busInput(busId) {
    const b = busses.get(busId);
    return b ? b.input : masterIn;
  }

  // --- media -------------------------------------------------------------------------------

  // fetch() of a file under the audio directory as an ArrayBuffer, retried a few times: the packs are
  // big and come in parallel, a flaky connection or a busy local server can drop one.
  async function fetchBuffer(path) {
    for (let attempt = 0; ; attempt++) {
      try {
        const r = await fetch(base + path + (version ? '?v=' + version : ''));
        if (!r.ok) throw new Error(path + ': HTTP ' + r.status);
        return await r.arrayBuffer();
      } catch (e) {
        if (attempt >= 3) throw e;
        await new Promise((res) => setTimeout(res, 500 << attempt));
      }
    }
  }

  function loadPacks() {
    packs = data.packs.map((name) => fetchBuffer(name));
    Promise.all(packs).then(predecode, (e) => console.error(LOG, 'loading sound packs failed', e));
  }

  // Decode the short sound effects in the background so the first shot of a gun isn't late.
  async function predecode() {
    const ids = Object.keys(media).filter((id) => {
      const m = media[id];
      return m.pack && m.samples / m.rate <= PREDECODE_MAX_SECONDS;
    });
    for (const id of ids) {
      if (cacheBytes > CACHE_LIMIT * PREDECODE_BUDGET) break;
      try {
        await getBuffer(+id);
      } catch (e) {
        // reported by getBuffer
      }
    }
  }

  async function fetchEncoded(id, m) {
    if (m.pack) {
      const [index, offset, length] = m.pack;
      const pack = await packs[index];
      return pack.slice(offset, offset + length);
    }
    const pre = encoded.get(id);
    if (pre) {
      encoded.delete(id);
      return pre;
    }
    return fetchBuffer(m.file);
  }

  function getBuffer(id) {
    let e = buffers.get(id);
    if (e) {
      e.last = performance.now();
      return e.promise;
    }
    const m = media[id];
    if (!m) {
      return Promise.reject(new Error('no media ' + id));
    }
    e = { promise: null, buffer: null, bytes: 0, last: performance.now(), refs: 0 };
    e.promise = fetchEncoded(id, m)
      .then((enc) => ctx.decodeAudioData(enc))
      .then((buf) => {
        e.buffer = buf;
        e.bytes = buf.length * buf.numberOfChannels * 4;
        cacheBytes += e.bytes;
        evict();
        return buf;
      }, (err) => {
        buffers.delete(id);
        warnOnce('media' + id, 'cannot decode media', id, err);
        throw err;
      });
    buffers.set(id, e);
    inflight.add(e.promise);
    e.promise.then(() => inflight.delete(e.promise), () => inflight.delete(e.promise));
    return e.promise;
  }

  function evict() {
    if (cacheBytes <= CACHE_LIMIT) {
      return;
    }
    const list = [...buffers.entries()].filter(([, e]) => e.buffer && e.refs === 0).sort((a, b) => a[1].last - b[1].last);
    for (const [id, e] of list) {
      if (cacheBytes <= CACHE_LIMIT * 0.8) {
        break;
      }
      buffers.delete(id);
      cacheBytes -= e.bytes;
    }
  }

  // --- voices ------------------------------------------------------------------------------

  // One playing sound: AudioBufferSource -> [LPF] -> [HPF] -> gain -> [panner] -> bus.
  class Voice {
    constructor(soundId, pc) {
      this.id = soundId;
      this.chain = chainOf(soundId);
      this.obj = pc.obj;
      this.pid = pc.pid;
      this.event = pc.event;
      this.bus = outputBusOf(this.chain);
      this.pos = positioningOf(this.chain);
      this.is3d = !!(this.pos && this.pos['3d']);
      this.started = false;
      this.stopped = false;
      this.paused = false;
      this.offset = 0;
      this.onEnded = null;
      this.random = newAcc();
      for (const id of this.chain) {
        const r = nodes[id].ranged;
        if (r) {
          if (r.vol) this.random.vol += rand(r.vol[0], r.vol[1]);
          if (r.makeup) this.random.makeup += rand(r.makeup[0], r.makeup[1]);
          if (r.pitch) this.random.pitch += rand(r.pitch[0], r.pitch[1]);
          if (r.lpf) this.random.lpf += rand(r.lpf[0], r.lpf[1]);
          if (r.hpf) this.random.hpf += rand(r.hpf[0], r.hpf[1]);
        }
      }
      const props = nodes[soundId].props || {};
      this.loops = props.loop === undefined ? 1 : props.loop; // 0 = infinite
      let delay = pc.delay || 0;
      for (const id of this.chain) {
        const p = nodes[id].props;
        if (p && p.idelay) delay += p.idelay;
      }
      this.when = ctx.currentTime + delay;
      this.fadeIn = pc.fadeIn || 0;
      this.mediaId = nodes[soundId].media;
      this.priority = priorityOf(this.chain);
      this.hdrBus = hdrBusOf(this.bus);
      if (this.hdrBus) this.setupEnvelope();
      this.serial = voiceSerial++;
      voices.add(this);
    }

    params() {
      const acc = newAcc();
      for (const id of this.chain) {
        accumulate(acc, id, this.obj);
      }
      acc.vol += this.random.vol;
      acc.makeup += this.random.makeup;
      acc.pitch += this.random.pitch;
      acc.lpf += this.random.lpf;
      acc.hpf += this.random.hpf;
      if (this.is3d) {
        acc.vol += this.attenuation();
      }
      return acc;
    }

    // HDR envelope tracking: the nearest node overriding the HDR settings decides (else the sound's
    // own flag); the active range (prop 33) limits how far the tracked level drops below the peak.
    setupEnvelope() {
      let enabled = false, decided = false, range;
      for (const id of this.chain) {
        const n = nodes[id];
        if (!decided && n.hdr_env && (n.hdr_env[0] || id === this.id)) {
          enabled = n.hdr_env[1];
          if (n.hdr_env[0]) decided = true;
        }
        if (range === undefined && n.props && n.props['33'] !== undefined) range = n.props['33'];
      }
      const m = media[this.mediaId];
      this.env = enabled && m && m.env ? m.env : null;
      this.envRange = range === undefined ? 12 : range;
    }

    // User aux busses the voice sends to: those of the nearest node in its hierarchy with sends.
    auxSends() {
      for (const id of this.chain) {
        if (nodes[id].aux) return nodes[id].aux;
      }
      return [];
    }

    // Volume Wwise's HDR sees right now: the voice's volume plus its tracked envelope.
    hdrLevelAt(t) {
      if (!this.env || !this.started || !this.buffer) return this.hdrLevel;
      let pos = (t - this.startedAt) * this.src.playbackRate.value;
      if (pos < 0) pos = 0;
      if (this.loops !== 1) pos %= this.buffer.duration;
      const e = this.env;
      let v = e[e.length - 1][1];
      for (let i = 1; i < e.length; i++) {
        if (pos < e[i][0]) {
          const a = e[i - 1], b = e[i];
          v = a[1] + (b[1] - a[1]) * ((pos - a[0]) / (b[0] - a[0] || 1));
          break;
        }
      }
      return this.hdrLevel + Math.max(v, -this.envRange);
    }

    // Curve of the voice's attenuation for one use (0 dry volume, 5 spread...), at the current distance.
    attenuationCurve(use) {
      const att = nodes[this.pos.att];
      if (!att || att.uses[use] === 255) return null;
      const curve = att.curves[att.uses[use]];
      return curve ? evalCurve(curve.points, this.distance(), curve.scaling) : null;
    }

    attenuation() {
      return this.attenuationCurve(0) || 0;
    }

    // Emitter relative to the listener, in Wwise's frame: x right, y up, z forward (the listener is
    // not rotated: in EtG it is a child of the player, which never turns).
    relative() {
      const o = objects.get(this.obj);
      if (!o) return null;
      return [o.x - listener.x, o.y - listener.y, (o.z || 0) - (listener.z || 0)];
    }

    distance() {
      const d = this.relative();
      return d ? Math.hypot(d[0], d[1], d[2]) : 0;
    }

    // Stereo position like Wwise's 3D panning: the azimuth on the horizontal (x/z) plane gives the
    // direction, the attenuation's spread curve (0-100 %) widens the source towards the centre.
    pan() {
      const d = this.relative();
      if (!d || (d[0] === 0 && d[2] === 0)) return 0;
      const spread = this.attenuationCurve(5);
      const width = spread === null ? 0 : clamp(spread / 100, 0, 1);
      return clamp((d[0] / Math.hypot(d[0], d[2])) * (1 - width), -1, 1);
    }

    start(buffer) {
      if (this.stopped) {
        return;
      }
      const now = ctx.currentTime;
      const late = now - this.when;
      if (late > LATE_DROP && this.loops !== 0 && !this.keepIfLate) {
        this.finish();
        return;
      }
      const e = buffers.get(this.mediaId);
      if (e) e.refs++;
      this.buffer = buffer;
      const src = ctx.createBufferSource();
      src.buffer = buffer;
      const m = media[this.mediaId];
      if (this.loops !== 1) {
        src.loop = true;
        if (m.loop) {
          src.loopStart = m.loop[0] / m.rate;
          src.loopEnd = m.loop[1] / m.rate;
        }
      }
      let out = src;
      const p = this.params();
      if (p.vol + p.makeup < volumeThreshold && this.loops === 1) { // inaudible one-shot: Wwise makes it virtual
        this.finish();
        return;
      }
      if (p.lpf > 0) {
        this.lpf = ctx.createBiquadFilter();
        this.lpf.type = 'lowpass';
        this.lpf.frequency.value = lpfToHz(p.lpf);
        out.connect(this.lpf);
        out = this.lpf;
      }
      if (p.hpf > 0) {
        this.hpf = ctx.createBiquadFilter();
        this.hpf.type = 'highpass';
        this.hpf.frequency.value = hpfToHz(p.hpf);
        out.connect(this.hpf);
        out = this.hpf;
      }
      for (const id of this.chain) out = effectChain(nodes[id].fx, out); // sound first, then its parents
      this.fxTail = effectTail(this.chain);
      this.gain = ctx.createGain();
      out.connect(this.gain);
      out = this.gain;
      if (this.is3d) {
        this.panner = ctx.createStereoPanner();
        this.panner.pan.value = this.pan();
        out.connect(this.panner);
        out = this.panner;
      }
      out.connect(busInput(this.bus));
      for (const auxId of this.auxSends()) {
        const b = busses.get(auxId);
        if (b && b.aux) out.connect(b.input); // user aux send, 0 dB
      }
      src.playbackRate.value = centsToRate(p.pitch);
      const at = Math.max(this.when, now);
      if (this.hdrBus) {
        this.hdrLevel = p.vol;
        updateHdr(this.hdrBus, at);
      }
      const target = dbToGain(p.vol + p.makeup);
      if (this.fadeIn > 0) {
        this.gain.gain.setValueAtTime(0, at);
        this.gain.gain.linearRampToValueAtTime(target, at + this.fadeIn / 1000);
      } else {
        this.gain.gain.value = target;
      }
      let offset = this.offset;
      if (late > 0 && this.loops !== 1) {
        offset += late; // keep loops in phase with when they were asked for
      }
      if (offset >= buffer.duration) {
        offset = this.loops === 1 ? buffer.duration : offset % buffer.duration;
      }
      src.onended = () => {
        if (this.src === src) this.finish();
      };
      src.start(at, offset);
      if (this.loops > 1) {
        src.stop(at + buffer.duration * this.loops - offset);
      }
      this.startedAt = at - offset;
      this.src = src;
      this.started = true;
    }

    update(fade) {
      if (!this.started || this.stopped) {
        return;
      }
      const p = this.params();
      if (!this.fadingOut) approach(this.gain.gain, dbToGain(p.vol + p.makeup), fade, 0.02);
      if (this.hdrBus) {
        this.hdrLevel = p.vol;
        updateHdr(this.hdrBus, ctx.currentTime);
      }
      approach(this.src.playbackRate, centsToRate(p.pitch), fade, 0.02);
      if (this.lpf) approach(this.lpf.frequency, p.lpf > 0 ? lpfToHz(p.lpf) : 22000, fade, 0.02);
      if (this.hpf) approach(this.hpf.frequency, p.hpf > 0 ? hpfToHz(p.hpf) : 10, fade, 0.02);
      if (this.panner) approach(this.panner.pan, this.pan(), 0, 0.02);
    }

    stop(fadeMs) {
      if (this.stopped) {
        return;
      }
      if (!this.started) {
        this.finish();
        return;
      }
      const t = ctx.currentTime;
      const fade = Math.max((fadeMs || 0) / 1000, 0.005);
      const g = this.gain.gain;
      g.cancelScheduledValues(t);
      g.setValueAtTime(g.value, t);
      g.linearRampToValueAtTime(0, t + fade);
      this.fadingOut = true;
      try {
        this.src.stop(t + fade);
      } catch (e) {
        this.finish();
      }
      this.stopped = true;
    }

    pause() {
      if (this.paused || this.stopped) {
        return;
      }
      this.paused = true;
      if (this.started) {
        const played = ctx.currentTime - this.startedAt;
        this.offset = this.loops !== 1 ? played % this.buffer.duration : played;
        const src = this.src;
        this.src = null;
        src.onended = null;
        src.stop();
        this.started = false;
      }
    }

    resume() {
      if (!this.paused || this.stopped) {
        return;
      }
      this.paused = false;
      this.when = ctx.currentTime;
      this.keepIfLate = true;
      this.fadeIn = 0;
      if (this.buffer) {
        this.start(this.buffer);
      }
    }

    finish() {
      if (this.finished) {
        return;
      }
      this.finished = true;
      this.stopped = true;
      voices.delete(this);
      if (this.hdrBus && this.hdrLevel !== undefined) updateHdr(this.hdrBus, ctx.currentTime);
      if (this.buffer) {
        const e = buffers.get(this.mediaId);
        if (e) e.refs--;
      }
      for (const n of [this.src, this.lpf, this.hpf]) {
        if (n) n.disconnect();
      }
      // an echo keeps ringing after the sound ends, as in Wwise
      const out = [this.gain, this.panner];
      const cut = () => out.forEach((n) => n && n.disconnect());
      if (this.fxTail > 0 && !this.fadingOut) later(cut, this.fxTail * 1000);
      else cut();
      if (this.onEnded) {
        this.onEnded(this);
      }
    }
  }

  // A continuous random/sequence container: plays its playlist one item after another.
  class SequenceInstance {
    constructor(id, pc) {
      this.id = id;
      this.chain = chainOf(id);
      this.obj = pc.obj;
      this.pid = pc.pid;
      this.event = pc.event;
      this.pc = pc;
      this.stopped = false;
      this.loopsLeft = nodes[id].loop; // 0 = infinite
      this.current = null;
      voices.add(this);
    }

    next() {
      if (this.stopped) {
        return;
      }
      const n = nodes[this.id];
      const st = playlistState(n, this.id, this.obj);
      if (st.pos >= n.playlist.length) {
        st.pos = 0;
        if (this.loopsLeft === 1) {
          this.finish();
          return;
        }
        if (this.loopsLeft > 1) this.loopsLeft--;
      }
      const child = n.mode === 1 ? n.playlist[st.pos][0] : pickRandom(n, st);
      st.pos++;
      const started = [];
      playNode(child, Object.assign({}, this.pc, { delay: 0, fadeIn: 0, collect: started }));
      const transition = (n.trans && n.trans[0]) / 1000 || 0;
      if (started.length === 0) {
        later(() => this.next(), 50);
        return;
      }
      const v = started[started.length - 1];
      this.current = started;
      if (n.trans_mode === 5 && transition > 0) {        // trigger rate
        later(() => this.next(), transition * 1000);
      } else if (n.trans_mode === 3 && transition > 0) { // delay
        v.onEnded = () => later(() => this.next(), transition * 1000);
      } else {
        v.onEnded = () => this.next();
      }
    }

    stop(fadeMs) {
      this.stopped = true;
      if (this.current) {
        for (const v of this.current) v.stop(fadeMs);
      }
      this.finish();
    }

    pause() {}
    resume() {}
    update() {}

    finish() {
      this.stopped = true;
      voices.delete(this);
    }
  }

  // --- playing nodes -----------------------------------------------------------------------

  function playlistState(n, id, obj) {
    const key = n.bits & 0x10 ? id : id + '|' + obj; // isGlobal
    let st = seqState.get(key);
    if (!st) {
      st = { pos: 0, history: [], bag: null };
      seqState.set(key, st);
    }
    return st;
  }

  function pickRandom(n, st) {
    const list = n.playlist;
    if (list.length === 1) {
      return list[0][0];
    }
    let candidates = list;
    if (n.random === 1) { // shuffle: every item once before any repeats
      if (!st.bag || st.bag.length === 0) st.bag = list.slice();
      candidates = st.bag;
    }
    const avoid = Math.min(n.avoid || 0, list.length - 1);
    const recent = st.history.slice(-avoid);
    const filtered = avoid > 0 ? candidates.filter((it) => !recent.includes(it[0])) : candidates;
    if (filtered.length) candidates = filtered;
    const useWeight = n.bits & 1;
    let total = 0;
    for (const it of candidates) total += useWeight ? it[1] : 1;
    let r = random() * total;
    let pick = candidates[candidates.length - 1];
    for (const it of candidates) {
      r -= useWeight ? it[1] : 1;
      if (r < 0) {
        pick = it;
        break;
      }
    }
    if (st.bag) st.bag.splice(st.bag.indexOf(pick), 1);
    st.history.push(pick[0]);
    if (st.history.length > 16) st.history.shift();
    return pick[0];
  }

  function playNode(id, pc) {
    const n = nodes[id];
    if (!n) {
      warnOnce('node' + id, 'unknown node', id, 'in event', nameOf(pc.event));
      return;
    }
    switch (n.type) {
      case 'sound':
        playSound(id, pc);
        break;
      case 'ranseq': {
        if (!n.playlist.length) break;
        if (n.bits & 8) { // continuous
          const seq = new SequenceInstance(id, pc);
          const st = playlistState(n, id, pc.obj);
          if (n.bits & 2) st.pos = 0; // reset playlist at each play
          seq.next();
        } else {
          const st = playlistState(n, id, pc.obj);
          let child;
          if (n.mode === 1) {
            if (st.pos >= n.playlist.length) st.pos = 0;
            child = n.playlist[st.pos++][0];
          } else {
            child = pickRandom(n, st);
          }
          playNode(child, pc);
        }
        break;
      }
      case 'switch': {
        let value;
        if (n.group_type === 1) {
          value = stateValues.get(n.group);
        } else {
          const m = switchValues.get(pc.obj);
          value = m && m.get(n.group);
        }
        if (value === undefined || value === 0) value = n.default; // unset or the "None" state
        const list = n.switches[value];
        if (list) for (const c of list) playNode(c, pc);
        break;
      }
      case 'layer':
        for (const c of n.children || []) playNode(c, pc);
        break;
      case 'mseg':
      case 'mranseq':
      case 'mswitch':
        Music.play(id, pc);
        break;
      default:
        warnOnce('play' + n.type, 'cannot play node type', n.type, 'in event', nameOf(pc.event));
    }
  }

  function playSound(id, pc) {
    const n = nodes[id];
    if (!media[n.media]) {
      warnOnce('nomedia' + n.media, 'sound', id, 'has no media', n.media);
      return;
    }
    if (!enforceLimits(id, pc)) {
      return;
    }
    const v = new Voice(id, pc);
    if (pc.keepIfLate) v.keepIfLate = true;
    if (pc.collect) pc.collect.push(v);
    // Before the first click/key the context is suspended; one-shots asked for then would all
    // play at once when it resumes.
    if (!offline && ctx.state !== 'running' && v.loops === 1 && !pc.keepIfLate) {
      v.finish();
      return;
    }
    const e = buffers.get(v.mediaId);
    if (e && e.buffer) {
      e.last = performance.now();
      v.start(e.buffer);
    } else {
      getBuffer(v.mediaId).then((buf) => v.start(buf), () => v.finish());
    }
  }

  // The first Priority property up the hierarchy (not additive).
  function priorityOf(chain) {
    for (const id of chain) {
      const p = nodes[id].props;
      if (p && p.priority !== undefined) return p.priority;
    }
    return DEFAULT_PRIORITY;
  }

  // Max instances (Advanced Settings): bit 0 = kill newest instead of oldest. Then the global
  // voice limit: the lowest-priority voice (the oldest among equals) makes room, or the new one
  // is dropped if it is the least important.
  function enforceLimits(id, pc) {
    for (const nid of chainOf(id)) {
      const lim = nodes[nid].max;
      if (!lim) continue;
      const active = [];
      for (const v of voices) {
        if (v instanceof Voice && !v.stopped && v.chain.includes(nid)) active.push(v);
      }
      if (active.length >= lim.n) {
        if (lim.bits & 1) return false;
        active.slice(0, active.length - lim.n + 1).forEach((v) => v.stop(10));
      }
    }
    const active = [];
    for (const v of voices) {
      if (v instanceof Voice && !v.stopped) active.push(v);
    }
    if (active.length >= maxVoices) {
      active.sort((a, b) => a.priority - b.priority || a.serial - b.serial);
      if (active[0].priority > priorityOf(chainOf(id))) return false;
      for (let i = 0; i <= active.length - maxVoices; i++) active[i].stop(10);
    }
    return true;
  }

  // --- actions -----------------------------------------------------------------------------

  function voicesFor(a, obj) {
    const out = [];
    const all = a.scope === 4 || a.scope === 5 || a.scope === 8 || a.scope === 9;
    const onObj = a.scope === 3 || a.scope === 5 || a.scope === 9;
    const except = a.except ? a.except.map((e) => e[0]) : [];
    for (const v of voices) {
      if (onObj && v.obj !== obj) continue;
      if (all) {
        if (except.length && v.chain.some((id) => except.includes(id))) continue;
      } else if (a.is_bus) {
        if (!busChainIncludes(v.bus, a.target)) continue;
      } else if (!(v.matches ? v.matches(a.target) : v.chain.includes(a.target))) {
        continue;
      }
      out.push(v);
    }
    return out;
  }

  function busChainIncludes(busId, target) {
    for (let b = busses.get(busId); b; b = busses.get(b.parent)) {
      if (b.id === target) return true;
    }
    return false;
  }

  function actionDelay(a) {
    let ms = (a.props && a.props.delay) || 0;
    if (a.ranged && a.ranged.delay) ms += rand(a.ranged.delay[0], a.ranged.delay[1]);
    return ms;
  }

  function runAction(a, obj, pid, eventId) {
    const delay = actionDelay(a);
    if (delay > 0 && a.type !== 4) {
      later(() => execAction(a, obj, pid, eventId, 0), delay);
    } else {
      execAction(a, obj, pid, eventId, delay);
    }
  }

  function execAction(a, obj, pid, eventId, delayMs) {
    const fade = (a.props && a.props.transition) || 0;
    switch (a.type) {
      case 0x04: // Play
        playNode(a.target, { obj, pid, event: eventId, delay: delayMs / 1000, fadeIn: fade });
        break;
      case 0x01: // Stop
        for (const v of voicesFor(a, obj)) v.stop(fade);
        break;
      case 0x02: // Pause
        for (const v of voicesFor(a, obj)) v.pause();
        break;
      case 0x03: // Resume
        for (const v of voicesFor(a, obj)) v.resume();
        break;
      case 0x06: // Mute
        setModifier(a, obj, { vol: -96 });
        break;
      case 0x07: // UnMute
        clearModifier(a, obj, 'vol');
        break;
      case 0x08: case 0x0A: case 0x0C: case 0x0E: case 0x20: { // Set Pitch / Volume / BusVolume / LPF / HPF
        const key = { 0x08: 'pitch', 0x0A: 'vol', 0x0C: 'busvol', 0x0E: 'lpf', 0x20: 'hpf' }[a.type];
        const [v, lo, hi] = a.value;
        setModifier(a, obj, { [key]: v + (lo || hi ? rand(lo, hi) : 0) }, fade);
        break;
      }
      case 0x09: case 0x0B: case 0x0D: case 0x0F: case 0x30: {
        const key = { 0x09: 'pitch', 0x0B: 'vol', 0x0D: 'busvol', 0x0F: 'lpf', 0x30: 'hpf' }[a.type];
        clearModifier(a, obj, key, fade);
        break;
      }
      case 0x12: // SetState
        setState(a.group, a.state);
        break;
      case 0x19: // SetSwitch
        setSwitch(a.group, a.switch, obj);
        break;
      case 0x13: { // SetGameParameter
        const [v, lo, hi] = a.value;
        setRtpc(a.target, v + (lo || hi ? rand(lo, hi) : 0), a.scope === 3 ? obj : -1, fade);
        break;
      }
      case 0x14:
        resetRtpc(a.target, a.scope === 3 ? obj : -1);
        break;
      case 0x1D: // Trigger
        Music.trigger(a.target, obj);
        break;
      default:
        warnOnce('action' + a.type, 'unsupported action type 0x' + a.type.toString(16), 'in', nameOf(eventId));
    }
  }

  function setModifier(a, obj, values, fade) {
    const key = a.target + '|' + (a.scope === 3 || a.scope === 5 ? obj : -1);
    modifiers.set(key, Object.assign(modifiers.get(key) || {}, values));
    refresh(fade);
  }

  function clearModifier(a, obj, prop, fade) {
    const key = a.target + '|' + (a.scope === 3 || a.scope === 5 ? obj : -1);
    const m = modifiers.get(key);
    if (m) {
      delete m[prop];
      refresh(fade);
    }
  }

  function refresh(fade) {
    updateBusses(fade || 0);
    for (const v of voices) v.update(fade || 0);
  }

  function setState(group, state) {
    if (stateValues.get(group) === state) return;
    stateValues.set(group, state);
    refresh(100);
    Music.onStateChange(group);
  }

  function setSwitch(group, value, obj) {
    let m = switchValues.get(obj);
    if (!m) {
      m = new Map();
      switchValues.set(obj, m);
    }
    m.set(group, value);
    Music.onSwitchChange(group, value, obj);
  }

  function setRtpc(id, value, obj, fadeMs) {
    if (obj === -1) {
      rtpcGlobal.set(id, value);
    } else {
      let m = rtpcObj.get(obj);
      if (!m) {
        m = new Map();
        rtpcObj.set(obj, m);
      }
      m.set(id, value);
    }
    refresh(fadeMs);
  }

  function resetRtpc(id, obj) {
    if (obj === -1) {
      rtpcGlobal.delete(id);
    } else if (rtpcObj.has(obj)) {
      rtpcObj.get(obj).delete(id);
    }
    refresh(0);
  }

  // --- interactive music -------------------------------------------------------------------
  // A Play on a music node creates a MusicInstance (it sits in `voices` like a sound, so Stop /
  // StopAll / StopPlayingID reach it). Inside, players mirror the node types:
  //   SwitchPlayer   music switch container: child picked by state/switch values, transitions
  //                  on change following the container's rules (sync point, fades, entry type)
  //   PlaylistPlayer music playlist container: segments back to back at their exit cues
  //   SegmentPlayer  music segment: its tracks' clips, timed from the segment's entry cue
  // Everything is scheduled on the AudioContext clock; a timer queues the next segment shortly
  // before the current one reaches its exit cue.

  const ENTRY_CUE = 43573010;
  const EXIT_CUE = 1539036744;
  const LOOKAHEAD = 2.5;        // s before an exit cue to schedule the next segment
  const encoded = new Map();    // media id -> Promise<ArrayBuffer> (music prefetched, not decoded)

  function prefetchEncoded(id) {
    const m = media[id];
    if (!m || m.pack || encoded.has(id) || buffers.has(id)) return;
    const p = fetchBuffer(m.file);
    p.catch(() => encoded.delete(id));
    encoded.set(id, p);
  }

  // Music media under a node (first segments only for playlists: enough to start any child fast).
  function musicMediaOf(id, out, depth) {
    const n = nodes[id];
    if (!n || depth > 8) return out;
    if (n.type === 'mtrack') {
      for (const s of n.sources) out.add(s);
    } else if (n.type === 'mseg' || n.type === 'mswitch') {
      for (const c of n.children || []) musicMediaOf(c, out, depth + 1);
    } else if (n.type === 'mranseq') {
      const first = n.playlist.find((it) => it.nchildren === 0);
      if (first) musicMediaOf(first.segment, out, depth + 1);
    }
    return out;
  }

  // Linear ramp of an AudioParam from its value at t0 to `to` at t1.
  function ramp(param, t0, t1, to) {
    t0 = Math.max(t0, ctx.currentTime);
    t1 = Math.max(t1, t0 + 0.005);
    param.cancelScheduledValues(t0);
    param.setValueAtTime(param.value, t0);
    param.linearRampToValueAtTime(to, t1);
  }

  // A fade from `from` to `to` over [begin, end] following an AkCurveInterpolation shape. A fade that
  // began in the past continues from where it would be now.
  function fadeCurve(param, begin, end, from, to, curve) {
    const now = ctx.currentTime;
    const start = Math.max(begin, now);
    if (start > now) {
      param.cancelScheduledValues(now);
      param.setValueAtTime(from, now); // hold the starting value until the fade begins
    }
    param.cancelScheduledValues(start);
    if (end - start < 0.005) {
      param.setValueAtTime(to, start);
      return;
    }
    const dur = Math.max(end - begin, 0.005);
    const n = 32;
    const values = new Float32Array(n);
    for (let i = 0; i < n; i++) {
      const x = clamp((start + ((end - start) * i) / (n - 1) - begin) / dur, 0, 1);
      values[i] = from + (to - from) * interp(0, 1, x, curve === undefined ? 4 : curve);
    }
    param.setValueCurveAtTime(values, start, end - start);
  }

  function makePlayer(id, inst) {
    const n = nodes[id];
    if (!n) return null;
    if (n.type === 'mseg') return new SegmentPlayer(id, inst);
    if (n.type === 'mranseq') return new PlaylistPlayer(id, inst);
    if (n.type === 'mswitch') return new SwitchPlayer(id, inst);
    return null;
  }

  class SegmentPlayer {
    constructor(id, inst) {
      this.id = id;
      this.inst = inst;
      this.n = nodes[id];
      const marks = this.n.markers || [];
      const cue = (mid) => {
        const m = marks.find((k) => k[0] === mid);
        return m ? m[1] : undefined;
      };
      this.entry = cue(ENTRY_CUE) || 0;
      const exit = cue(EXIT_CUE);
      this.length = Math.max((exit === undefined ? this.n.duration : exit) - this.entry, 0) / 1000;
      this.fade = ctx.createGain();
      this.fade.connect(inst.out(outputBusOf(chainOf(id))));
      this.clips = [];
      this.end = 0;
    }

    prepare() {
      for (const tid of this.n.children || []) {
        const t = nodes[tid];
        if (t && t.type === 'mtrack') for (const c of t.clips) getBuffer(c[1]).catch(() => {});
      }
    }

    path() {
      return [this.id];
    }

    current() {
      return this;
    }

    // Plays so that the entry cue, plus `offset` seconds, is at audio time t.
    start(t, offset) {
      this.t0 = t - (offset || 0);
      this.exitTime = this.t0 + this.length;
      for (const tid of this.n.children || []) {
        const track = nodes[tid];
        if (!track || track.type !== 'mtrack') continue;
        for (const c of track.clips) this.startClip(tid, track, c, t);
      }
    }

    startClip(tid, track, c, notBefore) {
      const [, mediaId, playAt, beginTrim, endTrim, srcDur] = c;
      const clip = { tid, mediaId, src: null, gain: null, ended: false };
      const from = (playAt + beginTrim - this.entry) / 1000; // relative to the entry cue
      const to = (playAt + srcDur + endTrim - this.entry) / 1000;
      this.end = Math.max(this.end, to);
      this.clips.push(clip);
      const go = (buf) => {
        const now = ctx.currentTime;
        if (this.stopTime !== undefined && this.stopTime <= now) return;
        let at = this.t0 + from;
        let offset = beginTrim / 1000;
        const earliest = Math.max(notBefore, now);
        if (at < earliest) { // late (still decoding) or entering mid-segment: skip ahead, stay in sync
          offset += earliest - at;
          at = earliest;
        }
        const until = this.t0 + to;
        if (at >= until || offset >= buf.duration) return;
        if (this.stopTime !== undefined && at >= this.stopTime) return;
        const src = ctx.createBufferSource();
        src.buffer = buf;
        const g = ctx.createGain();
        src.connect(g);
        g.connect(this.fade);
        g.gain.value = dbToGain(this.volume(tid));
        this.automate(track, g, this.t0 + (playAt - this.entry) / 1000, at);
        src.start(at, offset, until - at);
        if (this.stopTime !== undefined) src.stop(Math.max(this.stopTime, at));
        const e = buffers.get(mediaId);
        if (e) e.refs++;
        src.onended = () => {
          g.disconnect();
          if (e) e.refs--;
          clip.ended = true;
        };
        clip.src = src;
        clip.gain = g;
      };
      const e = buffers.get(mediaId);
      if (e && e.buffer) {
        e.last = performance.now();
        go(e.buffer);
      } else {
        getBuffer(mediaId).then(go, () => {});
      }
    }

    // Clip fade-in (3) / fade-out (4) automation; x in seconds from the clip start, y a gain factor.
    automate(track, g, clipStart, at) {
      if (!track.clip_autos) return;
      const base = g.gain.value;
      for (const [, type, points] of track.clip_autos) {
        if (type !== 3 && type !== 4) continue;
        for (const [x, y] of points) {
          const t = clipStart + x;
          if (t <= at) g.gain.setValueAtTime(base * y, at);
          else g.gain.linearRampToValueAtTime(base * y, t);
        }
      }
    }

    volume(tid) {
      const acc = newAcc();
      for (const id of chainOf(tid)) accumulate(acc, id, this.inst.obj);
      return acc.vol + acc.makeup;
    }

    update(fade) {
      for (const c of this.clips) {
        if (c.gain && !c.ended) approach(c.gain.gain, dbToGain(this.volume(c.tid)), fade, 0.02);
      }
    }

    fadeTo(t0, t1, from, to, curve) {
      fadeCurve(this.fade.gain, t0, t1, from, to, curve);
    }

    stop(t) {
      t = Math.max(t, ctx.currentTime);
      if (this.stopTime !== undefined && this.stopTime <= t) return;
      this.stopTime = t;
      for (const c of this.clips) {
        if (c.src && !c.ended) {
          try {
            c.src.stop(t);
          } catch (e) { /* already stopped */ }
        }
      }
    }

    tick() {}

    get done() {
      if (this.t0 === undefined) return this.stopTime !== undefined;
      const end = this.t0 + this.end;
      return ctx.currentTime > (this.stopTime !== undefined ? Math.min(this.stopTime, end) : end) + 0.1;
    }

    dispose() {
      this.fade.disconnect();
    }
  }

  // Flattened playlist [{segment, nchildren, rs_type, loop, weight, ...}] -> tree.
  function playlistTree(items) {
    let i = 0;
    const rec = () => {
      const it = items[i++];
      const node = Object.assign({ children: [] }, it);
      for (let k = 0; k < it.nchildren && i < items.length; k++) node.children.push(rec());
      return node;
    };
    return items.length ? rec() : null;
  }

  // Yields segment ids. rs_type: 0 continuous sequence, 1 step sequence, 2 continuous random,
  // 3 step random. loop: 0 = forever.
  function* playlistSegments(node, state) {
    if (node.nchildren === 0) {
      for (let k = 0; node.loop === 0 || k < node.loop; k++) yield node.segment;
      return;
    }
    const kids = node.children;
    if (!kids.length) return;
    let st = state.get(node);
    if (!st) {
      st = { pos: 0, history: [], bag: null };
      state.set(node, st);
    }
    const asContainer = {
      playlist: kids.map((c, idx) => [idx, c.weight]),
      avoid: node.avoid_repeat, random: node.shuffle ? 1 : 0, bits: node.use_weight ? 1 : 0,
    };
    for (let round = 0; node.loop === 0 || round < node.loop; round++) {
      if (node.rs_type === 0) {
        for (const c of kids) yield* playlistSegments(c, state);
      } else if (node.rs_type === 1) {
        yield* playlistSegments(kids[st.pos++ % kids.length], state);
      } else {
        const count = node.rs_type === 2 ? kids.length : 1;
        for (let k = 0; k < count; k++) yield* playlistSegments(kids[pickRandom(asContainer, st)], state);
      }
    }
  }

  class PlaylistPlayer {
    constructor(id, inst) {
      this.id = id;
      this.inst = inst;
      this.n = nodes[id];
      const tree = playlistTree(this.n.playlist);
      this.iter = tree ? playlistSegments(tree, new Map()) : [][Symbol.iterator]();
      this.first = null;
      this.cur = null;
      this.queued = null;
      this.past = [];
      this.finished = false;
    }

    nextSegment() {
      for (let guard = 0; guard < 64; guard++) {
        const r = this.iter.next();
        if (r.done) return null;
        const n = nodes[r.value];
        if (n && n.type === 'mseg') {
          const p = new SegmentPlayer(r.value, this.inst);
          if (p.length > 0) return p;
          p.dispose();
        }
      }
      return null;
    }

    prepare() {
      if (!this.cur && !this.first) this.first = this.nextSegment();
      if (this.first) this.first.prepare();
    }

    path() {
      return [this.id].concat(this.cur ? this.cur.path() : []);
    }

    current() {
      return this.cur;
    }

    start(t, offset) {
      const seg = this.first || this.nextSegment();
      this.first = null;
      if (!seg) {
        this.finished = true;
        return;
      }
      this.cur = seg;
      seg.start(t, offset);
      this.queued = this.nextSegment();
      if (this.queued) this.queued.prepare();
    }

    tick(now) {
      if (this.stopped || !this.cur) return;
      if (this.queued && this.cur.exitTime - now < LOOKAHEAD) {
        const seg = this.queued;
        seg.start(Math.max(this.cur.exitTime, now), 0);
        this.past.push(this.cur);
        this.cur = seg;
        this.queued = this.nextSegment();
        if (this.queued) this.queued.prepare();
      }
      this.past = this.past.filter((p) => {
        if (!p.done) return true;
        p.dispose();
        return false;
      });
      if (!this.queued && this.cur.done) this.finished = true;
    }

    active() {
      return this.past.concat(this.cur ? [this.cur] : []);
    }

    fadeTo(t0, t1, from, to, curve) {
      for (const p of this.active()) p.fadeTo(t0, t1, from, to, curve);
    }

    stop(t) {
      this.stopped = true;
      for (const p of this.active()) p.stop(t);
      for (const p of [this.queued, this.first]) if (p) p.dispose();
      this.queued = this.first = null;
    }

    update(fade) {
      for (const p of this.active()) p.update(fade);
    }

    get done() {
      return this.finished || (this.stopped && this.active().every((p) => p.done));
    }

    dispose() {
      for (const p of this.active()) p.dispose();
      for (const p of [this.queued, this.first]) if (p) p.dispose();
    }
  }

  class SwitchPlayer {
    constructor(id, inst) {
      this.id = id;
      this.inst = inst;
      this.n = nodes[id];
      this.child = null;
      this.childId = 0;
      this.old = [];
    }

    // Walks the decision tree with the current state/switch values; returns a node id or 0.
    // Inner tree nodes hold (first child index | child count << 16), leaves a node id; key 0 = any.
    resolve() {
      const tree = this.n.tree;
      if (!tree || !tree.length) return 0;
      let idx = 0;
      for (const [group, type] of this.n.tree_groups) {
        const val = tree[idx][1];
        const first = val & 0xffff, count = val >>> 16;
        let value;
        if (type === 1) {
          value = stateValues.get(group);
        } else {
          const m = switchValues.get(this.inst.obj);
          value = m && m.get(group);
        }
        let found = -1, fallback = -1;
        for (let i = first; i < first + count && i < tree.length; i++) {
          if (value !== undefined && tree[i][0] === value) found = i;
          if (tree[i][0] === 0) fallback = i;
        }
        idx = found >= 0 ? found : fallback;
        if (idx < 0) return 0;
      }
      const leaf = tree[idx][1];
      return nodes[leaf] ? leaf : 0;
    }

    prepare() {}

    path() {
      return [this.id].concat(this.child ? this.child.path() : []);
    }

    current() {
      return this.child ? this.child.current() : null;
    }

    // The first child fades in as the transition rules say for entering it from nothing, from the
    // start (the rule's offset only applies to real transitions) - matches Wwise in the audiodiff.
    start(t, offset) {
      this.childId = this.resolve();
      this.child = this.childId ? makePlayer(this.childId, this.inst) : null;
      if (!this.child) return;
      this.child.prepare();
      this.child.start(t, offset);
      const rule = this.rule([], this.childId);
      const df = rule && rule.dst_rule.fade.time > 0 ? rule.dst_rule.fade : null;
      if (df) this.child.fadeTo(t, t + df.time / 1000, 0, 1, df.curve);
    }

    onGroupChange(group) {
      if (this.child && this.child.onGroupChange) this.child.onGroupChange(group);
      if (this.stopped || !this.n.tree_groups.some((g) => g[0] === group)) return;
      const id = this.resolve();
      if (id !== this.childId) this.transition(id);
    }

    // Later rules are more specific than the first ([-1] -> [-1], any to any); the last match wins.
    rule(srcPath, dstId) {
      const rules = this.n.rules || [];
      for (let i = rules.length - 1; i >= 0; i--) {
        const r = rules[i];
        const srcOk = r.src.includes(-1) || r.src.some((s) => srcPath.includes(s >>> 0));
        const dstOk = r.dst.includes(-1) || r.dst.some((d) => (d >>> 0) === dstId);
        if (srcOk && dstOk) return r;
      }
      return null;
    }

    // AkSyncType: 0 immediate, 1 next grid, 2 next bar, 3 next beat, 4..7 next cue / exit cue.
    syncTime(sync, now) {
      const seg = this.child && this.child.current();
      if (!seg || seg.t0 === undefined || sync === 0) return now;
      const n = seg.n;
      const beat = (60 / (n.tempo || 120)) * (4 / ((n.sig && n.sig[1]) || 4));
      let unit, origin = 0;
      if (sync === 1) {
        unit = (n.grid ? n.grid[0] : 1000) / 1000;
        origin = (n.grid ? n.grid[1] : 0) / 1000;
      } else if (sync === 2) {
        unit = beat * ((n.sig && n.sig[0]) || 4);
      } else if (sync === 3) {
        unit = beat;
      } else {
        return Math.max(seg.exitTime, now);
      }
      if (!(unit > 0)) return now;
      const k = Math.ceil((now - seg.t0 - origin) / unit - 1e-6);
      return Math.max(seg.t0 + origin + k * unit, now);
    }

    transition(dstId) {
      const now = ctx.currentTime + 0.03;
      const src = this.child;
      const rule = this.rule(src ? src.path() : [], dstId);
      const sr = rule ? rule.src_rule : { fade: { time: 0, offset: 0 }, sync: 0 };
      const dr = rule ? rule.dst_rule : { fade: { time: 0, offset: 0 }, entry: 0 };
      let ts = this.syncTime(sr.sync, now);
      if (sr.sync === 0 || !src) {
        // immediate: late enough that neither fade has to begin in the past (Wwise does the same)
        const lead = Math.max(0, dr.fade.time > 0 ? -dr.fade.offset / 1000 : 0,
          src && sr.fade.time > 0 ? (sr.fade.time - sr.fade.offset) / 1000 : 0);
        ts = Math.max(ts, now + lead);
      }
      let entryOffset = 0;
      if (src) {
        const seg = src.current();
        if (dr.entry === 1 && seg && seg.t0 !== undefined) entryOffset = ts - seg.t0; // same time
        const end = ts + sr.fade.offset / 1000;
        if (sr.fade.time > 0) src.fadeTo(end - sr.fade.time / 1000, end, 1, 0, sr.fade.curve);
        src.stop(Math.max(end, now));
        this.old.push(src);
      }
      this.childId = dstId;
      this.child = dstId ? makePlayer(dstId, this.inst) : null;
      if (this.child) {
        this.child.prepare();
        this.child.start(ts, entryOffset);
        if (dr.fade.time > 0) {
          const begin = ts + dr.fade.offset / 1000;
          this.child.fadeTo(begin, begin + dr.fade.time / 1000, 0, 1, dr.fade.curve);
        }
      }
    }

    tick(now) {
      if (this.child) this.child.tick(now);
      this.old = this.old.filter((p) => {
        p.tick(now);
        if (!p.done) return true;
        p.dispose();
        return false;
      });
    }

    active() {
      return this.old.concat(this.child ? [this.child] : []);
    }

    fadeTo(t0, t1, from, to, curve) {
      for (const p of this.active()) p.fadeTo(t0, t1, from, to, curve);
    }

    stop(t) {
      this.stopped = true;
      for (const p of this.active()) p.stop(t);
    }

    update(fade) {
      for (const p of this.active()) p.update(fade);
    }

    get done() {
      // a switch with nothing to play keeps waiting for a state change until it is stopped
      return this.stopped ? this.active().every((p) => p.done) : !!this.child && this.child.done;
    }

    dispose() {
      for (const p of this.active()) p.dispose();
    }
  }

  class MusicInstance {
    constructor(id, pc) {
      this.id = id;
      this.chain = chainOf(id);
      this.obj = pc.obj;
      this.pid = pc.pid;
      this.event = pc.event;
      this.outs = new Map();
      this.stopped = false;
      this.root = makePlayer(id, this);
      voices.add(this);
      Music.instances.add(this);
    }

    out(busId) {
      let g = this.outs.get(busId);
      if (!g) {
        g = ctx.createGain();
        g.connect(busInput(busId));
        this.outs.set(busId, g);
      }
      return g;
    }

    start(delay, fadeInMs) {
      for (const m of musicMediaOf(this.id, new Set(), 0)) prefetchEncoded(m);
      this.root.prepare();
      const t = ctx.currentTime + 0.05 + delay;
      this.root.start(t, 0);
      if (fadeInMs > 0) {
        for (const g of this.outs.values()) {
          g.gain.setValueAtTime(0, t);
          g.gain.linearRampToValueAtTime(1, t + fadeInMs / 1000);
        }
      }
    }

    matches(target) {
      return this.chain.includes(target) || this.root.path().includes(target);
    }

    stop(fadeMs) {
      if (this.stopped) return;
      this.stopped = true;
      const now = ctx.currentTime;
      const fade = Math.max((fadeMs || 0) / 1000, 0.01);
      for (const g of this.outs.values()) ramp(g.gain, now, now + fade, 0);
      this.root.stop(now + fade);
    }

    pause() {}
    resume() {}

    update(fade) {
      this.root.update(fade);
    }

    tick(now) {
      this.root.tick(now);
      if (this.root.done) this.finish();
    }

    finish() {
      if (this.finished) return;
      this.finished = true;
      this.stopped = true;
      voices.delete(this);
      Music.instances.delete(this);
      this.root.dispose();
      for (const g of this.outs.values()) g.disconnect();
    }
  }

  const Music = {
    instances: new Set(),

    play(id, pc) {
      const inst = new MusicInstance(id, pc);
      if (!inst.root) {
        inst.finish();
        return;
      }
      if (pc.collect) pc.collect.push(inst);
      inst.start(pc.delay || 0, pc.fadeIn || 0);
    },

    tick() {
      const now = ctx.currentTime;
      for (const inst of [...Music.instances]) inst.tick(now);
    },

    onStateChange(group) {
      for (const inst of Music.instances) {
        if (!inst.stopped && inst.root.onGroupChange) inst.root.onGroupChange(group);
      }
    },

    onSwitchChange(group, value, obj) {
      for (const inst of Music.instances) {
        if (!inst.stopped && inst.obj === obj && inst.root.onGroupChange) inst.root.onGroupChange(group);
      }
    },

    trigger() {}, // EtG's music has no stingers
  };

  // --- public API (called from EtgWebShim.jslib) -------------------------------------------

  const api = {
    hash,
    get ready() { return !!data; },
    get context() { return ctx; },
    // Test only (port/audio/audiodiff.html): renders a scripted case on an OfflineAudioContext and
    // returns the stereo mix as [left, right]. spec: {duration, rate, seed, listener: [x, y, z],
    // steps: [{t, op, args}]} where op is a method of this API. Replaces the live context and resets
    // all engine state for every call.
    async render(spec) {
      await Promise.all(packs);
      const rate = spec.rate || 48000;
      offline = new OfflineAudioContext(2, Math.ceil(spec.duration * rate), rate);
      ctx = offline;
      for (const m of [voices, Music.instances, stateValues, switchValues, rtpcGlobal, rtpcObj, objects, seqState, modifiers, busses]) m.clear();
      timers = [];
      buildBusses();
      random = mulberry32(spec.seed || 1);
      [listener.x, listener.y, listener.z] = spec.listener || [0, 0, 0];
      const steps = spec.steps.slice().sort((a, b) => a.t - b.t);
      const quantum = 1024 / rate;
      const settle = async () => {
        while (inflight.size) await Promise.all([...inflight].map((p) => p.catch(() => {})));
        await null;
      };
      const runDue = async (now) => {
        while (steps.length && steps[0].t <= now + 1e-9) {
          const st = steps.shift();
          api[st.op](...st.args);
        }
        timers.sort((a, b) => a.t - b.t);
        while (timers.length && timers[0].t <= now + 1e-9) timers.shift().fn();
        Music.tick();
        hdrTick();
        await settle();
      };
      const stepAt = (time) => {
        offline.suspend(time).then(async () => {
          await runDue(time);
          if (time + quantum < spec.duration - quantum) stepAt(time + quantum);
          offline.resume();
        });
      };
      await runDue(0);
      if (spec.before) spec.before(offline, { busses, masterIn }); // test hook: tap or feed the graph
      stepAt(quantum);
      const out = await offline.startRendering();
      return [out.getChannelData(0), out.getChannelData(1)];
    },

    // Peak level of the mixed output over the last ~43 ms (0..1); for tests.
    level() {
      if (!analyser) return 0;
      const buf = new Float32Array(analyser.fftSize);
      analyser.getFloatTimeDomainData(buf);
      let peak = 0;
      for (const v of buf) peak = Math.max(peak, Math.abs(v));
      return peak;
    },
    debug: { voices, busses, stateValues, switchValues, rtpcGlobal, objects, listener, buffers, nodes: () => nodes, events: () => events, names: () => names },

    // version: build id from version.txt (cache busting); optional
    init(baseUrl, buildVersion) {
      if (ctx) return;
      base = baseUrl.endsWith('/') ? baseUrl : baseUrl + '/';
      version = buildVersion || '';
      ctx = new (window.AudioContext || window.webkitAudioContext)();
      const unlock = () => {
        if (offline) return;
        if (ctx.state !== 'running') ctx.resume();
        if (!unlocked && ctx.state === 'running') {
          unlocked = true;
        }
      };
      for (const ev of ['pointerdown', 'keydown', 'touchstart', 'mousedown', 'gamepadconnected']) {
        window.addEventListener(ev, unlock, true);
      }
      fetchBuffer('audio.json')
        .then((b) => JSON.parse(new TextDecoder().decode(b)))
        .then((json) => {
          data = json;
          nodes = json.nodes;
          events = json.events;
          media = json.media;
          names = json.names || {};
          if (json.settings) {
            maxVoices = json.settings.max_voices || maxVoices;
            if (json.settings.volume_threshold !== undefined) volumeThreshold = json.settings.volume_threshold;
          }
          buildBusses();
          loadPacks();
          setInterval(() => { if (!offline) Music.tick(); }, 100);
          setInterval(() => { if (!offline) hdrTick(); }, 25);
          console.log(LOG, 'ready:', Object.keys(events).length, 'events,', Object.keys(nodes).length, 'nodes');
          onLoaded();
        })
        .catch((e) => console.error(LOG, 'init failed', e));
    },

    // pid: playing ID handed out by the caller (the jslib bridge), or 0 for a new one
    postEvent(eventId, obj, x, y, z, pid) {
      eventId = eventId >>> 0;
      obj = objKey(obj);
      const actions = events[eventId];
      if (!actions) {
        warnOnce('event' + eventId, 'unknown event', nameOf(eventId));
        return 0;
      }
      if (x !== undefined && obj !== -1) {
        objects.set(obj, { x, y, z });
      }
      pid = pid ? pid >>> 0 : nextPlayingId++;
      for (const a of actions) {
        runAction(a, obj, pid, eventId);
      }
      return pid;
    },

    setSwitch(group, value, obj) {
      setSwitch(group >>> 0, value >>> 0, objKey(obj));
    },

    setState(group, state) {
      setState(group >>> 0, state >>> 0);
    },

    setRtpc(id, value, obj, fadeMs) {
      setRtpc(id >>> 0, value, objKey(obj), fadeMs || 0);
    },

    resetRtpc(id, obj) {
      resetRtpc(id >>> 0, objKey(obj));
    },

    stopAll(obj) {
      obj = objKey(obj);
      for (const v of [...voices]) {
        if (obj === -1 || v.obj === obj) v.stop(0);
      }
    },

    stopPlayingId(pid, fadeMs) {
      pid = pid >>> 0;
      for (const v of [...voices]) {
        if (v.pid === pid) v.stop(fadeMs || 0);
      }
    },

    // AkActionOnEventType: 0 Stop, 1 Pause, 2 Resume, 3 Break, 4 ReleaseEnvelope
    actionOnEvent(eventId, type, obj, fadeMs, pid) {
      eventId = eventId >>> 0;
      obj = objKey(obj);
      pid = pid >>> 0;
      const actions = events[eventId];
      if (!actions) return;
      for (const v of [...voices]) {
        if (v.event !== eventId) continue;
        if (obj !== -1 && v.obj !== obj) continue;
        if (pid && v.pid !== pid) continue;
        if (type === 0 || type === 3) v.stop(fadeMs || 0);
        else if (type === 1) v.pause();
        else if (type === 2) v.resume();
      }
    },

    // Returns 1 while the object has voices that follow its position.
    setPosition(obj, x, y, z) {
      if (!data) return 0;
      obj = objKey(obj);
      const o = objects.get(obj);
      if (o) {
        o.x = x; o.y = y; o.z = z;
      } else {
        objects.set(obj, { x, y, z });
      }
      let any = 0;
      for (const v of voices) {
        if (v.obj === obj && v.is3d) {
          v.update(0);
          any = 1;
        }
      }
      return any;
    },

    setListener(x, y, z) {
      listener.x = x; listener.y = y; listener.z = z;
      if (!data) return;
      for (const v of voices) {
        if (v.is3d) v.update(0);
      }
    },

    postTrigger(trigger, obj) {
      Music.trigger(trigger >>> 0, objKey(obj));
    },

    unregister(obj) {
      objects.delete(objKey(obj));
      switchValues.delete(objKey(obj));
      rtpcObj.delete(objKey(obj));
    },
  };

  // Calls made before audio.json is loaded are replayed once it is.
  const pending = [];
  for (const name of ['postEvent', 'setSwitch', 'setState', 'setRtpc', 'resetRtpc', 'stopAll', 'stopPlayingId',
    'actionOnEvent', 'postTrigger', 'unregister']) {
    const fn = api[name];
    api[name] = function () {
      if (data) return fn.apply(api, arguments);
      if (pending.length < 4096) pending.push([fn, arguments]);
      return 0;
    };
  }
  onLoaded = () => {
    for (const [fn, args] of pending) fn.apply(api, args);
    pending.length = 0;
  };

  window.EtgAudio = api;
})();
