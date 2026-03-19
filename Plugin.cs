using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using FlowWormsAnimationStates;
using HarmonyLib;
using UnityEngine;

namespace BopDuplicateFix
{
    [BepInPlugin("BopDuplicateFix", "Patch Duplicates", "1.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal const string Category = "patchDuplicates";
        internal const string DisplayName = "Patch Duplicates";

        internal const string BeginCullModel = Category + "/begin track dupe cull";
        internal const string EndCullModel = Category + "/end track dupe cull";

        private void Awake()
        {
            RegisterEditorEvents();
            new Harmony("BopDuplicateFix").PatchAll();
            Logger.LogInfo(DisplayName + " loaded");
        }

        private void RegisterEditorEvents()
        {
            var templates = new List<MixtapeEventTemplate>
            {
                new MixtapeEventTemplate
                {
                    dataModel = BeginCullModel,
                    length = 0.5f,
                    properties = new Dictionary<string, object>
                    {
                        ["priority"] = 0
                    }
                },
                new MixtapeEventTemplate
                {
                    dataModel = EndCullModel,
                    length = 0.5f,
                    properties = new Dictionary<string, object>()
                }
            };

            MixtapeEventTemplates.entities[Category] = templates;

            var categories = MixtapeEventTemplates.Categories;
            if (!categories.Contains(Category))
            {
                int effectsIdx = categories.IndexOf("effects");
                if (effectsIdx >= 0)
                    categories.Insert(effectsIdx + 1, Category);
                else
                    categories.Insert(0, Category);
            }
        }
    }

    internal struct DuplicateInfo
    {
        public float nudgedTarget;
        public float leaderTarget;
        public float speed;
    }

    internal struct EntityRestore
    {
        public Entity entity;
        public string originalDataModel;
        public float originalBeat;
    }

    internal static class FixState
    {
        internal static readonly Dictionary<float, float> duplicateToLeader = new Dictionary<float, float>();
        internal static readonly HashSet<float> skipTargets = new HashSet<float>();
        internal static readonly HashSet<int> snapshotted = new HashSet<int>();
        internal static readonly List<DuplicateInfo> duplicates = new List<DuplicateInfo>();
        internal static readonly List<EntityRestore> mutations = new List<EntityRestore>();
        internal static bool inBeginInternal;
        internal static readonly HashSet<float> mutedBeats = new HashSet<float>();
        internal static ConcurrentDictionary<float, bool> targetToHitCache;

        internal static bool ShouldAllowSound(float beat)
        {
            if (!inBeginInternal) return true;
            return !mutedBeats.Contains(RoundTarget(beat));
        }

        internal static float RoundTarget(float target)
        {
            return Mathf.Round(target * 10000f) / 10000f;
        }

        internal static readonly FieldInfo isSkyField = AccessTools.Field(typeof(FlowWormsScript), "isSky");
        internal static readonly FieldInfo targetToHitField = AccessTools.Field(typeof(FlowWormsScript), "targetToHit");
        internal static readonly Func<FlowWormsScript, float, Animator> getBall =
            (Func<FlowWormsScript, float, Animator>)Delegate.CreateDelegate(
                typeof(Func<FlowWormsScript, float, Animator>),
                AccessTools.Method(typeof(FlowWormsScript), "GetBall"));

        internal static void Clear()
        {
            duplicateToLeader.Clear();
            skipTargets.Clear();
            duplicates.Clear();
            mutations.Clear();
            snapshotted.Clear();
            mutedBeats.Clear();
            targetToHitCache = null;
        }
    }

    [HarmonyPatch(typeof(FlowWormsScript), "BeginInternal")]
    static class BeginInternalPatch
    {
        static void Prefix(FlowWormsScript __instance, Entity[] entities)
        {
            FixState.Clear();
            FixState.inBeginInternal = true;

            if (entities == null)
                return;

            bool isSky = (bool)FixState.isSkyField.GetValue(__instance);
            string gameName = isSky ? "flowWormsSky" : "flowWorms";

            var cullZones = BuildCullZones(entities);
            var balls = CollectBalls(entities, gameName);

            if (balls.Count == 0)
                return;

            var groups = GroupByTarget(balls);
            ProcessGroups(groups, balls, cullZones, entities);
        }

        static void Postfix(FlowWormsScript __instance)
        {
            FixState.inBeginInternal = false;

            FixState.targetToHitCache = (ConcurrentDictionary<float, bool>)FixState.targetToHitField.GetValue(__instance);

            foreach (var restore in FixState.mutations)
            {
                restore.entity.dataModel = restore.originalDataModel;
                restore.entity.beat = restore.originalBeat;
            }

            if (FixState.duplicates.Count == 0)
                return;

            ScheduleDuplicateBounces(__instance);
        }



        struct CullZone
        {
            public float start;
            public float end;
            public int priority;
        }

        struct CullMarker
        {
            public float beat;
            public bool isEnd;
            public int priority;
        }

        static Dictionary<int, List<CullZone>> BuildCullZones(Entity[] entities)
        {
            var markersByTrack = new Dictionary<int, List<CullMarker>>();
            foreach (var entity in entities)
            {
                bool isEnd;
                int priority = 0;
                if (entity.dataModel == Plugin.BeginCullModel)
                {
                    isEnd = false;
                    try { priority = entity.GetInt("priority"); } catch { priority = 0; }
                }
                else if (entity.dataModel == Plugin.EndCullModel)
                {
                    isEnd = true;
                }
                else
                {
                    continue;
                }

                List<CullMarker> list;
                if (!markersByTrack.TryGetValue(entity.track, out list))
                {
                    list = new List<CullMarker>();
                    markersByTrack[entity.track] = list;
                }
                list.Add(new CullMarker { beat = entity.beat, isEnd = isEnd, priority = priority });
            }

            var zones = new Dictionary<int, List<CullZone>>();
            foreach (var kv in markersByTrack)
            {
                int track = kv.Key;
                List<CullMarker> markers = kv.Value;
                markers.Sort((a, b) => a.beat.CompareTo(b.beat));

                var trackZones = new List<CullZone>();
                float openStart = float.NaN;
                int openPriority = 0;

                foreach (var marker in markers)
                {
                    if (!marker.isEnd)
                    {
                        openStart = marker.beat;
                        openPriority = marker.priority;
                    }
                    else
                    {
                        if (!float.IsNaN(openStart))
                        {
                            trackZones.Add(new CullZone { start = openStart, end = marker.beat, priority = openPriority });
                            openStart = float.NaN;
                        }
                    }
                }

                if (trackZones.Count > 0)
                    zones[track] = trackZones;
            }
            return zones;
        }

        static int GetCullPriority(Dictionary<int, List<CullZone>> zones, int track, float beat)
        {
            List<CullZone> trackZones;
            if (!zones.TryGetValue(track, out trackZones))
                return int.MaxValue;
            foreach (var zone in trackZones)
            {
                if (beat >= zone.start && beat < zone.end)
                    return zone.priority;
            }
            return int.MaxValue;
        }


        private const float BallTravelBeats = 6f;
        private static readonly char[] SlashSep = { '/' };

        struct BallInfo
        {
            public int entityIndex;
            public float target;
            public float speed;
            public int track;
        }

        static List<BallInfo> CollectBalls(Entity[] entities, string gameName)
        {
            var balls = new List<BallInfo>();
            for (int i = 0; i < entities.Length; i++)
            {
                string[] parts = entities[i].dataModel.Split(SlashSep, 2);
                if (parts.Length < 2 || parts[0] != gameName)
                    continue;

                float speed;
                switch (parts[1])
                {
                    case "ball":      speed = 1f; break;
                    case "fast ball": speed = 2f; break;
                    case "slow ball": speed = 0.5f; break;
                    default: continue;
                }

                balls.Add(new BallInfo
                {
                    entityIndex = i,
                    target = entities[i].beat + BallTravelBeats / speed,
                    speed = speed,
                    track = entities[i].track
                });
            }
            return balls;
        }


        static Dictionary<float, List<int>> GroupByTarget(List<BallInfo> balls)
        {
            var groups = new Dictionary<float, List<int>>();
            for (int i = 0; i < balls.Count; i++)
            {
                float key = FixState.RoundTarget(balls[i].target);
                List<int> group;
                if (!groups.TryGetValue(key, out group))
                {
                    group = new List<int>();
                    groups[key] = group;
                }
                group.Add(i);
            }
            return groups;
        }

        static void ProcessGroups(
            Dictionary<float, List<int>> groups,
            List<BallInfo> balls,
            Dictionary<int, List<CullZone>> cullZones,
            Entity[] entities)
        {
            foreach (var kv in groups)
            {
                List<int> group = kv.Value;
                if (group.Count <= 1)
                    continue;

                var survivors = new List<int>();
                var cullable = new List<int>();
                var cullPriorities = new Dictionary<int, int>();

                foreach (int i in group)
                {
                    BallInfo ball = balls[i];
                    int prio = GetCullPriority(cullZones, ball.track, entities[ball.entityIndex].beat);
                    if (prio != int.MaxValue)
                    {
                        cullable.Add(i);
                        cullPriorities[i] = prio;
                    }
                    else
                    {
                        survivors.Add(i);
                    }
                }

                cullable.Sort((a, b) => cullPriorities[a].CompareTo(cullPriorities[b]));

                if (survivors.Count > 0)
                {
                    foreach (int i in cullable)
                        MutateDataModel(entities, balls[i].entityIndex, "_purged/" + entities[balls[i].entityIndex].dataModel);
                }
                else if (cullable.Count > 0)
                {
                    int bestPriority = cullPriorities[cullable[0]];
                    foreach (int i in cullable)
                    {
                        if (cullPriorities[i] == bestPriority)
                            survivors.Add(i);
                        else
                            MutateDataModel(entities, balls[i].entityIndex, "_purged/" + entities[balls[i].entityIndex].dataModel);
                    }
                }

                if (survivors.Count <= 1)
                    continue;

                float leaderTarget = balls[survivors[0]].target;
                for (int g = 1; g < survivors.Count; g++)
                {
                    int i = survivors[g];
                    BallInfo ball = balls[i];
                    float delta = g * 0.002f;
                    MutateBeat(entities, ball.entityIndex, entities[ball.entityIndex].beat + delta);

                    float nudgedBeat = entities[ball.entityIndex].beat;
                    for (int n = 0; n <= 8; n++)
                        FixState.mutedBeats.Add(FixState.RoundTarget(nudgedBeat + (float)n / ball.speed));

                    float nudgedTarget = entities[ball.entityIndex].beat + 6f / ball.speed;
                    FixState.duplicateToLeader[FixState.RoundTarget(nudgedTarget)] = leaderTarget;
                    FixState.skipTargets.Add(FixState.RoundTarget(nudgedTarget));
                    FixState.duplicates.Add(new DuplicateInfo
                    {
                        nudgedTarget = nudgedTarget,
                        leaderTarget = leaderTarget,
                        speed = ball.speed
                    });
                }
            }
        }

        static void SnapshotForRestore(Entity[] entities, int idx)
        {
            if (FixState.snapshotted.Contains(idx))
                return;
            FixState.snapshotted.Add(idx);
            FixState.mutations.Add(new EntityRestore
            {
                entity = entities[idx],
                originalDataModel = entities[idx].dataModel,
                originalBeat = entities[idx].beat
            });
        }

        static void MutateDataModel(Entity[] entities, int idx, string newDataModel)
        {
            SnapshotForRestore(entities, idx);
            entities[idx].dataModel = newDataModel;
        }

        static void MutateBeat(Entity[] entities, int idx, float newBeat)
        {
            SnapshotForRestore(entities, idx);
            entities[idx].beat = newBeat;
        }


        static void ScheduleDuplicateBounces(FlowWormsScript instance)
        {
            Scheduler scheduler = instance.scheduler;
            var targetToHit = FixState.targetToHitCache;

            foreach (DuplicateInfo dup in FixState.duplicates)
            {
                float nudgedTarget = dup.nudgedTarget;
                float speed = dup.speed;
                float leaderTarget = dup.leaderTarget;

                scheduler.Schedule((double)nudgedTarget, delegate
                {
                    bool wasHit;
                    if (!targetToHit.TryGetValue(leaderTarget, out wasHit))
                        wasHit = false;

                    targetToHit[nudgedTarget] = wasHit;

                    if (!wasHit)
                        return;

                    Animator ball = FixState.getBall(instance, nudgedTarget);
                    if (ball == null) return;
                    float xAdjust = (speed <= 1f) ? -0.15f : -0.2f;
                    ball.transform.parent.SetX((6f - 3.5f) * 1.86f + xAdjust);
                    ball.transform.parent.transform.SetZ(-1f + nudgedTarget * 1E-05f);
                    ball.speed = speed;

                    if (speed >= 2f)
                        ball.SetState(Ball.BounceLow);
                    else if (speed <= 0.5f)
                        ball.SetState(Ball.BounceHigh);
                    else
                        ball.SetState(Ball.Bounce);
                });
            }
        }
    }

    [HarmonyPatch(typeof(InputManager), "AddTarget")]
    static class AddTargetPatch
    {
        static bool Prefix(Target target)
        {
            if (!FixState.inBeginInternal)
                return true;

            return !FixState.skipTargets.Contains(FixState.RoundTarget(target.press));
        }
    }

    [HarmonyPatch(typeof(FlowWormsScript), "Bounce")]
    static class BouncePatch
    {
        static void Prefix(FlowWormsScript __instance, float target)
        {
            if (FixState.duplicateToLeader.Count == 0) return;

            float leader;
            if (!FixState.duplicateToLeader.TryGetValue(FixState.RoundTarget(target), out leader))
                return;

            var targetToHit = FixState.targetToHitCache;
            if (targetToHit == null) return;

            bool hitState;
            if (targetToHit.TryGetValue(leader, out hitState))
                targetToHit[FixState.RoundTarget(target)] = hitState;
        }
    }

    [HarmonyPatch(typeof(JukeboxScript), "Schedule", new Type[] { typeof(TempoSound), typeof(float) })]
    [HarmonyPatch(typeof(JukeboxScript), "Schedule", new Type[] { typeof(TempoSound), typeof(float), typeof(float) })]
    [HarmonyPatch(typeof(JukeboxScript), "ScheduleOffset")]
    [HarmonyPatch(typeof(JukeboxScript), "ScheduleOffsetScaled")]
    static class MuteSchedulePatch
    {
        static bool Prefix(float beat) => FixState.ShouldAllowSound(beat);
    }

    [HarmonyPatch(typeof(MixtapeEditorScript), "GameNameToDisplay")]
    static class EditorDisplayNamePatch
    {
        static bool Prefix(string name, ref string __result)
        {
            if (name == Plugin.Category)
            {
                __result = Plugin.DisplayName;
                return false;
            }
            return true;
        }
    }
}
