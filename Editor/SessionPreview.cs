using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.preview;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>
    /// The Edit preview of a session: hides the real body and garment renderers (and, when asked, the avatar's other renderers) with
    /// forceRenderingOff, holds NDMF's previews off, and draws preview meshes with one body shape at a weight. Dispose writes every
    /// recorded flag back, then lets NDMF preview again.
    /// </summary>
    internal sealed class SessionPreview : IDisposable
    {
        public readonly TransferSession Session;
        private readonly List<(Renderer Renderer, bool Was)> hidden = new List<(Renderer, bool)>();
        private readonly HashSet<Renderer> others = new HashSet<Renderer>();
        /// <summary>The body and garment renderers: hidden for the whole preview, never through SetOthers.</summary>
        private readonly HashSet<Renderer> own = new HashSet<Renderer>();
        /// <summary>By renderer: the body's and every garment's; Undo of a reorder may change the session's order under the preview.</summary>
        private readonly Dictionary<Renderer, PreviewMesh> meshes = new Dictionary<Renderer, PreviewMesh>();
        private readonly Dictionary<Renderer, ShapeResult> shown = new Dictionary<Renderer, ShapeResult>();
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private bool raised;
        private string shape;
        private float weight = float.NaN;
        private bool clipping;
        private bool dotsStale = true;
        /// <summary>The fingerprints the dots were computed under: Not worn with or a reorder changes a garment's colliders without changing its result.</summary>
        private ulong[] dotsFor;

        /// <summary>Per garment renderer, world points of its vertices inside the skin at the weight shown.</summary>
        public readonly Dictionary<Renderer, List<Vector3>> Skin = new Dictionary<Renderer, List<Vector3>>();
        /// <summary>Per garment renderer, world points of its vertices inside a lower layer it collides with at the weight shown.</summary>
        public readonly Dictionary<Renderer, List<Vector3>> Layer = new Dictionary<Renderer, List<Vector3>>();

        public int SkinCount => Skin.Values.Sum(l => l.Count);
        public int LayerCount => Layer.Values.Sum(l => l.Count);

        public SessionPreview(TransferSession session)
        {
            Session = session;
            try
            {
                // raised before anything is hidden, and built from the window's own GUI (outside a Scene View pass), so each recorded flag is
                // the renderer's own, not one an NDMF proxy is about to reset
                raised = true;
                NDMFPreview.DisablePreviewDepth++;
                own.Add(session.Body);
                foreach (var g in session.Garments) own.Add(g.Renderer);
                foreach (var r in own) Hide(r);
                meshes[session.Body] = new PreviewMesh(session.Body, session.Surface.Mesh, session.Surface.Skin, session.Surface.Positions, session.Surface.Weights);
                foreach (var g in session.Garments)
                    meshes[g.Renderer] = new PreviewMesh(g.Renderer, g.Snapshot.Source, g.Snapshot.Skin, g.Snapshot.Positions, g.Snapshot.Weights);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Hides the given renderers too, and shows again (their recorded flag) the ones hidden this way and no longer given.</summary>
        public void SetOthers(IEnumerable<Renderer> renderers)
        {
            var wanted = new HashSet<Renderer>(renderers.Where(r => r != null && !own.Contains(r)));
            foreach (var r in others.Where(r => !wanted.Contains(r)).ToList())
            {
                Restore(r);
                others.Remove(r);
            }
            foreach (var r in wanted)
                if (others.Add(r)) Hide(r);
        }

        /// <summary>
        /// Shows the body shape at weight w. With compute, it first computes the results the garments lack (lower garments first; progress
        /// hears of each garment it computes when more than one correspondence has to be built). It rewrites the meshes whose shape, weight
        /// or result changed, so they follow a dragged weight slider; while a control is held it draws the results it has. The clipping dots
        /// wait for compute (none until then rather than another weight's) and follow any change of the garments' inputs.
        /// </summary>
        public void Show(string name, float w, bool withClipping, bool compute, Action<string, float> progress = null)
        {
            if (compute) Session.EnsureShape(name, progress);
            var moved = name != shape || w != weight;
            if (moved) meshes[Session.Body].Update(Session.BodyOffset(name, w), Session.BodyNormalOffset(name, w));
            var any = moved;
            foreach (var g in Session.Garments)
            {
                g.Results.TryGetValue(name, out var result);
                shown.TryGetValue(g.Renderer, out var before);
                if (!moved && ReferenceEquals(result, before)) continue;
                shown[g.Renderer] = result;
                meshes[g.Renderer].Update(Session.GarmentOffset(g, name, w), Session.GarmentNormalOffset(g, name, w));
                any = true;
            }
            shape = name;
            weight = w;
            if (any || withClipping != clipping) dotsStale = true;
            clipping = withClipping;
            ulong[] fingerprints = null;
            if (withClipping)
            {
                fingerprints = Session.Fingerprints();
                if (dotsFor == null || !dotsFor.SequenceEqual(fingerprints)) dotsStale = true;
            }
            if (!dotsStale) return;
            Skin.Clear();
            Layer.Clear();
            if (!withClipping)
            {
                dotsStale = false;
                return;
            }
            if (!compute) return;
            for (var k = 0; k < Session.Garments.Count; k++)
            {
                var renderer = Session.Garments[k].Renderer;
                var positions = meshes[renderer].Positions;
                var (skin, layer) = Session.ClippingAt(k, name, w);
                Skin[renderer] = skin.Select(v => positions[v]).ToList();
                Layer[renderer] = layer.Select(v => positions[v]).ToList();
            }
            dotsFor = fingerprints;
            dotsStale = false;
        }

        /// <summary>Queues the visible preview meshes for the camera's next render; the draw calls made.</summary>
        public int Draw(Camera camera, Func<Renderer, bool> visible)
        {
            var count = 0;
            foreach (var m in meshes.Values)
                if (visible(m.Renderer)) count += m.Draw(camera, block);
            return count;
        }

        /// <summary>The nearest visible garment the ray hits, the body left out; null on a miss.</summary>
        public SkinnedMeshRenderer Pick(Ray ray, Func<Renderer, bool> visible)
        {
            SkinnedMeshRenderer best = null;
            var nearest = float.MaxValue;
            foreach (var g in Session.Garments)
            {
                if (!visible(g.Renderer) || !meshes[g.Renderer].Raycast(ray, out var distance) || distance >= nearest) continue;
                nearest = distance;
                best = g.Renderer;
            }
            return best;
        }

        /// <summary>
        /// Hides the recorded renderers again before a Scene View render: a camera NDMF previews for outside the depth (MA's fit
        /// preview window) writes false back to them after its own render.
        /// </summary>
        public void Reassert()
        {
            foreach (var (renderer, _) in hidden)
                if (renderer != null) renderer.forceRenderingOff = true;
        }

        /// <summary>The NDMF depth once this preview's raise is taken back: never below 0, since NDMF resets it on leaving play mode.</summary>
        internal static int Lowered(int depth) => Math.Max(0, depth - 1);

        private void Hide(Renderer renderer)
        {
            if (renderer == null || hidden.Any(h => ReferenceEquals(h.Renderer, renderer))) return;
            hidden.Add((renderer, renderer.forceRenderingOff));
            renderer.forceRenderingOff = true;
        }

        private void Restore(Renderer renderer)
        {
            var i = hidden.FindIndex(h => ReferenceEquals(h.Renderer, renderer));
            if (i < 0) return;
            if (renderer != null) renderer.forceRenderingOff = hidden[i].Was;
            hidden.RemoveAt(i);
        }

        public void Dispose()
        {
            try
            {
                foreach (var (renderer, was) in hidden)
                    if (renderer != null) renderer.forceRenderingOff = was;
                hidden.Clear();
                others.Clear();
                foreach (var m in meshes.Values) m.Dispose();
                meshes.Clear();
            }
            finally
            {
                // the flags first: NDMF previews again only once they are back
                if (raised) NDMFPreview.DisablePreviewDepth = Lowered(NDMFPreview.DisablePreviewDepth);
                raised = false;
            }
        }
    }
}
