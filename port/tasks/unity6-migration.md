# Оценка: перенос порта с Unity 2017.4 на Unity 6.6 (WebAssembly64) или 2021/2022 LTS

Spike от 2026-10-18, миграции нет: рабочая 2017.4-сборка не тронута (`ripped/ExportedProject`, `webbuild/` и
`port/` кроме этого файла не менялись). Всё ниже — по офлайн-анализу и по пробному открытию копии проекта в
Unity 6000.6.5f1.

## Зачем

2017.4 (Emscripten 1.37.3, fastcomp) даёт wasm-кучу максимум 2032 МБ, и она выделяется сразу целиком. Модовая
dev-сборка падает с `Cannot enlarge memory arrays` на загрузке первого этажа, а пользователь хочет добавить ещё
оверхол-модов. Варианты:

- **Unity 2021.2+ / 2022.3**: wasm32 до 4 ГБ (Maximum Memory Size, куча растёт по мере надобности);
- **Unity 6.x**: то же, а с **6.6** ещё WebAssembly64 (куча до 16 ГБ; включается сама при Maximum Memory Size
  > 4096 МБ и тянет за собой Target WebAssembly 2023);
- остаться на 2017.4 и ужать память.

## Что сделано

1. Unity 6000.6.5f1 с WebGL Support **уже установлен** (`C:\Program Files\Unity\Hub\Editor\6000.6.5f1`),
   скачивать ничего не пришлось. 2021.3/2022.3 не установлены, по ним — оценка без проверки.
2. `Patcher verify` текущих пропатченных DLL (`port/build/patched`, с модами) против WebGL-библиотек 6.6
   (`PlaybackEngines/WebGLSupport/Variations/il2cpp/Managed` + `MonoBleedingEdge/lib/mono/unityaot-win32`).
   Плюс разовые инструменты в scratchpad (не в git): `movecheck` (как verify, но отличает «тип переехал в другой
   модуль» от «удалён» и показывает члены движка с `[Obsolete(error)]`), `retarget`/`spikefix` (правки DLL копии).
3. Копия проекта: `tools/u6spike/Project` (Assets/ProjectSettings/Packages из `ripped/ExportedProject`, состояние с
   модами; `tools/` в `.gitignore`). Открыта batchmode-ом:
   `Unity.exe -batchmode -quit -accept-apiupdate -buildTarget WebGL -projectPath tools/u6spike/Project`,
   логи `tools/u6spike/import1..6.log`. Каждый прогон упирался в следующую проблему; что пришлось поправить в копии,
   чтобы дойти до импорта ассетов, — ниже в «Результатах» по порядку.

## Результаты

### `Patcher verify` против Unity 6.6: 188 неразрешённых ссылок

Почти все — не удаления, а переезды и пакеты:

| Что | Сколько | Суть |
|---|---|---|
| Типы, переехавшие в другой модуль | 12 типов | `AssetBundle`, `AssetBundleManifest` → AssetBundleModule; `Input`, `Touch`, `TouchPhase`, `TouchType`, `Gyroscope`, `DeviceOrientation` → InputLegacyModule; `GUIElement`, `GUITexture` → IMGUIModule; `DownloadHandlerAssetBundle` → UnityWebRequestAssetBundleModule; `AnalyticsSessionInfo` (DataPrivacy) → UnityAnalyticsModule. Остальные ~140 строк verify — их члены |
| Сборки, ставшие пакетами | 2 | `UnityEngine.UI` (пакет `com.unity.ugui` 2.6.0, имя то же); `UnityEngine.Timeline` (пакет `com.unity.timeline` 6.6.0, сборка теперь называется **`Unity.Timeline`**). Все члены UGUI/Timeline, которые зовёт игра (свой `PointerInputModule` у InControl, `AkEventTrack`/`AkRTPCTrack`), в пакетах есть |
| Нет в поставке Unity | 2 | `Boo.Lang`, `UnityScript.Lang` (для `EtgUnityScript.dll`); есть в `EtG_Data/Managed`, кладутся плагинами |
| Удалённые члены | 7 | `Camera.hdr` get/set (Pixelator ×5, Bloom, BloomAndLensFlares → `allowHDR`); `Collider.material` / `sharedMaterial` с типом `PhysicMaterial` (PlayMaker GetCollisionInfo и т.п., tk2d `ColliderBuilder3D` → в 6.x тип `PhysicsMaterial`); `GUIText.text` set (PlayMaker SetGUIText); `GUITexture.pixelInset` (PlayMakerGUI); `Graphics.DrawProceduralIndirect(MeshTopology, ComputeBuffer, int)` (DepthOfFieldScatter) |

