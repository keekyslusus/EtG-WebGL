# Задача: автоматический smoke-тест содержимого модов

## Цель

Сделать автоматическую проверку **всего содержимого модов** (предметы, оружие, враги, этажи) в редакторном
Play Mode, чтобы не проверять каждый предмет руками. Один прогон должен выдать отчёт «что → какие исключения», по
которому потом чинить порт (или фиксировать баги самих модов).

## Контекст

Приватный порт Enter the Gungeon (Unity 2017.4.27f1) в WebGL 2, корень `C:\Users\pooky\Desktop\vm\etg`, ветка `mods`.
Порт приватный, ничего не публиковать. Моды (BepInEx/r2modman) **запекаются при сборке**: Mod the Gungeon API,
Alexandria, Silver Jacket, Expand the Gungeon (`port/mods/modpack.txt`).

Сначала прочитай: `README.md`, `port/README.md` целиком (особенно **Mods** и **Expand the Gungeon**), код
`port/UnityAssets/Editor/EtgWebBuild/` (меню `EtG Web`, `EtgWebPlayMode.cs` — Play From Boot, `EtgWebDebug.cs`),
`port/WebShim/BundleShim.cs` (симуляция бандлов в Play Mode), `port/WebShim/ModFiles.cs` (старт Chainloader).

### Рабочий цикл

1. **Unity должен быть закрыт** для `bash port/apply.sh --mods` (иначе он откажется: горячая перезагрузка DLL ломает
   домен редактора). Скрипты из `port/UnityAssets/` копируются в проект тем же `apply.sh`.
2. Play Mode запускает **пользователь** (EtG Web → Play From Boot, или пункт меню, который ты добавишь). Лог —
   `%LOCALAPPDATA%\Unity\Editor\Editor.log`, читай сам, не проси вставлять. Компьютер-юз на Unity неудобен.
3. WebGL-сборка — **только когда без неё никак**: модовая dev-сборка идёт ~1 ч 45 мин (asm2wasm на 3.45 ГБ asm.js).
   Пользователь явно просил отлаживать в Play Mode. Сборка — пропатченной копией Unity без прав админа:
   `tools/unity-2017.4.27f1/Editor/Unity.exe -batchmode -quit -projectPath "C:\Users\pooky\Desktop\vm\etg\ripped\ExportedProject" -executeMethod EtgWebBuild.BuildDevelopment -logFile <абс. путь>`
   (см. README, Building: `patch_webgl_noasm.py`).

### Что уже известно про Play Mode (не путать с ошибками порта)

- Редактор запрещает `ScriptableObject`/`LoadAssetAtPath` в конструкторах компонентов («…is not allowed to be called
  from a MonoBehaviour constructor») — в игре и WebGL можно. Такие ошибки в отчёте помечать отдельно, а не чинить.
- Префабы бандлов в Play Mode — ассеты проекта (`BundleShim` симуляция): для бандлов модов отдаются копии,
  `Renderer.material` у ассетов идёт через `WebShim.EditorPrefabShim`.
- Mono в редакторе проверяет доступ к членам (IL2CPP — нет): `MethodAccessException`/`FieldAccessException` — это
  ошибка `port/ModBaker/AccessFixer.cs`, чинить там.
- Обратное: Play Mode **не** покажет ошибок только WebGL/IL2CPP (null-проверки `this`, нехватка памяти, отсутствие
  стека для `GetCallingAssembly`, Reflection.Emit). Их ловить отдельным прогоном в браузере (см. «Потом»).
- Известные «шумные» ошибки самой игры, не связанные с модами: `DataPrivacy` (Cannot override system-specified
  headers), `Something is attempting to get a NONE save flag!`, `INSTANTIATING AMMONOMICON ???`,
  `Doing startup foyer checks!`, `No mid game save found`, `dfInputManager.processMouseInput` NRE, `Unable to find
  clip 'select_idle'`, `Material doesn't have a float or range property '_StencilVal'`. Отфильтровать.

