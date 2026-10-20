using System.Collections.Generic;
using Dungeonator;
using UnityEngine;

namespace Sukeban
{
    /// <summary>
    /// A ball tossed by <see cref="Fungo"/>. It flies up from the hand and keeps the thrower's velocity, as a coin
    /// does in ULTRAKILL, so she can keep running under it. A bat swing within <see cref="Reach"/> hits it; how hard
    /// depends on how close the swing is to the moment the ball is back at hand height (<see cref="SweetSpot"/> s after
    /// the toss). Unhit, it bounces once and lies on the floor for a while; walking over it puts it back in the bag.
    /// </summary>
    public class FungoBall : MonoBehaviour
    {
        // Flight in tiles and seconds: tossed from HandHeight, back there after SweetSpot, Apex above it at the top.
        // over her head: lower, the ball hides behind her and the bat right when its cue matters
        public const float HandHeight = 1.5f;
        public const float SweetSpot = 0.8f;
        public const float Apex = 2.5f;
        public const float Gravity = 8f * Apex / (SweetSpot * SweetSpot);
        public const float TossSpeed = Gravity * SweetSpot / 2f;
        public const float BounceKeep = 0.35f;

        // Timing: |swing - sweet spot| within these makes a home run, a line drive or a foul; later or earlier, a miss.
        public const float PerfectWindow = 0.07f;
        public const float GoodWindow = 0.18f;
        public const float FoulWindow = 0.35f;
        // ball's ground point to her feet
        public const float Reach = 2.25f;

        // Home run: stronger than the bat's own charged hit (100); it turns to the enemy nearest the aim within
        // AimAssistCone degrees (she watches the ball, not the crosshair) and homes a little on the way.
        public const float HomeRunDamage = 120f;
        public const float AimAssistCone = 35f;
        public const float AimAssistRange = 20f;
        public const float HomingRadius = 6f;
        public const float HomingTurn = 300f;

        // Timing cue on the ball, not the bat (the bat has its own charged flash): it warms from white to orange over
        // CueLead s before the sweet spot and pops bigger and gold through the home run window.
        public const float CueLead = 0.3f;
        private static readonly Color CueWarm = new Color(1f, 0.55f, 0.25f);
        private static readonly Color CueGold = new Color(1f, 0.85f, 0.15f);
        public const float CuePop = 1.5f;
        private static readonly Color StruckOut = new Color(0.55f, 0.55f, 0.6f);

        public const float LieSeconds = 8f;
        public const float PickupRadius = 0.8f;

        public enum Hit { None, Foul, LineDrive, HomeRun }

        /// <summary>The last hit and its timing (seconds from the sweet spot, + = late): for the smoke test and tuning.</summary>
        public static Hit LastHit;
        public static float LastTiming;
        public static int Tossed;

        private static readonly List<FungoBall> s_live = new List<FungoBall>();

        private PlayerController m_owner;
        private Fungo m_item;
        private Vector2 m_ground;
        private Vector2 m_velocity;
        private float m_height;
        private float m_vz;
        private float m_t;
        private bool m_bounced;
        private bool m_lying;
        // struck out: drops without being hittable
        private bool m_dead;
        private float m_lieRemaining;
        private tk2dBaseSprite m_sprite;
        private tk2dBaseSprite m_shadow;

        public static void Toss(PlayerController owner, Fungo item)
        {
            GameObject o = Instantiate(Fungo.BallPrefab);
            o.SetActive(true);
            FungoBall ball = o.AddComponent<FungoBall>();
            ball.m_owner = owner;
            ball.m_item = item;
            ball.m_ground = owner.specRigidbody.UnitBottomCenter;
            ball.m_velocity = owner.specRigidbody.Velocity;
            ball.m_height = HandHeight;
            ball.m_vz = TossSpeed;
            ball.m_sprite = o.GetComponent<tk2dBaseSprite>();

            GameObject shadow = Instantiate(Fungo.ShadowPrefab);
            shadow.SetActive(true);
            shadow.transform.parent = o.transform;
            ball.m_shadow = shadow.GetComponent<tk2dBaseSprite>();
            // flat on the floor under the actors, like a landed supply crate (EmergencyCrateController)
            shadow.layer = LayerMask.NameToLayer("FG_Critical");
            ball.m_shadow.renderer.sortingLayerName = "Background";
            ball.m_shadow.IsPerpendicular = false;
            ball.m_shadow.HeightOffGround = -1f;

            ball.Place();
            s_live.Add(ball);
            Tossed++;
        }

