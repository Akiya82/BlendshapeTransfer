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
        [SerializeField] private string previewShape;
        [SerializeField] private float previewWeight = 100f;
        [SerializeField] private bool showClipping;
        [SerializeField] private bool hideOthers;
        private SessionPreview preview;
        /// <summary>Per renderer of the preview, whether it is drawn; from the scene when the preview starts.</summary>
        private readonly Dictionary<Renderer, bool> eyes = new Dictionary<Renderer, bool>();

        internal SessionPreview Preview => preview;

        internal bool HideOthers
        {
            get => hideOthers;
            set
            {
                hideOthers = value;
                preview?.SetOthers(value ? OtherRenderers() : Enumerable.Empty<Renderer>());
                SceneView.RepaintAll();
            }
        }

        /// <summary>
        /// Starts the preview while the Edit tab shows read meshes, and ends it otherwise or when the session was replaced. It runs in the
        /// window's own GUI, never inside a Scene View's, where NDMF's pending reset would overwrite the flags just set.
        /// </summary>
        private void SyncPreview()
        {
            var wanted = tab == EditTab && session != null;
            if (preview != null && (!wanted || preview.Session != session)) EndPreview();
            if (!wanted || preview != null) return;
            preview = new SessionPreview(session);
            eyes.Clear();
            eyes[session.Body] = Shown(session.Body);
            foreach (var g in session.Garments) eyes[g.Renderer] = Shown(g.Renderer);
            preview.SetOthers(hideOthers ? OtherRenderers() : Enumerable.Empty<Renderer>());
            SceneView.RepaintAll();

            static bool Shown(Renderer r) => r.enabled && r.gameObject.activeInHierarchy;
        }

        private void EndPreview()
        {
            if (preview == null) return;
            preview.Dispose();
            preview = null;
            SceneView.RepaintAll();
        }

        /// <summary>The avatar's renderers outside the session, which Hide other meshes hides.</summary>
        private IEnumerable<Renderer> OtherRenderers() =>
            session.Avatar.GetComponentsInChildren<Renderer>(true).Where(r => r != session.Body && session.Garments.All(g => g.Renderer != r)).ToList();

        private bool Visible(Renderer r) => eyes.TryGetValue(r, out var shown) && shown;

        private void DrawEditTab()
        {
            Mark("edit");
            if (session == null)
            {
                foreach (var e in errors) EditorGUILayout.HelpBox(L.Text(e), MessageType.Error);
                DrawReadPrompt();
                return;
            }
            if (poseChanged)
            {
                // the preview keeps showing the read meshes
                EditorGUILayout.HelpBox(L.Get("garments.poseChanged"), MessageType.Warning);
                if (GUILayout.Button(L.Content("garments.readAgain")))
                {
                    Read();
                    GUIUtility.ExitGUI();
                }
            }
            DrawPreviewControls();
            DrawLayers();
            if (Current() != null) DrawSettingsSection();
        }

        private void DrawPreviewControls()
        {
            var names = PreviewShapes();
            if (names.Count == 0)
            {
                EditorGUILayout.HelpBox(L.Get("edit.noShapes"), MessageType.Info);
                return;
            }
            var labels = PreviewLabels(names).Select(l => new GUIContent(l)).ToArray();
            EditorGUI.BeginChangeCheck();
            var index = EditorGUILayout.Popup(L.Content("edit.shape"), Math.Max(0, names.IndexOf(previewShape)), labels);
            var weight = EditorGUILayout.Slider(L.Content("edit.weight"), previewWeight, 0f, 100f);
            var clipping = EditorGUILayout.ToggleLeft(L.Content("edit.showClipping"), showClipping);
            if (showClipping && preview != null) EditorGUILayout.LabelField(L.Format("edit.clippingCounts", preview.SkinCount, preview.LayerCount), EditorStyles.miniLabel);
            var others = EditorGUILayout.ToggleLeft(L.Content("edit.hideOthers"), hideOthers);
            if (!EditorGUI.EndChangeCheck()) return;
            previewShape = names[index];
            previewWeight = weight;
            if (others != hideOthers) HideOthers = others;
            SceneView.RepaintAll();
            if (clipping == showClipping) return;
            // the counts line comes or goes
            showClipping = clipping;
            GUIUtility.ExitGUI();
        }

        /// <summary>The body shapes ticked on at least one garment, in body order, separators left out.</summary>
        private List<string> PreviewShapes()
        {
            var mesh = session.Surface.Mesh;
            var result = new List<string>();
            for (var s = 0; s < mesh.blendShapeCount; s++)
            {
                var name = mesh.GetBlendShapeName(s);
                if (!ShapeGroups.IsSeparator(name) && session.Garments.Any(g => g.Data.Entry(name) is ShapeEntry e && e.enabled)) result.Add(name);
            }
            return result;
        }

        /// <summary>The names as the popup shows them: under their separator's title, as section 3 groups them.</summary>
        private string[] PreviewLabels(List<string> names)
        {
            var mesh = session.Surface.Mesh;
            var all = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToList();
            var titles = new Dictionary<string, string>();
            foreach (var group in ShapeGroups.Of(all))
                foreach (var s in group.Shapes)
                    titles[all[s]] = group.Title;
            return names.Select(n => titles.TryGetValue(n, out var t) && t != null ? $"{Plain(t)}/{Plain(n)}" : Plain(n)).ToArray();

            // a slash would open a submenu of its own
            static string Plain(string s) => s.Replace('/', '∕');
        }

        private void DrawLayers()
        {
            EditorGUILayout.LabelField(L.Content("edit.layers"), EditorStyles.boldLabel);
            LayerRow(session.Body, false);
            foreach (var g in session.Garments) LayerRow(g.Renderer, true);
        }

        private void LayerRow(Renderer renderer, bool garment)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var shown = Visible(renderer);
                var eye = new GUIContent(EditorGUIUtility.IconContent(shown ? "animationvisibilitytoggleon" : "animationvisibilitytoggleoff").image, L.Get("edit.eye"));
                if (GUILayout.Button(eye, EditorStyles.label, GUILayout.Width(22f)))
                {
                    eyes[renderer] = !shown;
                    SceneView.RepaintAll();
                }
                var style = garment && renderer == current ? EditorStyles.boldLabel : EditorStyles.label;
                if (GUILayout.Button(renderer.name, style) && garment && renderer != current)
                {
                    current = (SkinnedMeshRenderer)renderer;
                    GUIUtility.ExitGUI();
                }
            }
        }
    }
}
