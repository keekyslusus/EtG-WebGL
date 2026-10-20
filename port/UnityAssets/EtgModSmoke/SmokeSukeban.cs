using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Dungeonator;
using UnityEngine;

// Phase "characters", the Sukeban's own mechanics (port/mods/Sukeban): Fungo's toss and the bat swing that hits the
// ball, at several timings from the sweet spot; three balls juggled by taps; three fouls in a row (a strikeout);
// a ball shot with a bullet, then a chain of two, at an enemy put next to her; balls let drop and picked up again,
// the empty bag; the Geta's roll (reload finished, instant charge). Along the way the Elbow Guard's armor rewards
// (grand slam, kills, three reflected bullets, triple play) and its temporary armor burning (strikeout, room clear). Through the player's own input like the charged attack; the mod only through
// reflection.
namespace EtgModSmoke
{
    public partial class SmokeTest
    {
        private const string SukebanAssembly = "Sukeban";

        private IEnumerator SukebanTest(StepResult s)
        {
            PlayerController p = Player;
            Type ballType = FindType(SukebanAssembly, "Sukeban.FungoBall");
            PlayerItem fungo = null;
            if (p != null)
            {
                foreach (PlayerItem i in p.activeItems)
                {
                    if (i != null && i.GetType().FullName == "Sukeban.Fungo")
                    {
                        fungo = i;
                    }
                }
            }
            if (fungo != null && ballType != null)
            {
                float sweet = Convert.ToSingle(StaticMember(ballType, "SweetSpot"), CultureInfo.InvariantCulture);
                // the windows: home run 0.07, line drive 0.18, foul 0.35
                float[] offsets = { 0f, 0.12f, -0.12f, 0.26f, 0.5f };
                foreach (float off in offsets)
                {
                    yield return FungoSwing(s, p, fungo, ballType, sweet + off, off);
                }
                yield return FungoJuggle(s, p, fungo, ballType, sweet);
                ArmorNote(s, p, "after the juggle (grand slam)");
                yield return FungoShoot(s, p, fungo, ballType);
                ArmorNote(s, p, "after the shot balls (a kill)");
                yield return GuardReflect(s, p);
                ArmorNote(s, p, "after reflecting three bullets");
                // the HUD with temporary armor (tinted icons): <character>-armor-hud.png
                string hud = s.id + "-armor-hud.png";
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(m_reportDir, hud));
                s.notes.Add("armor HUD screenshot " + hud);
                yield return Wait(0.3f);
                yield return FungoStrikeout(s, p, fungo, ballType, sweet);
                ArmorNote(s, p, "after the strikeout");
                GuardRoomClear(s, p);
                yield return FungoTriplePlay(s, p, fungo, ballType, sweet);
                ArmorNote(s, p, "after the triple play (its room cleared too)");
                yield return FungoDrop(s, p, fungo);
            }
            if (p != null && FindType(SukebanAssembly, "Sukeban.Geta") != null)
            {
                yield return GetaRoll(s, p);
            }
        }

        private static PassiveItem Guard(PlayerController p)
        {
            foreach (PassiveItem i in p.passiveItems)
            {
                if (i != null && i.GetType().FullName == "Sukeban.ArmoredElbowGuard")
                {
                    return i;
                }
            }
            return null;
        }

        // armor, how much of it is the Elbow Guard's temporary armor, its last reward
        private void ArmorNote(StepResult s, PlayerController p, string when)
        {
            PassiveItem guard = Guard(p);
            if (guard == null)
            {
                return;
            }
            s.notes.Add("armor " + when + ": " + Fmt(p.healthHaver.Armor) + " (temporary " + InstanceMember(guard, "TempArmor") +
                        "), last reward: " + StaticMember(guard.GetType(), "LastReward"));
        }

        // Three enemy bullets just past the bat's barrel as a charged swing lets go: reflecting three at once is
        // worth temporary armor.
        private IEnumerator GuardReflect(StepResult s, PlayerController p)
        {
            Act("elbow guard: reflect three bullets with one swing");
            Gun gun = p.CurrentGun;
            if (gun == null)
            {
                yield break;
            }
            float reloadEnd = Now + 3f;
            while (gun != null && gun.IsReloading && Now < reloadEnd)
            {
                yield return null;
            }
            p.forceFireDown = true;
            yield return null;
            p.forceFireDown = false;
            p.forceFire = true;
            // a reload the last quick swings left (it starts with this press, 1 s), then the 1 s charge
            yield return Wait(CharacterChargeSeconds + 1f - 0.25f);
            for (int i = 0; i < 3 && gun != null; i++)
            {
                Projectile b = SpawnEnemyBullet(gun);
                if (b != null)
                {
                    m_spawned.Add(b.gameObject);
                }
            }
            yield return Wait(0.25f);
            p.forceFire = false;
            p.forceFireUp = true;
            yield return null;
            p.forceFireUp = false;
            yield return Wait(0.5f);
        }

