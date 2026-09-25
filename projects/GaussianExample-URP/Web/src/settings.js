// Viewer settings, remembered per browser. Defaults are the Unity rig's
// serialized values (VRCameraRigController / PlayerController).

const KEY = 'ukemixr.splatwalk.settings.v1';

export const DEFAULTS = {
  follow: 'auto', // auto | discrete | smooth
  catchUp: 0.25,
  orbit: 2.5,
  snap: 35,
  speed: 2.5,
  blink: true,
  debug: false,
  solid: 1.0,
  xrScale: 0.75,
};

export function loadSettings() {
  let saved = {};
  try {
    saved = JSON.parse(localStorage.getItem(KEY) || '{}');
  } catch { /* private mode or blocked storage */ }
  const s = { ...DEFAULTS, ...saved };
  const q = new URLSearchParams(location.search);
  if (q.has('debug')) s.debug = true;
  return s;
}

export function saveSettings(s) {
  try {
    localStorage.setItem(KEY, JSON.stringify(s));
  } catch { /* ignore */ }
}
