using System.Linq;
using Alexandria.ItemAPI;
using Gungeon;
using UnityEngine;

namespace Sukeban
{
    /// <summary>
    /// The starting bat: a kugibatto, the wooden bat with nails of Japanese delinquent manga. A copy of Casey's nailed
    /// form (gun 616, what Casey turns into with the #NAILBAT synergy "Careful Iteration"), so it has the nail volley
    /// from the start; charged swings reflect bullets like Casey's. Its sprites are Casey's recolored
    /// (tools/build_bat.py: bare wood, steel nails, a grip wrapped in white sarashi).
    /// </summary>
    public static class NinthInning
    {
        public const int NailedCaseyId = 616;
        public const int CaseyId = 541;
        public const string IconName = "ninth_inning_idle_001";
        public static int Id;

        public static void Init()
        {
            Gun nailbat = (Gun)PickupObjectDatabase.GetById(NailedCaseyId);
            GameObject obj = FakePrefab.Clone(nailbat.gameObject);
            obj.name = "Ninth Inning";
            Gun gun = obj.GetComponent<Gun>();

            ETGMod.Databases.Items.SetupItem(gun, "Ninth Inning");
            Reskin(gun, nailbat);
            gun.encounterTrackable.journalData.SuppressInAmmonomicon = false;
            gun.gunName = "Ninth Inning";
            Text.Set(gun,
                "Ninth Inning", "Bottom of the Ninth",
                "A battered wooden bat studded with nails, its grip wrapped in sarashi. Its owner swung at the last " +
                "pitch of the Koshien final and missed.\n\nCharged swings knock enemy bullets back.",
                "Девятый иннинг", "Последний шанс",
                "Потрёпанная деревянная бита, утыканная гвоздями, рукоять обмотана сараси. Хозяйка промахнулась ею " +
                "по последней подаче финала Косиэна.\n\nЗаряженный удар отбивает вражеские пули.");

            // Casey's +2 curse doesn't belong on a starting weapon.
            gun.RemovePassiveStatModifier(PlayerStats.StatType.Curse);
            gun.quality = PickupObject.ItemQuality.SPECIAL;
            gun.PreventStartingOwnerFromDropping = true;

            Game.Items.Add(Plugin.ID_POOL + ":ninth_inning", gun);
            ETGMod.Databases.Items.Add(gun);
            Id = gun.PickupObjectId;
        }

        /// <summary>
        /// Gives the gun copies of Casey's sprite collection and animation library whose materials use the
        /// kugibatto atlas (same layout as the weapon atlas, only the nailed bat's frames), so every offset, attach
        /// point and animation stays his. Casey himself keeps the originals.
        /// </summary>
        private static void Reskin(Gun gun, Gun nailbat)
        {
            Texture2D atlas = ResourceExtractor.GetTextureFromResource("Sukeban/Resources/kugibatto_atlas.png", Plugin.Assembly);
            tk2dSpriteCollectionData source = nailbat.sprite.Collection;
            tk2dSpriteCollectionData collection = Object.Instantiate(source);
            Object.DontDestroyOnLoad(collection);
            collection.name = "Ninth Inning Collection";
            Material[] materials = source.materials.Select(m =>
            {
                if (m == null)
                {
                    return null;
                }
                Material copy = new Material(m) { name = m.name, mainTexture = atlas };
                Object.DontDestroyOnLoad(copy);
                return copy;
            }).ToArray();
            collection.materials = materials;
            collection.materialInsts = materials.ToArray();
            foreach (tk2dSpriteDefinition def in collection.spriteDefinitions)
            {
                if (def != null && def.materialId >= 0 && def.materialId < materials.Length && materials[def.materialId] != null)
                {
                    def.material = materials[def.materialId];
                    def.materialInst = materials[def.materialId];
                }
            }

            tk2dSpriteAnimation library = Object.Instantiate(nailbat.spriteAnimator.Library);
            Object.DontDestroyOnLoad(library);
            foreach (tk2dSpriteAnimationClip clip in library.clips)
            {
                foreach (tk2dSpriteAnimationFrame frame in clip.frames)
                {
                    if (frame.spriteCollection == source)
                    {
                        frame.spriteCollection = collection;
                    }
                }
            }

            gun.sprite.Collection = collection;
            gun.sprite.SetSprite(collection, gun.sprite.spriteId);
            gun.spriteAnimator.Library = library;

            // the Ammonomicon draws its own collection: a copy of the idle frame, under her name
            tk2dSpriteDefinition icon = collection.spriteDefinitions[gun.sprite.spriteId].Copy();
            icon.name = IconName;
            SpriteBuilder.AddToAmmonomicon(icon);
            gun.encounterTrackable.journalData.AmmonomiconSprite = IconName;
        }
    }
}
