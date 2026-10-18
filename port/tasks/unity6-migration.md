# Переезд на Unity 6.6: оставшиеся шаги

Ветка `unity6`. Проект 6.6 — `ripped/Unity6Project` (создаётся `apply.sh` из `ripped/ExportedProject`), сборка
плеера — `webbuild6/`. `ripped/ExportedProject` и `webbuild/` остаются рабочей 2017.4-сборкой `master` до конца
переезда. Уже работает: патчер и `verify` (All references resolve), чистый импорт с модами без ошибок, Play From Boot,
полный `modsmoke.sh`.

## 1. Шейдеры

- `abtest.sh` и `variant-selection.sh` без Graphics Emulation (в 6.6 её нет): `EtgAbTest` по умолчанию
  `-abEmulation webgl2` и выходит с кодом 3, `EtgVariantSelection` зовёт пункт меню. Прогнать на 6.6 — это основная
  проверка, что GLES-рендер 6.6 совпадает с D3D11.
- `Pixelator.RenderGBuffer*` делает `Blit(temporary, temporary, m_pointLightMaterial)` (G-buffer света, код игры), и 6.6
  в Play Mode на каждом уровне пишет `A call to Blit with source and dest set to the same RenderTexture may result in
  undefined behaviour` (2017.4 молчал). На WebGL это возможный feedback loop — сверить свет abtest-ом и консоль браузера.
- `fix_shaders.py`, LPPV: в `UnityShaderVariables.cginc` 6.6 условие — `#if !defined(UNITY_NO_LPPV)` без списка API,
  то есть на WebGL, похоже, `UNITY_LIGHT_PROBE_PROXY_VOLUME` = 1. Тогда ветка `fix_occlusion_selector` станет
  исходным DX11-кодом — проверить abtest-ом.
- `shaderdiff.sh` берёт CGIncludes 2017.4 — перевести на `6000.6.5f1/Editor/Data/CGIncludes`.
- Шейдеры из *Always Included* компилируются только при сборке плеера — проверить в первой сборке.
- `Shader error in '': Parse error ... line 9` при импорте (шейдер без имени, похоже, из кода редактора) — разобрать.
- Устаревший API в `EtgAbTest` / `EtgWebDebug` (`ShaderUtil.GetProperty*` → `Shader.GetProperty*`,
  `Light.cookieSize` → `cookieSize2D`) — пока только предупреждения.

## 2. Бандлы

EtG Web → 3 (`BuildBundles`) в 6.6. В spike бандлы собрались без ошибок шейдеров, но со сломанным FullInspector;
теперь он исправлен (`EtgFullInspector.cs`, `UnityNull`) — проверить, что данные FullInspector в бандлах целы
(например, `BossStatues`: 8 ссылок, `levelData`).

## 3. Страница и упаковка

- `port/site`: загрузчик 2017.4 (`UnityLoader.instantiate`, `Build/webbuild.json`, `UnityLoader.SystemInfo`,
  `buildDownloadProgress`, `UnityLoader.Cryptography.md5`) → `createUnityInstance(canvas, config, onProgress)` и
  файлы `.loader.js` / `.framework.js` / `.data` / `.wasm`.
- Сохранения: путь `/idbfs/<md5>` — проверить, что 6.6 считает `persistentDataPath` так же, иначе старые сохранения в
  IndexedDB не найдутся (есть экспорт/импорт на странице).
- Gzip: нужны заголовки `Content-Encoding` от сервера или `decompressionFallback`.
- `serve-webbuild.ps1` отдаёт `webbuild/` — добавить `webbuild6/`.
- `ModBaker resources`: кладёт ли 6.6 `*-resources.dat` в WebGL-сборку сам (2017.4 терял их). Нужен ли ещё
  `AccessFixer`.

## 4. Первая dev-сборка и проверка в браузере

- `bash port/webbuild.sh --mods` → `webbuild6/` (куча 512 → 4096 МБ, wasm32).
- jslib под Emscripten 4 (`UTF8ToString`, `__sig`, `ptrToIdx`) в браузере ещё не проверен: звук, геймпад,
  `FS.syncfs`.
- `?smoketest` (`port/tasks/webgl-smoketest.md`): ошибки только IL2CPP (новее на 8 лет: shared generics, stripping —
  `stripEngineCode = false` и `link.xml`, исключения).
- Память с модами в пределах 4 ГБ; wasm64 включать, только если не хватит (Chrome/Edge 128+, Firefox 129+, Safari нет).
- Производительность против 2017.4: в 6.6 нет динамического батчинга (по замеру на 2017.4 он экономил 12–16 % draw
  calls, оценка +0,4–0,8 мс на кадр).

## 5. Завершение

- `webbuild6/` → `webbuild/`, `Unity6Project` — основной проект; README «Building» под 6.6.
- Убрать хаки 2017.4: `setstack.py`, `patch_webgl_dedupe.py`, `patch_webgl_noasm.py`, `tools/unity-2017.4.27f1`,
  `tools/u6spike`.
- Слить `unity6` в `master`.
