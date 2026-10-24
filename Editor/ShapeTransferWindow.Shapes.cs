using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using L = Akiya82.BlendshapeTransfer.Localization;

namespace Akiya82.BlendshapeTransfer
{
    internal sealed partial class ShapeTransferWindow
    {
        /// <summary>Unticked shapes whose reach shows as 0.0 mm are hidden.</summary>
        private const float HideBelow = 0.00005f;

        /// <summary>The body's shape names in mesh order, and the content hash of the body they were read from.</summary>
        private List<string> shapeNames;
        private ulong? shapesOf;
        /// <summary>The garment's entries by name for this pass (the first of a name wins, as in TransferData.Entry).</summary>
        private readonly Dictionary<string, ShapeEntry> entries = new Dictionary<string, ShapeEntry>();
        /// <summary>The widest reach, base offset and on-garment cells, so every row's columns line up.</summary>
        private float reachWidth, offsetWidth, overwriteWidth;

        partial void DrawShapesSection()
        {
            var g = Current();
            if (shapesOf != session.BodyContent)
            {
                var mesh = session.Surface.Mesh;
                shapeNames = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToList();
                groups = ShapeGroups.Of(shapeNames);
                shapesOf = session.BodyContent;
            }
            var names = shapeNames;
            entries.Clear();
            foreach (var e in g.Data.shapes) entries.TryAdd(e.name, e);
            var ticked = groups.Sum(group => group.Shapes.Count(i => Ticked(names[i])));
            if (!Section("shapes", Title("section.shapes", g.Renderer.name, ticked))) return;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                search = EditorGUILayout.TextField(L.Content("shapes.search"), search);
                var only = L.Content("shapes.reachOnly");
                using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(search)))
                    reachOnly = EditorGUILayout.ToggleLeft(only, reachOnly, Width(only, EditorStyles.label, 24f));
                if (EditorGUI.EndChangeCheck()) GUIUtility.ExitGUI();
            }
            reachWidth = TextWidth(L.Format("shapes.reach", 888.8f), EditorStyles.label);
            offsetWidth = TextWidth(L.Format("shapes.baseOffset", 888.8f), EditorStyles.miniLabel);
            overwriteWidth = 18f + TextWidth(L.Get("shapes.overwrite"), GUI.skin.toggle);
            var any = false;
            foreach (var group in groups)
            {
                var shown = group.Shapes.Where(i => Shown(g, names[i])).ToList();
                if (shown.Count == 0) continue;
                any = true;
                var titled = group.Title != null;
                if (titled && !GroupHeader(g, group, shown, names)) continue;
                if (titled) EditorGUI.indentLevel++;
                foreach (var i in shown) ShapeRow(g, i, names[i]);
                if (titled) EditorGUI.indentLevel--;
            }
            if (!any) EditorGUILayout.LabelField(L.Get(string.IsNullOrEmpty(search) ? "shapes.empty" : "shapes.emptySearch"), EditorStyles.miniLabel);
        }

        private static float TextWidth(string text, GUIStyle style) => style.CalcSize(new GUIContent(text)).x + 8f;

        private bool Ticked(string name) => entries.TryGetValue(name, out var e) && e.enabled;

        /// <summary>The search shows its matches among every shape; otherwise ticked shapes, and unticked ones that reach when asked.</summary>
        private bool Shown(GarmentState g, string name)
        {
            if (!string.IsNullOrEmpty(search)) return name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
            return Ticked(name) || !reachOnly || Reach(g, name) >= HideBelow;
        }

        private static float Reach(GarmentState g, string name) => g.Reach.TryGetValue(name, out var reach) ? reach : 0f;

        /// <summary>Ticks or unticks the shapes, one Undo step.</summary>
        internal static void SetTicked(TransferData data, IEnumerable<string> names, bool on)
        {
            Undo.RecordObject(data, L.Get("undo.tick"));
            foreach (var name in names) data.GetOrAddEntry(name).enabled = on;
            EditorUtility.SetDirty(data);
        }

        /// <summary>Overwrite says how to write a shape, not whether to: a shape without an entry stays unticked.</summary>
        internal static void SetOverwrite(TransferData data, string name, bool on)
        {
            Undo.RecordObject(data, L.Get("undo.overwrite"));
            var entry = data.Entry(name);
            if (entry == null)
            {
                entry = data.GetOrAddEntry(name);
                entry.enabled = false;
            }
            entry.overwrite = on;
            EditorUtility.SetDirty(data);
        }

        /// <summary>The group's fold with "ticked of shown", and a tick for all or none of its shown shapes (mixed shows a dash).</summary>
        private bool GroupHeader(GarmentState g, ShapeGroup group, List<int> shown, List<string> names)
        {
            var ticked = shown.Count(i => Ticked(names[i]));
            bool open;
            using (new EditorGUILayout.HorizontalScope())
            {
                open = Fold("Group." + group.Key, new GUIContent(L.Format("shapes.group", group.Title, ticked, shown.Count), L.Tip("shapes.group")));
                EditorGUI.showMixedValue = ticked > 0 && ticked < shown.Count;
                EditorGUI.BeginChangeCheck();
                var all = EditorGUILayout.ToggleLeft(new GUIContent("", L.Get("shapes.groupTick")), ticked == shown.Count, GUILayout.Width(18f));
                EditorGUI.showMixedValue = false;
                if (EditorGUI.EndChangeCheck())
                {
                    SetTicked(g.Data, shown.Select(i => names[i]), all);
                    GUIUtility.ExitGUI();
                }
            }
            return open;
        }

        private void ShapeRow(GarmentState g, int index, string name)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var on = EditorGUILayout.ToggleLeft(name, Ticked(name), GUILayout.MinWidth(140f));
                if (EditorGUI.EndChangeCheck())
                {
                    SetTicked(g.Data, new[] { name }, on);
                    GUIUtility.ExitGUI();
                }
                var reach = new GUIContent(L.Format("shapes.reach", Reach(g, name) * 1000f), L.Tip("shapes.reach"));
                GUILayout.Label(reach, GUILayout.Width(reachWidth));
                var weight = session.Surface.Weights[index];
                if (weight != 0f)
                {
                    var offset = new GUIContent(L.Format("shapes.baseOffset", weight), L.Tip("shapes.baseOffset"));
                    GUILayout.Label(offset, EditorStyles.miniLabel, GUILayout.Width(offsetWidth));
                }
                else GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.miniLabel, GUILayout.Width(offsetWidth));
                if (g.Snapshot.Source.GetBlendShapeIndex(name) < 0)
                {
                    GUILayoutUtility.GetRect(GUIContent.none, GUI.skin.label, GUILayout.Width(18f));
                    GUILayoutUtility.GetRect(GUIContent.none, GUI.skin.toggle, GUILayout.Width(overwriteWidth - 18f));
                    return;
                }
                GUILayout.Label(Icon("console.infoicon.sml", "shapes.onGarment"), GUILayout.Width(18f));
                var label = L.Content("shapes.overwrite");
                EditorGUI.BeginChangeCheck();
                var overwrite = GUILayout.Toggle(entries.TryGetValue(name, out var entry) && entry.overwrite, label, GUILayout.Width(overwriteWidth - 18f));
                if (!EditorGUI.EndChangeCheck()) return;
                SetOverwrite(g.Data, name, overwrite);
                GUIUtility.ExitGUI();
            }
        }
    }
}
