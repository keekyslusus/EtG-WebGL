# Задача: мод Expand the Gungeon в веб-сборке

## Цель

Добавить в веб-сборку (WebGL) мод **Expand the Gungeon** (ApacheThunder, 3.0.8: новые этажи, предметы, враги,
свои шейдеры и звук) так, чтобы он работал в браузере как в Steam. Первый набор модов (SilverJacket + Alexandria +
Mod the Gungeon API) уже работает — механизм есть, его надо довести до мода с собственными AssetBundle, шейдерами,
звуковыми банками и десятками MonoMod-хуков.

## Контекст

Приватный порт Enter the Gungeon (Unity 2017.4.27f1, Mono-сборки игры) в WebGL 2. Корень:
`C:\Users\pooky\Desktop\vm\etg`, ветка `mods`. Порт приватный, ничего не публиковать.

Сначала прочитай:
- `README.md` и `port/README.md` целиком, особенно разделы **Mods**, **Sound**, про шейдеры и сборку;
- код `port/ModBaker`, `port/ModFacades`, `port/WebShim/ModFiles.cs`, `port/WebShim/BundleShim.cs`,
  `port/Patcher/Program.cs`, `port/apply.sh`, `port/mods/`.

### Как работают моды сейчас (кратко)

IL2CPP не умеет загружать DLL и патчить методы во время игры, поэтому набор модов **запекается при сборке**:

- `port/mods/modpack.txt` — список пакетов Thunderstore; `port/mods/fetch_mods.sh` копирует их из кеша r2modman
  (`%APPDATA%\r2modmanPlus-local\ETG\cache\<пакет>\<версия>`) в `etg/mods/` (не в git).
- `port/ModBaker` (net472, гоняет настоящие HarmonyX/MonoMod из BepInExPack): HarmonyX сам собирает тела
  пропатченных методов, они переносятся в DLL игры через Cecil; патчи на движок/mscorlib — обёртки
  `ModBaker.Redirects` + перенаправление вызовов (включая generic-перегрузки, напр. `Instantiate<T>`);
  `new Hook(from, to)` MonoMod — статические трамплины (`HookBaker`: from/to вычисляются символьным исполнением
  кода `typeof(X).GetMethod(...)` перед конструктором); reverse-патчи; вычистка кода времени патчинга;
  `AccessFixer` — публичные трамплины `ModBaker.Access.*` (Mono в редакторе проверяет доступ, IL2CPP — нет);
  `ArgumentFixer`; `ResourceFiles` — файлы встроенных ресурсов для IL2CPP.
- `port/ModFacades` — заглушки `BepInEx` (+Chainloader), `0Harmony`, `MonoMod.RuntimeDetour`.
- `port/Patcher` — общая WebGL-адаптация (потоки, файлы, AssetBundle → `BundleShim`, P/Invoke, ...), моды идут
  через него как игра. Таблицы для частных случаев модов: `LiteralShims`, `ReflectionInvokerBodies`,
  `LogSwallowedExceptions`, `StubbedMethods`, заглушки типов (`WebShim/SystemStubs.cs`).
- Файлы модов (спрайты и т.п.) — `StreamingAssets/mods/files.pack` (`port/mods/build_pack.py`), Boot-сцена
  распаковывает в `/mods/BepInEx/plugins/<пакет>/...`, Chainloader стартует на загрузке первой сцены игры.

### Рабочий цикл

1. **Unity должен быть закрыт**, иначе `apply.sh` откажется (горячая перезагрузка DLL ломает домен редактора).
2. Добавь пакет в `port/mods/modpack.txt`, `bash port/mods/fetch_mods.sh`, затем `bash port/apply.sh --mods`.
   Бейкер печатает `== Done, no problems` или список проблем — их надо разобрать все.
   `ModBaker.exe inventory/scan` (см. `Program.cs`) — что мод использует из чужих сборок.
