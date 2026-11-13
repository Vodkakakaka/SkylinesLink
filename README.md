[English](README.md) | [简体中文](README.zh-CN.md)

# SkylinesLink · 天际互联

Connect AI tools to your city.

SkylinesLink is an independent Model Context Protocol (MCP) project for **Cities: Skylines II**, designed to let AI assistants and agents read, understand, and operate a city. It is being designed and developed from scratch.

The goal is a framework linking useful game information, normal gameplay actions, planning, and verifiable results. Public, task-specific AI guidance will ship with the product; player preferences belong to the current task rather than its default rules. These are product commitments, not implemented-capability claims.

**Developer preview: [GitHub Releases](https://github.com/Vodkakakaka/SkylinesLink/releases).** Source downloads include a commit-bound manifest, checksums and validation summary. See the [preview guide](docs/development/preview.md) for the source layout, build commands and known limitations. There is no game-validated player installation release yet. Source archives are contributor downloads, not player installation packages.

![SkylinesLink original city-connection concept illustration; not a game screenshot](assets/branding/city-link-concept.png)

*Original concept illustration. [Identity assets and provenance](assets/branding/README.md); this image does not depict game runtime or implemented UI.*

## Current status

The project is in foundation development and feasibility validation. It has a modular codebase, bounded connections, durable-operation and history experiments, and desktop entry points. The current source declares ten tools: `skylines_context`, `skylines_operation_get`, `skylines_guidance_search`, `skylines_guidance_get`, `skylines_capability_describe`, `skylines_task_stop`, `skylines_task_stop_decision_get`, `skylines_module_runtime_get`, `skylines_city_overview`, and `skylines_building_inspect`.

The module observation API captures loader facts and pages the same immutable observation. Its native adapter uses the configured normal mod connection; an unconfigured source reports unavailable. It does not enumerate every installed or subscribed mod, establish enabled state, or certify health and integration support. Complete coverage and frame-cost acceptance are still pending.

Building inspection reads one building from an exact reference in the current loading scope or the native information panel's published selection. It returns its transform, prefab metadata, asset-local bounds and a bounded prefix of direct attachments, subnets and installed upgrades. Missing fields and incomplete prefixes are explicit. It does not change selection or tools; base cost, stored road references and local bounds do not establish placement eligibility, reachability or a complete world footprint. The native selection source must first publish a change in the current scope; an older cached selection remains unavailable. Implementation, offline checks and game qualification are tracked separately. This read path has passed a bounded native new-city fixture through the normal mod connection and both tested MCP protocol versions, using an exact reference and a normally published panel selection. General asset coverage, ordinary-user operation and performance acceptance remain open.

The city overview reads native population and player money components in the currently loaded city, with loading scope, capture frames, units, missing reasons and the unlimited-money flag. Source update times remain unknown; this is not a full city diagnosis. A bounded new-city load/read/return cycle has passed through the normal mod connection and two MCP protocol versions. Full city coverage, player/AI coexistence and performance acceptance remain open.

Task Stop requests use Control permission; full decision reads use Read permission and explicitly select live or history. Production task creation and execution are not enabled, and runtimes without Task support report unavailable. An accepted Stop is a request to stop subsequent work, not proof that all native work has ended; it does not stop the AI client or undo committed game actions.

The three public-data tools provide bundled English/Simplified Chinese guidance and the exact declared input/output schemas without contacting the game. Start with `skylines_guidance_search` using `{}`, then read a returned guide ID with `skylines_guidance_get`. They describe this build, not current city facts or permission to execute an action. Construction tools and player-ready installation are still being developed; these additions do not upgrade an older Core candidate.

Offline checks cover the Launcher, bundled Node.js/Host, and an independent C# runtime probe. Isolated Codex and DeepSeek Harness checks provide evidence for the specific tested client and artifact combinations; they do not establish compatibility with every client, the current source revision, real model decisions, or the running game. See the [architecture and current implementation](docs/architecture.md).

Full game runtime coverage, player/AI coexistence, distribution channels, and complete performance acceptance remain unverified. A development build or local Core candidate is not a player-ready release.

The product aims to provide useful game information and reliable normal-play actions, including complex tasks. Progress is judged by complete city tasks, rework, call cost, and measured resource use. Planned capabilities and mod integrations are coverage commitments, not claims of completed support.

## Architecture and product goals

The architecture is **a C# game mod + a TypeScript/Node.js MCP Host**, in separate processes. The Host uses the official TypeScript MCP SDK and communicates with the C# runtime over authenticated Windows named pipes. The stable Launcher is the external stdio entry point. Streamable HTTP has an implementation and offline checks; its complete player-facing startup and lifecycle integration remains work in progress.

The intended player experience is:

- Standard MCP support without dependence on one model provider, with Codex and DeepSeek Harness as priority compatibility and acceptance targets. Each supported client, protocol version, and optional capability needs its own evidence.
- A complete package containing the required runtime and Host. Subscribing and enabling the mod should not require a separate Node.js, Python, .NET, service, or bridge installation. Initial MCP registration in an AI client will still need a short setup flow.
- The same Core, configuration/data, and stable connection entry point whether installed from GitHub or Paradox Mods, with coordinated updates and channel switching that avoids duplicate loading. These distribution and installation flows are not yet player-validated.
- Normal game funds, unlocks, native validation, and operation lifecycles.
- Players and AI working concurrently without the AI taking over the player's mouse, keyboard, window focus, active tool, or selection. Conflicting world changes must be detected and revalidated. This is a design requirement; actual in-game isolation has not been demonstrated.
- Clear module responsibilities, one-way dependencies, and reproducible idle/load measurements for performance and memory use.

## Development

Use Windows and stable Node.js, pnpm, and .NET SDK versions meeting the minimums in the [toolchain guide](docs/development/toolchain.md). Newer stable development tools are accepted; dependencies remain locked. TypeScript is a project dependency and does not need a global installation. pnpm is the primary package manager; npm is a fallback.

```powershell
pnpm install --frozen-lockfile
pnpm build
pnpm test
```

These commands build and check the game-independent foundation. `pnpm build:mod` additionally requires the official game toolchain. NativeAOT desktop publishing has separate native build prerequisites. Ordinary builds do not deploy a mod or publish a release. These are contributor commands, not player installation instructions.

## Documentation

- [Architecture and component responsibilities](docs/architecture.md)
- [Development toolchain and minimum versions](docs/development/toolchain.md)
- [Developer preview, source layout and known limitations](docs/development/preview.md)
- [Licensing and brand boundaries](docs/governance/licensing.md)
- [Publication privacy](docs/governance/publication-privacy.md)
- [Original identity assets and concept artwork](assets/branding/README.md)

## Releases and licensing

Player downloads will be published through this repository's [GitHub Releases](https://github.com/Vodkakakaka/SkylinesLink/releases) after the initial functionality and acceptance work is complete. Paradox Mods subscription delivery is also planned. Both channels are intended to share the same Core, version, source commit, and release notes, coordinated through a recoverable release process. Complete packaged delivery is the baseline; see the [architecture](docs/architecture.md).

The project's own content is licensed under the [Mozilla Public License 2.0](LICENSE). Third-party materials retain their respective licenses.

Copyright © 2026 Vodka and SkylinesLink contributors. See the [brand guidelines](BRANDING.md).

SkylinesLink is an independent community project and is not affiliated with or endorsed by Paradox Interactive, Colossal Order, OpenAI, or DeepSeek. Product names and trademarks belong to their respective owners.
