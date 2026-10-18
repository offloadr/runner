#!/usr/bin/env node
// Interoperability vectors for the sealed envelope format (docs/sealed-envelope.md), written
// against the WebCrypto API (crypto.subtle) alone, as a browser would hold the keys.
//
//   node build/sealed-envelope-vectors.mjs generate <vectors.json>   write fresh vectors
//   node build/sealed-envelope-vectors.mjs decrypt <vectors.json>    open and verify every vector
//
// The .NET tests decrypt the committed generated vectors and ask this script to decrypt
// vectors that .NET produced. Exit status is non-zero when any vector fails.
import { readFileSync, writeFileSync } from 'node:fs';

// The WebCrypto API only. Node 18 (Debian bookworm) exposes it as node:crypto's webcrypto
// rather than as globalThis.crypto; it is the same Crypto object either way.
const webcrypto = globalThis.crypto ?? (await import('node:crypto')).webcrypto;
const subtle = webcrypto.subtle;

const ALGORITHM = 'hpke-p256-sha256-aes128gcm+stream-aes256gcm-64k-v1';
const MAGIC = ascii('OFLDSEAL');
const VERSION = 1;
const KEM_ID = 0x0010;
const KDF_ID = 0x0001;
const AEAD_ID = 0x0001;
const PAYLOAD_ID = 0x0001;
const CHUNK_SIZE = 65536;
const TAG_SIZE = 16;
const MAX_PLAINTEXT_SIZE = 2 ** 32;
const MAX_HEADER_SIZE = 8192;
const DATA_KEY_SIZE = 32;
const INFO = ascii('offloadr sealed v1');
const EMPTY = new Uint8Array(0);
const KEM_SUITE = concat(ascii('KEM'), u16(KEM_ID));
const HPKE_SUITE = concat(ascii('HPKE'), u16(KEM_ID), u16(KDF_ID), u16(AEAD_ID));

// Identity kinds and their fields in header order: [name, max UTF-8 bytes, may be empty].
const IDENTITIES = {
  artifact: [0x01, [['session_id', 128], ['editor_sid', 128], ['type', 64], ['subfolder', 1024, true], ['filename', 255]]],
  workspace_file: [0x02, [['session_id', 128], ['root', 64], ['relative_path', 4096]]],
  seed_archive: [0x03, [['session_id', 128], ['root', 64]]],
};
const KEY_ID_MAX = 128;

// ---- bytes ---------------------------------------------------------------------------------

function ascii(text) {
  return new TextEncoder().encode(text);
}

function concat(...parts) {
  const out = new Uint8Array(parts.reduce((sum, part) => sum + part.length, 0));
  let offset = 0;
  for (const part of parts) {
    out.set(part, offset);
    offset += part.length;
  }
  return out;
}

function u16(value) {
  const out = new Uint8Array(2);
  new DataView(out.buffer).setUint16(0, value);
  return out;
}

function u32(value) {
  const out = new Uint8Array(4);
  new DataView(out.buffer).setUint32(0, value);
  return out;
}

function u64(value) {
  const out = new Uint8Array(8);
  new DataView(out.buffer).setBigUint64(0, BigInt(value));
  return out;
}

const toHex = (bytes) => Buffer.from(bytes).toString('hex');
const fromHex = (text) => new Uint8Array(Buffer.from(text, 'hex'));
const toBase64 = (bytes) => Buffer.from(bytes).toString('base64');
const fromBase64 = (text) => new Uint8Array(Buffer.from(text, 'base64'));
const toBase64Url = (bytes) => Buffer.from(bytes).toString('base64url');
const fromBase64Url = (text) => new Uint8Array(Buffer.from(text, 'base64url'));

function equalBytes(a, b) {
  return a.length === b.length && a.every((value, index) => value === b[index]);
}

