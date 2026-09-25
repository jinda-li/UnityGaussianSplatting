// Stand-in avatar: RobotExpressive by Tomás Laulhé (Quaternius), CC0, as
// shipped in the three.js examples. It plays the part VRIK + the Mixamo
// character play in Unity: visible in third person while walking, hidden in
// first person.
//
// Gait is picked from the actual ground speed (after collision), so walking
// into a wall plays idle rather than moonwalking in place.

import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

// Shorter than the 1.6 m eye height on purpose: the third-person camera
// orbits at eye height (as in Unity), and a full-height avatar puts the back
// of its head in the middle of the view.
const TARGET_HEIGHT = 1.35;

export class Avatar {
  constructor() {
    this.root = new THREE.Group();
    this.root.name = 'Avatar';
    this.root.visible = false;
    this.model = null;
    this.mixer = null;
    this.actions = {};
    this.current = null;
    this._firstPerson = true;
    this.shadow = makeBlobShadow();
    this.root.add(this.shadow);
  }

  async load(url) {
    const gltf = await new GLTFLoader().loadAsync(url);
    const model = gltf.scene;
    model.traverse((o) => {
      if (o.isMesh) {
        o.frustumCulled = false;
        o.castShadow = false;
      }
    });
    const box = new THREE.Box3().setFromObject(model);
    const h = box.max.y - box.min.y;
    const s = h > 0 ? TARGET_HEIGHT / h : 1;
    model.scale.setScalar(s);
    model.position.y = -box.min.y * s;
    this.model = model;
    this.root.add(model);

    this.mixer = new THREE.AnimationMixer(model);
    for (const clip of gltf.animations) this.actions[clip.name] = this.mixer.clipAction(clip);
    this._play('Idle', 0);
    return this;
  }

  setFirstPerson(firstPerson) {
    this._firstPerson = firstPerson;
    this.root.visible = !firstPerson;
  }

  setPose(x, y, z, yaw) {
    this.root.position.set(x, y, z);
    if (this.model) this.model.rotation.y = yaw;
  }

  update(dt, speed, maxSpeed) {
    if (!this.mixer) return;
    let name = 'Idle';
    if (speed > 0.15) name = speed > Math.max(1.8, maxSpeed * 0.7) ? 'Running' : 'Walking';
    if (name !== this.currentName) this._play(name, 0.2);
    // Rough stride match so the feet do not skate at partial stick.
    if (name === 'Walking') this.current.timeScale = THREE.MathUtils.clamp(speed / 1.1, 0.6, 1.6);
    else if (name === 'Running') this.current.timeScale = THREE.MathUtils.clamp(speed / 2.6, 0.8, 1.4);
    else this.current.timeScale = 1;
    this.mixer.update(dt);
  }

  _play(name, fade) {
    const next = this.actions[name];
    if (!next) return;
    next.reset().setEffectiveWeight(1).play();
    if (this.current && this.current !== next && fade > 0) this.current.crossFadeTo(next, fade, false);
    else if (this.current && this.current !== next) this.current.stop();
    this.current = next;
    this.currentName = name;
  }
}

function makeBlobShadow() {
  const size = 128;
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = size;
  const ctx = canvas.getContext('2d');
  const g = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2);
  g.addColorStop(0, 'rgba(0,0,0,0.55)');
  g.addColorStop(0.6, 'rgba(0,0,0,0.25)');
  g.addColorStop(1, 'rgba(0,0,0,0)');
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, size, size);
  const tex = new THREE.CanvasTexture(canvas);
  const mesh = new THREE.Mesh(
    new THREE.PlaneGeometry(0.9, 0.9),
    new THREE.MeshBasicMaterial({ map: tex, transparent: true, depthWrite: false }),
  );
  mesh.rotation.x = -Math.PI / 2;
  mesh.position.y = 0.015;
  mesh.renderOrder = 1;
  return mesh;
}
