// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Internal;

namespace Microsoft.Extensions.DependencyInjection.ServiceLookup
{
    internal sealed class CallSiteRuntimeResolver : CallSiteVisitor<RuntimeResolverContext, ValueTask<object?>>
    {
        public static CallSiteRuntimeResolver Instance { get; } = new();

        // ThreadStatic set to track call sites currently being resolved on this thread.
        // Used to detect circular dependencies that occur through factory functions.
        [ThreadStatic]
        private static HashSet<ServiceCallSite>? t_resolving;

        private CallSiteRuntimeResolver()
        {
        }

        // Synchronous wrapper used by the singleton fast path, the compiled
        // (Expression / IL emit) resolvers' fallback into runtime resolution,
        // and the public ServiceProvider entry points.
        // Throws InvalidOperationException if the underlying async pipeline
        // produces an incomplete ValueTask (no async sources exist today).
        public object? Resolve(ServiceCallSite callSite, ServiceProviderEngineScope scope)
        {
            return ValueTaskHelpers.GetSynchronousResult(ResolveAsync(callSite, scope));
        }

        public ValueTask<object?> ResolveAsync(ServiceCallSite callSite, ServiceProviderEngineScope scope)
        {
            // Fast path to avoid virtual calls if we already have the cached value in the root scope
            if (scope.IsRootScope && callSite.Value is object cached)
            {
                return new ValueTask<object?>(cached);
            }

            return VisitCallSite(callSite, new RuntimeResolverContext
            {
                Scope = scope
            });
        }

        protected override ValueTask<object?> VisitDisposeCache(ServiceCallSite transientCallSite, RuntimeResolverContext context)
        {
            ValueTask<object?> inner = VisitCallSiteMain(transientCallSite, context);
            if (inner.IsCompletedSuccessfully)
            {
                return new ValueTask<object?>(context.Scope.CaptureDisposable(inner.Result));
            }

            return AwaitAndCapture(inner, context.Scope);

            static async ValueTask<object?> AwaitAndCapture(ValueTask<object?> inner, ServiceProviderEngineScope scope)
            {
                object? result = await inner.ConfigureAwait(false);
                return scope.CaptureDisposable(result);
            }
        }

        protected override ValueTask<object?> VisitConstructor(ConstructorCallSite constructorCallSite, RuntimeResolverContext context)
        {
            ServiceCallSite[] parameterCallSites = constructorCallSite.ParameterCallSites;
            if (parameterCallSites.Length == 0)
            {
                return new ValueTask<object?>(InvokeConstructor(constructorCallSite, Array.Empty<object?>()));
            }

            object?[] parameterValues = new object?[parameterCallSites.Length];
            for (int index = 0; index < parameterValues.Length; index++)
            {
                ValueTask<object?> parameterValueTask = VisitCallSite(parameterCallSites[index], context);
                if (parameterValueTask.IsCompletedSuccessfully)
                {
                    parameterValues[index] = parameterValueTask.Result;
                }
                else
                {
                    return AwaitRemainingAndInvoke(constructorCallSite, parameterCallSites, parameterValues, index, parameterValueTask, context);
                }
            }

            return new ValueTask<object?>(InvokeConstructor(constructorCallSite, parameterValues));

            static async ValueTask<object?> AwaitRemainingAndInvoke(
                ConstructorCallSite constructorCallSite,
                ServiceCallSite[] parameterCallSites,
                object?[] parameterValues,
                int pendingIndex,
                ValueTask<object?> pendingValueTask,
                RuntimeResolverContext context)
            {
                parameterValues[pendingIndex] = await pendingValueTask.ConfigureAwait(false);
                for (int index = pendingIndex + 1; index < parameterValues.Length; index++)
                {
                    parameterValues[index] = await Instance.VisitCallSite(parameterCallSites[index], context).ConfigureAwait(false);
                }

                return InvokeConstructor(constructorCallSite, parameterValues);
            }
        }

        private static object InvokeConstructor(ConstructorCallSite constructorCallSite, object?[] parameterValues)
        {
#if NETFRAMEWORK || NETSTANDARD2_0
            try
            {
                return constructorCallSite.ConstructorInfo.Invoke(parameterValues);
            }
            catch (Exception ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                // The above line will always throw, but the compiler requires we throw explicitly.
                throw;
            }
#else
            return constructorCallSite.ConstructorInfo.Invoke(BindingFlags.DoNotWrapExceptions, binder: null, parameters: parameterValues, culture: null);
#endif
        }

        protected override ValueTask<object?> VisitRootCache(ServiceCallSite callSite, RuntimeResolverContext context)
        {
            if (callSite.Value is object value)
            {
                // Value already calculated, return it directly
                return new ValueTask<object?>(value);
            }

            var lockType = RuntimeResolverLock.Root;
            ServiceProviderEngineScope serviceProviderEngine = context.Scope.RootProvider.Root;

            lock (callSite)
            {
                // Lock the callsite and check if another thread already cached the value
                if (callSite.Value is object callSiteValue)
                {
                    return new ValueTask<object?>(callSiteValue);
                }

                // Detect circular dependencies by tracking what we're currently resolving on this thread
                t_resolving ??= new HashSet<ServiceCallSite>(ReferenceEqualityComparer.Instance);
                if (!t_resolving.Add(callSite))
                {
                    // We're already resolving this call site on this thread - circular dependency detected
                    throw new InvalidOperationException(
                        SR.Format(SR.CircularDependencyException, TypeNameHelper.GetTypeDisplayName(callSite.ServiceType)));
                }

                try
                {
                    // We hold a lock here. Awaiting an incomplete ValueTask under the lock would
                    // be incorrect; today no source produces incomplete tasks, so we synchronously
                    // unwrap and rely on ValueTaskHelpers to throw if that ever changes.
                    object? resolved = ValueTaskHelpers.GetSynchronousResult(VisitCallSiteMain(callSite, new RuntimeResolverContext
                    {
                        Scope = serviceProviderEngine,
                        AcquiredLocks = context.AcquiredLocks | lockType
                    }));
                    serviceProviderEngine.CaptureDisposable(resolved);
                    callSite.Value = resolved;
                    return new ValueTask<object?>(resolved);
                }
                finally
                {
                    t_resolving.Remove(callSite);
                }
            }
        }

