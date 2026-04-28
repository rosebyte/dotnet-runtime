# MEDI async-first internal pipeline — session context

Snapshot of an in-progress task on `rosebyte/dotnet-runtime` so it can be resumed
on another machine. Branch state: edits **uncommitted**. The user commits manually.

## Original task (verbatim intent)

> Change implementation of MEDI (Microsoft.Extensions.DependencyInjection) to
> async-first leveraging `ValueTask<object?>` whenever possible. The current
> public API stays unchanged; if a real (incomplete) Task appears at the end of
> resolution, just throw. Constructors are sync, so arguments must be awaited
> before the constructor call.

User clarifications during session:
- **Scope chosen: internal plumbing only.** No async factory registration API
  is added yet; today every `ValueTask<object?>` produced by the pipeline is
  synchronously completed. The "throw on incomplete" guard is for future async
  sources.
- User noted "the new async/await runtime is much better than the old
  compiler-generated state machine" — i.e. trust modern `async`/`await`, don't
  hand-roll state machines.
- User preferences (already stored as memories):
  - Never run baseline build unless asked.
  - Never run `git commit` / amend / change history — user does it.

## What was implemented

### Files created
- `src/libraries/Microsoft.Extensions.DependencyInjection/src/ServiceLookup/ValueTaskHelpers.cs`
  - `GetSynchronousResult<T>(ValueTask<T>)`:
    - completed-successfully → return `Result`
    - faulted/canceled → propagate via `GetAwaiter().GetResult()`
    - incomplete → throw `InvalidOperationException` (`SR.AsynchronousResolutionNotSupported`)

### Resource added
- `src/libraries/Microsoft.Extensions.DependencyInjection/src/Resources/Strings.resx`:
  - `AsynchronousResolutionNotSupported` — used by the throw above.

### Files rewritten
- `src/libraries/Microsoft.Extensions.DependencyInjection/src/ServiceLookup/CallSiteRuntimeResolver.cs`
  - Now `CallSiteVisitor<RuntimeResolverContext, ValueTask<object?>>`.
  - New primary entry: `ResolveAsync(callSite, scope) -> ValueTask<object?>`.
  - Old `Resolve(callSite, scope) -> object?` is now a thin sync wrapper around
    `ResolveAsync` that goes through `ValueTaskHelpers.GetSynchronousResult`.
  - `VisitConstructor` and `VisitIEnumerable` use a fast-path loop that only
    transitions into a local `async` helper (`AwaitRemainingAndInvoke` /
    `AwaitRemaining`) when an awaiting `ValueTask` is actually incomplete — so
    when everything is sync there is **no state machine allocation**.
  - `VisitRootCache` and `VisitCache` unwrap synchronously inside their
    `Monitor`-held lock (awaiting under a lock would be incorrect). They use
    the same `GetSynchronousResult` helper, so a future incomplete task there
    would surface as `InvalidOperationException`.
  - Caching, circular-dependency detection, ThreadStatic resolver set, and
    capture-disposable behavior are unchanged.

### Files updated (signature flow)
- `ServiceLookup/ServiceProviderEngine.cs`
  - `RealizeService` now returns `Func<ServiceProviderEngineScope, ValueTask<object?>>`.
- `ServiceLookup/RuntimeServiceProviderEngine.cs`
  - Returns `scope => CallSiteRuntimeResolver.Instance.ResolveAsync(callSite, scope)` directly.
- `ServiceLookup/CompiledServiceProviderEngine.cs`
  - Wraps the existing compiled `Func<scope, object?>` from `ResolverBuilder.Build`:
    `scope => new ValueTask<object?>(compiled(scope))`. **IL/Expression
    generation itself is unchanged.**
- `ServiceLookup/DynamicServiceProviderEngine.cs`
  - Calls `CallSiteRuntimeResolver.Instance.ResolveAsync` for the warm-up phase
    and returns the `ValueTask<object?>` directly. Background compilation
    behavior preserved.
- `ServiceLookup/Expressions/ExpressionsServiceProviderEngine.cs`
  - Same wrap pattern as `CompiledServiceProviderEngine`.
- `ServiceLookup/ILEmit/ILEmitServiceProviderEngine.cs`
  - Same wrap pattern.
- `ServiceProvider.cs`
  - `ServiceAccessor.RealizedService` is now `Func<scope, ValueTask<object?>>?`.
  - `GetService` calls the realized accessor and unwraps via
    `ServiceLookup.ValueTaskHelpers.GetSynchronousResult`.
  - The singleton fast path in `CreateServiceAccessor` still resolves
    synchronously via `CallSiteRuntimeResolver.Instance.Resolve` and stores
    `scope => new ValueTask<object?>(value)`.
  - `ReplaceServiceAccessor` accepts the new delegate type.

### Untouched (intentionally)
- `CallSiteVisitor<TArgument, TResult>` base — generic, didn't need changes.
- `ExpressionResolverBuilder` and `ILEmitResolverBuilder` (the actual
  Expression / IL generators) — see "Known gap" below.
- `CallSiteValidator`, `CallSiteFactory`, all `*CallSite.cs` types,
  `ServiceProviderEngineScope`, `CallSiteJsonFormatter` (no such file present),
  `StackGuard`, `ServiceLookupHelpers`.

