using Alexandria.ItemAPI;
using UnityEngine;

namespace Sukeban
{
    /// <summary>
    /// Active item: fungo, a coach's batting practice - toss a ball up for yourself and hit it. Tosses a ball
    /// (<see cref="FungoBall"/>); a bat swing as it comes back down sends it off, the harder the better the timing.
    /// Holds <see cref="MaxBalls"/> balls that come back one every <see cref="RegenSeconds"/>; a home run or a line
    /// drive puts the ball straight back in the bag, a ball that drops unhit can be picked up again. While a ball can
    /// be hit, the bat swings at once (no charge, no reload). A foul is a strike; three strikes in a row (no clean hit
    /// between, none older than <see cref="StrikeMemory"/>) are a strikeout: the bag is shut for a while.
    /// </summary>
    public class Fungo : PlayerItem
    {
        public const int MaxBalls = 3;
        public const float RegenSeconds = 4f;
        // While a ball is in reach and in its swing window the bat swings at once: its 1 s charge takes a frame and
        // its 1 s reload is skipped, so three balls tossed in a row can be hit one after another.
        public const float QuickSwingCharge = 60f;

        // Against tossing and swinging sloppily: a foul is a strike and switches the quick swing off for a moment;
        // three strikes are a strikeout (her past: she struck out in the bottom of the ninth at Koshien) - the bag is
        // shut and the balls in the air drop. The Geta's roll still gets the bat swinging meanwhile.
        public const int StrikesToOut = 3;
        public const float StrikeLockout = 0.75f;
        public const float StrikeMemory = 4f;
        public const float StrikeoutSeconds = 3f;

        // A grand slam (the Elbow Guard's permanent armor): GrandSlamHomeRuns home runs in a row, each within
        // GrandSlamGap s of the one before (juggling), no other hit or strike between.
        public const int GrandSlamHomeRuns = 3;
        public const float GrandSlamGap = 1.2f;

        // the swings that hit balls: hers, and Casey in both forms
        public static bool IsBat(Gun gun)
        {
            return gun && (gun.PickupObjectId == NinthInning.Id || gun.PickupObjectId == NinthInning.CaseyId ||
                           gun.PickupObjectId == NinthInning.NailedCaseyId);
        }

        public static int Id;
        internal static GameObject BallPrefab;
        internal static GameObject ShadowPrefab;
        internal static Projectile HitPrefab;

        private float m_regen;
        private PlayerController m_owner;
        private Gun m_hookedGun;
        private int m_swingFrame = -1;
        private StatModifier m_quickCharge;
        private int m_strikes;
        private float m_strikeMemory;
        private float m_lockout;
        private float m_strikeout;
        private int m_homeRuns;
        private float m_lastHomeRun = -100f;

