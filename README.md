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
3. **Unity закрыта** → `bash port/apply.sh`
   Патчит DLL, переименовывает `Assembly-CSharp*` → `EtgGame*` (с сохранением GUID),
   убирает Windows-бандлы и Wwise-банки из StreamingAssets, проверяет ссылки.
4. **Один раз**: `asm2wasm` из Unity 2017 падает на большой сборке от переполнения стека.
   Увеличить ему стек (нужны права администратора, оригинал сохраняется как `.orig`):
   ```
   python port/setstack.py <копия asm2wasm.exe> 1073741824
   ```
   и подложить копию вместо
   `<Unity>/Editor/Data/PlaybackEngines/WebGLSupport/BuildTools/Emscripten_FastComp_Win/binaryen/bin/asm2wasm.exe`.
5. Открыть `ripped/ExportedProject` в Unity 2017.4.27f1 → меню **EtG Web → Do Everything (1-4)**
   (boot-сцена, настройки WebGL 2 / wasm / .NET 2.0, бандлы, dev-сборка в `webbuild/`).
   Дальше при изменениях порта: `apply.sh` → Ctrl+R в Unity → **4. Build WebGL Player**.
6. Запускать через любой локальный HTTP-сервер из `webbuild/` (с `file://` не работает).

Лог сборки Unity: `%LOCALAPPDATA%\Unity\Editor\Editor.log`.

## Ограничения

- **Нет звука**: Wwise нативный, банки под Windows. План — шим `AkSoundEngine.PostEvent` → `AudioSource`.
- Только WebGL 2 (шейдеры после декомпиляции не собираются под WebGL 1).
- Генерация этажей однопоточная — дольше, чем на ПК.
- Проверка владения игрой (вход через Steam) ещё не сделана.
