# Переезд на Unity 6.6: оставшиеся шаги

Ветка `unity6`. Проект 6.6 — `ripped/Unity6Project` (создаётся `apply.sh` из `ripped/ExportedProject`), сборка
плеера — `webbuild6/`. `ripped/ExportedProject` и `webbuild/` остаются рабочей 2017.4-сборкой `master` до конца
переезда. Уже работает: патчер и `verify` (All references resolve), чистый импорт с модами без ошибок, Play From Boot,
полный `modsmoke.sh`, шейдеры (`abtest.sh` GLES = D3D11 как на 2017.4, `shaderdiff.sh` на
CGIncludes 6.6 — 3068/3068), бандлы (EtG Web → 3, данные FullInspector как в 2017.4: `bundle_ficheck.py`).

## 1. Страница и упаковка

- `port/site`: загрузчик 2017.4 (`UnityLoader.instantiate`, `Build/webbuild.json`, `UnityLoader.SystemInfo`,
  `buildDownloadProgress`, `UnityLoader.Cryptography.md5`) → `createUnityInstance(canvas, config, onProgress)` и
  файлы `.loader.js` / `.framework.js` / `.data` / `.wasm`.
- Сохранения: путь `/idbfs/<md5>` — проверить, что 6.6 считает `persistentDataPath` так же, иначе старые сохранения в
  IndexedDB не найдутся (есть экспорт/импорт на странице).
- Gzip: нужны заголовки `Content-Encoding` от сервера или `decompressionFallback`.
- `serve-webbuild.ps1` отдаёт `webbuild/` — добавить `webbuild6/`.
- `ModBaker resources`: кладёт ли 6.6 `*-resources.dat` в WebGL-сборку сам (2017.4 терял их). Нужен ли ещё
  `AccessFixer`.

## 2. Первая dev-сборка и проверка в браузере

- `bash port/webbuild.sh --mods` → `webbuild6/` (куча 512 → 4096 МБ, wasm32).
- jslib под Emscripten 4 (`UTF8ToString`, `__sig`, `ptrToIdx`) в браузере ещё не проверен: звук, геймпад,
  `FS.syncfs`.
- `?smoketest` (`port/tasks/webgl-smoketest.md`): ошибки только IL2CPP (новее на 8 лет: shared generics, stripping —
  `stripEngineCode = false` и `link.xml`, исключения).
- Память с модами в пределах 4 ГБ; wasm64 включать, только если не хватит (Chrome/Edge 128+, Firefox 129+, Safari нет).
- Шейдеры: *Always Included* компилируются только при сборке плеера — ошибки компиляции в логе сборки. В браузере —
  консоль на `Feedback loop` / `GL_INVALID_OPERATION`: `Pixelator.RenderGBuffer*` делает `Blit(temporary, temporary,
  m_pointLightMaterial)`, и 6.6 пишет «A call to Blit with source and dest set to the same RenderTexture may result in
  undefined behaviour» (в редакторе свет при этом верный). Редактор GLES работает на shader level 46 (эмуляции WebGL
  в 6.6 нет), WebGL 2 — 35: SubShader / `#pragma target` выше 3.5 abtest не поймает.
- Производительность против 2017.4: в 6.6 нет динамического батчинга (по замеру на 2017.4 он экономил 12–16 % draw
  calls, оценка +0,4–0,8 мс на кадр).

## 3. Завершение

- `webbuild6/` → `webbuild/`, `Unity6Project` — основной проект; README «Building» под 6.6.
- Убрать хаки 2017.4: `setstack.py`, `patch_webgl_dedupe.py`, `patch_webgl_noasm.py`, `tools/unity-2017.4.27f1`,
  `tools/u6spike`.
- Слить `unity6` в `master`.
