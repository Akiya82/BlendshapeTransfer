# Changelog

## 1.1.0 - 2026-10-26

- New Edit tab: previews a body shape at any weight in the Scene View, on the avatar, with the garments' transferred shapes. The renderers' own weights do not change.
  - Show clipping marks garment vertices inside the skin (red) or inside a lower garment (orange), and skin that comes through a garment (red).
  - Hide other meshes, an eye per layer, and a click on a garment to make it the current one.
- Anti-clip also checks the skin against the fabric. Skin that passes between sparse garment vertices, such as a nipple under a top, lifts the fabric. Over skin a shape moves, the fabric keeps at least 0.2 mm, or the skin's own motion if that is smaller.
- A shape's reach also counts the skin under a garment, so a small shape between a garment's vertices reaches it.
- The report counts skin left outside a garment as still clipping into the skin.
- Outputs made with 1.0.0 need a transfer: the pipeline changed.
- Requires NDMF 1.14.7 or later; Modular Avatar installs it.

## 1.0.0 - 2026-10-23

First public release.

- Transfers body blendshapes onto clothing, per garment, inner layers first, with anti-clip against the skin and lower garments.
- Rigid parts (rivets, buckles) ride on the cloth; layered sheets of one garment move together and keep their order.
- Transfer computes only garments whose inputs changed; All again computes everything.
- Save binds the outputs to the body with MA Blendshape Sync; Restore puts a garment back on its source mesh.
- English, Russian, Japanese and Simplified Chinese.