        protected override ValueTask<object?> VisitScopeCache(ServiceCallSite callSite, RuntimeResolverContext context)
        {
            // Check if we are in the situation where scoped service was promoted to singleton
            // and we need to lock the root
            return context.Scope.IsRootScope ?
                VisitRootCache(callSite, context) :
                VisitCache(callSite, context, context.Scope, RuntimeResolverLock.Scope);
        }

        private ValueTask<object?> VisitCache(ServiceCallSite callSite, RuntimeResolverContext context, ServiceProviderEngineScope serviceProviderEngine, RuntimeResolverLock lockType)
        {
            bool lockTaken = false;
            object sync = serviceProviderEngine.Sync;
            Dictionary<ServiceCacheKey, object?> resolvedServices = serviceProviderEngine.ResolvedServices;
            // Taking locks only once allows us to fork resolution process
            // on another thread without causing the deadlock because we
            // always know that we are going to wait the other thread to finish before
            // releasing the lock
            if ((context.AcquiredLocks & lockType) == 0)
            {
                Monitor.Enter(sync, ref lockTaken);
            }

            try
            {
                // Note: This method has already taken lock by the caller for resolution and access synchronization.
                // For scoped: takes a dictionary as both a resolution lock and a dictionary access lock.
                if (resolvedServices.TryGetValue(callSite.Cache.Key, out object? resolved))
                {
                    return new ValueTask<object?>(resolved);
                }

                // We hold a lock here. See note in VisitRootCache - all sources are sync today.
                resolved = ValueTaskHelpers.GetSynchronousResult(VisitCallSiteMain(callSite, new RuntimeResolverContext
                {
                    Scope = serviceProviderEngine,
                    AcquiredLocks = context.AcquiredLocks | lockType
                }));
                serviceProviderEngine.CaptureDisposable(resolved);
                resolvedServices.Add(callSite.Cache.Key, resolved);
                return new ValueTask<object?>(resolved);
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(sync);
                }
            }
        }

        protected override ValueTask<object?> VisitConstant(ConstantCallSite constantCallSite, RuntimeResolverContext context)
        {
            return new ValueTask<object?>(constantCallSite.DefaultValue);
        }

        protected override ValueTask<object?> VisitServiceProvider(ServiceProviderCallSite serviceProviderCallSite, RuntimeResolverContext context)
        {
            return new ValueTask<object?>(context.Scope);
        }

        protected override ValueTask<object?> VisitIEnumerable(IEnumerableCallSite enumerableCallSite, RuntimeResolverContext context)
        {
            ServiceCallSite[] itemCallSites = enumerableCallSite.ServiceCallSites;
            Array array = CreateArray(enumerableCallSite.ItemType, itemCallSites.Length);

            for (int index = 0; index < itemCallSites.Length; index++)
            {
                ValueTask<object?> itemValueTask = VisitCallSite(itemCallSites[index], context);
                if (itemValueTask.IsCompletedSuccessfully)
                {
                    array.SetValue(itemValueTask.Result, index);
                }
                else
                {
                    return AwaitRemaining(array, itemCallSites, index, itemValueTask, context);
                }
            }

            return new ValueTask<object?>(array);

            [UnconditionalSuppressMessage("AotAnalysis", "IL3050:RequiresDynamicCode",
                Justification = "VerifyAotCompatibility ensures elementType is not a ValueType")]
            static Array CreateArray(Type elementType, int length)
            {
                Debug.Assert(!ServiceProvider.VerifyAotCompatibility || !elementType.IsValueType, "VerifyAotCompatibility=true will throw during building the IEnumerableCallSite if elementType is a ValueType.");

                return Array.CreateInstance(elementType, length);
            }

            static async ValueTask<object?> AwaitRemaining(
                Array array,
                ServiceCallSite[] itemCallSites,
                int pendingIndex,
                ValueTask<object?> pendingValueTask,
                RuntimeResolverContext context)
            {
                array.SetValue(await pendingValueTask.ConfigureAwait(false), pendingIndex);
                for (int index = pendingIndex + 1; index < itemCallSites.Length; index++)
                {
                    array.SetValue(await Instance.VisitCallSite(itemCallSites[index], context).ConfigureAwait(false), index);
                }

                return array;
            }
        }

        protected override ValueTask<object?> VisitFactory(FactoryCallSite factoryCallSite, RuntimeResolverContext context)
        {
            return new ValueTask<object?>(factoryCallSite.Factory(context.Scope));
        }
    }

    internal struct RuntimeResolverContext
    {
        public ServiceProviderEngineScope Scope { get; set; }

        public RuntimeResolverLock AcquiredLocks { get; set; }
    }

    [Flags]
    internal enum RuntimeResolverLock
    {
        Scope = 1,
        Root = 2
    }
}