        // Three Bullet Kins in a row to her right and a home run at them: the first dies (a home run kill, temporary
        // armor), the ball carries on to the other two (a triple play, armor). With no temporary armor left (a room
        // clear before), one armor is taken first so there's room under the cap of two.
        private IEnumerator FungoTriplePlay(StepResult s, PlayerController p, PlayerItem fungo, Type ballType, float sweet)
        {
            Act("fungo: triple play");
            yield return WarpToOpenCell(p);
            p.healthHaver.Armor = Mathf.Max(0f, p.healthHaver.Armor - 1f);
            AIActor prefab = EnemyDatabase.GetOrLoadByGuid("01972dee89fc4404a5c408d50007dad5");
            List<AIActor> kins = new List<AIActor>();
            for (int i = 0; i < 3 && prefab != null && p.CurrentRoom != null; i++)
            {
                AIActor kin = AIActor.Spawn(prefab, p.CenterPosition + new Vector2(3.5f + 1.5f * i, (i - 1) * 1.2f), p.CurrentRoom, true, AIActor.AwakenAnimationType.Default, true);
                if (kin != null)
                {
                    kins.Add(kin);
                    m_spawned.Add(kin.gameObject);
                }
            }
            yield return Wait(1.5f);
            yield return WaitForBalls(fungo, 1);
            ArmorNote(s, p, "before the triple play (one armor taken)");
            p.forceAimPoint = p.CenterPosition + new Vector2(5f, 0f);
            FieldInfo lastHit = ballType.GetField("LastHit", BindingFlags.Static | BindingFlags.Public);
            lastHit.SetValue(null, Enum.ToObject(lastHit.FieldType, 0));
            UnityEngine.Object ball = TossBall(p, fungo, ballType, new List<UnityEngine.Object>());
            yield return null;
            yield return TapAt(p, ball, sweet);
            yield return Wait(1.5f);
            p.forceAimPoint = null;
            int dead = 0;
            foreach (AIActor kin in kins)
            {
                if (kin == null || kin.healthHaver == null || kin.healthHaver.IsDead)
                {
                    dead++;
                }
            }
            s.notes.Add("triple play: hit " + lastHit.GetValue(null) + ", " + dead + " of " + kins.Count + " Bullet Kins dead");
        }

