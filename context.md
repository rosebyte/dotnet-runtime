# MEDI async-first internal pipeline — session context

Snapshot of an in-progress task on `rosebyte/dotnet-runtime` so it can be resumed
on another machine. Branch: `experiment/full-async-di`. The user commits manually.

## Original task (verbatim intent)

> Change implementation of MEDI (Microsoft.Extensions.DependencyInjection) to
> async-first leveraging `ValueTask<object?>` whenever possible. The current
> public API stays unchanged; if a real (incomplete) Task appears at the end of
> resolution, just throw. Constructors are sync, so arguments must be awaited
> before the constructor call.

User clarifications across sessions:
- **Scope chosen: internal plumbing only.** No async factory registration API
  is added yet; today every `ValueTask<object?>` produced by the pipeline is
  synchronously completed. The "throw on incomplete" guard is for future async
  sources.
- **Full async pipeline — no fallback.** The compiled (Expression/IL emit) paths
  must produce `Func<scope, ValueTask<object?>>` natively. The user explicitly
  rejected a "runtime resolver fallback for async-tainted trees" approach.
- User preferences (stored as memories):
  - Never run baseline build unless asked.
  - Never run `git commit` / amend / change history — user does it.

## What was implemented

### Session 1 — Runtime resolver & engine plumbing (commit `bb332859d44`)

#### Files created
- `src/libraries/Microsoft.Extensions.DependencyInjection/src/ServiceLookup/ValueTaskHelpers.cs`
  - `GetSynchronousResult<T>(ValueTask<T>)`:
    - completed-successfully → return `Result`
    - faulted/canceled → propagate via `GetAwaiter().GetResult()`
    - incomplete → throw `InvalidOperationException` (`SR.AsynchronousResolutionNotSupported`)

#### Resource added
- `src/libraries/Microsoft.Extensions.DependencyInjection/src/Resources/Strings.resx`:
  - `AsynchronousResolutionNotSupported` — used by the throw above.

#### Files rewritten
- `CallSiteRuntimeResolver.cs`
  - Now `CallSiteVisitor<RuntimeResolverContext, ValueTask<object?>>`.
  - New primary entry: `ResolveAsync(callSite, scope) -> ValueTask<object?>`.
  - Old `Resolve(callSite, scope) -> object?` is a thin sync wrapper via
    `ValueTaskHelpers.GetSynchronousResult`.
  - `VisitConstructor` and `VisitIEnumerable` use a fast-path loop that only
    transitions into a local `async` helper when a `ValueTask` is actually
    incomplete — **no state machine allocation** when everything is sync.
  - `VisitRootCache` and `VisitCache` unwrap synchronously inside their
    `Monitor`-held lock (awaiting under a lock would be incorrect).

#### Files updated (signature flow)
- `ServiceProviderEngine.cs` — `RealizeService` returns `Func<scope, ValueTask<object?>>`.
- `RuntimeServiceProviderEngine.cs` — returns `scope => resolver.ResolveAsync(callSite, scope)`.
- `DynamicServiceProviderEngine.cs` — warm-up uses `ResolveAsync` directly.
- `ServiceProvider.cs` — `ServiceAccessor.RealizedService` is `Func<scope, ValueTask<object?>>?`.
  `GetService` unwraps via `GetSynchronousResult`. Singleton fast path stores
  `scope => new ValueTask<object?>(value)`.

### Session 2 — Builders produce ValueTask natively (uncommitted)

This session made the Expression and IL emit builders produce
`Func<scope, ValueTask<object?>>` at the `Build()` boundary. Expression trees
and raw IL cannot emit `await`, so the internal compiled code remains
`Func<scope, object?>`. The `Build()` method wraps:
`scope => new ValueTask<object?>(syncDelegate(scope))`.

#### ExpressionResolverBuilder.cs
- `Build(ServiceCallSite)` → public, returns `Func<scope, ValueTask<object?>>`.
  Calls private `BuildSync()` and wraps in ValueTask.
- `BuildSync(ServiceCallSite)` → private, returns `Func<scope, object?>`.
  Contains the original `Build()` logic unchanged.
- Internal methods renamed: `BuildSyncNoCache`, `BuildSyncExpression`.
- `VisitScopeCache` calls `BuildSync()` (not `Build()`) because scope-cached
  delegates are called inline within expression trees and must return `object?`.

