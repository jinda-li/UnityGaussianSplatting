// Everything that can push the virtual sticks, merged into the raw frame
// PlayerInput expects: { move: {x, y}, right: {x, y}, a, b }.
//
//   WebXR controllers  left thumbstick = move, right thumbstick x = snap turn,
//                      A / B on the right controller (same as the Unity rig)
//   Gamepad            left stick = move, right stick x = snap turn
//   Keyboard           WASD / arrows = move (Shift = run), Q / E = snap turn
//   Mouse              drag = look (desktop only; in a headset you just turn)
//   Touch              left half = virtual stick, right half = look

export class InputSources {
  constructor(element) {
    this.element = element;
    this.keys = new Set();
    this.lookDelta = { x: 0, y: 0 };
    this.override = null; // tests: a raw frame that replaces all devices
    this._touchMove = null;
    this._touchLook = null;
    this._dragging = false;
    this.walkMagnitude = 0.6;

    addEventListener('keydown', (e) => {
      if (isTyping(e)) return;
      this.keys.add(e.code);
      if (e.code.startsWith('Arrow')) e.preventDefault();
    });
    addEventListener('keyup', (e) => this.keys.delete(e.code));
    addEventListener('blur', () => this.keys.clear());

    element.addEventListener('pointerdown', (e) => {
      if (e.pointerType === 'mouse') {
        this._dragging = true;
        element.setPointerCapture(e.pointerId);
      }
    });
    element.addEventListener('pointermove', (e) => {
      if (e.pointerType === 'mouse' && this._dragging) {
        this.lookDelta.x += e.movementX;
        this.lookDelta.y += e.movementY;
      }
    });
    const endDrag = (e) => {
      if (e.pointerType === 'mouse') this._dragging = false;
    };
    element.addEventListener('pointerup', endDrag);
    element.addEventListener('pointercancel', endDrag);

    this._setupTouch(element);
  }

  // Look deltas in pixels since the last call.
  consumeLook() {
    const d = { ...this.lookDelta };
    this.lookDelta.x = 0;
    this.lookDelta.y = 0;
    return d;
  }

  read(xrSession) {
    if (this.override) return this.override;
    const raw = { move: { x: 0, y: 0 }, right: { x: 0, y: 0 }, a: false, b: false };

    // Keyboard.
    const k = this.keys;
    let kx = 0, ky = 0;
    if (k.has('KeyW') || k.has('ArrowUp')) ky += 1;
    if (k.has('KeyS') || k.has('ArrowDown')) ky -= 1;
    if (k.has('KeyD') || k.has('ArrowRight')) kx += 1;
    if (k.has('KeyA') || k.has('ArrowLeft')) kx -= 1;
    if (kx || ky) {
      const l = Math.hypot(kx, ky);
      const m = k.has('ShiftLeft') || k.has('ShiftRight') ? 1 : this.walkMagnitude;
      raw.move.x += (kx / l) * m;
      raw.move.y += (ky / l) * m;
    }
    if (k.has('KeyQ')) raw.right.x -= 1;
    if (k.has('KeyE')) raw.right.x += 1;

    // WebXR controllers (xr-standard mapping: thumbstick on axes 2/3).
    if (xrSession) {
      for (const source of xrSession.inputSources) {
        const gp = source.gamepad;
        if (!gp) continue;
        const [x, y] = gp.axes.length >= 4 ? [gp.axes[2], gp.axes[3]] : [gp.axes[0] ?? 0, gp.axes[1] ?? 0];
        if (source.handedness === 'left') {
          raw.move.x += x;
          raw.move.y -= y;
        } else if (source.handedness === 'right') {
          raw.right.x += x;
          raw.right.y -= y;
          raw.a ||= !!gp.buttons[4]?.pressed;
          raw.b ||= !!gp.buttons[5]?.pressed;
        }
      }
    } else if (navigator.getGamepads) {
      // Regular gamepads (not while in XR - there the controllers show up
      // here too on some browsers and would be counted twice).
      for (const gp of navigator.getGamepads()) {
        if (!gp || gp.mapping !== 'standard') continue;
        raw.move.x += dead(gp.axes[0]);
        raw.move.y -= dead(gp.axes[1]);
        raw.right.x += dead(gp.axes[2]);
        raw.right.y -= dead(gp.axes[3]);
        raw.a ||= !!gp.buttons[0]?.pressed;
        raw.b ||= !!gp.buttons[1]?.pressed;
      }
    }

    // Touch stick.
    if (this._touchMove) {
      raw.move.x += this._touchMove.x;
      raw.move.y += this._touchMove.y;
    }

    clampLen(raw.move);
    clampLen(raw.right);
    return raw;
  }

  _setupTouch(element) {
    const knob = document.createElement('div');
    knob.className = 'touch-stick';
    knob.innerHTML = '<div class="touch-stick-knob"></div>';
    document.body.appendChild(knob);
    const radius = 56;
    let moveId = null, lookId = null, origin = null, lastLook = null;

    element.addEventListener('touchstart', (e) => {
      for (const t of e.changedTouches) {
        if (t.clientX < innerWidth / 2 && moveId === null) {
          moveId = t.identifier;
          origin = { x: t.clientX, y: t.clientY };
          this._touchMove = { x: 0, y: 0 };
          knob.style.left = `${origin.x}px`;
          knob.style.top = `${origin.y}px`;
          knob.classList.add('active');
        } else if (lookId === null) {
          lookId = t.identifier;
          lastLook = { x: t.clientX, y: t.clientY };
        }
      }
      e.preventDefault();
    }, { passive: false });

    element.addEventListener('touchmove', (e) => {
      for (const t of e.changedTouches) {
        if (t.identifier === moveId) {
          let dx = t.clientX - origin.x, dy = t.clientY - origin.y;
          const l = Math.hypot(dx, dy);
          if (l > radius) { dx *= radius / l; dy *= radius / l; }
          this._touchMove = { x: dx / radius, y: -dy / radius };
          knob.firstChild.style.transform = `translate(${dx}px, ${dy}px)`;
        } else if (t.identifier === lookId) {
          this.lookDelta.x += (t.clientX - lastLook.x) * 1.5;
          this.lookDelta.y += (t.clientY - lastLook.y) * 1.5;
          lastLook = { x: t.clientX, y: t.clientY };
        }
      }
      e.preventDefault();
    }, { passive: false });

    const end = (e) => {
      for (const t of e.changedTouches) {
        if (t.identifier === moveId) {
          moveId = null;
          this._touchMove = null;
          knob.classList.remove('active');
          knob.firstChild.style.transform = '';
        } else if (t.identifier === lookId) {
          lookId = null;
        }
      }
    };
    element.addEventListener('touchend', end);
    element.addEventListener('touchcancel', end);
  }
}

function dead(v, dz = 0.12) {
  return Math.abs(v) < dz ? 0 : v;
}

function clampLen(v) {
  const l = Math.hypot(v.x, v.y);
  if (l > 1) { v.x /= l; v.y /= l; }
}

function isTyping(e) {
  const t = e.target;
  return t && (t.tagName === 'INPUT' || t.tagName === 'SELECT' || t.tagName === 'TEXTAREA');
}
