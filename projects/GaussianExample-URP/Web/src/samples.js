// Sample scenes, served from Web/SplatSamples at /samples (see vite.config.js).
// The PLY twins are the same captures uncompressed (34 MB each) and are only
// listed in dev; the deployed site ships the SPZ files. `upright` marks files
// known to be +Y up (Marble exports), which skips the which-way-up check.

export const SAMPLES = [
  {
    id: 'living-room',
    name: 'Cozy living room',
    kind: 'Indoor',
    sub: 'The sofa, coffee table and armchairs all stop you',
    tags: ['Indoor', '500K splats', 'SPZ 7.0 MB'],
    thumb: '/thumbs/living-room.jpg',
    url: '/samples/project-cozy-living-room-interior.spz',
    upright: true,
  },
  {
    id: 'bamboo-courtyard',
    name: 'Bamboo courtyard',
    kind: 'Outdoor',
    sub: 'Pool edges, bamboo groves and stone steps',
    tags: ['Outdoor', '500K splats', 'SPZ 7.3 MB'],
    thumb: '/thumbs/bamboo-courtyard.jpg',
    url: '/samples/project-ancient-chinese-bamboo-courtyard.spz',
    upright: true,
  },
  ...(import.meta.env.DEV
    ? [
        { id: 'living-room-ply', name: 'Cozy living room (PLY)', dev: true, url: '/samples/project-cozy-living-room-interior.ply', upright: true },
        { id: 'bamboo-courtyard-ply', name: 'Bamboo courtyard (PLY)', dev: true, url: '/samples/project-ancient-chinese-bamboo-courtyard.ply', upright: true },
      ]
    : []),
];
