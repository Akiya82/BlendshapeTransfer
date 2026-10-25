using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using L = Akiya82.BlendshapeTransfer.Localization;

namespace Akiya82.BlendshapeTransfer
{
    internal sealed partial class ShapeTransferWindow
    {
        private const float RowInfo = 150f;

        private void DrawGarmentsSection()
        {
            if (Section("garments", Title("section.garments"))) DrawGarments();
            // blocking errors show even with the section folded
            foreach (var e in errors) EditorGUILayout.HelpBox(L.Text(e), MessageType.Error);
        }

        private void DrawGarments()
        {
            EditorGUI.BeginChangeCheck();
            var newBody = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(L.Content("garments.body"), body, typeof(SkinnedMeshRenderer), true);
            if (EditorGUI.EndChangeCheck())
            {
                body = newBody;
                Drop(null);
                GUIUtility.ExitGUI();
            }
            list.index = garments.IndexOf(current);
            // Read sorts the garments by their stored layers, so an order dragged before it would be lost
            list.draggable = session != null;
            list.DoLayoutList();
            if (listChanged)
            {
                listChanged = false;
                GUIUtility.ExitGUI();
            }
            var avatar = body != null ? AvatarPaths.RootOf(body.transform) : null;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L.Content("garments.addSelected")))
                {
                    AddGarments(Selection.gameObjects.Select(o => o.GetComponent<SkinnedMeshRenderer>()), "garments.noneSelected");
                    GUIUtility.ExitGUI();
                }
                using (new EditorGUI.DisabledScope(avatar == null))
                {
                    if (GUILayout.Button(L.Content("garments.addSaved")))
                    {
                        AddGarments(OutputWriter.WithSavedData(avatar), "garments.noneSaved");
                        GUIUtility.ExitGUI();
                    }
                }
            }
            if (session == null)
            {
                DrawReadPrompt();
                return;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(L.Format("garments.readCount", session.Garments.Count));
                var again = L.Content("garments.readAgain");
                if (GUILayout.Button(again, Width(again, GUI.skin.button)))
                {
                    Read();
                    GUIUtility.ExitGUI();
                }
            }
            if (poseChanged) EditorGUILayout.HelpBox(L.Get("garments.poseChanged"), MessageType.Warning);
        }

        /// <summary>Why the meshes are not read (when the owner did not drop them) and the button that reads them; the Edit tab shows it too.</summary>
        private void DrawReadPrompt()
        {
            if (readWanted) EditorGUILayout.HelpBox(L.Get("garments.reloadDropped"), MessageType.Info);
            if (droppedBy != null) EditorGUILayout.HelpBox(L.Get(droppedBy), MessageType.Warning);
            if (GUILayout.Button(L.Content(readWanted ? "garments.readAgain" : "garments.read")))
            {
                Read();
                GUIUtility.ExitGUI();
            }
        }

        private void DrawGarmentRow(Rect rect, int index, bool active, bool focused)
        {
            rect.y += 1f;
            rect.height = EditorGUIUtility.singleLineHeight;
            var e = Event.current;
            // the renderer field takes the mouse-down, so the row is selected before it is drawn
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition) && garments[index] != current)
            {
                current = garments[index];
                listChanged = true;
            }
            var k = session == null ? -1 : session.Garments.FindIndex(g => g.Renderer == garments[index]);
            var info = k >= 0 && k < fresh.Length ? RowInfo : 0f;
            EditorGUI.BeginChangeCheck();
            var value = (SkinnedMeshRenderer)EditorGUI.ObjectField(new Rect(rect.x, rect.y, rect.width - info, rect.height), garments[index], typeof(SkinnedMeshRenderer), true);
            if (EditorGUI.EndChangeCheck())
            {
                if (garments[index] == current) current = value;
                garments[index] = value;
                Drop(null);
                listChanged = true;
                return;
            }
            if (info == 0f) return;
            var g = session.Garments[k];
            var preset = PresetName(g.Data.settings);
            var x = rect.xMax - info + 4f;
            GUI.Label(new Rect(x, rect.y, 90f, rect.height), new GUIContent(preset, L.Tip("garments.preset")), EditorStyles.miniLabel);
            GUI.Label(new Rect(x + 92f, rect.y, 20f, rect.height), fresh[k] ? Icon("TestPassed", "garments.upToDate") : Icon("d_Refresh", "garments.stale"));
            if (g.WarningCount > 0)
                GUI.Label(new Rect(x + 112f, rect.y, 36f, rect.height), new GUIContent(g.WarningCount.ToString(), EditorGUIUtility.IconContent("console.warnicon.sml").image, L.Get("garments.warnings")));
        }

        private void AddGarments(IEnumerable<SkinnedMeshRenderer> candidates, string none)
        {
            var count = garments.Count;
            foreach (var r in candidates.Where(r => r != null && r != body && !garments.Contains(r)).ToList()) garments.Add(r);
            if (garments.Count > count) Drop(null);
            else ShowNotification(new GUIContent(L.Get(none)));
        }
    }
}