## Что сделать

Редакторный скрипт (в `port/UnityAssets/Editor/EtgWebBuild/`, C# 4 — редактор 2017.4 с .NET 3.5: без
интерполяции строк, `?.`, expression-bodied) + при необходимости компонент рантайма, запускаемый пунктом меню
вроде **EtG Web → Mod Smoke Test**:

1. Play From Boot, дождаться загрузки модов (Expand the Gungeon грузится в несколько шагов — дождаться конца,
   `ExpandTheGungeon.loadStatus` / его экрана загрузки), выбрать персонажа и начать забег без участия человека
   (через API игры: `GameManager`, `Foyer`/выбор персонажа, загрузка первого этажа; не через эмуляцию клавиш).
2. Для **каждого предмета модов** (перечислить через `PickupObjectDatabase` / базы MtG API — ID вида `ex:...`,
   `slvjckt:...`, предметы Alexandria; полный список и способ узнать «чей» предмет — найти в коде MtG API):
   выдать игроку (`LootEngine.GivePrefabToPlayer` или как делает команда `give`), для активных — использовать,
   для оружия — несколько выстрелов и перезарядка, подождать пару кадров, выбросить/удалить. Обязательно и
   покупку из магазина хотя бы для нескольких (ошибки бывают в `ShopItemController.Interact`).
3. Каждого **врага модов** (Expand the Gungeon добавляет своих; через `EnemyDatabase` GUID) — заспавнить рядом,
   подождать, убить.
4. Каждый **этаж/flow мода** — загрузить (`load_flow` из `DungeonFlowModule.cs` Expand the Gungeon,
   `GameManager.LoadCustomFlowForDebug`), дождаться генерации, пройти несколько кадров.
5. Ловить **все** исключения и ошибки лога (`Application.logMessageReceived`), привязывая к текущему шагу (ID
   предмета/врага/этажа), считать ошибки за кадр (зависание вида «исключение каждый кадр в
   `PhysicsEngine.LateUpdate`» должно быть видно), таймаут на шаг (если шаг завис — записать и идти дальше).
   Один упавший предмет не должен останавливать прогон.
6. Отчёт в `port/build/modsmoke/report.md` (+ json): по каждому шагу — OK / исключения (тип, сообщение, первые строки
   стека, сколько раз), с разделением «ограничение Play Mode» / «ошибка порта» / «вероятно баг мода» по эвристикам
   выше. Сводка вверху.
7. Задокументировать в `port/README.md` (раздел Mods): как запускать, что проверяется, что нет.

Потом (по согласованию с пользователем): разобрать отчёт и чинить ошибки порта; тот же сценарий при необходимости
прогнать в WebGL-сборке (компонент рантайма, запуск по `?smoketest` в URL страницы `port/site`, отчёт в консоль),
чтобы поймать IL2CPP-специфичное.

## Состояние на начало задачи (предыдущая задача — Expand the Gungeon)

Сделано и описано в `port/README.md` (Mods → Expand the Gungeon): бандлы мода собираются из его исходников
(`port/mods/mod_bundles.py`), хуки MonoMod запекаются со «слотами» вкл/выкл, звук мода в Web Audio, видео через URL,
`GetCallingAssembly` для IL2CPP, исходные имена ассетов игры (`port/bundle_names.py`), память WebGL 2032 МБ.

Проверено: в браузере мод грузится без ошибок мода, `expand list_items` и `give ex:sonic_box` работают; в Play Mode
пользователь прошёл несколько этажей. **Не проверено**: этажи мода, враги мода, шейдеры мода в реальной игре, видео
(VHS), звук мода на слух. **В `webbuild/` устаревшая сборка**: без исправлений `BakedHook.TargetAs` (хуки с
`typeof(X)` как target — NRE каждый кадр в IL2CPP), AccessFixer для private-членов, `EditorPrefabShim`,
`SilverJacketData`. Пересобирать WebGL только после того, как smoke-тест в Play Mode станет чистым.

