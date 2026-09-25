// Port of Assets/VRPlayerLocomotion/Scripts/Shared/VRCameraRigController.cs.
//
// The comfort model this rig implements, unchanged from Unity:
//
//   Idle        first person. The rig is snapped so the headset sits in the
//               avatar's head; physical (room-scale) movement is 1:1.
//   Locomotion  third person. The stick walks the avatar; the camera does NOT
//               move continuously. Every catchUpInterval it cuts to a point
//               orbitRadius behind the avatar along the current view yaw, and
//               it never rotates on its own. No continuous optical flow means
//               no vection, which is where stick-locomotion sickness comes from.
//   Snap turn   discrete, snapAngleDeg per flick; while locomoting it orbits
//               the avatar instead of turning in place.
//   Start nudge moving sideways or backwards from idle first steps the camera
//               back (and to the side) so the avatar walks into view rather
//               than out of the camera's head.
//
// "xrOrigin" is `rig` (the camera's parent), "hmd" is the camera itself. The
// same code drives a WebXR headset (pose comes from the device) and the
// desktop camera (pose comes from the mouse) because it only ever moves the
// rig and reads the camera's world pose.
//
// Unity is left-handed with +Z forward; three.js is right-handed with -Z
// forward. Yaw here is radians counter-clockwise seen from above, and the
// forward vector for yaw is (-sin, 0, -cos). "Right" snaps therefore subtract.

import * as THREE from 'three';

const _v1 = new THREE.Vector3();
const _v2 = new THREE.Vector3();
const _q = new THREE.Quaternion();
const _yAxis = new THREE.Vector3(0, 1, 0);

export function yawOf(quaternion) {
  const f = _v1.set(0, 0, -1).applyQuaternion(quaternion);
  if (f.x * f.x + f.z * f.z < 1e-8) return 0;
  return Math.atan2(-f.x, -f.z);
}

export function yawForward(yaw, out = new THREE.Vector3()) {
  return out.set(-Math.sin(yaw), 0, -Math.cos(yaw));
}

export function deltaAngle(from, to) {
  let d = (to - from) % (Math.PI * 2);
  if (d > Math.PI) d -= Math.PI * 2;
  if (d < -Math.PI) d += Math.PI * 2;
  return d;
}

const DEG = Math.PI / 180;

export class CameraRig {
  // rig: Object3D parent of camera. camera: the "hmd".
  // avatarHead(out): writes the avatar head world position.
  // raycast(origin, dir, maxDist): distance to the first obstacle or Infinity.
  // input: PlayerInput (for the start nudge).
  constructor({ rig, camera, avatarHead, raycast, input }) {
    this.rig = rig;
    this.camera = camera;
    this.avatarHead = avatarHead;
    this.raycast = raycast;
    this.input = input;

    // Serialized fields of VRCameraRigController, with the values the
    // "VR Player Locomotion" prefab (and the Forest scene) actually use rather
    // than the script defaults: a cut every second, not every 0.25 s.
    this.snapAngleDeg = 35;
    this.orbitRadius = 2.5;
    this.preventOrbitClipping = true;
    this.orbitCollisionBuffer = 0.1;
    this.catchUpInterval = 1.0;
    this.catchUpOnlyWhileLocomoting = true;
    this.catchUpToOrbitDistance = 1.0;
    this.locomotionStartDeadzone = 0.2;
    this.forwardConeHalfAngleDeg = 30;
    this.backwardConeHalfAngleDeg = 35;
    this.strafeNudgeBackMeters = 1.0;
    this.strafeNudgeSideMeters = 0;
    this.backwardSnapBackMeters = 2.0;

    // Web additions. 'discrete' is the Unity behaviour. 'smooth' eases the
    // rig towards the orbit point every frame - nicer on a flat screen, where
    // 4 Hz cuts read as dropped frames, but not what you want in a headset.
    this.followMode = 'discrete';
    this.smoothFollowRate = 5;
    // Keep the headset's yaw when snapping back into the head at the end of a
    // walk. Unity rotates the view to the avatar head's yaw there; with VRIK
    // that head faces where you were looking anyway, but this avatar turns to
    // face where it walks, so rotating would add an unrequested turn.
    this.keepYawOnReturn = true;

    this.isLocomoting = false;
    this.onTeleport = null; // (kind, distance) => void

    // Not in the C#: no two cuts closer than this (unless the avatar is
    // already behind the lens), so a timer cut and a jump-back never land on
    // neighbouring frames and read as a slide.
    this.minCutSpacing = 0.2;

    this._viewYaw = 0;
    this._catchUpTimer = 0;
    this._sinceCut = 1;
  }

