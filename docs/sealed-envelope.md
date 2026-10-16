# Sealed envelopes

A sealed envelope encrypts one file in transit between a runner and the Offloadr control plane, so the transfer path and anything that stores it see only ciphertext. It protects artifact uploads and downloads, workspace files and workspace seed archives for sessions on rented GPUs.

This document is normative. The key words MUST, MUST NOT, SHOULD and MAY are used as in RFC 2119. The reference implementation is `src/Offloadr.Runner.Core/Sealing/`. `build/sealed-envelope-vectors.mjs` is a second implementation that uses only WebCrypto.

## Trust model

- The control plane is trusted. It holds the output recipient private key, so it can read every upload, and it seals every download. Sealed envelopes are not end-to-end encryption.
- Each runner session generates an ephemeral **reader key** pair. The private key lives only in the runner's memory and is never written to disk, logged or sent anywhere. The session disposes of it when it ends.
- Uploads (runner → control plane) are sealed to the control plane's **output recipient** public key. Downloads (control plane → runner) are sealed to the session's reader public key.
- A reader key is an encryption destination, never authorization. Holding a reader public key, or presenting one, grants no access. The control plane MUST authorize every transfer exactly as it would without sealing, and only then choose the key to seal to.
- HPKE base mode does not authenticate the sender: anyone who has a recipient public key can seal to it. Who sent an envelope is established by the authenticated transport that carried it, not by the envelope.

## Algorithm

The only algorithm id is:

```
hpke-p256-sha256-aes128gcm+stream-aes256gcm-64k-v1
```

- Key encapsulation: HPKE (RFC 9180) base mode, suite DHKEM(P-256, HKDF-SHA256) / HKDF-SHA256 / AES-128-GCM, which is KEM `0x0010`, KDF `0x0001`, AEAD `0x0001`.
- Payload: AES-256-GCM in the STREAM construction (Hoang, Reyhanitabar, Rogaway and Vizár, as used by age) with 64 KiB chunks.

Implementations MUST reject any other algorithm string. The comparison is exact and case-sensitive. They MUST also reject headers that name any other suite or payload. There is no negotiation and no fallback.

## Public keys

A sealing public key is carried as `SealingPublicKey { key_id, algorithm, public_key }`.

- `public_key` MUST be a 65-byte uncompressed SEC1 P-256 point: `0x04 || X || Y`, each coordinate 32 bytes big-endian. Compressed points, the point at infinity, coordinates not below the field prime and points not on the curve MUST be rejected. Validate on receipt, before any use.
- `key_id` is a non-empty UTF-8 string of at most 128 bytes, chosen by the key's owner. It names the key and is not secret. Reader keys SHOULD use a fresh random id per session.
- The same validation applies to the encapsulated key inside every envelope.

## Envelope

An envelope travels ahead of its payload as three byte strings:

| Field | Length | Contents |
| --- | --- | --- |
| `header` | 1–8192 bytes | the canonical header below |
| `encapsulated_key` | 65 bytes | HPKE `enc`: the sender's ephemeral P-256 public key, uncompressed |
| `sealed_data_key` | 48 bytes | HPKE ciphertext of the 32-byte data key (32 + 16-byte tag) |

The payload ciphertext follows, as one byte stream that the transport may split anywhere.

## Canonical header

The header has a fixed binary layout. Integers are unsigned big-endian. A *string* is a `u16` byte length followed by that many bytes of UTF-8. Strings MUST be well-formed UTF-8: no overlong forms, no surrogates and no truncated sequences. They are compared as exact byte sequences, with no Unicode normalization and no case folding.

| Offset | Size | Field | Value |
| --- | --- | --- | --- |
| 0 | 8 | magic | ASCII `OFLDSEAL` (`4f464c445345414c`) |
| 8 | 2 | format version | `1` |
| 10 | 2 | KEM id | `0x0010` |
| 12 | 2 | KDF id | `0x0001` |
| 14 | 2 | AEAD id | `0x0001` |
| 16 | 2 | payload id | `0x0001` (AES-256-GCM STREAM) |
| 18 | 4 | chunk size | `65536` |
| 22 | 8 | plaintext size | `0` to `2^32` (4 GiB) inclusive |
| 30 | string | key id | the recipient's `key_id`, 1–128 bytes |
| … | 1 | identity kind | `0x01`, `0x02` or `0x03` |
| … | strings | identity fields | per kind, in this order |

