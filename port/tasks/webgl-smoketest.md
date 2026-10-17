# Задача: smoke-тест модов в браузерной сборке (`?smoketest`)

## Цель

Запустить тот же smoke-тест модов (`port/UnityAssets/EtgModSmoke`, сейчас только в редакторном Play Mode) в WebGL-сборке
по параметру в URL страницы и получить отчёт того же вида. Так ловится то, что Play Mode показать не может:
ошибки только IL2CPP (null-проверки `this`, нехватка памяти, отсутствие стека для `GetCallingAssembly`, Reflection),
и подтверждается, что «ограничения Play Mode» из отчётов в плеере действительно не возникают.

## Контекст

Приватный порт Enter the Gungeon (Unity 2017.4.27f1) в WebGL 2, корень `C:\Users\pooky\Desktop\vm\etg`, ветка `mods`.
Порт приватный, ничего не публиковать; коммиты — только с согласия пользователя. Моды (Mod the Gungeon API, Alexandria,
Silver Jacket, Expand the Gungeon) запекаются при сборке (`bash port/apply.sh --mods`).

Сначала прочитай: `README.md`, `port/README.md` (особенно **Mods** → Expand the Gungeon и **Smoke test**),
`port/tasks/mod-smoke-test.md` (что тест делает и что уже разобрано), `port/site/README.md` (страница, F3/F4,
экспорт сохранений), код `port/UnityAssets/EtgModSmoke/` и `port/UnityAssets/Editor/EtgWebBuild/EtgModSmokeMenu.cs`,
`port/WebShim/FileShim.cs` (файлы в WebGL: MEMFS + IndexedDB через `FS.syncfs`), jslib в `port/UnityAssets/Plugins/WebGL`.

### Что сейчас мешает запуску в плеере

- `SmokeTest.AutoStart` выходит, если `!Application.isEditor`; старт идёт через файл `Temp/modsmoke.pending`.
- Отчёт пишется в `port/build/modsmoke` относительно `Application.dataPath` (`DefaultReportDir`), продолжение после
  зависания (`resume`, `report.json` прошлой попытки) тоже через этот каталог.
- Зависание главного потока в браузере не перехватить: страница просто встаёт, сторожа как в `port/modsmoke.sh` нет.
- Классификация ошибок рассчитана на редактор (категория `playmode`); в плеере таких ошибок быть не должно, и если
  они всплывут, это интересный результат, а не шум.

### Сборка

- Нужна **development**-сборка: release собирается с `ExplicitlyThrownExceptionsOnly`, там null-разыменование не
  становится исключением, тест ничего не поймает. Модовая dev-сборка идёт **около 1 ч 45 мин** (asm2wasm на 3.45 ГБ
  asm.js). Собирать как можно реже, всё проверить заранее (см. «Рабочий цикл»). Пропатченная копия Unity без прав админа:
  `tools/unity-2017.4.27f1/Editor/Unity.exe -batchmode -quit -projectPath "C:\Users\pooky\Desktop\vm\etg\ripped\ExportedProject" -executeMethod EtgWebBuild.BuildDevelopment -logFile <абс. путь>`
  (README, Building; Unity должен быть закрыт, `apply.sh` перед этим).
- `webbuild/` сейчас **устарел**: в нём нет исправлений с 2026-10-14 (TargetAs, AccessFixer, EditorPrefabShim,
  исправления glitch-этажа, West, параметров ModBaker). Первая же сборка с тестом заодно проверит их все.
- Сборка `EtgModSmoke` (asmdef без ограничений платформ) уже попадает в плеер. Проверить, что UnityLinker не вырежет
  то, к чему тест обращается через reflection: моды сохранены целиком (`Assets/EtgMods/link.xml`, preserve="all"),
  а члены самого теста и игры, доступные только через reflection, — нет (при необходимости свой `link.xml`).

## Что сделать

1. Старт в плеере: страница `port/site` читает `?smoketest[=фазы]` (по умолчанию `all`) и `&only=a,b` (фильтр как
   `only.txt`) и передаёт в игру (jslib → C#, например `SendMessage` после загрузки или экспортируемая функция,
   которую `SmokeTest` вызывает при старте). Без параметра тест не создаётся вообще.
2. Отчёт: те же `report.md` / `report.json` (`ReportWriter`), после каждого шага — в постоянное хранилище
   (IndexedDB через FileShim / `FS.syncfs`), плюс краткая строка в консоль браузера на шаг и кнопка или
   авто-скачивание файла отчёта в конце. Учти: буфер консоли во встроенном браузере теряет старые сообщения.
3. Продолжение после зависания: при перезагрузке страницы с `?smoketest&resume` тест берёт `report.json` из
   хранилища, шаг, который шёл, помечает как `hang` и идёт дальше (как `RESUME=1` у `port/modsmoke.sh`).
4. Не трогать поведение редакторного теста: Play Mode, меню и `port/modsmoke.sh` должны работать как раньше.
5. Прогнать в браузере (встроенный браузер, сервер `launch.json` → `site`, порт 8081; 8080 — сервер пользователя):
   сначала `?smoketest=items,shop`, потом остальное. Скрытая панель браузера стопорит кадры Unity: держать её видимой
   или «прокачивать» кадры скриншотами.
6. Разобрать отчёт и описать в `port/README.md` (Mods → Smoke test: запуск в браузере) и в этом файле.

### Что проверить в первую очередь

- `IndexOutOfRange` в `tk2dBaseSprite.GetUntrimmedBounds` у «испорченных» врагов glitch-этажа: в Steam её нет, в
  Play Mode есть; по разбору это особенность редактора (видимость спрайтов tk2d, см. `mod-smoke-test.md`). Если
  в браузере всплывёт — обрезать номер спрайта в `ExpandUtility.ApplyCustomTexture` в порте.
- Portable Ship, Iceberg Shavings, Ferryman Oar: в Play Mode падают из-за запретов в конструкторах MonoBehaviour,
  в плеере должны работать.
- Исправления этажей в плеере: glitch-этаж (`Custom_GlitchChest_Flow`), Old West на свежем сохранении, boss rush.
- Всё, что помечено в отчёте категорией `port`, и любые ошибки, которых нет в отчётах Play Mode.

### Рабочий цикл

Всё, что не требует плеера, проверять до сборки: C#-код теста компилируется офлайн (MonoBleedingEdge `mcs
-langversion:4` с 2.0-api и ссылками на `Managed/UnityEngine` + `Assets/Plugins`), редакторный тест после изменений —
`bash port/modsmoke.sh items,shop`, jslib и логику страницы — в Node / в браузере на старой сборке (параметр без теста
просто ничего не делает). Перед сборкой спросить пользователя.

## Готово, когда

- `?smoketest` в браузерной dev-сборке проходит все фазы (с продолжением после зависаний), отчёт сохраняется и
  скачивается; без параметра страница и игра ведут себя как раньше.
- Редакторный smoke-тест работает как прежде.
- Результаты разобраны (что подтверждено, что только Play Mode, что новое), описано в `port/README.md` и здесь;
  коммиты в `mods` — с согласия пользователя.
