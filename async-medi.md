# Async-ready MEDI — design proposal

> Working document for `experiment/full-async-di`. Builds on the work captured in
> `context.md` (sessions 1 & 2) and the genuinely async-aware Expression / IL
> emit builders that followed.

## 1. Goals & non-goals

### Goals
- Allow consumers to register **async factories** (`Func<IServiceProvider, ValueTask<T>>`)
  for transient, scoped and singleton lifetimes.
- Add a **public async resolution API** (`GetServiceAsync` / `GetRequiredServiceAsync`)
  that flows the existing `ValueTask<object?>` pipeline end-to-end without
  blocking.
- Preserve the existing synchronous API (`GetService`, `GetRequiredService`)
  with **identical** behavior for all-sync graphs and a clear, deterministic
  failure mode when the graph contains an async source.
- Keep the synchronous fast path (the common case) **allocation-free and
  branch-cheap** — no state machines, no `Task` allocation, no extra locking.
- Honor the rule the user set up front: **no "if it's async, fall back to the
  runtime resolver" tricks.** The compiled engines must produce code that
  natively reasons about `ValueTask<object?>`. That foundation now exists.

### Non-goals (in scope but explicitly deferred)
- Async constructor injection. Constructors are synchronous in C#; async
  dependencies are awaited *before* the constructor call by the generated
  composition code.
- Re-entrant async locks. We will not introduce a custom async-reentrant
  primitive in MEDI; the design works around the limitation instead.
- Replacing `IServiceProvider` itself. We add an async-aware companion
  interface; existing `IServiceProvider` consumers remain unaffected.
- Cancellation observability inside resolution. Tokens are accepted by the
  public API for forward compatibility but are not threaded into the visitor
  yet (see §7).

---

## 2. Where we are today

Sessions 1 & 2 already produced:

| Component | State |
|----------|-------|
| `ValueTaskHelpers` | `GetSynchronousResult<T>` for the sync bridge; `AwaitConstructor`, `AwaitArrayElements`, `AwaitAndCaptureDisposable` for slow-path composition. |
| `CallSiteRuntimeResolver` | Visitor returns `ValueTask<object?>`. Constructor / IEnumerable / DisposeCache nodes use a sync fast path that only spins up an `async` local function when a child task is incomplete. |
| `ExpressionResolverBuilder` | Every visit method emits an expression of type `ValueTask<object?>`. Composites store child VTs in locals, branch on `IsCompletedSuccessfully`, fast path constructs synchronously and wraps in `new ValueTask<object?>(…)`, slow path packs VTs into an array and calls `ValueTaskHelpers.AwaitConstructor` / `AwaitArrayElements` / `AwaitAndCaptureDisposable`. |
| `ILEmitResolverBuilder` | Same pattern in raw IL. `DynamicMethod` return type is `ValueTask<object?>`. |
| `ServiceProvider` | `ServiceAccessor.RealizedService` is `Func<ServiceProviderEngineScope, ValueTask<object?>>`. `GetService` unwraps via `GetSynchronousResult`. Singleton fast path stores `_ => new ValueTask<object?>(value)`. |
| `ServiceProviderEngine` family | `RealizeService` returns `Func<scope, ValueTask<object?>>`. Compiled engines delegate to the builders; runtime engine delegates to `CallSiteRuntimeResolver.ResolveAsync`. |

What is **not** yet async-ready:

1. **Public API.** `GetService` is sync-only; the pipeline result must currently
   be a synchronously-completed `ValueTask`. There is no public async entry.
2. **Factory registration.** `FactoryCallSite.Factory` is
   `Func<IServiceProvider, object>`; there is no way to register an async
   factory.
3. **Scope/root caches.** Both `VisitCache` (scope) and `VisitRootCache`
   (root/singleton) call `Monitor.Enter` and then call
   `ValueTaskHelpers.GetSynchronousResult` on the inner visit. If the inner
   visit ever becomes genuinely incomplete (an awaited async factory), this
   throws `InvalidOperationException`. The `BuildScopedExpression` in the
   Expression / IL builders has the same shape — `Monitor.Enter` then
   synchronous unwrap.