Object identity is a tagged union. Every field below is a string. Limits are in UTF-8 bytes, and every field is non-empty except `subfolder`.

| Kind | Object | Fields (maximum bytes) |
| --- | --- | --- |
| `0x01` | artifact | `session_id` (128), `editor_sid` (128), `type` (64), `subfolder` (0–1024), `filename` (255) |
| `0x02` | workspace file | `session_id` (128), `root` (64), `relative_path` (4096) |
| `0x03` | seed archive | `session_id` (128), `root` (64) |

`root` is the workspace root's lower-case name without the enum prefix: `user`, `custom_nodes` or `input`. Field values are exactly those of the transfer request they belong to. Senders MUST NOT normalize or rewrite them.

A parser MUST reject a header that is longer than 8192 bytes or truncated, has the wrong magic, version, suite ids, payload id or chunk size, declares a plaintext size over 4 GiB, has a string over its limit, an empty string where one is not allowed, malformed UTF-8 or an unknown identity kind, or has bytes after the last identity field. With those rules there is exactly one encoding of any header, so the received bytes are the canonical bytes.

Example: key id `read1`, plaintext size 65537, artifact `s1` / `e1` / `output` / `` / `a.png`:

```
4f464c445345414c 0001 0010 0001 0001 0001 00010000 0000000000010001
0005 7265616431
01
0002 7331  0002 6531  0006 6f7574707574  0000  0005 612e706e67
```

## Data key encapsulation

For every encryption, the sender:

1. Generates a fresh random 32-byte data key.
2. Encodes the header.
3. Runs HPKE `SealBase(pkR, info, aad, pt)` single-shot, with:
   - `pkR`: the recipient's public key;
   - `info`: ASCII `offloadr sealed v1` (18 bytes, no terminator);
   - `aad`: the exact header bytes;
   - `pt`: the data key.
4. Sends `enc` as `encapsulated_key` and the HPKE ciphertext as `sealed_data_key`.

HPKE is implemented exactly as RFC 9180 specifies: `LabeledExtract`, `LabeledExpand`, DHKEM `ExtractAndExpand` with `kem_context = enc || pkRm`, and the base-mode key schedule with empty `psk` and `psk_id`. The DH value is the 32-byte X coordinate of the shared point. Single-shot use means sequence number 0, so the AEAD nonce is `base_nonce`. Implementations MUST pass the RFC 9180 Appendix A.3 test vectors.

Because the header is the AAD, any change to it, whether key id, size or identity, makes the data key fail to open.

## Payload

The data key is used directly as the AES-256-GCM key. The payload is not bound to the header any further: the header binding comes from the data key's encapsulation. That is what lets the control plane re-wrap a stored payload (see below).

- Split the plaintext into 64 KiB chunks. Every chunk except the last is exactly 65536 bytes. The last chunk holds 1–65536 bytes.
- An empty plaintext is exactly one empty final chunk. A plaintext that is an exact multiple of 64 KiB ends with a full final chunk, never an extra empty one.
- So a payload of size `n` has `N = max(1, ceil(n / 65536))` chunks and `n + 16·N` ciphertext bytes.
- Chunk `i` (from 0) is encrypted with AES-256-GCM with a 16-byte tag, no AAD, and this 12-byte nonce:

  ```
  nonce = I2OSP(i, 11) || (i == N - 1 ? 0x01 : 0x00)
  ```

  It is an 11-byte big-endian counter followed by the final-chunk flag.
- Each chunk's ciphertext is followed by its tag. Chunks are concatenated in order, with no framing.
- The counter MUST NOT wrap. With the 4 GiB limit it never exceeds 65535, and implementations MUST reject any chunk index of 65536 or more as a counter overflow.

