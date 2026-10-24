using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>The built-in presets and the saved TransferPreset assets, and which of them a garment's settings equal.</summary>
    internal static class Presets
    {
        public sealed class Entry
        {
            public string Name;
            public TransferSettings Settings;
            public bool BuiltIn;
        }

        /// <summary>The built-in presets in their order, fresh settings on each call.</summary>
        public static List<Entry> BuiltIns() => TransferSettings.BuiltIns.Select(b => new Entry { Name = b.Name, Settings = b.Create(), BuiltIn = true }).ToList();

        /// <summary>Built-ins in their order, then saved presets by asset path.</summary>
        public static List<Entry> All()
        {
            var list = BuiltIns();
            list.AddRange(AssetDatabase.FindAssets("t:TransferPreset").Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal)
                .Select(AssetDatabase.LoadAssetAtPath<TransferPreset>).Where(p => p != null && p.settings != null)
                .Select(p => new Entry { Name = p.name, Settings = p.settings }));
            return list;
        }

        /// <summary>The first preset whose settings equal these, or null for custom settings.</summary>
        public static Entry Matching(TransferSettings settings, IEnumerable<Entry> all) => all.FirstOrDefault(e => Same(e.Settings, settings));

        public static bool Same(TransferSettings a, TransferSettings b) => Words(a).SequenceEqual(Words(b));

        /// <summary>The settings after Clamped as 32-bit words, every field: the one representation preset names and fingerprints compare.</summary>
        public static IEnumerable<int> Words(TransferSettings settings)
        {
            var s = settings.Clamped();
            yield return (int)s.mode;
            yield return BitConverter.SingleToInt32Bits(s.blendRadius);
            yield return BitConverter.SingleToInt32Bits(s.fieldRadius);
            yield return BitConverter.SingleToInt32Bits(s.hybridNear);
            yield return BitConverter.SingleToInt32Bits(s.hybridFar);
            yield return BitConverter.SingleToInt32Bits(s.falloffFull);
            yield return BitConverter.SingleToInt32Bits(s.falloffZero);
            yield return s.boneFilter ? 1 : 0;
            yield return s.sideFilter ? 1 : 0;
            yield return s.smoothIterations;
            yield return BitConverter.SingleToInt32Bits(s.rigidIslandSize);
            yield return s.antiClip ? 1 : 0;
            yield return BitConverter.SingleToInt32Bits(s.clearance);
        }
    }
}
