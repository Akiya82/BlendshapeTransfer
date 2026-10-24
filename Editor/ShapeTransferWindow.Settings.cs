using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using L = Akiya82.BlendshapeTransfer.Localization;
using Object = UnityEngine.Object;

namespace Akiya82.BlendshapeTransfer
{
    internal sealed partial class ShapeTransferWindow
    {
        partial void DrawSettingsSection()
        {
            var g = Current();
            if (!Section("settings", Title("section.settings", g.Renderer.name))) return;
            var data = g.Data;
            var s = data.settings;
            DrawPreset(data);

            Edit(data, () => (SamplerMode)EditorGUILayout.EnumPopup(L.Content("settings.mode"), s.mode), v => s.mode = v, true);
            if (s.mode == SamplerMode.Blend) Edit(data, () => Mm("settings.blendRadius", s.blendRadius), v => s.blendRadius = v);
            if (s.mode == SamplerMode.Field || s.mode == SamplerMode.Hybrid) Edit(data, () => Mm("settings.fieldRadius", s.fieldRadius), v => s.fieldRadius = v);
            Edit(data, () => EditorGUILayout.IntSlider(L.Content("settings.smoothing"), s.smoothIterations, 0, 50), v => s.smoothIterations = v);
            Edit(data, () => EditorGUILayout.Toggle(L.Content("settings.antiClip"), s.antiClip), v => s.antiClip = v, true);
            if (s.antiClip) Edit(data, () => Mm("settings.clearance", s.clearance), v => s.clearance = v);

            if (!Fold("Advanced", L.Content("settings.advanced"), openByDefault: false)) return;
            EditorGUI.indentLevel++;
            Edit(data, () => Mm("settings.falloffFull", s.falloffFull), v => s.falloffFull = v);
            Edit(data, () => Mm("settings.falloffZero", s.falloffZero), v => s.falloffZero = v);
            if (s.mode == SamplerMode.Hybrid)
            {
                Edit(data, () => Mm("settings.hybridNear", s.hybridNear), v => s.hybridNear = v);
                Edit(data, () => Mm("settings.hybridFar", s.hybridFar), v => s.hybridFar = v);
            }
            Edit(data, () => EditorGUILayout.Toggle(L.Content("settings.boneFilter"), s.boneFilter), v => s.boneFilter = v);
            if (s.mode != SamplerMode.Field) Edit(data, () => EditorGUILayout.Toggle(L.Content("settings.sideFilter"), s.sideFilter), v => s.sideFilter = v);
            Edit(data, () => Mm("settings.rigidSize", s.rigidIslandSize), v => s.rigidIslandSize = v);
            DrawNotWornWith(g);
            EditorGUILayout.Space();
            // a copy of another avatar's data is not refitted for good: the next read copies the original again
            using (new EditorGUI.DisabledScope(!AssetDatabase.Contains(data) || data.baseWeights.Count == 0 && data.garmentWeights.Count == 0))
            {
                if (GUILayout.Button(L.Content("settings.refit")))
                {
                    Refit(g);
                    GUIUtility.ExitGUI();
                }
            }
            foreach (var m in session.BaseWeightInfo(g)) EditorGUILayout.HelpBox(L.Text(m), MessageType.Info);
            EditorGUI.indentLevel--;
        }

        /// <summary>The current preset (or Custom); picking one applies it; a built-in one gets a line of description.</summary>
        private void DrawPreset(TransferData data)
        {
            var s = data.settings;
            presets ??= Presets.All();
            var match = Presets.Matching(s, presets);
            var names = presets.Select(p => p.Name).ToList();
            if (match == null) names.Add(L.Get("preset.custom"));
            var index = match == null ? names.Count - 1 : presets.IndexOf(match);
            using (new EditorGUILayout.HorizontalScope())
            {
                var pick = EditorGUILayout.Popup(L.Content("settings.preset"), index, names.Select(n => new GUIContent(n)).ToArray());
                if (pick != index && pick < presets.Count)
                {
                    Undo.RecordObject(data, L.Get("undo.preset"));
                    data.settings = presets[pick].Settings.Clone();
                    EditorUtility.SetDirty(data);
                    GUIUtility.ExitGUI();
                }
                var saveAs = L.Content("settings.saveAs");
                if (GUILayout.Button(saveAs, Width(saveAs, GUI.skin.button)))
                {
                    SavePreset(s);
                    GUIUtility.ExitGUI();
                }
            }
            if (match != null && match.BuiltIn) EditorGUILayout.LabelField(L.Get($"preset.{match.Name}.desc"), EditorStyles.wordWrappedMiniLabel);
        }

