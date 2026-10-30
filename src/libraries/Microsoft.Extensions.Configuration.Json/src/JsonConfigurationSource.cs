// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias ConfigurationCore;

global using ConfigurationCore::Microsoft.Extensions.Configuration;

using System;

namespace Microsoft.Extensions.Configuration.Json
{
    /// <summary>
    /// Represents a JSON file as an <see cref="IConfigurationSource"/>.
    /// </summary>
    public class JsonConfigurationSource : FileConfigurationSource, IConfigurationSourceWithOwnedProviders
    {
        bool IConfigurationSourceWithOwnedProviders.OwnsProviders => GetType() == typeof(JsonConfigurationSource);

        /// <summary>
        /// Builds the <see cref="JsonConfigurationProvider"/> for this source.
        /// </summary>
        /// <param name="builder">The <see cref="IConfigurationBuilder"/>.</param>
        /// <returns>A <see cref="JsonConfigurationProvider"/> instance.</returns>
        public override IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            EnsureDefaults(builder);
            return new JsonConfigurationProvider(this);
        }
    }
}
