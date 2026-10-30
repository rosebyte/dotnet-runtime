// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Primitives;
using Moq;
using Xunit;

namespace Microsoft.Extensions.Configuration.Test
{
    public class ConfigurationManagerTest
    {
        [Theory]
        [InlineData(ConfigurationFailureStage.Load)]
        [InlineData(ConfigurationFailureStage.GetReloadToken)]
        [InlineData(ConfigurationFailureStage.RegisterChangeCallback)]
        public void FailedOwnedAdditionDisposesCandidateAndAllowsFreshRetry(ConfigurationFailureStage stage)
        {
            using var config = new ConfigurationManager();
            Exception failure = null;
            var source = new OwnedConfigurationFailureSource
            {
                Configure = p => failure = new ConfigurationFailureSource(p).SetFailure(stage)
            };

            Exception exception = Assert.Throws<InvalidOperationException>(() => config.Sources.Add(source));
            Assert.Same(failure, exception);
            ConfigurationFailureProvider rejected = source.Provider;
            Assert.Equal(1, rejected.DisposeCount);
            Assert.Equal(0, rejected.Token.RegistrationCount);
            Assert.DoesNotContain(source, config.Sources);

            source.Configure = null;
            config.Sources.Add(source);
            Assert.NotSame(rejected, source.Provider);
            Assert.Equal(0, source.Provider.DisposeCount);
            Assert.Equal(1, source.Provider.Token.RegistrationCount);
        }

        [Theory]
        [InlineData(ConfigurationFailureStage.Build)]
        [InlineData(ConfigurationFailureStage.Load)]
        [InlineData(ConfigurationFailureStage.GetReloadToken)]
        [InlineData(ConfigurationFailureStage.RegisterChangeCallback)]
        public void FailedOwnedRebuildDisposesOnlyReplacementCandidates(ConfigurationFailureStage stage)
        {
            using var config = new ConfigurationManager();
            var owned = new OwnedConfigurationFailureSource();
            using var borrowedProvider = new ConfigurationFailureProvider();
            var failingSource = new ConfigurationFailureSource(borrowedProvider);
            config.Sources.Add(owned);
            config.Sources.Add(failingSource);
            ConfigurationFailureProvider active = owned.Provider;
            Exception failure = failingSource.SetFailure(stage);

            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
                ((IConfigurationBuilder)config).Properties["changed"] = true));
            ConfigurationFailureProvider rejected = owned.Provider;
            Assert.NotSame(active, rejected);
            Assert.Equal(1, rejected.DisposeCount);
            Assert.Equal(0, rejected.Token.RegistrationCount);
            Assert.Equal(0, active.DisposeCount);
            Assert.Equal(1, active.Token.RegistrationCount);
            Assert.Equal(0, borrowedProvider.DisposeCount);