`[Obsolete(error)]`, которые verify не видит (член есть, но пустышка): `GUITexture.*` / `GUIText.material` /
`GUIElement.*` кидают `FeatureRemoved` (iTween, PlayMaker-экшены — только если на объекте есть такой компонент,
а в 6.6 его быть не может); **`UnityWebRequest.GetAssetBundle` возвращает `null`** — это `WebShim.BootLoader`,
то есть загрузка всех бандлов при старте; `Object.GetInstanceID` работает (через `EntityId`). Ещё ~20 членов
`[Obsolete]` без ошибки (WWW, `Application.LoadLevel`, `FindObjectsOfType`...) работают.

Стабы `WebShim/UnityStubs.cs`: сетевые типы (RakNet) в 6.6 по-прежнему нужны. `MovieTexture` и
`ProceduralMaterial` в WebGL-модулях 6.6 тоже нет (стабы нужны плееру), но в **редакторных** модулях они есть
(`AudioModule`, `SubstanceModule`) — дубликат в WebShim конфликтует, нужен отдельный путь для редактора.

### Открытие копии в Unity 6.6

1. **Манифест пакетов.** `com.unity.modules.vr` в 6.6 нет — убран; добавлены `com.unity.ugui` 2.6.0 и
   `com.unity.timeline` 6.6.0. Unity сам добавил ещё 11 пакетов по умолчанию (purchasing, analytics, collab-proxy,
   ai.navigation, xr.legacyinputhelpers..., а с ними TextMeshPro и newtonsoft-json) — лишнее, при миграции вычистить.
2. **import1: 6 ошибок C# в самом UGUI** (CS0115/CS0534 в `GraphicRaycaster`, `PanelRaycaster`,
   `WorldDocumentRaycaster`). Причина: UGUI теперь компилируется из исходников и по умолчанию ссылается на все
   плагины, а в `EtgGame.dll` есть свой `RaycastResult` в глобальном пространстве имён — он перекрывает
   `UnityEngine.EventSystems.RaycastResult`. Решение: у всех DLL в `Assets/Plugins` выключить Auto Reference
   (`isExplicitlyReferenced: 1` в .meta), а `EtgWebBuild.Editor.asmdef` и `EtgModSmoke.asmdef` перечисляют их в
   `precompiledReferences`.
3. **import2/3: ошибки в наших скриптах.** `EtgModSmoke` использует типы игры напрямую (12 ошибок CS0246 до
   ссылок в asmdef). После этого в `port/UnityAssets/Editor` осталась **1 ошибка**: `BuildPipeline.BuildPlayer`
   возвращает `BuildReport`, а не строку (`EtgWebBuild.cs:330`), и 31 предупреждение об устаревшем API.
4. **import4/5: DLL не грузятся.** `Assembly 'EtgGame.dll' will not be loaded: Unable to resolve reference
   'UnityEngine.Timeline'`, за ней каскадом Alexandria, MtG API, dfScriptLite, SilverJacket, SonyNP, EtgModSmoke,
   EtgWebBuild; `EtgUnityScript.dll` — из-за Boo.Lang/UnityScript.Lang. Когда ссылки нашлись — `Unloading broken
   assembly EtgGame.dll / WebShim.dll`: Mono редактора не находит `UnityEngine.AssetBundle` и `GUITexture` в
   `UnityEngine.CoreModule`. **API Updater Unity (`-accept-apiupdate`) переезды типов между модулями не
   исправляет**, он поправил только `UnityWebRequest.GetAssetBundle` в WebShim. Всё это — работа патчера:
   в копии сделано Cecil-ом (12 типов переназначены по полному имени, ссылка `UnityEngine.Timeline` →
   `Unity.Timeline`, WebShim-овские `MovieTexture`/`ProceduralMaterial` убраны с дороги).