function randomBytes(length) {
  const out = new Uint8Array(length);
  // getRandomValues fills at most 65536 bytes per call.
  for (let offset = 0; offset < length; offset += 65536) {
    webcrypto.getRandomValues(out.subarray(offset, Math.min(length, offset + 65536)));
  }
  return out;
}

async function sha256Hex(bytes) {
  return toHex(new Uint8Array(await subtle.digest('SHA-256', bytes)));
}

// ---- HPKE (RFC 9180) base mode, DHKEM(P-256, HKDF-SHA256) / HKDF-SHA256 / AES-128-GCM -------

// WebCrypto has no HKDF-Extract on its own, so HKDF is built from HMAC. WebCrypto also refuses
// empty HMAC keys; an empty salt is equivalent to HashLen zero bytes (RFC 5869, section 2.2).
async function hmac(key, data) {
  const hmacKey = await subtle.importKey('raw', key.length === 0 ? new Uint8Array(32) : key,
    { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return new Uint8Array(await subtle.sign('HMAC', hmacKey, data));
}

async function expand(prk, info, length) {
  const out = new Uint8Array(length);
  let block = EMPTY;
  for (let counter = 1, offset = 0; offset < length; counter++) {
    block = await hmac(prk, concat(block, info, Uint8Array.of(counter)));
    out.set(block.subarray(0, Math.min(block.length, length - offset)), offset);
    offset += block.length;
  }
  return out;
}

const labeledExtract = (suite, salt, label, ikm) =>
  hmac(salt, concat(ascii('HPKE-v1'), suite, ascii(label), ikm));

const labeledExpand = (suite, prk, label, info, length) =>
  expand(prk, concat(u16(length), ascii('HPKE-v1'), suite, ascii(label), info), length);

async function extractAndExpand(dh, kemContext) {
  const eaePrk = await labeledExtract(KEM_SUITE, EMPTY, 'eae_prk', dh);
  return labeledExpand(KEM_SUITE, eaePrk, 'shared_secret', kemContext, 32);
}

async function keySchedule(sharedSecret, info) {
  const pskIdHash = await labeledExtract(HPKE_SUITE, EMPTY, 'psk_id_hash', EMPTY);
  const infoHash = await labeledExtract(HPKE_SUITE, EMPTY, 'info_hash', info);
  const context = concat(Uint8Array.of(0x00), pskIdHash, infoHash);
  const secret = await labeledExtract(HPKE_SUITE, sharedSecret, 'secret', EMPTY);
  return {
    key: await labeledExpand(HPKE_SUITE, secret, 'key', context, 16),
    baseNonce: await labeledExpand(HPKE_SUITE, secret, 'base_nonce', context, 12),
  };
}

async function importPublicKey(publicKey) {
  if (publicKey.length !== 65 || publicKey[0] !== 0x04) {
    throw new Error('public key is not a 65-byte uncompressed P-256 point');
  }
  // Importing a raw EC point checks that it lies on the curve.
  return subtle.importKey('raw', publicKey, { name: 'ECDH', namedCurve: 'P-256' }, true, []);
}

async function importPrivateKey(privateKey, publicKey) {
  const jwk = {
    kty: 'EC',
    crv: 'P-256',
    x: toBase64Url(publicKey.subarray(1, 33)),
    y: toBase64Url(publicKey.subarray(33, 65)),
    d: toBase64Url(privateKey),
  };
  return subtle.importKey('jwk', jwk, { name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
}

async function aesGcm(operation, key, iv, data, additionalData) {
  const aesKey = await subtle.importKey('raw', key, 'AES-GCM', false, [operation]);
  const params = { name: 'AES-GCM', iv, tagLength: 128 };
  if (additionalData) {
    params.additionalData = additionalData;
  }
  return new Uint8Array(await subtle[operation](params, aesKey, data));
}

// Single-shot seal: sequence number 0, so the nonce is the base nonce.
async function hpkeSeal(recipientPublicKey, info, aad, plaintext) {
  const recipient = await importPublicKey(recipientPublicKey);
  const ephemeral = await subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, true, ['deriveBits']);
  const encapsulatedKey = new Uint8Array(await subtle.exportKey('raw', ephemeral.publicKey));
  const dh = new Uint8Array(await subtle.deriveBits({ name: 'ECDH', public: recipient }, ephemeral.privateKey, 256));
  const sharedSecret = await extractAndExpand(dh, concat(encapsulatedKey, recipientPublicKey));
  const { key, baseNonce } = await keySchedule(sharedSecret, info);
  return { encapsulatedKey, ciphertext: await aesGcm('encrypt', key, baseNonce, plaintext, aad) };
}

async function hpkeOpen(recipientPrivateKey, recipientPublicKey, encapsulatedKey, info, aad, ciphertext) {
  const ephemeral = await importPublicKey(encapsulatedKey);
  const dh = new Uint8Array(await subtle.deriveBits({ name: 'ECDH', public: ephemeral }, recipientPrivateKey, 256));
  const sharedSecret = await extractAndExpand(dh, concat(encapsulatedKey, recipientPublicKey));
  const { key, baseNonce } = await keySchedule(sharedSecret, info);
  return aesGcm('decrypt', key, baseNonce, ciphertext, aad);
}

// ---- header --------------------------------------------------------------------------------

function encodeString(value, max, allowEmpty) {
  const bytes = new TextEncoder().encode(value);
  if (bytes.length > max || (bytes.length === 0 && !allowEmpty)) {
    throw new Error(`header string has ${bytes.length} bytes, allowed ${allowEmpty ? 0 : 1}..${max}`);
  }
  return concat(u16(bytes.length), bytes);
}

function encodeHeader({ keyId, plaintextSize, identity }) {
  const [kind, fields] = IDENTITIES[identity.kind];
  return concat(
    MAGIC, u16(VERSION), u16(KEM_ID), u16(KDF_ID), u16(AEAD_ID), u16(PAYLOAD_ID), u32(CHUNK_SIZE), u64(plaintextSize),
    encodeString(keyId, KEY_ID_MAX, false),
    Uint8Array.of(kind),
    ...fields.map(([name, max, allowEmpty]) => encodeString(identity[name], max, allowEmpty)));
}

function parseHeader(bytes) {
  if (bytes.length > MAX_HEADER_SIZE) {
    throw new Error('header too large');
  }
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let offset = 0;
  const take = (length) => {
    if (offset + length > bytes.length) {
      throw new Error('header truncated');
    }
    offset += length;
    return offset - length;
  };
  const decoder = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });
  const string = (max, allowEmpty) => {
    const length = view.getUint16(take(2));
    if (length > max || (length === 0 && !allowEmpty)) {
      throw new Error('header string length out of range');
    }
    const start = take(length);
    return decoder.decode(bytes.subarray(start, start + length));
  };

  if (!equalBytes(bytes.subarray(take(8), 8), MAGIC)) {
    throw new Error('not a sealed envelope header');
  }
  const numbers = [view.getUint16(take(2)), view.getUint16(take(2)), view.getUint16(take(2)),
    view.getUint16(take(2)), view.getUint16(take(2)), view.getUint32(take(4))];
  if (!equalBytes(numbers, [VERSION, KEM_ID, KDF_ID, AEAD_ID, PAYLOAD_ID, CHUNK_SIZE])) {
    throw new Error('unsupported version, suite or chunk size');
  }
  const plaintextSize = view.getBigUint64(take(8));
  if (plaintextSize > BigInt(MAX_PLAINTEXT_SIZE)) {
    throw new Error('plaintext size over 4 GiB');
  }
  const keyId = string(KEY_ID_MAX, false);
  const kindByte = bytes[take(1)];
  const entry = Object.entries(IDENTITIES).find(([, [kind]]) => kind === kindByte);
  if (!entry) {
    throw new Error('unknown identity kind');
  }
  const identity = { kind: entry[0] };
  for (const [name, max, allowEmpty] of entry[1][1]) {
    identity[name] = string(max, allowEmpty);
  }
  if (offset !== bytes.length) {
    throw new Error('header has trailing bytes');
  }
  return { keyId, plaintextSize: Number(plaintextSize), identity };
}

function sameIdentity(a, b) {
  const [, fields] = IDENTITIES[a.kind] ?? [];
  return a.kind === b.kind && fields.every(([name]) => a[name] === b[name]);
}

// ---- STREAM payload: AES-256-GCM, 64 KiB chunks --------------------------------------------

const chunkCount = (size) => (size === 0 ? 1 : Math.ceil(size / CHUNK_SIZE));

function chunkNonce(counter, final) {
  const nonce = new Uint8Array(12);
  new DataView(nonce.buffer).setBigUint64(3, BigInt(counter)); // bytes 0..10: 11-byte big-endian counter
  nonce[11] = final ? 0x01 : 0x00;
  return nonce;
}

async function sealPayload(dataKey, plaintext) {
  const key = await subtle.importKey('raw', dataKey, 'AES-GCM', false, ['encrypt']);
  const count = chunkCount(plaintext.length);
  const chunks = [];
  for (let index = 0; index < count; index++) {
    const chunk = plaintext.subarray(index * CHUNK_SIZE, Math.min(plaintext.length, (index + 1) * CHUNK_SIZE));
    const iv = chunkNonce(index, index === count - 1);
    chunks.push(new Uint8Array(await subtle.encrypt({ name: 'AES-GCM', iv, tagLength: 128 }, key, chunk)));
  }
  return concat(...chunks);
}

async function openPayload(dataKey, ciphertext, plaintextSize) {
  const count = chunkCount(plaintextSize);
  if (ciphertext.length < plaintextSize + (count * TAG_SIZE)) {
    throw new Error('ciphertext ends before the final chunk');
  }
  if (ciphertext.length > plaintextSize + (count * TAG_SIZE)) {
    throw new Error('ciphertext continues after the final chunk');
  }
  const key = await subtle.importKey('raw', dataKey, 'AES-GCM', false, ['decrypt']);
  const chunks = [];
  for (let index = 0, offset = 0; index < count; index++) {
    const length = (index < count - 1 ? CHUNK_SIZE : plaintextSize - (index * CHUNK_SIZE)) + TAG_SIZE;
    const iv = chunkNonce(index, index === count - 1);
    chunks.push(new Uint8Array(await subtle.decrypt({ name: 'AES-GCM', iv, tagLength: 128 },
      key, ciphertext.subarray(offset, offset + length))));
    offset += length;
  }
  return concat(...chunks);
}

// ---- envelope ------------------------------------------------------------------------------

async function seal(recipient, identity, plaintext) {
  const header = encodeHeader({ keyId: recipient.keyId, plaintextSize: plaintext.length, identity });
  const dataKey = randomBytes(DATA_KEY_SIZE); // fresh for every encryption
  const { encapsulatedKey, ciphertext: sealedDataKey } = await hpkeSeal(recipient.publicKey, INFO, header, dataKey);
  return {
    envelope: { header, encapsulatedKey, sealedDataKey },
    ciphertext: await sealPayload(dataKey, plaintext),
  };
}

async function open(vector) {
  const publicKey = fromHex(vector.recipient.public_key);
  const privateKey = await importPrivateKey(fromHex(vector.recipient.private_key), publicKey);
  const header = fromHex(vector.envelope.header);
  const parsed = parseHeader(header);
  if (parsed.keyId !== vector.recipient.key_id) {
    throw new Error('envelope is sealed to a different key id');
  }
  if (!sameIdentity(parsed.identity, vector.identity)) {
    throw new Error('envelope belongs to a different object than the one requested');
  }
  if (parsed.plaintextSize !== vector.plaintext_size) {
    throw new Error('plaintext size differs from the vector');
  }
  const dataKey = await hpkeOpen(privateKey, publicKey, fromHex(vector.envelope.encapsulated_key),
    INFO, header, fromHex(vector.envelope.sealed_data_key));
  if (dataKey.length !== DATA_KEY_SIZE) {
    throw new Error('data key has the wrong length');
  }
  return openPayload(dataKey, fromBase64(vector.ciphertext), parsed.plaintextSize);
}

// ---- commands ------------------------------------------------------------------------------

const SAMPLES = [
  ['artifact-empty', 0, { kind: 'artifact', session_id: 'session-0001', editor_sid: 'editor-0001', type: 'output', subfolder: '', filename: 'empty.txt' }],
  ['workspace-file-1', 1, { kind: 'workspace_file', session_id: 'session-0001', root: 'user', relative_path: 'default/workflows/räksmörgås.json' }],
  ['seed-archive-65536', 65536, { kind: 'seed_archive', session_id: 'session-0001', root: 'custom_nodes' }],
  ['artifact-65537', 65537, { kind: 'artifact', session_id: 'session-0001', editor_sid: 'editor-0001', type: 'input', subfolder: '2026/10', filename: 'photo.png' }],
];

async function generate(path) {
  const recipientKeys = await subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, true, ['deriveBits']);
  const publicKey = new Uint8Array(await subtle.exportKey('raw', recipientKeys.publicKey));
  const privateKey = fromBase64Url((await subtle.exportKey('jwk', recipientKeys.privateKey)).d);
  const recipient = { keyId: 'webcrypto-recipient-1', publicKey };

  const vectors = [];
  for (const [name, size, identity] of SAMPLES) {
    const plaintext = randomBytes(size);
    const { envelope, ciphertext } = await seal(recipient, identity, plaintext);
    vectors.push({
      name,
      recipient: { key_id: recipient.keyId, public_key: toHex(publicKey), private_key: toHex(privateKey) },
      identity,
      plaintext_size: size,
      plaintext_sha256: await sha256Hex(plaintext),
      envelope: {
        header: toHex(envelope.header),
        encapsulated_key: toHex(envelope.encapsulatedKey),
        sealed_data_key: toHex(envelope.sealedDataKey),
      },
      ciphertext: toBase64(ciphertext),
    });
  }

  const document = {
    format: 'offloadr-sealed-envelope-vectors',
    algorithm: ALGORITHM,
    generator: 'build/sealed-envelope-vectors.mjs (WebCrypto)',
    vectors,
  };
  await decryptDocument(document);
  writeFileSync(path, `${JSON.stringify(document, null, 2)}\n`);
  console.log(`wrote ${vectors.length} vectors to ${path}`);
}

async function decryptDocument(document) {
  if (document.algorithm !== ALGORITHM) {
    throw new Error(`unsupported algorithm ${document.algorithm}`);
  }
  for (const vector of document.vectors) {
    const plaintext = await open(vector);
    if (plaintext.length !== vector.plaintext_size || await sha256Hex(plaintext) !== vector.plaintext_sha256) {
      throw new Error(`${vector.name}: plaintext does not match`);
    }
  }
}

async function main([command, path]) {
  if (command === 'generate' && path) {
    await generate(path);
  } else if (command === 'decrypt' && path) {
    const document = JSON.parse(readFileSync(path, 'utf8'));
    await decryptDocument(document);
    console.log(`ok: ${document.vectors.map((vector) => vector.name).join(', ')}`);
  } else {
    console.error('usage: node build/sealed-envelope-vectors.mjs generate|decrypt <vectors.json>');
    process.exitCode = 2;
  }
}

main(process.argv.slice(2)).catch((error) => {
  console.error(`sealed-envelope-vectors: ${error.message}`);
  process.exitCode = 1;
});
