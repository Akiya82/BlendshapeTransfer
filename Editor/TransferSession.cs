using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    internal sealed class ShapeResult
    {
        public string Name;
        public float[] FrameWeights;
        /// <summary>The body's weight W₀ of the shape in the snapshot.</summary>
        public float BaseWeight;
        /// <summary>The garment at body weight 0 relative to the snapshot, mesh space, and the normal change it makes; null when W₀ is 0.</summary>
        public Vector3[] BaseMesh;
        public Vector3[] BaseNormals;
        /// <summary>BaseMesh in world space; zero when W₀ is 0.</summary>
        public Vector3[] BaseWorld;
        /// <summary>Per frame: the automatic response relative to the snapshot, world space.</summary>
        public Vector3[][] AutoWorld;
        /// <summary>Per frame: the written delta over the base, mesh space, edits applied.</summary>
        public Vector3[][] FinalMesh;
        /// <summary>Per frame: the garment relative to the snapshot (base plus final frame), world space.</summary>
        public Vector3[][] FinalWorld;
        public Vector3[][] NormalMesh;
        public float MaxMove;
        /// <summary>The garment's input fingerprint this result was computed under; it counts only while that is still the garment's.</summary>
        public ulong Fingerprint;
    }

    /// <summary>What a transfer did to one garment: numbers and lists, no text (the window renders it).</summary>
    [Serializable]
    public sealed class GarmentReport
    {
        public List<string> Written = new List<string>();
        public List<SkippedShape> Skipped = new List<SkippedShape>();
        /// <summary>Shapes written with a base offset, at the body weight W₀ the offset is for.</summary>
        public List<BaseWeight> BaseOffset = new List<BaseWeight>();
        public int Fallback;
        public int Singular;
        public int WeightMismatch;
        public ClipStats Clip;
        /// <summary>The largest move of a written shape's frame, metres.</summary>
        public float MaxMove;

        /// <summary>The counters that call for a look: still clipping (skin, lower layers, intermediate weights), unresolved pushes.</summary>
        public int WarningCount =>
            (Clip.StillSkin > 0 ? 1 : 0) + (Clip.StillLayer > 0 ? 1 : 0) + (Clip.Intermediate > 0 ? 1 : 0) + (Clip.Unresolved > 0 ? 1 : 0);
    }

    [Serializable]
    public struct SkippedShape
    {
        public string name;
        /// <summary>A message key: Msg.SkipKept or Msg.SkipNoReach.</summary>
        public string reason;
    }

    internal sealed class GarmentState
    {
        public SkinnedMeshRenderer Renderer;
        public TransferData Data;
        public GarmentSnapshot Snapshot;
        /// <summary>InputFingerprint.MeshContent of the source, taken at the read.</summary>
        public ulong Content;
        /// <summary>InputFingerprint.SnapshotState of the renderer, taken at the read.</summary>
        public ulong State;
        /// <summary>The clamped settings (JSON) and rigid marks Rigid and Attached were built for.</summary>
        public string PreparedFor;
        public HashSet<int> Rigid;
        public Attachments Attached;
        public ColliderSurface Collider;
        public SurfacePoint[] Closest;
        public float[] Distance;
        public Correspondence Correspondence;
        /// <summary>The settings Correspondence was built from, as JSON (its own copy is clamped, so it cannot be compared).</summary>
        public string CorrespondenceSettings;
        public ClipCandidates BodyCandidates;
        /// <summary>Body vertices under the garment at rest: reach counts their motion, anti-clip keeps them under it.</summary>
        public SkinCandidates SkinCandidates;
        public readonly Dictionary<GarmentState, ClipCandidates> LayerCandidates = new Dictionary<GarmentState, ClipCandidates>();
        public readonly Dictionary<string, ShapeResult> Results = new Dictionary<string, ShapeResult>();
        /// <summary>Per body shape, the largest skin motion from the snapshot to weight 0 or any frame weight, under the garment within falloffZero, metres.</summary>
        public readonly Dictionary<string, float> Reach = new Dictionary<string, float>();
        /// <summary>The falloffZero Reach was computed for.</summary>
        public float ReachFalloffZero;
        /// <summary>Warnings of the current read (edits dropped, singular skinning, bones without a body bone).</summary>
        public readonly List<Message> Warnings = new List<Message>();
        /// <summary>The report its output was written with (from the output state after a read); null before the first transfer.</summary>
        public GarmentReport Report;

        /// <summary>The current read's warnings plus the stored report's.</summary>
        public int WarningCount => Warnings.Count + (Report?.WarningCount ?? 0);
    }

    /// <summary>What one Transfer did; the window's status line reads it.</summary>
    internal sealed class TransferRun
    {
        public readonly List<GarmentState> Written = new List<GarmentState>();
        /// <summary>Garments skipped as up to date.</summary>
        public readonly List<GarmentState> Skipped = new List<GarmentState>();
        public bool Cancelled;
        public GarmentState Failed;
        /// <summary>The failed garment was being computed as a collider of one above it.</summary>
        public bool FailedAsCollider;
        public string Error;

        public bool Completed => !Cancelled && Failed == null;
    }

    /// <summary>A body and its garments in layer order (inner first) in one snapshot pose; computes, writes and assigns the transferred shapes.</summary>
    internal sealed class TransferSession
    {
        public const float ReachThreshold = 0.0001f;
        public const float NoEffect = 0.00001f;
        public const float PoseTolerance = 0.0001f;
        public const float WeightTolerance = 0.01f;
        public readonly Transform Avatar;
        public readonly SkinnedMeshRenderer Body;
        public readonly BodySurface Surface;
        /// <summary>InputFingerprint.MeshContent of the body, taken at the read.</summary>
        public readonly ulong BodyContent;
        /// <summary>InputFingerprint.SnapshotState of the body, taken at the read.</summary>
        public readonly ulong BodyState;
        public readonly ColliderSurface BodyCollider;
        public readonly List<GarmentState> Garments = new List<GarmentState>();
        private readonly List<(Transform Bone, Matrix4x4 Matrix)> pose = new List<(Transform, Matrix4x4)>();
        /// <summary>
        /// The renderers' own weights at Open, and the names of the shapes whose fit record the snapshot shows instead (asked at check
        /// time, since a transfer adds records).
        /// </summary>
        private readonly List<(SkinnedMeshRenderer Renderer, Mesh Mesh, float[] Weights, Func<IEnumerable<string>> Recorded)> liveWeights =
            new List<(SkinnedMeshRenderer, Mesh, float[], Func<IEnumerable<string>>)>();
        /// <summary>Play Mode as Open sees it; tests replace it.</summary>
        internal static Func<bool> InPlayMode = () => EditorApplication.isPlayingOrWillChangePlaymode;

        public static TransferSession Open(SkinnedMeshRenderer body, IReadOnlyList<SkinnedMeshRenderer> garments, List<Message> errors,
            IReadOnlyDictionary<SkinnedMeshRenderer, TransferData> carried = null)
        {
            errors.Clear();
            if (InPlayMode())
            {
                errors.Add(new Message(Msg.PlayMode));
                return null;
            }
            if (body == null || body.sharedMesh == null)
            {
                errors.Add(new Message(Msg.NoBody));
                return null;
            }
            // the session writes to the scene: an asset's avatar root is not the scene's
            if (InAsset(body))
            {
                errors.Add(new Message(Msg.InAsset, body.name));
                return null;
            }
            var avatar = AvatarPaths.RootOf(body.transform);
            if (avatar == null) errors.Add(new Message(Msg.NoAvatar, body.name));
            if (garments.Count == 0) errors.Add(new Message(Msg.NoGarments));
            for (var i = 0; i < garments.Count; i++)
            {
                var g = garments[i];
                if (g == null || g.sharedMesh == null) errors.Add(new Message(Msg.GarmentEmpty, i + 1));
                else if (InAsset(g)) errors.Add(new Message(Msg.InAsset, g.name));
                else if (g == body) errors.Add(new Message(Msg.GarmentIsBody, g.name));
                else if (avatar != null && AvatarPaths.RootOf(g.transform) != avatar) errors.Add(new Message(Msg.NotUnderAvatar, g.name, avatar.name));
                else if (garments.Take(i).Contains(g)) errors.Add(new Message(Msg.ListedTwice, g.name));
                // same-named siblings: their data, output and "not worn with" marks could not be told apart
                else if (avatar != null && garments.Take(i).FirstOrDefault(o => o != null && PathOf(o) == PathOf(g)) is SkinnedMeshRenderer same)
                    errors.Add(new Message(Msg.SamePath, same.name, g.name, PathOf(g)));
            }
            CheckReadable(body.sharedMesh, errors);
            if (errors.Count > 0) return null;

            // the garments' data first: the body snapshot shows the weights they were fitted at
            var found = new List<TransferData>();
            foreach (var g in garments)
            {
                var data = OutputWriter.FindData(g, avatar.name, AvatarPaths.RelativePath(avatar, g.transform));
                // settings of a garment that was never written live only in memory: a read in between must not lose them
                if ((data == null || !AssetDatabase.Contains(data)) && carried != null && carried.TryGetValue(g, out var kept) && kept != null && kept.sourceMesh == (data != null ? data.sourceMesh : g.sharedMesh))
                    data = kept;
                var sharing = data == null ? -1 : found.IndexOf(data);
                if (sharing >= 0) errors.Add(new Message(Msg.SameData, garments[sharing].name, g.name, AssetDatabase.GetAssetPath(data)));
                found.Add(data);
            }
            var fit = FitWeights(garments, found, errors);
            if (errors.Count > 0) return null;
            var oldPaths = found.Select(d => d == null ? null : d.rendererPath).ToList();

            var session = new TransferSession(avatar, body, fit);
            for (var i = 0; i < garments.Count; i++)
            {
                var state = session.Load(garments[i], found[i], errors);
                if (state != null) session.Garments.Add(state);
            }
            if (errors.Count > 0) return null;
            session.FollowMoves(oldPaths);
            session.OrderLayers();
            session.pose.AddRange(body.bones.Concat(garments.SelectMany(g => g.bones)).Append(body.transform).Concat(garments.Select(g => g.transform))
                .Where(t => t != null).Distinct().Select(t => (t, t.localToWorldMatrix)));
            session.liveWeights.Add((body, session.Surface.Mesh, ShapeMath.WeightsOf(body, session.Surface.Mesh),
                () => session.Garments.SelectMany(g => g.Data.baseWeights).Select(b => b.name)));
            foreach (var g in session.Garments)
                session.liveWeights.Add((g.Renderer, g.Snapshot.Source, ShapeMath.WeightsOf(g.Renderer, g.Snapshot.Source), () => g.Data.garmentWeights.Select(b => b.name)));
            return session;

            string PathOf(SkinnedMeshRenderer r) => AvatarPaths.RelativePath(avatar, r.transform);
        }

        /// <summary>
        /// Keeps a garment renamed or moved since its data was written in the other garments' "not worn with" lists: Load stored its
        /// current path, and its old path there becomes the current one. oldPaths are the stored paths in Garments' order, null for new data.
        /// </summary>
        private void FollowMoves(IReadOnlyList<string> oldPaths)
        {
            var moved = new Dictionary<string, string>();
            for (var i = 0; i < Garments.Count; i++)
                if (!string.IsNullOrEmpty(oldPaths[i]) && oldPaths[i] != Garments[i].Data.rendererPath) moved[oldPaths[i]] = Garments[i].Data.rendererPath;
            if (moved.Count == 0) return;
            foreach (var g in Garments)
            {
                var list = g.Data.notWornWith;
                for (var n = 0; n < list.Count; n++)
                    if (moved.TryGetValue(list[n], out var now)) list[n] = now;
            }
        }

        private TransferSession(Transform avatar, SkinnedMeshRenderer body, IEnumerable<BaseWeight> fit)
        {
            Avatar = avatar;
            Body = body;
            Surface = new BodySurface(body, fit);
            BodyCollider = new ColliderSurface(Surface.Topology, Surface.Positions, null);
            BodyContent = InputFingerprint.MeshContent(Surface.Mesh);
            BodyState = InputFingerprint.SnapshotState(avatar, body, Surface.Mesh, Surface.Weights);
        }

        /// <summary>The body weights the garments were fitted at, by shape; two garments fitted at different weights of one shape are an error.</summary>
        private static List<BaseWeight> FitWeights(IReadOnlyList<SkinnedMeshRenderer> garments, List<TransferData> found, List<Message> errors)
        {
            var fit = new Dictionary<string, (float Weight, string Garment)>();
            for (var i = 0; i < garments.Count; i++)
            {
                if (found[i] == null) continue;
                foreach (var b in found[i].baseWeights)
                {
                    if (!fit.TryGetValue(b.name, out var other)) fit[b.name] = (b.weight, garments[i].name);
                    else if (Mathf.Abs(other.Weight - b.weight) > WeightTolerance)
                        errors.Add(new Message(Msg.FitConflict, b.name, other.Garment, other.Weight, garments[i].name, b.weight));
                }
            }
            return fit.Select(kv => new BaseWeight { name = kv.Key, weight = kv.Value.Weight }).ToList();
        }

        /// <summary>
        /// True when a bone moved or a shape weight of the body or a garment renderer changed since Open, other than a shape with a fit
        /// record (the body's in any garment's baseWeights, a garment's in its own garmentWeights): the snapshot never shows its live
        /// weight, and Save and Blendshape Sync move it.
        /// </summary>
        public bool PoseChanged() =>
            pose.Any(p => p.Bone == null || SkinSpace.Differs(p.Matrix, p.Bone.localToWorldMatrix, PoseTolerance))
            || liveWeights.Any(w => WeightsChanged(w.Renderer, w.Mesh, w.Weights, w.Recorded()));

        /// <summary>The body's weight W₀ of a shape in the snapshot: the weight the garments were fitted at, else the renderer's at Open.</summary>
        public float BodyWeight(string name)
        {
            var index = Surface.Mesh.GetBlendShapeIndex(name);
            return index < 0 ? 0f : Surface.Weights[index];
        }

        /// <summary>
        /// Every garment's input fingerprint in layer order: the pipeline version, every settings field, the ticked shapes with
        /// Overwrite (body order, separators left out) and their edits, the rigid marks, the fit records,
        /// the per-read hashes of the body and the garment, its bone map, and the fingerprints of the lower garments it collides with.
        /// Cheap: the window recombines it on every Layout event.
        /// </summary>
        public ulong[] Fingerprints()
        {
            var result = new ulong[Garments.Count];
            var mesh = Surface.Mesh;
            for (var k = 0; k < Garments.Count; k++)
            {
                var g = Garments[k];
                var data = g.Data;
                var h = Fnv.New();
                h.Add(InputFingerprint.PipelineVersion);
                foreach (var w in Presets.Words(data.settings)) h.Add(w);
                for (var s = 0; s < mesh.blendShapeCount; s++)
                {
                    var name = mesh.GetBlendShapeName(s);
                    var entry = data.Entry(name);
                    if (entry == null || !entry.enabled || ShapeGroups.IsSeparator(name)) continue;
                    h.Add(name);
                    h.Add(entry.overwrite);
                    h.Add(entry.frames.Count);
                    foreach (var edit in entry.frames) InputFingerprint.Add(ref h, edit);
                }
                // counts and negative markers keep the variable-length parts apart
                h.Add(-5);
                h.Add(data.rigidIslandVertices.Count);
                foreach (var v in data.rigidIslandVertices.OrderBy(v => v)) h.Add(v);
                foreach (var b in data.baseWeights.OrderBy(b => b.name, StringComparer.Ordinal))
                {
                    h.Add(b.name);
                    h.Add(b.weight);
                }
                h.Add(-2);
                foreach (var b in data.garmentWeights.OrderBy(b => b.name, StringComparer.Ordinal))
                {
                    h.Add(b.name);
                    h.Add(b.weight);
                }
                h.Add(-3);
                h.Add(BodyContent);
                h.Add(BodyState);
                h.Add(g.Content);
                h.Add(g.State);
                // BoneMap breaks distance ties by bone name, which no other part hashes
                h.Add(g.Snapshot.BoneToBody.Length);
                foreach (var b in g.Snapshot.BoneToBody) h.Add(b);
                // only the lower garments it collides with matter to the output
                var colliding = Enumerable.Range(0, k).Where(j => Collides(k, j)).ToList();
                h.Add(colliding.Count);
                foreach (var j in colliding) h.Add(result[j]);
                result[k] = h.Value;
            }
            return result;
        }

        /// <summary>Sets the layer order to the given renderers' order (Undo records the layer indices).</summary>
        public void Reorder(IReadOnlyList<SkinnedMeshRenderer> renderers)
        {
            var ordered = renderers.Select(r => Garments.First(g => g.Renderer == r)).ToList();
            Undo.RecordObjects(Garments.Select(g => g.Data).ToArray(), Localization.Get("undo.reorder"));
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].Data.layerIndex = i;
                EditorUtility.SetDirty(ordered[i].Data);
            }
            FollowLayerIndices();
        }

        /// <summary>
        /// Puts the garments in the order of their layer indices, which Reorder, Undo and Redo set; results are dropped because
        /// colliders change. True when the order changed.
        /// </summary>
        public bool FollowLayerIndices()
        {
            var ordered = Garments.OrderBy(g => g.Data.layerIndex).ToList();
            if (ordered.SequenceEqual(Garments)) return false;
            Garments.Clear();
            Garments.AddRange(ordered);
            foreach (var g in Garments) g.Results.Clear();
            return true;
        }

        /// <summary>Transfer of the garments that are not up to date; true when the run completed.</summary>
        public bool Transfer(Func<string, float, bool> cancelled) => Transfer(false, cancelled).Completed;

        /// <summary>
        /// Computes and writes, in layer order, every garment that is not up to date (every garment with all). The lower garments one
        /// collides with are computed in memory first when their results are not current. Cancel or an exception stops the run: the
        /// garments written before it stay written, the interrupted one keeps its previous report, fit records, and results
        /// while still current (if the output state write itself fails, it is left stale with its records added).
        /// </summary>
        public TransferRun Transfer(bool all, Func<string, float, bool> cancelled)
        {
            var run = new TransferRun();
            var current = -1;
            var asCollider = false;
            try
            {
                for (var k = 0; k < Garments.Count; k++)
                {
                    current = k;
                    asCollider = false;
                    // after the lower garments this run wrote: their first records may have made this one stale
                    var f = Fingerprints();
                    // results computed under other inputs count as none
                    DropStale(f);
                    if (!all && IsUpToDate(Garments[k], f[k]))
                    {
                        run.Skipped.Add(Garments[k]);
                        continue;
                    }
                    var needed = new bool[k];
                    for (var j = k - 1; j >= 0; j--)
                        needed[j] = Collides(k, j) || Enumerable.Range(j + 1, k - j - 1).Any(m => needed[m] && Collides(m, j));
                    for (var j = 0; j < k; j++)
                    {
                        if (!needed[j] || HasCurrentResults(Garments[j], f[j])) continue;
                        current = j;
                        asCollider = true;
                        if (!Compute(j, k, collider: f[j], cancelled)) return Cancel(run);
                    }
                    current = k;
                    asCollider = false;
                    if (!Compute(k, k, collider: null, cancelled)) return Cancel(run);
                    run.Written.Add(Garments[k]);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                run.Failed = Garments[current];
                run.FailedAsCollider = asCollider;
                run.Error = e.Message;
            }
            return run;
        }

        private static TransferRun Cancel(TransferRun run)
        {
            run.Cancelled = true;
            return run;
        }

        /// <summary>
        /// Computes garment k's ticked shapes for garment step, whose share of the progress bar it shows. Given collider (its current
        /// fingerprint), it keeps the results in memory only, recording nothing; otherwise it writes the output, records the fit
        /// weights (Undo), then writes its output state. Its results and report change only once all that succeeded; false
        /// when cancelled.
        /// </summary>
        private bool Compute(int k, int step, ulong? collider, Func<string, float, bool> cancelled)
        {
            var g = Garments[k];
            var (rigid, attached) = Prepare(g);
            var report = new GarmentReport();
            var ticked = Ticked(g);
            // a kept vendor shape is not computed, so outer layers clip against it as the garment shows it in game
            foreach (var e in ticked.Where(e => !Written(g, e))) report.Skipped.Add(new SkippedShape { name = e.name, reason = Msg.SkipKept });
            var names = ticked.Where(e => Written(g, e)).Select(e => e.name).ToList();
            var results = new Dictionary<string, ShapeResult>();
            for (var i = 0; i < names.Count; i++)
            {
                if (cancelled != null && cancelled($"{g.Renderer.name}: {names[i]}", (step + (float)i / Mathf.Max(1, names.Count)) / Garments.Count)) return false;
                results[names[i]] = ComputeShape(k, names[i], rigid, attached, ref report.Clip);
            }
            var fingerprint = collider ?? Write(k, results, report, ticked);
            g.Results.Clear();
            foreach (var kv in results)
            {
                kv.Value.Fingerprint = fingerprint;
                g.Results[kv.Key] = kv.Value;
            }
            return true;
        }

        /// <summary>
        /// What garment g's shapes are computed with, rebuilt only when its inputs changed: its reach again when falloffZero changed, its
        /// correspondence for its settings, its rigid islands and the islands that ride on the cloth. Transfer and the Edit preview share it.
        /// </summary>
        private (HashSet<int> Rigid, Attachments Attached) Prepare(GarmentState g)
        {
            // the clamped settings, which the fingerprint hashes: a NaN rigid size must compute as the default it hashes as
            var settings = g.Data.settings.Clamped();
            if (g.ReachFalloffZero != FalloffZero(settings)) ComputeReach(g);
            var json = JsonUtility.ToJson(settings);
            if (g.Correspondence == null || g.CorrespondenceSettings != json)
            {
                g.Correspondence = new Correspondence(Surface, g.Snapshot, settings);
                g.CorrespondenceSettings = json;
            }
            var key = json + "|" + string.Join(",", g.Data.rigidIslandVertices);
            if (g.PreparedFor != key)
            {
                g.Rigid = DeltaOps.RigidIslands(g.Snapshot.Topology, g.Snapshot.Positions, settings.rigidIslandSize, g.Data.rigidIslandVertices);
                g.Attached = new Attachments(g.Snapshot.Topology, g.Snapshot.Positions, g.Rigid, g.Correspondence.Distance);
                g.PreparedFor = key;
            }
            return (g.Rigid, g.Attached);
        }

        /// <summary>Garment g computes and writes the body shape: it ticks it and either lacks a shape of that name or overwrites its own.</summary>
        internal bool WritesShape(GarmentState g, string name) => Ticked(g).Any(e => e.name == name && Written(g, e));

        /// <summary>Every shape garment g writes has a result computed under this fingerprint, so its results can stand in as a collider.</summary>
        internal bool HasCurrentResults(GarmentState g, ulong fingerprint) =>
            Ticked(g).Where(e => Written(g, e)).All(e => g.Results.TryGetValue(e.name, out var r) && r.Fingerprint == fingerprint);

        /// <summary>Drops every result computed under a fingerprint that is no longer its garment's: such a result counts as none.</summary>
        internal void DropStale(IReadOnlyList<ulong> fingerprints)
        {
            for (var k = 0; k < Garments.Count; k++)
            {
                var results = Garments[k].Results;
                foreach (var name in results.Where(kv => kv.Value.Fingerprint != fingerprints[k]).Select(kv => kv.Key).ToList()) results.Remove(name);
            }
        }

        /// <summary>
        /// Gives every garment that writes the body shape a result computed under its current inputs, in layer order, so a lower garment's
        /// result is ready before one above clips against it. Records and writes nothing (no fit records, no output, no shape entry).
        /// progress hears of each garment it computes when more than one correspondence has to be built.
        /// </summary>
        internal void EnsureShape(string name, Action<string, float> progress = null)
        {
            var f = Fingerprints();
            DropStale(f);
            var todo = Enumerable.Range(0, Garments.Count).Where(k => WritesShape(Garments[k], name) && !Garments[k].Results.ContainsKey(name)).ToList();
            // the correspondences are the slow part: progress only when more than one has to be built
            var builds = todo.Count(k => Garments[k].Correspondence == null || Garments[k].CorrespondenceSettings != JsonUtility.ToJson(Garments[k].Data.settings.Clamped()));
            for (var i = 0; i < todo.Count; i++)
            {
                var g = Garments[todo[i]];
                if (builds > 1) progress?.Invoke(g.Renderer.name, (float)i / todo.Count);
                var (rigid, attached) = Prepare(g);
                var clip = new ClipStats();
                var result = ComputeShape(todo[i], name, rigid, attached, ref clip);
                result.Fingerprint = f[todo[i]];
                g.Results[name] = result;
            }
        }

        /// <summary>Its output asset and output state exist and the state was written with this fingerprint.</summary>
        public bool IsUpToDate(GarmentState g, ulong fingerprint)
        {
            if (!AssetDatabase.Contains(g.Data)) return false;
            var path = AssetDatabase.GetAssetPath(g.Data);
            var state = OutputWriter.StateAt(path);
            return state != null && state.fingerprint == InputFingerprint.Hex(fingerprint) && OutputWriter.OutputAt(path) != null;
        }

        public bool AllUpToDate()
        {
            var fingerprints = Fingerprints();
            return Garments.Select((g, k) => IsUpToDate(g, fingerprints[k])).All(x => x);
        }

        /// <summary>
        /// Assigns every up-to-date garment whose output wrote shapes (the others keep their mesh) and, when asked, binds the written shapes
        /// and the ticked ones a garment keeps to the body with MA Blendshape Sync. Outputs and written shapes come from disk (the output
        /// file and its state), so Save works after a new read as after a Transfer.
        /// </summary>
        public void Save(bool sync)
        {
            var fingerprints = Fingerprints();
            for (var k = 0; k < Garments.Count; k++)
            {
                var g = Garments[k];
                if (!IsUpToDate(g, fingerprints[k])) continue;
                var path = AssetDatabase.GetAssetPath(g.Data);
                var output = OutputWriter.OutputAt(path);
                var written = OutputWriter.StateAt(path)?.report.Written;
                if (output == null || written == null) continue;
                if (written.Count > 0) OutputWriter.Assign(g.Renderer, output, Body, written);
                // outer layers were fitted assuming a kept shape follows the body
                if (sync) OutputWriter.Sync(g.Renderer, Body, written.Concat(Kept(g)), g.Data);
            }
            AssetDatabase.SaveAssets();
        }

        /// <summary>Forgets the weights the garment was fitted at (the body's and its overwritten shapes'), so the next Open snapshots the current ones.</summary>
        public void Refit(GarmentState g)
        {
            Undo.RecordObject(g.Data, Localization.Get("undo.refit"));
            g.Data.baseWeights.Clear();
            g.Data.garmentWeights.Clear();
            EditorUtility.SetDirty(g.Data);
        }

        /// <summary>The garment's data is on disk and the renderer shows a generated mesh or has Sync bindings this tool added (a garment that keeps its mesh gets bindings for its kept shapes).</summary>
        public bool CanRestore(GarmentState g) =>
            AssetDatabase.Contains(g.Data) && (g.Renderer.sharedMesh != g.Data.sourceMesh || g.Data.addedBindings.Count > 0);

        public void Restore(GarmentState g)
        {
            OutputWriter.Restore(g.Renderer, Body, g.Data);
            AssetDatabase.SaveAssets();
        }

        /// <summary>Body shapes whose weight now differs from the one the garment was fitted at; information only, the transfer keeps the fitted one.</summary>
        public IEnumerable<Message> BaseWeightInfo(GarmentState g)
        {
            foreach (var b in g.Data.baseWeights)
            {
                var shape = Surface.Mesh.GetBlendShapeIndex(b.name);
                if (shape < 0) continue;
                var now = Body.GetBlendShapeWeight(shape);
                if (Mathf.Abs(now - b.weight) < WeightTolerance) continue;
                yield return new Message(Msg.FitWeight, b.name, b.weight, now);
            }
        }

        internal ShapeResult ComputeShape(int k, string name, HashSet<int> rigid, Attachments attached, ref ClipStats clip)
        {
            var g = Garments[k];
            var snapshot = g.Snapshot;
            var shape = Surface.Mesh.GetBlendShapeIndex(name);
            var weights = ShapeMath.FrameWeights(Surface.Mesh, shape);
            var frames = weights.Length;
            var entry = g.Data.Entry(name);
            var result = new ShapeResult
            {
                Name = name, FrameWeights = weights, BaseWeight = Surface.Weights[shape],
                AutoWorld = new Vector3[frames][], FinalMesh = new Vector3[frames][], FinalWorld = new Vector3[frames][], NormalMesh = new Vector3[frames][],
            };
            var band = Mathf.Max(AntiClip.MinBand, g.Reach.TryGetValue(name, out var reach) ? reach : 0f);

            // the snapshot body already shows the shape at W₀: its motion from there to each frame weight, and to weight 0 (the base state)
            var bodyDeltas = new Vector3[frames][];
            for (var f = 0; f < frames; f++) bodyDeltas[f] = Surface.WorldDelta(shape, f);
            Vector3[] bodyBase = null;
            if (result.BaseWeight != 0f)
            {
                var atSnapshot = new Vector3[Surface.Positions.Length];
                ShapeMath.DeltaAt(weights, bodyDeltas, result.BaseWeight, atSnapshot);
                for (var f = 0; f < frames; f++) bodyDeltas[f] = DeltaOps.Add(bodyDeltas[f], atSnapshot, -1f);
                bodyBase = DeltaOps.Add(new Vector3[atSnapshot.Length], atSnapshot, -1f);
            }
            var layers = Layers(k, name, -1, bodyBase);
            var rest = snapshot.MeshPositions;
            if (bodyBase != null)
            {
                result.BaseMesh = MeshSpace.ToMesh(snapshot.Skin, TransferPipeline.AutoWorld(g.Correspondence, Surface, snapshot, bodyBase, rigid, layers, band, ref clip, attached: attached, self: g.Collider, skin: g.SkinCandidates));
                result.BaseNormals = MeshSpace.NormalDeltas(snapshot.Topology, rest, result.BaseMesh);
                rest = DeltaOps.Add(rest, result.BaseMesh, 1f);
            }
            result.BaseWorld = result.BaseMesh != null ? MeshSpace.ToWorld(snapshot.Skin, result.BaseMesh) : new Vector3[rest.Length];

            for (var f = 0; f < frames; f++)
            {
                var previous = layers;
                var previousGarment = f > 0 ? result.FinalWorld[f - 1] : result.BaseWorld;
                layers = Layers(k, name, f, bodyDeltas[f]);
                var inside = Enumerable.Repeat(-1, layers.Count).ToArray();
                result.AutoWorld[f] = TransferPipeline.AutoWorld(g.Correspondence, Surface, snapshot, bodyDeltas[f], rigid, layers, band, ref clip, inside, attached, g.Collider, g.SkinCandidates);
                var auto = MeshSpace.ToMesh(snapshot.Skin, result.AutoWorld[f]);
                var edit = entry?.Edit(f);
                result.FinalMesh[f] = MeshSpace.ApplyEdit(result.BaseMesh != null ? DeltaOps.Add(auto, result.BaseMesh, -1f) : auto, edit);
                var frame = MeshSpace.ToWorld(snapshot.Skin, result.FinalMesh[f]);
                result.FinalWorld[f] = DeltaOps.Add(result.BaseWorld, frame, 1f);
                result.NormalMesh[f] = MeshSpace.NormalDeltas(snapshot.Topology, rest, result.FinalMesh[f]);
                foreach (var d in frame) result.MaxMove = Mathf.Max(result.MaxMove, d.magnitude);

                // checked, not corrected: halfway from the base state to the first frame, or between consecutive frames
                var midGarment = Mid(previousGarment, result.FinalWorld[f]);
                // anti-clip counted its result, which is the frame written unless a hand edit or a singular vertex (kept at 0) changes it
                var counted = edit == null && snapshot.Skin.SingularCount == 0;
                for (var i = 0; i < layers.Count; i++)
                {
                    var layer = layers[i];
                    var recount = !counted || inside[i] < 0;
                    var still = recount ? AntiClip.CountInside(snapshot.Positions, result.FinalWorld[f], layer.Collider, layer.Candidates, layer.Delta, band) : inside[i];
                    var midDelta = Mid(previous[i].Delta, layer.Delta);
                    clip.Intermediate += AntiClip.CountInside(snapshot.Positions, midGarment, layer.Collider, layer.Candidates, midDelta, band);
                    if (!layer.IsBody)
                    {
                        clip.StillLayer += still;
                        continue;
                    }
                    // skin the fabric lets through between its vertices, which the vertex counts cannot see; anti-clip's count holds it
                    if (recount) still += AntiClip.SkinOutside(Surface.Positions, layer.Delta, g.Collider, g.SkinCandidates, result.FinalWorld[f], band).Count;
                    clip.StillSkin += still;
                    clip.Intermediate += AntiClip.SkinOutside(Surface.Positions, midDelta, g.Collider, g.SkinCandidates, midGarment, band).Count;
                }
            }
            return result;
        }

        private GarmentState Load(SkinnedMeshRenderer renderer, TransferData data, List<Message> errors)
        {
            var path = AvatarPaths.RelativePath(Avatar, renderer.transform);
            var state = new GarmentState { Renderer = renderer };
            if (data == null)
            {
                data = ScriptableObject.CreateInstance<TransferData>();
                data.name = "Transfer Data";
                data.sourceMesh = renderer.sharedMesh;
            }
            if (data.sourceMesh == null)
            {
                errors.Add(new Message(Msg.LostSource, renderer.name));
                return null;
            }
            var before = errors.Count;
            CheckReadable(data.sourceMesh, errors);
            if (errors.Count > before) return null;
            var hash = SourceHash.Of(data.sourceMesh);
            if (data.sourceHash.Length > 0 && data.sourceHash != hash && (data.shapes.Any(e => e.frames.Any(f => f != null)) || data.rigidIslandVertices.Count > 0))
            {
                foreach (var e in data.shapes) e.frames.Clear();
                data.rigidIslandVertices.Clear();
                state.Warnings.Add(new Message(Msg.EditsDropped));
            }
            data.sourceHash = hash;
            data.rendererPath = path;
            state.Data = data;

            state.Snapshot = new GarmentSnapshot(renderer, data.sourceMesh, Surface, data.garmentWeights);
            state.Content = InputFingerprint.MeshContent(data.sourceMesh);
            state.State = InputFingerprint.SnapshotState(Avatar, renderer, data.sourceMesh, state.Snapshot.Weights);
            if (state.Snapshot.Skin.SingularCount > 0) state.Warnings.Add(new Message(Msg.Singular, state.Snapshot.Skin.SingularCount));
            if (state.Snapshot.UnmatchedBones.Count > 0) state.Warnings.Add(new Message(Msg.UnmatchedBones, string.Join(", ", state.Snapshot.UnmatchedBones)));
            var n = state.Snapshot.Positions.Length;
            state.Closest = new SurfacePoint[n];
            state.Distance = new float[n];
            for (var v = 0; v < n; v++)
                state.Distance[v] = Surface.Query.Closest(state.Snapshot.Positions[v], 1f, null, out state.Closest[v]) ? state.Closest[v].Distance : float.PositiveInfinity;
            state.Collider = new ColliderSurface(state.Snapshot.Topology, state.Snapshot.Positions, Surface);
            state.BodyCandidates = new ClipCandidates(state.Snapshot.Positions, BodyCollider);
            state.SkinCandidates = new SkinCandidates(Surface.Positions, state.Collider);
            ComputeReach(state);
            var stored = AssetDatabase.Contains(data) ? OutputWriter.StateAt(AssetDatabase.GetAssetPath(data)) : null;
            // default ticks only before the first output: a new tick would make a read alone turn an up-to-date garment stale
            if (stored == null)
                foreach (var kv in state.Reach)
                    if (kv.Value > ReachThreshold && data.Entry(kv.Key) == null && !ShapeGroups.IsSeparator(kv.Key)) data.GetOrAddEntry(kv.Key);
            // a garment skipped as up to date later still has its report, from disk
            state.Report = stored?.report;
            return state;
        }

        /// <summary>
        /// Per body shape, the largest skin motion under the garment within falloffZero: |D(w) − D(W₀)| over w = 0 and every frame
        /// weight, at the closest skin point of its vertices and at the body vertices under it. D is piecewise linear in w, so those
        /// weights hold the maximum. It sets the shape list, the default ticks, the fit records and the anti-clip band.
        /// </summary>
        private void ComputeReach(GarmentState state)
        {
            var mesh = Surface.Mesh;
            var zero = FalloffZero(state.Data.settings);
            var near = Enumerable.Range(0, state.Distance.Length).Where(v => state.Distance[v] <= zero).ToArray();
            // the skin under the garment, so a shape smaller than its edges still reaches it
            var under = Enumerable.Range(0, state.SkinCandidates.Vertices.Length).Where(k => state.SkinCandidates.RestGap[k] <= zero)
                .Select(k => state.SkinCandidates.Vertices[k]).ToArray();
            var count = mesh.vertexCount;
            var atSnapshot = new Vector3[count];
            var buffers = new List<Vector3[]>();
            for (var shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                var weights = ShapeMath.FrameWeights(mesh, shape);
                while (buffers.Count < weights.Length) buffers.Add(new Vector3[count]);
                var frames = buffers.Take(weights.Length).ToArray();
                for (var f = 0; f < frames.Length; f++) Surface.WorldDelta(shape, f, frames[f]);
                ShapeMath.DeltaAt(weights, frames, Surface.Weights[shape], atSnapshot);
                var max = 0f;
                foreach (var v in near)
                {
                    var p = state.Closest[v];
                    var atBase = Surface.At(p, atSnapshot);
                    // weight 0: the skin moves by −D(W₀)
                    max = Mathf.Max(max, atBase.magnitude);
                    foreach (var frame in frames) max = Mathf.Max(max, (Surface.At(p, frame) - atBase).magnitude);
                }
                foreach (var b in under)
                {
                    max = Mathf.Max(max, atSnapshot[b].magnitude);
                    foreach (var frame in frames) max = Mathf.Max(max, (frame[b] - atSnapshot[b]).magnitude);
                }
                state.Reach[mesh.GetBlendShapeName(shape)] = max;
            }
            state.ReachFalloffZero = zero;
        }

        /// <summary>falloffZero as Correspondence clamps it.</summary>
        private static float FalloffZero(TransferSettings s) => s.Clamped().falloffZero;

        private void OrderLayers()
        {
            var indices = Garments.Select(g => g.Data.layerIndex).ToList();
            var ordered = indices.All(i => i >= 0) && indices.Distinct().Count() == indices.Count
                ? Garments.OrderBy(g => g.Data.layerIndex).ToList()
                : Garments.OrderBy(MedianOverlapDistance).ToList();
            Garments.Clear();
            Garments.AddRange(ordered);
            for (var i = 0; i < Garments.Count; i++) Garments[i].Data.layerIndex = i;
        }

        /// <summary>Median distance to the skin of a garment's vertices within 1 cm of another garment (all its vertices if none).</summary>
        private float MedianOverlapDistance(GarmentState state)
        {
            var overlapping = new List<float>();
            var near = new List<int>();
            foreach (var other in Garments.Where(o => o != state))
            {
                var grid = new Geometry.SpatialGrid(other.Snapshot.Positions, 0.01f);
                for (var v = 0; v < state.Distance.Length; v++)
                {
                    grid.Query(state.Snapshot.Positions[v], 0.01f, near);
                    if (near.Count > 0 && !float.IsInfinity(state.Distance[v])) overlapping.Add(state.Distance[v]);
                }
            }
            var values = overlapping.Count > 0 ? overlapping : state.Distance.Where(d => !float.IsInfinity(d)).ToList();
            if (values.Count == 0) return float.MaxValue;
            values.Sort();
            return values[values.Count / 2];
        }

        /// <summary>The colliders of garment k at a frame of a body shape, or at the base state (body weight 0) for frame -1; deltas relative to the snapshot.</summary>
        internal List<AntiClip.Layer> Layers(int k, string name, int frame, Vector3[] bodyDelta)
        {
            var g = Garments[k];
            var layers = new List<AntiClip.Layer> { new AntiClip.Layer { Collider = BodyCollider, Candidates = g.BodyCandidates, Delta = bodyDelta, IsBody = true } };
            for (var j = 0; j < k; j++)
                if (Collides(k, j))
                    layers.Add(new AntiClip.Layer { Collider = Garments[j].Collider, Candidates = CandidatesFor(g, Garments[j]), Delta = InnerDelta(Garments[j], name, frame) });
            return layers;
        }

        /// <summary>The colliders of garment k with the body shape at weight w, deltas relative to the snapshot: Layers at any weight.</summary>
        private List<AntiClip.Layer> LayersAt(int k, string name, float w)
        {
            var g = Garments[k];
            var layers = new List<AntiClip.Layer> { new AntiClip.Layer { Collider = BodyCollider, Candidates = g.BodyCandidates, Delta = BodyOffset(name, w), IsBody = true } };
            for (var j = 0; j < k; j++)
                if (Collides(k, j))
                    layers.Add(new AntiClip.Layer { Collider = Garments[j].Collider, Candidates = CandidatesFor(g, Garments[j]), Delta = GarmentOffset(Garments[j], name, w) });
            return layers;
        }

        private static ClipCandidates CandidatesFor(GarmentState g, GarmentState inner)
        {
            if (!g.LayerCandidates.TryGetValue(inner, out var candidates))
                g.LayerCandidates[inner] = candidates = new ClipCandidates(g.Snapshot.Positions, inner.Collider);
            return candidates;
        }

        /// <summary>The body's world offset from the snapshot with one shape at weight w and the others as in the snapshot, frames interpolated as Unity does.</summary>
        internal Vector3[] BodyOffset(string name, float w)
        {
            var shape = Surface.Mesh.GetBlendShapeIndex(name);
            var weights = ShapeMath.FrameWeights(Surface.Mesh, shape);
            var frames = Enumerable.Range(0, weights.Length).Select(f => Surface.WorldDelta(shape, f)).ToArray();
            var at = new Vector3[Surface.Positions.Length];
            var atSnapshot = new Vector3[at.Length];
            ShapeMath.DeltaAt(weights, frames, w, at);
            ShapeMath.DeltaAt(weights, frames, Surface.Weights[shape], atSnapshot);
            return DeltaOps.Add(at, atSnapshot, -1f);
        }

        /// <summary>The body's normal change in mesh space with one shape at weight w instead of its snapshot weight.</summary>
        internal Vector3[] BodyNormalOffset(string name, float w)
        {
            var shape = Surface.Mesh.GetBlendShapeIndex(name);
            return DeltaOps.Add(ShapeMath.DeltaAt(Surface.Mesh, shape, w, normals: true), ShapeMath.DeltaAt(Surface.Mesh, shape, Surface.Weights[shape], normals: true), -1f);
        }

        /// <summary>
        /// Garment g's world offset from the snapshot with the body shape at weight w: its result (the base state plus its frames as Unity
        /// interpolates them), else its own shape of that name moved from its snapshot weight to w, else null (it does not move). Stale
        /// results must have been dropped first (EnsureShape, DropStale).
        /// </summary>
        internal Vector3[] GarmentOffset(GarmentState g, string name, float w)
        {
            if (g.Results.TryGetValue(name, out var r))
            {
                // FinalWorld holds the base; its frames over the base interpolate as the written frames do
                var frames = r.FinalWorld.Select(f => DeltaOps.Add(f, r.BaseWorld, -1f)).ToArray();
                var at = new Vector3[r.BaseWorld.Length];
                ShapeMath.DeltaAt(r.FrameWeights, frames, w, at);
                return DeltaOps.Add(at, r.BaseWorld, 1f);
            }
            var mesh = g.Snapshot.Source;
            var own = mesh.GetBlendShapeIndex(name);
            if (own < 0) return null;
            return MeshSpace.ToWorld(g.Snapshot.Skin, DeltaOps.Add(ShapeMath.DeltaAt(mesh, own, w), ShapeMath.DeltaAt(mesh, own, g.Snapshot.Weights[own]), -1f));
        }

        /// <summary>Garment g's normal change in mesh space at body weight w, matching GarmentOffset; null when it does not move.</summary>
        internal Vector3[] GarmentNormalOffset(GarmentState g, string name, float w)
        {
            if (g.Results.TryGetValue(name, out var r))
            {
                var at = new Vector3[r.BaseWorld.Length];
                ShapeMath.DeltaAt(r.FrameWeights, r.NormalMesh, w, at);
                return r.BaseNormals == null ? at : DeltaOps.Add(at, r.BaseNormals, 1f);
            }
            var mesh = g.Snapshot.Source;
            var own = mesh.GetBlendShapeIndex(name);
            if (own < 0) return null;
            return DeltaOps.Add(ShapeMath.DeltaAt(mesh, own, w, normals: true), ShapeMath.DeltaAt(mesh, own, g.Snapshot.Weights[own], normals: true), -1f);
        }

        /// <summary>
        /// Garment k's vertices inside the skin and inside a lower garment it collides with, and the body vertices the shape pushes out
        /// through it, with the body shape at weight w: the check that counts "still clipping" for the report, at any weight. Stale
        /// results must have been dropped first.
        /// </summary>
        internal (List<int> Skin, List<int> Layer, List<int> Through) ClippingAt(int k, string name, float w)
        {
            // ponytail: each garment's call moves the body, its lower garments and its own sheet again; build them once per shape and weight if Show clipping lags on release
            var g = Garments[k];
            var band = Mathf.Max(AntiClip.MinBand, g.Reach.TryGetValue(name, out var reach) ? reach : 0f);
            var offset = GarmentOffset(g, name, w) ?? new Vector3[g.Snapshot.Positions.Length];
            var skin = new List<int>();
            var layer = new HashSet<int>();
            var layers = LayersAt(k, name, w);
            foreach (var l in layers)
            {
                var inside = AntiClip.Inside(g.Snapshot.Positions, offset, l.Collider, l.Candidates, l.Delta, band);
                if (l.IsBody) skin.AddRange(inside);
                else layer.UnionWith(inside);
            }
            // LayersAt puts the body first
            var through = AntiClip.SkinOutside(Surface.Positions, layers[0].Delta, g.Collider, g.SkinCandidates, offset, band);
            return (skin, layer.OrderBy(v => v).ToList(), through);
        }

        /// <summary>Whether garment k collides with the lower garment j: not when either lists the other as never worn with it.</summary>
        internal bool Collides(int k, int j) =>
            !Garments[k].Data.notWornWith.Contains(Garments[j].Data.rendererPath) && !Garments[j].Data.notWornWith.Contains(Garments[k].Data.rendererPath);

        /// <summary>
        /// World delta of an inner layer relative to its snapshot at a body shape frame (-1: the base state, body weight 0): its
        /// transferred response, else its own shape of that name moved from its snapshot weight to the body's weight, else none.
        /// A result found here is current: Transfer and EnsureShape drop stale ones first.
        /// </summary>
        private Vector3[] InnerDelta(GarmentState inner, string name, int frame)
        {
            if (inner.Results.TryGetValue(name, out var result)) return frame < 0 ? result.BaseWorld : frame < result.FinalWorld.Length ? result.FinalWorld[frame] : null;
            var mesh = inner.Snapshot.Source;
            var own = mesh.GetBlendShapeIndex(name);
            if (own < 0) return null;
            var delta = ShapeMath.DeltaAt(mesh, own, frame < 0 ? 0f : Surface.Mesh.GetBlendShapeFrameWeight(Surface.Mesh.GetBlendShapeIndex(name), frame));
            var atSnapshot = ShapeMath.DeltaAt(mesh, own, inner.Snapshot.Weights[own]);
            return MeshSpace.ToWorld(inner.Snapshot.Skin, DeltaOps.Add(delta, atSnapshot, -1f));
        }

        private int CountWeightMismatch(GarmentState g, ICollection<ShapeResult> results)
        {
            var snapshot = g.Snapshot;
            var count = 0;
            var diff = new Dictionary<int, float>();
            for (var v = 0; v < snapshot.Positions.Length; v++)
            {
                if (float.IsInfinity(g.Distance[v])) continue;
                if (!results.Any(r => r.FinalWorld.Any(f => (f[v] - r.BaseWorld[v]).sqrMagnitude > 1e-6f))) continue;
                diff.Clear();
                foreach (var (bone, weight) in snapshot.Bones.All[v])
                {
                    // garment bones without a body bone get keys of their own, so their weight counts as different
                    var key = bone < snapshot.BoneToBody.Length && snapshot.BoneToBody[bone] >= 0 ? snapshot.BoneToBody[bone] : -1 - bone;
                    diff[key] = (diff.TryGetValue(key, out var x) ? x : 0f) + weight;
                }
                foreach (var (bone, weight) in Surface.Bones.All[g.Closest[v].Corner])
                    diff[bone] = (diff.TryGetValue(bone, out var x) ? x : 0f) - weight;
                if (diff.Values.Sum(Mathf.Abs) > 0.5f) count++;
            }
            return count;
        }

        /// <summary>Writes garment k's output, its fit records and its output state, then sets its report; the fingerprint it wrote.</summary>
        private ulong Write(int k, Dictionary<string, ShapeResult> results, GarmentReport report, List<ShapeEntry> ticked)
        {
            var g = Garments[k];
            report.Fallback = g.Correspondence.FallbackCount;
            report.Singular = g.Snapshot.Skin.SingularCount;
            report.WeightMismatch = CountWeightMismatch(g, results.Values);
            var shapes = new List<OutputWriter.WrittenShape>();
            foreach (var r in results.Values.OrderBy(r => Surface.Mesh.GetBlendShapeIndex(r.Name)))
            {
                if (r.MaxMove < NoEffect)
                {
                    report.Skipped.Add(new SkippedShape { name = r.Name, reason = Msg.SkipNoReach });
                    continue;
                }
                var baseDelta = r.BaseMesh;
                var baseNormals = r.BaseNormals;
                // the snapshot showed the replaced vendor shape at the garment's weight, so the base keeps it
                var own = g.Snapshot.Source.GetBlendShapeIndex(r.Name);
                if (own >= 0 && g.Snapshot.Weights[own] != 0f)
                {
                    var n = g.Snapshot.Source.vertexCount;
                    baseDelta = DeltaOps.Add(baseDelta ?? new Vector3[n], ShapeMath.DeltaAt(g.Snapshot.Source, own, g.Snapshot.Weights[own]), 1f);
                    baseNormals = DeltaOps.Add(baseNormals ?? new Vector3[n], ShapeMath.DeltaAt(g.Snapshot.Source, own, g.Snapshot.Weights[own], normals: true), 1f);
                }
                shapes.Add(new OutputWriter.WrittenShape { Name = r.Name, Weights = r.FrameWeights, Deltas = r.FinalMesh, Normals = r.NormalMesh, BaseDelta = baseDelta, BaseNormals = baseNormals });
                report.Written.Add(r.Name);
                report.MaxMove = Mathf.Max(report.MaxMove, r.MaxMove);
                if (r.BaseWeight != 0f) report.BaseOffset.Add(new BaseWeight { name = r.Name, weight = r.BaseWeight });
            }
            var mesh = OutputWriter.BuildMesh(g.Snapshot.Source, shapes);
            var path = AssetDatabase.Contains(g.Data) ? AssetDatabase.GetAssetPath(g.Data) : OutputWriter.OutputPath(Avatar.name, g.Data.rendererPath, g.Snapshot.Source);
            OutputWriter.Write(mesh, path, g.Data);

            Undo.RecordObject(g.Data, Localization.Get("undo.transfer"));
            // the source fits the body as it was at the first transfer; a record is never replaced, the snapshot already showed it
            foreach (var kv in g.Reach)
                if (kv.Value > ReachThreshold && !ShapeGroups.IsSeparator(kv.Key) && !g.Data.baseWeights.Exists(b => b.name == kv.Key))
                    g.Data.baseWeights.Add(new BaseWeight { name = kv.Key, weight = BodyWeight(kv.Key) });
            // a shape of the garment's own under a ticked name fits at its weight at the first transfer: Save sets an overwritten one to
            // the body's weight and Sync binds a kept one to it
            foreach (var e in ticked)
            {
                var own = g.Snapshot.Source.GetBlendShapeIndex(e.name);
                if (own >= 0 && !g.Data.garmentWeights.Exists(b => b.name == e.name))
                    g.Data.garmentWeights.Add(new BaseWeight { name = e.name, weight = g.Snapshot.Weights[own] });
            }
            // the mesh write saved the data without them: the state write's save must take them along
            EditorUtility.SetDirty(g.Data);

            // after the records this transfer added: the fingerprint the next read computes
            var fingerprint = Fingerprints()[k];
            OutputWriter.WriteState(path, InputFingerprint.Hex(fingerprint), report);
            g.Report = report;
            return fingerprint;
        }

        /// <summary>A shape the garment lacks, or one it has with Overwrite ticked.</summary>
        private static bool Written(GarmentState g, ShapeEntry e) => g.Snapshot.Source.GetBlendShapeIndex(e.name) < 0 || e.overwrite;

        /// <summary>The garment's ticked body shapes in body order, separators left out.</summary>
        private List<ShapeEntry> Ticked(GarmentState g) =>
            g.Data.shapes.Where(e => e.enabled && !ShapeGroups.IsSeparator(e.name) && Surface.Mesh.GetBlendShapeIndex(e.name) >= 0)
                .OrderBy(e => Surface.Mesh.GetBlendShapeIndex(e.name)).ToList();

        /// <summary>Ticked body shapes the garment has and keeps (Overwrite off).</summary>
        private IEnumerable<string> Kept(GarmentState g) => Ticked(g).Where(e => !Written(g, e)).Select(e => e.name);

        private static bool WeightsChanged(SkinnedMeshRenderer renderer, Mesh mesh, float[] snapshot, IEnumerable<string> recorded)
        {
            if (renderer == null) return true;
            var now = ShapeMath.WeightsOf(renderer, mesh);
            if (now.Length != snapshot.Length) return true;
            var skip = new HashSet<string>(recorded);
            for (var s = 0; s < now.Length; s++)
                if (Mathf.Abs(now[s] - snapshot[s]) > WeightTolerance && !skip.Contains(mesh.GetBlendShapeName(s))) return true;
            return false;
        }

        private static Vector3[] Mid(Vector3[] a, Vector3[] b)
        {
            if (a == null && b == null) return null;
            var length = (b ?? a).Length;
            var result = new Vector3[length];
            for (var i = 0; i < length; i++) result[i] = ((a == null ? Vector3.zero : a[i]) + (b == null ? Vector3.zero : b[i])) * 0.5f;
            return result;
        }

        private static bool InAsset(Component c) => EditorUtility.IsPersistent(c) || EditorUtility.IsPersistent(c.gameObject);

        private static void CheckReadable(Mesh mesh, List<Message> errors)
        {
            if (mesh.vertexCount > 0 && mesh.vertices.Length != mesh.vertexCount)
                errors.Add(new Message(Msg.Unreadable, mesh.name, AssetDatabase.GetAssetPath(mesh)));
        }
    }
}