5. **import6 (чистый Library, все правки выше)**: проект открывается, код 0, **0 ошибок компиляции**, сборки
   игры и модов грузятся. Импорт 13 827 ассетов — 131 с (Library 2017.4 был 8,8 ГБ, здесь с нуля). Что в логе:

   | Сообщение | Сколько | Что это |
   |---|---|---|
   | `Could not set up parent class ... Invalid generic instantiation ... ComponentAction`1` | 7 960 | В 6.6 `GUIText`, `GUITexture`, `GUIElement`, `GUILayer` — пустые классы от `System.Object`, не `Component`. PlayMaker-экшены `ComponentAction<GUIText>` (1) и `ComponentAction<GUITexture>` (3) нарушают `where T : Component` и не грузятся |
   | `ReflectionTypeLoadException` → `TypeInitializationException: FullInspector.fiSettings` / `InspectedType` | 1 990 / 295 | Следствие предыдущего: `EtgGame.GetTypes()` бросает, статический конструктор FullInspector падает, и всё, что игра сериализует через FullInspector (`BaseBehavior.OnAfterDeserialize`), в редакторе не восстанавливается. **Блокер для Play Mode**; лечится в патчере: эти 4 экшена (или ссылки на 4 типа) — на стабы в WebShim, как уже сделано для NetworkView |
   | `GetInternalActivePlatformGuid is not allowed to be called during serialization` | 560 | Встроенный WebGL-модуль редактора 6.6 вызывается из десериализации `BehaviorSpeculator` (и др.); только редактор |
   | `GetAllAssetBundleNames is not allowed to be called from a MonoBehaviour constructor` | 64 | `WebShim.BundleShim` (симуляция бандлов) из инициализатора поля мода Expand. 2017.4 это разрешал; только редактор, для Play Mode — переделать на ленивый вызов |
   | `Problem detected while importing the Prefab` / `Component ... could not be loaded ... Removing it` | 49 префабов, 79 объектов | Это `GUILayer` (class 92) на камерах — компонента больше нет, удаление безвредно |
   | `below the supported minimum` | 6 400 | Старый формат сериализации (GameObject v5, TextureImporter v4...) — Unity апгрейдит при импорте, предупреждение |
   | `Duplicate assembly 'Newtonsoft.Json.dll'` | 4 | Добавленный Unity пакет `com.unity.nuget.newtonsoft-json` 13.0 вытесняет 11.0 из модов — пакет убрать |
   | Шейдеры | 253 импортированы, 1 `Parse error` без имени | см. «Шейдеры» |

   Сборка `EtgModSmoke` и `EtgWebBuild.Editor` (после правки `BuildReport`) компилируются.

### Шейдеры

- **Include-ы.** Декомпилированные шейдеры ничего не `#include` (кроме 15 строк `UnityCG`/`Lighting`/`AutoLight`
  в нескольких шейдерах не из декомпилятора) и опираются на авто-включаемые `HLSLSupport.cginc` +
  `UnityShaderVariables.cginc`: встроенные uniform-ы (`unity_ObjectToWorld`, `unity_MatrixVP`, `unity_4LightPos*`,
  `unity_SH*`, `unity_LightShadowBias`, `unity_SpecCube0_HDR`) и макросы (`UNITY_DECLARE_TEX3D_FLOAT`,
  `UNITY_DECLARE_SHADOWMAP`, `UNITY_ATTEN_CHANNEL`, `UNITY_UV_STARTS_AT_TOP`, `UNITY_REVERSED_Z`). Всё это в 6.6
  в тех же файлах (`Editor/Data/Resources/CGIncludes`). `unity_Lightmap_HDR` / `unity_DynamicLightmap_HDR` по-прежнему
  в `UnityCG.cginc` (баг 16 из `port/README.md`, Shaders, не вернётся), `unity_WorldToLight` — в `AutoLight.cginc`, так что
  668 своих объявлений не конфликтуют. Новые объявления в `UnityShaderVariables.cginc` (`unity_RenderingLayer`,
  `unity_ShadowMask` теперь всегда) с именами в шейдерах порта не пересекаются.
