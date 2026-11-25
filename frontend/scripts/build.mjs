// Build de producción. VITE_DEMO_MODE=true compila el modo demo (API simulada en el navegador); cualquier otro valor
// compila contra la API real. Lo usa Vercel: define VITE_DEMO_MODE=true en el proyecto (o en vercel.json).
import { spawnSync } from 'node:child_process';

const demo = process.env.VITE_DEMO_MODE === 'true';
console.log(`Compilando en modo ${demo ? 'DEMO (API simulada)' : 'normal (API real)'}...`);

const result = spawnSync('npx', ['ng', 'build', '--configuration', demo ? 'production,demo' : 'production'], {
  stdio: 'inherit',
  shell: true
});
process.exit(result.status ?? 1);