        /// <summary>The name of the preset the settings equal, or Custom.</summary>
        private string PresetName(TransferSettings settings)
        {
            presets ??= Presets.All();
            return Presets.Matching(settings, presets)?.Name ?? L.Get("preset.custom");
        }

        /// <summary>Ticks for the lower garments this one is never shown with; the relation is mutual, either garment's record counts.</summary>
        private void DrawNotWornWith(GarmentState g)
        {
            var k = session.Garments.IndexOf(g);
            if (k == 0) return;
            EditorGUILayout.LabelField(L.Content("settings.notWornWith"));
            EditorGUI.indentLevel++;
            for (var j = 0; j < k; j++)
            {
                var lower = session.Garments[j];
                EditorGUI.BeginChangeCheck();
                var now = EditorGUILayout.ToggleLeft(lower.Renderer.name, !session.Collides(k, j));
                if (!EditorGUI.EndChangeCheck()) continue;
                Undo.RecordObjects(new Object[] { g.Data, lower.Data }, L.Get("undo.settings"));
                if (now) g.Data.notWornWith.Add(lower.Data.rendererPath);
                else
                {
                    g.Data.notWornWith.Remove(lower.Data.rendererPath);
                    lower.Data.notWornWith.Remove(g.Data.rendererPath);
                }
                EditorUtility.SetDirty(g.Data);
                EditorUtility.SetDirty(lower.Data);
            }
            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// Draws one control and stores its value only when the owner changed it, so untouched fields keep their exact bits. A change
        /// that adds or removes controls (structural) ends the event.
        /// </summary>
        private void Edit<T>(TransferData data, Func<T> draw, Action<T> set, bool structural = false)
        {
            EditorGUI.BeginChangeCheck();
            var value = draw();
            if (!EditorGUI.EndChangeCheck()) return;
            Undo.RecordObject(data, L.Get("undo.settings"));
            set(value);
            EditorUtility.SetDirty(data);
            if (!structural) return;
            GUIUtility.keyboardControl = 0;
            GUIUtility.ExitGUI();
        }

        /// <summary>A length shown and typed in millimetres; the result is metres, by division.</summary>
        private static float Mm(string key, float metres) => TransferSettings.Metres(EditorGUILayout.FloatField(L.Content(key), metres * 1000f));

        private void SavePreset(TransferSettings settings)
        {
            var title = L.Get("settings.savePresetTitle");
            var path = EditorUtility.SaveFilePanelInProject(title, "Transfer Preset", "asset", title);
            if (string.IsNullOrEmpty(path)) return;
            var name = Path.GetFileNameWithoutExtension(path);
            if (TransferSettings.BuiltIns.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) || string.Equals(name, L.Get("preset.custom"), StringComparison.OrdinalIgnoreCase))
            {
                ShowNotification(new GUIContent(L.Format("settings.presetNameTaken", name)));
                return;
            }
            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing == null)
            {
                var created = CreateInstance<TransferPreset>();
                created.settings = settings.Clone();
                AssetDatabase.CreateAsset(created, path);
            }
            else if (existing is TransferPreset preset)
            {
                // keeps the asset's GUID
                preset.settings = settings.Clone();
                EditorUtility.SetDirty(preset);
                AssetDatabase.SaveAssetIfDirty(preset);
            }
            else
            {
                ShowNotification(new GUIContent(L.Format("settings.presetNotAPreset", path)));
                return;
            }
            presets = null;
        }

        private void Refit(GarmentState g)
        {
            if (!EditorUtility.DisplayDialog(L.Get("refit.title"), L.Format("refit.text", g.Renderer.name), L.Get("refit.ok"), L.Get("refit.cancel"))) return;
            session.Refit(g);
            Drop(null);
        }
    }
}
