using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

// Phase "characters", the Sukeban's own mechanics (port/mods/Sukeban): Fungo's toss and the bat swing that hits the
// ball, at several timings from the sweet spot; three balls juggled by taps; three fouls in a row (a strikeout);
// balls let drop and picked up again, the empty bag; the Geta's roll (reload finished, instant charge). Through the player's own input like the charged attack; the mod only through
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
                yield return FungoStrikeout(s, p, fungo, ballType, sweet);
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
            // strikes older than the strike memory (4 s) are forgotten
            yield return Wait(4.5f);
            List<UnityEngine.Object> known = new List<UnityEngine.Object>();
            List<string> taps = new List<string>();
            UnityEngine.Object spare = null;
            for (int i = 0; i < 3; i++)
            {
                yield return WaitForBalls(fungo, i == 2 ? 2 : 1);
                // the quick swing is off for 0.75 s after a strike
                yield return Wait(0.9f);
                lastHit.SetValue(null, Enum.ToObject(lastHit.FieldType, 0));
                UnityEngine.Object ball = TossBall(p, fungo, ballType, known);
                yield return null;
                if (i == 2)
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
