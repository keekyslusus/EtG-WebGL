using System.Collections.Generic;
using Alexandria.ItemAPI;
using HarmonyLib;
using UnityEngine;

namespace Sukeban
{
    /// <summary>
    /// Passive item: one armor when first picked up, and armor for playing well.
    /// <list type="bullet">
    /// <item>Temporary armor (cap <see cref="TempCap"/>) for flashy plays: reflecting <see cref="ReflectsForTemp"/>+
    /// bullets with one swing, killing with a home run, killing with a bullet sent on by a shot ball. It is plain
    /// armor the guard counts as temporary: lost first, gone when the room is cleared, on a new floor and on a
    /// strikeout. The HUD tints those armor icons (<see cref="HudPatch"/>).</item>
    /// <item>Permanent armor for hard ones, restoring armor up to <see cref="PermanentCap"/> (found armor counts
    /// on top): a triple play (a home run that touches three enemies) and a grand slam (three home runs in a row,
    /// juggling, no other hit or strike between).</item>
    /// <item>Whatever goes over a cap is a ball back in Fungo's bag.</item>
    /// </list>
    /// </summary>
    public class ArmoredElbowGuard : PassiveItem
    {
        public const int TempCap = 2;
        public const int PermanentCap = 2;
        public const int ReflectsForTemp = 3;

        // the tint of temporary armor icons in the HUD
        public static readonly Color TempTint = new Color(0.45f, 0.85f, 1f);

        public static int Id;

        /// <summary>The last reward and why: for the smoke test.</summary>
        public static string LastReward = "";

        private int m_temp;
        private float m_lastArmor;
        private bool m_lastCrest;

