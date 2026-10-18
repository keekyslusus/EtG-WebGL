using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Dungeonator;
using UnityEngine;

// Phase "batching" (not part of "all"): how many draw calls Unity's dynamic batching saves in this game, to judge
// what losing it costs (Unity 6.6 removed it; port/tasks/unity6-migration.md). In the start room of the first
// floor, as it is and then with a dozen shooting enemies around the player (godmode), dynamic batching is switched
// on and off a few times through the editor's player settings and UnityStats is averaged over each stretch.
// Editor only (UnityEditor.UnityStats / PlayerSettings by reflection, so this assembly stays runtime-only); the
// original batching setting is restored afterwards, also when the run ends early. Same C# 4 restrictions as the
// rest of the test.
namespace EtgModSmoke
{
    public partial class SmokeTest
    {
        public const string PhaseBatching = "batching";

        private const int BatchingSettleFrames = 30;
        private const int BatchingSampleFrames = 240;
        private const int BatchingRounds = 2;

        // vanilla enemies that shoot a lot: Bullet Kin, red / blue Shotgun Kin
        private static readonly string[] BatchingEnemies =
        {
            "01972dee89fc4404a5c408d50007dad5", "01972dee89fc4404a5c408d50007dad5", "01972dee89fc4404a5c408d50007dad5",
            "01972dee89fc4404a5c408d50007dad5", "01972dee89fc4404a5c408d50007dad5", "01972dee89fc4404a5c408d50007dad5",
            "128db2f0781141bcb505d8f00f9e4d47", "128db2f0781141bcb505d8f00f9e4d47", "128db2f0781141bcb505d8f00f9e4d47",
            "b54d89f9e802455cbb2b8a96a31e8259", "b54d89f9e802455cbb2b8a96a31e8259", "b54d89f9e802455cbb2b8a96a31e8259",
        };

        private static readonly string[] BatchingCounters =
        {
            "batches", "drawCalls", "dynamicBatchedDrawCalls", "dynamicBatches", "staticBatchedDrawCalls", "setPassCalls",
        };

        // (target, static, dynamic) to put back; null when nothing was changed
        private static object[] s_batchingRestore;

        private static Type EditorType(string name)
        {
            return Type.GetType(name + ", UnityEditor");
        }

        private static void SetBatching(object target, int staticBatching, int dynamicBatching)
        {
            EditorType("UnityEditor.PlayerSettings").GetMethod("SetBatchingForPlatform", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { target, staticBatching, dynamicBatching });
        }

        private static void RestoreBatching()
        {
            object[] r = s_batchingRestore;
            if (r == null)
            {
                return;
            }
            s_batchingRestore = null;
            try
            {
                SetBatching(r[0], (int)r[1], (int)r[2]);
                Log("batching: player settings restored (static " + r[1] + ", dynamic " + r[2] + ")");
            }
            catch (Exception e)
            {
                Debug.LogError(SmokeLog.Prefix + "batching: could not restore the player settings: " + e);
            }
        }

        private class BatchingSample
        {
            public int frames;
            public double ms;
            public readonly double[] sum = new double[BatchingCounters.Length];
            public readonly int[] max = new int[BatchingCounters.Length];
        }

