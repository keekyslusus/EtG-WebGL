using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using UnityEngine;

// Phase "characters", the Sukeban's own mechanics (port/mods/Sukeban): Fungo's toss and the bat swing that hits the
// ball, at several timings from the sweet spot; a ball let drop and picked up again; the empty bag; the Geta's
// instant charge out of a dodge roll. Through the player's own input like the charged attack; the mod only through
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
                yield return FungoDrop(s, p, fungo);
            }
            if (p != null && FindType(SukebanAssembly, "Sukeban.Geta") != null)
            {
                yield return GetaRoll(s, p);
            }
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
            if (Balls(fungo) == 0)
            {
                yield return Wait(3f);
            }

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

        private IEnumerator FungoDrop(StepResult s, PlayerController p, PlayerItem fungo)
        {
            Act("fungo: let balls drop");
            yield return Wait(3f);
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

        private IEnumerator GetaRoll(StepResult s, PlayerController p)
        {
            Act("geta: dodge roll, swing out of it");
            yield return Wait(0.5f);
            float idle = p.stats.GetStatValue(PlayerStats.StatType.ChargeAmountMultiplier);
            bool rolled = p.ForceStartDodgeRoll(Vector2.right);
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
            s.notes.Add("geta: roll started " + rolled + ", charge multiplier " + Fmt(idle) + " -> " + Fmt(during) + " in the roll -> " +
                        Fmt(after) + " after; a 0.15 s charge right out of the roll fired " + shots + " player projectiles");
        }

        private static string Fmt(float f)
        {
            return f.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
