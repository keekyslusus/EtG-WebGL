using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Dungeonator;
using UnityEngine;

// Phase "characters": the playable characters mods add (Alexandria's CharacterAPI registers them with Mod the
// Gungeon API's `character` console command, ETGModConsole.Characters). For each one, in the run: switch to it the
// way that command does, list what it starts with, use its active items and swing its gun charged at an enemy
// bullet put in front of it, noting which player stats changed (an item's effect shows there). Last, back in the
// Breach (as "Quit to Breach" goes), each must have its character select flag there. Same C# 4 restrictions as the
// rest of the test.
namespace EtgModSmoke
{
    public partial class SmokeTest
    {
        public const string PhaseCharacters = "characters";

        private const float CharacterChargeSeconds = 1.6f;

        private static readonly FieldInfo SelectedItemIndex =
            typeof(PlayerController).GetField("m_selectedItemIndex", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        // the game's own entries of ETGModConsole.Characters
        private static readonly string[] VanillaCharacters =
        {
            "pilot", "convict", "hunter", "marine", "bullet", "robot", "paradox", "gunslinger", "cultist", "lamey", "ninja", "cosmonaut",
        };

        private static IDictionary ConsoleCharacters()
        {
            return StaticMember(FindType("ModTheGungeonAPI", "ETGModConsole"), "Characters") as IDictionary;
        }

        private void CollectCharacters(List<StepResult> characters)
        {
            IDictionary all = ConsoleCharacters();
            if (all == null)
            {
                return;
            }
            List<string> keys = new List<string>();
            foreach (object k in all.Keys)
            {
                if (Array.IndexOf(VanillaCharacters, (string)k) < 0)
                {
                    keys.Add((string)k);
                }
            }
            keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                string shortName = (string)all[key];
                if (Wanted(PhaseCharacters, key, shortName))
                {
                    characters.Add(AddStep("character", key, shortName, null, "Player" + shortName));
                }
            }
        }

        private static void SwitchCharacter(string key)
        {
            MethodInfo m = FindType("ModTheGungeonAPI", "ETGModConsole").GetMethod("SwitchCharacter", BindingFlags.Public | BindingFlags.Static);
            m.Invoke(null, new object[] { new string[] { key } });
        }

        private IEnumerator SwitchAndSettle(StepResult s, string key)
        {
            PlayerController old = Player;
            Act("switch to " + key);
            SwitchCharacter(key);
            float end = Now + 10f;
            while ((Player == null || Player == old) && Now < end)
            {
                yield return null;
            }
            yield return Wait(1.5f);
            PlayerController p = Player;
            if (p == null || p == old)
            {
                s.notes.Add("no new player after switching to " + key);
                yield break;
            }
            if (p.IsInputOverridden)
            {
                p.ClearAllInputOverrides();
            }
            SetGodmode();
            TakeBaseline(p);
        }

        private void TakeBaseline(PlayerController p)
        {
            m_baselineGuns.Clear();
            m_baselinePassives.Clear();
            foreach (Gun g in p.inventory.AllGuns)
            {
                m_baselineGuns.Add(g.PickupObjectId);
            }
            foreach (PassiveItem i in p.passiveItems)
            {
                m_baselinePassives.Add(i.PickupObjectId);
            }
        }