        private IEnumerator BatchingTest(StepResult s)
        {
            Type stats = EditorType("UnityEditor.UnityStats");
            Type settings = EditorType("UnityEditor.PlayerSettings");
            Type build = EditorType("UnityEditor.EditorUserBuildSettings");
            if (!Application.isEditor || stats == null || settings == null || build == null)
            {
                s.status = "skipped";
                s.notes.Add("editor only (UnityEditor.UnityStats)");
                yield break;
            }
            PropertyInfo[] counters = new PropertyInfo[BatchingCounters.Length];
            for (int i = 0; i < counters.Length; i++)
            {
                counters[i] = stats.GetProperty(BatchingCounters[i], BindingFlags.Public | BindingFlags.Static);
            }
            object target = build.GetProperty("activeBuildTarget", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
            object[] current = { target, 0, 0 };
            settings.GetMethod("GetBatchingForPlatform", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, current);
            int staticBatching = (int)current[1];
            s_batchingRestore = new object[] { target, current[1], current[2] };
            s.notes.Add("build target " + target + ", player settings: static batching " + current[1] + ", dynamic " + current[2]);

            Dictionary<string, BatchingSample> results = new Dictionary<string, BatchingSample>();
            List<string> order = new List<string>();
            string[] scenes = { "start room", "start room + 12 shooting enemies" };
            for (int scene = 0; scene < scenes.Length; scene++)
            {
                if (scene == 1)
                {
                    Act("spawn enemies");
                    SpawnBatchingEnemies(s);
                    yield return Wait(4f); // awaken, start shooting
                }
                for (int round = 0; round < BatchingRounds; round++)
                {
                    for (int dyn = 1; dyn >= 0; dyn--)
                    {
                        string key = scenes[scene] + ", dynamic batching " + (dyn == 1 ? "on" : "off");
                        BatchingSample sample;
                        if (!results.TryGetValue(key, out sample))
                        {
                            sample = new BatchingSample();
                            results[key] = sample;
                            order.Add(key);
                        }
                        Act(key);
                        SetBatching(target, staticBatching, dyn);
                        yield return Frames(BatchingSettleFrames);
                        for (int f = 0; f < BatchingSampleFrames; f++)
                        {
                            yield return null;
                            sample.frames++;
                            sample.ms += Time.unscaledDeltaTime * 1000.0;
                            for (int i = 0; i < counters.Length; i++)
                            {
                                int v = counters[i] == null ? -1 : (int)counters[i].GetValue(null, null);
                                sample.sum[i] += v;
                                sample.max[i] = Math.Max(sample.max[i], v);
                            }
                        }
                    }
                }
            }
            RestoreBatching();

            foreach (string key in order)
            {
                BatchingSample r = results[key];
                List<string> parts = new List<string>();
                for (int i = 0; i < counters.Length; i++)
                {
                    parts.Add(BatchingCounters[i] + " " + (r.sum[i] / r.frames).ToString("0.0", CultureInfo.InvariantCulture) + " (max " + r.max[i] + ")");
                }
                string line = key + ": " + string.Join(", ", parts.ToArray()) + "; editor frame " +
                    (r.ms / r.frames).ToString("0.0", CultureInfo.InvariantCulture) + " ms over " + r.frames + " frames";
                s.notes.Add(line);
                Log("batching: " + line);
            }
            s.status = "ok";
        }

        private void SpawnBatchingEnemies(StepResult s)
        {
            PlayerController p = Player;
            RoomHandler room = p.CurrentRoom;
            IntVector2 playerCell = p.CenterPosition.ToIntVector2(VectorConversions.Floor);
            int spawned = 0;
            foreach (string guid in BatchingEnemies)
            {
                AIActor prefab = EnemyDatabase.GetOrLoadByGuid(guid);
                if (prefab == null)
                {
                    s.notes.Add("no enemy " + guid);
                    continue;
                }
                IntVector2? cell = room.GetRandomAvailableCell(prefab.Clearance, prefab.PathableTiles, false, delegate(IntVector2 c)
                {
                    float d = IntVector2.Distance(playerCell, c);
                    return d >= 4f && d <= 10f;
                });
                if (!cell.HasValue)
                {
                    cell = playerCell + new IntVector2(4 + spawned % 3, spawned / 3 - 2);
                }
                AIActor actor = AIActor.Spawn(prefab, cell.Value, room, true, AIActor.AwakenAnimationType.Default, true);
                if (actor != null)
                {
                    m_spawned.Add(actor.gameObject);
                    spawned++;
                }
            }
            s.notes.Add(spawned + " enemies spawned in " + room.GetRoomName());
        }
    }
}
