// Collision proxy generated straight from the splats, in the browser.
//
// The Unity pipeline (docs/workflows/superspl-at-to-unity.md) voxelises the
// splat offline with splat-transform and loads the result as a MeshCollider.
// On the web the file can be anything the user drops in, so the same idea is
// done here at load time: splat centres (and, for large splats, a few points
// across their footprint) are splatted into a dense occupancy grid, weighted
// by opacity. A voxel is solid once enough opacity has landed in it, which
// drops the odd floater without a separate cleanup pass.
//
// The player is a vertical cylinder standing on that grid, the same shape a
// CharacterController has:
//   - support: the highest solid voxel under the feet, within a step up or a
//     short drop down. No support means a hole or the edge of the capture, and
//     the move is refused - that is what keeps people inside the scene.
//   - body: solid voxels between knee height and the top of the head block
//     the move. A chair seat is above the step height, so it stops you.
// Moves are sub-stepped below the voxel size and slide along walls one axis at
// a time, so a diagonal push into a wall keeps the free component.

const DEFAULTS = {
  // Target voxel edge in metres; grown when the capture is too large for the
  // cell budget.
  voxelSize: 0.1,
  maxCells: 24_000_000,
  // Opacity that has to land in a voxel before it counts as solid.
  solidWeight: 1.0,
  // Splats fainter than this are ignored entirely.
  minOpacity: 0.15,
  // Splats larger than this (metres, largest axis) are background/sky blobs.
  maxSplatScale: 1.5,
  // Percentile bounds so a few far floaters do not blow up the grid.
  boundsPercentile: 0.004,
  boundsPadding: 0.75,
};

const PLAYER_DEFAULTS = {
  radius: 0.22,
  height: 1.7,
  stepHeight: 0.35,
  maxDrop: 0.6,
  // Solid voxels inside the body volume needed to call it blocked. 1 would
  // turn every stray floater into an invisible wall.
  blockCount: 2,
  // Horizontal distance over which the climb is limited to stepHeight.
  trailLength: 0.4,
};

const QSCALE = 32; // opacity quantisation: weight * QSCALE stored in a Uint8

export class VoxelWorld {
  constructor(grid, options) {
    Object.assign(this, grid);
    this.options = options;
    this.player = { ...PLAYER_DEFAULTS };
    this.setSolidWeight(options.solidWeight);
  }

  setSolidWeight(w) {
    this.options.solidWeight = w;
    this._solidQ = Math.max(1, Math.round(w * QSCALE));
  }

