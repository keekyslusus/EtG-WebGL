using Alexandria.ItemAPI;
using UnityEngine;

namespace Sukeban
{
    /// <summary>
    /// Passive item: geta, wooden sandals. During a dodge roll and for <see cref="WindowAfterRoll"/> s after it,
    /// charged weapons charge almost at once (the bat's 1 s wind-up becomes a few frames) and reloads finish at once:
    /// she swings out of the roll, also while Fungo is struck out. Rolling over a dropped <see cref="FungoBall"/>
    /// picks it up from further away.
    /// </summary>
    public class Geta : PassiveItem
    {
        public const float WindowAfterRoll = 0.4f;
        public const float ChargeMultiplier = 25f;
        public const float RollPickupReach = 2f;

        public static int Id;

        private StatModifier m_charge;
        private float m_window;

        public static void Init()
        {
            const string name = "Geta";
            GameObject obj = new GameObject(name);
            Geta item = obj.AddComponent<Geta>();
            ItemBuilder.AddSpriteToObject(name, "Sukeban/Resources/geta", obj, Plugin.Assembly);
            item.SetupItem("Out of the Roll", "", Plugin.ID_POOL);
            Text.Set(item,
                name, "Out of the Roll",
                "Wooden sandals, loud on every floor of the school. Nobody has ever heard her coming anyway.\n\n" +
                "Charged weapons charge instantly during a dodge roll and right after it, and a roll finishes reloading. " +
                "Rolling picks up dropped balls from further away.",
                "Гэта", "Удар с переката",
                "Деревянные сандалии, гремят на каждом этаже школы. Всё равно никто ни разу не слышал, как она подходит.\n\n" +
                "Во время переката и сразу после него заряжаемое оружие заряжается мгновенно, а перекат завершает " +
                "перезарядку. Перекатом упавшие мячи подбираются издалека.");
            item.quality = ItemQuality.SPECIAL;
            item.CanBeDropped = false;
            item.PreventStartingOwnerFromDropping = true;
            Id = item.PickupObjectId;
        }

        /// <summary>How much further than usual <paramref name="player"/> picks up a lying ball right now.</summary>
        public static float PickupReach(PlayerController player)
        {
            if (!player.IsDodgeRolling)
            {
                return 1f;
            }
            foreach (PassiveItem passive in player.passiveItems)
            {
                if (passive is Geta)
                {
                    return RollPickupReach;
                }
            }
            return 1f;
        }

        public override DebrisObject Drop(PlayerController player)
        {
            SetCharge(player, false);
            return base.Drop(player);
        }

        protected override void OnDestroy()
        {
            if (m_owner)
            {
                SetCharge(m_owner, false);
            }
            base.OnDestroy();
        }

        protected override void Update()
        {
            base.Update();
            if (!m_owner)
            {
                return;
            }
            if (m_owner.IsDodgeRolling)
            {
                m_window = WindowAfterRoll;
            }
            else if (m_window > 0f)
            {
                m_window -= BraveTime.DeltaTime;
            }
            // a reload that starts during the roll (the bat starts its own a moment after the swing) ends at once too
            if (m_window > 0f && m_owner.CurrentGun && m_owner.CurrentGun.IsReloading)
            {
                m_owner.CurrentGun.ForceImmediateReload();
            }
            SetCharge(m_owner, m_window > 0f);
        }

        private void SetCharge(PlayerController player, bool on)
        {
            if (m_charge == null)
            {
                m_charge = new StatModifier
                {
                    statToBoost = PlayerStats.StatType.ChargeAmountMultiplier,
                    modifyType = StatModifier.ModifyMethod.MULTIPLICATIVE,
                    amount = ChargeMultiplier,
                };
            }
            bool has = player.ownerlessStatModifiers.Contains(m_charge);
            if (on == has)
            {
                return;
            }
            if (on)
            {
                player.ownerlessStatModifiers.Add(m_charge);
            }
            else
            {
                player.ownerlessStatModifiers.Remove(m_charge);
            }
            player.stats.RecalculateStats(player);
        }
    }
}