        /// <summary>How much of the owner's armor is temporary.</summary>
        public int TempArmor => m_temp;

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
                "Reflecting three bullets with one swing or killing with a home run or a shot ball gives temporary " +
                "armor (up to two, gone when the room is cleared or on a strikeout). A triple play or a grand slam " +
                "restores armor up to two. Anything over the limit is a ball back in the bag.",
                "Бронированный налокотник", "Броня бэттера",
                "Налокотник со стальными пластинами. Пережил больше драк, чем матчей.\n\nДаёт броню. " +
                "Три пули, отбитые одним взмахом, или убийство хоумраном или выстрелом через мяч дают временную " +
                "броню (до двух, сгорает после зачистки комнаты и при страйкауте). Трипл-плей или гранд-слэм " +
                "восстанавливают броню до двух. Всё сверх предела - мяч обратно в мешок.");
            item.quality = ItemQuality.SPECIAL;
            item.CanBeDropped = false;
            item.PreventStartingOwnerFromDropping = true;
            Id = item.PickupObjectId;
        }

        /// <summary>The guard <paramref name="player"/> wears, or null.</summary>
        public static ArmoredElbowGuard For(PlayerController player)
        {
            if (!player)
            {
                return null;
            }
            foreach (PassiveItem passive in player.passiveItems)
            {
                if (passive is ArmoredElbowGuard guard)
                {
                    return guard;
                }
            }
            return null;
        }

        public override void Pickup(PlayerController player)
        {
            if (!m_pickedUpThisRun)
            {
                player.healthHaver.Armor += 1f;
            }
            base.Pickup(player);
            player.OnRoomClearEvent += OnRoomClear;
            player.OnNewFloorLoaded += OnNewFloor;
            Remember();
        }

        public override DebrisObject Drop(PlayerController player)
        {
            Unhook(player);
            BurnTemp(false);
            return base.Drop(player);
        }

        protected override void OnDestroy()
        {
            if (m_owner)
            {
                Unhook(m_owner);
            }
            base.OnDestroy();
        }

        private void Unhook(PlayerController player)
        {
            player.OnRoomClearEvent -= OnRoomClear;
            player.OnNewFloorLoaded -= OnNewFloor;
        }

        private void OnRoomClear(PlayerController player)
        {
            BurnTemp(false);
        }

        private void OnNewFloor(PlayerController player)
        {
            BurnTemp(false);
        }

        // Armor the owner had last frame: a drop (a hit) takes temporary armor first. A hit that breaks the Old
        // Crest takes the crest's armor, not ours.
        private void Remember()
        {
            if (m_owner)
            {
                m_lastArmor = m_owner.healthHaver.Armor;
                m_lastCrest = m_owner.healthHaver.HasCrest;
            }
        }

        protected override void Update()
        {
            base.Update();
            if (!m_owner)
            {
                return;
            }
            HealthHaver hh = m_owner.healthHaver;
            if (hh.Armor < m_lastArmor && m_temp > 0)
            {
                int lost = Mathf.RoundToInt(m_lastArmor - hh.Armor);
                if (m_lastCrest && !hh.HasCrest)
                {
                    lost--;
                }
                SetTemp(m_temp - Mathf.Max(0, lost));
            }
            int room = Mathf.RoundToInt(hh.Armor) - (hh.HasCrest ? 1 : 0);
            if (m_temp > room)
            {
                SetTemp(Mathf.Max(0, room));
            }
            Remember();
        }

        private void SetTemp(int temp)
        {
            if (temp == m_temp)
            {
                return;
            }
            m_temp = temp;
            RefreshHud();
        }

        private void RefreshHud()
        {
            if (m_owner && GameUIRoot.HasInstance)
            {
                GameUIRoot.Instance.UpdatePlayerHealthUI(m_owner, m_owner.healthHaver);
            }
        }

        /// <summary>A flashy play: temporary armor, or a ball if it is at its cap.</summary>
        public static void AwardTemp(PlayerController player, string why)
        {
            ArmoredElbowGuard guard = For(player);
            if (guard == null)
            {
                return;
            }
            if (guard.m_temp >= TempCap)
            {
                Overflow(player, why + " (temporary armor full)");
                return;
            }
            guard.m_temp++;
            player.healthHaver.Armor += 1f;
            guard.Remember();
            guard.RefreshHud();
            LastReward = "temporary armor: " + why;
            Debug.Log("[ElbowGuard] " + LastReward);
            AkSoundEngine.PostEvent("Play_OBJ_fortune_shield_01", player.gameObject);
        }

        /// <summary>A hard play: armor up to <see cref="PermanentCap"/> (not counting temporary armor), or a ball.</summary>
        public static void AwardPermanent(PlayerController player, string why, string sayEn, string sayRu)
        {
            ArmoredElbowGuard guard = For(player);
            if (guard == null)
            {
                return;
            }
            FungoBall.Say(player, sayEn, sayRu);
            HealthHaver hh = player.healthHaver;
            int permanent = Mathf.RoundToInt(hh.Armor) - guard.m_temp - (hh.HasCrest ? 1 : 0);
            if (permanent >= PermanentCap)
            {
                Overflow(player, why + " (armor full)");
                return;
            }
            hh.Armor += 1f;
            guard.Remember();
            guard.RefreshHud();
            LastReward = "armor: " + why;
            Debug.Log("[ElbowGuard] " + LastReward);
            AkSoundEngine.PostEvent("Play_ITM_Crisis_Stone_Shield_01", player.gameObject);
        }

        private static void Overflow(PlayerController player, string why)
        {
            foreach (PlayerItem active in player.activeItems)
            {
                if (active is Fungo fungo)
                {
                    fungo.AddBall();
                }
            }
            LastReward = "ball: " + why;
            Debug.Log("[ElbowGuard] " + LastReward);
        }

        /// <summary>Takes away the temporary armor left (room cleared, new floor, strikeout).</summary>
        public static void BurnTemp(PlayerController player)
        {
            ArmoredElbowGuard guard = For(player);
            if (guard != null)
            {
                guard.BurnTemp(true);
            }
        }

        private void BurnTemp(bool loud)
        {
            if (m_temp <= 0 || !m_owner)
            {
                m_temp = 0;
                return;
            }
            HealthHaver hh = m_owner.healthHaver;
            int burn = m_temp;
            m_temp = 0;
            hh.Armor = Mathf.Max(0f, hh.Armor - burn);
            Remember();
            RefreshHud();
            Debug.Log("[ElbowGuard] temporary armor gone: " + burn);
            if (loud)
            {
                AkSoundEngine.PostEvent("Play_OBJ_purchase_unable_01", m_owner.gameObject);
            }
        }

        // Reflecting enough bullets with one swing (the bat's charged swing, or anything else that reflects through
        // PassiveReflectItem.ReflectBulletsInRange).
        [HarmonyPatch(typeof(PassiveReflectItem), nameof(PassiveReflectItem.ReflectBulletsInRange))]
        private static class ReflectPatch
        {
            [HarmonyPostfix]
            private static void Postfix(int __result, GameActor newOwner)
            {
                if (__result >= ReflectsForTemp && newOwner is PlayerController player)
                {
                    AwardTemp(player, __result + " bullets reflected");
                }
            }
        }

        // The HUD: the last armor icons (before the Old Crest's, which the game keeps last) as many as there is
        // temporary armor are tinted. Only the color: the game itself puts sprite names back (written for the crest).
        [HarmonyPatch(typeof(GameUIHeartController), nameof(GameUIHeartController.UpdateHealth))]
        private static class HudPatch
        {
            [HarmonyPostfix]
            private static void Postfix(GameUIHeartController __instance, HealthHaver hh)
            {
                List<dfSprite> armors = __instance.extantArmors;
                if (armors == null || hh == null)
                {
                    return;
                }
                ArmoredElbowGuard guard = For(hh.gameActor as PlayerController);
                int temp = guard == null ? 0 : guard.m_temp;
                int end = armors.Count - (hh.HasCrest ? 1 : 0);
                Color normal = __instance.armorSpritePrefab ? __instance.armorSpritePrefab.Color : Color.white;
                for (int i = 0; i < end; i++)
                {
                    dfSprite icon = armors[i];
                    if (icon)
                    {
                        icon.Color = i >= end - temp ? TempTint : normal;
                    }
                }
            }
        }
    }
}
