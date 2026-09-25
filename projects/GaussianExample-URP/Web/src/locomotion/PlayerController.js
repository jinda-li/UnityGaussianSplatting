// Port of Assets/Scripts/PlayerController.cs (the controller the Trogir and
// forest scenes use), with VRIK/IKRetarget replaced by a simple animated
// avatar and the CharacterController replaced by VoxelWorld.moveAndSlide.
//
//   Idle        first person; room-scale head movement drags the body along
//               with collision (FollowHeadHorizontally).
//   Locomotion  stick moves the body relative to the view yaw at moveSpeed;
//               the camera rig handles the third-person catch-up.
//
// Transitions are the C#'s: movement-started enters locomotion, movement-ended
// (with the stick really released) goes back to idle, which snaps the rig back
// into the avatar's head.

import * as THREE from 'three';
import { yawForward } from './CameraRig.js';

export const PlayerState = Object.freeze({ Idle: 'idle', Locomotion: 'locomotion' });

export class PlayerController {
  constructor({ input, cameraRig, world, avatar }) {
    this.input = input;
    this.cameraRig = cameraRig;
    this.world = world;
    this.avatar = avatar;

    this.moveSpeed = 2.5; // m/s at full stick, PlayerController.moveSpeed
    this.eyeHeight = 1.6; // avatar head height above the feet
    this.yawRotateSpeed = 12; // VRPlayerRootMotionController.yawRotateSpeed
    this.roomScale = true; // FollowHeadHorizontally in idle

    this.state = PlayerState.Idle;
    this.body = { x: 0, y: 0, z: 0 };
    this.facingYaw = 0; // avatar facing, radians (three.js convention)
    this.speed = 0;
    this.lastHit = false;

    this._visualY = 0;
    this._lastHmd = new THREE.Vector3();
    this._hasLastHmd = false;
    this._lastIdleBodyY = 0;
  }

  get isLocomoting() { return this.state === PlayerState.Locomotion; }

  avatarHead(out = new THREE.Vector3()) {
    return out.set(this.body.x, this._visualY + this.eyeHeight, this.body.z);
  }

  // Teleport the body (spawn/respawn) and put the camera in its head.
  placeAt(x, y, z, yaw = null) {
    this.body.x = x; this.body.y = y; this.body.z = z;
    this.world?.resetTrail(this.body);
    this._visualY = y;
    if (yaw !== null) this.facingYaw = yaw;
    this.speed = 0;
    this._syncAvatar();
    this.enterIdle();
  }

  update(dt) {
    this._handleSnapTurn();
    this._handleStateTransitions();
    if (this.state === PlayerState.Idle) this._tickIdle(dt);
    else this._tickLocomotion(dt);

    // Smooth the visible height only; collision uses body.y as is.
    const k = 1 - Math.exp(-12 * dt);
    this._visualY += (this.body.y - this._visualY) * k;
    if (Math.abs(this.body.y - this._visualY) > 1.0) this._visualY = this.body.y;
    this._syncAvatar();
    this.avatar?.update(dt, this.speed, this.moveSpeed);
  }

  enterIdle() {
    this.state = PlayerState.Idle;
    this.speed = 0;
    this._visualY = this.body.y;
    this.cameraRig.snapRigToAvatarHead();
    this.cameraRig.setLocomotionState(false);
    this.avatar?.setFirstPerson(true);
    this._hasLastHmd = false;
    this._lastIdleBodyY = this.body.y;
  }

  enterLocomotion() {
    this.state = PlayerState.Locomotion;
    // The avatar starts facing where the player looks, so the first steps
    // read as "walking forward" rather than spinning on the spot.
    // facingYaw is an object rotation (model faces +Z); the view yaw looks down
    // -Z, hence the half turn.
    this.facingYaw = this.cameraRig.hmdYaw() + Math.PI;
    this.cameraRig.setLocomotionState(true);
    this.avatar?.setFirstPerson(false);
  }

  _handleSnapTurn() {
    const d = this.input.getCameraSnapDirection();
    if (d < 0) this.cameraRig.snapLeft();
    else if (d > 0) this.cameraRig.snapRight();
  }

  _handleStateTransitions() {
    if (this.input.getMovementStarted()) this.enterLocomotion();
    else if (this.input.getMovementEnded() && !this.input.isMovePressed) this.enterIdle();
  }

  _tickIdle() {
    this.speed = 0;
    if (!this.roomScale || !this.world) return;
    // Room-scale: drag the body along with physical head movement, through
    // the collision world, instead of hard-setting it.
    const hmd = this.cameraRig.hmdPosition(new THREE.Vector3());
    if (!this._hasLastHmd) {
      this._lastHmd.copy(hmd);
      this._hasLastHmd = true;
      return;
    }
    const dx = hmd.x - this._lastHmd.x, dz = hmd.z - this._lastHmd.z;
    this._lastHmd.copy(hmd);
    if (dx * dx + dz * dz < 1e-8) return;
    this.world.moveAndSlide(this.body, dx, dz);
    // Keep the view on the floor the body is standing on (a step, a rug).
    const dy = this.body.y - this._lastIdleBodyY;
    if (Math.abs(dy) > 1e-4) {
      this.cameraRig._translateRig(0, dy, 0);
      this._lastHmd.y += dy;
      this._lastIdleBodyY = this.body.y;
    }
  }

  _tickLocomotion(dt) {
    const stick = this.input.moveAxis;
    const move = this._viewRelativeMove(stick);
    const mag = Math.min(1, move.length());
    const before = { x: this.body.x, z: this.body.z };
    let hit = false;
    if (mag > 1e-4 && this.world) {
      const d = this.moveSpeed * dt;
      hit = this.world.moveAndSlide(this.body, move.x * d, move.z * d).hit;
    }
    const moved = Math.hypot(this.body.x - before.x, this.body.z - before.z);
    this.speed = dt > 0 ? moved / dt : 0;
    this.lastHit = hit;

    // Face the direction of travel (stick direction, so pushing into a wall
    // still turns to face it).
    if (mag > 0.05) {
      const target = Math.atan2(move.x, move.z);
      let d = target - this.facingYaw;
      d = Math.atan2(Math.sin(d), Math.cos(d));
      this.facingYaw += d * Math.min(1, this.yawRotateSpeed * dt);
    }
  }

  // GetViewRelativeMove: stick in the frame of the headset's yaw.
  _viewRelativeMove(stick) {
    const yaw = this.cameraRig.hmdYaw();
    const fwd = yawForward(yaw, new THREE.Vector3());
    const right = new THREE.Vector3(-fwd.z, 0, fwd.x);
    const m = fwd.multiplyScalar(stick.y).addScaledVector(right, stick.x);
    if (m.lengthSq() > 1) m.normalize();
    return m;
  }

  _syncAvatar() {
    this.avatar?.setPose(this.body.x, this._visualY, this.body.z, this.facingYaw);
  }
}