4. **`CallSiteValidator`** does not know about async factories.
5. **`ValidateOnBuild`** drives the runtime resolver with a sync wrapper; for
   async-factory descriptors this would currently throw.
6. **External-container conformance tests** (`DI.External.Tests`) only cover
   the sync API, so a parallel async test suite is needed to lock in the
   contract for keyed services and disposables.

---

## 3. High-level design

### 3.1 The async pipeline already exists; finish wiring it up

The composition pipeline is already uniformly typed
`Func<scope, ValueTask<object?>>`. Therefore the *only* work needed to expose
async resolution is:

1. Add an async path on the public surface that does **not** call
   `GetSynchronousResult` and instead returns the `ValueTask` directly.
2. Allow the call-site graph to contain an **`AsyncFactoryCallSite`** whose
   `VisitFactory` invokes `Func<IServiceProvider, ValueTask<object?>>`.
3. Replace the **single remaining sync barrier** — the `Monitor`-based scope
   and root caches — with a strategy that does not block the pipeline when
   the inner resolution is genuinely incomplete.

These are the three Phase 2/3/4 items below. Everything else is plumbing
(extension methods, abstractions, validation, tests, docs).

### 3.2 Public surface — companion interface, not replacement

We add a companion abstraction in `Microsoft.Extensions.DependencyInjection.Abstractions`:

```csharp
namespace Microsoft.Extensions.DependencyInjection;

public interface IAsyncServiceProvider
{
    ValueTask<object?> GetServiceAsync(Type serviceType, CancellationToken cancellationToken = default);
}

public interface IKeyedAsyncServiceProvider
{
    ValueTask<object?> GetKeyedServiceAsync(Type serviceType, object? serviceKey, CancellationToken cancellationToken = default);
    ValueTask<object> GetRequiredKeyedServiceAsync(Type serviceType, object? serviceKey, CancellationToken cancellationToken = default);
}
```

`ServiceProvider` and `ServiceProviderEngineScope` implement both. Generic /
required helpers live as **extension methods** on `IServiceProvider`
(consistent with the existing pattern in `ServiceProviderServiceExtensions`):

```csharp
public static ValueTask<T?> GetServiceAsync<T>(this IServiceProvider provider, CancellationToken cancellationToken = default);
public static ValueTask<T>  GetRequiredServiceAsync<T>(this IServiceProvider provider, CancellationToken cancellationToken = default) where T : notnull;
public static ValueTask<object> GetRequiredServiceAsync(this IServiceProvider provider, Type serviceType, CancellationToken cancellationToken = default);
public static ValueTask<IEnumerable<T>> GetServicesAsync<T>(this IServiceProvider provider, CancellationToken cancellationToken = default);
```

Extension methods feature-detect `IAsyncServiceProvider`; if the provider
doesn’t implement it (third-party container without async support), they fall
back to wrapping the sync result, exactly as `GetRequiredService` does today
relative to `IServiceProviderIsService`.

Why a companion interface and not a new method on `IServiceProvider`?
`IServiceProvider` lives in the BCL and is implemented by countless types; we
can’t add to it. A separate interface mirrors the precedent of
`IKeyedServiceProvider` / `IServiceProviderIsService` and is the same trick
used elsewhere in the framework when an established interface needs to grow.

### 3.3 Async factories — a new call-site kind

A new internal type, parallel to `FactoryCallSite`:

```csharp
internal sealed class AsyncFactoryCallSite : ServiceCallSite
{
    public Func<IServiceProvider, ValueTask<object>> Factory { get; }
    public override CallSiteKind Kind => CallSiteKind.AsyncFactory;
    // …
}
```

Public registration extensions in
`ServiceCollectionServiceExtensions` (and a keyed sibling):

