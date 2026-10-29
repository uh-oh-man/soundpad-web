import { readFile, writeFile, mkdir } from 'node:fs/promises';
const packages=['react','react-dom','scheduler','lucide-react','zod','trystero','@trystero-p2p/core','@trystero-p2p/nostr','@noble/secp256k1'];
let text='Soundpad Web — browser third-party notices\n\n';
for(const name of packages){const meta=JSON.parse(await readFile(`node_modules/${name}/package.json`,'utf8'));text+=`\n=== ${name} ${meta.version} ===\n\n${await readFile(`node_modules/${name}/LICENSE`,'utf8')}\n`;}
text+='\n=== Leppsoft remote-control reference ===\n\n'+await readFile('docs/licenses/Soundpad-reference.txt','utf8');
await mkdir('web/public',{recursive:true});await writeFile('web/public/THIRD-PARTY-NOTICES.txt',text);
