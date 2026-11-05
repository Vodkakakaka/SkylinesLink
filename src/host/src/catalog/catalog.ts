import { capabilities } from '@skylineslink/contracts';

export type Capability = (typeof capabilities)[number];

/** 不另维护工具名单；骨架只投影已经生成且有处理器的能力。 */
export const productCapabilities: readonly Capability[] = capabilities;
