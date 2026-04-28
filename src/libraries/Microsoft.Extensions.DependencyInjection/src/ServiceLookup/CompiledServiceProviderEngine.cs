// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Microsoft.Extensions.DependencyInjection.ServiceLookup
{
    internal abstract class CompiledServiceProviderEngine : ServiceProviderEngine
    {
#if IL_EMIT
        public ILEmitResolverBuilder ResolverBuilder { get; }
#else
        public ExpressionResolverBuilder ResolverBuilder { get; }
#endif

        [RequiresDynamicCode("Creates DynamicMethods")]
        public CompiledServiceProviderEngine(ServiceProvider provider)
        {
            ResolverBuilder = new(provider);
        }

        public override Func<ServiceProviderEngineScope, ValueTask<object?>> RealizeService(ServiceCallSite callSite)
        {
            // The compiled (Expression / IL emit) pipeline still produces a synchronous
            // Func<scope, object?>; wrap it into the async-first signature here so the
            // rest of the resolution pipeline stays uniform.
            Func<ServiceProviderEngineScope, object?> compiled = ResolverBuilder.Build(callSite);
            return scope => new ValueTask<object?>(compiled(scope));
        }
    }
}