  // ---- hmd helpers --------------------------------------------------------

  hmdPosition(out = new THREE.Vector3()) {
    this.rig.updateMatrixWorld(true);
    return this.camera.getWorldPosition(out);
  }

  hmdYaw() {
    this.rig.updateMatrixWorld(true);
    return yawOf(this.camera.getWorldQuaternion(_q));
  }

  get viewYaw() { return this._viewYaw; }

  _rotateRigAroundPivot(pivot, deltaYaw) {
    if (Math.abs(deltaYaw) < 1e-9) return;
    const rig = this.rig;
    _v2.copy(rig.position).sub(pivot).applyAxisAngle(_yAxis, deltaYaw);
    rig.position.copy(pivot).add(_v2);
    rig.quaternion.premultiply(_q.setFromAxisAngle(_yAxis, deltaYaw));
    rig.updateMatrixWorld(true);
  }

  _translateRig(dx, dy, dz) {
    this.rig.position.x += dx;
    this.rig.position.y += dy;
    this.rig.position.z += dz;
    this.rig.updateMatrixWorld(true);
  }

  _applyDesiredHmdPose(desiredPos, desiredYaw, kind) {
    const hmd = this.hmdPosition(new THREE.Vector3());
    this._rotateRigAroundPivot(hmd, deltaAngle(this.hmdYaw(), desiredYaw));
    const now = this.hmdPosition(new THREE.Vector3());
    const d = desiredPos.clone().sub(now);
    this._translateRig(d.x, d.y, d.z);
    this._viewYaw = this.hmdYaw();
    this.onTeleport?.(kind, d.length());
  }

  // ---- public API (same names as the C#) ----------------------------------

  snapRigToAvatarHead() {
    const head = this.avatarHead(new THREE.Vector3());
    if (!this.keepYawOnReturn) {
      // Unity: rotate so hmd yaw matches the avatar head's yaw.
      const hmd = this.hmdPosition(new THREE.Vector3());
      this._rotateRigAroundPivot(hmd, deltaAngle(this.hmdYaw(), this.avatarHeadYaw?.() ?? this.hmdYaw()));
    }
    const hmd = this.hmdPosition(new THREE.Vector3());
    const d = head.sub(hmd);
    this._translateRig(d.x, d.y, d.z);
    this._viewYaw = this.hmdYaw();
    this.onTeleport?.('return', d.length());
  }

  setLocomotionState(locomoting) {
    const started = locomoting && !this.isLocomoting;
    this.isLocomoting = locomoting;
    this._viewYaw = this.hmdYaw();
    if (started) {
      this._catchUpTimer = this.catchUpInterval;
      this._tryNudgeCameraOnLocomotionStart();
    }
  }

  snapLeft() { this._applySnap(-1); }
  snapRight() { this._applySnap(+1); }

  lateUpdate(dt) {
    if (this.catchUpOnlyWhileLocomoting && !this.isLocomoting) return;

    if (this.followMode === 'smooth') {
      this._smoothFollow(dt);
      return;
    }

    this._catchUpTimer -= dt;
    this._sinceCut += dt;
    const hmd = this.hmdPosition(new THREE.Vector3());
    const head = this.avatarHead(new THREE.Vector3());
    const toHead = head.clone().sub(hmd);
    let dist = toHead.length();
    const fwd = _v1.set(0, 0, -1).applyQuaternion(this.camera.getWorldQuaternion(_q));
    const behind = fwd.dot(toHead) < 0;
    if (behind) dist = -dist;
    // The avatar walked back at (or past) the camera: back off right away
    // instead of waiting for the timer, or it walks through the lens.
    //
    // Not in the C#: the threshold shrinks with the orbit distance a wall
    // allows. Against a wall the orbit point is clipped to well under 1 m, and
    // with Unity's fixed threshold every frame is a "jump back" - the camera
    // then glides continuously, which is exactly the motion this rig avoids.
    const desired = this._desiredOrbitHmdPosition();
    const reachable = Math.hypot(desired.x - head.x, desired.z - head.z);
    const jumpBackwards = dist < Math.min(this.catchUpToOrbitDistance, reachable * 0.8);
    if (!behind && this._sinceCut < this.minCutSpacing) return;
    if (jumpBackwards || this._catchUpTimer <= 0) {
      if (this._catchUpToOrbitRadius(jumpBackwards, behind)) this._sinceCut = 0;
      this._catchUpTimer = this.catchUpInterval;
    }
  }

  // ---- internals ----------------------------------------------------------

  _applySnap(direction) {
    // +1 is a right turn, i.e. clockwise from above: negative yaw here.
    this._viewYaw -= direction * this.snapAngleDeg * DEG;
    if (this.isLocomoting) this._snapOrbitUsingViewYaw();
    else this._snapTurnUsingViewYaw();
  }

