// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.Extensions.Primitives;

namespace Microsoft.Extensions.Configuration
{
    /// <summary>
    /// Represents the root node for a configuration.
    /// </summary>
    [DebuggerDisplay("{DebuggerToString(),nq}")]
    [DebuggerTypeProxy(typeof(ConfigurationRootDebugView))]
    public class ConfigurationRoot : IConfigurationRoot, IDisposable
    {
        private readonly IList<IConfigurationProvider> _providers;
        private readonly List<IDisposable> _changeTokenRegistrations;
        private ConfigurationReloadToken _changeToken = new ConfigurationReloadToken();

        /// <summary>
        /// Initializes a Configuration root with a list of providers.
        /// </summary>
        /// <param name="providers">The <see cref="IConfigurationProvider"/>s for this configuration.</param>
        public ConfigurationRoot(IList<IConfigurationProvider> providers)
        {
            ArgumentNullException.ThrowIfNull(providers);

            _providers = providers;
            _changeTokenRegistrations = new List<IDisposable>(providers.Count);
            try
            {
                foreach (IConfigurationProvider p in providers)
                {
                    p.Load();
                    _changeTokenRegistrations.Add(ChangeToken.OnChange(p.GetReloadToken, RaiseChanged));
                }
            }
            catch (Exception exception)
            {
                DisposeResources(_changeTokenRegistrations, exception);
                throw;
            }
        }

        /// <summary>
        /// The <see cref="IConfigurationProvider"/>s for this configuration.
        /// </summary>
        public IEnumerable<IConfigurationProvider> Providers => _providers;

        /// <summary>
        /// Gets or sets the value corresponding to a configuration key.
        /// </summary>
        /// <param name="key">The configuration key.</param>
        /// <returns>The configuration value.</returns>
        public string? this[string key]
        {
            get => GetConfiguration(_providers, key);
            set => SetConfiguration(_providers, key, value);
        }

        /// <summary>
        /// Gets the immediate children subsections.
        /// </summary>
        /// <returns>The children.</returns>
        public IEnumerable<IConfigurationSection> GetChildren() => this.GetChildrenImplementation(null);

        /// <summary>
        /// Returns a <see cref="IChangeToken"/> that can be used to observe when this configuration is reloaded.
        /// </summary>
        /// <returns>The <see cref="IChangeToken"/>.</returns>
        public IChangeToken GetReloadToken() => _changeToken;

        /// <summary>
        /// Gets a configuration subsection with the specified key.
        /// </summary>
        /// <param name="key">The key of the configuration section.</param>
        /// <returns>The <see cref="IConfigurationSection"/>.</returns>
        /// <remarks>
        ///     This method will never return <c>null</c>. If no matching subsection is found with the specified key,
        ///     an empty <see cref="IConfigurationSection"/> is returned.
        /// </remarks>
        public IConfigurationSection GetSection(string key)
            => new ConfigurationSection(this, key);

        /// <summary>
        /// Forces the configuration values to be reloaded from the underlying sources.
        /// </summary>
        public void Reload()
        {
            foreach (IConfigurationProvider provider in _providers)
            {
                provider.Load();
            }
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            ConfigurationReloadToken previousToken = Interlocked.Exchange(ref _changeToken, new ConfigurationReloadToken());
            previousToken.OnReload();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // dispose change token registrations
            foreach (IDisposable registration in _changeTokenRegistrations)
            {
                registration.Dispose();
            }

            // dispose providers
            foreach (IConfigurationProvider provider in _providers)
            {
                (provider as IDisposable)?.Dispose();
            }
        }

        internal static IDisposable? GetOwnedProvider(
            IConfigurationSource source, IConfigurationProvider provider, List<IConfigurationProvider>? activeProviders = null)
        {
            if (source is IConfigurationSourceWithOwnedProviders { OwnsProviders: true } && provider is IDisposable disposable)
            {
                if (activeProviders is not null)
                {
                    foreach (IConfigurationProvider activeProvider in activeProviders)
                    {
                        if (ReferenceEquals(provider, activeProvider))
                        {
                            return null;
                        }
                    }
                }

                return disposable;
            }

            return null;
        }

        internal static void AddOwnedProviders(
            List<IDisposable> resources,
            IList<IConfigurationSource> sources,
            List<IConfigurationProvider> providers,
            List<IConfigurationSource>.Enumerator sourceEnumerator,
            List<IConfigurationProvider>? activeProviders = null)
        {
            try
            {
                // Validate the original enumeration before pairing providers with sources on this failure path.
                sourceEnumerator.MoveNext();
            }
            catch (InvalidOperationException)
            {
                // Reentrant edits make the source/provider pairing ambiguous.
                return;
            }

            for (int i = 0; i < providers.Count; i++)
            {
                if (GetOwnedProvider(sources[i], providers[i], activeProviders) is IDisposable provider)
                {
                    resources.Add(provider);
                }
            }
        }

        internal static void DisposeResources(List<IDisposable> resources, Exception? exception = null)
        {
            List<Exception>? exceptions = null;
            foreach (IDisposable resource in resources)
            {
                try
                {
                    resource.Dispose();
                }
                catch (Exception disposalException)
                {
                    (exceptions ??= new List<Exception>()).Add(disposalException);
                }
            }

            if (exceptions is not null)
            {
                if (exception is not null)
                {
                    exceptions.Insert(0, exception);
                }
                else if (exceptions.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(exceptions[0]).Throw();
                }

                throw new AggregateException(exceptions);
            }
        }

        internal static string? GetConfiguration(IList<IConfigurationProvider> providers, string key)
        {
            for (int i = providers.Count - 1; i >= 0; i--)
            {
                IConfigurationProvider provider = providers[i];

                if (provider.TryGet(key, out string? value))
                {
                    return value;
                }
            }

            return null;
        }

        internal static void SetConfiguration(IList<IConfigurationProvider> providers, string key, string? value)
        {
            if (providers.Count == 0)
            {
                throw new InvalidOperationException(SR.Error_NoSources);
            }

            foreach (IConfigurationProvider provider in providers)
            {
                provider.Set(key, value);
            }
        }

        private string DebuggerToString()
        {
            return $"Sections = {ConfigurationSectionDebugView.FromConfiguration(this, this).Count}";
        }

        private sealed class ConfigurationRootDebugView
        {
            private readonly ConfigurationRoot _current;

            public ConfigurationRootDebugView(ConfigurationRoot current)
            {
                _current = current;
            }

            [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
            public ConfigurationSectionDebugView[] Items => ConfigurationSectionDebugView.FromConfiguration(_current, _current).ToArray();
        }
    }
}