## Готово, когда

- Один пункт меню запускает весь прогон без участия человека и выдаёт отчёт; прогон доходит до конца даже при
  падениях отдельных шагов.
- Отчёт покрывает все предметы, оружие, врагов и этажи модов из `modpack.txt`.
- Описано в `port/README.md`; коммиты в ветку `mods` — с согласия пользователя.

## Состояние (ночь на 2026-10-15)

Сделано: `EtG Web → Mod Smoke Test` (меню) и `bash port/modsmoke.sh [фазы]` (без участия человека: Unity с
`-force-gles30`, сторож зависаний/вылетов, продолжение с места остановки). Описание — `port/README.md`, Mods → Smoke
test. Отчёт — `port/build/modsmoke/report.md` / `.json`.

Итог полного прогона (`all`, 43 предмета, 12 покупок, 31 враг, 8 этажей, 32 flow): **ошибок порта — ноль.**
Разобрано:
- Portable Ship, Iceberg Shavings, Ferryman Oar — ограничения Play Mode (поля, заданные в конструкторах
  MonoBehaviour / `ScriptableObject` в инициализаторах полей, в редакторе остаются `null`); в плеере работают.
- Этаж West (`tt_west`, `F4c_West_Flow_01`) не грузится — баг мода, зависящий от сохранения: для SPECIAL-узла из
  таблицы `basic_special_rooms_noBlackMarket` подходят только `Shrine_Cleanse_Room` (проклятие ≥ 1) и
  `Shrine_CursedMirror_Room` (TIME_PLAYED > 3 ч и TIMES_REACHED_GUNGEON > 2); в свежем сохранении редактора ни одной →
  NRE в `LoopBuilderComposite.GetExitPairsSimple` → бесконечная загрузка. С «наигранным» сохранением должно грузиться.
- Белый прямоугольник на экране загрузки — потерянная AssetRipper'ом текстура `Loading Atlas.mat`
  (`port/fix_materials.py` + `material_textures.tsv`, вызывается из `apply.sh`). Визуально не проверено.

Открыто (не порт либо не выяснено):
- Вылет Mono редактора (`mono_class_from_mono_type: implement me 0x00` при форматировании стека исключения в
  `LateUpdate`) на Cascading Bullets после Bone Parade — плавающий; гипотеза: NRE в `CascadingBullets.OnDidDamage`
  (`target.gameActor` у целей без актора, кость Bone Parade попала в бочку). IL2CPP этим Mono не пользуется.
- Вылет Mono при `DEMO_STAGE_FLOW`: переполнение стека в `mono_traverse_objects` (liveness при `UnloadUnusedAssets`) —
  проверить в WebGL.
- `Custom_GlitchChest_Flow` с `Base_Gungeon` генерируется бесконечно (тайлсет подобран тестом наугад);
  `Bossrush_01_Castle` и `MiniBossRush_01` стабильно не генерируются (6 неудачных итераций подряд в каждом прогоне;
  загружались через `LoadCustomFlowForDebug`, не в режиме босс-раша). Грузятся 28 из 32 flow
  (`report-flows-recheck.md`: `Fruit_Loops`, `Bossrush_04_Catacombs` после поднятия порога неудач генерации до 6).
- NRE в `LoopFlowBuilder.AttachPregeneratedInjectionData` (плавающий, после рестарта забега; `TEST_West_Floor_03a_Flow`,
  `Bossrush_01a_Sewer`) — вероятно, запись глобальной инъекции с пустым `injectionData`.
- `IndexOutOfRange` в `tk2dBaseSprite.GetUntrimmedBounds` у «испорченных» врагов glitch-flow и «Material doesn't have
  _Color» в `ExpandExtensions.Copy` — код мода, на Steam не сравнивалось.
- Потом: тот же сценарий в WebGL (`?smoketest`), чтобы поймать IL2CPP-специфичное.
