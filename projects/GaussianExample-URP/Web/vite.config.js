import { defineConfig } from 'vite';
import fs from 'node:fs';
import path from 'node:path';

// Web/SplatSamples holds the sample captures. Serve it at /samples in dev and
// copy the .spz files into the build (the .ply twins are 34 MB each and are
// the same scenes, so they stay out of the deployment).
const samplesDir = path.resolve(import.meta.dirname, 'SplatSamples');

function samples() {
  return {
    name: 'ukemixr-samples',
    configureServer(server) {
      server.middlewares.use('/samples', (req, res, next) => {
        const file = path.join(samplesDir, decodeURIComponent(req.url.split('?')[0]));
        if (!file.startsWith(samplesDir) || !fs.existsSync(file) || !fs.statSync(file).isFile()) return next();
        res.setHeader('Content-Length', fs.statSync(file).size);
        res.setHeader('Content-Type', 'application/octet-stream');
        fs.createReadStream(file).pipe(res);
      });
    },
    closeBundle() {
      const out = path.resolve(import.meta.dirname, 'dist/samples');
      fs.mkdirSync(out, { recursive: true });
      for (const f of fs.readdirSync(samplesDir)) {
        if (f.endsWith('.spz')) fs.copyFileSync(path.join(samplesDir, f), path.join(out, f));
      }
    },
  };
}

export default defineConfig({
  plugins: [samples()],
  build: { target: 'es2022', chunkSizeWarningLimit: 4000 },
  server: { host: '127.0.0.1', port: 5173 },
});
