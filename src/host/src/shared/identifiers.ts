/** Deployment selectors, not the grammar for arbitrary domain/Wire identities. */
export function isDeploymentIdentifier(value: string): boolean {
  return /^[A-Za-z0-9._-]{1,64}$/.test(value) && value !== '.' && value !== '..' && !value.endsWith('.');
}

export function canonicalEnvironment(value: string): string {
  if (!isDeploymentIdentifier(value) || /^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(value)) throw new Error('环境标识无效。');
  return value.toLowerCase();
}
