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
            return ResolverBuilder.Build(callSite);
        }
    }
}