3. **Play Mode** (EtG Web → Play From Boot) — быстрая проверка без сборки; пользователь запускает сам, лог —
   `%LOCALAPPDATA%\Unity\Editor\Editor.log` (читай сам, не проси вставлять).
   Ограничения Play Mode: бандлы имитируются из AssetDatabase (`BundleShim.Simulation`), звука нет, редактор
   запрещает создавать ScriptableObject в конструкторах компонентов (в игре и WebGL можно).
4. **WebGL-сборка** ~30 мин:
   `"/c/Program Files/Unity/Hub/Editor/2017.4.27f1/Editor/Unity.exe" -batchmode -quit -projectPath "C:\Users\pooky\Desktop\vm\etg\ripped\ExportedProject" -executeMethod EtgWebBuild.BuildDevelopment -logFile <абсолютный путь>`
   (путь проекта — абсолютный Windows-путь). Пересобирай только когда это действительно нужно.
5. Проверка в браузере: preview «site» из `.claude/launch.json` (порт 8081). Скрытая панель браузера почти не
   даёт кадров: клавиши посылай через JS `KeyboardEvent` keydown → скриншот → keyup. Логи удобно ловить
   перехватом `console.*` в `window.__log` до нажатия Play. Консоль MtG — F2, `give <id>`.

## Что известно про Expand the Gungeon

Пакет: `ApacheThunder-ExpandTheGungeon\3.0.8` в кеше r2modman (`ExpandTheGungeon.dll`, бандлы `expandsharedauto`
17 МБ, `expandaudio` 53 МБ, `expandshaders` (+ `_linux`, `_macos`)). Зависит только от MtG API. Исходники:
https://github.com/ApacheThunder/ExpandTheGungeon (ветка BepInEx). Бандлы собраны Unity 2017.4.40f1 под
Windows.

### 1. Хуки (основной объём)
- 26 `[HarmonyPatch]` и **57 `new Hook(...)`**. HookBaker пока видел один хук (SilverJacket) — жди новых
  шаблонов кода перед конструктором и расширяй символьный исполнитель; всё, что он не понял, он пишет в проблемы.
- **Хуки, которые включаются и выключаются во время игры.** Часть хуков создаётся в командах консоли или по
  настройкам (`ExpandConsole`, `ExpandSettings`), ~8 мест вызывают `Dispose`/`Undo`. Сейчас HookBaker вшивает
  каждый хук навсегда — для таких хуков поведение разойдётся с игрой. Нужен флаг на хук: трамплин проверяет
  его и иначе вызывает оригинал, а facade `Hook` (ctor / `Apply` / `Undo` / `Dispose`) этот флаг переключает
  (ключ — пара from/to). Хуки из статических инициализаторов и `Init` должны вести себя как сейчас.

### 2. AssetBundle мода
- Загрузка: `ExpandAssets.LoadFromModZIPOrModFolder` → `AssetBundle.LoadFromFile(FilePath + "/" + name)` (или
  Ionic.Zip + `AssetBundle.LoadFromStream`, если мод лежит zip-ом — Patcher этот вызов пока считает
  необработанным), `LoadAssetBundleFromResource` → `LoadFromMemory`. Бандлы Windows-сборки WebGL не загрузит.
- **Исходники всех бандлов есть в репозитории мода** (ветка BepInEx): `ExpandAssets/` — Unity-проект
  (2017.4.40f1) с префабами, комнатами, спрайтами, текстурами и Wwise-проектом, из которого собираются
  `expandsharedauto` и `expandaudio`; `ExpandShaders/` — проект с 8 шейдерами в исходниках (`ExpandGlitchBasic`,
  `ExpandGlitchScreen`, `ExpandCRT`, `ExpandScanlines`, `ExpandVHSPostProcessEffect`, `ExpandCinematic`,
  `ExpandChromaKey`, `ExpandBlendTextures`). `UpdateAssetBundles.cmd` показывает, что куда копируется.
  Значит, бандлы надо **собрать из исходников под WebGL** (своим Unity 2017.4.27f1 или в отдельном проекте),
  а не ripать AssetRipper'ом: никакой декомпиляции шейдеров.