        public static void Init()
        {
            const string name = "Fungo";
            GameObject obj = new GameObject(name);
            Fungo item = obj.AddComponent<Fungo>();
            ItemBuilder.AddSpriteToObject(name, "Sukeban/Resources/fungo", obj, Plugin.Assembly);
            item.SetupItem("Batting Practice", "", Plugin.ID_POOL);
            Text.Set(item,
                name, "Batting Practice",
                "A bag of scuffed practice balls. She hits them alone after everyone else has gone home.\n\n" +
                "Tosses a ball up. Swing a bat as it comes down: the better the timing, the harder the hit. " +
                "While a ball is within reach the bat swings at once. Clean hits put the ball back in the bag; " +
                "fouls are strikes, and three strikes shut the bag for a while. Shooting a ball in the air sends the " +
                "shot on to the nearest enemy, harder (or to the next ball). Dropped balls can be picked up again.",
                "Фунго", "Тренировка отбивания",
                "Мешок потёртых тренировочных мячей. Она отбивает их одна, когда все уже ушли домой.\n\n" +
                "Подбрасывает мяч. Взмахни битой, когда он падает: чем точнее момент, тем сильнее удар. " +
                "Пока мяч рядом, бита бьёт без замаха. Чистый удар возвращает мяч в мешок; фол - это страйк, " +
                "после трёх страйков мешок ненадолго закрывается. Выстрел в мяч в воздухе отправляет пулю в ближайшего " +
                "врага, сильнее (или в следующий мяч). Упавший мяч можно подобрать.");
            item.SetCooldownType(ItemBuilder.CooldownType.Timed, RegenSeconds);
            // numberOfUses is the balls in hand; PlayerItem shows the count while there is more than one
            item.numberOfUses = MaxBalls;
            item.UsesNumberOfUsesBeforeCooldown = true;
            item.consumable = false;
            item.quality = ItemQuality.SPECIAL;
            item.PreventStartingOwnerFromDropping = true;
            Id = item.PickupObjectId;

            BallPrefab = Sprite("Fungo Ball", "fungo_ball");
            ShadowPrefab = Sprite("Fungo Ball Shadow", "fungo_shadow");

            // the hit ball: a plain bullet (38 Special's) wearing the ball's sprite
            Projectile bullet = ((Gun)PickupObjectDatabase.GetById(56)).DefaultModule.projectiles[0];
            GameObject hit = FakePrefab.Clone(bullet.gameObject);
            hit.name = "Fungo Hit";
            HitPrefab = hit.GetComponent<Projectile>();
            // through the projectile collection, as mods' guns do: Alexandria gives the definition the collider
            // vertices the bullet's PixelCollider is generated from (a plain sprite has none)
            SpriteBuilder.AddSpriteToCollection("Sukeban/Resources/fungo_ball", ETGMod.Databases.Items.ProjectileCollection, Plugin.Assembly);
            HitPrefab.SetProjectileSpriteRight("fungo_ball", 6, 6, false, tk2dBaseSprite.Anchor.MiddleCenter);
            HitPrefab.shouldRotate = false;
            hit.AddComponent<FungoHitBall>();
        }

        private static GameObject Sprite(string name, string resource)
        {
            GameObject o = SpriteBuilder.SpriteFromResource("Sukeban/Resources/" + resource, null, Plugin.Assembly);
            o.name = name;
            FakePrefab.MarkAsFakePrefab(o);
            Object.DontDestroyOnLoad(o);
            o.SetActive(false);
            return o;
        }

        public int Balls => numberOfUses;

        public override bool CanBeUsed(PlayerController user)
        {
            return numberOfUses > 0 && m_strikeout <= 0f && base.CanBeUsed(user);
        }

        protected override void DoEffect(PlayerController user)
        {
            AkSoundEngine.PostEvent("Play_OBJ_item_throw_01", gameObject);
            FungoBall.Toss(user, this);
        }

        // PlayerItem.Use refills numberOfUses when the last ball is thrown and starts the cooldown: the hand is empty
        // until the next ball comes back, which the cooldown bar shows.
        protected override void AfterCooldownApplied(PlayerController user)
        {
            numberOfUses = 0;
            CurrentTimeCooldown = RegenSeconds - m_regen;
            base.AfterCooldownApplied(user);
        }

        /// <summary>A ball back in hand (regrown, or picked up off the floor).</summary>
        public void AddBall()
        {
            if (numberOfUses >= MaxBalls)
            {
                return;
            }
            numberOfUses++;
            CurrentTimeCooldown = 0f;
        }

        public override void Update()
        {
            base.Update();
            PlayerController owner = LastOwner;
            if (owner != m_owner)
            {
                if (m_owner)
                {
                    SetQuickCharge(m_owner, false);
                }
                HookGun(null);
                m_owner = owner;
            }
            if (!m_owner)
            {
                return;
            }
            HookGun(IsBat(m_owner.CurrentGun) ? m_owner.CurrentGun : null);
            float dt = BraveTime.DeltaTime;
            m_lockout = Mathf.Max(0f, m_lockout - dt);
            if (m_strikeMemory > 0f)
            {
                m_strikeMemory -= dt;
                if (m_strikeMemory <= 0f)
                {
                    m_strikes = 0;
                }
            }
            bool quick = m_hookedGun && m_lockout <= 0f && m_strikeout <= 0f && FungoBall.AnyHittable(m_owner);
            SetQuickCharge(m_owner, quick);
            if (quick && m_hookedGun.IsReloading)
            {
                m_hookedGun.ForceImmediateReload();
            }

            if (numberOfUses < MaxBalls)
            {
                m_regen += dt;
                if (m_regen >= RegenSeconds)
                {
                    m_regen -= RegenSeconds;
                    AddBall();
                }
            }
            else
            {
                m_regen = 0f;
            }
            if (m_strikeout > 0f)
            {
                // the cooldown bar shows the bag shut
                m_strikeout -= dt;
                CurrentTimeCooldown = m_strikeout > 0f ? m_strikeout : 0f;
            }
            else if (numberOfUses == 0)
            {
                CurrentTimeCooldown = Mathf.Max(0.01f, RegenSeconds - m_regen);
            }
        }

