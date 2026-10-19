using System.Reflection;
using Alexandria.CharacterAPI;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace Sukeban
{
    /// <summary>
    /// The Sukeban: a delinquent with a nailed bat, Convict's body until her own sprites exist. Her past (the strikeout
    /// in the bottom of the ninth at Koshien) and her unlock come later.
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency("etgmodding.etg.mtgapi")]
    [BepInDependency("alexandria.etgmod.alexandria")]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "keekyslusus.etg.sukeban";
        public const string NAME = "Sukeban";
        public const string VERSION = "0.1.0";
        public const string ID_POOL = "skb";

        public static Assembly Assembly => typeof(Plugin).Assembly;
        public static CustomCharacterData Character;

        public void Awake()
        {
            new Harmony(GUID).PatchAll();
        }

        public void Start()
        {
            ETGModMainBehaviour.WaitForGameManagerStart(GMStart);
        }

        public void GMStart(GameManager manager)
        {
            // Items first: the character's loadout names them.
            NinthInning.Init();
            KoshienDirt.Init();
            ArmoredElbowGuard.Init();

            Character = Loader.BuildCharacter("Sukeban/Character", GUID,
                foyerPos: new Vector3(30.125f, 29.5f),
                hasAltSkin: false,
                altSwapperPos: Vector3.zero,
                paradoxUsesSprites: false);
            ETGModConsole.Log($"{NAME} v{VERSION} started.");
        }
    }
}