  // splats: { count, forEach(cb(x, y, z, sx, sy, sz, qx, qy, qz, qw, opacity)) }
  // All coordinates must already be in world space, +Y up.
  static build(splats, userOptions = {}) {
    const options = { ...DEFAULTS, ...userOptions };
    const t0 = now();

    // Pass 1: keep the splats that can matter and find robust bounds.
    const n = splats.count;
    const pos = new Float32Array(n * 3);
    const ext = new Float32Array(n * 6); // two largest axes, scaled
    const wts = new Float32Array(n);
    let kept = 0;
    const tmpAxis = [0, 0, 0, 0, 0, 0, 0, 0, 0];
    splats.forEach((x, y, z, sx, sy, sz, qx, qy, qz, qw, opacity) => {
      if (!(opacity >= options.minOpacity)) return;
      const smax = Math.max(sx, sy, sz);
      if (!(smax <= options.maxSplatScale)) return;
      if (!Number.isFinite(x + y + z)) return;
      const i = kept++;
      pos[i * 3] = x; pos[i * 3 + 1] = y; pos[i * 3 + 2] = z;
      wts[i] = opacity;
      // Rotation matrix columns = the splat's local axes in world space.
      quatAxes(qx, qy, qz, qw, tmpAxis);
      const s = [sx, sy, sz];
      // Indices of the two largest scales.
      let a = 0, b = 1, c = 2;
      if (s[b] > s[a]) [a, b] = [b, a];
      if (s[c] > s[a]) [a, c] = [c, a];
      if (s[c] > s[b]) [b, c] = [c, b];
      ext[i * 6 + 0] = tmpAxis[a * 3] * s[a];
      ext[i * 6 + 1] = tmpAxis[a * 3 + 1] * s[a];
      ext[i * 6 + 2] = tmpAxis[a * 3 + 2] * s[a];
      ext[i * 6 + 3] = tmpAxis[b * 3] * s[b];
      ext[i * 6 + 4] = tmpAxis[b * 3 + 1] * s[b];
      ext[i * 6 + 5] = tmpAxis[b * 3 + 2] * s[b];
    });

    const min = [0, 0, 0], max = [0, 0, 0];
    for (let axis = 0; axis < 3; ++axis) {
      const [lo, hi] = percentileRange(pos, kept, axis, options.boundsPercentile);
      min[axis] = lo - options.boundsPadding;
      max[axis] = hi + options.boundsPadding;
    }
    const volume = (max[0] - min[0]) * (max[1] - min[1]) * (max[2] - min[2]);
    const voxel = Math.max(options.voxelSize, Math.cbrt(volume / options.maxCells));
    const nx = Math.max(1, Math.ceil((max[0] - min[0]) / voxel));
    const ny = Math.max(1, Math.ceil((max[1] - min[1]) / voxel));
    const nz = Math.max(1, Math.ceil((max[2] - min[2]) / voxel));
    const cells = new Uint8Array(nx * ny * nz);

    const grid = { voxel, nx, ny, nz, ox: min[0], oy: min[1], oz: min[2], cells };

    // Pass 2: splat opacity into the grid. Large splats are sampled across
    // their two main axes so a big floor splat covers the floor it draws.
    const stamp = (x, y, z, w) => {
      const ix = Math.floor((x - grid.ox) / voxel);
      const iy = Math.floor((y - grid.oy) / voxel);
      const iz = Math.floor((z - grid.oz) / voxel);
      if (ix < 0 || iy < 0 || iz < 0 || ix >= nx || iy >= ny || iz >= nz) return;
      const idx = (iy * nz + iz) * nx + ix;
      const q = cells[idx] + Math.round(w * QSCALE);
      cells[idx] = q > 255 ? 255 : q;
    };
    for (let i = 0; i < kept; ++i) {
      const x = pos[i * 3], y = pos[i * 3 + 1], z = pos[i * 3 + 2];
      const w = wts[i];
      stamp(x, y, z, w);
      const ax = ext[i * 6], ay = ext[i * 6 + 1], az = ext[i * 6 + 2];
      const bx = ext[i * 6 + 3], by = ext[i * 6 + 4], bz = ext[i * 6 + 5];
      const la = Math.hypot(ax, ay, az), lb = Math.hypot(bx, by, bz);
      if (la < voxel * 0.6) continue;
      // Sample the 1-sigma ellipse on a grid about one voxel apart.
      const na = Math.min(6, Math.ceil(la / voxel));
      const nb = Math.min(6, Math.ceil(lb / voxel));
      const sw = w * 0.75;
      for (let u = -na; u <= na; ++u) {
        for (let v = -nb; v <= nb; ++v) {
          if (u === 0 && v === 0) continue;
          const fu = u / na, fv = nb ? v / nb : 0;
          if (fu * fu + fv * fv > 1) continue;
          stamp(x + ax * fu + bx * fv, y + ay * fu + by * fv, z + az * fu + bz * fv, sw);
        }
      }
    }

    const world = new VoxelWorld(grid, options);
    world.stats = {
      splats: n,
      used: kept,
      voxel,
      dims: [nx, ny, nz],
      buildMs: Math.round(now() - t0),
      bounds: { min, max },
    };
    return world;
  }

  // ---- grid access -------------------------------------------------------

  cell(ix, iy, iz) {
    if (ix < 0 || iy < 0 || iz < 0 || ix >= this.nx || iy >= this.ny || iz >= this.nz) return 0;
    return this.cells[(iy * this.nz + iz) * this.nx + ix];
  }

  solidCell(ix, iy, iz) {
    return this.cell(ix, iy, iz) >= this._solidQ;
  }

  solidAt(x, y, z) {
    return this.solidCell(this.ix(x), this.iy(y), this.iz(z));
  }

  ix(x) { return Math.floor((x - this.ox) / this.voxel); }
  iy(y) { return Math.floor((y - this.oy) / this.voxel); }
  iz(z) { return Math.floor((z - this.oz) / this.voxel); }

  insideXZ(x, z) {
    const ix = this.ix(x), iz = this.iz(z);
    return ix >= 0 && iz >= 0 && ix < this.nx && iz < this.nz;
  }