        private void OnHit(FungoBall.Hit hit)
        {
            switch (hit)
            {
                case FungoBall.Hit.HomeRun:
                case FungoBall.Hit.LineDrive:
                    // a clean hit: the ball comes back, the count is cleared
                    AddBall();
                    m_strikes = 0;
                    m_strikeMemory = 0f;
                    break;
                case FungoBall.Hit.Foul:
                    Strike();
                    break;
            }
            if (hit == FungoBall.Hit.None)
            {
                return;
            }
            if (hit != FungoBall.Hit.HomeRun || Time.time - m_lastHomeRun > GrandSlamGap)
            {
                m_homeRuns = 0;
            }
            if (hit == FungoBall.Hit.HomeRun)
            {
                m_homeRuns++;
                m_lastHomeRun = Time.time;
                if (m_homeRuns >= GrandSlamHomeRuns)
                {
                    m_homeRuns = 0;
                    ArmoredElbowGuard.AwardPermanent(m_owner, "grand slam", "Grand slam!", "Гранд-слэм!");
                }
            }
        }

        private void Strike()
        {
            m_strikes++;
            m_strikeMemory = StrikeMemory;
            m_lockout = StrikeLockout;
            if (m_strikes < StrikesToOut)
            {
                FungoBall.Say(m_owner, "Strike " + m_strikes + "!", "Страйк " + m_strikes + "!");
                return;
            }
            m_strikes = 0;
            m_strikeMemory = 0f;
            m_strikeout = StrikeoutSeconds;
            m_homeRuns = 0;
            FungoBall.StrikeOut(m_owner);
            ArmoredElbowGuard.BurnTemp(m_owner);
            FungoBall.Say(m_owner, "Strikeout!", "Страйкаут!");
            AkSoundEngine.PostEvent("Play_OBJ_purchase_unable_01", gameObject);
        }

        private void SetQuickCharge(PlayerController player, bool on)
        {
            if (m_quickCharge == null)
            {
                m_quickCharge = new StatModifier
                {
                    statToBoost = PlayerStats.StatType.ChargeAmountMultiplier,
                    modifyType = StatModifier.ModifyMethod.MULTIPLICATIVE,
                    amount = QuickSwingCharge,
                };
            }
            bool has = player.ownerlessStatModifiers.Contains(m_quickCharge);
            if (on == has)
            {
                return;
            }
            if (on)
            {
                player.ownerlessStatModifiers.Add(m_quickCharge);
            }
            else
            {
                player.ownerlessStatModifiers.Remove(m_quickCharge);
            }
            player.stats.RecalculateStats(player);
        }

        // A bat's own shots (a swing fires the bat's hit and its nail volley in one frame) are the swing.
        private void HookGun(Gun gun)
        {
            if (gun == m_hookedGun)
            {
                return;
            }
            if (m_hookedGun)
            {
                m_hookedGun.PostProcessProjectile -= OnBatShot;
            }
            m_hookedGun = gun;
            if (gun)
            {
                gun.PostProcessProjectile += OnBatShot;
            }
        }

        private void OnBatShot(Projectile shot)
        {
            if (shot && !shot.GetComponent<FungoBatShot>())
            {
                shot.gameObject.AddComponent<FungoBatShot>();
            }
            if (Time.frameCount == m_swingFrame || !m_owner || !m_hookedGun)
            {
                return;
            }
            m_swingFrame = Time.frameCount;
            OnHit(FungoBall.Swing(m_owner, m_hookedGun.CurrentAngle));
        }

        protected override void OnPreDrop(PlayerController user)
        {
            SetQuickCharge(user, false);
            HookGun(null);
            m_owner = null;
            base.OnPreDrop(user);
        }

        protected override void OnDestroy()
        {
            if (m_owner)
            {
                SetQuickCharge(m_owner, false);
            }
            HookGun(null);
            base.OnDestroy();
        }
    }
}
