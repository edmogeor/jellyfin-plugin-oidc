# Agent Instructions

Read `CONTEXT.md` before changing authentication, identity, or configuration behavior. Use its defined terms, especially **Jellyfin User**, **Identity Provider**, and **Identity Link**.

## Commands

- `make setup` installs local tooling.
- `make format` applies CSharpier and Prettier.
- `make lint` runs Roslyn analyzers and oxlint.
- `make check` verifies formatting and linting.
- `make test-unit` runs unit tests.
- `make test-e2e` resets Docker state and runs e2e tests.
- `make test` runs unit tests followed by e2e tests.
- `make up` starts the e2e stack for manual testing.

Run `make check` after code or configuration changes. Run the relevant test target for behavior changes. Use `make test-e2e`, not direct Playwright execution, when a clean e2e state is required.

## Changes

- Keep OIDC configuration secret-free in browser responses and logs.
- Preserve the one-Identity-Provider scope and local-relative return URL boundary.
- Do not add unsupported claim paths, endpoint overrides, or protocol flows without updating `CONTEXT.md`.
- Use CSharpier for C# and Prettier for e2e JavaScript, JSON, YAML, and HTML. The pre-commit hook formats staged supported files.
- GitHub Actions runs `make check` and the test suites for code, configuration, tooling, or test changes. Markdown-only pushes and pull requests skip CI.

## Releases

- Bump `build.yaml` and add a matching top-level `CHANGELOG.md` entry before release.
- Use the Jellyfin version format `X.Y.Z.W`; bump the patch component for backward-compatible fixes, for example `0.1.0.0` to `0.1.1.0`.
- Push a matching `vX.Y.Z.W` tag to trigger the release workflow. It verifies the tag, runs the test suite, packages the plugin, creates or updates the GitHub release, and publishes the manifest.

## Commits

Use a conventional prefix and imperative summary:

- `feat: add provider logout fallback`
- `fix: reject backslash return URLs`
- `test: cover stale identity links`
- `docs: consolidate project guidance`
- `chore: update tooling`

Keep commits focused. Do not commit build outputs, `node_modules`, test artifacts, TLS files, or reference checkouts.
