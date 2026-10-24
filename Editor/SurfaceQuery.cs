using System;
using System.Collections.Generic;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    internal struct SurfacePoint
    {
        public int Triangle;
        public Vector3 Bary;
        public Vector3 Point;
        public Vector3 Normal;
        public float Distance;
        /// <summary>The triangle corner with the largest barycentric weight; filters look at its bones.</summary>
        public int Corner;
    }

    /// <summary>
    /// Closest-point queries on a triangle surface through a vertex grid. Each corner c of a triangle no farther than the nearest
    /// vertex lies within (nearest vertex distance + longest edge at c), so only triangles around those vertices are tested. The grid
    /// holds every vertex and is searched to <see cref="ShortEdge"/> past that distance; vertices with longer edges also sit in sparse
    /// grids, one per doubling of the edge length, each searched to its own longest edge, so a few long edges do not widen every search.
    /// </summary>
    internal sealed class SurfaceQuery
    {
        public delegate bool Accept(int corner, Vector3 point, Vector3 normal);

        public readonly Vector3[] Positions;
        /// <summary>Vertex normals a hit interpolates; null when the query has face signs.</summary>
        public readonly Vector3[] Normals;
        public readonly Topology Topology;
        public readonly float LongestEdge;
        /// <summary>Longest edge at each vertex.</summary>
        public readonly float[] EdgeAt;
        /// <summary>Side of a grid cell.</summary>
        public readonly float Cell;
        /// <summary>Two median edges: the vertex grid alone answers for edges up to this length.</summary>
        public readonly float ShortEdge;
        /// <summary>ShortEdge, at most the longest edge.</summary>
        private readonly float denseEdge;
        private readonly bool rejectBoundary;
        /// <summary>Per triangle ±1: a hit takes the triangle's face normal times its sign instead of interpolated vertex normals, 0 skips the triangle; null for those.</summary>
        private readonly float[] faceSigns;
        private readonly Geometry.SpatialGrid grid;
        private readonly List<int> scratch = new List<int>();
        /// <summary>Per long-edge level: its longest edge, its vertices, their grid and the last query's hits (indices into its vertices).</summary>
        private readonly List<(float Edge, int[] Vertices, Geometry.SpatialGrid Grid, List<int> Found)> levels = new List<(float, int[], Geometry.SpatialGrid, List<int>)>();
        private readonly int[] stamp;
        private int query;

        /// <param name="cell">Grid cell; 0 takes it from the mesh density (three median edges, clamped to 1 mm–2 cm: a point a few
        /// millimetres off the surface finds its triangle in one ring of cells).</param>
        /// <param name="shortEdge">ShortEdge; 0 takes two median edges.</param>
        /// <param name="faceSigns">Per triangle ±1, or 0 to skip it: hits take the signed face normal and normals may be null.</param>
        public SurfaceQuery(Vector3[] positions, Vector3[] normals, Topology topology, bool rejectBoundary, float cell = 0f, float shortEdge = 0f, float[] faceSigns = null)
        {
            Positions = positions;
            Normals = normals;
            Topology = topology;
            this.rejectBoundary = rejectBoundary;
            this.faceSigns = faceSigns;
            var t = topology.Triangles;
            EdgeAt = new float[positions.Length];
            for (var i = 0; i < t.Length; i += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = t[i + k], b = t[i + (k + 1) % 3];
                var length = (positions[a] - positions[b]).magnitude;
                LongestEdge = Mathf.Max(LongestEdge, length);
                EdgeAt[a] = Mathf.Max(EdgeAt[a], length);
                EdgeAt[b] = Mathf.Max(EdgeAt[b], length);
            }
            var median = cell > 0f && shortEdge > 0f ? 0f : MedianEdge(positions, t);
            // ponytail: one cell for the whole mesh; per-region cells if a body mixes very dense and very sparse parts
            Cell = cell > 0f ? cell : t.Length == 0 ? 0.02f : Mathf.Clamp(3f * median, 0.001f, 0.02f);
            grid = new Geometry.SpatialGrid(positions, Cell);
            ShortEdge = shortEdge > 0f ? shortEdge : 2f * median;
            denseEdge = Mathf.Min(ShortEdge, LongestEdge);
            for (var below = denseEdge; below < LongestEdge;)
            {
                var edge = below > 0f ? Mathf.Min(2f * below, LongestEdge) : LongestEdge;
                var vertices = new List<int>();
                var points = new List<Vector3>();
                for (var v = 0; v < positions.Length; v++)
                    if (EdgeAt[v] > below && EdgeAt[v] <= edge)
                    {
                        vertices.Add(v);
                        points.Add(positions[v]);
                    }
                if (vertices.Count > 0) levels.Add((edge, vertices.ToArray(), new Geometry.SpatialGrid(points, 2f * edge), new List<int>()));
                below = edge;
            }
            stamp = new int[t.Length / 3];
        }

        private static float MedianEdge(Vector3[] positions, int[] t)
        {
            if (t.Length == 0) return 0f;
            var edges = new float[t.Length];
            for (var i = 0; i < t.Length; i++) edges[i] = (positions[t[i]] - positions[t[i % 3 == 2 ? i - 2 : i + 1]]).magnitude;
            Array.Sort(edges);
            return edges[edges.Length / 2];
        }

        /// <summary>Vertices within radius of p.</summary>
        public void Near(Vector3 p, float radius, List<int> output) => grid.Query(p, radius, output);

        /// <summary>Every triangle with a point within radius of p, once each, and some farther ones: those around the vertices v within (radius + longest edge at v).</summary>
        public void TrianglesNear(Vector3 p, float radius, List<int> output)
        {
            output.Clear();
            query++;
            Gather(p, radius, 0f, radius + LongestEdge);
            foreach (var v in scratch) Collect(v, p, radius, output);
            foreach (var level in levels)
            foreach (var i in level.Found)
                Collect(level.Vertices[i], p, radius, output);
        }

        private void Collect(int v, Vector3 p, float radius, List<int> output)
        {
            var within = radius + EdgeAt[v];
            if ((Positions[v] - p).sqrMagnitude > within * within) return;
            foreach (var t in Topology.VertexTriangles[v])
            {
                if (stamp[t] == query) continue;
                stamp[t] = query;
                output.Add(t);
            }
        }

        /// <summary>The closest point within maxRadius that accept takes (null takes all).</summary>
        public bool Closest(Vector3 p, float maxRadius, Accept accept, out SurfacePoint hit)
        {
            hit = default;
            hit.Distance = float.MaxValue;
            // a triangle with a point within maxRadius has every corner within maxRadius + LongestEdge
            var limit = maxRadius + LongestEdge;
            // a coarse cell starts at three median edges, which usually holds the nearest vertex and everything Gather needs
            for (var r = ShortEdge > 0f ? Mathf.Min(Cell, 1.5f * ShortEdge) : Cell; ; r *= 2f)
            {
                var reach = Mathf.Min(r, limit);
                grid.Query(p, reach, scratch);
                if (scratch.Count > 0)
                {
                    var nearest = float.MaxValue;
                    foreach (var v in scratch) nearest = Mathf.Min(nearest, (Positions[v] - p).sqrMagnitude);
                    nearest = Mathf.Sqrt(nearest);
                    var search = Gather(p, nearest, reach, limit);
                    if (Scan(p, nearest, accept, ref hit) || Scan(p, float.PositiveInfinity, accept, ref hit))
                    {
                        // beyond the nearest vertex (it is in no triangle, or its triangles were rejected) the bound did not hold
                        if (hit.Distance > nearest)
                        {
                            Gather(p, hit.Distance, search, limit);
                            Scan(p, hit.Distance, accept, ref hit);
                        }
                        if (hit.Distance > maxRadius)
                        {
                            hit = default;
                            return false;
                        }
                        return true;
                    }
                    r = search;
                }
                if (reach >= limit) return false;
            }
        }

        /// <summary>Distance of p along the surface normal at its closest point; negative is inside.</summary>
        public static float Signed(Vector3 p, in SurfacePoint hit) => Vector3.Dot(p - hit.Point, hit.Normal);

        /// <summary>Unit normal of a triangle at these positions, times its face sign when the query has them; zero for a degenerate or skipped triangle.</summary>
        public Vector3 FaceNormal(int triangle)
        {
            var t = Topology.Triangles;
            var a = Positions[t[3 * triangle]];
            var cross = Vector3.Cross(Positions[t[3 * triangle + 1]] - a, Positions[t[3 * triangle + 2]] - a);
            // not .normalized: the cross product is in m², and Unity zeroes vectors shorter than 1e-5
            var length = cross.magnitude;
            return length > 0f ? cross * ((faceSigns?[triangle] ?? 1f) / length) : Vector3.zero;
        }

        /// <summary>
        /// Collects every vertex v within min(bound + longest edge at v, limit) of p: the grid to bound + ShortEdge (it already holds
        /// the vertices within searched), each long-edge level to bound + its edge. Returns the radius the grid now holds.
        /// </summary>
        private float Gather(Vector3 p, float bound, float searched, float limit)
        {
            var radius = Mathf.Min(Mathf.Max(searched, bound + denseEdge), limit);
            if (radius > searched) grid.Query(p, radius, scratch);
            foreach (var level in levels) level.Grid.Query(p, Mathf.Min(bound + level.Edge, limit), level.Found);
            return radius;
        }

        /// <summary>Tests the triangles around the gathered vertices v within (bound + longest edge at v) of p.</summary>
        private bool Scan(Vector3 p, float bound, Accept accept, ref SurfacePoint best)
        {
            query++;
            var improved = false;
            foreach (var v in scratch) improved |= Around(v, p, bound, accept, ref best);
            foreach (var level in levels)
            foreach (var i in level.Found)
                improved |= Around(level.Vertices[i], p, bound, accept, ref best);
            return improved;
        }

        private bool Around(int v, Vector3 p, float bound, Accept accept, ref SurfacePoint best)
        {
            var within = bound + EdgeAt[v];
            if ((Positions[v] - p).sqrMagnitude > within * within) return false;
            var improved = false;
            var tris = Topology.Triangles;
            foreach (var t in Topology.VertexTriangles[v])
            {
                if (stamp[t] == query) continue;
                stamp[t] = query;
                int a = tris[3 * t], b = tris[3 * t + 1], c = tris[3 * t + 2];
                var q = Geometry.ClosestPointOnTriangle(p, Positions[a], Positions[b], Positions[c], out var bary);
                var d = (q - p).magnitude;
                if (d >= best.Distance) continue;
                if (rejectBoundary && OnBoundary(a, b, c, bary)) continue;
                var n = faceSigns != null ? FaceNormal(t) : (Normals[a] * bary.x + Normals[b] * bary.y + Normals[c] * bary.z).normalized;
                // a degenerate or skipped (sign 0) face has no side to take
                if (faceSigns != null && n == Vector3.zero) continue;
                var corner = bary.x >= bary.y ? (bary.x >= bary.z ? a : c) : (bary.y >= bary.z ? b : c);
                if (accept != null && !accept(corner, q, n)) continue;
                best = new SurfacePoint { Triangle = t, Bary = bary, Point = q, Normal = n, Distance = d, Corner = corner };
                improved = true;
            }
            return improved;
        }

        private bool OnBoundary(int a, int b, int c, Vector3 bary)
        {
            const float edge = 1e-4f;
            return bary.x < edge && Topology.IsBoundary(b, c)
                   || bary.y < edge && Topology.IsBoundary(a, c)
                   || bary.z < edge && Topology.IsBoundary(a, b);
        }
    }
}
