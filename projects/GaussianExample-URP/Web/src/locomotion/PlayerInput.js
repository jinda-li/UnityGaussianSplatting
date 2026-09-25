// Port of Assets/VRPlayerLocomotion/Scripts/VRPlayerControllerInput.cs.
//
// Same thresholds and the same "buffered edge" model: a stick crossing its
// start threshold stamps a time, and the state machine consumes that stamp
// once. Hysteresis (start 0.20 / end 0.15, snap 0.75 / reset 0.50) is what
// keeps a resting thumb from flickering between idle and locomotion - which
// in this rig would mean the camera jumping in and out of the avatar's head.
//
// Unity reads InputActions; here the raw values are fed in once per frame by
// InputSources (keyboard, mouse, gamepad, WebXR controllers, touch).

export class PlayerInput {
  constructor(options = {}) {
    this.moveStartThreshold = options.moveStartThreshold ?? 0.2;
    this.moveEndThreshold = options.moveEndThreshold ?? 0.15;
    this.cameraSnapThreshold = options.cameraSnapThreshold ?? 0.75;
    this.cameraSnapResetThreshold = options.cameraSnapResetThreshold ?? 0.5;

    this.canMove = true;
    this.canChangeCamera = true;

    this.moveAxis = { x: 0, y: 0 };
    this.rightStickAxis = { x: 0, y: 0 };
    this.isMovePressed = false;

    this._time = 0;
    this._moveWasPressed = false;
    this._cameraSnapHeld = false;
    this._aWasPressed = false;
    this._bWasPressed = false;

    this._moveStartedBuffered = -10;
    this._moveEndedBuffered = -10;
    this._aDownBuffered = -10;
    this._bDownBuffered = -10;
    this._cameraSnapBuffered = 0;
  }

  // raw: { move: {x, y}, right: {x, y}, a: bool, b: bool }. y is forward.
  update(time, raw) {
    this._time = time;
    this._updateButtons(raw);
    this._updateMovement(raw.move);
    this._updateCamera(raw.right);
  }

  getMovementStarted() { return this._consume('_moveStartedBuffered'); }
  getMovementEnded() { return this._consume('_moveEndedBuffered'); }
  getADown(bufferTime = 0) { return this._peek('_aDownBuffered', bufferTime); }
  getBDown(bufferTime = 0) { return this._peek('_bDownBuffered', bufferTime); }
  resetBufferedBDown() { this._bDownBuffered = -1; }
  resetBufferedADown() { this._aDownBuffered = -1; }

  getCameraSnapDirection() {
    const v = this._cameraSnapBuffered;
    this._cameraSnapBuffered = 0;
    return v;
  }

  setCanMove(v) {
    this.canMove = v;
    if (!v) this._resetMovement();
  }

  setCanChangeCamera(v) {
    this.canChangeCamera = v;
    if (!v) this._resetCamera();
  }

  _consume(key) {
    const t = this[key];
    if (t < 0 || this._time - t > 0) return false;
    this[key] = -1;
    return true;
  }

  _peek(key, bufferTime) {
    const t = this[key];
    return !(t < 0 || this._time - t > bufferTime);
  }

  _updateButtons(raw) {
    const a = !!raw.a, b = !!raw.b;
    if (a && !this._aWasPressed) this._aDownBuffered = this._time;
    if (b && !this._bWasPressed) this._bDownBuffered = this._time;
    this._aWasPressed = a;
    this._bWasPressed = b;
  }

  _updateMovement(axis) {
    if (!this.canMove) {
      this._resetMovement();
      return;
    }
    this.moveAxis = { x: axis.x, y: axis.y };
    const magnitude = Math.hypot(axis.x, axis.y);
    const pressedNow = this._moveWasPressed
      ? magnitude >= this.moveEndThreshold
      : magnitude >= this.moveStartThreshold;

    if (pressedNow && !this._moveWasPressed) this._moveStartedBuffered = this._time;
    else if (!pressedNow && this._moveWasPressed) this._moveEndedBuffered = this._time;

    this.isMovePressed = pressedNow;
    this._moveWasPressed = pressedNow;
  }

  _updateCamera(axis) {
    if (!this.canChangeCamera) {
      this._resetCamera();
      return;
    }
    this.rightStickAxis = { x: axis.x, y: axis.y };
    const x = axis.x;
    const wantsSnap = Math.abs(x) >= this.cameraSnapThreshold;
    if (wantsSnap && !this._cameraSnapHeld) {
      this._cameraSnapBuffered = x < 0 ? -1 : 1;
      this._cameraSnapHeld = true;
    } else if (this._cameraSnapHeld && Math.abs(x) <= this.cameraSnapResetThreshold) {
      this._cameraSnapHeld = false;
    }
  }

  _resetMovement() {
    this.moveAxis = { x: 0, y: 0 };
    this.isMovePressed = false;
    this._moveWasPressed = false;
    this._moveStartedBuffered = -1;
    this._moveEndedBuffered = -1;
  }

  _resetCamera() {
    this.rightStickAxis = { x: 0, y: 0 };
    this._cameraSnapHeld = false;
    this._cameraSnapBuffered = 0;
  }
}
