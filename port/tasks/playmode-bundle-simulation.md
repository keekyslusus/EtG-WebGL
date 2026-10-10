# Задача: запуск порта EtG в Play Mode редактора Unity (симуляция AssetBundle)

## Контекст

Это приватный порт Enter the Gungeon (Unity 2017.4.27f1, Mono) в WebGL. Корень: `C:\Users\pooky\Desktop\vm\etg`.
Прочитай сначала `README.md` — там структура и полный конвейер сборки.

Коротко, как устроен порт:
- `ripped/ExportedProject` — Unity-проект, экспортированный AssetRipper. Код игры лежит **не исходниками**, а
  оригинальными DLL в `Assets/Plugins`, пропатченными на уровне IL. Переименованы: `Assembly-CSharp` → `EtgGame`,
  `Assembly-CSharp-firstpass` → `EtgGameFirstpass`, `Assembly-UnityScript` → `EtgUnityScript` (Unity 2017 игнорирует
  плагины с зарезервированными именами; `.meta` с GUID перенесены, на них ссылаются сцены и префабы).
- `port/Patcher/Program.cs` — патчер на Mono.Cecil: глушит P/Invoke (Wwise, Steam, Galaxy…), перенаправляет
  `Thread`, `AssetBundle.LoadFromFile`, `File.*`, `Screen.resolutions` и т.д. в `WebShim`, переименовывает сборки,
  подменяет типы, вырезанные из WebGL, на стабы. Режим `verify` проверяет, что все ссылки разрешаются против
  WebGL-библиотек Unity.
- `port/WebShim/*.cs` — рантайм-прослойка (net35, `LangVersion 7.3`, компилируется против DLL из
  `Enter the Gungeon/EtG_Data/Managed`). Важное для тебя:
  - `BundleShim.cs` — `LoadFromFile(path)` возвращает бандл, заранее скачанный `BootLoader`'ом. Вне WebGL
    сейчас падает обратно на настоящий `AssetBundle.LoadFromFile`.
  - `BootLoader.cs` — MonoBehaviour в сцене `Assets/Scenes/Boot.unity` (build index 0): качает все 30 бандлов
    из `StreamingAssets/Assets` через `UnityWebRequest.GetAssetBundle`, регистрирует их в `BundleShim`, грузит
    сцену 1 (`LoadingDungeon`, оригинальный `level0` игры).
- `port/UnityAssets/` — копируется в `Assets/` проекта: меню **EtG Web** (`Editor/EtgWebBuild/EtgWebBuild.cs`,
  asmdef только для редактора), `link.xml`, `.jslib`.
- `port/apply.sh` — собирает WebShim и Patcher, патчит оригинальные DLL из папки игры, ставит всё в проект,
  запускает `verify`. Безопасно запускать при открытой Unity (после — Ctrl+R в редакторе), **кроме** первого
  запуска на свежем рипе: там переименование DLL/`.meta`, и Unity должна быть закрыта.

Почему нужна задача: полная WebGL-сборка (IL2CPP → C++ → wasm) занимает ~13 минут, а почти все текущие ошибки —
рантаймовые и не специфичные для wasm (логика, данные, форматы текстур, шейдеры). Их можно ловить в Play Mode за
секунды. Мешают только **бандлы**: в `Assets/StreamingAssets/Assets` лежат бандлы, собранные под WebGL, а
редактор отказывается их грузить (не та платформа). Собирать отдельные бандлы под Windows дорого: каждая смена
платформы переимпортирует ~975 текстур.

## Цель

В Play Mode (старт со сцены `Boot.unity`) игра работает **без загрузки бандлов**: ассеты берутся прямо из проекта
через `AssetDatabase` по уже прописанным именам бандлов (`assetBundleName` в `.meta` — 4208 ассетов и 4 сцены,
см. `SceneBundles` в `EtgWebBuild.cs`). Это аналог «Simulation Mode» из Unity Asset Bundle Manager.

WebGL-сборка при этом должна вести себя **ровно как сейчас**.

## Предлагаемый дизайн (можешь улучшить, но объясни почему)

1. **Режим симуляции в `BundleShim`.** Включается только при `Application.isEditor` (и, например, флагом,
   который ставит пункт меню `EtG Web/Simulate Bundles In Play Mode`, хранится в `EditorPrefs`; читать его из
   рантайм-DLL — через reflection или через статическое поле, которое выставляет `[InitializeOnLoad]`-класс в
   редакторской сборке). В этом режиме `LoadFromFile(path)` вместо настоящего бандла возвращает «плейсхолдер»,
   управляемый объект `AssetBundle` без нативной части (например,
   `FormatterServices.GetUninitializedObject(typeof(AssetBundle))`), и запоминает плейсхолдер → имя бандла
   по ссылке. ⚠️ Оператор `==` у `UnityEngine.Object` для такого объекта вернёт «null». Проверь все места, где
   игра сравнивает бандл с null (`BraveResources`, `*Database`, `FoyerPreloader`, `GameManager`,
   `PlaceableAsyncSceneLoader`, `ResourceManager` — см. `decompiled/Assembly-CSharp/`). Если плейсхолдер так
   не работает, предложи другой способ сохранить идентичность бандла.