        // The room cleared (the guard's handler called as the player's OnRoomClearEvent would): temporary armor gone.
        private void GuardRoomClear(StepResult s, PlayerController p)
        {
            PassiveItem guard = Guard(p);
            if (guard == null)
            {
                return;
            }
            MethodInfo clear = guard.GetType().GetMethod("OnRoomClear", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (clear != null)
            {
                clear.Invoke(guard, new object[] { p });
            }
            ArmorNote(s, p, "after a room clear");
        }

        private static int Balls(PlayerItem fungo)
        {
            return fungo.numberOfUses;
        }

        private static void UseActive(PlayerController p, PlayerItem item)
        {
            SelectedItemIndex.SetValue(p, p.activeItems.IndexOf(item));
            s_useItem.Invoke(p, null);
        }

        private IEnumerator FungoSwing(StepResult s, PlayerController p, PlayerItem fungo, Type ballType, float target, float off)
        {
            string label = "fungo swing at " + off.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + " s";
            Act(label);
            FieldInfo lastHit = ballType.GetField("LastHit", BindingFlags.Static | BindingFlags.Public);
            lastHit.SetValue(null, Enum.ToObject(lastHit.FieldType, 0));
            yield return Wait(0.3f);
            // the bat reloads after each swing; fire held meanwhile doesn't start a charge
            float reloadEnd = Now + 3f;
            while (p.CurrentGun != null && p.CurrentGun.IsReloading && Now < reloadEnd)
            {
                yield return null;
            }
            yield return Wait(0.2f);
            yield return WaitForBalls(fungo, 1);

            // wind the bat up first (its swing needs a 1 s charge), toss, release at the target
            int shots = 0;
            Action<Projectile, float> countShots = delegate { shots++; };
            p.PostProcessProjectile += countShots;
            p.forceFireDown = true;
            yield return null;
            p.forceFireDown = false;
            p.forceFire = true;
            yield return Wait(CharacterChargeSeconds);
            UseActive(p, fungo);
            UnityEngine.Object ball = null;
            foreach (UnityEngine.Object b in UnityEngine.Object.FindObjectsOfType(ballType))
            {
                if (ball == null || (float)InstanceMember(b, "m_t") < (float)InstanceMember(ball, "m_t"))
                {
                    ball = b;
                }
            }
            float end = Now + 3f;
            // the swing lands in the player's next Update
            // at the sweet spot: screenshots of the ball's timing cue, warming (0.2 s before) and gold (in the window)
            float[] cueAt = off == 0f ? new float[] { target - 0.2f, target - 0.04f } : new float[0];
            string[] cueName = { "warm", "gold" };
            int cue = 0;
            while (ball && Now < end && (float)InstanceMember(ball, "m_t") + Time.deltaTime < target)
            {
                if (cue < cueAt.Length && (float)InstanceMember(ball, "m_t") >= cueAt[cue])
                {
                    string file = s.id + "-fungo-cue-" + cueName[cue] + ".png";
                    ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(m_reportDir, file));
                    s.notes.Add("fungo cue screenshot " + file);
                    cue++;
                }
                yield return null;
            }
            bool tossed = ball != null;
            p.forceFire = false;
            p.forceFireUp = true;
            yield return null;
            p.forceFireUp = false;
            yield return Frames(3);
            p.PostProcessProjectile -= countShots;

            int hits = 0;
            foreach (Projectile proj in UnityEngine.Object.FindObjectsOfType<Projectile>())
            {
                if (proj && proj.name.StartsWith("Fungo Hit", StringComparison.Ordinal))
                {
                    hits++;
                }
            }
            object hit = lastHit.GetValue(null);
            float timing = Convert.ToSingle(StaticMember(ballType, "LastTiming"), CultureInfo.InvariantCulture);
            s.notes.Add(label + ": " + (tossed ? "ball tossed" : "NO BALL") + ", swing fired " + shots + " projectiles, hit " + hit +
                        (hit.ToString() == "None" ? "" : " at " + timing.ToString("+0.000;-0.000", CultureInfo.InvariantCulture) + " s") +
                        ", " + hits + " hit balls flying, " + Balls(fungo) + " balls in the bag");
            yield return Wait(1.5f);
        }

        // Three balls tossed 0.3 s apart, each hit by a tap (fire down for three frames) at its sweet spot: the quick
        // swing (no charge, no reload while a ball is hittable) must hit all three.
        private IEnumerator FungoJuggle(StepResult s, PlayerController p, PlayerItem fungo, Type ballType, float sweet)
        {
            Act("fungo: juggle three balls");
            yield return Wait(3f);
            FieldInfo lastHit = ballType.GetField("LastHit", BindingFlags.Static | BindingFlags.Public);
            List<UnityEngine.Object> balls = new List<UnityEngine.Object>();
            for (int i = 0; i < 3 && fungo.CanBeUsed(p) && !fungo.IsOnCooldown; i++)
            {
                UseActive(p, fungo);
                yield return null;
                foreach (UnityEngine.Object b in UnityEngine.Object.FindObjectsOfType(ballType))
                {
                    if (!balls.Contains(b))
                    {
                        balls.Add(b);
                    }
                }
                if (i < 2)
                {
                    yield return Wait(0.3f);
                }
            }
            List<string> hits = new List<string>();
            foreach (UnityEngine.Object ball in balls)
            {
                lastHit.SetValue(null, Enum.ToObject(lastHit.FieldType, 0));
                float end = Now + 3f;
                while (ball && Now < end && (float)InstanceMember(ball, "m_t") + 2f * Time.deltaTime < sweet)
                {
                    yield return null;
                }
                if (!ball)
                {
                    hits.Add("gone");
                    continue;
                }
                p.forceFireDown = true;
                yield return null;
                p.forceFireDown = false;
                p.forceFire = true;
                yield return null;
                yield return null;
                p.forceFire = false;
                p.forceFireUp = true;
                yield return null;
                p.forceFireUp = false;
                yield return Frames(2);
                object hit = lastHit.GetValue(null);
                float timing = Convert.ToSingle(StaticMember(ballType, "LastTiming"), CultureInfo.InvariantCulture);
                hits.Add(hit + (hit.ToString() == "None" ? "" : " " + timing.ToString("+0.000;-0.000", CultureInfo.InvariantCulture)));
            }
            s.notes.Add("fungo juggle: " + balls.Count + " balls tossed, taps: " + Join(hits));
            yield return Wait(1.5f);
        }

