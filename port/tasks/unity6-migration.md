# Переезд на Unity 6.6: оставшиеся шаги

Ветка `unity6`. Проект 6.6 — `ripped/Unity6Project` (создаётся `apply.sh` из `ripped/ExportedProject`), сборка
плеера — `webbuild6/`. `ripped/ExportedProject` и `webbuild/` остаются рабочей 2017.4-сборкой `master` до конца
переезда. Уже работает: патчер и `verify` (All references resolve), чистый импорт с модами без ошибок, Play From Boot,
полный `modsmoke.sh`, шейдеры (`abtest.sh` GLES = D3D11 как на 2017.4, `shaderdiff.sh` на
CGIncludes 6.6 — 3068/3068), бандлы (EtG Web → 3, данные FullInspector как в 2017.4: `bundle_ficheck.py`), страница под
`createUnityInstance` (`etg-build.js`, `serve.py` с `Content-Encoding`; на пробной сборке 6.6 проверены загрузка,
путь сохранений `/idbfs/<md5>` как в 2017.4, экспорт/импорт, чтение `*-resources.dat` из `/Il2CppData/Resources`). `ModBaker resources` и `AccessFixer` по-прежнему нужны: упаковщик
6.6 тоже не кладёт `*-resources.dat` в `.data`, Mono 6.6 в Play Mode тоже проверяет доступ к членам.

## 1. Первая dev-сборка и проверка в браузере

- `bash port/webbuild.sh --mods` → `webbuild6/` (куча 512 → 4096 МБ, wasm32). Первая сборка 2026-10-19: **7,5 мин**
  (2017.4 — 1 ч 48 мин), wasm 158 МБ, data 19 МБ; dev-сборку 6.6 не сжимает (`.gz` нет, `etg-build.js` это учитывает).
  В браузере грузится за ~10 с, заставки и титульный экран Expand the Gungeon, клавиатура работает; по словам
  пользователя, несколько этажей — стабильные 60 FPS даже в dev-сборке (2017.4 — ~35).
- jslib под Emscripten 4 (`UTF8ToString`, `__sig`, `ptrToIdx`) в браузере ещё не проверен: звук, геймпад,
  `FS.syncfs`.
- `?smoketest` сделан и прогнан (`port/tasks/webgl-smoketest.md`): все фазы проходят, ошибок порта / IL2CPP нет;
  открыто там же — `GetUntrimmedBounds` на glitch-этажах и одно неповторившееся падение рантайма.
- Память с модами в пределах 4 ГБ; wasm64 включать, только если не хватит (Chrome/Edge 128+, Firefox 129+, Safari нет).
- Страница на настоящей игре: полноэкранный режим игры (переопределённый `canvas.requestFullscreen` — зовёт ли его
  6.6), ввод (Esc, keyboard lock), F3/F4-монитор (`Etg.gl()`). `serve-webbuild.ps1 -Dir webbuild6`.
- 6.6 предупреждает: ручной `JS_FileSystem_Sync` (`PlayerPrefs.Save`, `FS.syncfs` в jslib) устарел — решить про
  `autoSyncPersistentDataPath: true` в конфиге `createUnityInstance` (`etg-site.js`).
- Шейдеры: *Always Included* компилируются только при сборке плеера — ошибки компиляции в логе сборки. В браузере —
  консоль на `Feedback loop` / `GL_INVALID_OPERATION`: `Pixelator.RenderGBuffer*` делает `Blit(temporary, temporary,
  m_pointLightMaterial)`, и 6.6 пишет «A call to Blit with source and dest set to the same RenderTexture may result in
  undefined behaviour» в редакторе (свет при этом верный); плеер (`?smoketest`) этого не пишет — остаётся глянуть
  консоль браузера на предупреждения WebGL. Редактор GLES работает на shader level 46 (эмуляции WebGL
  в 6.6 нет), WebGL 2 — 35: SubShader / `#pragma target` выше 3.5 abtest не поймает.
- Производительность: несмотря на отсутствие динамического батчинга в 6.6, dev-сборка держит 60 FPS против ~35 у
  2017.4 — замерить F3/F4 в release-сборке для README.

## 2. Завершение

- `webbuild6/` → `webbuild/`, `Unity6Project` — основной проект; README «Building» под 6.6.
- Убрать хаки 2017.4: `setstack.py`, `patch_webgl_dedupe.py`, `patch_webgl_noasm.py`, `tools/unity-2017.4.27f1`,
  `tools/u6spike`.
- Слить `unity6` в `master`.
