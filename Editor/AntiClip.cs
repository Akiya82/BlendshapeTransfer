using System;
using System.Collections.Generic;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>
    /// A surface the garment must stay outside of: the body, whose hits take its interpolated vertex normals, or an inner garment,
    /// whose hits take the hit triangle's face normal, each face turned once at rest to point away from the skin, so open,
    /// double-sided or reversed clothing has a defined outside that cannot flip inside a triangle. On a garment, faces standing
    /// across the skin (<see cref="RimCos"/>) and closest points on its boundary edges are ignored, and so are points beside it
    /// (<see cref="Hit"/>).
    /// </summary>
    internal sealed class ColliderSurface
    {
        /// <summary>cos 30°: a point counts for a garment collider only within this cone around the face normal at its closest point.</summary>
        public const float AboveSheet = 0.8660254f;
        /// <summary>A garment face whose |cos| to the skin normal is below this (hem, waistband, edge thickness) never collides: layers press along the skin normal, and such a face's outside is noise.</summary>
        public const float RimCos = 0.3f;
        public readonly Topology Topology;
        public readonly Vector3[] Rest;
        public readonly SurfaceQuery RestQuery;
        /// <summary>Per triangle: −1 where the face is turned to point away from the skin, 0 for a rim face the queries skip, else 1; null for the body.</summary>
        private readonly float[] faceSigns;

        /// <param name="body">The body when this collider is a garment; null for the body itself.</param>
        public ColliderSurface(Topology topology, Vector3[] rest, BodySurface body)
        {
            Topology = topology;
            Rest = rest;
            if (body == null)
            {
                RestQuery = new SurfaceQuery(rest, topology.Normals(rest), topology, rejectBoundary: false);
                return;
            }
            var t = topology.Triangles;
            faceSigns = new float[t.Length / 3];
            for (var f = 0; f < faceSigns.Length; f++)
            {
                Vector3 a = rest[t[3 * f]], b = rest[t[3 * f + 1]], c = rest[t[3 * f + 2]];
                // agree with the skin normal at the skin point closest to the centroid; the offset to that point would flip the parts
                // under the skin. Signs per vertex mix on hem triangles, where the interpolated normal flips with millimetre motion.
                // Not .normalized: the cross product is in m², and Unity zeroes vectors shorter than 1e-5. No skin within reach keeps the winding.
                var normal = Vector3.Cross(b - a, c - a);
                var length = normal.magnitude;
                var cos = !body.Query.Closest((a + b + c) / 3f, 1f, null, out var skin) ? 1f : length > 0f ? Vector3.Dot(normal, skin.Normal) / length : 0f;
                faceSigns[f] = Mathf.Abs(cos) < RimCos ? 0f : Mathf.Sign(cos);
            }
            RestQuery = new SurfaceQuery(rest, null, topology, rejectBoundary: true, faceSigns: faceSigns);
        }

        /// <summary>
        /// The collider moved by scale × delta (ponytail: rebuilds a grid per call, ~20 ms on 25k vertices, so a frame with a hand edit
        /// builds the full-weight collider twice, for anti-clip and its check; cache per delta if live recompute is too slow, seed from
        /// rest triangles if Transfer is).
        /// </summary>
        public SurfaceQuery Deformed(Vector3[] delta, float scale)
        {
            if (delta == null || scale == 0f) return RestQuery;
            var positions = DeltaOps.Add(Rest, delta, scale);
            return new SurfaceQuery(positions, faceSigns == null ? Topology.Normals(positions) : null, Topology, faceSigns != null, RestQuery.Cell, RestQuery.ShortEdge, faceSigns);
        }

        /// <summary>
        /// The closest point of query (this collider at rest or deformed) within reach. On a garment it counts only when p lies
        /// roughly along the face normal from it, |signed| ≥ <see cref="AboveSheet"/> × distance: beside an open edge the distance
        /// to the face's plane says nothing about inside or outside.
        /// </summary>
        public bool Hit(SurfaceQuery query, Vector3 p, float reach, out SurfacePoint hit) =>
            query.Closest(p, reach, null, out hit) && (faceSigns == null || Mathf.Abs(SurfaceQuery.Signed(p, hit)) >= AboveSheet * hit.Distance);
    }

    /// <summary>Garment vertices that are outside a collider at rest, with that distance d₀.</summary>
    internal sealed class ClipCandidates
    {
        public const float Cap = 0.5f;
        public readonly int[] Vertices;
        public readonly float[] RestDistance;
        /// <summary>Distance to each candidate's closest point at rest; d₀ is its part along the normal.</summary>
        public readonly float[] ClosestDistance;
        /// <summary>Longest edge of each candidate's closest triangle at rest.</summary>
        public readonly float[] TriangleEdge;

        public ClipCandidates(Vector3[] garmentRest, ColliderSurface collider)
        {
            var vertices = new List<int>();
            var distances = new List<float>();
            var closest = new List<float>();
            var edges = new List<float>();
            var t = collider.Topology.Triangles;
            for (var v = 0; v < garmentRest.Length; v++)
            {
                if (!collider.Hit(collider.RestQuery, garmentRest[v], Cap, out var hit)) continue;
                var s = SurfaceQuery.Signed(garmentRest[v], hit);
                if (s <= 0f) continue;
                vertices.Add(v);
                distances.Add(s);
                closest.Add(hit.Distance);
                Vector3 a = collider.Rest[t[3 * hit.Triangle]], b = collider.Rest[t[3 * hit.Triangle + 1]], c = collider.Rest[t[3 * hit.Triangle + 2]];
                edges.Add(Mathf.Sqrt(Mathf.Max((a - b).sqrMagnitude, (b - c).sqrMagnitude, (c - a).sqrMagnitude)));
            }
            Vertices = vertices.ToArray();
            RestDistance = distances.ToArray();
            ClosestDistance = closest.ToArray();
            TriangleEdge = edges.ToArray();
        }
    }

    /// <summary>
    /// Body vertices under a garment sheet at rest (a hit on the garment within the cone, signed distance not above 0), with their gap
    /// to it: the skin that can come through the fabric between its vertices.
    /// </summary>
    internal sealed class SkinCandidates
    {
        /// <summary>
        /// How far under the sheet skin is looked for, metres (ponytail: a skirt hanging farther over a thigh shape is not lifted;
        /// raise it if one clips).
        /// </summary>
        public const float Reach = 0.1f;
        public readonly int[] Vertices;
        public readonly float[] RestGap;

        public SkinCandidates(Vector3[] bodyRest, ColliderSurface garment)
        {
            var bounds = new Bounds(garment.Rest[0], Vector3.zero);
            foreach (var p in garment.Rest) bounds.Encapsulate(p);
            bounds.Expand(2f * Reach);
            var vertices = new List<int>();
            var gaps = new List<float>();
            for (var b = 0; b < bodyRest.Length; b++)
            {
                if (!bounds.Contains(bodyRest[b]) || !garment.Hit(garment.RestQuery, bodyRest[b], Reach, out var hit)) continue;
                var s = SurfaceQuery.Signed(bodyRest[b], hit);
                if (s > 0f) continue;
                vertices.Add(b);
                gaps.Add(-s);
            }
            Vertices = vertices.ToArray();
            RestGap = gaps.ToArray();
        }
    }

    [Serializable]
    public struct ClipStats
    {
        public int Pushed;
        public float MaxPush;
        public int StillSkin;
        public int StillLayer;
        public int Intermediate;
        /// <summary>Garment vertices still closer than their target after the last round.</summary>
        public int Unresolved;
    }

    internal static class AntiClip
    {
        public const int Rounds = 3;
        public const float MinBand = 0.02f;
        /// <summary>
        /// The fabric is kept at least this far over skin a shape moves, also where it touched the skin at rest, so coincident fabric
        /// does not show the skin through it.
        /// </summary>
        public const float SkinFloor = 0.0002f;
        private const float QueryMargin = 0.05f;
        /// <summary>Added to the reach of a moved collider vertex in <see cref="Active"/>: welded duplicates and rounding.</summary>
        private const float ActiveMargin = 0.001f;

        public sealed class Layer
        {
            public ColliderSurface Collider;
            public ClipCandidates Candidates;
            /// <summary>World delta of the collider for this shape frame; null when it does not move.</summary>
            public Vector3[] Delta;
            public bool IsBody;
            private SurfaceQuery deformed;

            /// <summary>The collider moved by Delta, built on first use and kept, so later steps of the frame reuse it.</summary>
            public SurfaceQuery Deformed => deformed ??= Collider.Deformed(Delta, 1f);
        }

        /// <summary>
        /// Pushes candidates closer to a deformed collider than min(d₀, clearance) back out along its normal; pushes are feathered
        /// to the welded neighbours and a rigid island moves by its deepest push. delta (world) is changed in place. Returns each
        /// layer's candidates still inside it at the result, as <see cref="CountInside"/> counts them.
        /// </summary>
        public static int[] Apply(Topology topology, Vector3[] rest, Vector3[] delta, IReadOnlyList<Layer> layers, float band, float clearance, HashSet<int> rigidIslands, ref ClipStats stats)
        {
            var reach = new float[layers.Count];
            var active = new bool[layers.Count][];
            var garmentMove = MaxMagnitude(delta);
            for (var i = 0; i < layers.Count; i++)
            {
                reach[i] = band + MaxMagnitude(layers[i].Delta) + garmentMove + QueryMargin;
                active[i] = Active(rest, delta, layers[i].Collider, layers[i].Candidates, layers[i].Delta, band);
            }
            // each candidate's signed and closest-point distance where it was last queried, which is where it ends: d₀ until a query,
            // NaN after a query that found nothing
            var signed = new float[layers.Count][];
            var distance = new float[layers.Count][];
            for (var i = 0; i < layers.Count; i++)
            {
                signed[i] = (float[])layers[i].Candidates.RestDistance.Clone();
                distance[i] = new float[signed[i].Length];
            }
            var push = new Vector3[delta.Length];
            var pushed = new bool[delta.Length];
            // the collider stays put during the call, so a candidate that did not move since its last query keeps its answer
            var moved = new bool[delta.Length];
            // the round after the last one only counts what is left
            for (var round = 0; ; round++)
            {
                Array.Clear(push, 0, push.Length);
                var any = false;
                for (var i = 0; i < layers.Count; i++)
                {
                    var candidates = layers[i].Candidates;
                    for (var k = 0; k < candidates.Vertices.Length; k++)
                    {
                        var d0 = candidates.RestDistance[k];
                        if (d0 > band) continue;
                        var v = candidates.Vertices[k];
                        if (round == 0 ? !active[i][k] : !moved[v]) continue;
                        var p = rest[v] + delta[v];
                        // the deformed collider is built on the first candidate that needs it
                        if (!layers[i].Collider.Hit(layers[i].Deformed, p, reach[i], out var hit))
                        {
                            signed[i][k] = float.NaN;
                            continue;
                        }
                        var target = Mathf.Min(d0, clearance);
                        var s = signed[i][k] = SurfaceQuery.Signed(p, hit);
                        distance[i][k] = hit.Distance;
                        if (s >= target) continue;
                        var need = hit.Normal * (target - s);
                        if (need.sqrMagnitude > push[v].sqrMagnitude) push[v] = need;
                        any = true;
                    }
                }
                if (!any) break;
                if (round == Rounds)
                {
                    for (var v = 0; v < push.Length; v++)
                        if (push[v] != Vector3.zero) stats.Unresolved++;
                    break;
                }
                Feather(topology, push);
                foreach (var island in rigidIslands)
                {
                    var deepest = Vector3.zero;
                    foreach (var v in topology.IslandVertices[island])
                        if (push[v].sqrMagnitude > deepest.sqrMagnitude) deepest = push[v];
                    foreach (var v in topology.IslandVertices[island]) push[v] = deepest;
                }
                for (var v = 0; v < delta.Length; v++)
                {
                    moved[v] = push[v] != Vector3.zero;
                    if (!moved[v]) continue;
                    delta[v] += push[v];
                    if (!pushed[v]) { pushed[v] = true; stats.Pushed++; }
                    stats.MaxPush = Mathf.Max(stats.MaxPush, push[v].magnitude);
                }
            }

            var inside = new int[layers.Count];
            var finalMove = MaxMagnitude(delta);
            for (var i = 0; i < layers.Count; i++)
            {
                // the reach CountInside takes for the result; it does not count what lies beyond
                var countReach = band + MaxMagnitude(layers[i].Delta) + finalMove + QueryMargin;
                var candidates = layers[i].Candidates;
                for (var k = 0; k < candidates.Vertices.Length; k++)
                {
                    if (candidates.RestDistance[k] > band) continue;
                    if (float.IsNaN(signed[i][k]))
                    {
                        var p = rest[candidates.Vertices[k]] + delta[candidates.Vertices[k]];
                        if (layers[i].Collider.Hit(layers[i].Deformed, p, countReach, out var hit) && SurfaceQuery.Signed(p, hit) < 0f) inside[i]++;
                    }
                    else if (signed[i][k] < 0f && distance[i][k] <= countReach) inside[i]++;
                }
            }
            return inside;
        }

        /// <summary>
        /// The skin side of anti-clip. Skin the shape moves (skin candidates within band, body delta not zero) that ends closer under
        /// the deformed garment than max(min(gap, clearance), SkinFloor), or outside it, lifts the hit triangle along its face normal by
        /// the least change of its corners. Pushes are feathered and a rigid island moves by its deepest push, as in Apply, for at most
        /// Rounds rounds. It catches skin passing between garment vertices, which Apply cannot see. delta (world) is changed in place.
        /// </summary>
        public static void LiftOverSkin(Topology topology, Vector3[] delta, ColliderSurface garment, SkinCandidates skin,
            Vector3[] bodyRest, Vector3[] bodyDelta, float band, float clearance, HashSet<int> rigidIslands, ref ClipStats stats)
        {
            if (bodyDelta == null) return;
            var active = new List<int>();
            for (var k = 0; k < skin.Vertices.Length; k++)
                if (skin.RestGap[k] <= band && !DeltaOps.IsZero(bodyDelta[skin.Vertices[k]])) active.Add(k);
            if (active.Count == 0) return;
            var t = topology.Triangles;
            var push = new Vector3[delta.Length];
            for (var round = 0; ; round++)
            {
                Array.Clear(push, 0, push.Length);
                var query = garment.Deformed(delta, 1f);
                var reach = band + MaxMagnitude(bodyDelta) + MaxMagnitude(delta) + QueryMargin;
                var any = false;
                foreach (var k in active)
                {
                    var b = skin.Vertices[k];
                    var p = bodyRest[b] + bodyDelta[b];
                    if (!garment.Hit(query, p, reach, out var hit)) continue;
                    // the skin point is under the sheet when signed is negative: lift what is missing to the target gap
                    var lift = SurfaceQuery.Signed(p, hit) + Mathf.Max(Mathf.Min(skin.RestGap[k], clearance), SkinFloor);
                    if (lift <= 0f) continue;
                    var bary = hit.Bary;
                    var squares = bary.x * bary.x + bary.y * bary.y + bary.z * bary.z;
                    for (var c = 0; c < 3; c++)
                    {
                        var need = hit.Normal * (lift * bary[c] / squares);
                        var v = t[3 * hit.Triangle + c];
                        if (need.sqrMagnitude > push[v].sqrMagnitude) push[v] = need;
                    }
                    any = true;
                }
                if (!any) break;
                if (round == Rounds)
                {
                    for (var v = 0; v < push.Length; v++)
                        if (push[v] != Vector3.zero) stats.Unresolved++;
                    break;
                }
                Feather(topology, push);
                foreach (var island in rigidIslands)
                {
                    var deepest = Vector3.zero;
                    foreach (var v in topology.IslandVertices[island])
                        if (push[v].sqrMagnitude > deepest.sqrMagnitude) deepest = push[v];
                    foreach (var v in topology.IslandVertices[island]) push[v] = deepest;
                }
                for (var v = 0; v < delta.Length; v++)
                {
                    if (push[v] == Vector3.zero) continue;
                    delta[v] += push[v];
                    stats.MaxPush = Mathf.Max(stats.MaxPush, push[v].magnitude);
                }
            }
        }

        /// <summary>The skin candidates (body vertex indices) the shape moves that end outside the garment sheet, with the body and the garment moved by the given deltas.</summary>
        public static List<int> SkinOutside(Vector3[] bodyRest, Vector3[] bodyDelta, ColliderSurface garment, SkinCandidates skin, Vector3[] garmentDelta, float band)
        {
            var outside = new List<int>();
            if (bodyDelta == null) return outside;
            SurfaceQuery query = null;
            var reach = band + MaxMagnitude(bodyDelta) + MaxMagnitude(garmentDelta) + QueryMargin;
            for (var k = 0; k < skin.Vertices.Length; k++)
            {
                var b = skin.Vertices[k];
                if (skin.RestGap[k] > band || DeltaOps.IsZero(bodyDelta[b])) continue;
                query ??= garment.Deformed(garmentDelta, 1f);
                var p = bodyRest[b] + bodyDelta[b];
                if (garment.Hit(query, p, reach, out var hit) && SurfaceQuery.Signed(p, hit) > 0f) outside.Add(b);
            }
            return outside;
        }

        /// <summary>Candidates inside the collider (signed distance below 0) with the garment and the collider moved by the given deltas.</summary>
        public static int CountInside(Vector3[] rest, Vector3[] garmentDelta, ColliderSurface collider, ClipCandidates candidates, Vector3[] colliderDelta, float band) =>
            Inside(rest, garmentDelta, collider, candidates, colliderDelta, band).Count;

        /// <summary>The garment vertices CountInside counts.</summary>
        public static List<int> Inside(Vector3[] rest, Vector3[] garmentDelta, ColliderSurface collider, ClipCandidates candidates, Vector3[] colliderDelta, float band)
        {
            var active = Active(rest, garmentDelta, collider, candidates, colliderDelta, band);
            SurfaceQuery query = null;
            var reach = band + MaxMagnitude(colliderDelta) + MaxMagnitude(garmentDelta) + QueryMargin;
            var inside = new List<int>();
            for (var k = 0; k < candidates.Vertices.Length; k++)
            {
                if (!active[k]) continue;
                query ??= collider.Deformed(colliderDelta, 1f);
                var v = candidates.Vertices[k];
                var p = rest[v] + garmentDelta[v];
                if (collider.Hit(query, p, reach, out var hit) && SurfaceQuery.Signed(p, hit) < 0f) inside.Add(v);
            }
            return inside;
        }

        /// <summary>
        /// Candidates within band whose closest point can differ from rest: the garment vertex moved, or a moved collider vertex u lies
        /// within (rest closest distance) + max(collider move, closest triangle's edge) + (longest edge at u) of it. Farther away no moved
        /// triangle comes as close as the rest closest point and no triangle around its triangle's corners moved, so the other
        /// candidates keep their rest closest point and d₀ &gt; 0.
        /// </summary>
        private static bool[] Active(Vector3[] rest, Vector3[] garmentDelta, ColliderSurface collider, ClipCandidates candidates, Vector3[] colliderDelta, float band)
        {
            var active = new bool[candidates.Vertices.Length];
            var moved = new List<int>();
            float move = 0f, edge = 0f;
            if (colliderDelta != null)
                for (var u = 0; u < colliderDelta.Length; u++)
                {
                    if (DeltaOps.IsZero(colliderDelta[u])) continue;
                    moved.Add(u);
                    move = Mathf.Max(move, colliderDelta[u].magnitude);
                    edge = Mathf.Max(edge, collider.RestQuery.EdgeAt[u]);
                }
            var closest = 0f;
            for (var k = 0; k < active.Length; k++)
                if (candidates.RestDistance[k] <= band) closest = Mathf.Max(closest, candidates.ClosestDistance[k]);
            // cells as large as the largest radius asked, so the moved vertices within it of a point lie in its cell or the 26 around it;
            // a cell is near when one of those 27 holds a moved vertex
            var cell = closest + Mathf.Max(move, collider.RestQuery.LongestEdge) + edge + ActiveMargin;
            var cells = new Dictionary<long, List<int>>();
            var near = new HashSet<long>();
            foreach (var u in moved)
            {
                var (x, y, z) = CellOf(collider.Rest[u], cell);
                if (!cells.TryGetValue(Key(x, y, z), out var list))
                {
                    cells[Key(x, y, z)] = list = new List<int>();
                    for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                        near.Add(Key(x + dx, y + dy, z + dz));
                }
                list.Add(u);
            }
            for (var k = 0; k < active.Length; k++)
            {
                if (candidates.RestDistance[k] > band) continue;
                var v = candidates.Vertices[k];
                if (!DeltaOps.IsZero(garmentDelta[v])) active[k] = true;
                else if (near.Count > 0)
                {
                    var p = rest[v];
                    var (x, y, z) = CellOf(p, cell);
                    if (!near.Contains(Key(x, y, z))) continue;
                    var radius = candidates.ClosestDistance[k] + Mathf.Max(move, candidates.TriangleEdge[k]) + ActiveMargin;
                    for (var dx = -1; dx <= 1 && !active[k]; dx++)
                    for (var dy = -1; dy <= 1 && !active[k]; dy++)
                    for (var dz = -1; dz <= 1 && !active[k]; dz++)
                    {
                        if (!cells.TryGetValue(Key(x + dx, y + dy, z + dz), out var list)) continue;
                        foreach (var u in list)
                        {
                            var within = radius + collider.RestQuery.EdgeAt[u];
                            if ((collider.Rest[u] - p).sqrMagnitude > within * within) continue;
                            active[k] = true;
                            break;
                        }
                    }
                }
            }
            return active;
        }

        private static (int, int, int) CellOf(Vector3 p, float cell) => (Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));

        /// <summary>A cell packed into a long, 21 bits an axis (kilometres at these cell sizes).</summary>
        private static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (uint)(z & 0x1FFFFF);

        /// <summary>Largest length in delta; 0 for null.</summary>
        private static float MaxMagnitude(Vector3[] delta)
        {
            var max = 0f;
            if (delta != null)
                foreach (var d in delta) max = Mathf.Max(max, d.sqrMagnitude);
            return Mathf.Sqrt(max);
        }

        /// <summary>A welded group keeps its largest push; a group without one takes half the mean of its neighbours' pushes.</summary>
        private static void Feather(Topology topology, Vector3[] push)
        {
            var group = new Vector3[topology.GroupCount];
            for (var v = 0; v < push.Length; v++)
                if (push[v].sqrMagnitude > group[topology.Group[v]].sqrMagnitude) group[topology.Group[v]] = push[v];
            var result = (Vector3[])group.Clone();
            for (var g = 0; g < group.Length; g++)
            {
                if (group[g] != Vector3.zero || topology.Neighbours[g].Length == 0) continue;
                var sum = Vector3.zero;
                foreach (var n in topology.Neighbours[g]) sum += group[n];
                result[g] = sum / topology.Neighbours[g].Length * 0.5f;
            }
            for (var v = 0; v < push.Length; v++) push[v] = result[topology.Group[v]];
        }
    }
}