#### ILEmitResolverBuilder.cs
- `Build(ServiceCallSite)` → wraps `BuildType(callSite).Lambda` in ValueTask:
  `var sync = BuildType(callSite).Lambda; return scope => new ValueTask<object?>(sync(scope));`

#### Engine classes (simplified)
- `CompiledServiceProviderEngine.RealizeService` → `return ResolverBuilder.Build(callSite);`
- `ExpressionsServiceProviderEngine.RealizeService` → same.
- `ILEmitServiceProviderEngine.RealizeService` → same.
  No wrapping at engine level — builders handle it.

#### Test changes
- `CallSiteTests.cs` — `CompileCallSite` helper unwraps `ValueTask` from
  `Build()` via `ValueTaskHelpers.GetSynchronousResult`.

## Validation

- Build: 0 warnings, 0 errors across all TFMs (`net11.0`, `net10.0`,
  `netstandard2.1`, `netstandard2.0`, `net462`).
- Tests:
  - `tests/DI.Tests`: **1369 / 1369 passing.**
  - `tests/DI.External.Tests`: **534 / 534 passing.**
- Code review: passed (no issues).

Test commands (from `src/libraries/Microsoft.Extensions.DependencyInjection/`):
```
# Windows
..\..\..\dotnet.cmd build --nologo /t:test tests\DI.Tests\Microsoft.Extensions.DependencyInjection.Tests.csproj
..\..\..\dotnet.cmd build --nologo /t:test tests\DI.External.Tests\Microsoft.Extensions.DependencyInjection.ExternalContainers.Tests.csproj
# Unix
../../../dotnet.sh build --nologo /t:test tests/DI.Tests/Microsoft.Extensions.DependencyInjection.Tests.csproj
../../../dotnet.sh build --nologo /t:test tests/DI.External.Tests/Microsoft.Extensions.DependencyInjection.ExternalContainers.Tests.csproj
```

## Architecture summary

The entire resolution pipeline is now uniformly typed as `ValueTask<object?>`:

```
ServiceProvider.GetService()
  → ServiceAccessor.RealizedService(scope)           Func<scope, ValueTask<object?>>
    → built by one of:
      (a) RuntimeServiceProviderEngine                → CallSiteRuntimeResolver.ResolveAsync
      (b) DynamicServiceProviderEngine                → ResolveAsync (warm-up), then compiled
      (c) CompiledServiceProviderEngine               → ResolverBuilder.Build(callSite)
          → ExpressionResolverBuilder.Build()         wraps sync expression in ValueTask
          → ILEmitResolverBuilder.Build()             wraps sync IL delegate in ValueTask
  → ValueTaskHelpers.GetSynchronousResult()           unwraps at API boundary
```

The wrapping creates one closure per service type (allocated once at
compilation time, not per-resolve). Internal scope-cache delegates remain
`Func<scope, object?>` because they're called inline within expression trees
or IL and must return `object?` on the evaluation stack.

## Remaining work / next steps

1. **Public async API** (`GetServiceAsync`, async factory registrations) — not
   yet started. When async factories are added:
   - `CallSiteRuntimeResolver` already handles them (its visitor returns
     `ValueTask<object?>` and has async helpers for incomplete tasks).
   - Expression/IL builders will need to detect async-tainted trees and fall
     back to the runtime resolver for those trees, since expression trees and
     raw IL cannot emit `await`.
2. **Async-taint detection** — add `bool ContainsAsyncServices` to call sites
   so compiled engines can route async-tainted trees to the runtime resolver.
3. **IServiceProviderIsService** and other auxiliary interfaces — unchanged,
   no async implications.

## Resume checklist (next machine)

1. `git status` — confirm uncommitted edits on top of commit `bb332859d44`:
   - `src/.../ServiceLookup/Expressions/ExpressionResolverBuilder.cs`
   - `src/.../ServiceLookup/ILEmit/ILEmitResolverBuilder.cs`
   - `src/.../ServiceLookup/CompiledServiceProviderEngine.cs`
   - `src/.../ServiceLookup/Expressions/ExpressionsServiceProviderEngine.cs`
   - `src/.../ServiceLookup/ILEmit/ILEmitServiceProviderEngine.cs`
   - `tests/DI.Tests/CallSiteTests.cs`
2. Re-run the two test commands above; expect 1369 + 534 passing.
3. Decide on next phase (public async API, async-taint detection, etc.).