```csharp
public static IServiceCollection AddSingleton<TService>(this IServiceCollection services, Func<IServiceProvider, ValueTask<TService>> factory) where TService : class;
public static IServiceCollection AddScoped<TService>(this IServiceCollection services, Func<IServiceProvider, ValueTask<TService>> factory) where TService : class;
public static IServiceCollection AddTransient<TService>(this IServiceCollection services, Func<IServiceProvider, ValueTask<TService>> factory) where TService : class;
// + (TService, TImplementation) overloads, + Add(ServiceDescriptor) constructed with an async factory
// + AddKeyedSingleton / AddKeyedScoped / AddKeyedTransient async variants
```

`ServiceDescriptor` grows a parallel `AsyncImplementationFactory` field
(read-only, set by a new constructor). Existing `ImplementationFactory` is
unchanged. `CallSiteFactory.GetCallSite` recognizes the async factory and
emits an `AsyncFactoryCallSite`.

Visitor coverage:

| Visitor                       | Handling |
|--------------------------------|----------|
| `CallSiteRuntimeResolver`      | New `VisitAsyncFactory` returns `factory(scope)`. The constructor / array fast-path already deals with possibly-incomplete child VTs, so async factories light up automatically through the existing slow path. |
| `ExpressionResolverBuilder`    | New `VisitAsyncFactory` emits `Expression.Invoke(Constant(factory), Scope)` (already typed `ValueTask<object?>`). No fast/slow distinction is needed — the result *is* a VT. |
| `ILEmitResolverBuilder`        | Same shape; emit a delegate invocation that leaves a `ValueTask<object?>` on the stack. |
| `CallSiteVisitor`              | Add an abstract `VisitAsyncFactory`; provide a default implementation in the existing dispatcher. |

### 3.4 Sync API behavior on async-tainted graphs

`GetService` (and friends) keeps calling
`ValueTaskHelpers.GetSynchronousResult`. If the resolved tree contains an
`AsyncFactoryCallSite` *and* the factory completes synchronously, the call
succeeds (no different from a sync factory that completes immediately).
If the factory yields, `GetSynchronousResult` already throws
`InvalidOperationException` with `SR.AsynchronousResolutionNotSupported`. We
upgrade this resource string to mention the public async API:

> *"This service was registered with an asynchronous factory. Resolve it with
> `GetServiceAsync` / `GetRequiredServiceAsync` instead."*

This is the contract:

- **Sync API on sync graph:** unchanged.
- **Sync API on async-completed-synchronously graph:** unchanged.
- **Sync API on a graph that yields:** `InvalidOperationException` with a
  message that points at the async API.
- **Async API on any graph:** works.

This honors the user’s "no fallback" rule — we never silently route async
trees through the runtime resolver; we just refuse to lie about a sync
completion that doesn’t exist.

### 3.5 Scope and root cache redesign — the only real engineering change

This is the biggest open item. Today both caches use a `Monitor` lock and
require their inner resolution to complete synchronously while the lock is
held. That is incompatible with awaiting an async factory.

**Constraints:**

1. Each `(scope, cache key)` must resolve **at most once**.
2. Concurrent resolves of the same key must observe the same instance.
3. Resolution under a cache must support **re-entrancy on the same logical
   resolve operation** (the `AcquiredLocks` flag in `RuntimeResolverContext`
   prevents double acquisition during nested calls on the same path).
4. Disposable instances must be captured exactly once.
5. We must not block a thread in the worker pool while awaiting an async
   factory.
6. We must not regress the all-sync hot path.

**Chosen approach: optimistic single-flight via `Lazy<ValueTask<object?>>`,
falling back to async-friendly cache fill for async-tainted call sites.**

The proposal is a **two-tier cache**:

- For all-sync call sites (the overwhelming majority and the entire current
  pipeline), keep the current `Monitor.Enter` + dictionary path. It’s fast,
  reentrant, and well-tested. The only change is that the inner unwrap stays
  in `ValueTaskHelpers.GetSynchronousResult` — which throws cleanly if the
  source ever turns out to be incomplete. We annotate the call site with a
  `bool ContainsAsyncSources` (computed at `CallSiteFactory` build time;
  propagates up through composites). For sync call sites, `ContainsAsyncSources`
  is `false`, the old hot path runs, and behavior is identical to today.