- **Что меняется для `fix_shaders.py`:** в `UnityShaderVariables.cginc` 6.6 условие LPPV — просто
  `#if !defined(UNITY_NO_LPPV)`, без списка API (в 2017.4 GLES в списке не было), то есть, похоже,
  `UNITY_LIGHT_PROBE_PROXY_VOLUME` = 1 и на WebGL (если компилятор не ставит `UNITY_NO_LPPV` для GLES — проверить). Объявления `unity_ProbeVolume*` под
  `#if !UNITY_LIGHT_PROBE_PROXY_VOLUME` станут пустыми (это правильно), а ветка `fix_occlusion_selector`
  (`unity_OcclusionMaskSelector` «не задан на GLES») станет исходным DX11-кодом — нужно перепроверить abtest-ом.
- **Импорт:** 253 `.shader` импортированы без ошибок разбора; одна ошибка `Shader error in ''` (без имени, строка 9)
  — похоже на шейдер, создаваемый из строки кодом при загрузке редактора, не ассет; не разобрано.
- **Компиляция под WebGL (`EtgWebBuild.BuildBundles` в копии, 6 мин):** 33 бандла собраны, **0 ошибок и 0
  предупреждений шейдеров**; 72 шейдера из бандлов, 1 398 вариантов vp/fp для gles3. Размеры бандлов почти те же,
  что у 2017.4 (`expandsharedauto` 23,9 МБ, `shared_auto_001` 23,1 МБ против 22,99 МБ). Шейдеры из *Always Included*
  компилируются только при сборке плеера — не проверены. Сами бандлы этого прогона непригодны для игры:
  FullInspector в редакторе сломан (см. import6), его `OnBeforeSerialize` падал при записи (2 877 исключений);
  ещё 4× `MissingMethodException: Collider.set_sharedMaterial(PhysicMaterial)` (tk2d ColliderBuilder3D) —
  удалённый член из таблицы выше.
- **Тесты шейдеров:** `shaderdiff.sh` от Unity не зависит. `abtest.sh` и `variant-selection.sh` требуют замены
  Graphics Emulation (см. ниже) и прогона на 6.6 — это и есть основная проверка, что GLES-рендер 6.6 совпадает
  с D3D11.

Оценка: 1–2 дня, если abtest не найдёт системных расхождений.

### jslib (`port/UnityAssets/Plugins/WebGL/EtgWebShim.jslib`) под Emscripten 4.0.20 / wasm64

В 6.6 Emscripten **4.0.20** (в 2017.4 — 1.37.3). jslib маленький, проблем три:

- `Pointer_stringify` (1 место, `EtgAudioInit`) удалён → `UTF8ToString`.
- `HEAPU32[ptr >> 2]` / `HEAP16[(ptr + 8) >> 1]` в `EtgGamepadGetState`: уже на wasm32 с кучей > 2 ГБ указатель
  выше 2 ГБ отрицательный и `>>` даёт отрицательный индекс; на wasm64 указатель приходит как BigInt и `>>` с
  числом бросает TypeError. Решение по документации 6.6: `EtgGamepadGetState__sig: "iip"` (Emscripten сам
  переводит `p` в Number) и `{{{ ptrToIdx('ptr', 2) }}}` вместо сдвигов.
- `EtgAudioInit` тоже получает указатель (строку) → `__sig: "vp"`.

Остальные функции принимают только int/float (`obj` у Wwise — `int`, не `ulong`), `Runtime.dynCall` и
`dynCall` нигде нет. `Module.etg*`, `FS.syncfs` работают и в новом фреймворке (на всякий случай `__deps: ['$FS']`).
Оценка: 1–2 часа.

### Страница (`port/site`)

Загрузчик 2017.4 (`UnityLoader.instantiate`, `Build/webbuild.json`, `UnityLoader.SystemInfo.hasWebGL`,
`instance.Module.buildDownloadProgress`, `UnityLoader.Cryptography.md5` для пути IDBFS) заменяется на
`createUnityInstance(canvas, config, onProgress)` и другой набор файлов (`.loader.js`, `.framework.js`, `.data`,
`.wasm`). md5 для `/idbfs/<hash>` придётся взять свой; надо проверить, что 6.6 считает путь `persistentDataPath`
так же — иначе старые сохранения из IndexedDB не найдутся (экспорт/импорт сохранений на странице есть, так что
в худшем случае перенос руками). Оценка: ~1 день.