        /// <summary>
        /// A bat swing by <paramref name="owner"/> aimed at <paramref name="angle"/>: hits her best-timed ball in reach.
        /// Returns the hit, None if no ball was hit.
        /// </summary>
        public static Hit Swing(PlayerController owner, float angle)
        {
            // one ball per swing: the best-timed one in reach (the others wait for their own swing when juggling)
            FungoBall best = null;
            foreach (FungoBall ball in s_live)
            {
                if (!ball || ball.m_owner != owner || ball.m_lying || ball.m_dead)
                {
                    continue;
                }
                float distance = Vector2.Distance(ball.m_ground, owner.specRigidbody.UnitBottomCenter);
                if (distance > Reach)
                {
                    Debug.Log("[Fungo] swing missed: ball " + distance.ToString("0.00") + " tiles away");
                }
                else if (best == null || Mathf.Abs(ball.m_t - SweetSpot) < Mathf.Abs(best.m_t - SweetSpot))
                {
                    best = ball;
                }
            }
            return best != null ? best.TryHit(angle) : Hit.None;
        }

        /// <summary>A strikeout: <paramref name="owner"/>'s balls in the air can't be hit any more and just drop.</summary>
        public static void StrikeOut(PlayerController owner)
        {
            foreach (FungoBall ball in s_live)
            {
                if (ball && ball.m_owner == owner && !ball.m_lying)
                {
                    ball.m_dead = true;
                }
            }
        }

        /// <summary>Whether one of <paramref name="owner"/>'s balls is in reach and in its swing window right now.</summary>
        public static bool AnyHittable(PlayerController owner)
        {
            foreach (FungoBall ball in s_live)
            {
                if (ball && ball.m_owner == owner && !ball.m_lying && !ball.m_dead && Mathf.Abs(ball.m_t - SweetSpot) <= FoulWindow &&
                    Vector2.Distance(ball.m_ground, owner.specRigidbody.UnitBottomCenter) <= Reach)
                {
                    return true;
                }
            }
            return false;
        }

        private Hit TryHit(float angle)
        {
            float timing = m_t - SweetSpot;
            float off = Mathf.Abs(timing);
            Hit hit = off <= PerfectWindow ? Hit.HomeRun : off <= GoodWindow ? Hit.LineDrive : off <= FoulWindow ? Hit.Foul : Hit.None;
            if (hit == Hit.None)
            {
                Debug.Log("[Fungo] swing missed: timing " + timing.ToString("+0.000;-0.000") + " s");
                return hit;
            }
            LastHit = hit;
            LastTiming = timing;
            Debug.Log("[Fungo] " + hit + ", timing " + timing.ToString("+0.000;-0.000") + " s");

            Vector2 from = m_ground + new Vector2(0f, m_height);
            switch (hit)
            {
                case Hit.HomeRun:
                    Launch(from, AimAssist(from, angle), HomeRunDamage, 40f, 2, true);
                    GameManager.Instance.MainCameraController.DoScreenShake(0.6f, 8f, 0.12f, 0.1f, null);
                    Say(m_owner, "Home run!", "Хоумран!");
                    AkSoundEngine.PostEvent("Play_WPN_duelingpistol_impact_01", gameObject);
                    AkSoundEngine.PostEvent("Play_OBJ_power_up_01", gameObject);
                    break;
                case Hit.LineDrive:
                    Launch(from, angle, 30f, 30f, 0);
                    AkSoundEngine.PostEvent("Play_WPN_duelingpistol_impact_01", gameObject);
                    break;
                default:
                    // a foul: off the end of the bat, 30-60 degrees off the aim
                    float side = Random.value < 0.5f ? -1f : 1f;
                    Launch(from, angle + side * Random.Range(30f, 60f), 15f, 22f, 0);
                    AkSoundEngine.PostEvent("Play_PET_junk_punch_01", gameObject);
                    break;
            }
            Destroy(gameObject);
            return hit;
        }