- For async-tainted call sites, use a different cache slot whose value is a
  `Task<object?>` (or a custom `IValueTaskSource<object?>` for fewer
  allocations) representing the *single in-flight resolve*. Concurrent
  callers observe the same `Task` and `await` it. The first caller publishes
  the Task into the dictionary atomically (`TryAdd`); losers from the race
  observe the winner. Cycle / re-entrancy detection moves out of `Monitor`
  and into a per-thread `HashSet<ServiceCallSite>` (the
  `t_resolving` ThreadStatic already exists in `CallSiteRuntimeResolver` for
  exactly this purpose) plus the `RuntimeResolverContext.AcquiredLocks`
  flag carried through the visit.

In code (sketch — `VisitCache`):

```csharp
// async-tainted branch
ConcurrentDictionary<ServiceCacheKey, object?> resolved = scope.ResolvedServices;
if (resolved.TryGetValue(callSite.Cache.Key, out object? existing))
{
    return existing is Task<object?> pending
        ? new ValueTask<object?>(pending)
        : new ValueTask<object?>(existing);
}

var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
if (!resolved.TryAdd(callSite.Cache.Key, tcs.Task))
{
    // Lost the race — read the winner.
    return new ValueTask<object?>((Task<object?>)resolved[callSite.Cache.Key]);
}

return ResolveOnceAsync(callSite, scope, tcs);

static async ValueTask<object?> ResolveOnceAsync(...)
{
    try
    {
        object? value = await VisitCallSiteMain(callSite, ctx).ConfigureAwait(false);
        scope.CaptureDisposable(value);
        // Replace the Task with the resolved value so future readers don't go through the Task.
        scope.ResolvedServices[callSite.Cache.Key] = value;
        tcs.SetResult(value);
        return value;
    }
    catch (Exception ex)
    {
        // Don’t leave a faulted Task in the cache — remove it so subsequent calls get a fresh attempt.
        scope.ResolvedServices.TryRemove(callSite.Cache.Key, out _);
        tcs.SetException(ex);
        throw;
    }
}
```

`ResolvedServices` becomes a `ConcurrentDictionary<ServiceCacheKey, object?>`
that may store either resolved values or in-flight `Task<object?>` markers.
The sync fast path detects the marker and falls through to
`GetSynchronousResult` (which throws if the marker is incomplete — exactly
the sync-on-async behavior described in §3.4).

Why this and not `SemaphoreSlim.WaitAsync`?

- `SemaphoreSlim` is not reentrant. Today’s `AcquiredLocks` mechanism
  *requires* reentrancy for nested resolves on the same scope. Reworking it
  to a per-thread async-local stack would touch every visitor.
- It’s also heavier per-cache-entry (an entire semaphore) than a single
  `Task<object?>` slot.
- It blocks ThreadPool threads when contended; the optimistic Task-based
  approach simply awaits the in-flight Task.

Why not `Lazy<Task<object?>>(LazyThreadSafetyMode.ExecutionAndPublication)`?
It would work, but it adds an allocation per cache entry and gives less
control over the on-failure cleanup (`Lazy` permanently caches the failed
attempt). The `TryAdd(TCS.Task)` pattern is what `HttpClientFactory` and
several other BCL caches use for the same reason.

Root cache (singletons) follows the same pattern, except the disposable
list is held by the root scope. The `t_resolving` ThreadStatic continues to
detect cycles.

**Compiled engines.** The Expression / IL emit `BuildScopedExpression`
already calls `GetSynchronousResult` under a `Monitor` lock today. For
async-tainted scope/root resolutions, the builders emit a call to a new
helper `ValueTaskHelpers.GetOrAddAsync(callSite, scope)` that performs the
optimistic-Task pattern above. For non-async-tainted call sites, the
existing emission is unchanged. The decision is made once at *build* time
based on `callSite.ContainsAsyncSources`, so the compiled delegate has zero
runtime branching for the sync case.

