// Native side of WebShim.FileShim: flush the IndexedDB-backed file system
// after the game writes a save. Debounced so a burst of Move/Delete calls
// from one save results in a single syncfs.
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
  }
});
