import { createHmac, timingSafeEqual } from 'node:crypto';
import type { WireAuthenticateV2 } from '@skylineslink/contracts';

export const isLowerHex256 = (value: string): boolean => /^[0-9a-f]{64}$/.test(value);

/** D11.1 fixed, role-separated transcript. No JSON formatting/canonicalization dependency. */
export function createHandshakeProof(key: Uint8Array, role: 'server' | 'client', auth: WireAuthenticateV2, serverNonce: string): string {
  if (key.byteLength !== 32 || !isLowerHex256(auth.clientNonce) || !isLowerHex256(serverNonce)) throw new Error('Invalid authentication material.');
  const fields = ['SkylinesLink/pipe-auth/v2', role, auth.requestId, auth.environmentId, auth.expectedInstanceId,
    auth.productVersion, auth.contractVersion, auth.coreManifestSha256, String(auth.expectedAuthorityEpoch), auth.profileId,
    String(auth.expectedGrantEpoch), auth.clientNonce, serverNonce];
  const hmac = createHmac('sha256', key);
  for (const field of fields) {
    const bytes = Buffer.from(field, 'utf8'); const length = Buffer.allocUnsafe(4); length.writeUInt32LE(bytes.length);
    hmac.update(length); hmac.update(bytes);
  }
  return hmac.digest('hex');
}

export function equalProof(actual: string, expected: string): boolean {
  return isLowerHex256(actual) && isLowerHex256(expected) && timingSafeEqual(Buffer.from(actual, 'hex'), Buffer.from(expected, 'hex'));
}