> Note: `ContainsAsyncSources` *is* a derived attribute of the call-site
> graph and **not** an "async-taint fallback" mechanism — it only switches
> the cache-fill strategy, never the resolution path. The visitor still
> emits genuinely async code at every node.

### 3.6 Disposal flow (already mostly in place)

`ServiceProviderEngineScope.DisposeAsync` already exists and properly
awaits `IAsyncDisposable` services. The new pieces are:

1. `CaptureDisposable` is invoked from the slow-path
   `AwaitAndCaptureDisposable` helper *after* the await — so the disposable
   is registered exactly once even when resolution yields. Confirmed in
   `ValueTaskHelpers.AwaitAndCaptureDisposable`.
2. If a graph contains an async factory, `ServiceProvider.Dispose` (sync)
   still throws `InvalidOperationException` if any captured service implements
   `IAsyncDisposable` but not `IDisposable` (existing behavior — see
   `SR.AsyncDisposableServiceDispose`). We update XML doc to make the
   "prefer DisposeAsync" guidance more prominent in the async story.

### 3.7 Cancellation

We accept `CancellationToken` on every async public entry but **do not
thread it through the visitor in this pass**:

- Resolution is mostly composition; the only place cancellation could be
  honored is the user-supplied async factory.
- Threading the token through the entire resolver / both compiled engines
  is invasive and orthogonal to the core async story.
- Accepting it now is a no-op behavior change but a forward-compatible API
  shape, so we don’t break anyone the day we wire it up.

We document this clearly: today the token is observed only at API entry
(early `OperationCanceledException`); future versions may flow it into
async factories.

---

## 4. Public API additions (summary)

### Microsoft.Extensions.DependencyInjection.Abstractions

```csharp
public interface IAsyncServiceProvider
{
    ValueTask<object?> GetServiceAsync(Type serviceType, CancellationToken cancellationToken = default);
}

public interface IKeyedAsyncServiceProvider
{
    ValueTask<object?> GetKeyedServiceAsync(Type serviceType, object? serviceKey, CancellationToken cancellationToken = default);
    ValueTask<object>  GetRequiredKeyedServiceAsync(Type serviceType, object? serviceKey, CancellationToken cancellationToken = default);
}

public static class ServiceProviderServiceExtensions
{
    // existing members unchanged
    public static ValueTask<T?> GetServiceAsync<T>(this IServiceProvider provider, CancellationToken cancellationToken = default);
    public static ValueTask<T>  GetRequiredServiceAsync<T>(this IServiceProvider provider, CancellationToken cancellationToken = default) where T : notnull;
    public static ValueTask<object> GetRequiredServiceAsync(this IServiceProvider provider, Type serviceType, CancellationToken cancellationToken = default);
    public static ValueTask<IEnumerable<T>> GetServicesAsync<T>(this IServiceProvider provider, CancellationToken cancellationToken = default);
    // + keyed variants on IKeyedServiceProviderExtensions (or the same static class — match existing layout)
}

public static class ServiceCollectionServiceExtensions
{
    // existing members unchanged
    public static IServiceCollection AddSingleton<TService>(this IServiceCollection services, Func<IServiceProvider, ValueTask<TService>> implementationFactory) where TService : class;
    public static IServiceCollection AddSingleton<TService, TImplementation>(this IServiceCollection services, Func<IServiceProvider, ValueTask<TImplementation>> implementationFactory) where TService : class where TImplementation : class, TService;
    public static IServiceCollection AddScoped<TService>(this IServiceCollection services, Func<IServiceProvider, ValueTask<TService>> implementationFactory) where TService : class;
    // … and Transient, plus keyed siblings
}

public sealed class ServiceDescriptor
{
    // existing members unchanged
    public Func<IServiceProvider, ValueTask<object>>? AsyncImplementationFactory { get; }
    public ServiceDescriptor(Type serviceType, Func<IServiceProvider, ValueTask<object>> implementationFactory, ServiceLifetime lifetime);
    public ServiceDescriptor(Type serviceType, object? serviceKey, Func<IServiceProvider, object?, ValueTask<object>> implementationFactory, ServiceLifetime lifetime);
}
```

