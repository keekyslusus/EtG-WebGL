using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Dungeonator;
using UnityEngine;

// Automatic smoke test of everything the baked mods add (port/README.md, Mods -> Smoke test): waits for
// the mods to load, starts a run, then gives / uses / fires / drops every mod item, buys a few from a
// shop, spawns and kills every mod enemy, loads every mod floor and flow. Errors in the log are
// attributed to the step that was running (SmokeLog); the report goes to port/build/modsmoke.
// Started by EtG Web -> Mod Smoke Test (Editor/EtgWebBuild/EtgModSmokeMenu.cs) through a pending file.
// The game and mods are reached through their public API; mods only by reflection, so this compiles
// (and does nothing) without them. Same C# 4 restrictions as the editor scripts.
namespace EtgModSmoke
{
    public partial class SmokeTest : MonoBehaviour
    {
        public const string PhaseItems = "items";
        public const string PhaseShop = "shop";
        public const string PhaseEnemies = "enemies";
        public const string PhaseFloors = "floors";
        public const string PhaseFlows = "flows";
        public static readonly string[] AllPhases = { PhaseItems, PhaseShop, PhaseEnemies, PhaseFloors, PhaseFlows };

        // Set by the editor (exits Play Mode when the run is over). Argument: report path.
        public static Action<string> Finished;

        public static SmokeTest Instance;

        private const int ShopPurchasesPerKind = 4;

        private readonly List<StepResult> m_steps = new List<StepResult>();
        private HashSet<string> m_phases = new HashSet<string>();
        private List<string> m_filter = new List<string>();
        private string m_reportDir;
        private DateTime m_started;
        private float m_startedRealtime;
        private string m_state = "starting";
        private bool m_finished;
        private readonly List<string> m_runNotes = new List<string>();

        private readonly HashSet<int> m_baselineGuns = new HashSet<int>();
        private readonly HashSet<int> m_baselinePassives = new HashSet<int>();
        private readonly List<UnityEngine.Object> m_spawned = new List<UnityEngine.Object>();
        private GameManager.GameMode? m_restoreGameMode; // a step switched the game mode (boss rush flows)
        private static MethodInfo s_useItem;

        // ---- start ---------------------------------------------------------------------------------

        // Editor: EtgModSmokeMenu writes Temp/modsmoke.pending (phases;filter) and enters Play Mode.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoStart()
        {
            if (!Application.isEditor)
            {
                return;
            }
            string pending = PendingFile;
            if (!File.Exists(pending))
            {
                return;
            }
            string text = File.ReadAllText(pending);
            File.Delete(pending);
            string[] parts = text.Split(new[] { '\n' }, 2);
            Begin(parts[0].Trim(), parts.Length > 1 ? parts[1] : "", DefaultReportDir);
        }

        public static string PendingFile
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/modsmoke.pending")); }
        }