  // Top of the highest solid voxel in the column with its top inside
  // [yMin, yMax]; NaN if there is none.
  columnTop(x, z, yMin, yMax) {
    const ix = this.ix(x), iz = this.iz(z);
    if (ix < 0 || iz < 0 || ix >= this.nx || iz >= this.nz) return NaN;
    const hi = Math.min(this.ny - 1, Math.floor((yMax - this.oy) / this.voxel) - 1);
    const lo = Math.max(0, Math.floor((yMin - this.oy) / this.voxel));
    const stride = this.nx * this.nz;
    let idx = hi * stride + iz * this.nx + ix;
    for (let iy = hi; iy >= lo; --iy, idx -= stride) {
      if (this.cells[idx] >= this._solidQ) return this.oy + (iy + 1) * this.voxel;
    }
    return NaN;
  }

  // ---- player queries -----------------------------------------------------

  // Ground under a standing player whose feet are at feetY. NaN = no floor.
  support(x, z, feetY) {
    const p = this.player;
    const r = p.radius * 0.7;
    const yMin = feetY - p.maxDrop, yMax = feetY + p.stepHeight;
    let n = 0, best = -Infinity, second = -Infinity;
    for (let k = 0; k < 5; ++k) {
      const sx = x + (k === 1 ? r : k === 2 ? -r : 0);
      const sz = z + (k === 3 ? r : k === 4 ? -r : 0);
      const top = this.columnTop(sx, sz, yMin, yMax);
      if (Number.isNaN(top)) continue;
      ++n;
      if (top > best) { second = best; best = top; } else if (top > second) second = top;
    }
    // Two feet on the ground is enough; one lone column is noise or a ledge.
    if (n < 2) return NaN;
    // The second-highest ignores a single spike (a rug fold, a stray splat).
    return n >= 3 ? second : best;
  }

  // Number of solid voxels overlapping the body standing at groundY.
  bodyHits(x, z, groundY, limit = Infinity) {
    const p = this.player;
    const v = this.voxel;
    const y0 = this.iy(groundY + p.stepHeight + v * 0.5);
    const y1 = this.iy(groundY + p.height);
    let hits = 0;
    for (let k = 0; k < 9; ++k) {
      let sx = x, sz = z;
      if (k > 0) {
        const a = ((k - 1) / 8) * Math.PI * 2;
        sx += Math.cos(a) * p.radius;
        sz += Math.sin(a) * p.radius;
      }
      const ix = this.ix(sx), iz = this.iz(sz);
      for (let iy = y0; iy <= y1; ++iy) {
        if (this.solidCell(ix, iy, iz)) {
          if (++hits >= limit) return hits;
        }
      }
    }
    return hits;
  }

  blocked(x, z, groundY) {
    return this.bodyHits(x, z, groundY, this.player.blockCount) >= this.player.blockCount;
  }

  // Where a player at feet (x, feetY, z) may stand after moving to (x2, z2).
  // Returns ground height or NaN if the spot is refused.
  _standAt(x2, z2, feetY) {
    if (!this.insideXZ(x2, z2)) return NaN;
    const g = this.support(x2, z2, feetY);
    if (Number.isNaN(g)) return NaN;
    if (this.blocked(x2, z2, g)) return NaN;
    return g;
  }