        // The aim, or the direction to the enemy nearest it within AimAssistCone degrees.
        private float AimAssist(Vector2 from, float angle)
        {
            RoomHandler room = m_owner.CurrentRoom;
            List<AIActor> enemies = room == null ? null : room.GetActiveEnemies(RoomHandler.ActiveEnemyType.All);
            if (enemies == null)
            {
                return angle;
            }
            float best = angle;
            float bestOff = AimAssistCone;
            foreach (AIActor a in enemies)
            {
                if (!a || !a.healthHaver || a.healthHaver.IsDead)
                {
                    continue;
                }
                Vector2 to = a.CenterPosition - from;
                if (to.magnitude > AimAssistRange)
                {
                    continue;
                }
                float toAngle = BraveMathCollege.Atan2Degrees(to);
                float off = Mathf.Abs(Mathf.DeltaAngle(angle, toAngle));
                if (off <= bestOff)
                {
                    best = toAngle;
                    bestOff = off;
                }
            }
            return best;
        }

        private void Launch(Vector2 from, float angle, float damage, float speed, int ricochets, bool homing = false)
        {
            GameObject o = Instantiate(Fungo.HitPrefab.gameObject, from, Quaternion.Euler(0f, 0f, angle));
            Projectile p = o.GetComponent<Projectile>();
            p.Owner = m_owner;
            p.Shooter = m_owner.specRigidbody;
            p.collidesWithPlayer = false;
            p.collidesWithEnemies = true;
            p.baseData.damage = damage;
            p.baseData.speed = speed;
            p.baseData.range = 40f;
            if (ricochets > 0)
            {
                PierceProjModifier pierce = o.AddComponent<PierceProjModifier>();
                pierce.penetration = ricochets + 1;
                o.AddComponent<FungoRicochet>().Left = ricochets;
            }
            if (homing)
            {
                HomingModifier home = o.AddComponent<HomingModifier>();
                home.HomingRadius = HomingRadius;
                home.AngularVelocity = HomingTurn;
            }
            // what she carries applies to the ball too (fire, homing, bouncing...)
            m_owner.DoPostProcessProjectile(p);
        }

        /// <summary>A short speech bubble over <paramref name="owner"/> in the game's language (English or Russian).</summary>
        public static void Say(PlayerController owner, string en, string ru)
        {
            string text = GameManager.Options.CurrentLanguage == StringTableManager.GungeonSupportedLanguages.RUSSIAN ? ru : en;
            Transform t = owner.transform;
            TextBoxManager.ClearTextBoxImmediate(t);
            TextBoxManager.ShowTextBox(owner.sprite.WorldTopCenter + new Vector2(0f, 0.5f), t, 0.8f, text);
        }

        private static bool Blocked(Vector2 at)
        {
            DungeonData data = GameManager.Instance.Dungeon.data;
            IntVector2 cell = at.ToIntVector2(VectorConversions.Floor);
            return !data.CheckInBoundsAndValid(cell) || data.isWall(cell.x, cell.y);
        }

        private void Update()
        {
            float dt = BraveTime.DeltaTime;
            if (!m_owner || GameManager.Instance.IsLoadingLevel)
            {
                Destroy(gameObject);
                return;
            }
            if (m_lying)
            {
                Lie(dt);
                return;
            }

            m_t += dt;
            Vector2 next = m_ground + m_velocity * dt;
            if (Blocked(next))
            {
                m_velocity = Vector2.zero;
            }
            else
            {
                m_ground = next;
            }
            m_vz -= Gravity * dt;
            m_height += m_vz * dt;
            if (m_height <= 0f)
            {
                m_height = 0f;
                DungeonData data = GameManager.Instance.Dungeon.data;
                IntVector2 cell = m_ground.ToIntVector2(VectorConversions.Floor);
                if (data.CheckInBoundsAndValid(cell) && data[cell].type == CellType.PIT)
                {
                    Destroy(gameObject);
                    return;
                }
                if (!m_bounced)
                {
                    m_bounced = true;
                    m_vz = -m_vz * BounceKeep;
                    m_velocity *= 0.5f;
                    AkSoundEngine.PostEvent("Play_OBJ_item_throw_01", gameObject);
                }
                else
                {
                    m_lying = true;
                    m_lieRemaining = LieSeconds;
                }
            }
            Place();
        }

