using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>Operations on per-vertex world deltas of a garment.</summary>
    internal static class DeltaOps
    {
        public static Vector3[] Add(Vector3[] a, Vector3[] b, float scale)
        {
            var result = new Vector3[a.Length];
            for (var i = 0; i < a.Length; i++) result[i] = a[i] + b[i] * scale;
            return result;
        }

        /// <summary>Every welded group gets the mean delta of its members, so seams and double-sided copies move together.</summary>
        public static void Weld(Topology topology, Vector3[] delta)
        {
            foreach (var members in topology.Members)
            {
                if (members.Length < 2) continue;
                var sum = Vector3.zero;
                foreach (var v in members) sum += delta[v];
                sum /= members.Length;
                foreach (var v in members) delta[v] = sum;
            }
        }

        /// <summary>Uniform Laplacian of the delta field over welded groups; expects welded input.</summary>
        /// <param name="adjacency">Each group's neighbours to average over; null takes the mesh's (<see cref="LayerPairs.Neighbours"/> adds the layer links).</param>
        public static void Smooth(Topology topology, Vector3[] delta, int iterations, int[][] adjacency = null, float factor = 0.5f)
        {
            if (iterations <= 0) return;
            adjacency ??= topology.Neighbours;
            var current = new Vector3[topology.GroupCount];
            for (var g = 0; g < current.Length; g++) current[g] = delta[topology.Members[g][0]];
            var next = new Vector3[current.Length];
            for (var it = 0; it < iterations; it++)
            {
                for (var g = 0; g < current.Length; g++)
                {
                    var neighbours = adjacency[g];
                    if (neighbours.Length == 0) { next[g] = current[g]; continue; }
                    var average = Vector3.zero;
                    foreach (var n in neighbours) average += current[n];
                    average /= neighbours.Length;
                    next[g] = current[g] + factor * (average - current[g]);
                }
                (current, next) = (next, current);
            }
            for (var v = 0; v < delta.Length; v++) delta[v] = current[topology.Group[v]];
        }

        /// <summary>Exactly zero; Vector3's == takes anything under 1e-5 for equal.</summary>
        public static bool IsZero(Vector3 d) => d.x == 0f && d.y == 0f && d.z == 0f;

        public static void Scale(Vector3[] delta, float[] factor)
        {
            for (var v = 0; v < delta.Length; v++) delta[v] *= factor[v];
        }

        /// <summary>Islands whose bounding-box diagonal is at most maxSize, plus the islands of the marked vertices.</summary>
        public static HashSet<int> RigidIslands(Topology topology, Vector3[] positions, float maxSize, IEnumerable<int> markedVertices)
        {
            var result = new HashSet<int>();
            for (var i = 0; i < topology.IslandVertices.Length; i++)
            {
                var vertices = topology.IslandVertices[i];
                var min = positions[vertices[0]];
                var max = min;
                foreach (var v in vertices)
                {
                    min = Vector3.Min(min, positions[v]);
                    max = Vector3.Max(max, positions[v]);
                }
                if ((max - min).magnitude <= maxSize) result.Add(i);
            }
            foreach (var v in markedVertices)
                if (v >= 0 && v < topology.Island.Length && topology.Island[v] >= 0) result.Add(topology.Island[v]);
            return result;
        }

        /// <summary>
        /// Replaces the deltas of each listed island with the rigid motion that best matches them (shape matching). An island with
        /// fewer than three welded groups or a line-like shape has no defined rotation and gets the mean translation.
        /// Welded vertices shared with another island take the rigid island's motion.
        /// </summary>
        public static void Rigid(Topology topology, Vector3[] positions, Vector3[] delta, HashSet<int> islands)
        {
            foreach (var island in islands)
            {
                var vertices = topology.IslandVertices[island];
                // shape matching would leave rounding noise on an island nothing moves
                if (vertices.All(v => IsZero(delta[v]))) continue;
                var restCenter = Vector3.zero;
                var movedCenter = Vector3.zero;
                foreach (var v in vertices)
                {
                    restCenter += positions[v];
                    movedCenter += positions[v] + delta[v];
                }
                restCenter /= vertices.Length;
                movedCenter /= vertices.Length;
                var groups = vertices.Select(v => topology.Group[v]).Distinct().Count();
                if (groups < 3 || LineLike(vertices, positions))
                {
                    var translation = movedCenter - restCenter;
                    foreach (var v in vertices) delta[v] = translation;
                    continue;
                }
                Vector3 a0 = Vector3.zero, a1 = Vector3.zero, a2 = Vector3.zero;
                foreach (var v in vertices)
                {
                    var x = positions[v] - restCenter;
                    var y = positions[v] + delta[v] - movedCenter;
                    a0 += y * x.x;
                    a1 += y * x.y;
                    a2 += y * x.z;
                }
                var rotation = Rotation(a0, a1, a2);
                foreach (var v in vertices) delta[v] = movedCenter + rotation * (positions[v] - restCenter) - positions[v];
            }
            // Welded twins in other islands (a ring touching a strap at one point) follow the rigid island; the first island wins.
            var done = new HashSet<int>();
            foreach (var island in islands)
            foreach (var v in topology.IslandVertices[island])
            {
                if (!done.Add(topology.Group[v])) continue;
                foreach (var twin in topology.Members[topology.Group[v]]) delta[twin] = delta[v];
            }
        }

        /// <summary>Rotational part of the matrix with columns a0..a2 (Müller et al. 2016, iterative, no SVD).</summary>
        private static Quaternion Rotation(Vector3 a0, Vector3 a1, Vector3 a2)
        {
            var q = Quaternion.identity;
            for (var i = 0; i < 50; i++)
            {
                var r0 = q * Vector3.right;
                var r1 = q * Vector3.up;
                var r2 = q * Vector3.forward;
                var omega = (Vector3.Cross(r0, a0) + Vector3.Cross(r1, a1) + Vector3.Cross(r2, a2))
                            / (Mathf.Abs(Vector3.Dot(r0, a0) + Vector3.Dot(r1, a1) + Vector3.Dot(r2, a2)) + 1e-20f);
                var w = omega.magnitude;
                if (w < 1e-6f) break;
                q = Quaternion.AngleAxis(w * Mathf.Rad2Deg, omega / w) * q;
                q.Normalize();
            }
            return q;
        }

        private static bool LineLike(int[] vertices, Vector3[] positions)
        {
            var origin = positions[vertices[0]];
            var far = origin;
            foreach (var v in vertices)
                if ((positions[v] - origin).sqrMagnitude > (far - origin).sqrMagnitude) far = positions[v];
            var axis = far - origin;
            var length = axis.magnitude;
            if (length < 1e-9f) return true;
            axis /= length;
            var off = 0f;
            foreach (var v in vertices)
            {
                var d = positions[v] - origin;
                off = Mathf.Max(off, (d - axis * Vector3.Dot(d, axis)).magnitude);
            }
            return off < 1e-3f * length;
        }
    }

    /// <summary>
    /// Islands that lie on the rest of the garment and ride on it, with the closest point of each of their vertices on it. Smoothing
    /// never crosses into a separate island except through layer pairs (<see cref="LayerPairs"/>), so a rivet, or a belt loop more
    /// than <see cref="LayerPairs.MaxGap"/> off the cloth, keeps its own raw sample while the cloth around it takes the smoothed motion
    /// and swallows it; <see cref="Apply"/> moves such islands with the cloth instead.
    /// - A rigid island (a rivet, a button) with a point within <see cref="Reach"/> of non-rigid cloth rides with its rigid fit.
    /// - A non-rigid island (a belt loop, a patch pocket) rides vertex by vertex when every vertex lies within Reach of a larger
    ///   non-rigid island and the island on average lies no closer to the skin than the points under it; a lining under the
    ///   cloth does not ride, and keeps its own motion except through layer pairs.
    /// </summary>
    internal sealed class Attachments
    {
        /// <summary>How far from the cloth an island may sit and still ride on it, metres.</summary>
        public const float Reach = 0.01f;
        /// <summary>Rigid islands that ride.</summary>
        public readonly HashSet<int> Islands = new HashSet<int>();
        /// <summary>Non-rigid islands that ride, larger first, so one that rides on another overlay moves after it.</summary>
        public readonly List<int> Overlays = new List<int>();
        private readonly List<(int Vertex, int A, int B, int C, Vector3 Bary)> overlayPoints = new List<(int, int, int, int, Vector3)>();
        private readonly List<(int Vertex, int A, int B, int C, Vector3 Bary)> rigidPoints = new List<(int, int, int, int, Vector3)>();

        /// <summary>The cloth is every island that is not rigid; with all islands rigid (the Rigid preset) nothing rides.</summary>
        /// <param name="skinDistance">Each vertex's rest distance to the skin (<see cref="Correspondence.Distance"/>).</param>
        public Attachments(Topology topology, Vector3[] positions, HashSet<int> rigidIslands, float[] skinDistance)
        {
            var count = topology.IslandVertices.Length;
            if (count < 2 || rigidIslands.Count == count) return;
            var query = new SurfaceQuery(positions, topology.Normals(positions), topology, rejectBoundary: false);
            var t = topology.Triangles;
            var size = new float[count];
            for (var i = 0; i < count; i++)
            {
                var vertices = topology.IslandVertices[i];
                var min = positions[vertices[0]];
                var max = min;
                foreach (var v in vertices)
                {
                    min = Vector3.Min(min, positions[v]);
                    max = Vector3.Max(max, positions[v]);
                }
                size[i] = (max - min).magnitude;
            }
            // a strict order, so two equal islands cannot each ride on the other
            bool Larger(int a, int b) => size[a] != size[b] ? size[a] > size[b]
                : topology.IslandVertices[a].Length != topology.IslandVertices[b].Length ? topology.IslandVertices[a].Length > topology.IslandVertices[b].Length : a > b;
            var found = new List<(int, int, int, int, Vector3)>();

            var free = Enumerable.Range(0, count).Where(i => !rigidIslands.Contains(i)).ToList();
            free.Sort((a, b) => a == b ? 0 : Larger(a, b) ? -1 : 1);
            foreach (var island in free)
            {
                SurfaceQuery.Accept larger = (corner, point, normal) =>
                {
                    var host = topology.Island[corner];
                    return host >= 0 && !rigidIslands.Contains(host) && Larger(host, island);
                };
                found.Clear();
                // how much farther from the skin than the cloth under it the island lies, summed; a loop folded over the
                // waistband's edge has a few vertices under it, a lining has nearly all
                var above = 0f;
                foreach (var v in topology.IslandVertices[island])
                {
                    if (!query.Closest(positions[v], Reach, larger, out var hit)) break;
                    int a = t[3 * hit.Triangle], b = t[3 * hit.Triangle + 1], c = t[3 * hit.Triangle + 2];
                    found.Add((v, a, b, c, hit.Bary));
                    // a vertex far from the skin has no distance (infinity); one with a finite distance over such cloth is under it
                    if (float.IsInfinity(skinDistance[v])) continue;
                    if (float.IsInfinity(skinDistance[a]) || float.IsInfinity(skinDistance[b]) || float.IsInfinity(skinDistance[c])) above -= Reach;
                    else above += skinDistance[v] - (skinDistance[a] * hit.Bary.x + skinDistance[b] * hit.Bary.y + skinDistance[c] * hit.Bary.z);
                }
                if (found.Count < topology.IslandVertices[island].Length || above < -Correspondence.SideTolerance * found.Count) continue;
                Overlays.Add(island);
                overlayPoints.AddRange(found);
            }

            SurfaceQuery.Accept cloth = (corner, point, normal) => topology.Island[corner] >= 0 && !rigidIslands.Contains(topology.Island[corner]);
            foreach (var island in rigidIslands)
            {
                var vertices = topology.IslandVertices[island];
                var radius = Reach + size[island];
                var touches = false;
                found.Clear();
                foreach (var v in vertices)
                {
                    if (!query.Closest(positions[v], radius, cloth, out var hit)) break;
                    touches |= hit.Distance <= Reach;
                    found.Add((v, t[3 * hit.Triangle], t[3 * hit.Triangle + 1], t[3 * hit.Triangle + 2], hit.Bary));
                }
                if (!touches || found.Count < vertices.Length) continue;
                Islands.Add(island);
                rigidPoints.AddRange(found);
            }
        }

        /// <summary>
        /// Gives every overlay the motion of the cloth under it, larger overlays first, then every rigid island that rides the motion
        /// of the cloth under it (overlays included) and its own rigid fit of that motion.
        /// </summary>
        public void Apply(Topology topology, Vector3[] positions, Vector3[] delta)
        {
            foreach (var (v, a, b, c, bary) in overlayPoints) delta[v] = delta[a] * bary.x + delta[b] * bary.y + delta[c] * bary.z;
            if (Islands.Count == 0) return;
            foreach (var (v, a, b, c, bary) in rigidPoints) delta[v] = delta[a] * bary.x + delta[b] * bary.y + delta[c] * bary.z;
            DeltaOps.Rigid(topology, positions, delta, Islands);
        }
    }

    /// <summary>
    /// Sheets of one garment lying on each other: a folded waistband, a lining, a belt loop. A pair is a welded group within the
    /// falloff and the face of the same garment that its rest skin normal, cast inward, crosses first within <see cref="MaxGap"/>;
    /// faces with a corner in the group and faces standing across the skin (|cos| below <see cref="ColliderSurface.RimCos"/>) do
    /// not count. Smoothing links each pair, so the sheets move as one surface; after anti-clip, before the attachments ride, the outer
    /// side is kept in front.
    /// </summary>
    internal sealed class LayerPairs
    {
        /// <summary>How far under a vertex, along its skin normal, another sheet may lie and still pair with it, metres.</summary>
        public const float MaxGap = 0.003f;
        /// <summary>The gap an outer vertex keeps to its inner sheet when its rest gap is larger, metres.</summary>
        public const float MinGap = 0.0005f;
        private readonly Pair[] pairs;
        private HashSet<int> neighboursRigid;
        private int[][] neighbours;

        private struct Pair
        {
            public int Group;
            public int Triangle;
            public Vector3 Bary;
            /// <summary>Rest distance to the triangle along the rest skin normal.</summary>
            public float Gap;
            /// <summary>The group's closest skin point at rest.</summary>
            public SurfacePoint Skin;
        }

        /// <param name="skin">Each vertex's closest skin point at rest; read only where falloff is above 0.</param>
        public LayerPairs(Topology topology, Vector3[] positions, SurfacePoint[] skin, float[] falloff)
        {
            var t = topology.Triangles;
            // normals are never read: only TrianglesNear is asked
            var query = new SurfaceQuery(positions, null, topology, rejectBoundary: false);
            var near = new List<int>();
            var found = new List<Pair>();
            for (var g = 0; g < topology.GroupCount; g++)
            {
                var v = topology.Members[g][0];
                if (falloff[v] <= 0f) continue;
                var p = positions[v];
                var n = skin[v].Normal;
                var best = new Pair { Gap = float.MaxValue };
                query.TrianglesNear(p, MaxGap, near);
                foreach (var f in near)
                {
                    int a = t[3 * f], b = t[3 * f + 1], c = t[3 * f + 2];
                    if (topology.Group[a] == g || topology.Group[b] == g || topology.Group[c] == g) continue;
                    // a hit's depth is the barycentric mean of its corners' depths: a face wholly above the vertex or wholly beyond
                    // MaxGap cannot be under it (most faces near a vertex are its own sheet's)
                    float da = Vector3.Dot(p - positions[a], n), db = Vector3.Dot(p - positions[b], n), dc = Vector3.Dot(p - positions[c], n);
                    if (Mathf.Max(da, Mathf.Max(db, dc)) <= Topology.WeldDistance || Mathf.Min(da, Mathf.Min(db, dc)) > MaxGap) continue;
                    // not .normalized: the cross product is in m², and Unity zeroes vectors shorter than 1e-5
                    var cross = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                    var length = cross.magnitude;
                    if (length == 0f || Mathf.Abs(Vector3.Dot(cross, n)) < ColliderSurface.RimCos * length) continue;
                    if (!Inward(p, n, positions[a], positions[b], positions[c], out var depth, out var bary)) continue;
                    if (depth > Topology.WeldDistance && depth <= MaxGap && depth < best.Gap)
                        best = new Pair { Group = g, Triangle = f, Bary = bary, Gap = depth, Skin = skin[v] };
                }
                if (best.Gap <= MaxGap) found.Add(best);
            }
            // inner layers first, so a stack of sheets mostly settles in Keep's first round; the group breaks ties for a fixed order
            pairs = found.OrderBy(x => x.Skin.Distance).ThenBy(x => x.Group).ToArray();
        }

        /// <summary>
        /// The smoothing adjacency of the garment's welded groups: the mesh's neighbours plus, both ways, each pair's group and the
        /// groups of its triangle's corners, unless either side lies in a rigid island. Kept for the last rigid set asked, which is
        /// the same for every shape of a garment's transfer.
        /// </summary>
        public int[][] Neighbours(Topology topology, HashSet<int> rigidIslands)
        {
            var rigid = rigidIslands ?? new HashSet<int>();
            if (neighbours != null && neighboursRigid.SetEquals(rigid)) return neighbours;
            var extra = new Dictionary<int, HashSet<int>>();
            void Link(int from, int to)
            {
                if (!extra.TryGetValue(from, out var set)) extra[from] = set = new HashSet<int>();
                set.Add(to);
            }
            var t = topology.Triangles;
            foreach (var pair in pairs)
            {
                var corners = new[] { topology.Group[t[3 * pair.Triangle]], topology.Group[t[3 * pair.Triangle + 1]], topology.Group[t[3 * pair.Triangle + 2]] };
                if (Rigid(topology, pair.Group, rigid) || corners.Any(corner => Rigid(topology, corner, rigid))) continue;
                foreach (var c in corners)
                {
                    Link(pair.Group, c);
                    Link(c, pair.Group);
                }
            }
            var result = topology.Neighbours;
            if (extra.Count > 0)
            {
                result = (int[][])result.Clone();
                foreach (var kv in extra) result[kv.Key] = result[kv.Key].Union(kv.Value).ToArray();
            }
            neighboursRigid = new HashSet<int>(rigid);
            return neighbours = result;
        }

        /// <summary>
        /// Keeps every outer group outside a rigid island at least min(its rest gap, <see cref="MinGap"/>) in front of its inner
        /// sheet: along the deformed skin normal at its rest skin point, against the deformed triangle at the pair's barycentrics,
        /// pushing it out along that normal by the shortfall. A pair none of whose vertices moved is left alone. A push can move a group
        /// that is another pair's inner sheet, so the pass repeats until nothing is pushed, at most <see cref="AntiClip.Rounds"/> times.
        /// </summary>
        /// <param name="bodyTriangles">The body's triangles, which the skin points index.</param>
        /// <param name="bodyNormals">The deformed body's vertex normals; asked once, at the first pair that moved and is not rigid.</param>
        public void Keep(Topology topology, Vector3[] positions, Vector3[] delta, HashSet<int> rigidIslands, int[] bodyTriangles, Func<Vector3[]> bodyNormals)
        {
            Vector3[] normals = null;
            var t = topology.Triangles;
            for (var round = 0; round < AntiClip.Rounds; round++)
            {
                var pushed = false;
                foreach (var pair in pairs)
                {
                    var members = topology.Members[pair.Group];
                    var v = members[0];
                    int a = t[3 * pair.Triangle], b = t[3 * pair.Triangle + 1], c = t[3 * pair.Triangle + 2];
                    if (DeltaOps.IsZero(delta[v]) && DeltaOps.IsZero(delta[a]) && DeltaOps.IsZero(delta[b]) && DeltaOps.IsZero(delta[c])) continue;
                    if (Rigid(topology, pair.Group, rigidIslands)) continue;
                    normals ??= bodyNormals();
                    var s = pair.Skin;
                    var i = 3 * s.Triangle;
                    var n = (normals[bodyTriangles[i]] * s.Bary.x + normals[bodyTriangles[i + 1]] * s.Bary.y + normals[bodyTriangles[i + 2]] * s.Bary.z).normalized;
                    var inner = (positions[a] + delta[a]) * pair.Bary.x + (positions[b] + delta[b]) * pair.Bary.y + (positions[c] + delta[c]) * pair.Bary.z;
                    var shortfall = Mathf.Min(pair.Gap, MinGap) - Vector3.Dot(positions[v] + delta[v] - inner, n);
                    if (shortfall <= 0f) continue;
                    foreach (var m in members) delta[m] += n * shortfall;
                    pushed = true;
                }
                if (!pushed) break;
            }
        }

        private static bool Rigid(Topology topology, int group, HashSet<int> rigidIslands) =>
            rigidIslands != null && topology.Members[group].Any(v => rigidIslands.Contains(topology.Island[v]));

        /// <summary>
        /// Where the line p − depth·n crosses triangle abc (Möller–Trumbore, depth of either sign). Barycentrics a little outside the
        /// triangle count, so a line through an edge or a corner hits. Expects n not parallel to the triangle.
        /// </summary>
        private static bool Inward(Vector3 p, Vector3 n, Vector3 a, Vector3 b, Vector3 c, out float depth, out Vector3 bary)
        {
            const float edge = 1e-4f;
            depth = 0f;
            bary = Vector3.zero;
            Vector3 e1 = b - a, e2 = c - a, s = p - a;
            var h = Vector3.Cross(-n, e2);
            var det = Vector3.Dot(e1, h);
            var u = Vector3.Dot(s, h) / det;
            var q = Vector3.Cross(s, e1);
            var w = Vector3.Dot(-n, q) / det;
            if (u < -edge || w < -edge || u + w > 1f + edge) return false;
            depth = Vector3.Dot(e2, q) / det;
            bary = new Vector3(1f - u - w, u, w);
            return true;
        }
    }
}
