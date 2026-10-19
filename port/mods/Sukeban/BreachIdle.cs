using System.Linq;
using Alexandria.CharacterAPI;
using HarmonyLib;
using UnityEngine;

namespace Sukeban
{
    /// <summary>
    /// Her idles in the Breach. Her select flag is a copy of Convict's (Alexandria copies a character's own idle
    /// phases onto it only for "newspritesetup" sprites, not for a player sheet), so it has his phases: crossing his
    /// arms (redrawn as her by tools/build_sheet.py, kept) and lighting a cigarette, smoking, throwing it away -
    /// replaced here by hers: she looks down and scuffs the floor with a geta. Its frames are drawn by build_sheet.py
    /// (SCUFF_SLOTS) into the atlas slots of his lighter idle.
    /// </summary>
    internal static class BreachIdle
    {
        private static readonly string[] LookDown = { "convict_select_light_up_001", "convict_select_light_up_002" };
        private static readonly string[] Scuff =
        {
            "convict_select_light_up_003", "convict_select_light_up_004", "convict_select_light_up_005", "convict_select_light_up_006",
        };
        // his phase that plays these slots
        private static readonly string[] Replaced = { "select_light", "select_smoke_idle", "select_trash" };

        private static string s_flagName;
        private static CharacterSelectIdlePhase s_scuff;

        public static void Add(CustomCharacterData data)
        {
            PlayerController player = CharacterBuilder.storedCharacters[data.nameInternal.ToLower()].Second.GetComponent<PlayerController>();
            tk2dSpriteCollectionData collection = player.sprite.Collection;
            tk2dSpriteAnimation library = player.spriteAnimator.Library;
            library.clips = library.clips.Concat(new[]
            {
                Clip("sukeban_scuff_in", collection, LookDown, 6f, tk2dSpriteAnimationClip.WrapMode.Once),
                Clip("sukeban_scuff", collection, Scuff, 8f, tk2dSpriteAnimationClip.WrapMode.Loop),
                Clip("sukeban_scuff_out", collection, LookDown.Reverse().ToArray(), 6f, tk2dSpriteAnimationClip.WrapMode.Once),
            }).ToArray();
            s_scuff = new CharacterSelectIdlePhase
            {
                inAnimation = "sukeban_scuff_in",
                holdAnimation = "sukeban_scuff",
                holdMin = 2.5f,
                holdMax = 5f,
                outAnimation = "sukeban_scuff_out",
            };
            s_flagName = "NPC_FoyerCharacter_" + data.nameShort;
        }

        private static tk2dSpriteAnimationClip Clip(string name, tk2dSpriteCollectionData collection, string[] sprites, float fps,
            tk2dSpriteAnimationClip.WrapMode wrapMode)
        {
            return new tk2dSpriteAnimationClip
            {
                name = name,
                fps = fps,
                wrapMode = wrapMode,
                frames = sprites.Select(s => new tk2dSpriteAnimationFrame { spriteCollection = collection, spriteId = collection.GetSpriteIdByName(s) }).ToArray(),
            };
        }

        private static bool IsReplaced(CharacterSelectIdlePhase phase)
        {
            return Replaced.Contains(phase.inAnimation) || Replaced.Contains(phase.holdAnimation) || Replaced.Contains(phase.outAnimation);
        }

        // after Alexandria's postfix, which puts the custom characters' flags into the Breach
        [HarmonyPatch(typeof(Foyer), "SetUpCharacterCallbacks")]
        private static class FlagPatch
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix()
            {
                if (s_scuff == null)
                {
                    return;
                }
                foreach (FoyerCharacterSelectFlag flag in Object.FindObjectsOfType<FoyerCharacterSelectFlag>())
                {
                    CharacterSelectIdleDoer doer = flag.name == s_flagName ? flag.GetComponent<CharacterSelectIdleDoer>() : null;
                    if (doer != null && !doer.phases.Contains(s_scuff))
                    {
                        doer.phases = doer.phases.Where(p => !IsReplaced(p)).Concat(new[] { s_scuff }).ToArray();
                    }
                }
            }
        }
    }
}