- Шейдеры из исходников: артефактов декомпилятора (то, ради чего был shaderdiff) не будет, и большинство
  различий D3D11/GLES Unity снимает сам при компиляции под платформу. Остаются ошибки самого исходника,
  которые на D3D не видны: переворот UV у пост-эффектов с render texture (`ExpandGlitchScreen`, `ExpandVHS…`,
  `ExpandCRT`, `ExpandScanlines`), точность `half`/`fixed` (на GLES — mediump/lowp), `#pragma target` выше 3.5.
  Полный shaderdiff не нужен; достаточно прогнать эти 8 шейдеров через A/B-галерею (`bash port/abtest.sh`,
  `EtgAbTest.cs`) D3D11 vs GLES и посмотреть эффекты в браузере глазами.
- Дальше как с бандлами игры: собранные WebGL-бандлы научить `BundleShim` (список, предзагрузка в Boot, отдача
  по `LoadFromFile`/`LoadFromStream`/`LoadFromMemory`) и Play Mode (имитация).
- MonoBehaviour мода в префабах ссылаются на классы `ExpandTheGungeon.dll`: у DLL в `Assets/Plugins` должен быть
  стабильный GUID (`.meta`) до экспорта/сборки, иначе скрипты в префабах потеряются.
- `ExpandShaders.ApplyGlitchShader` и подобные — проверь эффекты глазами (глитч, голограмма, радуга).
- Размер: +70 МБ к загрузке; подумай о сжатии/кеше (бандлы игры уже кешируются в IndexedDB по хешу).

### 3. Звук
- `ExpandAssets.InitAudio`: банк — `TextAsset` в бандле `expandaudio` → `AkSoundEngine.LoadAndDecodeBankFromMemory`.
  `.wwu` в исходниках — это файлы проекта Wwise (исходники), игре не нужны.
- Звук у нас — свой движок на Web Audio (`port/audio`, раздел Sound в `port/README.md`): `wwbank.py` разбирает
  банки (версия 128 — проверь, что у мода такая же), `build_audio.py` строит `audio.json` и паки Opus.
- План: на этапе бейка достать банки мода, прогнать через тот же конвейер в дополнительный пак, который движок
  подгружает вместе с основным (события/объекты адресуются FNV-хешами имён, ссылки на шины игры должны
  работать); `LoadAndDecodeBankFromMemory`/`LoadBank` в `WwiseShim` — без нативного Wwise, «уже загружено».
- Проверка — `port/WwiseRef` (настоящий `AkSoundEngine.dll` офлайн) + `audiodiff.py` по событиям мода.
  Эффекты-плагины, которых нет у игры, потребуют моделей (`measure_fx.py`).

### 4. Прочее, что всплывёт
Опыт первого набора модов (всё уже исправлено, но похожее будет): Reflection.Emit (`DynamicMethod`) под IL2CPP
не работает; встроенные ресурсы требовали `*-resources.dat`; Mono-тольковые вольности с типами аргументов не
компилируются в C++ IL2CPP; старый mcs Unity не читает сборки с атрибутами C# 8 на ограничениях; типы, которых
нет в WebGL-профиле .NET (`BindingList`), — заглушки. Проверка ссылок (`Patcher verify` в `apply.sh`) ловит
отсутствующее до сборки. Релизная сборка (`BuildRelease`) не ловит NullReferenceException (только явно брошенные
исключения) — если мод полагается на пойманный NRE, в релизе будет падение; проверяй на dev-сборке.

## Готово, когда
- `apply.sh --mods` с Expand the Gungeon в `modpack.txt` проходит без проблем бейкера и с чистым `verify`.
- В браузере (dev-сборка): мод стартует без исключений, его этажи/предметы/враги доступны и работают (проверь
  несколько предметов через `give`, вход на новый этаж), шейдеры мода выглядят как в Steam, звуки мода играют.
- Хуки, которые мод включает/выключает во время игры, ведут себя как в Steam.
- `port/README.md` (раздел Mods и, если менялся звук, Sound) описывает сделанное; если что-то из списка не
  удалось — честно перечисли, что и почему.
- Коммиты — в ветку `mods`, с согласия пользователя.