  // CharacterController.Move for the voxel world. pos is {x, y, z} at the
  // feet and is updated in place; y is set to the ground reached. Returns a
  // flags object: { hit, moved }.
  //
  // Climbing is limited over a short trail of recent ground heights, not per
  // sub-step: otherwise a chair with a sloped cushion is a staircase of tiny
  // steps and the player walks up onto the seat. Real stairs (~0.17 m rise
  // per 0.28 m run) stay within stepHeight over the trail length.
  moveAndSlide(pos, dx, dz) {
    const len = Math.hypot(dx, dz);
    const flags = { hit: false, moved: 0 };
    if (len < 1e-6) return flags;
    const maxStep = this.voxel * 0.5;
    const steps = Math.max(1, Math.ceil(len / maxStep));
    let sx = dx / steps, sz = dz / steps;
    const trail = pos.trail || (pos.trail = { d: 0, pts: [] });
    // A player spawned (or room-scale walked) into geometry must be able to
    // walk out of it - but only over real floor, and only towards less overlap.
    let stuckHits = -1;
    if (Number.isNaN(this._standAt(pos.x, pos.z, pos.y))) {
      const g0 = this.support(pos.x, pos.z, pos.y);
      stuckHits = Number.isNaN(g0) ? Infinity : this.bodyHits(pos.x, pos.z, g0);
    }
    const accept = (nx, nz, g) => {
      const d = Math.hypot(nx - pos.x, nz - pos.z);
      pos.x = nx; pos.z = nz;
      if (!Number.isNaN(g)) pos.y = g;
      flags.moved += d;
      trail.d += d;
      trail.pts.push(trail.d, pos.y);
      const keepFrom = trail.d - this.player.trailLength;
      let cut = 0;
      while (cut + 2 < trail.pts.length && trail.pts[cut + 2] < keepFrom) cut += 2;
      if (cut) trail.pts.splice(0, cut);
    };
    const tryAt = (nx, nz) => {
      let g = this._standAt(nx, nz, pos.y);
      if (Number.isNaN(g) && stuckHits >= 0 && this.insideXZ(nx, nz)) {
        const gs = this.support(nx, nz, pos.y);
        if (!Number.isNaN(gs) && this.bodyHits(nx, nz, gs) <= stuckHits) return gs;
      }
      if (Number.isNaN(g)) return NaN;
      if (g > pos.y + 1e-4 && g - this._trailMin(trail, pos.y) > this.player.stepHeight) return NaN;
      return g;
    };
    for (let s = 0; s < steps; ++s) {
      let g = tryAt(pos.x + sx, pos.z + sz);
      if (!Number.isNaN(g)) {
        accept(pos.x + sx, pos.z + sz, g);
        continue;
      }
      flags.hit = true;
      // Slide: keep whichever axis still moves.
      if (Math.abs(sx) > 1e-7) {
        g = tryAt(pos.x + sx, pos.z);
        if (!Number.isNaN(g)) { accept(pos.x + sx, pos.z, g); sz = 0; continue; }
      }
      if (Math.abs(sz) > 1e-7) {
        g = tryAt(pos.x, pos.z + sz);
        if (!Number.isNaN(g)) { accept(pos.x, pos.z + sz, g); sx = 0; continue; }
      }
      break;
    }
    return flags;
  }

  _trailMin(trail, y) {
    let m = y;
    for (let i = 1; i < trail.pts.length; i += 2) if (trail.pts[i] < m) m = trail.pts[i];
    return m;
  }

  // Forget the climb history, e.g. after a respawn or teleport.
  resetTrail(pos) {
    pos.trail = { d: 0, pts: [] };
  }

  // Distance along the ray to the first solid voxel, or Infinity.
  raycast(ox, oy, oz, dx, dy, dz, maxDist) {
    const len = Math.hypot(dx, dy, dz) || 1;
    dx /= len; dy /= len; dz /= len;
    const step = this.voxel * 0.5;
    for (let t = step; t <= maxDist; t += step) {
      if (this.solidAt(ox + dx * t, oy + dy * t, oz + dz * t)) return t;
    }
    return Infinity;
  }

  // Standing spot near (x, z) on the scene's main floor level.
  //
  // A capture's origin is usually the generation camera, and what is right
  // under it is not always floor (the bamboo courtyard has a pool there). So:
  // take every column within `radius`, find the lowest surface a player can
  // stand on, call the most common height the floor, and pick the nearest
  // free spot on it.
  findSpawn(x = 0, z = 0, radius = 8) {
    const p = this.player;
    const step = Math.max(this.voxel * 2, 0.2);
    const samples = [];
    for (let dz = -radius; dz <= radius; dz += step) {
      for (let dx = -radius; dx <= radius; dx += step) {
        if (dx * dx + dz * dz > radius * radius) continue;
        const sx = x + dx, sz = z + dz;
        if (!this.insideXZ(sx, sz)) continue;
        const y = this._lowestStandable(sx, sz);
        if (!Number.isNaN(y)) samples.push([sx, y, sz]);
      }
    }
    if (samples.length === 0) {
      return { x, y: this.oy + this.ny * this.voxel * 0.5, z, ok: false };
    }
    const bin = 0.2;
    const hist = new Map();
    for (const s of samples) {
      const b = Math.round(s[1] / bin);
      hist.set(b, (hist.get(b) || 0) + 1);
    }
    let floorBin = 0, bestCount = -1;
    for (const [b, c] of hist) {
      // Neighbouring bins count too - a sloped or noisy floor straddles them.
      const c3 = c + (hist.get(b - 1) || 0) + (hist.get(b + 1) || 0);
      if (c3 > bestCount) { bestCount = c3; floorBin = b; }
    }
    const floorY = floorBin * bin;
    let best = null, bestD = Infinity;
    for (const s of samples) {
      if (Math.abs(s[1] - floorY) > bin * 1.5) continue;
      // Prefer spots with room to walk: all four neighbours standable too.
      let open = true;
      for (const [ox, oz] of [[0.5, 0], [-0.5, 0], [0, 0.5], [0, -0.5]]) {
        if (Number.isNaN(this._standAt(s[0] + ox, s[2] + oz, s[1]))) { open = false; break; }
      }
      if (!open) continue;
      const d = (s[0] - x) ** 2 + (s[2] - z) ** 2;
      if (d < bestD) { bestD = d; best = s; }
    }
    if (!best) {
      for (const s of samples) {
        const d = (s[0] - x) ** 2 + (s[2] - z) ** 2;
        if (Math.abs(s[1] - floorY) <= bin * 1.5 && d < bestD) { bestD = d; best = s; }
      }
    }
    return { x: best[0], y: best[1], z: best[2], ok: true, floorY };
  }

