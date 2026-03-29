import { readFileSync, writeFileSync } from 'fs';

const root = JSON.parse(readFileSync(new URL('../package.json', import.meta.url)));
const version = root.version;

// Update js/package.json
const jsPkgPath = new URL('../js/package.json', import.meta.url);
const jsPkg = JSON.parse(readFileSync(jsPkgPath));
if (jsPkg.version !== version) {
  jsPkg.version = version;
  writeFileSync(jsPkgPath, JSON.stringify(jsPkg, null, 2) + '\n');
}

// Update csharp .csproj
const csprojPath = new URL('../csharp/SimpleVCLib/SimpleVCLib.csproj', import.meta.url);
const csproj = readFileSync(csprojPath, 'utf8');
const updated = csproj.replace(/<Version>.*?<\/Version>/, `<Version>${version}</Version>`);
if (updated !== csproj) {
  writeFileSync(csprojPath, updated);
}

console.log(`Versions synced to ${version}`);
