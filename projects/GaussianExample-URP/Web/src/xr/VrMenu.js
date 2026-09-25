// In-headset menu: a world-locked panel drawn on a canvas. Opened with X / Y
// on the left controller, pointed at with either controller's ray, trigger to
// pick. World-locked (placed in front of you when opened) rather than
// hand-attached, so it holds still while you read it.

import * as THREE from 'three';

const W = 1024, H = 720;
const PANEL_W = 0.62; // metres
const PANEL_H = (PANEL_W * H) / W;

export class VrMenu {
  // items(): [{ id, kind: 'scene'|'action', label, sub, thumb, active }]
  constructor({ items, onPick }) {
    this.items = items;
    this.onPick = onPick;
    this.canvas = document.createElement('canvas');
    this.canvas.width = W;
    this.canvas.height = H;
    this.ctx = this.canvas.getContext('2d');
    this.texture = new THREE.CanvasTexture(this.canvas);
    this.texture.colorSpace = THREE.SRGBColorSpace;
    this.mesh = new THREE.Mesh(
      new THREE.PlaneGeometry(PANEL_W, PANEL_H),
      new THREE.MeshBasicMaterial({ map: this.texture, transparent: true, depthTest: false }),
    );
    this.mesh.renderOrder = 1e4;
    this.mesh.visible = false;
    this.mesh.name = 'VR Menu';
    this.hover = null;
    this.hoverBy = { left: null, right: null };
    this._rects = [];
    this._images = new Map();
  }

  get open() { return this.mesh.visible; }

  // Place ~0.8 m in front of the head, a little below eye level, facing it.
  show(headPos, headYaw) {
    const fwd = new THREE.Vector3(-Math.sin(headYaw), 0, -Math.cos(headYaw));
    this.mesh.position.copy(headPos).addScaledVector(fwd, 0.8);
    this.mesh.position.y -= 0.12;
    this.mesh.rotation.set(-0.12, headYaw, 0, 'YXZ');
    this.mesh.visible = true;
    this.hover = null;
    this.hoverBy = { left: null, right: null };
    this.redraw();
  }

  hide() {
    this.mesh.visible = false;
    this.hover = null;
  }

  // Ray hover for one hand; returns hit distance or null. Each hand keeps its
  // own hover so the trigger picks what that hand points at; the highlight
  // follows the right hand when both are on the panel.
  pointer(hand, raycaster) {
    if (!this.open) return null;
    const hit = raycaster.intersectObject(this.mesh, false)[0];
    let id = null;
    if (hit?.uv) {
      const x = hit.uv.x * W, y = (1 - hit.uv.y) * H;
      id = this._rects.find((r) => x >= r.x && x <= r.x + r.w && y >= r.y && y <= r.y + r.h)?.id ?? null;
    }
    this.hoverBy[hand] = id;
    const shown = this.hoverBy.right ?? this.hoverBy.left ?? null;
    if (shown !== this.hover) {
      this.hover = shown;
      this.redraw();
    }
    return hit ? hit.distance : null;
  }

  // World position of an item's centre (tests aim the emulated controller at it).
  worldPointOf(id) {
    const r = this._rects.find((x) => x.id === id);
    if (!r) return null;
    const local = new THREE.Vector3(((r.x + r.w / 2) / W - 0.5) * PANEL_W, (0.5 - (r.y + r.h / 2) / H) * PANEL_H, 0);
    this.mesh.updateMatrixWorld(true);
    return this.mesh.localToWorld(local);
  }

  select(hand) {
    const id = (hand && this.hoverBy[hand]) || this.hover;
    if (this.open && id) this.onPick(id);
  }