  // findSpawn near the capture origin first (where a generator's camera
  // stood), then around the middle of the scene with a radius that covers it.
  findSpawnAnywhere() {
    const near = this.findSpawn(0, 0, 8);
    if (near.ok) return near;
    const b = this.stats.bounds;
    const cx = (b.min[0] + b.max[0]) / 2, cz = (b.min[2] + b.max[2]) / 2;
    const r = Math.min(60, Math.max(b.max[0] - b.min[0], b.max[2] - b.min[2]) / 2);
    return this.findSpawn(cx, cz, r);
  }

  // The most open direction from a standing spot: yaw (three.js view
  // convention, forward = (-sin, 0, -cos)) whose ray at chest height runs
  // furthest. `preferYaw` wins unless something is clearly more open, so a
  // capture's intended view (Marble looks down -Z) is kept when it is usable.
  openYaw(x, feetY, z, preferYaw = 0) {
    const y = feetY + 1.2;
    const reach = (yaw) => Math.min(12, this.raycast(x, y, z, -Math.sin(yaw), 0, -Math.cos(yaw), 12));
    let best = preferYaw, bestD = reach(preferYaw);
    const preferD = bestD;
    for (let k = 0; k < 16; ++k) {
      const yaw = (k / 16) * Math.PI * 2;
      const d = reach(yaw);
      if (d > bestD) { bestD = d; best = yaw; }
    }
    return preferD >= Math.min(3, bestD * 0.6) ? preferYaw : best;
  }

  // Floor area (m²) a player can walk to from the spawn, counted up to
  // `cap`. A few square metres means "a statue / a sofa on its own", which is
  // better looked at than walked on.
  reachableArea(spawn, cap = 12) {
    const v = Math.max(this.voxel, 0.15);
    const maxCells = Math.ceil(cap / (v * v));
    const seen = new Set();
    const key = (i, k) => i * 100003 + k;
    const queue = [[0, 0, spawn.y]];
    seen.add(key(0, 0));
    let n = 0;
    while (queue.length && n < maxCells) {
      const [i, k, y] = queue.shift();
      ++n;
      for (const [di, dk] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
        const ni = i + di, nk = k + dk;
        if (seen.has(key(ni, nk))) continue;
        seen.add(key(ni, nk));
        const g = this._standAt(spawn.x + ni * v, spawn.z + nk * v, y);
        if (Number.isNaN(g) || g - y > this.player.stepHeight) continue;
        queue.push([ni, nk, g]);
      }
    }
    return n * v * v;
  }

  // How much stands on the floor around the spawn: solid voxels between knee
  // and head height. Furniture, walls, plants and people-height detail sit on
  // a real floor; under an upside-down ceiling that band is mostly empty.
  // Used to pick which way up an unknown capture goes.
  uprightScore(spawn, radius = 6) {
    const v = this.voxel;
    const r = Math.ceil(radius / v);
    const cix = this.ix(spawn.x), ciz = this.iz(spawn.z);
    const y0 = this.iy(spawn.y + 0.3), y1 = this.iy(spawn.y + 1.5);
    let n = 0;
    for (let iz = ciz - r; iz <= ciz + r; ++iz) {
      for (let ix = cix - r; ix <= cix + r; ++ix) {
        if ((ix - cix) ** 2 + (iz - ciz) ** 2 > r * r) continue;
        for (let iy = y0; iy <= y1; ++iy) if (this.solidCell(ix, iy, iz)) n++;
      }
    }
    return n;
  }

