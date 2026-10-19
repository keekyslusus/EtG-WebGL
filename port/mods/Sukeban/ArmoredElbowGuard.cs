using Alexandria.ItemAPI;
using HarmonyLib;
using UnityEngine;

namespace Sukeban
{
    /// <summary>
    /// Passive item: one armor when first picked up; reflecting enemy bullets (the bat's charged swing, or anything
    /// else that reflects through PassiveReflectItem.ReflectBulletsInRange) gives a short burst of speed.
    /// </summary>
    public class ArmoredElbowGuard : PassiveItem
    {
        public const float SpeedBonus = 1.5f;
        public const float SpeedDuration = 3f;

        public static int Id;

        private StatModifier m_speed;
        private float m_speedRemaining;

        public static void Init()
        {
            const string name = "Armored Elbow Guard";
            GameObject obj = new GameObject(name);
            ArmoredElbowGuard item = obj.AddComponent<ArmoredElbowGuard>();
            ItemBuilder.AddSpriteToObject(name, "Sukeban/Resources/elbow_guard", obj, Plugin.Assembly);
            item.SetupItem("Batter's Armor", "", Plugin.ID_POOL);
            Text.Set(item,
                name, "Batter's Armor",
                "A steel-plated guard for the lead elbow, worn through more fights than games.\n\nGrants armor. " +
                "Reflecting an enemy bullet briefly increases movement speed.",
                "Бронированный налокотник", "Броня бэттера",
                "Налокотник со стальными пластинами. Пережил больше драк, чем матчей.\n\nДаёт броню. " +
                "Отбитая вражеская пуля ненадолго ускоряет бег.");
            item.quality = ItemQuality.SPECIAL;
            item.CanBeDropped = false;
            item.PreventStartingOwnerFromDropping = true;
            Id = item.PickupObjectId;
        }

        public override void Pickup(PlayerController player)
        {
            if (!m_pickedUpThisRun)
            {
                player.healthHaver.Armor += 1f;
            }
            base.Pickup(player);
        }

        public override DebrisObject Drop(PlayerController player)
        {
            SetSpeed(player, false);
            m_speedRemaining = 0f;
            return base.Drop(player);
        }

        protected override void OnDestroy()
        {
            if (m_owner)
            {
                SetSpeed(m_owner, false);
            }
            base.OnDestroy();
        }

        protected override void Update()
        {
            base.Update();
            if (m_speedRemaining > 0f)
            {
                m_speedRemaining -= BraveTime.DeltaTime;
                if (m_speedRemaining <= 0f && m_owner)
                {
                    SetSpeed(m_owner, false);
                }
            }
        }

        private void OnReflected()
        {
            if (!m_owner)
            {
                return;
            }
            m_speedRemaining = SpeedDuration;
            SetSpeed(m_owner, true);
        }

        private void SetSpeed(PlayerController player, bool on)
        {
            if (m_speed == null)
            {
                m_speed = new StatModifier
                {
                    statToBoost = PlayerStats.StatType.MovementSpeed,
                    modifyType = StatModifier.ModifyMethod.ADDITIVE,
                    amount = SpeedBonus,
                };
            }
            bool has = player.ownerlessStatModifiers.Contains(m_speed);
            if (on == has)
            {
                return;
            }
            if (on)
            {
                player.ownerlessStatModifiers.Add(m_speed);
            }
            else
            {
                player.ownerlessStatModifiers.Remove(m_speed);
            }
            player.stats.RecalculateStats(player);
        }

        [HarmonyPatch(typeof(PassiveReflectItem), nameof(PassiveReflectItem.ReflectBulletsInRange))]
        private static class ReflectPatch
        {
            [HarmonyPostfix]
            private static void Postfix(int __result, GameActor newOwner)
            {
                if (__result <= 0 || !(newOwner is PlayerController player))
                {
                    return;
                }
                foreach (PassiveItem passive in player.passiveItems)
                {
                    if (passive is ArmoredElbowGuard guard)
                    {
                        guard.OnReflected();
                    }
                }
            }
        }
    }
}
