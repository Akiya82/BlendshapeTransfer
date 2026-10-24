using System;
using System.Collections.Generic;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    internal struct Sample
    {
        public int Index;
        public float Weight;
    }

    /// <summary>Where a garment vertex sits on the body (triangle, barycentrics) and its offset in the local frame there.</summary>
    internal struct Binding
    {
        public int Triangle;
        public Vector3 Bary;
        public Vector3 Offset;
    }

    /// <summary>
    /// Everything a garment's sampling needs that does not depend on the shape, computed once per garment and settings:
    /// distance to the skin, filtered surface bindings, kernel samples, falloff, the Hybrid mix and the garment's layer pairs.
    /// </summary>
    internal sealed class Correspondence
    {
        public const float SideTolerance = 0.0005f;
        public readonly TransferSettings Settings;
        public readonly float[] Distance;
        public readonly Binding[] Surface;
        public readonly Sample[][] Blend;
        public readonly Sample[][] Field;
        public readonly FieldCells Cells;
        public readonly float[] Falloff;
        public readonly float[] Mix;
        public readonly int FallbackCount;
        public readonly LayerPairs Pairs;

        public Correspondence(BodySurface body, GarmentSnapshot garment, TransferSettings settings)
        {
            Settings = settings.Clamped();
            settings = Settings;
            var n = garment.Positions.Length;
            Distance = new float[n];
            Falloff = new float[n];
            Mix = new float[n];
            var mode = settings.mode;
            var needSurface = mode == SamplerMode.Surface || mode == SamplerMode.Hybrid;
            var needBlend = mode == SamplerMode.Blend;
            var needField = mode == SamplerMode.Field || mode == SamplerMode.Hybrid;
            if (needSurface) Surface = new Binding[n];
            if (needBlend) Blend = new Sample[n][];
            if (needField)
            {
                Field = new Sample[n][];
                Cells = new FieldCells(body, settings.fieldRadius / 6f);
            }
            // vertices beyond falloffZero never move, so the skin farther than that only needs to be known as absent
            var reach = Mathf.Max(settings.falloffZero, settings.hybridFar) + 0.05f;
            var near = new List<int>();
            var skin = new SurfacePoint[n];
            for (var v = 0; v < n; v++)
            {
                var p = garment.Positions[v];
                if (!body.Query.Closest(p, reach, null, out var closest))
                {
                    Distance[v] = float.PositiveInfinity;
                    continue;
                }
                Distance[v] = closest.Distance;
                skin[v] = closest;
                Falloff[v] = 1f - Smoothstep(settings.falloffFull, settings.falloffZero, closest.Distance);
                Mix[v] = mode == SamplerMode.Hybrid ? Smoothstep(settings.hybridNear, settings.hybridFar, closest.Distance) : mode == SamplerMode.Field ? 1f : 0f;
                if (Falloff[v] <= 0f) continue;

                var bones = settings.boneFilter ? garment.BodyBones[v] : Array.Empty<int>();
                // fabric inside the skin would have the side filter reject the very skin it clips into
                var side = settings.sideFilter && SurfaceQuery.Signed(p, closest) >= -SideTolerance;
                var useSurface = needSurface && Mix[v] < 1f;
                var useField = needField && Mix[v] > 0f;
                var fellBack = false;
                var hit = closest;
                if (useSurface || needBlend)
                {
                    SurfaceQuery.Accept accept = (corner, q, normal) =>
                        body.Bones.RelatedToAny(corner, bones) && (!side || Vector3.Dot(normal, p - q) > -SideTolerance);
                    // accepted skin is searched only about as far as the closest skin, so a rejected neighbourhood does not bind far away
                    if (!accept(closest.Corner, closest.Point, closest.Normal)
                        && !body.Query.Closest(p, closest.Distance + Mathf.Max(0.02f, closest.Distance), accept, out hit))
                    {
                        hit = closest;
                        fellBack = true;
                    }
                }
                if (useSurface) Surface[v] = Bind(body, hit, p);
                if (needBlend) Blend[v] = BlendSamples(body, hit, p, bones, side, settings, near, ref fellBack);
                if (useField)
                {
                    Field[v] = Cells.Samples(p, settings.fieldRadius, cell => CellRelated(body.Bones, Cells.Bone[cell], bones), out var fieldFellBack);
                    fellBack |= fieldFellBack;
                }
                if (fellBack) FallbackCount++;
            }
            Pairs = new LayerPairs(garment.Topology, garment.Positions, skin, Falloff);
        }

        public static float Smoothstep(float from, float to, float x)
        {
            if (to <= from) return x < from ? 0f : 1f;
            var t = Mathf.Clamp01((x - from) / (to - from));
            return t * t * (3f - 2f * t);
        }

        /// <summary>True for an unweighted cell or one whose bone is related to one of bones; an empty bones array filters nothing.</summary>
        private static bool CellRelated(BoneData data, int bone, int[] bones)
        {
            if (bones.Length == 0 || bone < 0) return true;
            foreach (var b in bones)
                if (data.Related(bone, b)) return true;
            return false;
        }

        private static Binding Bind(BodySurface body, in SurfacePoint hit, Vector3 p)
        {
            Samplers.Frame(body.Topology.Triangles, hit.Triangle, hit.Bary, body.Positions, body.Normals, out var point, out var t, out var b, out var n);
            var o = p - point;
            return new Binding { Triangle = hit.Triangle, Bary = hit.Bary, Offset = new Vector3(Vector3.Dot(o, t), Vector3.Dot(o, b), Vector3.Dot(o, n)) };
        }

        private static Sample[] BlendSamples(BodySurface body, in SurfacePoint center, Vector3 p, int[] bones, bool side, TransferSettings s, List<int> near, ref bool fellBack)
        {
            body.Query.Near(center.Point, s.blendRadius, near);
            var samples = Samplers.Kernel(body.Positions, body.Areas, center.Point, s.blendRadius, near, j =>
                body.Bones.RelatedToAny(j, bones) && (!side || Vector3.Dot(body.Normals[j], p - body.Positions[j]) > -SideTolerance));
            if (samples.Length == 0)
            {
                fellBack = true;
                samples = Samplers.Kernel(body.Positions, body.Areas, center.Point, s.blendRadius, near, null);
            }
            if (samples.Length > 0) return samples;
            var t = body.Topology.Triangles;
            var i = center.Triangle * 3;
            return new[]
            {
                new Sample { Index = t[i], Weight = center.Bary.x },
                new Sample { Index = t[i + 1], Weight = center.Bary.y },
                new Sample { Index = t[i + 2], Weight = center.Bary.z },
            };
        }
    }
}
