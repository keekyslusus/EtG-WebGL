// Native side of WebShim.FileShim: flush the IndexedDB-backed file system
// after the game writes a save. Debounced so a burst of Move/Delete calls
// from one save results in a single syncfs.
//
// Native side of WebShim.WwiseShim: forwards the game's Wwise calls to the
// audio engine in StreamingAssets/webaudio/etg-audio.js (window.EtgAudio).
// The engine script is loaded on the first call; calls made before it is
// there are queued. Playing IDs are handed out here so PostEvent can return
// one right away.
mergeInto(LibraryManager.library, {
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

  EtgAudioInit: function (baseUrl) {
    if (Module.etgAudio) {
      return;
    }
    var base = Pointer_stringify(baseUrl);
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
    var script = document.createElement("script");
    script.src = base + "etg-audio.js";
    script.onload = function () {
      var engine = window.EtgAudio;
      engine.init(base);
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
    document.head.appendChild(script);
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
  }
});
