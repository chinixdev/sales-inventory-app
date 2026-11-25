/** JWT sin firmar para pruebas: la aplicación solo lee los claims, no valida la firma. */
export function fakeToken(role: string, extraClaims: Record<string, unknown> = {}): string {
  const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=+$/, '');
  const claims = { role, nameid: '1', exp: Math.floor(Date.now() / 1000) + 3600, ...extraClaims };
  return `${encode({ alg: 'HS256' })}.${encode(claims)}.sig`;
}
