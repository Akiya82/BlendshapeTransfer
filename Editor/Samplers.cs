using System;
using System.Collections.Generic;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    internal static class Samplers
    {
        /// <summary>Raw world deltas of every garment vertex for one body shape frame (step 1 of the pipeline).</summary>
        public static Vector3[] Evaluate(Correspondence c, BodySurface body, Vector3[] bodyDelta, Vector3[] garmentPositions)
        {
            var raw = new Vector3[garmentPositions.Length];
            Vector3[] deformed = null, deformedNormals = null, cellDelta = null;
            if (c.Surface != null)
            {
                // ponytail: deforms and renormals the whole body per shape; restrict to the shape's support plus one ring if Transfer misses the time targets
                deformed = DeltaOps.Add(body.Positions, bodyDelta, 1f);
                deformedNormals = body.Topology.Normals(deformed);
            }
            if (c.Field != null) cellDelta = c.Cells.Deltas(bodyDelta);
            var triangles = body.Topology.Triangles;
            for (var v = 0; v < raw.Length; v++)
            {
                if (c.Falloff[v] <= 0f) continue;
                var surface = Vector3.zero;
                var field = Vector3.zero;
                // Hybrid binds each side only where its mix weight is above 0
                if (c.Surface != null && c.Mix[v] < 1f)
                {
                    var b = c.Surface[v];
                    // the difference of the deformed and the rest frame: exactly zero where the triangle and its normals did not move,
                    // which rebuilding the garment point and subtracting it is not
                    Frame(triangles, b.Triangle, b.Bary, body.Positions, body.Normals, out var point0, out var t0, out var bt0, out var n0);
                    Frame(triangles, b.Triangle, b.Bary, deformed, deformedNormals, out var point, out var t, out var bt, out var n);
                    surface = (point - point0) + (t - t0) * b.Offset.x + (bt - bt0) * b.Offset.y + (n - n0) * b.Offset.z;
                }
                if (c.Field != null && c.Mix[v] > 0f) field = Sum(c.Field[v], cellDelta);
                switch (c.Settings.mode)
                {
                    case SamplerMode.Surface: raw[v] = surface; break;
                    case SamplerMode.Blend: raw[v] = Sum(c.Blend[v], bodyDelta); break;
                    case SamplerMode.Field: raw[v] = field; break;
                    default: raw[v] = Vector3.Lerp(surface, field, c.Mix[v]); break;
                }
            }
            return raw;
        }

        /// <summary>Point, tangent frame and normal at barycentric coordinates of a triangle; the tangent follows its first edge.</summary>
        public static void Frame(int[] triangles, int t, Vector3 bary, Vector3[] positions, Vector3[] normals,
            out Vector3 point, out Vector3 tangent, out Vector3 bitangent, out Vector3 normal)
        {
            int a = triangles[3 * t], b = triangles[3 * t + 1], c = triangles[3 * t + 2];
            point = positions[a] * bary.x + positions[b] * bary.y + positions[c] * bary.z;
            normal = (normals[a] * bary.x + normals[b] * bary.y + normals[c] * bary.z).normalized;
            var edge = positions[b] - positions[a];
            tangent = (edge - normal * Vector3.Dot(edge, normal)).normalized;
            if (tangent.sqrMagnitude < 0.5f) tangent = Vector3.Cross(normal, Mathf.Abs(normal.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
            bitangent = Vector3.Cross(normal, tangent);
        }

        /// <summary>Normalized kernel weights (1 − r²/R²)² · area over the candidates that accept takes (null takes all).</summary>
        public static Sample[] Kernel(Vector3[] positions, float[] areas, Vector3 center, float radius, List<int> candidates, Func<int, bool> accept)
        {
            var samples = new List<Sample>();
            var total = 0f;
            foreach (var i in candidates)
            {
                if (accept != null && !accept(i)) continue;
                var q = (positions[i] - center).sqrMagnitude / (radius * radius);
                if (q >= 1f) continue;
                var w = (1f - q) * (1f - q) * areas[i];
                if (w <= 0f) continue;
                samples.Add(new Sample { Index = i, Weight = w });
                total += w;
            }
            for (var k = 0; k < samples.Count; k++) samples[k] = new Sample { Index = samples[k].Index, Weight = samples[k].Weight / total };
            return samples.ToArray();
        }

        public static Vector3 Sum(Sample[] samples, Vector3[] field)
        {
            var sum = Vector3.zero;
            foreach (var s in samples) sum += field[s.Index] * s.Weight;
            return sum;
        }
    }
}
