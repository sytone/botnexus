// Cross-language pre-build binding. Matches snapshot.py: SHA256 of sorted sha256sum lines.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const [root, expected] = process.argv.slice(2);
if (!root || !/^[0-9a-f]{64}$/.test(expected || '')) throw new Error('Expected source context digest is required.');
const entries = [];
const walk = (directory, relative = '') => {
  for (const name of fs.readdirSync(directory)) {
    const item = path.join(directory, name);
    const local = relative ? `${relative}/${name}` : name;
    const stat = fs.lstatSync(item);
    if (stat.isSymbolicLink()) throw new Error('Source context contains a link.');
    if (stat.isDirectory()) walk(item, local);
    else if (stat.isFile()) entries.push([Buffer.from(local), crypto.createHash('sha256').update(fs.readFileSync(item)).digest('hex')]);
    else throw new Error('Source context contains a special entry.');
  }
};
walk(root);
entries.sort((a, b) => Buffer.compare(a[0], b[0]));
const digest = crypto.createHash('sha256');
for (const [name, hash] of entries) digest.update(Buffer.concat([Buffer.from(`${hash}  ./`), name, Buffer.from('\n')]));
const actual = digest.digest('hex');
if (actual !== expected) throw new Error('Source context digest mismatch.');
process.stdout.write(`${actual}\n`);