## Reading

A reader knows which object it asked for and which of its keys it published. It MUST, in this order:

1. Parse the header with the rules above.
2. Check that the header's key id equals the key id of the private key it is about to use.
3. Check that the header's identity equals the identity it requested, field for field and kind included. A mismatch is a hard failure, never a redirect.
4. Check the lengths of `encapsulated_key` (65) and `sealed_data_key` (48), validate `encapsulated_key` as a public key, and HPKE-open the data key with the received header bytes as AAD.
5. Decrypt the payload as it arrives, in pieces of any size. Use the authenticated plaintext size to know each chunk's length and which chunk is final. Release a chunk's plaintext only after its tag verifies.
6. At the end of the stream, require that exactly `N` chunks arrived and that the total plaintext equals the declared size.

A reader MUST reject:

- a tag failure on any chunk, including chunks that are reordered, duplicated or carry the wrong final flag;
- a stream that ends before the final chunk is complete (truncation);
- any byte after the final chunk;
- a counter overflow;
- a plaintext size that differs from the header's;
- a declared size over 4 GiB.

Consumers decrypt into a temporary file. They publish it (rename into place, register, hand to the editor) only after the whole stream has validated. If anything fails, they delete the temporary file. Earlier chunks of a failed stream were authentic, but the object as a whole was not, so it MUST NOT be used.

## Freshness and re-wrapping

- Every encryption uses a fresh data key and a fresh HPKE encapsulation: every upload, every retry of an upload and every re-upload of an unchanged file. Senders MUST NOT cache or reuse envelopes, data keys or ephemeral keys. In the reference implementation a `SealedEnvelopeWriter` encrypts once and refuses a second use.
- To serve a stored upload, the control plane MAY re-wrap instead of re-encrypting. It opens the stored data key with the output recipient private key, builds a new header for the request (the reader key's id, the requested identity and the same plaintext size), and seals the same data key to the reader public key with a fresh encapsulation. The payload ciphertext is sent unchanged. A re-wrap never encrypts new plaintext under an existing data key. Any new or changed plaintext gets a new data key.
- Downloads of content that did not arrive as a sealed upload are encrypted fresh to the reader key.

## Implementing on WebCrypto

The format uses only primitives that WebCrypto provides, so a browser can hold either key later:

- P-256 ECDH: `generateKey`, `importKey('raw', …)` for points (which validates them) and `deriveBits(…, 256)` for the X coordinate. Private keys import as JWK.
- HKDF: WebCrypto's `HKDF` cannot return the extracted PRK, so build `Extract` and `Expand` from `HMAC` with SHA-256. WebCrypto refuses empty HMAC keys. An empty salt is equivalent to 32 zero bytes (RFC 5869, section 2.2), so use that instead.
- AES-GCM: `encrypt`/`decrypt` with `tagLength: 128`, returning ciphertext and tag together, as the format lays them out.

## Transport mapping

The contracts carry the envelope next to the existing stream metadata: `SealedEnvelope { header, encapsulated_key, sealed_data_key }` on upload and read metadata, and `SealingPublicKey` wherever a recipient is published. The ciphertext travels in the existing `chunk` messages, which may cut it anywhere.

Fields outside the envelope, such as content type, sizes, digests and timestamps, are neither encrypted nor authenticated by this format. The header's plaintext size is the authoritative one. A plaintext digest sent in clear reveals when two transfers carry equal content.

## Test vectors

- RFC 9180 Appendix A.3.1 (base mode): `tests/Offloadr.Runner.Tests/Sealing/HpkeTests.cs`.
- WebCrypto-generated envelopes: `tests/Offloadr.Runner.Tests/Sealing/vectors/webcrypto-vectors.json`, written by `node build/sealed-envelope-vectors.mjs generate <file>`. The .NET tests open them. A .NET test also writes envelopes and runs `node build/sealed-envelope-vectors.mjs decrypt <file>` on them. It needs `node` on `PATH`, and it is skipped without node unless `RUNNER_NATIVE_TESTS_REQUIRED=1`.