### Редакторные скрипты (`port/UnityAssets/Editor`)

Кроме ошибки `BuildReport` — то, что компилируется, но работать не будет:

- **Edit → Graphics Emulation удалено** (строк "Graphics Emulation" в `Unity.exe` 6.6 нет совсем, в 2017.4 — 16).
  `EtgAbTest` (`-abEmulation webgl2` по умолчанию, иначе `Exit(3)`), `EtgVariantSelection`, Play From Boot
  вызывают эти пункты меню. `-force-gles30` у редактора на Windows в 6.6 по документации остался.
- `PlayerSettings.WebGL.memorySize` → `initialMemorySize` / `maximumMemorySize` (+ `memoryGrowthMode`);
  `emscriptenArgs = "--js-opts 0"` (флаг fastcomp; в Emscripten 4 его нет); `linkerTarget` (через reflection,
  упадёт тихо); `ApiCompatibilityLevel.NET_2_0` → `.NET Standard 2.1` / `.NET Framework`; перегрузки с
  `BuildTargetGroup` → `NamedBuildTarget`; `ShaderUtil.GetProperty*` → `Shader.GetProperty*`; `Light.cookieSize`.
- Хаки 2017.4 больше не нужны: `setstack.py` (asm2wasm), `patch_webgl_dedupe.py`, `patch_webgl_noasm.py`,
  копия редактора `tools/unity-2017.4.27f1` — в 2019.1+ нет fastcomp/asm.js, а с ними уходит самая долгая часть
  модовой dev-сборки (сейчас ~1 ч 45 мин).

Оценка: ~1 день.

### Что переносится без изменений

- **Patcher** — подход тот же; новое: переназначение 12 переехавших типов (в spike ~40 строк Cecil), сборки
  Timeline, стабы для 7 удалённых членов и для `GUIText`/`GUITexture`/`GUIElement`/`GUILayer` (от `Behaviour`, иначе
  FullInspector в редакторе не работает), `verify` с путями 6.6 и пакетных сборок (`Library/ScriptAssemblies`),
  отдельные пути для MovieTexture/ProceduralMaterial (редактор/плеер). Переименование `Assembly-CSharp*` → `EtgGame*`
  по-прежнему нужно. Снятие атрибутов на ограничениях дженериков (для mcs) станет не нужно, но не мешает.
