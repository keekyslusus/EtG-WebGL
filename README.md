# Enter the Gungeon → WebGL

Неофициальный браузерный порт Enter the Gungeon (Unity 2017.4, Mono) для личного использования.
В репозитории только инструменты порта — файлы игры нужно взять из своей купленной копии.
**Не публиковать сборки и риппнутый проект**: это ассеты и код Dodge Roll.

## Что нужно

- Enter the Gungeon 2.1.9 (Steam) — папка игры
- [Unity 2017.4.27f1](https://unity.com/releases/editor/archive) + модуль **WebGL Build Support**
- .NET SDK 9
- [AssetRipper](https://github.com/AssetRipper/AssetRipper/releases) 2.x
- Git Bash (для `apply.sh`), Python 3 (для `setstack.py`)

## Структура

```
Enter the Gungeon/      файлы игры (не в git)
ripped/ExportedProject/ Unity-проект от AssetRipper (не в git)
webbuild/               готовая WebGL-сборка (не в git)
port/
  WebShim/      рантайм-заглушки: потоки, предзагрузка бандлов, сохранения в IndexedDB,
                стабы вырезанных из WebGL типов Unity, загрузочный экран
  Patcher/      Mono.Cecil-патчер DLL игры + `verify` (проверка ссылок против WebGL-библиотек)
  UnityAssets/  то, что копируется в Unity-проект: меню "EtG Web", link.xml, jslib
  apply.sh      собирает WebShim/Patcher, патчит DLL, ставит всё в проект
  setstack.py   увеличивает стек у exe (нужно для asm2wasm)
```

## Сборка

1. Скопировать игру в `Enter the Gungeon/`.
2. **AssetRipper** (настройки: *Script Export Format* = `Dll Export Without Renaming`,
   *Shader Export Format* = `Decompilation`) → открыть папку игры → *Export Unity Project*
   в `ripped/`.
3. Шейдеры: AssetRipper вместо них пишет заглушки (`DummyShaderTextExporter`), поэтому
   собрать [USCSandbox](https://github.com/nesrak1/USCSandbox) в `tools/uscs`
   (`dotnet build -c Release -o tools/uscs` + `Files/classdata.tpk` рядом с exe) и запустить
   `bash port/decompile_shaders.sh` — декомпилирует DX11-байткод всех шейдеров игры в `tools/uscs/out`.
4. **Unity закрыта** → `bash port/apply.sh`
   Патчит DLL, переименовывает `Assembly-CSharp*` → `EtgGame*` (с сохранением GUID),
   заменяет шейдеры-заглушки декомпилированными, убирает Windows-бандлы и Wwise-банки
   из StreamingAssets, проверяет ссылки.
5. **Один раз**: `asm2wasm` из Unity 2017 падает на большой сборке от переполнения стека.
   Увеличить ему стек (нужны права администратора, оригинал сохраняется как `.orig`):
   ```
   python port/setstack.py <копия asm2wasm.exe> 1073741824
   ```
   и подложить копию вместо
   `<Unity>/Editor/Data/PlaybackEngines/WebGLSupport/BuildTools/Emscripten_FastComp_Win/binaryen/bin/asm2wasm.exe`.
6. Открыть `ripped/ExportedProject` в Unity 2017.4.27f1 → меню **EtG Web → Do Everything (1-4)**
   (boot-сцена, настройки WebGL 2 / wasm / .NET 2.0, бандлы, dev-сборка в `webbuild/`).
   Дальше при изменениях порта: `apply.sh` → Ctrl+R в Unity → **4. Build WebGL Player**.
7. Запускать через любой локальный HTTP-сервер из `webbuild/` (с `file://` не работает).

Лог сборки Unity: `%LOCALAPPDATA%\Unity\Editor\Editor.log`.

## Быстрая проверка в Play Mode

WebGL-сборка идёт ~13 минут, а большинство ошибок рантаймовые — их быстрее ловить в редакторе.
**EtG Web → Play From Boot**: открывает `Boot.unity` и запускает Play Mode с симуляцией бандлов.
Бандлы из StreamingAssets (собраны под WebGL) не грузятся: `BundleShim` отдаёт ассеты прямо из проекта
через `AssetDatabase` по их `assetBundleName`. Сцены из бандлов (`tt_foyer`, `Foyer`, `Foyer_Coop`, `tt6`) на время
Play Mode добавляются в Build Settings и убираются после выхода.

- Флаг **EtG Web → Simulate Bundles In Play Mode** (EditorPrefs, по умолчанию включён) действует и на обычный Play.
- Патчер перенаправляет все инстансные вызовы `AssetBundle` в игре (`LoadAsset*`, `GetAllScenePaths`) в `BundleShim`;
  для настоящего бандла вызывается оригинальный метод, поэтому WebGL-сборка работает как раньше.
- Бандл-заглушка — объект `AssetBundle` без нативной части, и `== null` для него возвращает `true`. Игра от этого только
  повторно запрашивает бандл у `ResourceManager`, где он уже закэширован.
- Сейвы Play Mode лежат в `etg/editor-saves`, а не в `LocalLow/Dodge Roll/Enter the Gungeon`. У проекта те же
  company/product name, что у игры, так что без перенаправления редактор писал бы в настоящий Steam-сейв.
- Play From Boot включает **Edit → Graphics Emulation → WebGL 2.0**. По умолчанию для WebGL-таргета стоит эмуляция
  WebGL 1.0, а плеер работает только на WebGL 2. Но рендер всё равно идёт через D3D11, так что графика
  (шейдеры, render texture) может отличаться от браузера.

## Ограничения

- **Нет звука**: Wwise нативный, банки под Windows. План — шим `AkSoundEngine.PostEvent` → `AudioSource`.
- Только WebGL 2 (шейдеры после декомпиляции не собираются под WebGL 1).
- Генерация этажей однопоточная — дольше, чем на ПК.
- Проверка владения игрой (вход через Steam) ещё не сделана.
