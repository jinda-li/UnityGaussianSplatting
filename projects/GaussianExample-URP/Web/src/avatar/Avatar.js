// The third-person body: a humanoid that is visible while walking and hidden
// in first person (the Unity rig does the same with VRIK + a Mixamo-style
// character). The Unity project's character and animation pack are licensed
// assets that cannot be published on a website, so the web build uses the
// Mixamo humanoids that ship with the three.js examples.
//
// Gait is picked from the actual ground speed (after collision), so walking
// into a wall plays idle rather than moonwalking in place.

import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

export const AVATARS = {
  // Neutral Mixamo mannequin ("X Bot").
  xbot: {
    url: '/avatar/Xbot.glb',
    clips: { idle: 'idle', walk: 'walk', run: 'run' },
    yawOffset: 0,
    height: 1.75,
    // Re-skinned in the UkemiXR palette (the stock X Bot is salmon pink).
    materials: {
      Beta_Joints_MAT: { color: 0x4fd1c5, metalness: 0.3, roughness: 0.35, emissive: 0x0b3b37 },
      'asdf1:Beta_HighLimbsGeoSG2': { color: 0x2b313b, metalness: 0.35, roughness: 0.42 },
    },
    walkSpeed: 1.25, // metres per second the walk clip is authored at
    runSpeed: 3.2,
  },
  soldier: {
    url: '/avatar/Soldier.glb',
    clips: { idle: 'Idle', walk: 'Walk', run: 'Run' },
    yawOffset: Math.PI,
    height: 1.78,
    walkSpeed: 1.3,
    runSpeed: 3.4,
  },
};

export class Avatar {
  constructor(config = AVATARS.xbot) {
    this.config = config;
    this.root = new THREE.Group();
    this.root.name = 'Avatar';
    this.root.visible = false;
    this.model = null;
    this.mixer = null;
    this.actions = {};
    this.current = null;
    this.currentName = null;
    this.shadow = makeBlobShadow();
    this.root.add(this.shadow);
  }

  async load() {
    const gltf = await new GLTFLoader().loadAsync(this.config.url);
    const model = gltf.scene;
    model.traverse((o) => {
      if (o.isMesh) {
        o.frustumCulled = false;
        o.castShadow = false;
        const look = this.config.materials?.[o.material?.name];
        if (look) {
          o.material = o.material.clone();
          o.material.color.set(look.color);
          o.material.metalness = look.metalness;
          o.material.roughness = look.roughness;
          if (look.emissive) o.material.emissive.set(look.emissive);
        }
      }
    });
    // Measure in bind pose, scale to the configured height, feet on y = 0.
    const box = new THREE.Box3().setFromObject(model);
    const h = box.max.y - box.min.y;
    const s = h > 0 ? this.config.height / h : 1;
    model.scale.multiplyScalar(s);
    model.position.y = -box.min.y * s;
    // The pivot turns the model to face +Z (object convention used by the
    // player controller); `model` keeps its own authored transform.
    this.pivot = new THREE.Group();
    this.pivot.add(model);
    this.model = model;
    this.root.add(this.pivot);

    this.mixer = new THREE.AnimationMixer(model);
    const { clips } = this.config;
    for (const clip of gltf.animations) {
      // Root motion stays in place: the controller moves the body.
      for (const track of clip.tracks) {
        if (track.name.endsWith('.position') && /hips/i.test(track.name)) {
          const v = track.values;
          for (let i = 0; i < v.length; i += 3) { v[i] = v[0]; v[i + 2] = v[2]; }
        }
      }
      this.actions[clip.name] = this.mixer.clipAction(clip);
    }
    this._play(clips.idle, 0);
    return this;
  }

  setFirstPerson(firstPerson) {
    this.root.visible = !firstPerson;
  }

  setPose(x, y, z, yaw) {
    this.root.position.set(x, y, z);
    if (this.pivot) this.pivot.rotation.y = yaw + this.config.yawOffset;
  }

  update(dt, speed, maxSpeed) {
    if (!this.mixer) return;
    const { clips, walkSpeed, runSpeed } = this.config;
    let name = clips.idle;
    if (speed > 0.15) name = speed > Math.max(1.9, maxSpeed * 0.72) ? clips.run : clips.walk;
    if (name !== this.currentName) this._play(name, 0.25);
    // Match stride to ground speed so the feet do not skate.
    if (name === clips.walk) this.current.timeScale = THREE.MathUtils.clamp(speed / walkSpeed, 0.55, 1.7);
    else if (name === clips.run) this.current.timeScale = THREE.MathUtils.clamp(speed / runSpeed, 0.7, 1.4);
    else this.current.timeScale = 1;
    this.mixer.update(dt);
  }

  _play(name, fade) {
    const next = this.actions[name];
    if (!next) return;
    next.reset().setEffectiveWeight(1).play();
    if (this.current && this.current !== next) {
      if (fade > 0) this.current.crossFadeTo(next, fade, true);
      else this.current.stop();
    }
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
  g.addColorStop(0, 'rgba(0,0,0,0.5)');
  g.addColorStop(0.6, 'rgba(0,0,0,0.22)');
  g.addColorStop(1, 'rgba(0,0,0,0)');
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, size, size);
  const tex = new THREE.CanvasTexture(canvas);
  const mesh = new THREE.Mesh(
    new THREE.PlaneGeometry(0.8, 0.8),
    new THREE.MeshBasicMaterial({ map: tex, transparent: true, depthWrite: false }),
  );
  mesh.rotation.x = -Math.PI / 2;
  mesh.position.y = 0.015;
  mesh.renderOrder = 1;
  return mesh;
}