- **WebShim** — пересобрать против модулей 6.6 (сейчас `net35` и модули игры 2017), `BootLoader` →
  `UnityWebRequestAssetBundle.GetAssetBundle`, `isNetworkError/isHttpError` → `result`. ThreadShim, FileShim,
  XInputShim, WwiseShim — без изменений (C#-потоков в Web по-прежнему нет).
- **ModBaker** — работает на .NET Framework с DLL игры, от версии Unity не зависит. `ModBaker resources`
  (ресурсы, которые упаковщик 2017.4 терял) и `AccessFixer` (Mono редактора) скорее всего остаются, проверить.
- **Звук** (`port/audio`, etg-audio.js, WwiseRef, audiodiff) — не зависит от Unity.
- **ShaderDiff** — сравнивает с DXBC через d3dcompiler, от Unity не зависит.
- **Smoke-тест модов** (`EtgModSmoke`) — скомпилировался в 6.6 без правок кода (нужны только ссылки в asmdef);
  `modsmoke.sh`, abtest, variant-selection — после замены Graphics Emulation.
- **Бандлы модов** (`mod_bundles.py`) — переносятся; `useLegacyImporter: 0` у видео и MovieTexture-проблема 2017.4
  уходят сами.

### Риски, которые spike не проверял

- **Динамический батчинг удалён в 6.6** (в 6.5 объявлен устаревшим; в 6.0–6.5 он ещё есть). Замерено на 2017.4,
  см. «Замер: что даёт динамический батчинг»: он экономит **12–16 % draw calls**, так что потеря небольшая.
- **Built-in Render Pipeline объявлен устаревшим в 6.5**, поддерживается до конца 6.7 LTS. Все шейдеры порта —
  Built-in; переход на URP — это переписать 252 декомпилированных шейдера, не вариант.
- **Сериализация `Dictionary` в 6.6**: Unity теперь сериализует поля-словари. У MonoBehaviour игры и модов
  такие поля раньше молча игнорировались; теперь они попадут в данные (пустыми) — поведение и размер префабов
  могут измениться. Проверяется smoke-тестом.
- **IL2CPP новее на 8 лет**: другие правила shared generics, stripping (сейчас `stripEngineCode = false` и
  `link.xml`), исключения. Ошибки только плеера ищет `?smoketest` (`port/tasks/webgl-smoketest.md`).
- **wasm64 больше памяти на объект**: ссылки 8 байт вместо 4, управляемая куча вырастет (на сколько — не мерили).
  Unity: «немного медленнее» и «не рекомендуется, где важна скорость загрузки».

## Замер: что даёт динамический батчинг (2017.4, Play Mode)

Фаза `batching` smoke-теста (`port/UnityAssets/EtgModSmoke/SmokeBatching.cs`, в `all` не входит): быстрый старт
за Пилота с модами, стартовая комната первого этажа, редактор через `-force-gles30`. Динамический батчинг
включается и выключается через player settings (`PlayerSettings.SetBatchingForPlatform`, потом возвращается как
было), по 2 отрезка по 240 кадров на каждое состояние, счётчики `UnityStats`. Выключение действует сразу:
`dynamicBatchedDrawCalls` = 0. Отчёт: `port/build/modsmoke/report-batching.md`.

| Сцена | Батчинг | draw calls (batches) | склеено в батчи | SetPass |
|---|---|---|---|---|
| стартовая комната | вкл | 251,5 (макс. 276) | 77,5 вызовов → 28 батчей | 229 |
| стартовая комната | выкл | 286,0 (макс. 286) | 0 | 196 |
| + 12 стреляющих врагов (Bullet Kin, Shotgun Kin) | вкл | 455,7 (макс. 480) | 106,3 → 25,7 | 425 |
| + 12 стреляющих врагов | выкл | 545,3 (макс. 597) | 0 | 435 |

Без батчинга draw calls больше на **+14 % (+35) в пустой комнате и +20 % (+90) в бою**; иначе говоря, батчинг
экономит 12–16 %. Время кадра редактора не изменилось (13,7 / 13,6 и 14,9 / 14,7 мс). Склеивать
батчингу почти нечего: SetPass-вызовов почти столько же, сколько draw calls (у соседних спрайтов разные материалы),
а Pixelator рисует кадр в несколько проходов по рендер-текстурам. В браузере (release) GL занимал 3–4 мс на кадр; +20 % draw calls —
порядка **+0,4–0,8 мс** (оценка, не замер). Не покрыто: тяжёлые бои с боссами (сотни пуль), этажи модов,
co-op.

Вывод: ради динамического батчинга оставаться на 6.0–6.5 не нужно; это не повод выбирать 6.3 LTS вместо 6.6.

## Память: поддержка Memory64 в браузерах

По документации Unity 6.6 (`wasm-64bit-prereq`): Chrome **128+** и Edge 128+ (Windows/macOS/Linux; Android 146+),
Firefox 129+ (Android 148+), **Safari не поддерживает** (26.5). В университете, скорее всего, Chrome — он
обновляется сам, так что версия почти наверняка подходит (проверить `chrome://version`; на управляемых машинах
обновления иногда держат). Важнее железо: куча 4+ ГБ в одной вкладке требует столько же свободной RAM, на машине
с 8 ГБ это уже на грани. WebAssembly 2023 (обязателен при wasm64) — Chrome 91+.

wasm32 с 4 ГБ работает во всех браузерах с WebGL 2 (Chrome/Edge/Firefox, Safari 15+).

## Оценка по областям (рабочие дни)

| Область | Unity 6.6 | 2021.3 / 2022.3 |
|---|---|---|
| Проект и пакеты (манифест, ugui/timeline, Auto Reference, asmdef) | 0,5 (сделано в spike) | 0,5 |
| Patcher + `verify` (переезды типов, Timeline, 7 удалённых членов, стабы GUIText/GUITexture/GUIElement/GUILayer, MovieTexture/ProceduralMaterial для редактора и плеера) | 1–2 | 1–2 (PhysicMaterial ещё не переименован) |
| WebShim (пересборка, BootLoader, UnityWebRequest) | 0,5 | 0,5 |
| jslib | 0,2 | 0,2 |
| Страница (`createUnityInstance`, IDBFS, сохранения) | 1 | 1 |
| Редакторные скрипты, `apply.sh`, abtest/variant-selection без Graphics Emulation | 1 | 1 |
| Шейдеры (сборка под WebGL, abtest, variant-selection, правки fix_shaders) | 1–2 (бандлы уже собрались без ошибок шейдеров) | 1–3 (не проверено) |
| Отладка в плеере (первая сборка, smoke-тест, регрессии IL2CPP/рендера/производительности) | 3–7 | 3–7 |
| **Итого** | **≈ 8–15** | **≈ 8–15** |

По объёму 6.6 и 2021/2022 почти одинаковы: основные поломки (UGUI/Timeline как пакеты, переезды модулей, удалённые
GUIText/GUITexture/`Camera.hdr`, новый загрузчик, новый Emscripten, нет Graphics Emulation) появились ещё в
2019–2021. У 6.6 сверху: `PhysicsMaterial`, Dictionary-сериализация, нет динамического батчинга (по замеру
−12–16 % draw calls, мелочь), Built-in RP устарел. У 2021/2022: надо скачать редактор (~3–4 ГБ с WebGL-модулем, нужно согласие), поддержка 2021.3 и 2022.3
LTS уже закончилась, потолок 4 ГБ навсегда.

## Рекомендация

**Переходить на Unity 6, а не на 2021/2022; начинать с wasm32 и потолка 4 ГБ, wasm64 включать только если 4 ГБ
не хватит.** Остаться на 2017.4 — не вариант для «ещё модов».

- **Против 2017.4 + экономии памяти.** Потолок 2032 МБ жёсткий, а каждый оверхол-мод приносит свои комнаты,
  текстуры (у Expand они `readable`, то есть в куче и на CPU) и код. Ужать можно (не держать все бандлы
  загруженными после старта, release вместо dev, `readable` только где мод читает пиксели), но это выигрыш
  в десятки процентов против двукратного запаса у 4 ГБ, и каждый новый мод снова упирается в потолок. Плюс
  хаки сборки 2017.4 (asm2wasm со стеком, патчи `UnityEditor.WebGL.Extensions.dll`, 1 ч 45 мин на модовую
  dev-сборку) никуда не денутся. Экономия памяти имеет смысл как короткий шаг, если нужно играть прямо сейчас.
- **Unity 6 против 2021.3/2022.3.** Работы почти столько же (см. таблицу), но 6.x уже установлен, поддерживается,
  с новым Emscripten и быстрее собирает, и в нём есть путь дальше 4 ГБ. У 2022.3 надо качать редактор, поддержка
  кончилась, потолок 4 ГБ навсегда.
- **Какая именно 6.x: 6.6.** Динамический батчинг, удалённый в 6.6, по замеру экономит всего 12–16 % draw
  calls (+0,4–0,8 мс на кадр в браузере по оценке), так что 6.3 LTS / 6.5 ради него не нужны. В пользу 6.6: уже
  установлен, единственный с wasm64. Против: не LTS (поддержка до следующего выпуска) и Built-in RP «устарел»;
  когда выйдет 6.7 LTS (Built-in RP поддерживается до конца её срока), перейти на неё — это мелкий апгрейд.
  Maximum Memory Size начать с 4096 МБ (wasm32), wasm64 включать, только если не хватит.
- **Порядок работ** (каждый шаг проверяется до первой долгой сборки): патчер + `verify` с путями 6.6 → чистый
  импорт без ошибок (как import6, плюс стабы GUIText/GUITexture/GUIElement/GUILayer) → редакторные скрипты и
  Play From Boot → `modsmoke.sh` в Play Mode → abtest/variant-selection → бандлы → jslib и страница → первая
  dev-сборка → `?smoketest` в браузере.

## Файлы

- Копия проекта для spike: `tools/u6spike/Project` (можно удалить; ~1 ГБ Assets + Library), логи
  `tools/u6spike/import*.log`.
- Сравнение include-ов: `UnityShaderVariables.cginc` 2017.4 → 6.6 различаются на 28 строк; объявления, на которые
  опираются декомпилированные шейдеры и `fix_shaders.py`, на месте.