  redraw() {
    const c = this.ctx;
    const items = this.items();
    this._rects = [];
    c.clearRect(0, 0, W, H);
    roundRect(c, 0, 0, W, H, 36);
    c.fillStyle = 'rgba(13,17,23,0.94)';
    c.fill();
    c.strokeStyle = 'rgba(255,255,255,0.12)';
    c.lineWidth = 2;
    c.stroke();

    // Header.
    c.fillStyle = '#e8ecf1';
    c.font = '700 40px system-ui, "PingFang SC", "Microsoft YaHei", sans-serif';
    c.fillText('UkemiXR', 48, 78);
    c.fillStyle = '#8f9aab';
    c.font = '400 26px system-ui, "PingFang SC", "Microsoft YaHei", sans-serif';
    c.fillText('Scenes', 232, 76);
    c.fillStyle = '#4fd1c5';
    c.beginPath();
    c.arc(W - 64, 64, 9, 0, Math.PI * 2);
    c.fill();

    // Scene cards.
    const scenes = items.filter((i) => i.kind === 'scene');
    const cardW = 290, cardH = 250, gap = 24, top = 120;
    scenes.slice(0, 3).forEach((it, k) => {
      const x = 48 + k * (cardW + gap), y = top;
      const hov = this.hover === it.id;
      roundRect(c, x, y, cardW, cardH, 22);
      c.fillStyle = hov ? 'rgba(79,209,197,0.18)' : 'rgba(255,255,255,0.05)';
      c.fill();
      c.lineWidth = it.active ? 4 : 2;
      c.strokeStyle = it.active ? '#4fd1c5' : hov ? 'rgba(79,209,197,0.6)' : 'rgba(255,255,255,0.1)';
      c.stroke();
      const img = this._image(it.thumb);
      c.save();
      roundRect(c, x + 12, y + 12, cardW - 24, 150, 14);
      c.clip();
      if (img?.complete && img.naturalWidth) drawCover(c, img, x + 12, y + 12, cardW - 24, 150);
      else { c.fillStyle = '#1b222c'; c.fillRect(x + 12, y + 12, cardW - 24, 150); }
      c.restore();
      c.fillStyle = '#e8ecf1';
      c.font = '600 28px system-ui, "PingFang SC", "Microsoft YaHei", sans-serif';
      c.fillText(ellipsize(c, it.label, cardW - 36), x + 18, y + 202);
      c.fillStyle = it.active ? '#4fd1c5' : '#8f9aab';
      c.font = '400 22px system-ui, "PingFang SC", "Microsoft YaHei", sans-serif';
      c.fillText(it.active ? 'Now showing' : it.sub || '', x + 18, y + 234);
      this._rects.push({ id: it.id, x, y, w: cardW, h: cardH });
    });

    // Actions.
    const actions = items.filter((i) => i.kind === 'action');
    const bw = (W - 96 - gap * (actions.length - 1)) / Math.max(1, actions.length), by = 420, bh = 96;
    actions.forEach((it, k) => {
      const x = 48 + k * (bw + gap);
      const hov = this.hover === it.id;
      roundRect(c, x, by, bw, bh, 20);
      c.fillStyle = it.primary ? (hov ? '#6ee0d5' : '#4fd1c5') : hov ? 'rgba(79,209,197,0.18)' : 'rgba(255,255,255,0.06)';
      c.fill();
      c.fillStyle = it.primary ? '#062422' : '#e8ecf1';
      c.font = '600 28px system-ui, "PingFang SC", "Microsoft YaHei", sans-serif';
      c.textAlign = 'center';
      c.fillText(it.label, x + bw / 2, by + 46);
      c.fillStyle = it.primary ? '#0b3b37' : '#8f9aab';
      c.font = '400 20px system-ui, "PingFang SC", "Microsoft YaHei", sans-serif';
      c.fillText(it.sub || '', x + bw / 2, by + 76);
      c.textAlign = 'left';
      this._rects.push({ id: it.id, x, y: by, w: bw, h: bh });
    });

    // Controls legend.
    c.fillStyle = '#8f9aab';
    c.font = '400 22px system-ui, "PingFang SC", "Microsoft YaHei", sans-serif';
    const legend = ['Left stick   walk (third person)', 'Right stick   snap turn 35°', 'X / Y   open / close menu', 'Trigger   select'];
    legend.forEach((t, k) => c.fillText(t, 48 + (k % 2) * 470, 588 + Math.floor(k / 2) * 42));
    this.texture.needsUpdate = true;
  }

  _image(url) {
    if (!url) return null;
    let img = this._images.get(url);
    if (!img) {
      img = new Image();
      img.onload = () => this.open && this.redraw();
      img.src = url;
      this._images.set(url, img);
    }
    return img;
  }
}

function roundRect(c, x, y, w, h, r) {
  c.beginPath();
  c.moveTo(x + r, y);
  c.arcTo(x + w, y, x + w, y + h, r);
  c.arcTo(x + w, y + h, x, y + h, r);
  c.arcTo(x, y + h, x, y, r);
  c.arcTo(x, y, x + w, y, r);
  c.closePath();
}

function drawCover(c, img, x, y, w, h) {
  const s = Math.max(w / img.naturalWidth, h / img.naturalHeight);
  const iw = img.naturalWidth * s, ih = img.naturalHeight * s;
  c.drawImage(img, x + (w - iw) / 2, y + (h - ih) / 2, iw, ih);
}

function ellipsize(c, text, max) {
  if (c.measureText(text).width <= max) return text;
  let t = text;
  while (t.length > 1 && c.measureText(t + '…').width > max) t = t.slice(0, -1);
  return t + '…';
}
