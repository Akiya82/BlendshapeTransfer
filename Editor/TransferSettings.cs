using System;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    public enum SamplerMode { Surface, Blend, Field, Hybrid }

    /// <summary>Parameters of one transfer. A preset asset holds one; a garment's TransferData keeps the copy it was computed with.</summary>
    [Serializable]
    public sealed class TransferSettings
    {
        public SamplerMode mode = SamplerMode.Surface;
        public float blendRadius = 0.04f;
        public float fieldRadius = 0.12f;
        public float hybridNear = 0.015f;
        public float hybridFar = 0.05f;
        public float falloffFull = 0.02f;
        public float falloffZero = 0.06f;
        public bool boneFilter = true;
        public bool sideFilter = true;
        public int smoothIterations = 2;
        public float rigidIslandSize = 0.03f;
        public bool antiClip = true;
        public float clearance = 0.001f;

        /// <summary>Largest kernel radius, metres: Blend's samples grow with its square (4 typed for 4 cm would take the whole body per vertex).</summary>
        public const float MaxRadius = 0.5f;
        /// <summary>Largest distance from the skin for falloff and the Hybrid band, metres: farther than any garment of an avatar.</summary>
        public const float MaxDistance = 1f;
        /// <summary>Largest clearance, metres: anti-clip pushes only to min(d₀, clearance), so more only keeps the rest distance.</summary>
        public const float MaxClearance = 0.05f;

        public TransferSettings Clone() => (TransferSettings)MemberwiseClone();

        /// <summary>
        /// A copy within sane bounds (the values come from float fields in the window). rigidIslandSize is not bounded: it only
        /// selects islands, at no cost, and anything above the garment's size means every island (the Rigid preset's 1000).
        /// Non-finite values take the default and −0 becomes +0, so equal settings have equal bits (preset names and fingerprints compare bits).
        /// </summary>
        public TransferSettings Clamped()
        {
            var s = Clone();
            var d = new TransferSettings();
            s.blendRadius = Mathf.Clamp(Finite(s.blendRadius, d.blendRadius), 0.001f, MaxRadius);
            s.fieldRadius = Mathf.Clamp(Finite(s.fieldRadius, d.fieldRadius), 0.001f, MaxRadius);
            s.falloffFull = Mathf.Clamp(Finite(s.falloffFull, d.falloffFull), 0f, MaxDistance);
            s.falloffZero = Mathf.Clamp(Finite(s.falloffZero, d.falloffZero), s.falloffFull, MaxDistance);
            s.hybridNear = Mathf.Clamp(Finite(s.hybridNear, d.hybridNear), 0f, MaxDistance);
            s.hybridFar = Mathf.Clamp(Finite(s.hybridFar, d.hybridFar), s.hybridNear, MaxDistance);
            s.smoothIterations = Mathf.Max(s.smoothIterations, 0);
            s.rigidIslandSize = Finite(s.rigidIslandSize, d.rigidIslandSize);
            s.clearance = Mathf.Clamp(Finite(s.clearance, d.clearance), 0f, MaxClearance);
            return s;
        }

        /// <summary>NaN and infinities take the default, −0 becomes +0: equal settings then have equal bits.</summary>
        private static float Finite(float value, float fallback) => float.IsNaN(value) || float.IsInfinity(value) ? fallback : value == 0f ? 0f : value;

        /// <summary>Millimetres typed in the window as stored metres; division keeps a typed 40 equal to the literal 0.04f.</summary>
        public static float Metres(float mm) => mm / 1000f;

        public static TransferSettings Tight() => new TransferSettings();

        public static TransferSettings Loose() => new TransferSettings
            { mode = SamplerMode.Blend, falloffFull = 0.06f, falloffZero = 0.15f, smoothIterations = 10, clearance = 0.003f };

        public static TransferSettings Skirt() => new TransferSettings
            { mode = SamplerMode.Hybrid, falloffFull = 0.10f, falloffZero = 0.30f, smoothIterations = 20, clearance = 0.003f };

        /// <summary>Every island rigid; 1 km stands in for "no size limit".</summary>
        public static TransferSettings Rigid() => new TransferSettings
            { mode = SamplerMode.Blend, falloffFull = 0.04f, falloffZero = 0.10f, smoothIterations = 5, rigidIslandSize = 1000f, clearance = 0.002f };

        public static readonly (string Name, Func<TransferSettings> Create)[] BuiltIns =
        {
            ("Tight", Tight), ("Loose", Loose), ("Skirt", Skirt), ("Rigid", Rigid),
        };
    }
}
