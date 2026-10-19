# Smoke-тест модов в браузерной сборке (`?smoketest`)

Сделано 2026-10-19 на ветке `unity6` (Unity 6.6, dev-сборка `webbuild6/` за 7,5 мин, повторная — ~2 мин). Как
запускать — `port/README.md`, Mods → Smoke test, «In the browser». Код: `port/UnityAssets/EtgModSmoke/SmokeWeb.cs`,
`EtgModSmoke.jslib`, `port/site/etg-site.js` (`Etg.onSmokeReport`, `Etg.onGameAbort`), `port/site/serve.py --reports`.

## Результаты первого прогона

Отчёты: `port/build/modsmoke/web-items-shop.md`, `web-enemies-floors-flows.md` (рядом `.json`); для сравнения Play Mode
6.6 — `report-u6-all.md`.

- items,shop — 4 мин: items 52 шага, 51 ok, 0 failed; shop 12/12. Portable Ship, Iceberg Shavings, Ferryman Oar в плеере
  работают (в Play Mode падают из-за запретов в конструкторах MonoBehaviour). Silver Jacket в плеере регистрирует ещё
  9 предметов, которых нет в Play Mode (Doll Arm, HF Blade Lightning, Hook And Gut, Punishing Cross MG / Rocket, Ripper,
  The Right Angle, Debug Screwdriver, Electrolyte Pack) — все ok.
- enemies,floors,flows — 21 мин, одна попытка: enemies как в Play Mode (19 ok, 4 компаньона пропущены, у боссов —
  «uh... activating a boss health bar» игры), 8 этажей и 35 flows грузятся и проходятся, 0 failed / timeout / hang;
  flows 12 ok (в Play Mode каждый шаг портило предупреждение редактора про Blit). Категория `port` — 0.
- Ошибки только редактора в плеере не появились: конструкторы MonoBehaviour, `Destroying assets`, Blit с одной
  RenderTexture (`Pixelator`, значит, предупреждение только редактора).
- `UnloadAsset can only be used on assets` (игровой `PostDungeonGenerationCleanup`) есть и в плеере → перенесено из
  `playmode` в известный шум (`SmokeLog.Noise`).

## Открыто

1. **`IndexOutOfRangeException` в `tk2dBaseSprite.GetUntrimmedBounds`** (`AIShooter.Initialize` ← `AIActor.Awake` ←
   `FakePrefabHooks.InstantiateOPR`) у «испорченных» врагов glitch-этажей (`Custom_Glitch*`, `Apache_Fucking_Around`):
   в плеере тоже (47 раз, 7 шагов), а в Steam, по прежней проверке, нет. Значит, не особенность редактора, а отличие
   порта: сравнить `spriteId` / коллекцию спрайтов, которые строит `ExpandUtility` (`BuildSpriteCollection`,
   `ApplyCustomTexture`), в плеере и в Steam (r2modman, `expand debug stats`, F2 `load_flow`). Возможный обход в
   порте — обрезать номер спрайта, но сначала найти причину.
2. **Падение рантайма (abort) один раз** на `floor:tt_backrooms` в первом прогоне enemies,floors,flows (после 31
   врага); причина ушла из буфера консоли. Отдельно `tt_backrooms` и повторный полный прогон — без падения. Теперь страница
   при abort сохраняет последние 400 строк консоли в `console.txt` и продолжает тест — если повторится, смотреть его
   (подозрение — память: куча 512 → 4096 МБ, растёт геометрически).
3. `PortableShip.OnDestroy` → `ResetConfig` → `sprite.SetSprite` NRE при уничтожении предмета (баг мода, `sprite` уже
   null), в Play Mode не виден, т. к. Portable Ship там не создаётся. Мелочь.
