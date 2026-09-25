// In-headset menu: a world-locked panel drawn on a canvas. Opened with X / Y
// on the left controller, pointed at with either controller's ray, trigger to
// pick. World-locked (placed in front of you when opened) rather than
// hand-attached, so it holds still while you read it.

import * as THREE from 'three';

// Layout is in W x H canvas units; the backing canvas is SS times larger so
// text stays sharp when the headset samples it (mipmaps + anisotropy take care
// of the minification).
const W = 1024, H = 720;
const SS = 2;
const PANEL_W = 0.8; // metres
const PANEL_H = (PANEL_W * H) / W;
const DIST = 0.85; // metres in front of the head
const FONT = 'system-ui, "PingFang SC", "Microsoft YaHei", sans-serif';
const TEXT = '#ffffff';
const TEXT_DIM = '#c6cfdb';
const ACCENT = '#5eead4';

export class VrMenu {
  // items(): [{ id, kind: 'scene'|'action', label, sub, thumb, active }]
  // anisotropy: renderer.capabilities.getMaxAnisotropy(), keeps the text
  // crisp when the panel is seen at an angle.
  constructor({ items, onPick, anisotropy = 1 }) {
    this.items = items;
    this.onPick = onPick;
    this.canvas = document.createElement('canvas');
    this.canvas.width = W * SS;
    this.canvas.height = H * SS;
    this.ctx = this.canvas.getContext('2d');
    this.texture = new THREE.CanvasTexture(this.canvas);
    this.texture.colorSpace = THREE.SRGBColorSpace;
    this.texture.anisotropy = anisotropy;
    this.mesh = new THREE.Mesh(
      new THREE.PlaneGeometry(PANEL_W, PANEL_H),
      new THREE.MeshBasicMaterial({ map: this.texture, transparent: true, depthTest: false, depthWrite: false, toneMapped: false }),
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

  // Place in front of the head, a little below eye level, tilted to face it.
  show(headPos, headYaw) {
    const fwd = new THREE.Vector3(-Math.sin(headYaw), 0, -Math.cos(headYaw));
    this.mesh.position.copy(headPos).addScaledVector(fwd, DIST);
    this.mesh.position.y -= 0.1;
    this.mesh.rotation.set(-0.1, headYaw, 0, 'YXZ');
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
    c.setTransform(1, 0, 0, 1, 0, 0);
    c.clearRect(0, 0, W * SS, H * SS);
    c.setTransform(SS, 0, 0, SS, 0, 0);
    c.textBaseline = 'alphabetic';
    roundRect(c, 0, 0, W, H, 36);
    c.fillStyle = 'rgba(12,15,21,0.98)';
    c.fill();
    c.strokeStyle = 'rgba(255,255,255,0.22)';
    c.lineWidth = 3;
    c.stroke();

    // Header.
    c.fillStyle = TEXT;
    c.font = `700 46px ${FONT}`;
    c.fillText('UkemiXR', 48, 80);
    const titleW = c.measureText('UkemiXR').width;
    c.fillStyle = TEXT_DIM;
    c.font = `500 30px ${FONT}`;
    c.fillText('Scenes', 48 + titleW + 24, 78);
    c.fillStyle = ACCENT;
    c.beginPath();
    c.arc(W - 64, 66, 10, 0, Math.PI * 2);
    c.fill();

    // Scene cards.
    const scenes = items.filter((i) => i.kind === 'scene');
    const cardW = 290, cardH = 262, gap = 24, top = 116, imgH = 150;
    scenes.slice(0, 3).forEach((it, k) => {
      const x = 48 + k * (cardW + gap), y = top;
      const hov = this.hover === it.id;
      roundRect(c, x, y, cardW, cardH, 22);
      c.fillStyle = hov ? 'rgba(94,234,212,0.24)' : 'rgba(255,255,255,0.08)';
      c.fill();
      c.lineWidth = it.active || hov ? 5 : 2;
      c.strokeStyle = it.active ? ACCENT : hov ? 'rgba(94,234,212,0.85)' : 'rgba(255,255,255,0.18)';
      c.stroke();
      const img = this._image(it.thumb);
      c.save();
      roundRect(c, x + 12, y + 12, cardW - 24, imgH, 14);
      c.clip();
      if (img?.complete && img.naturalWidth) drawCover(c, img, x + 12, y + 12, cardW - 24, imgH);
      else { c.fillStyle = '#222a35'; c.fillRect(x + 12, y + 12, cardW - 24, imgH); }
      c.restore();
      c.fillStyle = TEXT;
      // Shrink long names (down to 24 px) before resorting to an ellipsis.
      let size = 30;
      do c.font = `600 ${size}px ${FONT}`; while (c.measureText(it.label).width > cardW - 36 && --size > 24);
      c.fillText(ellipsize(c, it.label, cardW - 36), x + 18, y + 208);
      c.fillStyle = it.active ? ACCENT : TEXT_DIM;
      c.font = `500 25px ${FONT}`;
      c.fillText(ellipsize(c, it.active ? 'Now showing' : it.sub || '', cardW - 36), x + 18, y + 243);
      this._rects.push({ id: it.id, x, y, w: cardW, h: cardH });
    });

    // Actions.
    const actions = items.filter((i) => i.kind === 'action');
    const bw = (W - 96 - gap * (actions.length - 1)) / Math.max(1, actions.length), by = 408, bh = 104;
    actions.forEach((it, k) => {
      const x = 48 + k * (bw + gap);
      const hov = this.hover === it.id;
      roundRect(c, x, by, bw, bh, 20);
      c.fillStyle = it.primary ? (hov ? '#99f6e4' : ACCENT) : hov ? 'rgba(94,234,212,0.26)' : 'rgba(255,255,255,0.1)';
      c.fill();
      if (hov && !it.primary) {
        c.lineWidth = 4;
        c.strokeStyle = 'rgba(94,234,212,0.85)';
        c.stroke();
      }
      c.textAlign = 'center';
      c.fillStyle = it.primary ? '#04201d' : TEXT;
      c.font = `700 32px ${FONT}`;
      c.fillText(it.label, x + bw / 2, by + 50);
      c.fillStyle = it.primary ? '#0a3833' : TEXT_DIM;
      c.font = `500 23px ${FONT}`;
      c.fillText(it.sub || '', x + bw / 2, by + 84);
      c.textAlign = 'left';
      this._rects.push({ id: it.id, x, y: by, w: bw, h: bh });
    });

    // Controls legend: key in white, what it does in the dimmer tone.
    const legend = [['Left stick', 'walk'], ['Right stick', 'turn 35°'], ['X / Y', 'menu'], ['Trigger', 'select']];
    legend.forEach(([key, what], k) => {
      const x = 48 + (k % 2) * 470, y = 580 + Math.floor(k / 2) * 48;
      c.font = `600 25px ${FONT}`;
      c.fillStyle = TEXT;
      c.fillText(key, x, y);
      const kw = c.measureText(key).width;
      c.font = `500 25px ${FONT}`;
      c.fillStyle = TEXT_DIM;
      c.fillText(what, x + kw + 18, y);
    });
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