        private static IEnumerator WaitForBalls(PlayerItem fungo, int n)
        {
            float end = Now + 13f;
            while (Now < end && (Balls(fungo) < n || fungo.IsOnCooldown))
            {
                yield return null;
            }
        }

        // Tap at a ball's time from the toss (the quick swing: fire down for three frames).
        private IEnumerator TapAt(PlayerController p, UnityEngine.Object ball, float t)
        {
            float end = Now + 3f;
            while (ball && Now < end && (float)InstanceMember(ball, "m_t") + 2f * Time.deltaTime < t)
            {
                yield return null;
            }
            p.forceFireDown = true;
            yield return null;
            p.forceFireDown = false;
            p.forceFire = true;
            yield return null;
            yield return null;
            p.forceFire = false;
            p.forceFireUp = true;
            yield return null;
            p.forceFireUp = false;
            yield return Frames(2);
        }

        private UnityEngine.Object TossBall(PlayerController p, PlayerItem fungo, Type ballType, List<UnityEngine.Object> known)
        {
            UseActive(p, fungo);
            UnityEngine.Object newest = null;
            foreach (UnityEngine.Object b in UnityEngine.Object.FindObjectsOfType(ballType))
            {
                if (!known.Contains(b))
                {
                    known.Add(b);
                    newest = b;
                }
            }
            return newest;
        }

        // Three fouls in a row: strikes 1, 2, then a strikeout - the bag shuts for a while and a second ball already in
        // the air can't be hit any more.
        private IEnumerator FungoStrikeout(StepResult s, PlayerController p, PlayerItem fungo, Type ballType, float sweet)
        {
            Act("fungo: three fouls, a strikeout");
            FieldInfo lastHit = ballType.GetField("LastHit", BindingFlags.Static | BindingFlags.Public);
            // strikes older than the strike memory (4 s) are forgotten; a full bag so the fouls come quickly
            yield return Wait(4.5f);
            yield return WaitForBalls(fungo, 3);
            List<UnityEngine.Object> known = new List<UnityEngine.Object>();
            List<string> taps = new List<string>();
            UnityEngine.Object spare = null;
            for (int i = 0; i < 3; i++)
            {
                // the quick swing is off for 0.75 s after a strike
                yield return Wait(0.9f);
                lastHit.SetValue(null, Enum.ToObject(lastHit.FieldType, 0));
                UnityEngine.Object ball = TossBall(p, fungo, ballType, known);
                yield return null;
                if (i == 2 && fungo.CanBeUsed(p))
                {
                    // a second ball, tossed just before the third foul: far from its own window then, so the swing
                    // takes the first one (one ball per swing, the best-timed)
                    float end = Now + 3f;
                    while (ball && Now < end && (float)InstanceMember(ball, "m_t") < sweet + 0.2f)
                    {
                        yield return null;
                    }
                    spare = TossBall(p, fungo, ballType, known);
                }
                yield return TapAt(p, ball, sweet + 0.27f);
                taps.Add(lastHit.GetValue(null) + " (strikes " + InstanceMember(fungo, "m_strikes") + ")");
            }
            string shut = "can be used " + fungo.CanBeUsed(p) + ", cooldown " + fungo.CurrentTimeCooldown.ToString("0.00", CultureInfo.InvariantCulture) + " s";
            lastHit.SetValue(null, Enum.ToObject(lastHit.FieldType, 0));
            string spareHit = "no second ball";
            if (spare)
            {
                yield return TapAt(p, spare, sweet);
                spareHit = "the ball in the air after it: " + lastHit.GetValue(null);
            }
            yield return Wait(3.2f);
            s.notes.Add("fungo strikeout: " + Join(taps) + "; then " + shut + "; " + spareHit + "; 3.2 s later can be used " + fungo.CanBeUsed(p));
        }