        private void Lie(float dt)
        {
            if (Vector2.Distance(m_owner.specRigidbody.UnitBottomCenter, m_ground) <= PickupRadius * Geta.PickupReach(m_owner) &&
                m_item && m_item.Balls < Fungo.MaxBalls)
            {
                m_item.AddBall();
                AkSoundEngine.PostEvent("Play_OBJ_ammo_pickup_01", gameObject);
                Destroy(gameObject);
                return;
            }
            m_lieRemaining -= dt;
            if (m_lieRemaining < 2f)
            {
                // blink out over the last two seconds
                m_sprite.renderer.enabled = m_lieRemaining > 0f && Mathf.FloorToInt(m_lieRemaining * 6f) % 2 == 0;
            }
            if (m_lieRemaining <= 0f)
            {
                Destroy(gameObject);
            }
        }

        private void Place()
        {
            float off = m_t - SweetSpot;
            bool pop = !m_lying && !m_dead && Mathf.Abs(off) <= PerfectWindow;
            if (m_dead)
            {
                m_sprite.color = StruckOut;
            }
            else if (pop)
            {
                m_sprite.color = CueGold;
            }
            else if (!m_lying && off < 0f && off >= -CueLead)
            {
                m_sprite.color = Color.Lerp(Color.white, CueWarm, (off + CueLead) / CueLead);
            }
            else
            {
                m_sprite.color = Color.white;
            }
            float scale = pop ? CuePop : 1f;
            m_sprite.scale = new Vector3(scale, scale, 1f);
            m_sprite.PlaceAtPositionByAnchor(m_ground + new Vector2(0f, m_height), tk2dBaseSprite.Anchor.LowerCenter);
            // drawn in front of her (sorted as if a tile closer), not behind her sprite
            m_sprite.HeightOffGround = m_height + 1f;
            m_sprite.UpdateZDepth();
            m_shadow.PlaceAtPositionByAnchor(m_ground, tk2dBaseSprite.Anchor.MiddleCenter);
            m_shadow.UpdateZDepth();
        }

        private void OnDestroy()
        {
            s_live.Remove(this);
        }
    }

    /// <summary>A home run carries on from each enemy it hits to the nearest one it hasn't hit, <see cref="Left"/> times.</summary>
    public class FungoRicochet : MonoBehaviour
    {
        public int Left;
        public const float SearchRadius = 12f;
        private readonly List<AIActor> m_hit = new List<AIActor>();

        private void Start()
        {
            GetComponent<Projectile>().OnHitEnemy += OnHitEnemy;
        }

        private void OnHitEnemy(Projectile p, SpeculativeRigidbody body, bool fatal)
        {
            if (body && body.aiActor)
            {
                m_hit.Add(body.aiActor);
            }
            if (Left <= 0 || !(p.Owner is PlayerController owner) || owner.CurrentRoom == null)
            {
                return;
            }
            Vector2 here = p.specRigidbody.UnitCenter;
            AIActor best = null;
            float bestDistance = SearchRadius;
            List<AIActor> enemies = owner.CurrentRoom.GetActiveEnemies(RoomHandler.ActiveEnemyType.All);
            if (enemies == null)
            {
                return;
            }
            foreach (AIActor a in enemies)
            {
                if (!a || m_hit.Contains(a) || !a.healthHaver || a.healthHaver.IsDead)
                {
                    continue;
                }
                float d = Vector2.Distance(here, a.CenterPosition);
                if (d < bestDistance)
                {
                    best = a;
                    bestDistance = d;
                }
            }
            if (best)
            {
                Left--;
                p.SendInDirection(best.CenterPosition - here, true);
            }
        }
    }
}