## Validation done in session

- Build: `dotnet.sh build src/Microsoft.Extensions.DependencyInjection.csproj`
  succeeded across all TFMs (`net11.0`, `net10.0`, `netstandard2.1`,
  `netstandard2.0`, `net462`). 0 warnings, 0 errors.
- Tests:
  - `tests/DI.Tests`: **1369 / 1369 passing.**
  - `tests/DI.External.Tests`: **534 / 534 passing.**

Test commands (from `src/libraries/Microsoft.Extensions.DependencyInjection/`):
```
../../../dotnet.sh build --nologo /t:test tests/DI.Tests/Microsoft.Extensions.DependencyInjection.Tests.csproj
../../../dotnet.sh build --nologo /t:test tests/DI.External.Tests/Microsoft.Extensions.DependencyInjection.ExternalContainers.Tests.csproj
```

## Known gap (raised by user, accepted, deferred)

The compiled (Expression / IL emit) paths are still sync-only internally — the
`new ValueTask<object?>(compiled(scope))` wrap at the engine boundary is
**cosmetic** for them. With sync-only sources today this is correct; the moment
an async factory is introduced, any callsite tree that transitively reaches it
will break in compiled paths because:

- `VisitFactory` emits IL hard-typed to `Func<IServiceProvider, object>`.
- `VisitConstructor` emits direct `newobj` with no await machinery for params.
- `VisitRootCache` / scope cache emit calls to the **sync**
  `CallSiteRuntimeResolver.Resolve`, which now throws on an incomplete ValueTask.

### Two strategies for the eventual async-factory work
1. **Rework IL / Expression emission** to emit code returning
   `ValueTask<object?>` with proper await machinery for child resolutions.
   Heavy, especially raw `ILEmitResolverBuilder` (state machines in raw IL).
2. **Async-taint fallback (recommended).** When async factories land, mark the
   originating `ServiceCallSite` and every ancestor that transitively includes
   it as "async-tainted". `CompiledServiceProviderEngine.RealizeService` returns
   `scope => CallSiteRuntimeResolver.Instance.ResolveAsync(callSite, scope)`
   for tainted trees and uses the existing compiled fast path only for fully
   sync trees. The runtime resolver already handles arbitrary sync/async mixes
   correctly.

User left open whether to implement Option 2 proactively or wait until the
async factory API is concretely defined. **Awaiting their answer** when work
resumes.

## Resume checklist (next machine)

1. `git status` — confirm uncommitted edits to:
   - `src/libraries/Microsoft.Extensions.DependencyInjection/src/ServiceProvider.cs`
   - `src/libraries/Microsoft.Extensions.DependencyInjection/src/ServiceLookup/CallSiteRuntimeResolver.cs`
   - `src/libraries/Microsoft.Extensions.DependencyInjection/src/ServiceLookup/ServiceProviderEngine.cs`
   - `src/libraries/Microsoft.Extensions.DependencyInjection/src/ServiceLookup/RuntimeServiceProviderEngine.cs`
   - `src/libraries/Microsoft.Extensions.DependencyInjection/src/ServiceLookup/CompiledServiceProviderEngine.cs`
   - `src/libraries/Microsoft.Extensions.DependencyInjection/src/ServiceLookup/DynamicServiceProviderEngine.cs`
   - `src/libraries/Microsoft.Extensions.DependencyInjection/src/ServiceLookup/Expressions/ExpressionsServiceProviderEngine.cs`
   - `src/libraries/Microsoft.Extensions.DependencyInjection/src/ServiceLookup/ILEmit/ILEmitServiceProviderEngine.cs`
   - `src/libraries/Microsoft.Extensions.DependencyInjection/src/Resources/Strings.resx`
   - new file: `src/libraries/Microsoft.Extensions.DependencyInjection/src/ServiceLookup/ValueTaskHelpers.cs`
2. Re-run the two test commands above; expect 1369 + 534 passing.
3. Decide on the async-taint fallback (Option 2). If yes, design notes:
   - Add `bool ServiceCallSite.IsAsync` (or compute via visitor traversal),
     defaulting to `false`.
   - Add an async factory call site kind + abstractions API
     (`Func<IServiceProvider, ValueTask<object>>`).
   - Compute "tree contains async source" once at `CallSiteFactory` build time
     or via a single visitor pass; cache on the call site.
   - In `CompiledServiceProviderEngine.RealizeService`, branch:
     ```csharp
     if (callSite.TreeIsAsync) return scope => CallSiteRuntimeResolver.Instance.ResolveAsync(callSite, scope);
     var compiled = ResolverBuilder.Build(callSite);
     return scope => new ValueTask<object?>(compiled(scope));
     ```
   - Singleton fast path in `ServiceProvider.CreateServiceAccessor` must also
     route through `ResolveAsync` for async-tainted singletons (and would have
     to allow the singleton's value to materialize asynchronously — at which
     point the public sync `GetService` would throw via `GetSynchronousResult`,
     unless a public async API is also added).

## Plan file

A more compact plan lives at the session workspace path
`~/.copilot/session-state/71c89317-3582-4075-8dff-027105411478/plan.md` on the
original machine. It will not transfer automatically — this `context.md` is the
portable summary.
