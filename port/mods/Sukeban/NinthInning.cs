using Alexandria.ItemAPI;
using Gungeon;
using UnityEngine;

namespace Sukeban
{
    /// <summary>
    /// The starting bat: a copy of Casey's nailed form (gun 616, what Casey turns into with the #NAILBAT synergy
    /// "Careful Iteration"), so it has the nail volley from the start. Charged swings reflect bullets like Casey's.
    /// Uses 616's sprites until it gets its own.
    /// </summary>
    public static class NinthInning
    {
        public const int NailedCaseyId = 616;
        public const int CaseyId = 541;
        public static int Id;

        public static void Init()
        {
            Gun nailbat = (Gun)PickupObjectDatabase.GetById(NailedCaseyId);
            GameObject obj = FakePrefab.Clone(nailbat.gameObject);
            obj.name = "Ninth Inning";
            Gun gun = obj.GetComponent<Gun>();

            ETGMod.Databases.Items.SetupItem(gun, "Ninth Inning");
            gun.encounterTrackable.journalData.AmmonomiconSprite = AmmonomiconSprite(nailbat);
            gun.encounterTrackable.journalData.SuppressInAmmonomicon = false;
            gun.gunName = "Ninth Inning";
            Text.Set(gun,
                "Ninth Inning", "Bottom of the Ninth",
                "A battered wooden bat studded with nails. Its owner swung it at the last pitch of the Koshien final and " +
                "missed.\n\nCharged swings knock enemy bullets back.",
                "Девятый иннинг", "Последний шанс",
                "Потрёпанная деревянная бита, утыканная гвоздями. Хозяйка промахнулась ею по последней подаче финала " +
                "Косиэна.\n\nЗаряженный удар отбивает вражеские пули.");

            // Casey's +2 curse doesn't belong on a starting weapon.
            gun.RemovePassiveStatModifier(PlayerStats.StatType.Curse);
            gun.quality = PickupObject.ItemQuality.SPECIAL;
            gun.PreventStartingOwnerFromDropping = true;

            Game.Items.Add(Plugin.ID_POOL + ":ninth_inning", gun);
            ETGMod.Databases.Items.Add(gun);
            Id = gun.PickupObjectId;
        }

        /// <summary>616 is hidden from the Ammonomicon and may have no icon there; Casey's then.</summary>
        private static string AmmonomiconSprite(Gun nailbat)
        {
            string sprite = nailbat.encounterTrackable.journalData.AmmonomiconSprite;
            tk2dSpriteCollectionData icons = AmmonomiconController.ForceInstance.EncounterIconCollection;
            if (!string.IsNullOrEmpty(sprite) && icons.GetSpriteIdByName(sprite, -1) >= 0)
            {
                return sprite;
            }
            return PickupObjectDatabase.GetById(CaseyId).encounterTrackable.journalData.AmmonomiconSprite;
        }
    }
}
