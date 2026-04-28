// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Microsoft.Extensions.DependencyInjection.ServiceLookup
{
    internal sealed class ILEmitServiceProviderEngine : ServiceProviderEngine
    {
        private readonly ILEmitResolverBuilder _expressionResolverBuilder;

        [RequiresDynamicCode("Creates DynamicMethods")]
        public ILEmitServiceProviderEngine(ServiceProvider serviceProvider)
        {
            _expressionResolverBuilder = new ILEmitResolverBuilder(serviceProvider);
        }

        public override Func<ServiceProviderEngineScope, ValueTask<object?>> RealizeService(ServiceCallSite callSite)
        {
            Func<ServiceProviderEngineScope, object?> compiled = _expressionResolverBuilder.Build(callSite);
            return scope => new ValueTask<object?>(compiled(scope));
        }
    }
}