        private IEnumerator FungoDrop(StepResult s, PlayerController p, PlayerItem fungo)
        {
            Act("fungo: let balls drop");
            yield return WaitForBalls(fungo, 3);
            int before = Balls(fungo);
            for (int i = 0; i < 3 && fungo.CanBeUsed(p) && !fungo.IsOnCooldown; i++)
            {
                UseActive(p, fungo);
                yield return Wait(0.1f);
            }
            string empty = Balls(fungo) + " balls, on cooldown " + fungo.IsOnCooldown + " (" +
                           fungo.CurrentTimeCooldown.ToString("0.00", CultureInfo.InvariantCulture) + " s), can be used " + fungo.CanBeUsed(p);
            // they land under her (she stands still) and are picked up as they come to rest; regrowing takes longer
            yield return Wait(1.4f);
            s.notes.Add("fungo: " + before + " balls, after three tosses " + empty + "; 1.4 s later " + Balls(fungo) + " balls");
        }

        // Shooting the ball (ULTRAKILL's coin): a Bullet Kin 5 tiles to her right, a ball tossed and a bullet of hers
        // (38 Special's) fired at it on the way up; then two balls and one bullet (a chain). The bullet's damage
        // multiplier, the chain length and the enemy's health are noted.
        private IEnumerator FungoShoot(StepResult s, PlayerController p, PlayerItem fungo, Type ballType)
        {
            Act("fungo: shoot the ball");
            // somewhere open: the start room has the elevator's colliders right above where she stands
            yield return WarpToOpenCell(p);
            AIActor prefab = EnemyDatabase.GetOrLoadByGuid("01972dee89fc4404a5c408d50007dad5");
            AIActor enemy = prefab == null || p.CurrentRoom == null ? null
                : AIActor.Spawn(prefab, p.CenterPosition + new Vector2(5f, 0f), p.CurrentRoom, true, AIActor.AwakenAnimationType.Default, true);
            if (enemy != null)
            {
                m_spawned.Add(enemy.gameObject);
            }
            yield return Wait(1.5f);
            for (int balls = 1; balls <= 2; balls++)
            {
                yield return WaitForBalls(fungo, balls);
                FieldInfo links = ballType.GetField("LastShotLinks", BindingFlags.Static | BindingFlags.Public);
                links.SetValue(null, 0);
                float hpBefore = enemy != null && enemy.healthHaver != null ? enemy.healthHaver.GetCurrentHealth() : -1f;
                List<UnityEngine.Object> known = new List<UnityEngine.Object>();
                UnityEngine.Object first = TossBall(p, fungo, ballType, known);
                yield return null;
                UnityEngine.Object second = null;
                if (balls == 2)
                {
                    yield return Wait(0.15f);
                    second = TossBall(p, fungo, ballType, known);
                    yield return null;
                }
                float end = Now + 2f;
                // still low (about her head): high up, the ball can be drawn over the room's north wall, which a
                // bullet can't get through
                while (first && Now < end && (float)InstanceMember(first, "m_t") < 0.05f)
                {
                    yield return null;
                }
                float damage = -1f;
                Projectile bullet = null;
                if (first)
                {
                    // from just left of the ball, flying right through it (no wall in the way)
                    Vector2 target = (Vector2)InstanceMember(first, "Visual");
                    bullet = SpawnPlayerBullet(p, target - new Vector2(1.2f, 0f), target);
                    damage = bullet == null ? -1f : bullet.baseData.damage;
                }
                // follow the bullet: how close it came to the enemy, how long it lived
                float closest = float.MaxValue;
                float lived = 0f;
                float start = Now;
                while (Now < start + 1.2f)
                {
                    if (bullet && bullet.specRigidbody && enemy != null)
                    {
                        closest = Mathf.Min(closest, Vector2.Distance(bullet.specRigidbody.UnitCenter, enemy.CenterPosition));
                        lived = Now - start;
                    }
                    yield return null;
                }
                s.notes.Add("fungo shot bullet: lived " + Fmt(lived) + " s, closest to the enemy " + Fmt(closest) +
                            " tiles; enemy vulnerable " + (enemy != null && enemy.healthHaver != null && enemy.healthHaver.IsVulnerable) +
                            ", at " + (enemy == null ? "-" : enemy.CenterPosition.ToString()) + ", player at " + p.CenterPosition);
                float hpAfter = enemy != null && enemy.healthHaver != null ? enemy.healthHaver.GetCurrentHealth() : -1f;
                s.notes.Add("fungo shot " + balls + " ball" + (balls == 1 ? "" : "s") + ": bullet damage " + Fmt(damage) +
                            ", chain links " + StaticMember(ballType, "LastShotLinks") + ", multiplier x" +
                            Fmt(Convert.ToSingle(StaticMember(ballType, "LastShotMultiplier"), CultureInfo.InvariantCulture)) +
                            ", balls left in the air " + ((first ? 1 : 0) + (second ? 1 : 0)) +
                            ", enemy health " + Fmt(hpBefore) + " -> " + (enemy == null ? "gone" : Fmt(hpAfter)));
            }
        }

