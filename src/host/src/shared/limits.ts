/** Host 通信骨架的可执行初值；不是游戏性能或最终产品预算结论。 */
export interface TransportLimits {
  messageBytes: number;
  jsonDepth: number;
  jsonTokens: number;
  activeRequests: number;
  messagesPerWindow: number;
  windowMs: number;
  sendBytes: number;
  sendCount: number;
  sendDeadlineMs: number;
  receiveDeadlineMs: number;
  httpConnections: number;
}

export const probeLimits: Readonly<TransportLimits> = Object.freeze({
  messageBytes: 64 * 1024,
  jsonDepth: 32,
  jsonTokens: 8192,
  activeRequests: 8,
  messagesPerWindow: 64,
  windowMs: 1000,
  sendBytes: 2 * 1024 * 1024,
  sendCount: 16,
  sendDeadlineMs: 5000,
  receiveDeadlineMs: 5000,
  httpConnections: 16,
});

export class TransportBudgetError extends Error {
  constructor(message = '传输资源额度不足，连接已停止。') {
    super(message);
    this.name = 'TransportBudgetError';
  }
}

export function validateLimits(limits: TransportLimits): void {
  for (const key of Object.keys(probeLimits) as (keyof TransportLimits)[]) {
    const value = limits[key];
    if (!Number.isSafeInteger(value) || value < 1) throw new Error('传输预算必须为有限正整数。');
  }
  if (limits.messageBytes > limits.sendBytes) throw new Error('单条消息不能大于发送总额。');
}
