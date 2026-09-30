// Rebuilds oracle.json from corpus.txt with the acorn tokenizer that ships inside node.
// Run: node --expose-internals generate-oracle.cjs
// Entries in corpus.txt are separated by a line that holds only four dashes.
// acorn is an internal module of node, so a node upgrade can move it, and this script then refuses.
// An entry that acorn cannot finish records the kind of text left open, and the test reads that field, never the message.
'use strict';
const fs = require('fs');
const path = require('path');

const NL = String.fromCharCode(10);
const CR = String.fromCharCode(13);
const oraclePath = path.join(__dirname, 'oracle.json');

let acorn;
try {
  acorn = require('internal/deps/acorn/acorn/dist/acorn');
} catch (e) {
  let last = 'no earlier run';
  try {
    last = JSON.parse(fs.readFileSync(oraclePath, 'utf8'))[0].node;
  } catch (ignored) {
    last = 'an unreadable oracle.json';
  }
  console.error(`refused: node ${process.version} does not load internal/deps/acorn/acorn/dist/acorn. Run it with --expose-internals. The oracle was last generated under node ${last}.`);
  process.exit(1);
}

const raw = fs.readFileSync(path.join(__dirname, 'corpus.txt'), 'utf8').split(CR + NL).join(NL);
const entries = [];
for (let chunk of raw.split(NL + '----' + NL)) {
  while (chunk.endsWith(NL)) chunk = chunk.slice(0, -1);
  if (chunk.trim().length > 0) entries.push(chunk);
}

function openKind(source, pos, lastToken, number) {
  if (typeof pos === 'number' && source.startsWith('/*', pos)) return 'comment';
  if (typeof pos === 'number' && (source[pos] === "'" || source[pos] === '"')) return 'string';
  if (lastToken && (lastToken.k === '`' || lastToken.k === 'template')) return 'template';
  console.error(`refused: entry ${number} stops at text this script cannot name: ${JSON.stringify(source.slice(pos, pos + 20))}`);
  process.exit(1);
}

const lines = entries.map((source, index) => {
  const tokens = [];
  const comments = [];
  let error = null;
  let unterminated = null;
  try {
    for (const t of acorn.tokenizer(source, { ecmaVersion: 'latest', sourceType: 'module', onComment: comments })) {
      tokens.push({ k: t.type.label, kw: t.type.keyword || null, s: t.start, e: t.end });
    }
  } catch (e) {
    error = e.message;
    unterminated = openKind(source, e.pos, tokens[tokens.length - 1], index + 1);
  }
  for (const c of comments) tokens.push({ k: 'comment', kw: null, s: c.start, e: c.end });
  tokens.sort((a, b) => a.s - b.s);
  return JSON.stringify({ source, node: process.version, acorn: acorn.version, tokens, error, unterminated });
});

fs.writeFileSync(oraclePath, '[' + NL + lines.join(',' + NL) + NL + ']' + NL, 'utf8');
console.log(`wrote ${lines.length} entries with acorn ${acorn.version} under node ${process.version}`);