            failingSource.ClearFailure();
            ((IConfigurationBuilder)config).Properties["retry"] = true;
            Assert.NotSame(rejected, owned.Provider);
            Assert.Equal(0, owned.Provider.DisposeCount);
            Assert.Equal(1, active.DisposeCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NotificationFailureDoesNotDisposeOwnedCandidate(bool replaceProviders)
        {
            using var config = new ConfigurationManager();
            var source = new OwnedConfigurationFailureSource();
            var failure = new InvalidOperationException("Notification failed.");
            using IDisposable registration = ((IConfiguration)config).GetReloadToken()
                .RegisterChangeCallback(_ => throw failure, null);

            AggregateException exception = Assert.Throws<AggregateException>(() =>
            {
                if (replaceProviders)
                {
                    config.Sources.Insert(0, source);
                }
                else
                {
                    config.Sources.Add(source);
                }
            });

            Assert.Same(failure, Assert.Single(exception.InnerExceptions));
            Assert.Contains(source.Provider, ((IConfigurationRoot)config).Providers);
            Assert.Equal(0, source.Provider.DisposeCount);
        }

        [Fact]
        public void RetirementFailureDoesNotDisposeOwnedReplacement()
        {
            using var config = new ConfigurationManager();
            var source = new OwnedConfigurationFailureSource();
            config.Sources.Add(source);
            ConfigurationFailureProvider previous = source.Provider;
            var failure = new InvalidOperationException("Provider disposal failed.");
            previous.DisposeException = failure;

            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
                ((IConfigurationBuilder)config).Properties["changed"] = true));
            Assert.NotSame(previous, source.Provider);
            Assert.Equal(1, previous.DisposeCount);
            Assert.Equal(0, source.Provider.DisposeCount);
            Assert.Contains(source.Provider, ((IConfigurationRoot)config).Providers);
        }

        [Theory]
        [InlineData(ConfigurationFailureStage.Build)]
        [InlineData(ConfigurationFailureStage.Load)]
        [InlineData(ConfigurationFailureStage.GetReloadToken)]
        [InlineData(ConfigurationFailureStage.RegisterChangeCallback)]
        public void FailedAdditionRemovesSourceAndAllowsRetry(ConfigurationFailureStage stage)
        {
            using var provider = new ConfigurationFailureProvider();
            var source = new ConfigurationFailureSource(provider);
            using var config = new ConfigurationManager();
            IConfigurationBuilder builder = config;
            IConfigurationSource[] originalSources = config.Sources.ToArray();
            IChangeToken token = ((IConfiguration)config).GetReloadToken();
            Exception failure = source.SetFailure(stage);

            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => builder.Add(source)));
            Assert.Equal(originalSources, config.Sources);
            Assert.Equal(0, provider.DisposeCount);
            Assert.Equal(0, provider.Token.RegistrationCount);
            Assert.False(token.HasChanged);

            builder.Properties["retry"] = true;
            Assert.Equal(1, source.BuildCount);

            source.ClearFailure();
            builder.Add(source);
            Assert.Equal(2, source.BuildCount);
            Assert.Equal(1, provider.Token.RegistrationCount);
            Assert.Same(source, config.Sources.Last());
            Assert.Equal("value", config["key"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FailedAdditionToDisposedManagerReleasesRegistration(bool cleanupThrows)
        {
            using var provider = new ConfigurationFailureProvider();
            var source = new ConfigurationFailureSource(provider);
            var config = new ConfigurationManager();
            config.Dispose();
            IConfigurationSource[] originalSources = config.Sources.ToArray();
            var cleanupFailure = new InvalidOperationException("Registration disposal failed.");
            if (cleanupThrows)
            {
                provider.Token.DisposeException = cleanupFailure;
            }

            if (cleanupThrows)
            {
                AggregateException exception = Assert.Throws<AggregateException>(() => config.Sources.Add(source));
                Assert.Equal(2, exception.InnerExceptions.Count);
                Assert.IsType<ObjectDisposedException>(exception.InnerExceptions[0]);
                Assert.Same(cleanupFailure, exception.InnerExceptions[1]);
            }
            else
            {
                Assert.Throws<ObjectDisposedException>(() => config.Sources.Add(source));
            }

            Assert.Equal(originalSources, config.Sources);
            Assert.Equal(0, provider.Token.RegistrationCount);
            Assert.Equal(0, provider.DisposeCount);
        }

        [Theory]
        [InlineData(nameof(IList<IConfigurationSource>.Add))]
        [InlineData(nameof(IList<IConfigurationSource>.Clear))]
        [InlineData(nameof(IList<IConfigurationSource>.Insert))]
        [InlineData(nameof(IConfigurationBuilder.Properties))]
        public void FailedAdditionDoesNotUndoReentrantChanges(string mutation)
        {
            using var provider = new ConfigurationFailureProvider();
            var source = new ConfigurationFailureSource(provider);
            using var config = new ConfigurationManager();
            var failure = new InvalidOperationException("Source construction failed after changing the builder.");
            IConfigurationSource[] expectedSources = null;
            IConfigurationProvider[] expectedProviders = null;
            source.BeforeBuild = builder =>
            {
                source.BeforeBuild = null;
                switch (mutation)
                {
                    case nameof(IList<IConfigurationSource>.Add):
                        builder.Sources.Add(new MemoryConfigurationSource());
                        break;
                    case nameof(IList<IConfigurationSource>.Clear):
                        builder.Sources.Clear();
                        break;
                    case nameof(IList<IConfigurationSource>.Insert):
                        builder.Sources.Insert(0, new MemoryConfigurationSource());
                        break;
                    case nameof(IConfigurationBuilder.Properties):
                        builder.Properties["changed"] = true;
                        break;
                }

                expectedSources = builder.Sources
                    .Where(s => mutation != nameof(IList<IConfigurationSource>.Add) || !ReferenceEquals(s, source))
                    .ToArray();
                expectedProviders = ((IConfigurationRoot)builder).Providers.ToArray();
                source.BuildException = failure;
                throw failure;
            };

            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => config.Sources.Add(source)));
            Assert.Equal(expectedSources, config.Sources);
            Assert.Equal(expectedProviders, ((IConfigurationRoot)config).Providers);
            Assert.Equal(0, provider.DisposeCount);
            if (mutation == nameof(IList<IConfigurationSource>.Add))
            {
                ((IConfigurationBuilder)config).Properties["retry"] = true;
                Assert.Equal(1, source.BuildCount);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FailedAdditionPreservesCachedProvider(bool sameManager)
        {
            using var provider = new ConfigurationFailureProvider();
            var source = new ConfigurationFailureSource(provider);
            using var owner = new ConfigurationManager();
            using var other = new ConfigurationManager();
            owner.Sources.Add(source);
            ConfigurationManager target = sameManager ? owner : other;
            IConfigurationSource[] originalSources = target.Sources.ToArray();
            Exception failure = source.SetFailure(ConfigurationFailureStage.Load);

            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => target.Sources.Add(source)));
            Assert.Equal(originalSources, target.Sources);
            Assert.Equal(0, provider.DisposeCount);
            Assert.Equal(1, provider.Token.RegistrationCount);
            Assert.Equal("value", owner["key"]);
            source.ClearFailure();
            IChangeToken token = ((IConfiguration)owner).GetReloadToken();
            provider.Reload();
            Assert.True(token.HasChanged);
        }

        [Theory]
        [InlineData(false, ConfigurationFailureStage.Build)]
        [InlineData(false, ConfigurationFailureStage.Load)]
        [InlineData(false, ConfigurationFailureStage.GetReloadToken)]
        [InlineData(false, ConfigurationFailureStage.RegisterChangeCallback)]
        [InlineData(true, ConfigurationFailureStage.Build)]
        [InlineData(true, ConfigurationFailureStage.Load)]
        [InlineData(true, ConfigurationFailureStage.GetReloadToken)]
        [InlineData(true, ConfigurationFailureStage.RegisterChangeCallback)]
        public void FailedSourceReloadPreservesActiveConfiguration(bool changeProperty, ConfigurationFailureStage stage)
        {
            using var active = new ConfigurationFailureProvider();
            using var candidate = new ConfigurationFailureProvider();
            using var failing = new ConfigurationFailureProvider();
            var source = new ConfigurationFailureSource(active);
            var failingSource = new ConfigurationFailureSource(failing);
            using var config = new ConfigurationManager();
            IConfigurationBuilder builder = config;
            config.Sources.Add(source);
            config.Sources.Add(failingSource);
            source.Provider = candidate;
            Exception failure = failingSource.SetFailure(stage);
            IConfigurationProvider[] originalProviders = ((IConfigurationRoot)config).Providers.ToArray();
            IChangeToken token = ((IConfiguration)config).GetReloadToken();

            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            {
                if (changeProperty)
                {
                    builder.Properties["changed"] = true;
                }
                else
                {
                    config.Sources.Insert(0, new MemoryConfigurationSource());
                }
            }));

            Assert.Equal(originalProviders, ((IConfigurationRoot)config).Providers);
            Assert.Equal(0, active.DisposeCount);
            Assert.Equal(0, candidate.DisposeCount);
            Assert.Equal(0, failing.DisposeCount);
            Assert.Equal(1, active.Token.RegistrationCount);
            Assert.Equal(1, failing.Token.RegistrationCount);
            Assert.Equal(0, candidate.Token.RegistrationCount);
            Assert.False(token.HasChanged);
            Assert.Equal("value", config["key"]);
            if (changeProperty)
            {
                Assert.Equal(true, builder.Properties["changed"]);
            }
            else
            {
                Assert.Equal(4, config.Sources.Count);
            }

            active.Reload();
            Assert.True(token.HasChanged);

            failingSource.ClearFailure();
            builder.Properties["retry"] = true;
            Assert.Contains(candidate, ((IConfigurationRoot)config).Providers);
            Assert.Equal(1, candidate.Token.RegistrationCount);
            Assert.Equal(0, active.Token.RegistrationCount);
            Assert.Equal(1, failing.Token.RegistrationCount);
            Assert.Equal(1, active.DisposeCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NotificationFailureDoesNotUndoPublishedChanges(bool replaceProviders)
        {
            using var provider = new ConfigurationFailureProvider();
            var source = new ConfigurationFailureSource(provider);
            using var config = new ConfigurationManager();
            var failure = new InvalidOperationException("Notification failed.");
            using IDisposable registration = ((IConfiguration)config).GetReloadToken()
                .RegisterChangeCallback(_ => throw failure, null);

            AggregateException exception = Assert.Throws<AggregateException>(() =>
            {
                if (replaceProviders)
                {
                    config.Sources.Insert(0, source);
                }
                else
                {
                    config.Sources.Add(source);
                }
            });

            Assert.Same(failure, Assert.Single(exception.InnerExceptions));
            Assert.Contains(source, config.Sources);
            Assert.Contains(provider, ((IConfigurationRoot)config).Providers);
            Assert.Equal(0, provider.DisposeCount);
            Assert.Equal(1, provider.Token.RegistrationCount);
            Assert.Equal("value", config["key"]);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void DisposalFailureDoesNotUndoPublishedChanges(bool registrationThrows, bool notificationThrows)
        {
            var oldProvider = new ConfigurationFailureProvider();
            using var newProvider = new ConfigurationFailureProvider();
            var source = new ConfigurationFailureSource(oldProvider);
            using var config = new ConfigurationManager();
            config.Sources.Add(source);
            source.Provider = newProvider;
            var failure = new InvalidOperationException("Disposal failed.");
            var notificationFailure = new InvalidOperationException("Notification failed.");
            IChangeToken token = ((IConfiguration)config).GetReloadToken();
            using IDisposable registration = token.RegisterChangeCallback(_ =>
            {
                if (notificationThrows)
                {
                    throw notificationFailure;
                }
            }, null);
            if (registrationThrows)
            {
                oldProvider.Token.DisposeException = failure;
            }
            else
            {
                oldProvider.DisposeException = failure;
            }

            if (notificationThrows)
            {
                AggregateException exception = Assert.Throws<AggregateException>(() =>
                    ((IConfigurationBuilder)config).Properties["changed"] = true);
                Assert.Equal(new[] { failure, notificationFailure }, exception.Flatten().InnerExceptions);
            }
            else
            {
                Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
                    ((IConfigurationBuilder)config).Properties["changed"] = true));
            }

            Assert.True(token.HasChanged);
            Assert.Contains(newProvider, ((IConfigurationRoot)config).Providers);
            Assert.DoesNotContain(oldProvider, ((IConfigurationRoot)config).Providers);
            Assert.Equal(true, ((IConfigurationBuilder)config).Properties["changed"]);
            Assert.Equal(0, oldProvider.Token.RegistrationCount);
            Assert.Equal(1, oldProvider.DisposeCount);
            Assert.Equal(0, newProvider.DisposeCount);
            Assert.Equal(1, newProvider.Token.RegistrationCount);
            Assert.Equal("value", config["key"]);
        }

        [Fact]
        public void RetirementReportsMultipleFailuresAndNotifiesSubscribers()
        {
            var first = new ConfigurationFailureProvider();
            var second = new ConfigurationFailureProvider();
            using var config = new ConfigurationManager();
            config.Sources.Add(new ConfigurationFailureSource(first));
            config.Sources.Add(new ConfigurationFailureSource(second));
            var registrationFailure = new InvalidOperationException("Registration disposal failed.");
            var providerFailure = new InvalidOperationException("Provider disposal failed.");
            first.Token.DisposeException = registrationFailure;
            second.DisposeException = providerFailure;
            IChangeToken token = ((IConfiguration)config).GetReloadToken();

            AggregateException exception = Assert.Throws<AggregateException>(() => config.Sources.Clear());

            Assert.Equal(new[] { registrationFailure, providerFailure }, exception.InnerExceptions);
            Assert.True(token.HasChanged);
            Assert.Empty(config.Sources);
            Assert.Empty(((IConfigurationRoot)config).Providers);
            Assert.Equal(0, first.Token.RegistrationCount);
            Assert.Equal(0, second.Token.RegistrationCount);
            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(1, second.DisposeCount);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void FailedReloadPreservesProvidersAndAllowsRecovery(bool useManager, bool fromWatcher)
        {
            using var provider = new ConfigurationFailureProvider();
            var source = new ConfigurationFailureSource(provider);
            IConfigurationBuilder builder = useManager ? new ConfigurationManager() : new ConfigurationBuilder();
            builder.Add(source);
            IConfigurationRoot root = builder.Build();
            using var owner = (IDisposable)root;
            Exception failure = source.SetFailure(ConfigurationFailureStage.Load);

            if (fromWatcher)
            {
                using IDisposable registration = root.GetReloadToken().RegisterChangeCallback(_ => root.Reload(), null);
                AggregateException exception = Assert.Throws<AggregateException>(() => provider.Reload());
                Assert.Contains(failure, exception.Flatten().InnerExceptions);
            }
            else
            {
                Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => root.Reload()));
            }

            Assert.Equal(0, provider.DisposeCount);
            Assert.Equal(1, provider.Token.RegistrationCount);
            source.ClearFailure();
            root.Reload();
            IChangeToken token = root.GetReloadToken();
            provider.Reload();
            Assert.True(token.HasChanged);
        }

        [Fact]
        public void AutoUpdates()
        {
            var config = new ConfigurationManager();

            config.AddInMemoryCollection(new Dictionary<string, string>
            {
                { "TestKey", "TestValue" },
            });

            Assert.Equal("TestValue", config["TestKey"]);
        }

        [Fact]
        public void TriggersReloadTokenOnSourceAddition()
        {
            var config = new ConfigurationManager();

            var reloadToken = ((IConfiguration)config).GetReloadToken();

            Assert.False(reloadToken.HasChanged);

            config.AddInMemoryCollection(new Dictionary<string, string>
            {
                { "TestKey", "TestValue" },
            });

            Assert.True(reloadToken.HasChanged);
        }

        [Fact]
        public void SettingValuesWorksWithoutManuallyAddingSource()
        {
            var config = new ConfigurationManager
            {
                ["TestKey"] = "TestValue",
            };

            Assert.Equal("TestValue", config["TestKey"]);
        }

        [Fact]
        public void SettingConfigValuesDoesNotTriggerReloadToken()
        {
            var config = new ConfigurationManager();
            var reloadToken = ((IConfiguration)config).GetReloadToken();

            config["TestKey"] = "TestValue";

            Assert.Equal("TestValue", config["TestKey"]);

            // ConfigurationRoot doesn't fire the token today when the setter is called.
            Assert.False(reloadToken.HasChanged);
        }

        [Fact]
        public void SettingIConfigurationBuilderPropertiesReloadsSources()
        {
            var config = new ConfigurationManager();
            IConfigurationBuilder configBuilder = config;

            config["PreReloadTestConfigKey"] = "PreReloadTestConfigValue";

            var reloadToken1 = ((IConfiguration)config).GetReloadToken();
            // Changing Properties causes all the IConfigurationSources to be reload.
            configBuilder.Properties["TestPropertyKey"] = "TestPropertyValue";

            var reloadToken2 = ((IConfiguration)config).GetReloadToken();
            config["PostReloadTestConfigKey"] = "PostReloadTestConfigValue";

            Assert.Equal("TestPropertyValue", configBuilder.Properties["TestPropertyKey"]);
            Assert.Null(config["TestPropertyKey"]);

            // Changes before the reload are lost by the MemoryConfigurationSource.
            Assert.Null(config["PreReloadTestConfigKey"]);
            Assert.Equal("PostReloadTestConfigValue", config["PostReloadTestConfigKey"]);

            Assert.True(reloadToken1.HasChanged);
            Assert.False(reloadToken2.HasChanged);
        }

        [Fact]
        public void DisposesProvidersOnDispose()
        {
            var provider1 = new TestConfigurationProvider("foo", "foo-value");
            var provider2 = new DisposableTestConfigurationProvider("bar", "bar-value");
            var provider3 = new TestConfigurationProvider("baz", "baz-value");
            var provider4 = new DisposableTestConfigurationProvider("qux", "qux-value");
            var provider5 = new DisposableTestConfigurationProvider("quux", "quux-value");

            var config = new ConfigurationManager();
            IConfigurationBuilder builder = config;

            builder.Add(new TestConfigurationSource(provider1));
            builder.Add(new TestConfigurationSource(provider2));
            builder.Add(new TestConfigurationSource(provider3));
            builder.Add(new TestConfigurationSource(provider4));
            builder.Add(new TestConfigurationSource(provider5));

            Assert.Equal("foo-value", config["foo"]);
            Assert.Equal("bar-value", config["bar"]);
            Assert.Equal("baz-value", config["baz"]);
            Assert.Equal("qux-value", config["qux"]);
            Assert.Equal("quux-value", config["quux"]);

            config.Dispose();

            Assert.True(provider2.IsDisposed);
            Assert.True(provider4.IsDisposed);
            Assert.True(provider5.IsDisposed);
        }

        [Fact]
        public void DisposesProvidersOnRemoval()
        {
            var provider1 = new TestConfigurationProvider("foo", "foo-value");
            var provider2 = new DisposableTestConfigurationProvider("bar", "bar-value");
            var provider3 = new TestConfigurationProvider("baz", "baz-value");
            var provider4 = new DisposableTestConfigurationProvider("qux", "qux-value");
            var provider5 = new DisposableTestConfigurationProvider("quux", "quux-value");

            var source1 = new TestConfigurationSource(provider1);
            var source2 = new TestConfigurationSource(provider2);
            var source3 = new TestConfigurationSource(provider3);
            var source4 = new TestConfigurationSource(provider4);
            var source5 = new TestConfigurationSource(provider5);

            var config = new ConfigurationManager();
            IConfigurationBuilder builder = config;

            builder.Add(source1);
            builder.Add(source2);
            builder.Add(source3);
            builder.Add(source4);
            builder.Add(source5);

            Assert.Equal("foo-value", config["foo"]);
            Assert.Equal("bar-value", config["bar"]);
            Assert.Equal("baz-value", config["baz"]);
            Assert.Equal("qux-value", config["qux"]);
            Assert.Equal("quux-value", config["quux"]);

            builder.Sources.Remove(source2);
            builder.Sources.Remove(source4);

            // While only provider2 and provider4 need to be disposed here, we do not assert provider5 is not disposed
            // because even though it's unnecessary, Configuration disposes all providers on removal and rebuilds
            // all the sources. While not optimal, this should be a pretty rare scenario.
            Assert.True(provider2.IsDisposed);
            Assert.True(provider4.IsDisposed);

            config.Dispose();

            Assert.True(provider2.IsDisposed);
            Assert.True(provider4.IsDisposed);
            Assert.True(provider5.IsDisposed);
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsMultithreadingSupported))]
        public async Task ProviderCanBlockLoadWaitingOnConcurrentRead()
        {
            using var mre = new ManualResetEventSlim(false);
            var provider = new BlockLoadOnMREProvider(mre, timeout: TimeSpan.FromSeconds(30));

            var config = new ConfigurationManager();
            IConfigurationBuilder builder = config;

            // builder.Add(source) will block on provider.Load().
            var loadTask = Task.Run(() => builder.Add(new TestConfigurationSource(provider)));
            await provider.LoadStartedTask;

            // Read configuration while provider.Load() is blocked waiting on us.
            _ = config["key"];

            // Unblock provider.Load()
            mre.Set();

            // This will throw if provider.Load() timed out instead of unblocking gracefully after the read.
            await loadTask;
        }

        public static TheoryData<Action<IConfiguration>> ConcurrentReadActions
        {
            get
            {
                return new TheoryData<Action<IConfiguration>>
                {
                    config => _ = config["key"],
                    config => config.GetChildren(),
                    config => config.GetSection("key").GetChildren(),
                };
            }
        }

        [ConditionalTheory(typeof(PlatformDetection), nameof(PlatformDetection.IsMultithreadingSupported))]
        [MemberData(nameof(ConcurrentReadActions))]
        public async Task ProviderDisposeDelayedWaitingOnConcurrentRead(Action<IConfiguration> concurrentReadAction)
        {
            using var mre = new ManualResetEventSlim(false);
            var provider = new BlockReadOnMREProvider(mre, timeout: TimeSpan.FromSeconds(30));

            var config = new ConfigurationManager();
            IConfigurationBuilder builder = config;

            builder.Add(new TestConfigurationSource(provider));

            // Reading configuration will block on provider.TryRead() or provider.GetChildKeys().
            var readTask = Task.Run(() => concurrentReadAction(config));
            await provider.ReadStartedTask;

            // Removing the source normally disposes the provider except when there provider is in use as is the case here.
            builder.Sources.Clear();

            Assert.False(provider.IsDisposed);

            // Unblock TryRead() or GetChildKeys()
            mre.Set();

            // This will throw if TryRead() or GetChildKeys() timed out instead of unblocking gracefully after setting the MRE.
            await readTask;

            // The provider should be disposed when the concurrentReadAction releases the last reference to the provider.
            Assert.True(provider.IsDisposed);
        }

        [Fact]
        public void DisposingConfigurationManagerCausesOnlySourceChangesToThrow()
        {
            var config = new ConfigurationManager
            {
                ["TestKey"] = "TestValue",
            };

            config.Dispose();

            Assert.Equal("TestValue", config["TestKey"]);
            config["TestKey"] = "TestValue2";
            Assert.Equal("TestValue2", config["TestKey"]);

            Assert.Throws<ObjectDisposedException>(() => config.AddInMemoryCollection());
            Assert.Throws<ObjectDisposedException>(() => ((IConfigurationBuilder)config).Sources.Clear());
        }

        // Moq heavily utilizes RefEmit, which does not work on most aot workloads
        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsReflectionEmitSupported))]
        public void DisposesChangeTokenRegistrationsOnDispose()
        {
            var changeToken = new TestChangeToken();
            var providerMock = new Mock<IConfigurationProvider>();
            providerMock.Setup(p => p.GetReloadToken()).Returns(changeToken);

            var config = new ConfigurationManager();

            ((IConfigurationBuilder)config).Add(new TestConfigurationSource(providerMock.Object));

            Assert.NotEmpty(changeToken.Callbacks);

            config.Dispose();

            Assert.Empty(changeToken.Callbacks);
        }

        // Moq heavily utilizes RefEmit, which does not work on most aot workloads
        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsReflectionEmitSupported))]
        public void DisposesChangeTokenRegistrationsOnRemoval()
        {
            var changeToken = new TestChangeToken();
            var providerMock = new Mock<IConfigurationProvider>();
            providerMock.Setup(p => p.GetReloadToken()).Returns(changeToken);

            var source = new TestConfigurationSource(providerMock.Object);

            var config = new ConfigurationManager();
            IConfigurationBuilder builder = config;

            builder.Add(source);

            Assert.NotEmpty(changeToken.Callbacks);

            builder.Sources.Remove(source);

            Assert.Empty(changeToken.Callbacks);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ChainedConfigurationIsDisposedOnDispose(bool shouldDispose)
        {
            var provider = new DisposableTestConfigurationProvider("foo", "foo-value");
            var chainedConfig = new ConfigurationRoot(new IConfigurationProvider[] {
                provider
            });

            var config = new ConfigurationManager();

            config.AddConfiguration(chainedConfig, shouldDisposeConfiguration: shouldDispose);

            Assert.False(provider.IsDisposed);

            config.Dispose();

            Assert.Equal(shouldDispose, provider.IsDisposed);
        }

        [Fact]
        public void LoadAndCombineKeyValuePairsFromDifferentConfigurationProviders()
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"Mem1:KeyInMem1", "ValueInMem1"}
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"Mem2:KeyInMem2", "ValueInMem2"}
            };
            var dic3 = new Dictionary<string, string>()
            {
                {"Mem3:KeyInMem3", "ValueInMem3"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };
            var memConfigSrc3 = new MemoryConfigurationSource { InitialData = dic3 };

            var config = new ConfigurationManager();
            IConfigurationBuilder configurationBuilder = config;

            // Act
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            configurationBuilder.Add(memConfigSrc3);

            var memVal1 = config["mem1:keyinmem1"];
            var memVal2 = config["Mem2:KeyInMem2"];
            var memVal3 = config["MEM3:KEYINMEM3"];

            // Assert
            Assert.Contains(memConfigSrc1, configurationBuilder.Sources);
            Assert.Contains(memConfigSrc2, configurationBuilder.Sources);
            Assert.Contains(memConfigSrc3, configurationBuilder.Sources);

            Assert.Equal("ValueInMem1", memVal1);
            Assert.Equal("ValueInMem2", memVal2);
            Assert.Equal("ValueInMem3", memVal3);

            Assert.Equal("ValueInMem1", config["mem1:keyinmem1"]);
            Assert.Equal("ValueInMem2", config["Mem2:KeyInMem2"]);
            Assert.Equal("ValueInMem3", config["MEM3:KEYINMEM3"]);
            Assert.Null(config["NotExist"]);
        }

        [Fact]
        public void CanChainConfiguration()
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"Mem1:KeyInMem1", "ValueInMem1"}
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"Mem2:KeyInMem2", "ValueInMem2"}
            };
            var dic3 = new Dictionary<string, string>()
            {
                {"Mem3:KeyInMem3", "ValueInMem3"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };
            var memConfigSrc3 = new MemoryConfigurationSource { InitialData = dic3 };

            var config = new ConfigurationManager();
            IConfigurationBuilder configurationBuilder = config;

            // Act
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            configurationBuilder.Add(memConfigSrc3);

            var chained = new ConfigurationManager();
            chained.AddConfiguration(config);
            var memVal1 = chained["mem1:keyinmem1"];
            var memVal2 = chained["Mem2:KeyInMem2"];
            var memVal3 = chained["MEM3:KEYINMEM3"];

            // Assert

            Assert.Equal("ValueInMem1", memVal1);
            Assert.Equal("ValueInMem2", memVal2);
            Assert.Equal("ValueInMem3", memVal3);

            Assert.Null(chained["NotExist"]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ChainedAsEnumerateFlattensIntoDictionaryTest(bool removePath)
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"Mem1", "Value1"},
                {"Mem1:", "NoKeyValue1"},
                {"Mem1:KeyInMem1", "ValueInMem1"},
                {"Mem1:KeyInMem1:Deep1", "ValueDeep1"}
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"Mem2", "Value2"},
                {"Mem2:", "NoKeyValue2"},
                {"Mem2:KeyInMem2", "ValueInMem2"},
                {"Mem2:KeyInMem2:Deep2", "ValueDeep2"}
            };
            var dic3 = new Dictionary<string, string>()
            {
                {"Mem3", "Value3"},
                {"Mem3:", "NoKeyValue3"},
                {"Mem3:KeyInMem3", "ValueInMem3"},
                {"Mem3:KeyInMem3:Deep3", "ValueDeep3"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };
            var memConfigSrc3 = new MemoryConfigurationSource { InitialData = dic3 };

            var config1 = new ConfigurationManager();
            IConfigurationBuilder configurationBuilder = config1;

            // Act
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);

            var config2 = new ConfigurationManager();

            config2
                .AddConfiguration(config1)
                .Add(memConfigSrc3);

            var dict = config2.AsEnumerable(makePathsRelative: removePath).ToDictionary(k => k.Key, v => v.Value);

            // Assert
            Assert.Equal("Value1", dict["Mem1"]);
            Assert.Equal("NoKeyValue1", dict["Mem1:"]);
            Assert.Equal("ValueDeep1", dict["Mem1:KeyInMem1:Deep1"]);
            Assert.Equal("ValueInMem2", dict["Mem2:KeyInMem2"]);
            Assert.Equal("Value2", dict["Mem2"]);
            Assert.Equal("NoKeyValue2", dict["Mem2:"]);
            Assert.Equal("ValueDeep2", dict["Mem2:KeyInMem2:Deep2"]);
            Assert.Equal("Value3", dict["Mem3"]);
            Assert.Equal("NoKeyValue3", dict["Mem3:"]);
            Assert.Equal("ValueInMem3", dict["Mem3:KeyInMem3"]);
            Assert.Equal("ValueDeep3", dict["Mem3:KeyInMem3:Deep3"]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AsEnumerateFlattensIntoDictionaryTest(bool removePath)
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"Mem1", "Value1"},
                {"Mem1:", "NoKeyValue1"},
                {"Mem1:KeyInMem1", "ValueInMem1"},
                {"Mem1:KeyInMem1:Deep1", "ValueDeep1"}
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"Mem2", "Value2"},
                {"Mem2:", "NoKeyValue2"},
                {"Mem2:KeyInMem2", "ValueInMem2"},
                {"Mem2:KeyInMem2:Deep2", "ValueDeep2"}
            };
            var dic3 = new Dictionary<string, string>()
            {
                {"Mem3", "Value3"},
                {"Mem3:", "NoKeyValue3"},
                {"Mem3:KeyInMem3", "ValueInMem3"},
                {"Mem3:KeyInMem3:Deep3", "ValueDeep3"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };
            var memConfigSrc3 = new MemoryConfigurationSource { InitialData = dic3 };

            var config = new ConfigurationManager();
            IConfigurationBuilder configurationBuilder = config;

            // Act
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            configurationBuilder.Add(memConfigSrc3);
            var dict = config.AsEnumerable(makePathsRelative: removePath).ToDictionary(k => k.Key, v => v.Value);

            // Assert
            Assert.Equal("Value1", dict["Mem1"]);
            Assert.Equal("NoKeyValue1", dict["Mem1:"]);
            Assert.Equal("ValueDeep1", dict["Mem1:KeyInMem1:Deep1"]);
            Assert.Equal("ValueInMem2", dict["Mem2:KeyInMem2"]);
            Assert.Equal("Value2", dict["Mem2"]);
            Assert.Equal("NoKeyValue2", dict["Mem2:"]);
            Assert.Equal("ValueDeep2", dict["Mem2:KeyInMem2:Deep2"]);
            Assert.Equal("Value3", dict["Mem3"]);
            Assert.Equal("NoKeyValue3", dict["Mem3:"]);
            Assert.Equal("ValueInMem3", dict["Mem3:KeyInMem3"]);
            Assert.Equal("ValueDeep3", dict["Mem3:KeyInMem3:Deep3"]);
        }

        [Fact]
        public void AsEnumerateStripsKeyFromChildren()
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"Mem1", "Value1"},
                {"Mem1:", "NoKeyValue1"},
                {"Mem1:KeyInMem1", "ValueInMem1"},
                {"Mem1:KeyInMem1:Deep1", "ValueDeep1"}
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"Mem2", "Value2"},
                {"Mem2:", "NoKeyValue2"},
                {"Mem2:KeyInMem2", "ValueInMem2"},
                {"Mem2:KeyInMem2:Deep2", "ValueDeep2"}
            };
            var dic3 = new Dictionary<string, string>()
            {
                {"Mem3", "Value3"},
                {"Mem3:", "NoKeyValue3"},
                {"Mem3:KeyInMem3", "ValueInMem3"},
                {"Mem3:KeyInMem4", "ValueInMem4"},
                {"Mem3:KeyInMem3:Deep3", "ValueDeep3"},
                {"Mem3:KeyInMem3:Deep4", "ValueDeep4"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };
            var memConfigSrc3 = new MemoryConfigurationSource { InitialData = dic3 };

            var config = new ConfigurationManager();
            IConfigurationBuilder configurationBuilder = config;

            // Act
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            configurationBuilder.Add(memConfigSrc3);

            var dict = config.GetSection("Mem1").AsEnumerable(makePathsRelative: true).ToDictionary(k => k.Key, v => v.Value);
            Assert.Equal(3, dict.Count);
            Assert.Equal("NoKeyValue1", dict[""]);
            Assert.Equal("ValueInMem1", dict["KeyInMem1"]);
            Assert.Equal("ValueDeep1", dict["KeyInMem1:Deep1"]);

            var dict2 = config.GetSection("Mem2").AsEnumerable(makePathsRelative: true).ToDictionary(k => k.Key, v => v.Value);
            Assert.Equal(3, dict2.Count);
            Assert.Equal("NoKeyValue2", dict2[""]);
            Assert.Equal("ValueInMem2", dict2["KeyInMem2"]);
            Assert.Equal("ValueDeep2", dict2["KeyInMem2:Deep2"]);

            var dict3 = config.GetSection("Mem3").AsEnumerable(makePathsRelative: true).ToDictionary(k => k.Key, v => v.Value);
            Assert.Equal(5, dict3.Count);
            Assert.Equal("NoKeyValue3", dict3[""]);
            Assert.Equal("ValueInMem3", dict3["KeyInMem3"]);
            Assert.Equal("ValueInMem4", dict3["KeyInMem4"]);
            Assert.Equal("ValueDeep3", dict3["KeyInMem3:Deep3"]);
            Assert.Equal("ValueDeep4", dict3["KeyInMem3:Deep4"]);
        }

        [Fact]
        public void NewConfigurationProviderOverridesOldOneWhenKeyIsDuplicated()
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
                {
                    {"Key1:Key2", "ValueInMem1"}
                };
            var dic2 = new Dictionary<string, string>()
                {
                    {"Key1:Key2", "ValueInMem2"}
                };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };

            var config = new ConfigurationManager();
            IConfigurationBuilder configurationBuilder = config;

            // Act
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);

            // Assert
            Assert.Equal("ValueInMem2", config["Key1:Key2"]);
        }

        [Fact]
        public void NewConfigurationRootMayBeBuiltFromExistingWithDuplicateKeys()
        {
            var configurationRoot = new ConfigurationManager();

            configurationRoot.AddInMemoryCollection(new Dictionary<string, string>
            {
                {"keya:keyb", "valueA"},
            });
            configurationRoot.AddInMemoryCollection(new Dictionary<string, string>
            {
                {"KEYA:KEYB", "valueB"},
            });

            var newConfigurationRoot = new ConfigurationManager();

            newConfigurationRoot.AddInMemoryCollection(configurationRoot.AsEnumerable());

            Assert.Equal("valueB", newConfigurationRoot["keya:keyb"]);
        }

        [Fact]
        public void SettingValueUpdatesAllConfigurationProviders()
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Key1", "Value1"},
                {"Key2", "Value2"}
            };

            var memConfigSrc1 = new TestMemorySourceProvider(dict);
            var memConfigSrc2 = new TestMemorySourceProvider(dict);
            var memConfigSrc3 = new TestMemorySourceProvider(dict);

            var config = new ConfigurationManager();
            IConfigurationBuilder configurationBuilder = config;

            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            configurationBuilder.Add(memConfigSrc3);

            // Act
            config["Key1"] = "NewValue1";
            config["Key2"] = "NewValue2";

            var memConfigProvider1 = memConfigSrc1.Build(configurationBuilder);
            var memConfigProvider2 = memConfigSrc2.Build(configurationBuilder);
            var memConfigProvider3 = memConfigSrc3.Build(configurationBuilder);

            // Assert
            Assert.Equal("NewValue1", config["Key1"]);
            Assert.Equal("NewValue1", Get(memConfigProvider1, "Key1"));
            Assert.Equal("NewValue1", Get(memConfigProvider2, "Key1"));
            Assert.Equal("NewValue1", Get(memConfigProvider3, "Key1"));
            Assert.Equal("NewValue2", config["Key2"]);
            Assert.Equal("NewValue2", Get(memConfigProvider1, "Key2"));
            Assert.Equal("NewValue2", Get(memConfigProvider2, "Key2"));
            Assert.Equal("NewValue2", Get(memConfigProvider3, "Key2"));
        }

        [Fact]
        public void CanGetConfigurationSection()
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"Data:DB1:Connection1", "MemVal1"},
                {"Data:DB1:Connection2", "MemVal2"}
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"DataSource:DB2:Connection", "MemVal3"}
            };
            var dic3 = new Dictionary<string, string>()
            {
                {"Data", "MemVal4"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };
            var memConfigSrc3 = new MemoryConfigurationSource { InitialData = dic3 };

            var config = new ConfigurationManager();
            IConfigurationBuilder configurationBuilder = config;

            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            configurationBuilder.Add(memConfigSrc3);

            // Act
            var configFocus = config.GetSection("Data");

            var memVal1 = configFocus["DB1:Connection1"];
            var memVal2 = configFocus["DB1:Connection2"];
            var memVal3 = configFocus["DB2:Connection"];
            var memVal4 = configFocus["Source:DB2:Connection"];
            var memVal5 = configFocus.Value;

            // Assert
            Assert.Equal("MemVal1", memVal1);
            Assert.Equal("MemVal2", memVal2);
            Assert.Equal("MemVal4", memVal5);

            Assert.Equal("MemVal1", configFocus["DB1:Connection1"]);
            Assert.Equal("MemVal2", configFocus["DB1:Connection2"]);
            Assert.Null(configFocus["DB2:Connection"]);
            Assert.Null(configFocus["Source:DB2:Connection"]);
            Assert.Equal("MemVal4", configFocus.Value);
        }

        [Fact]
        public void CanGetConnectionStrings()
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"ConnectionStrings:DB1:Connection1", "MemVal1"},
                {"ConnectionStrings:DB1:Connection2", "MemVal2"}
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"ConnectionStrings:DB2:Connection", "MemVal3"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };

            var config = new ConfigurationManager();
            IConfigurationBuilder configurationBuilder = config;

            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);

            // Act
            var memVal1 = config.GetConnectionString("DB1:Connection1");
            var memVal2 = config.GetConnectionString("DB1:Connection2");
            var memVal3 = config.GetConnectionString("DB2:Connection");

            // Assert
            Assert.Equal("MemVal1", memVal1);
            Assert.Equal("MemVal2", memVal2);
            Assert.Equal("MemVal3", memVal3);
        }

        [Fact]
        public void CanGetConfigurationChildren()
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"Data:DB1:Connection1", "MemVal1"},
                {"Data:DB1:Connection2", "MemVal2"}
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"Data:DB2Connection", "MemVal3"}
            };
            var dic3 = new Dictionary<string, string>()
            {
                {"DataSource:DB3:Connection", "MemVal4"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };
            var memConfigSrc3 = new MemoryConfigurationSource { InitialData = dic3 };

            var config = new ConfigurationManager();
            IConfigurationBuilder configurationBuilder = config;

            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            configurationBuilder.Add(memConfigSrc3);

            // Act
            var configSections = config.GetSection("Data").GetChildren().ToList();

            // Assert
            Assert.Equal(2, configSections.Count());
            Assert.Equal("MemVal1", configSections.FirstOrDefault(c => c.Key == "DB1")["Connection1"]);
            Assert.Equal("MemVal2", configSections.FirstOrDefault(c => c.Key == "DB1")["Connection2"]);
            Assert.Equal("MemVal3", configSections.FirstOrDefault(c => c.Key == "DB2Connection").Value);
            Assert.False(configSections.Exists(c => c.Key == "DB3"));
            Assert.False(configSections.Exists(c => c.Key == "DB3"));
        }

        [Fact]
        public void SourcesReturnsAddedConfigurationProviders()
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Mem:KeyInMem", "MemVal"}
            };
            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dict };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dict };
            var memConfigSrc3 = new MemoryConfigurationSource { InitialData = dict };

            var config = new ConfigurationManager();
            IConfigurationBuilder configurationBuilder = config;

            // Act

            // A MemoryConfigurationSource is added by default, so there will be no error unless we clear it
            configurationBuilder.Sources.Clear();
            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);
            configurationBuilder.Add(memConfigSrc3);

            // Assert
            Assert.Equal(new[] { memConfigSrc1, memConfigSrc2, memConfigSrc3 }, configurationBuilder.Sources);
        }

        [Fact]
        public void SetValueThrowsExceptionNoSourceRegistered()
        {
            // Arrange
            var config = new ConfigurationManager();

            // A MemoryConfigurationSource is added by default, so there will be no error unless we clear it
            config["Title"] = "Welcome";

            ((IConfigurationBuilder)config).Sources.Clear();

            // Act
            var ex = Assert.Throws<InvalidOperationException>(() => config["Title"] = "Welcome");

            // Assert
            Assert.Equal(SR.Error_NoSources, ex.Message);
        }

        [Fact]
        public void SameReloadTokenIsReturnedRepeatedly()
        {
            // Arrange
            IConfiguration config = new ConfigurationManager();

            // Act
            var token1 = config.GetReloadToken();
            var token2 = config.GetReloadToken();

            // Assert
            Assert.Same(token1, token2);
        }

        [Fact]
        public void DifferentReloadTokenReturnedAfterReloading()
        {
            // Arrange
            IConfigurationRoot config = new ConfigurationManager();

            // Act
            var token1 = config.GetReloadToken();
            var token2 = config.GetReloadToken();
            config.Reload();
            var token3 = config.GetReloadToken();
            var token4 = config.GetReloadToken();

            // Assert
            Assert.Same(token1, token2);
            Assert.Same(token3, token4);
            Assert.NotSame(token1, token3);
        }

        [Fact]
        public void TokenTriggeredWhenReloadOccurs()
        {
            // Arrange
            IConfigurationRoot config = new ConfigurationManager();

            // Act
            var token1 = config.GetReloadToken();
            var hasChanged1 = token1.HasChanged;
            config.Reload();
            var hasChanged2 = token1.HasChanged;

            // Assert
            Assert.False(hasChanged1);
            Assert.True(hasChanged2);
        }

        [Fact]
        public void MultipleCallbacksCanBeRegisteredToReload()
        {
            // Arrange
            IConfigurationRoot config = new ConfigurationManager();

            // Act
            var token1 = config.GetReloadToken();
            var called1 = 0;
            token1.RegisterChangeCallback(_ => called1++, state: null);
            var called2 = 0;
            token1.RegisterChangeCallback(_ => called2++, state: null);

            // Assert
            Assert.Equal(0, called1);
            Assert.Equal(0, called2);

            config.Reload();
            Assert.Equal(1, called1);
            Assert.Equal(1, called2);

            var token2 = config.GetReloadToken();
            var cleanup1 = token2.RegisterChangeCallback(_ => called1++, state: null);
            token2.RegisterChangeCallback(_ => called2++, state: null);

            cleanup1.Dispose();

            config.Reload();
            Assert.Equal(1, called1);
            Assert.Equal(2, called2);
        }

        [Fact]
        public void NewTokenAfterReloadIsNotChanged()
        {
            // Arrange
            IConfigurationRoot config = new ConfigurationManager();

            // Act
            var token1 = config.GetReloadToken();
            var hasChanged1 = token1.HasChanged;
            config.Reload();
            var hasChanged2 = token1.HasChanged;
            var token2 = config.GetReloadToken();
            var hasChanged3 = token2.HasChanged;

            //
            // Assert
            Assert.False(hasChanged1);
            Assert.True(hasChanged2);
            Assert.False(hasChanged3);
            Assert.NotSame(token1, token2);
        }

        [Fact]
        public void KeyStartingWithColonMeansFirstSectionHasEmptyName()
        {
            // Arrange
            var dict = new Dictionary<string, string>
            {
                [":Key2"] = "value"
            };
            var config = new ConfigurationManager();
            config.AddInMemoryCollection(dict);

            // Act
            var children = config.GetChildren().ToArray();

            // Assert
            Assert.Single(children);
            Assert.Equal(string.Empty, children.First().Key);
            Assert.Single(children.First().GetChildren());
            Assert.Equal("Key2", children.First().GetChildren().First().Key);
        }

        [Fact]
        public void KeyWithDoubleColonHasSectionWithEmptyName()
        {
            // Arrange
            var dict = new Dictionary<string, string>
            {
                ["Key1::Key3"] = "value"
            };

            var config = new ConfigurationManager();
            ((IConfigurationBuilder)config).AddInMemoryCollection(dict);

            // Act
            var children = config.GetChildren().ToArray();

            // Assert
            Assert.Single(children);
            Assert.Equal("Key1", children.First().Key);
            Assert.Single(children.First().GetChildren());
            Assert.Equal(string.Empty, children.First().GetChildren().First().Key);
            Assert.Single(children.First().GetChildren().First().GetChildren());
            Assert.Equal("Key3", children.First().GetChildren().First().GetChildren().First().Key);
        }

        [Fact]
        public void KeyEndingWithColonMeansLastSectionHasEmptyName()
        {
            // Arrange
            var dict = new Dictionary<string, string>
            {
                ["Key1:"] = "value"
            };

            var config = new ConfigurationManager();
            ((IConfigurationBuilder)config).AddInMemoryCollection(dict);

            // Act
            var children = config.GetChildren().ToArray();

            // Assert
            Assert.Single(children);
            Assert.Equal("Key1", children.First().Key);
            Assert.Single(children.First().GetChildren());
            Assert.Equal(string.Empty, children.First().GetChildren().First().Key);
        }

        [Fact]
        public void SectionWithValueExists()
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Mem1", "Value1"},
                {"Mem1:KeyInMem1", "ValueInMem1"},
                {"Mem1:KeyInMem1:Deep1", "ValueDeep1"}
            };

            var config = new ConfigurationManager();
            ((IConfigurationBuilder)config).AddInMemoryCollection(dict);

            // Act
            var sectionExists1 = config.GetSection("Mem1").Exists();
            var sectionExists2 = config.GetSection("Mem1:KeyInMem1").Exists();
            var sectionNotExists = config.GetSection("Mem2").Exists();

            // Assert
            Assert.True(sectionExists1);
            Assert.True(sectionExists2);
            Assert.False(sectionNotExists);
        }

        [Fact]
        public void SectionGetRequiredSectionSuccess()
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Mem1", "Value1"},
                {"Mem1:KeyInMem1", "ValueInMem1"},
                {"Mem1:KeyInMem1:Deep1", "ValueDeep1"}
            };

            var config = new ConfigurationManager();
            ((IConfigurationBuilder)config).AddInMemoryCollection(dict);

            // Act
            var sectionExists1 = config.GetRequiredSection("Mem1").Exists();
            var sectionExists2 = config.GetRequiredSection("Mem1:KeyInMem1").Exists();

            // Assert
            Assert.True(sectionExists1);
            Assert.True(sectionExists2);
        }

        [Fact]
        public void SectionGetRequiredSectionMissingThrowException()
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Mem1", "Value1"},
                {"Mem1:Deep1", "Value1"},
            };

            var config = new ConfigurationManager();
            ((IConfigurationBuilder)config).AddInMemoryCollection(dict);

            Assert.Throws<InvalidOperationException>(() => config.GetRequiredSection("Mem2"));
            Assert.Throws<InvalidOperationException>(() => config.GetRequiredSection("Mem1:Deep2"));
        }

        [Fact]
        public void SectionWithChildrenExists()
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Mem1:KeyInMem1", "ValueInMem1"},
                {"Mem1:KeyInMem1:Deep1", "ValueDeep1"},
                {"Mem2:KeyInMem2:Deep1", "ValueDeep2"}
            };

            var config = new ConfigurationManager();
            ((IConfigurationBuilder)config).AddInMemoryCollection(dict);

            // Act
            var sectionExists1 = config.GetSection("Mem1").Exists();
            var sectionExists2 = config.GetSection("Mem2").Exists();
            var sectionNotExists = config.GetSection("Mem3").Exists();

            // Assert
            Assert.True(sectionExists1);
            Assert.True(sectionExists2);
            Assert.False(sectionNotExists);
        }

        [Theory]
        [InlineData("Value1")]
        [InlineData("")]
        public void KeyWithValueAndWithoutChildrenExistsAsSection(string value)
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Mem1", value}
            };

            var config = new ConfigurationManager();
            ((IConfigurationBuilder)config).AddInMemoryCollection(dict);

            // Act
            var sectionExists = config.GetSection("Mem1").Exists();

            // Assert
            Assert.True(sectionExists);
        }

        [Fact]
        public void KeyWithNullValueAndWithoutChildrenIsASectionButNotExists()
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Mem1", null}
            };

            var config = new ConfigurationManager();
            ((IConfigurationBuilder)config).AddInMemoryCollection(dict);

            // Act
            var sections = config.GetChildren();
            var sectionExists = config.GetSection("Mem1").Exists();
            var sectionChildren = config.GetSection("Mem1").GetChildren();

            // Assert
            Assert.Single(sections, section => section.Key == "Mem1");
            Assert.False(sectionExists);
            Assert.Empty(sectionChildren);
        }

        [Fact]
        public void SectionWithChildrenHasNullValue()
        {
            // Arrange
            var dict = new Dictionary<string, string>()
            {
                {"Mem1:KeyInMem1", "ValueInMem1"},
            };


            var config = new ConfigurationManager();
            ((IConfigurationBuilder)config).AddInMemoryCollection(dict);

            // Act
            var sectionValue = config.GetSection("Mem1").Value;

            // Assert
            Assert.Null(sectionValue);
        }

        [Fact]
        public void ProviderWithNullReloadToken()
        {
            // Arrange
            var config = new ConfigurationManager();
            IConfigurationBuilder builder = config;

            // Assert
            Assert.NotNull(builder.Build());
        }

        [Fact]
        public void BuildReturnsThis()
        {
            // Arrange
            var config = new ConfigurationManager();

            // Assert
            Assert.Same(config, ((IConfigurationBuilder)config).Build());
        }

        [Fact]
        public void ClearConfigurationSources()
        {
            // Arrange
            var dic1 = new Dictionary<string, string>()
            {
                {"Mem1:KeyInMem1", "ValueInMem1"},
            };
            var dic2 = new Dictionary<string, string>()
            {
                {"Mem2:KeyInMem2", "ValueInMem2"},
            };

            var memConfigSrc1 = new MemoryConfigurationSource { InitialData = dic1 };
            var memConfigSrc2 = new MemoryConfigurationSource { InitialData = dic2 };

            var config = new ConfigurationManager();
            IConfigurationBuilder configurationBuilder = config;

            configurationBuilder.Add(memConfigSrc1);
            configurationBuilder.Add(memConfigSrc2);

            // Act
            config.Sources.Clear();

            // Assert
            Assert.DoesNotContain(memConfigSrc1, config.Sources);
            Assert.DoesNotContain(memConfigSrc2, config.Sources);
            Assert.Null(config["Mem1:KeyInMem1"]);
            Assert.Null(config["Mem2:KeyInMem2"]);
        }

        private static string Get(IConfigurationProvider provider, string key)
        {
            string value;

            if (!provider.TryGet(key, out value))
            {
                throw new InvalidOperationException("Key not found");
            }

            return value;
        }

        private class TestConfigurationSource : IConfigurationSource
        {
            private readonly IConfigurationProvider _provider;

            public TestConfigurationSource(IConfigurationProvider provider)
            {
                _provider = provider;
            }

            public IConfigurationProvider Build(IConfigurationBuilder builder)
            {
                return _provider;
            }
        }

        private class TestConfigurationProvider : ConfigurationProvider
        {
            public TestConfigurationProvider(string key, string value)
                => Data.Add(key, value);
        }

        private class BlockLoadOnMREProvider : ConfigurationProvider
        {
            private readonly ManualResetEventSlim _mre;
            private readonly TimeSpan _timeout;

            private readonly TaskCompletionSource<object> _loadStartedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public BlockLoadOnMREProvider(ManualResetEventSlim mre, TimeSpan timeout)
            {
                _mre = mre;
                _timeout = timeout;
            }

            public Task LoadStartedTask => _loadStartedTcs.Task;

            public override void Load()
            {
                _loadStartedTcs.SetResult(null);
                Assert.True(_mre.Wait(_timeout), "BlockLoadOnMREProvider.Load() timed out.");
            }
        }

        private class BlockReadOnMREProvider : ConfigurationProvider, IDisposable
        {
            private readonly ManualResetEventSlim _mre;
            private readonly TimeSpan _timeout;

            private readonly TaskCompletionSource<object> _readStartedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public BlockReadOnMREProvider(ManualResetEventSlim mre, TimeSpan timeout)
            {
                _mre = mre;
                _timeout = timeout;
            }

            public Task ReadStartedTask => _readStartedTcs.Task;

            public bool IsDisposed { get; set; }

            public override bool TryGet(string key, out string? value)
            {
                _readStartedTcs.SetResult(null);
                Assert.True(_mre.Wait(_timeout), "BlockReadOnMREProvider.TryGet() timed out.");
                return base.TryGet(key, out value);
            }

            public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
            {
                _readStartedTcs.SetResult(null);
                Assert.True(_mre.Wait(_timeout), "BlockReadOnMREProvider.GetChildKeys() timed out.");
                return base.GetChildKeys(earlierKeys, parentPath);
            }

            public void Dispose() => IsDisposed = true;
        }

        private class DisposableTestConfigurationProvider : ConfigurationProvider, IDisposable
        {
            public bool IsDisposed { get; set; }

            public DisposableTestConfigurationProvider(string key, string value)
                => Data.Add(key, value);

            public void Dispose()
                => IsDisposed = true;
        }

        private class TestChangeToken : IChangeToken
        {
            public List<(Action<object>, object)> Callbacks { get; } = new List<(Action<object>, object)>();

            public bool HasChanged => false;

            public bool ActiveChangeCallbacks => true;

            public IDisposable RegisterChangeCallback(Action<object> callback, object state)
            {
                var item = (callback, state);
                Callbacks.Add(item);
                return new DisposableAction(() => Callbacks.Remove(item));
            }

            private class DisposableAction : IDisposable
            {
                private Action _action;

                public DisposableAction(Action action)
                {
                    _action = action;
                }

                public void Dispose()
                {
                    var a = _action;
                    if (a != null)
                    {
                        _action = null;
                        a();
                    }
                }
            }
        }

        private class TestMemorySourceProvider : MemoryConfigurationProvider, IConfigurationSource
        {
            public TestMemorySourceProvider(Dictionary<string, string> initialData)
                : base(new MemoryConfigurationSource { InitialData = initialData })
            { }

            public IConfigurationProvider Build(IConfigurationBuilder builder)
            {
                return this;
            }
        }

        private class NullReloadTokenConfigSource : IConfigurationSource, IConfigurationProvider
        {
            public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string parentPath) => throw new NotImplementedException();
            public IChangeToken GetReloadToken() => null;
            public void Load() { }
            public void Set(string key, string value) => throw new NotImplementedException();
            public bool TryGet(string key, out string value) => throw new NotImplementedException();
            public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
        }

    }
}
