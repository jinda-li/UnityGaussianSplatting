// WebXR controllers: simple drawn models (no runtime download of controller
// profiles, which also keeps the site working where public CDNs are slow),
// pointer rays for the in-headset menu, and edge-detected buttons.
//
// Controllers are parented to the rig (the XR Origin), because WebXR poses
// are relative to the reference space and the locomotion moves the rig.

import * as THREE from 'three';

const RAY_LENGTH = 4;

export class XrControllers {
  constructor(renderer, rig) {
    this.renderer = renderer;
    this.hands = { left: null, right: null };
    this.onSelect = null; // (hand) => void
    this._pressed = {};
    this.raysVisible = false;

    for (let i = 0; i < 2; ++i) {
      const ray = renderer.xr.getController(i);
      const grip = renderer.xr.getControllerGrip(i);
      const model = makeControllerModel();
      grip.add(model);
      const line = makeRay();
      ray.add(line);
      const dot = makeDot();
      ray.add(dot);
      rig.add(ray);
      rig.add(grip);
      const entry = { ray, grip, line, dot, source: null, model };
      ray.addEventListener('connected', (e) => {
        entry.source = e.data;
        const hand = e.data.handedness;
        if (hand === 'left' || hand === 'right') this.hands[hand] = entry;
        model.visible = !e.data.hand; // hand tracking: no controller mesh
      });
      ray.addEventListener('disconnected', () => {
        for (const h of ['left', 'right']) if (this.hands[h] === entry) this.hands[h] = null;
        entry.source = null;
      });
      ray.addEventListener('selectstart', () => {
        const hand = entry.source?.handedness;
        if (hand) this.onSelect?.(hand);
      });
    }
  }

  // Rising edge of a gamepad button (xr-standard index) on one hand.
  pressed(hand, index) {
    const gp = this.hands[hand]?.source?.gamepad;
    const now = !!gp?.buttons[index]?.pressed;
    const key = `${hand}${index}`;
    const was = !!this._pressed[key];
    this._pressed[key] = now;
    return now && !was;
  }

  setRaysVisible(v) {
    this.raysVisible = v;
    for (const h of ['left', 'right']) {
      const e = this.hands[h];
      if (!e) continue;
      e.line.visible = v;
      if (!v) e.dot.visible = false;
    }
  }

  // World-space ray for a hand, or null.
  worldRay(hand, raycaster) {
    const e = this.hands[hand];
    if (!e) return null;
    e.ray.updateMatrixWorld(true);
    raycaster.ray.origin.setFromMatrixPosition(e.ray.matrixWorld);
    raycaster.ray.direction.set(0, 0, -1).transformDirection(e.ray.matrixWorld);
    raycaster.far = RAY_LENGTH;
    return raycaster;
  }

  // Shorten the ray to its hit and park the dot there (distance or null).
  showHit(hand, distance) {
    const e = this.hands[hand];
    if (!e) return;
    const d = distance ?? RAY_LENGTH;
    e.line.scale.z = d / RAY_LENGTH;
    e.dot.visible = this.raysVisible && distance != null;
    e.dot.position.set(0, 0, -d);
  }
}

function makeControllerModel() {
  const g = new THREE.Group();
  const body = new THREE.Mesh(
    new THREE.CapsuleGeometry(0.018, 0.07, 6, 12),
    new THREE.MeshStandardMaterial({ color: 0x1d232d, roughness: 0.45, metalness: 0.1 }),
  );
  body.rotation.x = Math.PI / 2 - 0.5;
  body.position.set(0, -0.012, 0.035);
  const ring = new THREE.Mesh(
    new THREE.TorusGeometry(0.034, 0.005, 8, 32),
    new THREE.MeshBasicMaterial({ color: 0x4fd1c5 }),
  );
  ring.rotation.x = Math.PI / 2 - 0.35;
  ring.position.set(0, 0.012, -0.012);
  g.add(body, ring);
  return g;
}

function makeRay() {
  const geo = new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, 0, -RAY_LENGTH)]);
  const line = new THREE.Line(geo, new THREE.LineBasicMaterial({ color: 0x4fd1c5, transparent: true, opacity: 0.8 }));
  line.visible = false;
  return line;
}

function makeDot() {
  const dot = new THREE.Mesh(
    new THREE.SphereGeometry(0.008, 12, 8),
    new THREE.MeshBasicMaterial({ color: 0xffffff, depthTest: false }),
  );
  dot.renderOrder = 1e5;
  dot.visible = false;
  return dot;
}