  // Is a head at (x, y, z) inside solid geometry? Used to fade the view out
  // when someone leans (room-scale) through a wall the body cannot pass.
  headInside(x, y, z) {
    const r = 0.1;
    let n = 0;
    if (this.solidAt(x, y, z)) n++;
    if (this.solidAt(x + r, y, z)) n++;
    if (this.solidAt(x - r, y, z)) n++;
    if (this.solidAt(x, y + r, z)) n++;
    if (this.solidAt(x, y - r, z)) n++;
    if (this.solidAt(x, y, z + r)) n++;
    if (this.solidAt(x, y, z - r)) n++;
    return n >= 4;
  }

  _lowestStandable(x, z) {
    const ix = this.ix(x), iz = this.iz(z);
    for (let iy = 0; iy < this.ny; ++iy) {
      if (!this.solidCell(ix, iy, iz) || this.solidCell(ix, iy + 1, iz)) continue;
      const top = this.oy + (iy + 1) * this.voxel;
      const g = this.support(x, z, top);
      if (Number.isNaN(g) || Math.abs(g - top) > this.player.stepHeight) continue;
      if (!this.blocked(x, z, g)) return g;
    }
    return NaN;
  }

  // Solid voxels for the debug overlay, capped. Colour class relative to a
  // player standing at feetY: 0 = low enough to walk over (floor, rugs,
  // steps), 1 = in the body band, i.e. something that stops you.
  debugVoxels(cx, feetY, cz, radius, maxCount = 150_000) {
    const v = this.voxel;
    const r = Math.ceil(radius / v);
    const cix = this.ix(cx), ciz = this.iz(cz);
    const out = [];
    const yLo = Math.max(0, this.iy(feetY - 1.5)), yHi = Math.min(this.ny - 1, this.iy(feetY + 2.5));
    const stepTop = feetY + this.player.stepHeight;
    for (let iz = Math.max(0, ciz - r); iz <= Math.min(this.nz - 1, ciz + r); ++iz) {
      for (let ix = Math.max(0, cix - r); ix <= Math.min(this.nx - 1, cix + r); ++ix) {
        const ddx = ix - cix, ddz = iz - ciz;
        if (ddx * ddx + ddz * ddz > r * r) continue;
        for (let iy = yLo; iy <= yHi; ++iy) {
          if (!this.solidCell(ix, iy, iz)) continue;
          // Skip interior voxels - only the shell is visible anyway.
          if (this.solidCell(ix, iy + 1, iz) && this.solidCell(ix, iy - 1, iz) &&
              this.solidCell(ix + 1, iy, iz) && this.solidCell(ix - 1, iy, iz) &&
              this.solidCell(ix, iy, iz + 1) && this.solidCell(ix, iy, iz - 1)) continue;
          const y = this.oy + (iy + 0.5) * v;
          out.push(this.ox + (ix + 0.5) * v, y, this.oz + (iz + 0.5) * v, y > stepTop ? 1 : 0);
          if (out.length >= maxCount * 4) return out;
        }
      }
    }
    return out;
  }
}

function now() {
  return typeof performance !== 'undefined' ? performance.now() : Date.now();
}

// Columns of the rotation matrix for quaternion (x, y, z, w).
function quatAxes(x, y, z, w, out) {
  const x2 = x + x, y2 = y + y, z2 = z + z;
  const xx = x * x2, xy = x * y2, xz = x * z2;
  const yy = y * y2, yz = y * z2, zz = z * z2;
  const wx = w * x2, wy = w * y2, wz = w * z2;
  out[0] = 1 - (yy + zz); out[1] = xy + wz; out[2] = xz - wy;
  out[3] = xy - wz; out[4] = 1 - (xx + zz); out[5] = yz + wx;
  out[6] = xz + wy; out[7] = yz - wx; out[8] = 1 - (xx + yy);
}

function percentileRange(pos, count, axis, p) {
  if (count === 0) return [-1, 1];
  const stride = Math.max(1, Math.floor(count / 200_000));
  const vals = new Float32Array(Math.ceil(count / stride));
  let n = 0;
  for (let i = 0; i < count; i += stride) vals[n++] = pos[i * 3 + axis];
  const sorted = vals.subarray(0, n).sort();
  const lo = sorted[Math.floor(p * (n - 1))];
  const hi = sorted[Math.ceil((1 - p) * (n - 1))];
  return [lo, hi];
}