All additions are **purely additive** — no existing member changes. This is
intentional so the API review focuses on shape rather than back-compat
analysis.

### Microsoft.Extensions.DependencyInjection (default container)

`ServiceProvider` and `ServiceProviderEngineScope` implement
`IAsyncServiceProvider` and `IKeyedAsyncServiceProvider`.

---

## 5. Implementation phasing

| Phase | Scope | Risk |
|-------|-------|------|
| **1 — done** | Internal `ValueTask<object?>` plumbing across runtime resolver, both compiled engines, and `ServiceProvider`. | Validated: 1369 + 534 tests pass. |
| **2 — public async API, sync-only graphs** | `IAsyncServiceProvider`, `GetServiceAsync` extensions, `ServiceProvider.GetServiceAsync` returning the existing pipeline VT directly. No new factories yet. | Low. No behavior change for sync graphs; new API is a thin pass-through. |
| **3 — async factory registration** | `AsyncFactoryCallSite`, `ServiceDescriptor` async constructors, registration extensions, visitor support, validation. **No cache redesign yet** — async factories that require yielding under a scope/root cache will throw `InvalidOperationException` with a clear message at this stage. Transient async factories work end-to-end immediately. | Medium. The validator and `ValidateOnBuild` need updates. Test surface grows. |
| **4 — async-aware scope/root cache** | `ContainsAsyncSources` flag, optimistic-Task cache fill, `ResolvedServices` widened to allow Task markers, builder branch on `ContainsAsyncSources`. | Highest. Concurrency surface, cycle detection, disposable capture all interact. Needs targeted stress tests. |
| **5 — polish** | Docs, samples, conformance tests for external containers (`DI.External.Tests` async profile), benchmarks (sync hot path must not regress), feature switch defaults. | Low. |

Phase 2 is independently shippable and unblocks library authors who want to
*consume* the async pattern (e.g., `IHostedService` startup) before MEDI
itself emits it. Phase 3 unblocks "I have an async startup dependency" use
cases in the common transient case. Phase 4 closes the "scoped async
service" loop.

---

## 6. Validation plan

### Build & lint
- 0 warnings / 0 errors on net11.0, net10.0, netstandard2.1, netstandard2.0, net462.
- API approval per dotnet/runtime API review process for every public
  addition in §4.

### Tests
- All existing `tests/DI.Tests` (1369) and `tests/DI.External.Tests` (534)
  remain passing at every phase.
- New tests per phase:
  - **Phase 2**: `GetServiceAsync` returns same instance as `GetService` for
    sync graphs; null/missing service behavior; cancellation observed at
    entry; `IAsyncServiceProvider` feature-detect on third-party providers.
  - **Phase 3**: `AsyncFactoryCallSite` resolves transient correctly;
    sync API throws clear `InvalidOperationException` if factory yields;
    keyed variants; validator detects circular deps via async factory;
    `ValidateOnBuild` runs async factories and reports failures.
  - **Phase 4**: concurrency stress (N threads resolving the same scoped
    async service observe one instance); cycle detection through async
    factory; faulted async factory does not poison the cache (next call
    retries); disposable captured exactly once across both fast and slow
    paths; reentrancy (factory awaits a service whose factory awaits another
    service in the same scope) does not deadlock.

### Performance
- Microbenchmark the sync hot path (singleton, scoped, transient with
  ctor injection) **before** Phase 4 and after; target: ≤ 1% regression on
  resolves/sec.
- Microbenchmark async resolution against `Task.FromResult`-wrapping a sync
  factory, to surface unintended overhead.