        // Into a free 7 x 7 tiles spot of another non-boss room, its own enemies erased.
        private IEnumerator WarpToOpenCell(PlayerController p)
        {
            foreach (RoomHandler room in new List<RoomHandler>(GameManager.Instance.Dungeon.data.rooms))
            {
                if (room == null || room == p.CurrentRoom || (room.area != null && room.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS))
                {
                    continue;
                }
                IntVector2? cell = room.GetRandomAvailableCell(new IntVector2(7, 7), CellTypes.FLOOR, false, null);
                if (!cell.HasValue)
                {
                    continue;
                }
                Act("warp to " + room.GetRoomName() + " to shoot");
                p.WarpToPoint(cell.Value.ToVector2() + new Vector2(3.5f, 3.5f), false, false);
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
                yield break;
            }
        }

        // A bullet of hers (38 Special's) from a point toward another.
        private static Projectile SpawnPlayerBullet(PlayerController p, Vector2 from, Vector2 target)
        {
            Gun source = PickupObjectDatabase.GetById(56) as Gun;
            Projectile prefab = source == null ? null : source.DefaultModule.projectiles[0];
            if (prefab == null)
            {
                return null;
            }
            GameObject o = SpawnManager.SpawnProjectile(prefab.gameObject, from, Quaternion.Euler(0f, 0f, BraveMathCollege.Atan2Degrees(target - from)), true);
            Projectile proj = o.GetComponent<Projectile>();
            proj.Owner = p;
            proj.Shooter = p.specRigidbody;
            proj.collidesWithPlayer = false;
            proj.collidesWithEnemies = true;
            proj.baseData.speed = 20f;
            proj.UpdateSpeed();
            return proj;
        }

        private IEnumerator GetaRoll(StepResult s, PlayerController p)
        {
            Act("geta: charged swing, dodge roll, swing out of it");
            yield return Wait(0.5f);
            // a charged swing first, so the bat is reloading when the roll starts
            p.forceFireDown = true;
            yield return null;
            p.forceFireDown = false;
            p.forceFire = true;
            yield return Wait(CharacterChargeSeconds);
            p.forceFire = false;
            p.forceFireUp = true;
            yield return null;
            p.forceFireUp = false;
            // the bat starts its reload a moment after the swing
            float reloadWait = Now + 1f;
            while (p.CurrentGun != null && !p.CurrentGun.IsReloading && Now < reloadWait)
            {
                yield return null;
            }
            bool reloadingBefore = p.CurrentGun != null && p.CurrentGun.IsReloading;
            float idle = p.stats.GetStatValue(PlayerStats.StatType.ChargeAmountMultiplier);
            bool rolled = p.ForceStartDodgeRoll(Vector2.right);
            yield return Frames(2);
            bool reloadingInRoll = p.CurrentGun != null && p.CurrentGun.IsReloading;
            float during = 0f;
            float end = Now + 2f;
            while (p.IsDodgeRolling && Now < end)
            {
                during = Mathf.Max(during, p.stats.GetStatValue(PlayerStats.StatType.ChargeAmountMultiplier));
                yield return null;
            }
            int shots = 0;
            Action<Projectile, float> countShots = delegate { shots++; };
            p.PostProcessProjectile += countShots;
            p.forceFireDown = true;
            yield return null;
            p.forceFireDown = false;
            p.forceFire = true;
            yield return Wait(0.15f);
            p.forceFire = false;
            p.forceFireUp = true;
            yield return null;
            p.forceFireUp = false;
            yield return Wait(0.6f);
            p.PostProcessProjectile -= countShots;
            float after = p.stats.GetStatValue(PlayerStats.StatType.ChargeAmountMultiplier);
            s.notes.Add("geta: bat reloading before the roll " + reloadingBefore + ", in the roll " + reloadingInRoll +
                        "; roll started " + rolled + ", charge multiplier " + Fmt(idle) + " -> " + Fmt(during) + " in the roll -> " +
                        Fmt(after) + " after; a 0.15 s charge right out of the roll fired " + shots + " player projectiles");
        }

        private static string Fmt(float f)
        {
            return f.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
