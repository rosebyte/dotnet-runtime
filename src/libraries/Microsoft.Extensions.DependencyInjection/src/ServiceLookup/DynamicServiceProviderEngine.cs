// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Extensions.DependencyInjection.ServiceLookup
{
    internal sealed class DynamicServiceProviderEngine : CompiledServiceProviderEngine
    {
        private readonly ServiceProvider _serviceProvider;

        [RequiresDynamicCode("Creates DynamicMethods")]
        public DynamicServiceProviderEngine(ServiceProvider serviceProvider) : base(serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public override Func<ServiceProviderEngineScope, ValueTask<object?>> RealizeService(ServiceCallSite callSite)
        {
            int callCount = 0;
            return scope =>
            {
                // Resolve via the async-first runtime resolver. Since no source produces an
                // incomplete ValueTask today, the returned task is always synchronously
                // completed. We still return it as ValueTask<object?> to keep the pipeline uniform.
                ValueTask<object?> resultTask = CallSiteRuntimeResolver.Instance.ResolveAsync(callSite, scope);

                if (Interlocked.Increment(ref callCount) == 2)
                {
                    // Don't capture the ExecutionContext when forking to build the compiled version of the
                    // resolve function
                    _ = ThreadPool.UnsafeQueueUserWorkItem(_ =>
                    {
                        try
                        {
                            _serviceProvider.ReplaceServiceAccessor(callSite, base.RealizeService(callSite));
                        }
                        catch (Exception ex)
                        {
                            DependencyInjectionEventSource.Log.ServiceRealizationFailed(ex, _serviceProvider.GetHashCode());

                            Debug.Fail($"We should never get exceptions from the background compilation.{Environment.NewLine}{ex}");
                        }
                    },
                    null);
                }

                return resultTask;
            };
        }
    }
}