        private IEnumerator CharacterTest(StepResult s)
        {
            yield return SwitchAndSettle(s, s.id);
            PlayerController p = Player;
            if (p == null || p.name.IndexOf(s.detail, StringComparison.OrdinalIgnoreCase) < 0)
            {
                s.notes.Add("the player after the switch is " + (p == null ? "none" : p.name));
                s.status = "failed";
                yield break;
            }
            s.notes.Add("identity " + p.characterIdentity + ", " + p.healthHaver.GetCurrentHealth().ToString("0.#", CultureInfo.InvariantCulture) +
                        " health, " + p.healthHaver.Armor.ToString("0.#", CultureInfo.InvariantCulture) + " armor");
            List<string> guns = new List<string>();
            foreach (Gun g in p.inventory.AllGuns)
            {
                guns.Add(g.EncounterNameOrDisplayName + " #" + g.PickupObjectId + (g.InfiniteAmmo ? " (infinite)" : ""));
            }
            List<string> actives = new List<string>();
            foreach (PlayerItem i in p.activeItems)
            {
                actives.Add(i.EncounterNameOrDisplayName + " #" + i.PickupObjectId);
            }
            List<string> passives = new List<string>();
            foreach (PassiveItem i in p.passiveItems)
            {
                passives.Add(i.EncounterNameOrDisplayName + " #" + i.PickupObjectId);
            }
            s.notes.Add("guns: " + Join(guns) + "; actives: " + Join(actives) + "; passives: " + Join(passives));
            if (guns.Count == 0)
            {
                s.notes.Add("starts without a gun");
            }

            Dictionary<PlayerStats.StatType, float> before = Stats(p);
            if (s_useItem == null)
            {
                s_useItem = typeof(PlayerController).GetMethod("UseItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }
            foreach (PlayerItem item in p.activeItems.ToArray())
            {
                if (item == null)
                {
                    continue;
                }
                // UseItem uses the selected one
                SelectedItemIndex.SetValue(p, p.activeItems.IndexOf(item));
                if (!item.CanBeUsed(p))
                {
                    s.notes.Add(item.EncounterNameOrDisplayName + ": CanBeUsed = false");
                    continue;
                }
                Act("use " + item.EncounterNameOrDisplayName);
                s_useItem.Invoke(p, null);
                yield return Wait(0.7f);
                s.notes.Add("after using " + item.EncounterNameOrDisplayName + ": " + StatChanges(before, Stats(p)));
            }

            Gun gun = p.CurrentGun;
            if (gun != null)
            {
                Dictionary<PlayerStats.StatType, float> beforeSwing = Stats(p);
                int shots = 0;
                Action<Projectile, float> countShots = delegate { shots++; };
                p.PostProcessProjectile += countShots;
                Act("charged attack with " + gun.EncounterNameOrDisplayName);
                // through the player's own fire handling (PlayerController.HandleGunAttack): a gun attacked directly is
                // ceased by it in the next frame, the fire button being up
                p.forceFireDown = true;
                yield return null;
                p.forceFireDown = false;
                p.forceFire = true;
                bool firing = gun != null && gun.IsFiring;
                float release = Now + CharacterChargeSeconds;
                Projectile bullet = null;
                while (Now < release && gun != null)
                {
                    yield return null;
                    if (bullet == null && gun != null && Now > release - 0.25f)
                    {
                        bullet = SpawnEnemyBullet(gun);
                    }
                }
                string bulletState = bullet == null ? "no enemy bullet spawned"
                    : !bullet ? "the enemy bullet was gone at the release"
                    : "the enemy bullet was " + Vector2.Distance(bullet.sprite.WorldCenter, gun.barrelOffset.position).ToString("0.##", CultureInfo.InvariantCulture) +
                      " from the barrel at the release (reflect radius 2.66)";
                p.forceFire = false;
                p.forceFireUp = true;
                yield return null;
                p.forceFireUp = false;
                yield return Wait(0.3f);
                p.PostProcessProjectile -= countShots;
                s.notes.Add("charged attack: firing " + firing + ", " + shots + " player projectiles (reflected ones included); " + bulletState);
                if (bullet)
                {
                    s.notes.Add(bullet.Owner is PlayerController ? "the enemy bullet was reflected" : "the enemy bullet was not reflected");
                    m_spawned.Add(bullet.gameObject);
                }
                s.notes.Add("after the charged attack: " + StatChanges(beforeSwing, Stats(p)));
            }

            yield return ActivesAcrossRooms(s);
            yield return SwitchAndSettle(s, "pilot");
        }

        private const int CharacterRoomVisits = 5;

        // What an active leaves in the world may look different by room and tileset: use the actives in a few rooms of
        // the floor and save a screenshot of each (<report dir>/<character>-room<N>.png) for a look.
        private IEnumerator ActivesAcrossRooms(StepResult s)
        {
            PlayerController p = Player;
            if (p == null || p.activeItems.Count == 0 || GameManager.Instance.Dungeon == null)
            {
                yield break;
            }
            int visited = 0;
            List<string> shots = new List<string>();
            foreach (RoomHandler room in new List<RoomHandler>(GameManager.Instance.Dungeon.data.rooms))
            {
                p = Player;
                if (visited >= CharacterRoomVisits || p == null || GameManager.Instance.IsLoadingLevel)
                {
                    break;
                }
                if (room == null || room == p.CurrentRoom || (room.area != null && room.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS))
                {
                    continue;
                }
                IntVector2? cell = room.GetRandomAvailableCell(new IntVector2(4, 4), CellTypes.FLOOR, false, null);
                if (!cell.HasValue)
                {
                    continue;
                }
                Act("warp to " + room.GetRoomName());
                p.WarpToPoint(cell.Value.ToVector2() + new Vector2(2f, 2f), false, false);
                visited++;
                yield return Wait(0.75f);
                if (p.CurrentRoom != null)
                {
                    List<AIActor> enemies = p.CurrentRoom.GetActiveEnemies(RoomHandler.ActiveEnemyType.All);
                    if (enemies != null)
                    {
                        foreach (AIActor a in enemies.ToArray())
                        {
                            if (a != null && a.healthHaver != null && !a.healthHaver.IsBoss)
                            {
                                a.EraseFromExistence(true);
                            }
                        }
                    }
                }
                foreach (PlayerItem item in p.activeItems.ToArray())
                {
                    if (item == null)
                    {
                        continue;
                    }
                    item.ClearCooldowns();
                    SelectedItemIndex.SetValue(p, p.activeItems.IndexOf(item));
                    if (item.CanBeUsed(p))
                    {
                        s_useItem.Invoke(p, null);
                    }
                }
                yield return Wait(0.6f);
                string file = s.id + "-room" + visited + ".png";
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(m_reportDir, file));
                shots.Add(file + " (" + room.GetRoomName() + ")");
                yield return Wait(0.4f);
            }
            s.notes.Add("actives used in " + visited + " rooms, screenshots: " + Join(shots));
        }

        // A plain bullet (38 Special's) aimed at the player, just past the gun's barrel: what a charged bat reflects.
        private static Projectile SpawnEnemyBullet(Gun gun)
        {
            Gun source = PickupObjectDatabase.GetById(56) as Gun;
            Projectile prefab = source == null ? null : source.DefaultModule.projectiles[0];
            PlayerController p = Player;
            if (prefab == null || p == null || gun.barrelOffset == null)
            {
                return null;
            }
            Vector2 barrel = gun.barrelOffset.position;
            Vector2 toPlayer = p.CenterPosition - barrel;
            Vector2 at = barrel - toPlayer.normalized * 0.75f;
            GameObject o = SpawnManager.SpawnProjectile(prefab.gameObject, at, Quaternion.Euler(0f, 0f, BraveMathCollege.Atan2Degrees(toPlayer)), true);
            Projectile proj = o.GetComponent<Projectile>();
            proj.Owner = null;
            proj.Shooter = null;
            proj.collidesWithPlayer = true;
            proj.collidesWithEnemies = false;
            proj.baseData.speed = 2f;
            proj.UpdateSpeed();
            return proj;
        }

        private static Dictionary<PlayerStats.StatType, float> Stats(PlayerController p)
        {
            Dictionary<PlayerStats.StatType, float> d = new Dictionary<PlayerStats.StatType, float>();
            foreach (PlayerStats.StatType t in Enum.GetValues(typeof(PlayerStats.StatType)))
            {
                d[t] = p.stats.GetStatValue(t);
            }
            return d;
        }

        private static string StatChanges(Dictionary<PlayerStats.StatType, float> before, Dictionary<PlayerStats.StatType, float> after)
        {
            List<string> changes = new List<string>();
            foreach (KeyValuePair<PlayerStats.StatType, float> kv in after)
            {
                float old;
                if (before.TryGetValue(kv.Key, out old) && Mathf.Abs(old - kv.Value) > 0.0001f)
                {
                    changes.Add(kv.Key + " " + old.ToString("0.##", CultureInfo.InvariantCulture) + " -> " + kv.Value.ToString("0.##", CultureInfo.InvariantCulture));
                }
            }
            return changes.Count == 0 ? "no stat changes" : Join(changes);
        }

        private static string Join(List<string> parts)
        {
            return parts.Count == 0 ? "none" : string.Join(", ", parts.ToArray());
        }

        // "Quit to Breach", then the select flag Alexandria puts there for every character (NPC_FoyerCharacter_<name>).
        private IEnumerator BreachTest(StepResult s, List<StepResult> characters)
        {
            Act("load the Breach");
            GameManager.Instance.LoadCharacterSelect(true, false);
            yield return Wait(1f);
            float end = Now + 120f;
            while ((!GameManager.HasInstance || GameManager.Instance.IsLoadingLevel || Foyer.Instance == null) && Now < end)
            {
                yield return null;
            }
            yield return Wait(4f);
            if (Foyer.Instance == null)
            {
                s.notes.Add("no Breach after 120 s");
                s.status = "failed";
                yield break;
            }
            Dictionary<string, FoyerCharacterSelectFlag> flags = new Dictionary<string, FoyerCharacterSelectFlag>();
            foreach (FoyerCharacterSelectFlag f in UnityEngine.Object.FindObjectsOfType<FoyerCharacterSelectFlag>())
            {
                flags[f.name] = f;
            }
            foreach (StepResult c in characters)
            {
                FoyerCharacterSelectFlag flag;
                if (!flags.TryGetValue("NPC_FoyerCharacter_" + c.name, out flag))
                {
                    s.notes.Add(c.name + ": no select flag in the Breach");
                    s.status = "failed";
                    continue;
                }
                Renderer r = flag.sprite == null ? null : flag.sprite.renderer;
                s.notes.Add(c.name + ": select flag at " + flag.transform.position.ToString() + ", " +
                            (flag.gameObject.activeInHierarchy && r != null && r.enabled ? "visible" : "not visible") +
                            (flag.CanBeSelected() ? ", selectable" : ", not selectable"));
            }
        }
    }
}
