// Native side of EtgModSmoke.SmokeWeb (the mod smoke test in the WebGL player): flushes the report to IndexedDB and
// hands it to the page as window.EtgSmoke = {state, markdown, json, finished}; port/site/etg-site.js
// (Etg.onSmokeReport) saves it at the end or reloads the page with &resume for "restart before X".
mergeInto(LibraryManager.library, {
  EtgSmokeSync__deps: ['$FS'],
  EtgSmokeSync: function () {
    FS.syncfs(false, function (err) {
      if (err) console.error("[ModSmoke] FS.syncfs failed", err);
    });
  },

  EtgSmokeReport__deps: ['$UTF8ToString'],
  EtgSmokeReport__sig: 'vpppi',
  EtgSmokeReport: function (state, markdown, json, finished) {
    var report = {
      state: UTF8ToString(state),
      markdown: UTF8ToString(markdown),
      json: UTF8ToString(json),
      finished: !!finished,
    };
    window.EtgSmoke = report;
    if (window.Etg && window.Etg.onSmokeReport) window.Etg.onSmokeReport(report);
  },
});