        // Assets -> ExportedProject -> ripped -> etg
        public static string DefaultReportDir
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "../../../port/build/modsmoke")); }
        }

        public static SmokeTest Begin(string phases, string filter, string reportDir)
        {
            if (Instance != null)
            {
                return Instance;
            }
            GameObject go = new GameObject("[ModSmoke]");
            DontDestroyOnLoad(go);
            SmokeTest t = go.AddComponent<SmokeTest>();
            foreach (string p in phases.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                t.m_phases.Add(p.Trim().ToLowerInvariant());
            }
            if (t.m_phases.Count == 0 || t.m_phases.Contains("all"))
            {
                t.m_phases = new HashSet<string>(AllPhases);
            }
            foreach (string f in filter.Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (f.Trim().Length > 0 && !f.Trim().StartsWith("#"))
                {
                    t.m_filter.Add(f.Trim().ToLowerInvariant());
                }
            }
            t.m_reportDir = reportDir;
            t.LoadPrevious();
            Instance = t;
            return t;
        }

        // port/modsmoke.sh restarts the editor after a hang or crash with <report dir>/resume present: steps the
        // previous attempt finished are taken over from its report.json, the one it was running is marked "hang".
        // A level load that died or never finished leaves the game in a state where a quick restart has hung on
        // the main thread; under port/modsmoke.sh the attempt ends instead and a fresh editor carries on.
        private bool m_brokenLoad;

        private Dictionary<string, StepResult> m_previous;
        private string m_hungStep;
        private int m_attempt = 1;

        private void LoadPrevious()
        {
            string resume = Path.Combine(m_reportDir, "resume");
            string json = Path.Combine(m_reportDir, "report.json");
            if (!File.Exists(resume) || !File.Exists(json))
            {
                return;
            }
            File.Delete(resume);
            try
            {
                Report prev = JsonUtility.FromJson<Report>(File.ReadAllText(json));
                m_attempt = prev.attempt + 1;
                m_previous = new Dictionary<string, StepResult>();
                foreach (StepResult s in prev.steps)
                {
                    m_previous[s.Key] = s;
                }
                if (prev.state != null && prev.state.StartsWith("running "))
                {
                    m_hungStep = prev.state.Substring("running ".Length);
                }
                m_runNotes.AddRange(prev.runNotes);
                m_runNotes.Add(m_hungStep != null
                    ? "attempt " + prev.attempt + " ended in a hang or crash during " + m_hungStep + "; resumed after it"
                    : "attempt " + prev.attempt + " ended: " + prev.state + " (fresh editor)");
                foreach (ErrorGroup g in prev.errors)
                {
                    SmokeLog.AddPrevious(g);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(SmokeLog.Prefix + "resume: can not read " + json + ": " + e.Message);
                m_previous = null;
            }
        }

        private void Awake()
        {
            m_started = DateTime.Now;
            m_startedRealtime = Time.realtimeSinceStartup;
            Application.runInBackground = true;
            SmokeLog.ScanAssemblies(); // the plugin DLLs are loaded with the domain: startup errors get classified too
            SmokeLog.Install();
            StartCoroutine(Run());
        }

        private void OnDestroy()
        {
            if (!m_finished)
            {
                m_state = "aborted (runner destroyed)";
                WriteReport();
            }
            SmokeLog.Uninstall();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void OnApplicationQuit()
        {
            RestoreBatching();
            if (!m_finished)
            {
                m_state = "aborted (Play Mode stopped)";
                WriteReport();
                m_finished = true;
            }
        }

        private static void Log(string s)
        {
            Debug.Log(SmokeLog.Prefix + s);
        }

        // Breadcrumb in Editor.log before each action: if the editor dies (a Mono crash takes the report with it),
        // the log's last line says what the test was doing.
        private static void Act(string action)
        {
            Debug.Log(SmokeLog.Prefix + "  . " + action + " (frame " + Time.frameCount + ")");
        }

        private static float Now { get { return Time.realtimeSinceStartup; } }

        private static PlayerController Player
        {
            get { return GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null; }
        }

        // ---- main sequence ---------------------------------------------------------------------------

        private IEnumerator Run()
        {
            StepResult startup = AddStep("startup", "boot", "Boot -> title, mods loading", null, null);
            SmokeLog.Current = startup;
            m_state = "waiting for the mods to load";
            yield return StartCoroutine(Drive(startup, WaitForMods(startup), 900f));

            SmokeLog.ScanAssemblies();
            List<StepResult> items = new List<StepResult>();
            List<StepResult> shop = new List<StepResult>();
            List<StepResult> enemies = new List<StepResult>();
            List<StepResult> floors = new List<StepResult>();
            List<StepResult> flows = new List<StepResult>();
            Dictionary<string, PickupObject> itemObjects = new Dictionary<string, PickupObject>();
            Dictionary<string, AIActor> enemyObjects = new Dictionary<string, AIActor>();
            Dictionary<string, string[]> loads = new Dictionary<string, string[]>();
            try
            {
                CollectItems(items, shop, itemObjects);
                CollectEnemies(enemies, enemyObjects);
                CollectFloors(floors, flows, loads);
            }
            catch (Exception e)
            {
                SmokeLog.RecordException(e, "collecting mod content");
            }
            Log("mods: " + string.Join(", ", new List<string>(SmokeLog.ModAssemblies).ToArray()) + "; items " + items.Count + ", shop " + shop.Count +
                ", enemies " + enemies.Count + ", floors " + floors.Count + ", flows " + flows.Count);
            WriteReport();

            StepResult run = AddStep("run", "start", "Quick start (Pilot) -> first floor", null, null);
            m_state = "starting a run";
            yield return StartCoroutine(Drive(run, StartRun(run), 240f));
            if (!InRun())
            {
                Finish("no run could be started");
                yield break;
            }
            if (m_phases.Contains(PhaseFloors) || m_phases.Contains(PhaseFlows))
            {
                StepResult diag = AddStep("diag", "room-tables", "Expand the Gungeon's room tables: rooms the generator can't use", "ExpandTheGungeon", null);
                if (!diag.carried)
                {
                    yield return StartCoroutine(Drive(diag, RoomTableDiag(diag), 30f));
                }
            }

            if (m_phases.Contains(PhaseBatching))
            {
                StepResult batching = AddStep("perf", "batching", "Draw calls with dynamic batching on / off", null, null);
                yield return StartCoroutine(RunStep(batching, BatchingTest(batching), 240f));
            }
            foreach (StepResult s in items)
            {
                yield return StartCoroutine(RunStep(s, ItemTest(s, itemObjects[s.Key]), 30f));
            }
            foreach (StepResult s in shop)
            {
                yield return StartCoroutine(RunStep(s, ShopTest(s, itemObjects[s.Key]), 20f));
            }
            foreach (StepResult s in enemies)
            {
                yield return StartCoroutine(RunStep(s, EnemyTest(s, enemyObjects[s.Key]), 30f));
            }
            foreach (StepResult s in floors)
            {
                yield return StartCoroutine(RunStep(s, FloorTest(s, loads[s.Key]), 180f));
            }
            foreach (StepResult s in flows)
            {
                yield return StartCoroutine(RunStep(s, FloorTest(s, loads[s.Key]), 180f));
            }
            Finish("done");
        }

        private void Finish(string state)
        {
            RestoreBatching();
            SmokeLog.Current = null;
            m_state = state;
            m_finished = true;
            string path = WriteReport();
            Log("finished (" + state + "): " + path);
            if (Finished != null)
            {
                Finished(path);
            }
        }

        private StepResult AddStep(string kind, string id, string name, string owner, string detail)
        {
            StepResult s = new StepResult { kind = kind, id = id, name = name, owner = owner, detail = detail };
            StepResult prev;
            if (m_previous != null && kind != "startup" && kind != "run" && m_previous.TryGetValue(s.Key, out prev))
            {
                if (s.Key == m_hungStep)
                {
                    s.status = "hang";
                    s.notes.AddRange(prev.notes);
                    s.errors.AddRange(prev.errors);
                    s.notes.Add("the editor hung or crashed during this step (or the run restart before it) in attempt " + (m_attempt - 1) + "; not run again");
                    s.carried = true;
                }
                else if (prev.status != "pending")
                {
                    s.status = prev.status;
                    s.seconds = prev.seconds;
                    s.frames = prev.frames;
                    s.errorFrames = prev.errorFrames;
                    s.notes.AddRange(prev.notes);
                    s.errors.AddRange(prev.errors);
                    s.carried = true;
                }
            }
            m_steps.Add(s);
            return s;
        }

        private bool Wanted(string phase, string id, string name)
        {
            if (!m_phases.Contains(phase))
            {
                return false;
            }
            if (m_filter.Count == 0)
            {
                return true;
            }
            foreach (string f in m_filter)
            {
                if ((id != null && id.ToLowerInvariant().Contains(f)) || (name != null && name.ToLowerInvariant().Contains(f)))
                {
                    return true;
                }
            }
            return false;
        }

        // A step: makes sure a run is going, runs the body with its timeout, cleans up.
        private IEnumerator RunStep(StepResult s, IEnumerator body, float timeout)
        {
            if (s.carried)
            {
                yield break;
            }
            m_state = "running " + s.Key;
            WriteReport(); // a hang in the run restart below is then attributed to this step
            if (!InRun() && m_brokenLoad && EtgModSmoke.SmokeTest.Finished != null && Resumable)
            {
                Finish("restart before " + s.Key);
                yield break;
            }
            if (!InRun())
            {
                StepResult restart = AddStep("run", "restart-" + m_steps.Count, "Run restarted before " + s.Key, null, null);
                m_runNotes.Add("run lost before " + s.Key + "; restarted");
                yield return StartCoroutine(Drive(restart, StartRun(restart), 240f));
                if (!InRun())
                {
                    s.status = "skipped";
                    s.notes.Add("no run (restart failed)");
                    yield break;
                }
            }
            yield return StartCoroutine(Drive(s, body, timeout));
            if ((s.kind == "floor" || s.kind == "flow") && (s.status == "failed" || s.status == "timeout"))
            {
                m_brokenLoad = true;
            }
        }

        // Only an unattended run (port/modsmoke.sh) restarts the editor for the rest.
        public static bool Resumable;

        // Runs an enumerator by hand (nested enumerators included) so exceptions anywhere in it are
        // caught and a hanging step can be abandoned.
        private IEnumerator Drive(StepResult s, IEnumerator body, float timeout)
        {
            SmokeLog.Current = s;
            s.firstFrame = Time.frameCount;
            float t0 = Now;
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(body);
            bool failed = false, timedOut = false;
            Log("step " + s.Key + " (" + s.name + ")");
            while (stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                bool more;
                try
                {
                    more = top.MoveNext();
                }
                catch (Exception e)
                {
                    SmokeLog.RecordException(e, "thrown in step");
                    s.notes.Add("step aborted by " + e.GetType().Name);
                    failed = true;
                    break;
                }
                if (!more)
                {
                    stack.Pop();
                    continue;
                }
                IEnumerator nested = top.Current as IEnumerator;
                if (nested != null)
                {
                    stack.Push(nested);
                    continue;
                }
                if (Now - t0 > timeout)
                {
                    timedOut = true;
                    s.notes.Add("timeout after " + timeout + " s");
                    break;
                }
                yield return top.Current;
            }
            Cleanup(s);
            s.seconds = Now - t0;
            s.frames = Time.frameCount - s.firstFrame;
            if (s.status == "pending")
            {
                s.status = timedOut ? "timeout" : failed ? "failed" : s.errors.Count > 0 ? "errors" : "ok";
            }
            else if (s.status == "ok" && s.errors.Count > 0)
            {
                s.status = "errors";
            }
            Log("  -> " + s.status + (s.errors.Count > 0 ? " (" + s.errors.Count + " error kinds)" : ""));
            SmokeLog.Current = null;
            WriteReport();
        }

        private static IEnumerator Wait(float seconds)
        {
            float end = Now + seconds;
            while (Now < end)
            {
                yield return null;
            }
        }

        private static IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                yield return null;
            }
        }

        // ---- startup / run ---------------------------------------------------------------------------

        private IEnumerator WaitForMods(StepResult s)
        {
            float t0 = Now;
            string lastStatus = null;
            while (true)
            {
                string status = ExpandLoadStatus();
                if (status != lastStatus)
                {
                    Log("Expand the Gungeon: " + (status ?? "not installed"));
                    lastStatus = status;
                }
                bool modsDone = status == null || status == "LoadFinished" || status == "LoadError";
                if (modsDone && GameManager.HasInstance && !GameManager.Instance.IsLoadingLevel && Foyer.Instance != null)
                {
                    break;
                }
                yield return null;
            }
            if (lastStatus == "LoadError")
            {
                s.notes.Add("Expand the Gungeon reported LoadError");
            }
            s.notes.Add("mods loaded after " + (Now - t0).ToString("0", CultureInfo.InvariantCulture) + " s");
            yield return Wait(3f);
        }

        private static bool InRun()
        {
            PlayerController p = Player;
            return p != null && !GameManager.Instance.IsLoadingLevel && !Dungeon.IsGenerating && !GameManager.Instance.IsFoyer &&
                   GameManager.Instance.Dungeon != null && p.healthHaver != null && p.healthHaver.IsAlive && p.CurrentRoom != null;
        }

        // What the title screen's quick start does (FinalIntroSequenceManager), always as the Pilot.
        private IEnumerator StartRun(StepResult s)
        {
            if (GameManager.Instance.IsPaused)
            {
                GameManager.Instance.ForceUnpause();
            }
            PlayerController old = Player;
            if (old != null && !GameManager.Instance.IsFoyer)
            {
                s.notes.Add("quick restart");
                GameManager.Instance.QuickRestart(new QuickRestartOptions());
            }
            else
            {
                GameManager.PlayerPrefabForNewGame = (GameObject)BraveResources.Load("PlayerRogue");
                GameManager.Instance.GlobalInjectionData.PreprocessRun();
                yield return null;
                PlayerController prefab = GameManager.PlayerPrefabForNewGame.GetComponent<PlayerController>();
                GameStatsManager.Instance.BeginNewSession(prefab);
                GameObject player = UnityEngine.Object.Instantiate(GameManager.PlayerPrefabForNewGame, Vector3.zero, Quaternion.identity);
                GameManager.PlayerPrefabForNewGame = null;
                player.SetActive(true);
                PlayerController pc = player.GetComponent<PlayerController>();
                pc.PlayerIDX = 0;
                GameManager.Instance.PrimaryPlayer = pc;
                GameManager.Instance.FlushMusicAudio();
                GameManager.Instance.SetNextLevelIndex(1);
                GameManager.Instance.IsSelectingCharacter = false;
                GameManager.Instance.IsFoyer = false;
                Foyer.DoIntroSequence = false;
                Foyer.DoMainMenu = false;
                GameManager.Instance.DelayedLoadNextLevel(0.5f);
            }
            yield return Wait(1f);
            while (!InRun())
            {
                yield return null;
            }
            yield return Wait(2f);
            PlayerController p = Player;
            // A player created in the Breach gets ForceNoGun and the Breach shader (Foyer.ProcessPlayerEnteredFoyer);
            // the Breach undoes that when the player leaves through it (Foyer.OnDepartedFoyer), which a quick start skips.
            if (p.inventory.ForceNoGun)
            {
                p.inventory.ForceNoGun = false;
                if (p.CurrentGun != null)
                {
                    p.CurrentGun.gameObject.SetActive(true);
                }
                p.ProcessHandAttachment();
                p.ClearOverrideShader();
                p.AlternateCostumeLibrary = null;
                s.notes.Add("Breach state cleared (ForceNoGun)");
            }
            p.RemoveAllActiveItems();
            m_baselineGuns.Clear();
            m_baselinePassives.Clear();
            foreach (Gun g in p.inventory.AllGuns)
            {
                m_baselineGuns.Add(g.PickupObjectId);
            }
            foreach (PassiveItem i in p.passiveItems)
            {
                m_baselinePassives.Add(i.PickupObjectId);
            }
            SetGodmode();
            s.notes.Add("floor: " + DungeonName());
        }

        private static void SetGodmode()
        {
            Type t = FindType("ModTheGungeonAPI", "ETGModMainBehaviour");
            FieldInfo f = t == null ? null : t.GetField("Godmode", BindingFlags.Public | BindingFlags.Static);
            if (f != null)
            {
                f.SetValue(null, true);
            }
            PlayerController p = Player;
            if (p != null && p.healthHaver != null)
            {
                p.healthHaver.PreventAllDamage = true;
            }
        }

        private static string DungeonName()
        {
            Dungeon d = GameManager.HasInstance ? GameManager.Instance.Dungeon : null;
            return d == null ? "none" : d.name + " (" + d.DungeonFloorName + ")";
        }

        // Puts the player back to the state the run started with: removes everything a step gave
        // (also what items handed out themselves), heals, unpauses, clears time scale changes.
        private void Cleanup(StepResult s)
        {
            try
            {
                foreach (UnityEngine.Object o in m_spawned)
                {
                    if (o != null)
                    {
                        UnityEngine.Object.Destroy(o is Component ? ((Component)o).gameObject : o);
                    }
                }
                m_spawned.Clear();
                if (!GameManager.HasInstance)
                {
                    return;
                }
                if (m_restoreGameMode.HasValue)
                {
                    GameManager.Instance.CurrentGameMode = m_restoreGameMode.Value;
                    m_restoreGameMode = null;
                }
                if (GameManager.Instance.IsPaused)
                {
                    GameManager.Instance.ForceUnpause();
                    s.notes.Add("game was paused");
                }
                PlayerController p = Player;
                if (p == null || GameManager.Instance.IsLoadingLevel)
                {
                    return;
                }
                foreach (Gun g in p.inventory.AllGuns.ToArray())
                {
                    if (g != null && !m_baselineGuns.Contains(g.PickupObjectId))
                    {
                        if (g.IsFiring)
                        {
                            g.CeaseAttack(false, null);
                        }
                        p.inventory.RemoveGunFromInventory(g);
                    }
                }
                for (int i = p.activeItems.Count - 1; i >= 0; i--)
                {
                    p.RemoveActiveItem(p.activeItems[i].PickupObjectId);
                }
                for (int i = p.passiveItems.Count - 1; i >= 0; i--)
                {
                    if (!m_baselinePassives.Contains(p.passiveItems[i].PickupObjectId))
                    {
                        p.RemovePassiveItem(p.passiveItems[i].PickupObjectId);
                    }
                }
                if (p.CurrentGun == null && p.inventory.AllGuns.Count > 0)
                {
                    p.ChangeToGunSlot(0, true);
                }
                if (p.healthHaver != null && p.healthHaver.IsAlive)
                {
                    p.healthHaver.FullHeal();
                }
                if (p.IsInputOverridden)
                {
                    p.ClearAllInputOverrides();
                    s.notes.Add("player input was overridden");
                }
                BraveTime.ClearAllMultipliers();
                SetGodmode();
            }
            catch (Exception e)
            {
                SmokeLog.RecordException(e, "cleanup");
            }
        }

        // ---- items -----------------------------------------------------------------------------------

        private void CollectItems(List<StepResult> items, List<StepResult> shop, Dictionary<string, PickupObject> objects)
        {
            // Mod the Gungeon API: Game.Items (namespaced IDs, "gungeon:" = the game's own) and
            // ETGMod.Databases.Items.ModItems (everything added through ItemDB, with or without an ID).
            Dictionary<PickupObject, string> ids = new Dictionary<PickupObject, string>();
            object pool = StaticMember(FindType("ModTheGungeonAPI", "Gungeon.Game"), "Items");
            IEnumerable pairs = pool == null ? null : InstanceMember(pool, "Pairs") as IEnumerable;
            if (pairs != null)
            {
                foreach (object o in pairs)
                {
                    KeyValuePair<string, PickupObject> kv = (KeyValuePair<string, PickupObject>)o;
                    if (kv.Value != null && !kv.Key.StartsWith("gungeon:") && !ids.ContainsKey(kv.Value))
                    {
                        ids[kv.Value] = kv.Key;
                    }
                }
            }
            List<PickupObject> all = new List<PickupObject>(ids.Keys);
            object itemDb = StaticMember(FindType("ModTheGungeonAPI", "ETGMod+Databases"), "Items");
            IEnumerable modItems = itemDb == null ? null : InstanceMember(itemDb, "ModItems") as IEnumerable;
            if (modItems != null)
            {
                foreach (object o in modItems)
                {
                    PickupObject po = o as PickupObject;
                    if (po != null && !ids.ContainsKey(po))
                    {
                        ids[po] = "#" + po.PickupObjectId + " " + po.name;
                        all.Add(po);
                    }
                }
            }
            all.Sort(delegate(PickupObject a, PickupObject b)
            {
                int c = string.Compare(ItemOwner(a, ids[a]), ItemOwner(b, ids[b]), StringComparison.Ordinal);
                return c != 0 ? c : string.Compare(ids[a], ids[b], StringComparison.Ordinal);
            });
            Dictionary<string, int> shopKinds = new Dictionary<string, int>();
            foreach (PickupObject po in all)
            {
                string id = ids[po];
                string kind = ItemKind(po);
                string name;
                try
                {
                    name = po.EncounterNameOrDisplayName;
                }
                catch (Exception e)
                {
                    SmokeLog.RecordException(e, "name of " + id);
                    name = po.name;
                }
                string owner = ItemOwner(po, id);
                if (Wanted(PhaseItems, id, name))
                {
                    StepResult s = AddStep("item", id, name, owner, kind);
                    objects[s.Key] = po;
                    items.Add(s);
                }
                int n;
                shopKinds.TryGetValue(kind, out n);
                // EXCLUDED / SPECIAL items have no shop price (GlobalDungeonData.GetBasePrice logs an error) and are never sold
                bool sellable = po.quality >= PickupObject.ItemQuality.COMMON;
                if (kind != "other" && sellable && n < ShopPurchasesPerKind && Wanted(PhaseShop, id, name))
                {
                    shopKinds[kind] = n + 1;
                    StepResult s = AddStep("shop", id, name, owner, kind);
                    objects[s.Key] = po;
                    shop.Add(s);
                }
            }
        }

        private static string ItemKind(PickupObject po)
        {
            if (po is Gun)
            {
                return "gun";
            }
            if (po is PlayerItem)
            {
                return "active";
            }
            if (po is PassiveItem)
            {
                return "passive";
            }
            return "other";
        }

        // The mod whose component sits on the item, else the ID's namespace.
        private static string ItemOwner(Component c, string id)
        {
            string owner = ComponentOwner(c);
            if (owner != null)
            {
                return owner;
            }
            int colon = id.IndexOf(':');
            return colon > 0 ? "ns:" + id.Substring(0, colon) : "?";
        }

        private static string ComponentOwner(Component c)
        {
            if (c == null)
            {
                return null;
            }
            foreach (Component comp in c.GetComponents<Component>())
            {
                if (comp != null && SmokeLog.IsModAssembly(comp.GetType().Assembly))
                {
                    return comp.GetType().Assembly.GetName().Name;
                }
            }
            return null;
        }

        private IEnumerator ItemTest(StepResult s, PickupObject prefab)
        {
            PlayerController p = Player;
            int id = prefab.PickupObjectId;
            s.notes.Add("pickup id " + id);
            NPCCellKeyItem cellKey = prefab.GetComponent<NPCCellKeyItem>();
            if (cellKey != null)
            {
                // as MtG API's `give` does
                p.AcquirePuzzleItem(cellKey);
                yield return Wait(1f);
                yield break;
            }
            Act("give");
            bool given = LootEngine.TryGivePrefabToPlayer(prefab.gameObject, p, true);
            if (!given)
            {
                s.notes.Add("TryGivePrefabToPlayer returned false");
            }
            yield return Wait(0.5f);
            p = Player;
            if (p == null)
            {
                s.notes.Add("player gone after pickup");
                yield break;
            }
            if (prefab is Gun)
            {
                yield return GunTest(s, p, id);
            }
            else if (prefab is PlayerItem)
            {
                yield return ActiveTest(s, p, id);
            }
            else if (prefab is PassiveItem)
            {
                PassiveItem item = p.passiveItems.Find(delegate(PassiveItem x) { return x.PickupObjectId == id; });
                if (item == null)
                {
                    s.notes.Add("not among the passive items after pickup");
                    yield break;
                }
                yield return Wait(1f);
                Act("drop passive");
                Drop(s, p.DropPassiveItem(item), "passive");
            }
            else
            {
                yield return Wait(1f);
            }
            yield return Wait(0.5f);
        }

        private IEnumerator GunTest(StepResult s, PlayerController p, int id)
        {
            Gun gun = p.inventory.AllGuns.Find(delegate(Gun g) { return g.PickupObjectId == id; });
            if (gun == null)
            {
                s.notes.Add("not in the inventory after pickup");
                yield break;
            }
            if (p.CurrentGun != gun)
            {
                int slot = p.inventory.AllGuns.IndexOf(gun);
                p.ChangeToGunSlot(slot, true);
                yield return Frames(3);
            }
            if (p.CurrentGun != gun)
            {
                s.notes.Add("could not switch to it");
            }
            if (!gun.InfiniteAmmo)
            {
                gun.GainAmmo(gun.AdjustedMaxAmmo);
            }
            // two trigger pulls (semi-automatic guns), held for a while (charge and beam guns)
            for (int pull = 0; pull < 2; pull++)
            {
                Act("fire");
                gun.Attack(null, null);
                float end = Now + 0.8f;
                while (Now < end && gun != null)
                {
                    yield return null;
                    if (gun != null)
                    {
                        gun.ContinueAttack(true, null);
                    }
                }
                if (gun == null)
                {
                    s.notes.Add("gun destroyed while firing");
                    yield break;
                }
                gun.CeaseAttack(true, null);
                yield return Wait(0.3f);
            }
            Act("reload");
            gun.Reload();
            float reloadEnd = Now + 4f;
            while (gun != null && gun.IsReloading && Now < reloadEnd)
            {
                yield return null;
            }
            if (gun != null && gun.IsReloading)
            {
                s.notes.Add("still reloading after 4 s");
            }
            yield return Wait(0.3f);
            if (gun != null && p != null && p.inventory.AllGuns.Contains(gun))
            {
                Act("drop gun");
                Drop(s, p.ForceDropGun(gun), "gun");
            }
        }

        private IEnumerator ActiveTest(StepResult s, PlayerController p, int id)
        {
            PlayerItem item = p.activeItems.Find(delegate(PlayerItem x) { return x.PickupObjectId == id; });
            if (item == null)
            {
                s.notes.Add("not among the active items after pickup");
                yield break;
            }
            if (s_useItem == null)
            {
                s_useItem = typeof(PlayerController).GetMethod("UseItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }
            // UseItem uses the selected item: make it the only one.
            for (int i = p.activeItems.Count - 1; i >= 0; i--)
            {
                if (p.activeItems[i] != item)
                {
                    p.RemoveActiveItem(p.activeItems[i].PickupObjectId);
                }
            }
            if (!item.CanBeUsed(p))
            {
                s.notes.Add("CanBeUsed = false");
            }
            Act("use");
            s_useItem.Invoke(p, null);
            yield return Wait(1.5f);
            if (item != null && p != null && item.IsCurrentlyActive)
            {
                Act("use");
                s_useItem.Invoke(p, null); // toggles off
                yield return Wait(0.5f);
            }
            if (item != null && p != null && p.activeItems.Contains(item))
            {
                Act("drop active");
                Drop(s, p.DropActiveItem(item, 4f, false), "active");
            }
            else
            {
                s.notes.Add("used up");
            }
        }

        private void Drop(StepResult s, DebrisObject debris, string what)
        {
            if (debris == null)
            {
                s.notes.Add(what + " not dropped (cannot be dropped?)");
                return;
            }
            m_spawned.Add(debris.gameObject);
        }

        // Buys the item from a ShopItemController set up the way the shops do it, on the first
        // floor's shop: ShopItemController.Initialize / Interact with the player's money.
        private IEnumerator ShopTest(StepResult s, PickupObject prefab)
        {
            PlayerController p = Player;
            BaseShopController shop = null;
            // the shop room's objects can be inactive while the player is elsewhere: look through those too
            foreach (BaseShopController c in Resources.FindObjectsOfTypeAll<BaseShopController>())
            {
                if (c != null && c.gameObject.scene.IsValid() && c.baseShopType == BaseShopController.AdditionalShopType.NONE)
                {
                    shop = c;
                    break;
                }
            }
            if (shop == null)
            {
                s.status = "skipped";
                s.notes.Add("no shop on " + DungeonName());
                yield break;
            }
            RoomHandler room = shop.GetAbsoluteParentRoom();
            // Not under the shop: its room is inactive while the player is elsewhere, and an object destroyed while
            // inactive never runs OnDestroy - its SpeculativeRigidbody stayed in PhysicsEngine (an error every frame).
            // (Initialize places the item at its parent: an active stand-in spot next to the player)
            GameObject spot = new GameObject("[ModSmoke] shop spot");
            spot.transform.position = p.transform.position + new Vector3(2f, 0f, 0f);
            m_spawned.Add(spot);
            GameObject go = new GameObject("[ModSmoke] shop item " + prefab.name);
            go.transform.parent = spot.transform;
            go.transform.localPosition = Vector3.zero;
            m_spawned.Add(go);
            ShopItemController sic = go.AddComponent<ShopItemController>();
            if (room != null && !room.IsRegistered(sic))
            {
                room.RegisterInteractable(sic);
            }
            sic.Initialize(prefab, shop);
            yield return Frames(2);
            p.carriedConsumables.Currency = Math.Max(p.carriedConsumables.Currency, sic.ModifiedPrice + 10);
            int before = p.carriedConsumables.Currency;
            s.notes.Add("price " + sic.ModifiedPrice + " " + sic.CurrencyType);
            Act("buy");
            sic.Interact(p);
            yield return Wait(1f);
            if (p.HasPickupID(prefab.PickupObjectId))
            {
                s.notes.Add("bought (" + (before - p.carriedConsumables.Currency) + " paid)");
            }
            else
            {
                s.notes.Add("not in the player's inventory after Interact");
                if (s.status == "pending")
                {
                    s.status = "failed";
                }
            }
            if (room != null && sic != null && room.IsRegistered(sic))
            {
                room.DeregisterInteractable(sic);
            }
        }

        // ---- enemies ---------------------------------------------------------------------------------

        private void CollectEnemies(List<StepResult> enemies, Dictionary<string, AIActor> objects)
        {
            // Expand the Gungeon keeps its own GUID -> prefab map (EnemyDatabase.GetOrLoadByGuid is patched
            // to look there); other mods' enemies are database entries without a bundle path.
            Dictionary<string, AIActor> found = new Dictionary<string, AIActor>();
            Dictionary<string, string> owners = new Dictionary<string, string>();
            IDictionary expand = StaticMember(FindType("ExpandTheGungeon", "ExpandTheGungeon.ExpandPrefab.ExpandEnemyDatabase"), "enemyPrefabDictionary") as IDictionary;
            if (expand != null)
            {
                foreach (DictionaryEntry e in expand)
                {
                    AIActor a = e.Value as AIActor;
                    if (a != null && !found.ContainsKey((string)e.Key))
                    {
                        found[(string)e.Key] = a;
                        owners[(string)e.Key] = "ExpandTheGungeon";
                    }
                }
            }
            foreach (EnemyDatabaseEntry entry in EnemyDatabase.Instance.Entries)
            {
                if (entry == null || !string.IsNullOrEmpty(entry.path) || string.IsNullOrEmpty(entry.myGuid) || found.ContainsKey(entry.myGuid))
                {
                    continue;
                }
                AIActor a = null;
                try
                {
                    a = EnemyDatabase.GetOrLoadByGuid(entry.myGuid);
                }
                catch (Exception ex)
                {
                    SmokeLog.RecordException(ex, "EnemyDatabase.GetOrLoadByGuid " + entry.myGuid);
                }
                if (a != null)
                {
                    found[entry.myGuid] = a;
                }
            }
            List<string> guids = new List<string>(found.Keys);
            guids.Sort(delegate(string x, string y) { return string.Compare(found[x].name, found[y].name, StringComparison.Ordinal); });
            foreach (string guid in guids)
            {
                AIActor a = found[guid];
                string name = a.name + (string.IsNullOrEmpty(a.ActorName) || a.ActorName == a.name ? "" : " (" + a.ActorName + ")");
                if (!Wanted(PhaseEnemies, guid, name))
                {
                    continue;
                }
                string owner;
                if (!owners.TryGetValue(guid, out owner))
                {
                    owner = ComponentOwner(a) ?? "?";
                }
                bool boss = a.healthHaver != null && a.healthHaver.IsBoss;
                bool companion = a.GetComponent<CompanionController>() != null;
                StepResult s = AddStep("enemy", guid, name, owner, boss ? "boss" : companion ? "companion" : null);
                if (companion && !s.carried)
                {
                    // spawned without an owner its CompanionController.Update throws every frame; companions come
                    // with their items (item steps)
                    s.status = "skipped";
                    s.notes.Add("companion: tested through the item that brings it");
                    continue;
                }
                objects[s.Key] = a;
                enemies.Add(s);
            }
        }

        private IEnumerator EnemyTest(StepResult s, AIActor prefab)
        {
            PlayerController p = Player;
            RoomHandler room = p.CurrentRoom;
            IntVector2 playerCell = p.CenterPosition.ToIntVector2(VectorConversions.Floor);
            IntVector2? cell = room.GetRandomAvailableCell(prefab.Clearance, prefab.PathableTiles, false, delegate(IntVector2 c)
            {
                float d = IntVector2.Distance(playerCell, c);
                return d >= 3f && d <= 12f;
            });
            if (!cell.HasValue)
            {
                cell = room.GetRandomAvailableCell(prefab.Clearance, prefab.PathableTiles, true, null);
            }
            if (!cell.HasValue)
            {
                s.notes.Add("no free cell in " + room.GetRoomName() + "; spawning next to the player");
                cell = playerCell + new IntVector2(3, 0);
            }
            Act("spawn");
            AIActor actor = AIActor.Spawn(prefab, cell.Value, room, true, AIActor.AwakenAnimationType.Default, true);
            if (actor == null)
            {
                s.notes.Add("AIActor.Spawn returned null");
                yield break;
            }
            m_spawned.Add(actor.gameObject);
            yield return Wait(3f);
            if (actor == null)
            {
                s.notes.Add("gone before it was killed");
            }
            else if (actor.healthHaver != null && actor.healthHaver.IsAlive)
            {
                Act("kill");
                actor.healthHaver.ApplyDamage(1000000f, Vector2.zero, "ModSmoke", CoreDamageTypes.None, DamageCategory.Unstoppable, true, null, true);
                yield return Wait(2f);
                if (actor != null && actor.healthHaver != null && actor.healthHaver.IsAlive)
                {
                    s.notes.Add("still alive after 1e6 damage");
                }
            }
            // whatever it spawned
            yield return Wait(0.5f);
            if (p != null && p.CurrentRoom != null)
            {
                List<AIActor> left = p.CurrentRoom.GetActiveEnemies(RoomHandler.ActiveEnemyType.All);
                if (left != null)
                {
                    foreach (AIActor a in left.ToArray())
                    {
                        if (a != null)
                        {
                            a.EraseFromExistence(true);
                        }
                    }
                }
            }
        }

        // ---- floors and flows ------------------------------------------------------------------------

        private void CollectFloors(List<StepResult> floors, List<StepResult> flows, Dictionary<string, string[]> loads)
        {
            // Floors: Expand the Gungeon's dungeon prefabs (ExpandDungeonPrefabs.dungeonDatabase) with the
            // level definition the game finds for them, loaded like the mod does (GameManager.LoadCustomLevel).
            IDictionary dungeons = StaticMember(FindType("ExpandTheGungeon", "ExpandTheGungeon.ExpandPrefab.ExpandDungeonPrefabs"), "dungeonDatabase") as IDictionary;
            List<GameLevelDefinition> defs = new List<GameLevelDefinition>();
            defs.AddRange(GameManager.Instance.dungeonFloors);
            defs.AddRange(GameManager.Instance.customFloors);
            if (dungeons != null)
            {
                List<string> keys = new List<string>();
                foreach (object k in dungeons.Keys)
                {
                    keys.Add((string)k);
                }
                keys.Sort(StringComparer.Ordinal);
                foreach (string prefab in keys)
                {
                    GameLevelDefinition def = defs.Find(delegate(GameLevelDefinition d) { return d.dungeonPrefabPath != null && d.dungeonPrefabPath.ToLowerInvariant() == prefab; });
                    string scene = def == null ? null : def.dungeonSceneName;
                    if (!Wanted(PhaseFloors, scene ?? prefab, prefab))
                    {
                        continue;
                    }
                    StepResult s = AddStep("floor", scene ?? prefab, prefab, "ExpandTheGungeon", null);
                    loads[s.Key] = scene == null ? null : new[] { "level", scene };
                    floors.Add(s);
                }
            }
            // Flows: Expand the Gungeon's own (ExpandDungeonFlow.KnownFlows without the "_Copy" duplicates of
            // the game's flows), loaded the way the game / the mod loads them: boss rush flows in boss rush mode
            // (GameManager.InstantLoadBossRushFloor; the generator only links boss rooms to each other there),
            // glitch chest flows with ExpandDungeonFlow.isGlitchFlow on Phobos and Space
            // (ExpandUtility.DelayedGlitchLevelLoad), the rest with the tileset the flow name belongs to.
            Type expandFlows = FindType("ExpandTheGungeon", "ExpandTheGungeon.ExpandDungeonFlows.ExpandDungeonFlow");
            DungeonFlow[] known = StaticMember(expandFlows, "KnownFlows") as DungeonFlow[];
            List<string> glitchFlows = StaticMember(expandFlows, "GlitchChestFlows") as List<string> ?? new List<string>();
            if (known != null)
            {
                foreach (DungeonFlow flow in known)
                {
                    if (flow == null || string.IsNullOrEmpty(flow.name) || flow.name.EndsWith("_Copy") || !Wanted(PhaseFlows, flow.name, flow.name))
                    {
                        continue;
                    }
                    string mode = flow.name.ToLowerInvariant().Contains("bossrush") ? FlowModeBossRush
                        : glitchFlows.Contains(flow.name.ToLowerInvariant()) ? FlowModeGlitch : "";
                    string[] tileset = FlowTileset(flow.name);
                    StepResult s = AddStep("flow", flow.name, flow.name + " @ " + tileset[0] + (mode == "" ? "" : " (" + mode + ")"), "ExpandTheGungeon", null);
                    loads[s.Key] = new[] { "flow", flow.name, tileset[0], tileset[1], mode };
                    flows.Add(s);
                    if (mode == FlowModeGlitch)
                    {
                        // the mod picks Phobos or Space at random
                        s = AddStep("flow", flow.name + "@Base_Space", flow.name + " @ Base_Space (" + mode + ")", "ExpandTheGungeon", null);
                        loads[s.Key] = new[] { "flow", flow.name, "Base_Space", "tt_space", mode };
                        flows.Add(s);
                    }
                }
            }
        }

        private const string FlowModeBossRush = "boss rush";
        private const string FlowModeGlitch = "glitch";

        // Prefix of the flow name -> dungeon prefab and level (scene) name.
        private static readonly string[][] FlowTilesets =
        {
            new[] { "f1b_jungle", "Base_Jungle", "tt_jungle" },
            new[] { "f2b_belly", "Base_Belly", "tt_belly" },
            new[] { "f4c_west", "Base_West", "tt_west" },
            new[] { "test_west", "Base_West", "tt_west" },
            new[] { "f0b_phobos", "Base_Phobos", "tt_phobos" },
            new[] { "f0b_office", "Base_Office", "tt_office" },
            new[] { "f1b_future", "Base_Future", "tt_future" },
            new[] { "backrooms", "Base_BackRooms", "tt_backrooms" },
            new[] { "bossrush_01_castle", "Base_Castle", "tt_castle" },
            new[] { "bossrush_01a_sewer", "Base_Sewer", "tt_sewer" },
            new[] { "bossrush_02_gungeon", "Base_Gungeon", "tt5" },
            new[] { "bossrush_02a_cathedral", "Base_Cathedral", "tt_cathedral" },
            new[] { "bossrush_03_mines", "Base_Mines", "tt_mines" },
            new[] { "bossrush_04_catacombs", "Base_Catacombs", "tt_catacombs" },
            new[] { "bossrush_05_forge", "Base_Forge", "tt_forge" },
            new[] { "bossrush_06_bullethell", "Base_BulletHell", "tt_bullethell" },
            // glitch chest floors (ExpandUtility.DelayedGlitchLevelLoad: Phobos or Space)
            new[] { "custom_glitch", "Base_Phobos", "tt_phobos" },
            new[] { "apache_fucking_around", "Base_Phobos", "tt_phobos" },
        };

        private static string[] FlowTileset(string flow)
        {
            string f = flow.ToLowerInvariant();
            foreach (string[] t in FlowTilesets)
            {
                if (f.StartsWith(t[0]))
                {
                    return new[] { t[1], t[2] };
                }
            }
            return new[] { "Base_Gungeon", "tt5" };
        }

        // Every GenericRoomTable of ExpandPrefabs: entries without a room and rooms whose prerequisites fail now
        // (LoopFlowBuilder.GetViableAvailableRooms skips both; a SPECIAL node with nothing left breaks generation).
        private IEnumerator RoomTableDiag(StepResult s)
        {
            Type t = FindType("ExpandTheGungeon", "ExpandTheGungeon.ExpandPrefab.ExpandPrefabs");
            if (t == null)
            {
                s.status = "skipped";
                yield break;
            }
            int tables = 0, problems = 0;
            foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.FieldType != typeof(GenericRoomTable))
                {
                    continue;
                }
                GenericRoomTable table = f.GetValue(null) as GenericRoomTable;
                if (table == null)
                {
                    s.notes.Add(f.Name + ": null table");
                    problems++;
                    continue;
                }
                tables++;
                List<WeightedRoom> rooms = table.GetCompiledList();
                List<string> bad = new List<string>();
                int usable = 0;
                foreach (WeightedRoom w in rooms)
                {
                    if (w == null || w.room == null)
                    {
                        bad.Add("<no room>");
                    }
                    else if (!w.CheckPrerequisites() || !w.room.CheckPrerequisites())
                    {
                        bad.Add(w.room.name + " (prerequisites, " + w.room.category + ")");
                    }
                    else
                    {
                        usable++;
                    }
                }
                if (bad.Count > 0)
                {
                    problems++;
                    s.notes.Add(f.Name + " '" + table.name + "': " + usable + " of " + rooms.Count + " usable; " + string.Join(", ", bad.ToArray()));
                }
            }
            s.notes.Add(tables + " tables, " + problems + " with unusable rooms");
            yield break;
        }

        private IEnumerator FloorTest(StepResult s, string[] load)
        {
            if (load == null)
            {
                s.status = "skipped";
                s.notes.Add("no level definition for this dungeon prefab");
                yield break;
            }
            GameManager gm = GameManager.Instance;
            if (load[0] == "level")
            {
                gm.LoadCustomLevel(load[1]);
            }
            else
            {
                if (load[4] == FlowModeBossRush)
                {
                    // Cleanup puts the mode back after the step
                    m_restoreGameMode = gm.CurrentGameMode;
                    gm.CurrentGameMode = GameManager.GameMode.BOSSRUSH;
                }
                else if (load[4] == FlowModeGlitch)
                {
                    // the mod clears it again once the level is set up (ExpandObjectMods)
                    FieldInfo glitch = FindType("ExpandTheGungeon", "ExpandTheGungeon.ExpandDungeonFlows.ExpandDungeonFlow").GetField("isGlitchFlow", BindingFlags.Static | BindingFlags.Public);
                    glitch.SetValue(null, true);
                }
                gm.LoadCustomFlowForDebug(load[1], load[2], load[3]);
            }
            float t0 = Now;
            while (!GameManager.Instance.IsLoadingLevel && Now - t0 < 2f)
            {
                yield return null;
            }
            if (!GameManager.Instance.IsLoadingLevel)
            {
                // LoadCustomFlowForDebug returns quietly when FlowDatabase doesn't know the flow
                s.status = "failed";
                s.notes.Add("the load didn't start (still on " + DungeonName() + ")");
                yield break;
            }
            while (!InRun())
            {
                // Hopeless loads end early (the next step restarts the run, which gets the game out of them).
                if (s.loadCoroutineDied)
                {
                    s.status = "failed";
                    s.notes.Add("exception in the level loading coroutine: the load never finishes");
                    yield break;
                }
                int logged = 0;
                foreach (ErrorGroup g in s.errors)
                {
                    logged += g.count;
                }
                if (s.generationFailures >= 6 || logged > 20000) // big flows may need a few iterations
                {
                    s.status = "failed";
                    s.notes.Add("generation failed " + s.generationFailures + " times, " + logged + " errors (the game retries forever: no fitting rooms for this flow + tileset?)");
                    yield break;
                }
                yield return null;
            }
            s.notes.Add("loaded in " + (Now - t0).ToString("0.0", CultureInfo.InvariantCulture) + " s: " + DungeonName());
            SetGodmode();
            yield return Wait(2f);
            // Walk through some rooms: entering them runs the rooms' own setup (enemies, mod objects).
            List<RoomHandler> rooms = new List<RoomHandler>(GameManager.Instance.Dungeon.data.rooms);
            int visited = 0;
            foreach (RoomHandler room in rooms)
            {
                if (visited >= 12)
                {
                    break;
                }
                PlayerController p = Player;
                if (p == null || room == null || room == p.CurrentRoom || GameManager.Instance.IsLoadingLevel)
                {
                    continue;
                }
                // a boss intro takes the camera and input; floors are about generation and rooms
                if (room.area != null && room.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS)
                {
                    continue;
                }
                IntVector2? cell = room.GetRandomAvailableCell(new IntVector2(2, 2), CellTypes.FLOOR, false, null);
                if (!cell.HasValue)
                {
                    continue;
                }
                p.WarpToPoint(cell.Value.ToVector2(), false, false);
                visited++;
                yield return Wait(0.75f);
                // don't fight: clear what woke up
                if (Player != null && Player.CurrentRoom != null)
                {
                    List<AIActor> enemies = Player.CurrentRoom.GetActiveEnemies(RoomHandler.ActiveEnemyType.All);
                    if (enemies != null)
                    {
                        foreach (AIActor a in enemies.ToArray())
                        {
                            if (a != null && a.healthHaver != null && !a.healthHaver.IsBoss)
                            {
                                a.EraseFromExistence(true);
                            }
                        }
                    }
                }
            }
            s.notes.Add(visited + " of " + rooms.Count + " rooms visited");
            yield return Wait(1f);
        }

        // ---- reflection helpers ----------------------------------------------------------------------

        private static Type FindType(string assembly, string type)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == assembly)
                {
                    return a.GetType(type);
                }
            }
            return null;
        }

        private static object StaticMember(Type t, string name)
        {
            if (t == null)
            {
                return null;
            }
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo p = t.GetProperty(name, flags);
            if (p != null)
            {
                return p.GetValue(null, null);
            }
            FieldInfo f = t.GetField(name, flags);
            return f == null ? null : f.GetValue(null);
        }

        private static object InstanceMember(object o, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo p = o.GetType().GetProperty(name, flags);
            if (p != null)
            {
                return p.GetValue(o, null);
            }
            FieldInfo f = o.GetType().GetField(name, flags);
            return f == null ? null : f.GetValue(o);
        }

        private static string ExpandLoadStatus()
        {
            object status = StaticMember(FindType("ExpandTheGungeon", "ExpandTheGungeon.ExpandTheGungeon"), "loadStatus");
            return status == null ? null : status.ToString();
        }

        // ---- report ----------------------------------------------------------------------------------

        private string WriteReport()
        {
            try
            {
                Report r = new Report
                {
                    started = m_started.ToString("yyyy-MM-dd HH:mm:ss"),
                    minutes = (Now - m_startedRealtime) / 60f,
                    state = m_state,
                    attempt = m_attempt,
                    phases = string.Join(",", new List<string>(m_phases).ToArray()),
                    filter = string.Join(", ", m_filter.ToArray()),
                    mods = string.Join(", ", new List<string>(SmokeLog.ModAssemblies).ToArray()),
                    expandStatus = ExpandLoadStatus(),
                    runNotes = m_runNotes,
                    steps = m_steps,
                };
                foreach (KeyValuePair<string, ErrorGroup> g in SmokeLog.Global)
                {
                    r.errors.Add(g.Value);
                }
                foreach (KeyValuePair<string, int> f in SmokeLog.Filtered)
                {
                    r.filteredNoise.Add(f.Key + " x" + f.Value);
                }
                if (Application.isEditor)
                {
                    Directory.CreateDirectory(m_reportDir);
                    File.WriteAllText(Path.Combine(m_reportDir, "report.json"), JsonUtility.ToJson(r, true));
                    string md = Path.Combine(m_reportDir, "report.md");
                    File.WriteAllText(md, ReportWriter.Markdown(r));
                    return md;
                }
                if (m_finished)
                {
                    Debug.Log(SmokeLog.Prefix + "report\n" + ReportWriter.Markdown(r));
                }
                return "(console)";
            }
            catch (Exception e)
            {
                Debug.LogWarning(SmokeLog.Prefix + "writing the report failed: " + e);
                return null;
            }
        }
    }
}
