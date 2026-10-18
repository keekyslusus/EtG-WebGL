// Native side of WebShim.FileShim: flush the IndexedDB-backed file system
// after the game writes a save. Debounced so a burst of Move/Delete calls
// from one save results in a single syncfs.
//
// Native side of WebShim.WwiseShim: forwards the game's Wwise calls to the
// audio engine in StreamingAssets/webaudio/etg-audio.js (window.EtgAudio).
// The engine script is loaded on the first call; calls made before it is
// there are queued. Playing IDs are handed out here so PostEvent can return
// one right away.
//
// Native side of WebShim.XInputShim: browser gamepads as XInput controllers.
//
// Pointers: with a heap over 2 GB a wasm32 pointer arrives as a negative int and `ptr >> 2` breaks; on
// wasm64 it is a BigInt. A "p" in __sig plus Unity's ptrToIdx macro (BuildTools/lib/AA_Wasm64Support.js)
// give the right heap index in every memory mode.
mergeInto(LibraryManager.library, {
  EtgSyncFS__deps: ['$FS'],
  EtgSyncFS: function () {
    if (Module.etgSyncTimer) {
      clearTimeout(Module.etgSyncTimer);
    }
    Module.etgSyncTimer = setTimeout(function () {
      Module.etgSyncTimer = null;
      FS.syncfs(false, function (err) {
        if (err) {
          console.error("[EtG] FS.syncfs failed", err);
        }
      });
    }, 250);
  },

  EtgAudioInit__deps: ['$UTF8ToString'],
  EtgAudioInit__sig: 'vp',
  EtgAudioInit: function (baseUrl) {
    if (Module.etgAudio) {
      return;
    }
    var base = UTF8ToString(baseUrl);
    var audio = { queue: [], nextPid: 1, engine: null };
    audio.call = function (name, args) {
      if (audio.engine) {
        return audio.engine[name].apply(audio.engine, args);
      }
      if (audio.queue.length < 4096) {
        audio.queue.push([name, args]);
      }
      return 0;
    };
    Module.etgAudio = audio;
    // version.txt (written by build_audio.py) names the build of the audio files; every URL carries
    // it, so an updated engine/bank is never taken from the browser cache.
    var version = "";
    var script = document.createElement("script");
    script.onload = function () {
      var engine = window.EtgAudio;
      engine.init(base, version);
      audio.engine = engine;
      for (var i = 0; i < audio.queue.length; i++) {
        engine[audio.queue[i][0]].apply(engine, audio.queue[i][1]);
      }
      audio.queue = [];
    };
    script.onerror = function () {
      console.error("[EtG] could not load " + script.src + " - run port/audio/build_audio.py");
      audio.queue = [];
      audio.call = function () { return 0; };
    };
    fetch(base + "version.txt", { cache: "no-store" })
      .then(function (r) { return r.ok ? r.text() : ""; }, function () { return ""; })
      .then(function (v) {
        version = v.trim();
        script.src = base + "etg-audio.js" + (version ? "?v=" + version : "");
        document.head.appendChild(script);
      });
  },

  EtgAudioPostEvent: function (eventId, obj, x, y, z) {
    var audio = Module.etgAudio;
    if (!audio) {
      return 0;
    }
    var pid = audio.nextPid++;
    audio.call("postEvent", [eventId, obj, x, y, z, pid]);
    return pid;
  },

  EtgAudioSetSwitch: function (group, value, obj) {
    if (Module.etgAudio) Module.etgAudio.call("setSwitch", [group, value, obj]);
  },

  EtgAudioSetState: function (group, state) {
    if (Module.etgAudio) Module.etgAudio.call("setState", [group, state]);
  },

  EtgAudioSetRtpc: function (rtpc, value, obj, fadeMs) {
    if (Module.etgAudio) Module.etgAudio.call("setRtpc", [rtpc, value, obj, fadeMs]);
  },

  EtgAudioResetRtpc: function (rtpc, obj) {
    if (Module.etgAudio) Module.etgAudio.call("resetRtpc", [rtpc, obj]);
  },

  EtgAudioStopAll: function (obj) {
    if (Module.etgAudio) Module.etgAudio.call("stopAll", [obj]);
  },

  EtgAudioStopPlayingId: function (playingId, fadeMs) {
    if (Module.etgAudio) Module.etgAudio.call("stopPlayingId", [playingId, fadeMs]);
  },

  EtgAudioActionOnEvent: function (eventId, actionType, obj, fadeMs, playingId) {
    if (Module.etgAudio) Module.etgAudio.call("actionOnEvent", [eventId, actionType, obj, fadeMs, playingId]);
  },

  EtgAudioPostTrigger: function (trigger, obj) {
    if (Module.etgAudio) Module.etgAudio.call("postTrigger", [trigger, obj]);
  },

  EtgAudioSetPosition: function (obj, x, y, z) {
    var audio = Module.etgAudio;
    return audio && audio.engine ? audio.engine.setPosition(obj, x, y, z) : 0;
  },

  EtgAudioSetListener: function (x, y, z) {
    var audio = Module.etgAudio;
    if (audio && audio.engine) audio.engine.setListener(x, y, z);
  },

  EtgAudioUnregister: function (obj) {
    if (Module.etgAudio) Module.etgAudio.call("unregister", [obj]);
  },

  // Gamepads for WebShim.XInputShim: fills an XINPUT_STATE (16 bytes: uint packet; ushort
  // buttons; byte LT, RT; short LX, LY, RX, RY) from the index-th connected browser gamepad.
  // The Gamepad API's "standard" mapping is the Xbox layout; other mappings are read the same
  // way as a best effort. Returns ERROR_SUCCESS (0) or ERROR_DEVICE_NOT_CONNECTED (1167).
  EtgGamepadGetState__sig: 'iip',
  EtgGamepadGetState: function (index, ptr) {
    var p8 = {{{ ptrToIdx('ptr') }}}, p16 = {{{ ptrToIdx('ptr', 1) }}}, p32 = {{{ ptrToIdx('ptr', 2) }}};
    var pads = navigator.getGamepads ? navigator.getGamepads() : [];
    var pad = null;
    for (var i = 0, n = 0; i < pads.length; i++) {
      if (pads[i] && pads[i].connected) {
        if (n++ === index) {
          pad = pads[i];
          break;
        }
      }
    }
    HEAPU32[p32] = 0;
    HEAPU32[p32 + 1] = 0;
    HEAPU32[p32 + 2] = 0;
    HEAPU32[p32 + 3] = 0;
    if (!pad) {
      return 1167;
    }
    var b = pad.buttons, a = pad.axes;
    function down(i) { return b[i] && (b[i].pressed || b[i].value > 0.5); }
    function value(i) { return b[i] ? b[i].value : 0; }
    function axis(i, sign) {
      var v = (a[i] || 0) * sign;
      return Math.max(-32768, Math.min(32767, Math.round(v * 32767)));
    }
    // XINPUT_GAMEPAD_* bits by standard button index: A B X Y LB RB (LT RT) Back Start LS RS Up Down Left Right
    var bits = [0x1000, 0x2000, 0x4000, 0x8000, 0x100, 0x200, 0, 0, 0x20, 0x10, 0x40, 0x80, 0x1, 0x2, 0x4, 0x8];
    var buttons = 0;
    for (var j = 0; j < bits.length; j++) {
      if (bits[j] && down(j)) buttons |= bits[j];
    }
    HEAPU32[p32] = Math.floor(pad.timestamp) >>> 0;
    HEAPU16[p16 + 2] = buttons;
    HEAPU8[p8 + 6] = Math.round(value(6) * 255);
    HEAPU8[p8 + 7] = Math.round(value(7) * 255);
    // The Gamepad API's Y axes point down, XInput's up.
    HEAP16[p16 + 4] = axis(0, 1);
    HEAP16[p16 + 5] = axis(1, -1);
    HEAP16[p16 + 6] = axis(2, 1);
    HEAP16[p16 + 7] = axis(3, -1);
    return 0;
  },

  // Rumble: XInput motors stay on until set to 0; a browser effect has a duration, so each change
  // starts a long effect and 0/0 stops it.
  EtgGamepadSetState: function (index, left, right) {
    var pads = navigator.getGamepads ? navigator.getGamepads() : [];
    var pad = null;
    for (var i = 0, n = 0; i < pads.length; i++) {
      if (pads[i] && pads[i].connected && n++ === index) {
        pad = pads[i];
        break;
      }
    }
    var act = pad && pad.vibrationActuator;
    if (!act) {
      return;
    }
    var rumble = Module.etgRumble || (Module.etgRumble = {});
    var key = left.toFixed(2) + "/" + right.toFixed(2);
    if (rumble[index] === key) {
      return;
    }
    rumble[index] = key;
    if (left <= 0 && right <= 0) {
      if (act.reset) act.reset();
    } else if (act.playEffect) {
      act.playEffect("dual-rumble", {
        duration: 5000,
        strongMagnitude: Math.min(1, left),
        weakMagnitude: Math.min(1, right)
      }).catch(function () {});
    }
  }
});
