// Sample scenes, served from Web/SplatSamples at /samples (see vite.config.js).
// The PLY twins are the same captures uncompressed (34 MB each) and are only
// listed in dev; the deployed site ships the SPZ files.

export const SAMPLES = [
  {
    id: 'living-room',
    name: '温馨客厅 · Cozy living room',
    url: '/samples/project-cozy-living-room-interior.spz',
  },
  {
    id: 'bamboo-courtyard',
    name: '竹林庭院 · Bamboo courtyard',
    url: '/samples/project-ancient-chinese-bamboo-courtyard.spz',
  },
  ...(import.meta.env.DEV
    ? [
        { id: 'living-room-ply', name: '温馨客厅 (PLY)', url: '/samples/project-cozy-living-room-interior.ply' },
        { id: 'bamboo-courtyard-ply', name: '竹林庭院 (PLY)', url: '/samples/project-ancient-chinese-bamboo-courtyard.ply' },
      ]
    : []),
];