  _snapTurnUsingViewYaw() {
    const hmd = this.hmdPosition(new THREE.Vector3());
    this._rotateRigAroundPivot(hmd, deltaAngle(this.hmdYaw(), this._viewYaw));
    const head = this.avatarHead(new THREE.Vector3());
    const now = this.hmdPosition(new THREE.Vector3());
    this._translateRig(0, head.y - now.y, 0);
    this.onTeleport?.('snap', 0);
  }

  _snapOrbitUsingViewYaw() {
    const desired = this._desiredOrbitHmdPosition();
    this._applyDesiredHmdPose(desired, this._desiredYawLookingAtAvatar(desired), 'snap');
  }

  _catchUpToOrbitRadius(jumpBackwards, behind = false) {
    const desired = this._desiredOrbitHmdPosition();
    const hmd = this.hmdPosition(new THREE.Vector3());
    const delta = desired.sub(hmd);
    const len = delta.length();
    if (len < 1e-4) return false;
    // Unity moves a flat backwardSnapBackMeters here; capped at the remaining
    // distance so a short correction does not overshoot and ping-pong. With
    // the avatar already behind the lens, one full cut beats several 1 m cuts
    // on consecutive frames.
    if (jumpBackwards && !behind) delta.multiplyScalar(Math.min(len, this.backwardSnapBackMeters) / len);
    this._translateRig(delta.x, delta.y, delta.z);
    this.onTeleport?.('catchup', delta.length());
    return true;
  }

  _smoothFollow(dt) {
    const desired = this._desiredOrbitHmdPosition();
    const hmd = this.hmdPosition(new THREE.Vector3());
    const k = 1 - Math.exp(-this.smoothFollowRate * dt);
    const d = desired.sub(hmd).multiplyScalar(k);
    this._translateRig(d.x, d.y, d.z);
  }

  _desiredOrbitHmdPosition() {
    const center = this.avatarHead(new THREE.Vector3());
    const look = yawForward(this._viewYaw, new THREE.Vector3());
    let radius = this.orbitRadius;
    if (this.preventOrbitClipping && this.raycast) {
      const hit = this.raycast(center, look.clone().negate(), radius);
      if (hit < radius) radius = Math.max(0, hit - this.orbitCollisionBuffer);
    }
    const out = center.clone().addScaledVector(look, -radius);
    out.y = center.y;
    return out;
  }

  _desiredYawLookingAtAvatar(desiredPos) {
    const head = this.avatarHead(new THREE.Vector3());
    const dx = head.x - desiredPos.x, dz = head.z - desiredPos.z;
    if (dx * dx + dz * dz < 1e-8) return this._viewYaw;
    return Math.atan2(-dx, -dz);
  }

  _tryNudgeCameraOnLocomotionStart() {
    const axis = this.input.moveAxis;
    if (Math.hypot(axis.x, axis.y) < this.locomotionStartDeadzone) return;

    const yaw = this.hmdYaw();
    const fwd = yawForward(yaw, new THREE.Vector3());
    const right = new THREE.Vector3(-fwd.z, 0, fwd.x); // fwd rotated -90° about Y
    const back = fwd.clone().negate();
    const move = fwd.clone().multiplyScalar(axis.y).addScaledVector(right, axis.x);
    if (move.lengthSq() < 1e-8) return;
    move.normalize();

    if (fwd.angleTo(move) <= this.forwardConeHalfAngleDeg * DEG) return;

    const hmd = this.hmdPosition(new THREE.Vector3());
    const offset = new THREE.Vector3();
    if (back.angleTo(move) <= this.backwardConeHalfAngleDeg * DEG) {
      offset.addScaledVector(back, this.backwardSnapBackMeters);
    } else {
      const side = Math.sign(axis.x);
      offset.addScaledVector(back, this.strafeNudgeBackMeters);
      offset.addScaledVector(right, side * this.strafeNudgeSideMeters);
    }
    // Not in the C#: a metre back from a wall-hugging player is inside the
    // wall, so the nudge is clipped like the orbit is.
    const len = offset.length();
    if (this.preventOrbitClipping && this.raycast) {
      const hit = this.raycast(hmd, offset.clone().normalize(), len);
      if (hit < len) offset.multiplyScalar(Math.max(0, hit - this.orbitCollisionBuffer) / len);
    }
    const desired = hmd.add(offset);
    desired.y = this.avatarHead(new THREE.Vector3()).y;
    this._applyDesiredHmdPose(desired, yaw, 'nudge');
  }
}
