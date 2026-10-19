using Alexandria.ItemAPI;
using UnityEngine;

namespace Sukeban
{
    /// <summary>
    /// Active item: a bag of stadium dirt (losing teams take some home from Koshien). Pours a home base onto the floor;
    /// while its owner stands on it, charged weapons (the bat) charge twice as fast.
    /// </summary>
    public class KoshienDirt : PlayerItem
    {
        // The zone is the sprite's ellipse (48 x 24 px = 3 x 1.5 tiles), a bit generous: the player's feet are a point.
        public const float HalfWidth = 1.5f;
        public const float HalfHeight = 0.75f;
        public const float Leniency = 1.15f;
        public const float Duration = 15f;
        public const float ChargeMultiplier = 2f;
        public const float DamageCooldown = 450f;

        public static int Id;
        private static GameObject s_basePrefab;

        public static void Init()
        {
            const string name = "Koshien Dirt";
            GameObject obj = new GameObject(name);
            KoshienDirt item = obj.AddComponent<KoshienDirt>();
            ItemBuilder.AddSpriteToObject(name, "Sukeban/Resources/koshien_dirt", obj, Plugin.Assembly);
            item.SetupItem("Home Base", "", Plugin.ID_POOL);
            Text.Set(item,
                name, "Home Base",
                "Stadium dirt scooped up after a lost final, as tradition demands.\n\nScatters a home base on the floor. " +
                "Charged weapons charge twice as fast while standing on it.",
                "Грязь Косиэна", "Домашняя база",
                "Земля со стадиона, собранная после проигранного финала - так велит традиция.\n\nРассыпает на полу " +
                "домашнюю базу. Пока стоишь на ней, заряжаемое оружие заряжается вдвое быстрее.");
            item.SetCooldownType(ItemBuilder.CooldownType.Damage, DamageCooldown);
            item.consumable = false;
            item.quality = ItemQuality.SPECIAL;
            item.PreventStartingOwnerFromDropping = true;
            Id = item.PickupObjectId;

            s_basePrefab = SpriteBuilder.SpriteFromResource("Sukeban/Resources/koshien_base", null, Plugin.Assembly);
            s_basePrefab.name = "Koshien Home Base";
            FakePrefab.MarkAsFakePrefab(s_basePrefab);
            s_basePrefab.SetActive(false);
        }

        protected override void DoEffect(PlayerController user)
        {
            AkSoundEngine.PostEvent("Play_OBJ_item_throw_01", gameObject);
            Vector2 at = user.specRigidbody.UnitBottomCenter;
            GameObject home = Instantiate(s_basePrefab);
            home.SetActive(true);
            home.AddComponent<HomeBase>().Place(user, at);
        }
    }

    /// <summary>The patch of dirt <see cref="KoshienDirt"/> leaves: lies flat on the floor, buffs its owner standing on it.</summary>
    public class HomeBase : MonoBehaviour
    {
        private PlayerController m_owner;
        private Vector2 m_center;
        private float m_remaining;
        private tk2dBaseSprite m_sprite;
        private StatModifier m_modifier;
        private bool m_applied;

        public void Place(PlayerController owner, Vector2 center)
        {
            m_owner = owner;
            m_center = center;
            m_remaining = KoshienDirt.Duration;
            m_modifier = new StatModifier
            {
                statToBoost = PlayerStats.StatType.ChargeAmountMultiplier,
                modifyType = StatModifier.ModifyMethod.MULTIPLICATIVE,
                amount = KoshienDirt.ChargeMultiplier,
            };

            m_sprite = GetComponent<tk2dBaseSprite>();
            m_sprite.PlaceAtPositionByAnchor(center, tk2dBaseSprite.Anchor.MiddleCenter);
            // Flat on the floor under the actors, the way a landed supply crate (EmergencyCrateController) and corpses
            // lie: FG_Critical layer in the Background sorting layer. On BG_Critical the floor itself could cover it.
            gameObject.layer = LayerMask.NameToLayer("FG_Critical");
            m_sprite.renderer.sortingLayerName = "Background";
            m_sprite.IsPerpendicular = false;
            m_sprite.HeightOffGround = -1f;
            m_sprite.UpdateZDepth();
            m_sprite.color = OffBase;
        }

        // Dimmed while nobody stands on it, full color while its owner is "on base".
        private static readonly Color OffBase = new Color(0.7f, 0.7f, 0.7f, 0.85f);

        private bool OwnerOnBase()
        {
            Vector2 d = m_owner.specRigidbody.UnitBottomCenter - m_center;
            float x = d.x / (KoshienDirt.HalfWidth * KoshienDirt.Leniency);
            float y = d.y / (KoshienDirt.HalfHeight * KoshienDirt.Leniency);
            return x * x + y * y <= 1f;
        }

        private void Update()
        {
            m_remaining -= BraveTime.DeltaTime;
            bool onBase = m_remaining > 0f && m_owner && !m_owner.healthHaver.IsDead && OwnerOnBase();
            if (onBase && !m_applied)
            {
                AkSoundEngine.PostEvent("Play_OBJ_power_up_01", gameObject);
            }
            SetApplied(onBase);
            m_sprite.color = onBase ? Color.white : OffBase;
            if (m_remaining < 2f)
            {
                // Blink out over the last two seconds.
                m_sprite.renderer.enabled = m_remaining > 0f && Mathf.FloorToInt(m_remaining * 6f) % 2 == 0;
            }
            if (m_remaining <= 0f)
            {
                Destroy(gameObject);
            }
        }

        private void OnDestroy()
        {
            SetApplied(false);
        }

        private void SetApplied(bool applied)
        {
            if (applied == m_applied || !m_owner)
            {
                return;
            }
            m_applied = applied;
            if (applied)
            {
                m_owner.ownerlessStatModifiers.Add(m_modifier);
            }
            else
            {
                m_owner.ownerlessStatModifiers.Remove(m_modifier);
            }
            m_owner.stats.RecalculateStats(m_owner);
        }
    }
}
