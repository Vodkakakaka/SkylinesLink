import {
  requireContract, schemaIds,
  type CapabilityQuery, type GuidanceDocument, type GuidanceQuery,
  type GuidanceSearchQuery, type GuidanceSearchResult, type GuidanceSummary,
} from '@skylineslink/contracts';
import { productCapabilities, type Capability } from '../catalog/catalog.ts';
import { GameClientError } from '../game-client/game-client.ts';
import { guideTranslations } from './content.ts';

type LocalRead = (args: Record<string, unknown>) => unknown;

// These bindings describe implemented Host handlers, not another capability catalogue.
// A future revision needs an explicit handler change before it can execute locally.
const localBindings = [
  { id: 'guidance.search', schemaRevision: '1', toolName: 'skylines_guidance_search', inputId: schemaIds.GuidanceSearchQuery, outputId: schemaIds.GuidanceSearchResult },
  { id: 'guidance.get', schemaRevision: '1', toolName: 'skylines_guidance_get', inputId: schemaIds.GuidanceQuery, outputId: schemaIds.GuidanceDocument },
  { id: 'capabilities.describe', schemaRevision: '1', toolName: 'skylines_capability_describe', inputId: schemaIds.CapabilityQuery, outputId: schemaIds.CapabilityDescription },
] as const;

function summary(guide: (typeof guideTranslations)[number]): GuidanceSummary {
  return {
    guideId: guide.guideId, revision: guide.revision, title: guide.title,
    summary: guide.summary, capabilityRefs: guide.capabilityRefs,
  };
}

/** Public, immutable bundle data. No game client, discovery, filesystem or execution port. */
export class LocalReads {
  readonly #catalog: ReadonlyMap<string, Capability>;
  readonly #productVersion: string;

  constructor(catalog: readonly Capability[], productVersion: string) {
    this.#productVersion = productVersion;
    const byId = new Map<string, Capability>();
    for (const capability of catalog) {
      if (byId.has(capability.id)) throw new Error('能力目录中存在重复能力身份。');
      byId.set(capability.id, capability);
    }
    this.#catalog = byId;

    // References describe this product build; a connection may expose only a subset.
    // Describing an unexposed capability still fails rather than revealing another route.
    const declared = new Map(productCapabilities.map(capability => [capability.id, capability]));
    const identities = new Set<string>();
    for (const guide of guideTranslations) {
      const identity = `${guide.language}/${guide.guideId}`;
      if (identities.has(identity)) throw new Error('公共指引中存在重复身份。');
      identities.add(identity);
      requireContract(schemaIds.GuidanceDocument, this.#document(guide));
      for (const reference of guide.capabilityRefs) {
        if (declared.get(reference.capabilityId)?.schemaRevision !== reference.schemaRevision)
          throw new Error('公共指引引用了未声明的能力修订。');
      }
    }
  }

  resolve(capability: Capability): LocalRead | undefined {
    const binding = localBindings.find(value => value.id === capability.id);
    if (!binding) return undefined;
    if (capability.effect !== 'read' || capability.schemaRevision !== binding.schemaRevision
      || capability.toolName !== binding.toolName || capability.inputId !== binding.inputId
      || capability.outputId !== binding.outputId)
      throw new Error('公共只读处理器与声明绑定不一致。');
    switch (binding.id) {
      case 'guidance.search': return args => this.#search(args as GuidanceSearchQuery);
      case 'guidance.get': return args => this.#get(args as GuidanceQuery);
      case 'capabilities.describe': return args => this.#describe(args as CapabilityQuery);
    }
  }

  #document(guide: (typeof guideTranslations)[number]): GuidanceDocument {
    return {
      sourceKind: 'productGuide', productVersion: this.#productVersion,
      language: guide.language, ...summary(guide), bodyMarkdown: guide.bodyMarkdown,
    };
  }

  #search(args: GuidanceSearchQuery): GuidanceSearchResult {
    const language = args.language ?? 'en';
    const query = (args.query ?? '').trim();
    const terms = query.toLowerCase().split(/\s+/u).filter(Boolean);
    const matches = guideTranslations.filter(guide => {
      if (guide.language !== language) return false;
      const searchable = [guide.guideId, guide.title, guide.summary, guide.bodyMarkdown,
        ...guide.capabilityRefs.map(reference => reference.capabilityId)].join('\n').toLowerCase();
      return terms.every(term => searchable.includes(term));
    });
    const limit = args.limit ?? 5;
    return {
      sourceKind: 'productGuide', productVersion: this.#productVersion, language, query,
      results: matches.slice(0, limit).map(summary), truncated: matches.length > limit,
    };
  }

  #get(args: GuidanceQuery): GuidanceDocument {
    const guide = guideTranslations.find(value => value.guideId === args.guideId && value.language === (args.language ?? 'en'));
    if (!guide) throw new GameClientError('GUIDANCE_NOT_FOUND', 'No bundled guide matches this ID and language.');
    return this.#document(guide);
  }

  #describe(args: CapabilityQuery): unknown {
    const capability = this.#catalog.get(args.capabilityId);
    if (!capability) throw new GameClientError('CAPABILITY_NOT_FOUND', 'This connection does not expose that capability.');
    if (args.schemaRevision !== undefined && args.schemaRevision !== capability.schemaRevision)
      throw new GameClientError('CAPABILITY_REVISION_MISMATCH', 'The requested revision is not exposed; refresh the capability description.');
    return {
      capabilityId: capability.id, schemaRevision: capability.schemaRevision,
      inputSchema: capability.inputSchema, outputSchema: capability.outputSchema,
    };
  }
}