2. **Патчер:** перенаправить все **инстансные** вызовы `UnityEngine.AssetBundle` в коде игры (`LoadAsset(string)`,
   `LoadAsset(string, Type)`, generic `LoadAsset<T>` — это `GenericInstanceMethod`, нужен такой же generic
   wrapper в шиме, `LoadAllAssets*`, `LoadAssetAsync*`, `GetAllScenePaths`, `GetAllAssetNames`, `Contains`,
   `Unload`, …; точный список собери сканированием IL через Cecil) на статические методы `BundleShim` с бандлом
   первым параметром. Настоящий бандл → вызывать настоящий метод. Плейсхолдер → через reflection к
   `UnityEditor.AssetDatabase`: `GetAssetPathsFromAssetBundleAndAssetName(bundle, name)` +
   `LoadAssetAtPath(path, type)`; для сцен — `GetAssetPathsFromAssetBundle(bundle)`. На `UnityEditor` из
   рантайм-DLL **нельзя ссылаться напрямую**: её нет в плеере, а IL2CPP/`verify` упадут.
3. **Сцены из бандлов** (`tt_foyer`, `Foyer`, `Foyer_Coop`, `tt6`): игра грузит их через
   `SceneManager.LoadScene(GetAllScenePaths()[0])`. В Play Mode Unity 2017.4 грузит только сцены из Build
   Settings, поэтому пункт меню для Play Mode должен временно добавлять их туда. Проверь, что
   `EtgWebBuild.BuildPlayer` передаёт явный список из двух сцен, и что `SetupBootScene` (он перезаписывает
   build settings) не ломает Play Mode и наоборот.
4. **`BootLoader` в режиме симуляции:** ничего не качать, зарегистрировать плейсхолдеры для `BundleShim.AllBundles`,
   сразу грузить сцену 1.
5. **Удобство:** пункт меню `EtG Web/Play From Boot` (включает симуляцию, открывает `Boot.unity`, нажимает Play).
   По желанию — `Edit → Graphics Emulation → WebGL 2.0`, чтобы редактор ближе повторял WebGL.

## Ограничения и подводные камни

- **Unity 2017.4, legacy .NET 3.5 runtime.** Редакторские скрипты компилируются как C# 4: без интерполяции
  строк, `?.`, expression-bodied членов, `nameof`. WebShim компилируется отдельно (`dotnet build`), там C# 7.3,
  но API только .NET 3.5.
- Проверка редакторского скрипта без Unity: собери временный net35-проект (`LangVersion 4`) с файлом
  `EtgWebBuild.cs` и ссылками на
  `C:\Program Files\Unity\Hub\Editor\2017.4.27f1\Editor\Data\Managed\UnityEditor.dll`,
  на фасад `Enter the Gungeon/EtG_Data/Managed/UnityEngine.dll`, на модули из `...\Editor\Data\Managed\UnityEngine\`
  (CoreModule, IMGUIModule и т.д.) и на `port/build/WebShim/WebShim.dll`. Монолитный редакторский
  `UnityEngine.dll` вместе с модулями даёт конфликт типов — не подключай их одновременно.
- После `apply.sh` обязательно пройти `verify` («All references resolve.»). Новые методы шима должны
  разрешаться против WebGL-библиотек.
- Лог Unity: `%LOCALAPPDATA%\Unity\Editor\Editor.log` (после перезапуска редактора предыдущий —
  `Editor-prev.log`). Читай его сам, не проси пользователя копировать.
- Unity не всегда замечает изменённые файлы — проси пользователя нажать **Ctrl+R**.
- Ничего не коммить и не публикуй: в репозитории только `port/`, `README.md` и служебные файлы, а
  `.gitignore` работает белым списком. Файлы игры и рип защищены авторским правом, проект приватный.
- Не трогай `C:\Program Files\Unity\...` (там уже пропатчен `asm2wasm.exe`, оригинал лежит рядом как `.orig`).

## Критерии готовности

1. `bash port/apply.sh` проходит, `verify` — «All references resolve.»
2. В Unity: **EtG Web → Play From Boot** → игра без ошибок загрузки бандлов доходит до титульного экрана
   (заставка Dodge Roll → титульник с крепостью) и до Breach. Остальные рантайм-ошибки игры ожидаемы, их чинить
   не нужно, но перечисли, что увидел в Play Mode.
3. Полная WebGL-сборка (**EtG Web → 4. Build WebGL Player**) и **Rebuild Bundles Only → webbuild** работают
   как раньше; в браузере (`python -m http.server 8080 --directory webbuild`) поведение не изменилось.
4. В `README.md` добавлен короткий раздел «Быстрая проверка в Play Mode».

## Известные текущие проблемы (для справки, не цель задачи)

Состояние на 2026-10-11: в браузере игра проходит интро, титульник (меню PLAY/OPTIONS/QUIT), Breach и
**загружает первый этаж** — персонаж, HUD, миникарта, генерация подземелья работают.

- **Освещение / темнота (главная проблема).** Breach виден частично и очень тёмный, на этаже пол и стены
  почти полностью чёрные, видны только персонаж и HUD. Вероятно, конвейер света EtG
  (`Pixelator`, `Brave/Internal/GBuffer_Light*`, `GBuffer_LightMask*`): декомпилированные шейдеры света или
  форматы render texture, которые WebGL 2 не умеет рендерить (float/half без `EXT_color_buffer_float`).
- Титульник: внизу белая заливка вместо тёмной воды — вероятно `Brave/Effects/TitleWaterShader`.
- `InvalidOperationException: Nullable object must have a value` в
  `BraveOptionsMenuItem+WindowsResolutionManager.TrySetDisplay` — оконный Windows-код, на WebGL не нужен.
- `Unable to find clip 'select_idle' in library` из `CharacterSelectIdleDoer` (ссылки на анимационные
  библиотеки выглядят корректными), `Material doesn't have a float or range property '_StencilVal'`.
- Текстуры: шаг `Fix Texture Formats` (оригинальные форматы для WebGL) убрал ошибки
  `Unsupported texture format` / `Can't resize to a compressed texture format`.
- Звука нет намеренно (Wwise заглушён).