- Validate no extra allocations on the all-sync hot path
  (`MemoryDiagnoser`).

### Conformance for external containers
- Mirror the new public API in `Specification.Tests` so external-container
  authors get a clear contract for the async surface. Existing sync tests
  remain in place.

---

## 7. Open questions (for review)

1. **`IAsyncServiceProvider` location.** Same assembly as
   `IServiceProvider` (BCL) is impossible; same assembly as
   `IKeyedServiceProvider` (`Microsoft.Extensions.DependencyInjection.Abstractions`)
   is the natural home. Confirm with API review.
2. **Should `ValueTask<object>` from `GetRequiredServiceAsync` be a
   `Task<T>` instead?** `ValueTask<T>` matches the internal pipeline and
   avoids allocation, but consumers may want a directly-awaitable
   `Task<T>` for `Task.WhenAll` scenarios. Proposal: stick with `ValueTask`,
   document the `.AsTask()` convert-up pattern.
3. **Should `AsyncFactoryCallSite` be its own `CallSiteKind` or a flag on
   `FactoryCallSite`?** Distinct kind is cleaner and matches the existing
   one-class-per-kind pattern.
4. **`ConfigureAwait(false)` on the public API VTs.** Today the runtime
   resolver uses `ConfigureAwait(false)` everywhere internal. The public
   `GetServiceAsync` will not synchronization-context capture (consistent
   with the rest of MEDI).
5. **What does `IServiceScopeFactory.CreateScope()` return for an async
   provider?** Same `IServiceScope` (sync); `IAsyncDisposable.DisposeAsync`
   is already implemented on the scope. No new abstraction needed.
6. **`GetRequiredServiceAsync` failure throw.** Match
   `GetRequiredService` exactly — `InvalidOperationException` with the
   same message — so error handling stays uniform.
7. **Can a sync factory be replaced by an async factory at re-registration
   time without a breaking change?** Yes — same descriptor key wins; old
   sync callers may start throwing `InvalidOperationException` from the
   async-on-sync path. Document as a behavioral note in async-factory docs;
   it’s the same risk profile as replacing any factory.

---

## 8. Risk register

| Risk | Likelihood | Mitigation |
|------|-----------:|------------|
| Cache redesign deadlocks via cycle through async factory. | Medium | Keep `t_resolving` ThreadStatic; add async-aware analogue (`AsyncLocal<HashSet<ServiceCallSite>>`) for nested awaited resolves. Stress tests. |
| Sync hot-path regression. | Low | `ContainsAsyncSources` flag short-circuits to today’s code path; benchmarks gate Phase 4 PR. |
| External containers diverge on async API contract. | Medium | Spec tests + opt-in profile; clear documentation. |
| `ValueTask` reuse semantic mistakes (consuming twice). | Medium | The runtime resolver already gets this right (await once, store result). Code review checklist for new visitors. Slow-path helpers always await exactly once. |
| API review rejects companion interface naming. | Low | Bike-shed in proposal stage; we have multiple precedents. |

---

## 9. Summary

The pipeline is already async-end-to-end. To finish the story we need:

1. A public async surface on the existing pipeline (Phase 2).
2. `AsyncFactoryCallSite` + registration + visitor coverage (Phase 3).
3. An async-friendly cache fill that doesn’t hold a `Monitor` while
   awaiting (Phase 4).

Steps 1 and 2 are mechanical and low-risk because the heavy lifting
(genuinely async-aware compiled trees, ValueTask helpers,
`CallSiteRuntimeResolver` rewrite) is done. Step 3 is the only piece with
real concurrency engineering; the proposed two-tier cache (sync `Monitor`
when the graph is sync, optimistic `TryAdd(Task)` single-flight when it
isn’t) keeps the hot path identical and isolates new complexity to the
async-tainted slice.

No fallbacks. No "if async, route to runtime resolver." The compiled
engines stay genuinely async, the public API surfaces it cleanly, and the
sync API gives a deterministic, helpful error when callers try to consume
an async graph synchronously.
