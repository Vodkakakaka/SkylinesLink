export interface GameClient {
  call(capabilityId: string, schemaRevision: string, args: unknown, signal: AbortSignal): Promise<unknown>;
  close(): Promise<void>;
}

/** 仅传递稳定的公开原因，不把系统异常、凭据和路径作为工具输出。 */
export class GameClientError extends Error {
  readonly code: string;
  readonly mayHaveApplied: boolean;

  constructor(code: string, message: string, mayHaveApplied = false) {
    super(message);
    this.name = 'GameClientError';
    this.code = code;
    this.mayHaveApplied = mayHaveApplied;
  }
}
