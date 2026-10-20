using Alexandria.ItemAPI;
using UnityEngine;

namespace Sukeban
{
    /// <summary>
    /// Active item: fungo, a coach's batting practice - toss a ball up for yourself and hit it. Tosses a ball
    /// (<see cref="FungoBall"/>); a bat swing as it comes back down sends it off, the harder the better the timing.
    /// Holds <see cref="MaxBalls"/> balls that come back one every <see cref="RegenSeconds"/>; a ball that drops
    /// unhit can be picked up again.
    /// </summary>
    public class Fungo : PlayerItem
    {
        public const int MaxBalls = 3;
        public const float RegenSeconds = 2.5f;

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
                "Holds three balls; dropped balls can be picked up again.",
                "Фунго", "Тренировка отбивания",
                "Мешок потёртых тренировочных мячей. Она отбивает их одна, когда все уже ушли домой.\n\n" +
                "Подбрасывает мяч. Взмахни битой, когда он падает: чем точнее момент, тем сильнее удар. " +
                "Вмещает три мяча; упавший мяч можно подобрать.");
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
            return numberOfUses > 0 && base.CanBeUsed(user);
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
                HookGun(null);
                m_owner = owner;
            }
            if (!m_owner)
            {
                return;
            }
            HookGun(IsBat(m_owner.CurrentGun) ? m_owner.CurrentGun : null);

            if (numberOfUses < MaxBalls)
            {
                m_regen += BraveTime.DeltaTime;
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
            if (numberOfUses == 0)
            {
                CurrentTimeCooldown = Mathf.Max(0.01f, RegenSeconds - m_regen);
            }
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
            if (Time.frameCount == m_swingFrame || !m_owner || !m_hookedGun)
            {
                return;
            }
            m_swingFrame = Time.frameCount;
            FungoBall.Swing(m_owner, m_hookedGun.CurrentAngle);
        }

        protected override void OnPreDrop(PlayerController user)
        {
            HookGun(null);
            m_owner = null;
            base.OnPreDrop(user);
        }

        protected override void OnDestroy()
        {
            HookGun(null);
            base.OnDestroy();
        }
    }
}
