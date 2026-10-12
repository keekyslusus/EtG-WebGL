// Code of the page around the WebGL build (port/site -> webbuild): pixel icons, the game's
// pause-screen sprite animations, save export/import straight from the IndexedDB that Unity's
// IDBFS keeps, fullscreen, and the UnityLoader glue.
// Plain ES5-ish browser JS, no dependencies.
(function () {
  "use strict";

  var Etg = {};
  window.Etg = Etg;
  Etg.assets = "assets/";

  // ------------------------------------------------------------------ pixel icons

  var ICONS = {
    fullscreen: [
      "###.....###",
      "#.........#",
      "#.........#",
      "...........",
      "...........",
      "...........",
      "...........",
      "...........",
      "#.........#",
      "#.........#",
      "###.....###",
    ],
    windowed: [
      "..#.....#..",
      "..#.....#..",
      "###.....###",
      "...........",
      "...........",
      "...........",
      "...........",
      "...........",
      "###.....###",
      "..#.....#..",
      "..#.....#..",
    ],
    export: [
      ".....#.....",
      ".....#.....",
      ".....#.....",
      ".....#.....",
      "..#..#..#..",
      "...#.#.#...",
      "....###....",
      ".....#.....",
      "#.........#",
      "#.........#",
      "###########",
    ],
    import: [
      ".....#.....",
      "....###....",
      "...#.#.#...",
      "..#..#..#..",
      ".....#.....",
      ".....#.....",
      ".....#.....",
      ".....#.....",
      "#.........#",
      "#.........#",
      "###########",
    ],
    play: [
      "..#........",
      "..##.......",
      "..###......",
      "..####.....",
      "..#####....",
      "..######...",
      "..#####....",
      "..####.....",
      "..###......",
      "..##.......",
      "..#........",
    ],
  };

  /** Inline SVG of a pixel icon, drawn in currentColor, `px` CSS pixels per icon pixel. */
  Etg.icon = function (name, px) {
    var rows = ICONS[name];
    var d = "";
    for (var y = 0; y < rows.length; y++) {
      var row = rows[y];
      for (var x = 0; x < row.length; x++) {
        if (row[x] !== "#") continue;
        var run = 1;
        while (row[x + run] === "#") run++;
        d += "M" + x + " " + y + "h" + run + "v1h-" + run + "z";
        x += run - 1;
      }
    }
    var w = rows[0].length, h = rows.length;
    px = px || 2;
    return '<svg class="etg-icon" aria-hidden="true" width="' + w * px + '" height="' + h * px +
      '" viewBox="0 0 ' + w + " " + h + '" shape-rendering="crispEdges"><path fill="currentColor" d="' + d + '"/></svg>';
  };

  /** Fills every [data-icon] element under root with its icon. */
  Etg.fillIcons = function (root) {
    var els = (root || document).querySelectorAll("[data-icon]");
    for (var i = 0; i < els.length; i++) {
      els[i].innerHTML = Etg.icon(els[i].getAttribute("data-icon"), +els[i].getAttribute("data-px") || 2);
    }
  };

  // ------------------------------------------------------------------ images

  function loadImage(src) {
    return new Promise(function (resolve, reject) {
      var img = new Image();
      img.onload = function () { resolve(img); };
      img.onerror = function () { reject(new Error("Could not load " + src)); };
      img.src = src;
    });
  }

  function loadJson(src) {
    return fetch(src).then(function (r) {
      if (!r.ok) throw new Error("Could not load " + src);
      return r.json();
    });
  }

  /** Largest integer scale (>= 1) at which a w x h picture fits into maxW x maxH. */
  Etg.fitScale = function (w, h, maxW, maxH) {
    return Math.max(1, Math.floor(Math.min(maxW / w, maxH / h)));
  };

  // ------------------------------------------------------------------ sprite animations

  // dfSpriteAnimation.Execute: loopType 0 Once, 1 Loop, 2 PingPong (unused here), 3 LoopSection
  // (play everything, then loop from frame loopFrom; frames before it run at loopFirstLength).
  function stepAnim(a, dt) {
    var n = a.frames.length;
    if (a.done) return n - 1;
    if (a.delay > 0) {
      a.delay -= dt;
      return frameAt(a);
    }
    var test = Math.floor(clamp01(a.elapsed / a.length) * n);
    if (a.loopType === 3 && a.loopFirstLength > 0 && test < a.loopFrom) {
      a.elapsed += dt * a.length / a.loopFirstLength;
    } else {
      a.elapsed += dt;
    }
    if (a.elapsed >= a.length) {
      var delay = a.loopDelay[1] > 0 ? a.loopDelay[0] + Math.random() * (a.loopDelay[1] - a.loopDelay[0]) : 0;
      if (a.loopType === 0) {
        a.done = true;
        return n - 1;
      } else if (a.loopType === 3) {
        a.elapsed = a.loopFrom / n * a.length;
        a.delay = delay;
        return a.loopFrom;
      } else {
        a.elapsed = 0;
        a.delay = delay;
        return 0;
      }
    }
    return frameAt(a);
  }

  function frameAt(a) {
    return Math.min(a.frames.length - 1, Math.floor(clamp01(a.elapsed / a.length) * a.frames.length));
  }

  function clamp01(v) {
    return v < 0 ? 0 : v > 1 ? 1 : v;
  }

  /** Plays one animation of pause_anim.json (by name) on its own canvas, looping its loop section. */
  Etg.spriteAnim = function (canvas, name) {
    return Promise.all([loadImage(Etg.assets + "pause_anim.png"), loadJson(Etg.assets + "pause_anim.json")]).then(function (r) {
      var sheet = r[0];
      var a = r[1].anims.filter(function (x) { return x.name === name; })[0];
      var w = 0, h = 0;
      a.frames.forEach(function (f) { w = Math.max(w, f[2]); h = Math.max(h, f[3]); });
      canvas.width = w;
      canvas.height = h;
      a.elapsed = 0;
      a.delay = 0;
      a.done = false;
      if (a.loopType === 0) a.loopType = 1;
      var g = canvas.getContext("2d");
      var last = performance.now(), running = true;
      function tick(now) {
        if (!running) return;
        var f = a.frames[stepAnim(a, Math.min(0.1, (now - last) / 1000))];
        last = now;
        g.clearRect(0, 0, w, h);
        g.drawImage(sheet, f[0], f[1], f[2], f[3], 0, h - f[3], f[2], f[3]);
        requestAnimationFrame(tick);
      }
      requestAnimationFrame(tick);
      return { width: w, height: h, stop: function () { running = false; } };
    });
  };

  // ------------------------------------------------------------------ fullscreen

  // Fullscreen is always the whole page (so the toolbar stays usable), also when the game asks
  // for it: see the canvas hook in startGame.
  Etg.isFullscreen = function () {
    return !!(document.fullscreenElement || document.webkitFullscreenElement);
  };

  Etg.enterFullscreen = function () {
    if (Etg.isFullscreen()) return;
    var el = document.documentElement;
    var p = (el.requestFullscreen || el.webkitRequestFullscreen).call(el, { navigationUI: "hide" });
    if (p && p.catch) p.catch(function () {});
  };

  Etg.exitFullscreen = function () {
    if (!Etg.isFullscreen()) return;
    var p = (document.exitFullscreen || document.webkitExitFullscreen).call(document);
    if (p && p.catch) p.catch(function () {});
  };

  Etg.toggleFullscreen = function () {
    if (Etg.isFullscreen()) Etg.exitFullscreen();
    else Etg.enterFullscreen();
  };

  Etg.onFullscreenChange = function (fn) {
    document.addEventListener("fullscreenchange", fn);
    document.addEventListener("webkitfullscreenchange", fn);
  };

  // Esc is the game's pause key: in fullscreen, keep it for the game (Chrome/Edge then show
  // "press and hold Esc to exit"); elsewhere Esc simply leaves fullscreen.
  Etg.onFullscreenChange(function () {
    var kb = navigator.keyboard;
    if (!kb || !kb.lock) return;
    if (Etg.isFullscreen()) kb.lock(["Escape"]).catch(function () {});
    else kb.unlock();
  });

  // ------------------------------------------------------------------ saves (IDBFS)

  // Unity mounts IDBFS at /idbfs: database "/idbfs", object store FILE_DATA, keyed by full path,
  // values {timestamp: Date, mode, contents?: Uint8Array}. Application.persistentDataPath is
  // /idbfs/<md5 of the page URL up to (not incl.) its last "/">: pages in one folder share saves.
  var IDB_NAME = "/idbfs", IDB_STORE = "FILE_DATA", IDB_VERSION = 21;
  var S_IFMT = 61440, S_IFDIR = 16384, S_IFREG = 32768;
  // The game's own files: SlotA.save, SlotA.options, SlotA.backup.90h43m, ... and Unity's
  // PlayerPrefs. A Steam save folder (LocalLow/Dodge Roll/Enter the Gungeon) also has
  // steam_autocloud.vdf, which is skipped.
  var SAVE_NAME = /^(Slot[A-Z]\.(save|options|backup\.[\w.]+)|PlayerPrefs)$/i;

  function openDb() {
    return new Promise(function (resolve, reject) {
      var req = indexedDB.open(IDB_NAME, IDB_VERSION);
      req.onupgradeneeded = function () {
        var db = req.result;
        var store = db.objectStoreNames.contains(IDB_STORE)
          ? req.transaction.objectStore(IDB_STORE)
          : db.createObjectStore(IDB_STORE);
        if (!store.indexNames.contains("timestamp")) {
          store.createIndex("timestamp", "timestamp", { unique: false });
        }
      };
      req.onsuccess = function () { resolve(req.result); };
      req.onerror = function () { reject(req.error); };
      // The game holds the database open at the same version, so no blocked case in practice.
    });
  }

  function readAll(db) {
    return new Promise(function (resolve, reject) {
      var out = [];
      var req = db.transaction(IDB_STORE, "readonly").objectStore(IDB_STORE).openCursor();
      req.onsuccess = function () {
        var c = req.result;
        if (!c) return resolve(out);
        out.push({ path: c.key, entry: c.value });
        c.continue();
      };
      req.onerror = function () { reject(req.error); };
    });
  }

  function md5Hex(text) {
    var bytes = new TextEncoder().encode(text);
    return [].slice.call(UnityLoader.Cryptography.md5(bytes)).map(function (x) {
      return ("0" + x.toString(16)).substr(-2);
    }).join("");
  }

  /** Unity's persistentDataPath for this page: md5 of the page's folder URL, no trailing "/". */
  Etg.saveDir = function () {
    var url = location.href.split("?")[0].split("#")[0];
    return "/idbfs/" + md5Hex(url.substring(0, url.lastIndexOf("/")));
  };

  /**
   * The game's save files of this page: {dir, files: [{name, data: Uint8Array, time: Date}]}.
   * Only top-level files named like saves; the same folder also holds Unity's asset bundle cache.
   */
  Etg.listSaves = function () {
    return openDb().then(function (db) {
      return readAll(db).then(function (all) {
        db.close();
        var saves = all.filter(function (r) {
          return (r.entry.mode & S_IFMT) === S_IFREG && r.entry.contents &&
            /^\/idbfs\/[^/]+\/[^/]+$/.test(r.path) && SAVE_NAME.test(r.path.split("/").pop());
        });
        var dir = Etg.saveDir();
        var mine = saves.filter(function (r) { return r.path.indexOf(dir + "/") === 0; });
        if (!mine.length && saves.length) {
          // Saves made from another folder of the same origin (another port or path): take the
          // newest. Another host name or port is a different origin and can't be seen at all.
          var newest = saves.reduce(function (a, b) { return a.entry.timestamp > b.entry.timestamp ? a : b; });
          dir = newest.path.substring(0, newest.path.lastIndexOf("/"));
          mine = saves.filter(function (r) { return r.path.indexOf(dir + "/") === 0; });
        }
        return {
          dir: dir,
          files: mine.map(function (r) {
            return { name: r.path.substring(dir.length + 1), data: r.entry.contents, time: r.entry.timestamp };
          }),
        };
      });
    });
  };

  /** Downloads every save file as one .zip. Resolves to the number of files. */
  Etg.exportSaves = function () {
    return Etg.listSaves().then(function (saves) {
      if (!saves.files.length) throw new Error("No save files yet");
      var blob = zipStore(saves.files);
      var a = document.createElement("a");
      var d = new Date();
      a.download = "gungeon-save-" + d.getFullYear() + "-" + pad(d.getMonth() + 1) + "-" + pad(d.getDate()) + ".zip";
      a.href = URL.createObjectURL(blob);
      document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(function () { URL.revokeObjectURL(a.href); }, 10000);
      return saves.files.length;
    });
  };

  function pad(n) {
    return (n < 10 ? "0" : "") + n;
  }


  /**
   * Reads picked files (single save files and/or .zip archives of them) and returns
   * [{name, data}] of the save files found, without writing anything.
   */
  Etg.readSaveFiles = function (fileList) {
    var files = [].slice.call(fileList);
    return Promise.all(files.map(function (f) {
      return f.arrayBuffer().then(function (buf) {
        var bytes = new Uint8Array(buf);
        if (/\.zip$/i.test(f.name) || (bytes[0] === 0x50 && bytes[1] === 0x4b)) return unzip(bytes);
        return [{ name: f.name, data: bytes }];
      });
    })).then(function (lists) {
      var byName = {};
      [].concat.apply([], lists).forEach(function (e) {
        var name = e.name.split(/[\\/]/).pop();
        if (SAVE_NAME.test(name)) byName[name] = { name: name, data: e.data };
      });
      return Object.keys(byName).sort().map(function (k) { return byName[k]; });
    });
  };

  /**
   * Writes save files into this page's save folder in IndexedDB. The running game keeps its own
   * copy in memory and would overwrite them on its next save, so reload the page right after.
   */
  Etg.writeSaves = function (files) {
    var dir = Etg.saveDir();
    return openDb().then(function (db) {
      return new Promise(function (resolve, reject) {
        var tx = db.transaction(IDB_STORE, "readwrite");
        var store = tx.objectStore(IDB_STORE);
        var now = new Date();
        store.get(dir).onsuccess = function (e) {
          if (!e.target.result) store.put({ timestamp: now, mode: S_IFDIR | 511 }, dir);
          files.forEach(function (f) {
            store.put({ timestamp: now, mode: S_IFREG | 438, contents: f.data }, dir + "/" + f.name);
          });
        };
        tx.oncomplete = function () { db.close(); resolve(files.length); };
        tx.onerror = function () { db.close(); reject(tx.error); };
      });
    });
  };

  // ---- minimal zip: store-only writer, stored/deflate reader

  var CRC_TABLE = (function () {
    var t = new Uint32Array(256);
    for (var n = 0; n < 256; n++) {
      var c = n;
      for (var k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      t[n] = c >>> 0;
    }
    return t;
  })();

  function crc32(data) {
    var c = 0xffffffff;
    for (var i = 0; i < data.length; i++) c = CRC_TABLE[(c ^ data[i]) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
  }

  function zipStore(files) {
    var parts = [], central = [], offset = 0;
    files.forEach(function (f) {
      var name = new TextEncoder().encode(f.name);
      var crc = crc32(f.data);
      var t = f.time instanceof Date ? f.time : new Date();
      var dosTime = (t.getHours() << 11) | (t.getMinutes() << 5) | (t.getSeconds() >> 1);
      var dosDate = ((t.getFullYear() - 1980) << 9) | ((t.getMonth() + 1) << 5) | t.getDate();
      var local = new DataView(new ArrayBuffer(30));
      local.setUint32(0, 0x04034b50, true);
      local.setUint16(4, 20, true);
      local.setUint16(10, dosTime, true);
      local.setUint16(12, dosDate, true);
      local.setUint32(14, crc, true);
      local.setUint32(18, f.data.length, true);
      local.setUint32(22, f.data.length, true);
      local.setUint16(26, name.length, true);
      var cen = new DataView(new ArrayBuffer(46));
      cen.setUint32(0, 0x02014b50, true);
      cen.setUint16(4, 20, true);
      cen.setUint16(6, 20, true);
      cen.setUint16(12, dosTime, true);
      cen.setUint16(14, dosDate, true);
      cen.setUint32(16, crc, true);
      cen.setUint32(20, f.data.length, true);
      cen.setUint32(24, f.data.length, true);
      cen.setUint16(28, name.length, true);
      cen.setUint32(42, offset, true);
      parts.push(local.buffer, name, f.data);
      central.push(cen.buffer, name);
      offset += 30 + name.length + f.data.length;
    });
    var size = central.reduce(function (s, p) { return s + p.byteLength; }, 0);
    var end = new DataView(new ArrayBuffer(22));
    end.setUint32(0, 0x06054b50, true);
    end.setUint16(8, files.length, true);
    end.setUint16(10, files.length, true);
    end.setUint32(12, size, true);
    end.setUint32(16, offset, true);
    return new Blob(parts.concat(central, [end.buffer]), { type: "application/zip" });
  }

  function unzip(bytes) {
    var view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    var eocd = -1;
    for (var i = bytes.length - 22; i >= Math.max(0, bytes.length - 65557); i--) {
      if (view.getUint32(i, true) === 0x06054b50) { eocd = i; break; }
    }
    if (eocd < 0) return Promise.reject(new Error("Not a zip file"));
    var count = view.getUint16(eocd + 10, true);
    var p = view.getUint32(eocd + 16, true);
    var jobs = [];
    for (var n = 0; n < count; n++) {
      var method = view.getUint16(p + 10, true);
      var csize = view.getUint32(p + 20, true);
      var nameLen = view.getUint16(p + 28, true);
      var extraLen = view.getUint16(p + 30, true);
      var commentLen = view.getUint16(p + 32, true);
      var local = view.getUint32(p + 42, true);
      var name = new TextDecoder().decode(bytes.subarray(p + 46, p + 46 + nameLen));
      var start = local + 30 + view.getUint16(local + 26, true) + view.getUint16(local + 28, true);
      var raw = bytes.subarray(start, start + csize);
      p += 46 + nameLen + extraLen + commentLen;
      if (name.slice(-1) === "/") continue;
      if (method === 0) {
        jobs.push(Promise.resolve({ name: name, data: raw.slice() }));
      } else if (method === 8) {
        jobs.push(inflate(raw).then((function (nm) { return function (d) { return { name: nm, data: d }; }; })(name)));
      }
    }
    return Promise.all(jobs);
  }

  function inflate(raw) {
    var stream = new Blob([raw]).stream().pipeThrough(new DecompressionStream("deflate-raw"));
    return new Response(stream).arrayBuffer().then(function (b) { return new Uint8Array(b); });
  }

  // ------------------------------------------------------------------ game loader

  /**
   * Starts UnityLoader on `container`. callbacks: progress(fraction 0..1, label, bytes), ready(),
   * error(msg); bytes = {loaded, total} of the download so far (total 0 until known).
   * Unity reports 0..0.9 while downloading/compiling and 1 once the runtime has started.
   */
  Etg.startGame = function (container, callbacks) {
    var cb = callbacks || {};
    var reported = -1;
    function bytes(instance) {
      var loaded = 0, total = 0, all = instance.Module.buildDownloadProgress || {};
      for (var id in all) {
        if (all[id].lengthComputable) {
          loaded += all[id].loaded;
          total += all[id].total;
        }
      }
      return { loaded: loaded, total: total };
    }
    function progress(instance, p) {
      if (p >= 1) {
        if (reported >= 1) return;
        reported = 1;
        if (cb.progress) cb.progress(1, "Ready", bytes(instance));
        if (cb.ready) cb.ready(instance);
      } else if (cb.progress) {
        reported = p;
        cb.progress(p, p >= 0.9 ? "Starting" : "Loading", bytes(instance));
      }
    }
    var instance = UnityLoader.instantiate(container, "Build/webbuild.json", {
      onProgress: progress,
      compatibilityCheck: function (inst, ok, fail) {
        if (UnityLoader.SystemInfo.hasWebGL < 2) {
          if (cb.error) cb.error("This browser has no WebGL 2");
          fail();
        } else {
          ok();
        }
      },
    });
    // The game applies its saved display mode at startup; EtG defaults to FULLSCREEN, so it calls
    // Screen.SetResolution(.., true) and Unity runs canvas.requestFullscreen() on the next click.
    // Route that to the page's fullscreen: the toolbar stays visible and Esc stays with the game.
    var canvas = instance.Module.canvas;
    if (canvas) {
      canvas.requestFullscreen = canvas.webkitRequestFullscreen = function () {
        Etg.enterFullscreen();
        return Promise.resolve();
      };
    }
    Etg.game = instance;
    return instance;
  };

  // ------------------------------------------------------------------ frame monitor

  // Unity's WebGL main loop runs from requestAnimationFrame, so wrapping it measures the game's frames
  // without touching the build: the interval between frames (FPS), the time the frame's code takes
  // on the main thread (CPU: game logic plus issuing the GL calls) and, where the browser has
  // EXT_disjoint_timer_query_webgl2, the time the GPU spends on that frame's commands. Frames come
  // at the display's refresh rate; a frame whose CPU or GPU time doesn't fit waits for the next one.
  // On with ?fps in the address or F3.
  var monitor = null;

  Etg.toggleFrameMonitor = function () {
    if (monitor) {
      monitor.el.remove();
      clearInterval(monitor.timer);
      monitor = null;
      return;
    }
    var el = document.createElement("div");
    el.className = "frame-monitor";
    document.body.appendChild(el);
    monitor = { el: el, intervals: [], cpu: [], gpu: [], last: 0, queries: [], timer: 0 };
    monitor.timer = setInterval(showFrameMonitor, 500);
    wrapAnimationFrame();
  };

  var wrapped = false;
  function wrapAnimationFrame() {
    if (wrapped) return;
    wrapped = true;
    var raf = window.requestAnimationFrame.bind(window);
    window.requestAnimationFrame = function (callback) {
      return raf(function (time) {
        if (!monitor) return callback(time);
        var start = performance.now();
        var gl = Etg.game && Etg.game.Module && Etg.game.Module.ctx;
        var query = gl && beginGpuQuery(gl);
        try {
          callback(time);
        } finally {
          var end = performance.now();
          if (query) gl.endQuery(query.ext.TIME_ELAPSED_EXT);
          if (monitor.last) monitor.intervals.push(start - monitor.last);
          monitor.last = start;
          monitor.cpu.push(end - start);
          if (gl) collectGpuQueries(gl);
        }
      });
    };
  }

  function beginGpuQuery(gl) {
    var ext = gl.getExtension("EXT_disjoint_timer_query_webgl2");
    if (!ext || monitor.queries.length > 8) return null;
    var q = { ext: ext, query: gl.createQuery() };
    gl.beginQuery(ext.TIME_ELAPSED_EXT, q.query);
    monitor.queries.push(q);
    return q;
  }

  // Results arrive a few frames later; a disjoint event (GPU reset, power state) voids them.
  function collectGpuQueries(gl) {
    while (monitor.queries.length) {
      var q = monitor.queries[0];
      if (!gl.getQueryParameter(q.query, gl.QUERY_RESULT_AVAILABLE)) break;
      var ns = gl.getQueryParameter(q.query, gl.QUERY_RESULT);
      if (!gl.getParameter(q.ext.GPU_DISJOINT_EXT)) monitor.gpu.push(ns / 1e6);
      gl.deleteQuery(q.query);
      monitor.queries.shift();
    }
  }

  function showFrameMonitor() {
    function stats(a) {
      if (!a.length) return "-";
      var sum = 0, max = 0;
      a.forEach(function (v) { sum += v; max = Math.max(max, v); });
      return (sum / a.length).toFixed(1) + " ms (max " + max.toFixed(1) + ")";
    }
    var m = monitor, n = m.intervals.length;
    var fps = n ? 1000 / (m.intervals.reduce(function (a, b) { return a + b; }, 0) / n) : 0;
    var canvas = Etg.game && Etg.game.Module && Etg.game.Module.canvas;
    m.el.textContent = fps.toFixed(0) + " FPS\nframe " + stats(m.intervals) + "\ncpu   " + stats(m.cpu) +
      "\ngpu   " + (m.gpu.length ? stats(m.gpu) : "n/a") + (canvas ? "\n" + canvas.width + "x" + canvas.height : "");
    m.intervals = [];
    m.cpu = [];
    m.gpu = [];
  }

  if (/[?&]fps\b/.test(location.search)) {
    window.addEventListener("load", function () { Etg.toggleFrameMonitor(); });
  }
  window.addEventListener("keydown", function (e) {
    if (e.key === "F3") {
      e.preventDefault();
      e.stopImmediatePropagation();
      Etg.toggleFrameMonitor();
    }
  }, true);

  /** "312.4 MB"-style size. */
  Etg.formatMB = function (n) {
    return (n / 1048576).toFixed(n < 10485760 ? 1 : 0) + " MB";
  };

  // ------------------------------------------------------------------ small UI helpers

  /** Opens a file picker for saves; resolves to the picked FileList (may be empty). */
  Etg.pickFiles = function () {
    return new Promise(function (resolve) {
      var input = document.createElement("input");
      input.type = "file";
      input.multiple = true;
      input.accept = ".zip,.save,.options,application/zip";
      input.onchange = function () { resolve(input.files); };
      input.click();
    });
  };

  var toastTimer;
  /** Shows a short message in the page's [data-toast] element. */
  Etg.toast = function (text) {
    var el = document.querySelector("[data-toast]");
    if (!el) return;
    el.textContent = text;
    el.classList.add("show");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.classList.remove("show"); }, 3500);
  };

  /** Pixel-styled yes/no dialog. Resolves to true on "yes". */
  Etg.confirm = function (title, items, text, yes, no) {
    var root = document.createElement("div");
    root.className = "dialog open";
    root.innerHTML = '<div class="dialog-box" role="dialog" aria-modal="true"><h2></h2><ul></ul><p></p>' +
      '<div class="dialog-actions"><button data-no></button><button data-yes></button></div></div>';
    root.querySelector("h2").textContent = title;
    var ul = root.querySelector("ul");
    (items || []).forEach(function (t) {
      var li = document.createElement("li");
      li.textContent = t;
      ul.appendChild(li);
    });
    root.querySelector("p").textContent = text || "";
    var yesButton = /** @type {HTMLButtonElement} */ (root.querySelector("[data-yes]"));
    var noButton = /** @type {HTMLButtonElement} */ (root.querySelector("[data-no]"));
    yesButton.textContent = yes || "Yes";
    noButton.textContent = no || "Cancel";
    document.body.appendChild(root);
    noButton.focus();
    return new Promise(function (resolve) {
      function done(v) {
        root.remove();
        window.removeEventListener("keydown", key, true);
        resolve(v);
      }
      function key(e) {
        if (e.key === "Escape") done(false);
        e.stopImmediatePropagation();
      }
      window.addEventListener("keydown", key, true);
      yesButton.onclick = function () { done(true); };
      noButton.onclick = function () { done(false); };
    });
  };

  /** Standard export handler with toasts. */
  Etg.doExport = function () {
    return Etg.exportSaves().then(function (n) {
      Etg.toast("Exported " + n + " file" + (n === 1 ? "" : "s"));
    }, function (e) {
      Etg.toast(e.message);
    });
  };

  /**
   * Standard import flow: pick, check, confirm (via `confirmFn(names) -> Promise<bool>`),
   * write, reload so the game reads the new files.
   */
  Etg.doImport = function (confirmFn) {
    confirmFn = confirmFn || function (names) {
      return Etg.confirm("Import save?", names,
        "Files with the same names are replaced. The game restarts to load them.", "Import");
    };
    return Etg.pickFiles().then(function (list) {
      if (!list || !list.length) return;
      return Etg.readSaveFiles(list).then(function (files) {
        if (!files.length) {
          Etg.toast("No SlotX.save / .options files found");
          return;
        }
        return Promise.resolve(confirmFn(files.map(function (f) { return f.name; }))).then(function (ok) {
          if (!ok) return;
          return Etg.writeSaves(files).then(function () {
            Etg.toast("Imported - restarting");
            setTimeout(function () { location.reload(); }, 600);
          });
        });
      });
    }).catch(function (e) {
      Etg.toast(e.message || String(e));
    });
  };
})();
