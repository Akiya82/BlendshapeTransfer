# Changelog

## 1.0.0 - 2026-10-23

First public release.

- Transfers body blendshapes onto clothing, per garment, inner layers first, with anti-clip against the skin and lower garments.
- Rigid parts (rivets, buckles) ride on the cloth; layered sheets of one garment move together and keep their order.
- Transfer computes only garments whose inputs changed; All again computes everything.
- Save binds the outputs to the body with MA Blendshape Sync; Restore puts a garment back on its source mesh.
- English, Russian, Japanese and Simplified Chinese.
